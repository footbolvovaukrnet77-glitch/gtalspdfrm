using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Shared.Security
{
    public sealed class ViolationRecord
    {
        public ViolationRecord(ViolationKind kind, string detail, ViolationAction action)
        {
            Kind = kind;
            Detail = detail;
            Action = action;
        }

        public ViolationKind Kind { get; }

        public string Detail { get; }

        public ViolationAction Action { get; }

        public override string ToString() => $"{Kind}: {Detail} -> {Action}";
    }

    public sealed class ValidationOutcome
    {
        /// <summary>
        /// False when the update must not be written into the world. The server then
        /// keeps its own state, which the next snapshot replays to the client as a
        /// correction — this is what "server authority" actually means in practice.
        /// </summary>
        public bool Accepted { get; set; } = true;

        public List<ViolationRecord> Violations { get; } = new List<ViolationRecord>();

        public ViolationAction StrongestAction
        {
            get
            {
                ViolationAction strongest = ViolationAction.Ignore;
                foreach (ViolationRecord violation in Violations)
                {
                    if (violation.Action > strongest)
                    {
                        strongest = violation.Action;
                    }
                }

                return strongest;
            }
        }
    }

    /// <summary>Tunables for <see cref="AntiCheatEngine"/>. All distances in metres, all times in seconds.</summary>
    public sealed class AntiCheatSettings
    {
        public AntiCheatLevel Level { get; set; } = AntiCheatLevel.Standard;

        /// <summary>Fastest a sprinting ped moves, with headroom for slopes and stairs.</summary>
        public float MaxOnFootSpeed { get; set; } = 14f;

        /// <summary>Fast enough for the quickest vanilla vehicles and most add-ons.</summary>
        public float MaxVehicleSpeed { get; set; } = 160f;

        /// <summary>A jump this far in one update is a teleport regardless of the speed budget.</summary>
        public float TeleportDistance { get; set; } = 75f;

        /// <summary>Multiplier applied to speed limits to absorb latency and interpolation.</summary>
        public float SpeedTolerance { get; set; } = 1.6f;

        /// <summary>
        /// How many seconds of movement allowance may be banked.
        /// <para>
        /// Movement is checked against a replenishing budget rather than per update.
        /// A per-update check compares distance moved against the time since the last
        /// update, which produces false positives whenever the network bunches two
        /// updates together — a normal event on a jittery link, and one that would
        /// otherwise wedge an honest player in place. A budget bounds sustained speed
        /// just as tightly while tolerating bursty arrival.
        /// </para>
        /// </summary>
        public float MovementBurstSeconds { get; set; } = 1.5f;

        /// <summary>Health a player may legitimately regain per second.</summary>
        public float MaxHealthRegenPerSecond { get; set; } = 25f;

        public int MaxArmor { get; set; } = 100;

        /// <summary>Client state updates accepted per second before the peer is throttled.</summary>
        public int MaxUpdatesPerSecond { get; set; } = 90;

        /// <summary>Violations tolerated before the configured escalation applies.</summary>
        public int ViolationsBeforeEscalation { get; set; } = 20;

        public Dictionary<ViolationKind, ViolationAction> Actions { get; } = new Dictionary<ViolationKind, ViolationAction>();

        public ViolationAction ActionFor(ViolationKind kind)
        {
            if (Actions.TryGetValue(kind, out ViolationAction action))
            {
                return action;
            }

            return Level switch
            {
                AntiCheatLevel.Off => ViolationAction.Ignore,
                AntiCheatLevel.Basic => ViolationAction.Log,
                AntiCheatLevel.Standard => ViolationAction.Warn,
                AntiCheatLevel.Strict => ViolationAction.Kick,
                _ => ViolationAction.Log,
            };
        }
    }

    /// <summary>Per-player counters the engine needs between updates.</summary>
    public sealed class PlayerValidationState
    {
        public double LastUpdateTime { get; set; }

        /// <summary>Metres of movement currently banked. Negative means the budget has never been primed.</summary>
        public double MovementBudget { get; set; } = -1d;

        /// <summary>
        /// Behavioural checks are skipped until this time.
        /// <para>
        /// The server itself moves players — a respawn teleports them across the map
        /// and restores their health in one step. Without a grace window the server's
        /// own action would be flagged as a teleport and a health hack, and the player
        /// would be rejected out of the position the server just put them in.
        /// </para>
        /// </summary>
        public double GraceUntil { get; set; }

        /// <summary>Suspends behavioural checks for <paramref name="seconds"/> and re-primes the movement budget.</summary>
        public void GrantGrace(double now, double seconds)
        {
            GraceUntil = now + seconds;
            MovementBudget = -1d;
            LastUpdateTime = 0d;
        }

        public int UpdatesInWindow { get; set; }

        public double WindowStart { get; set; }

        public int TotalViolations { get; set; }

        public Dictionary<ViolationKind, int> ViolationCounts { get; } = new Dictionary<ViolationKind, int>();

        public void Count(ViolationKind kind)
        {
            TotalViolations++;
            ViolationCounts.TryGetValue(kind, out int existing);
            ViolationCounts[kind] = existing + 1;
        }
    }

    /// <summary>
    /// Validates client-reported player state before it reaches the world.
    /// <para>
    /// The bounds and finiteness checks run at every level including
    /// <see cref="AntiCheatLevel.Off"/>: they are not cheat detection, they are what
    /// stops a malformed or hostile packet from writing garbage into the
    /// authoritative world. Only the behavioural checks are level-gated.
    /// </para>
    /// </summary>
    public sealed class AntiCheatEngine
    {
        public AntiCheatEngine(AntiCheatSettings? settings = null)
        {
            Settings = settings ?? new AntiCheatSettings();
        }

        public AntiCheatSettings Settings { get; }

        public ValidationOutcome ValidatePlayerState(
            PlayerEntity current,
            PlayerStateProposal proposal,
            PlayerValidationState state,
            double now)
        {
            var outcome = new ValidationOutcome();

            // --- Always-on protocol guards -------------------------------------
            if (!IsFinite(proposal.Position) || !IsFinite(proposal.Velocity) || !IsFinite(proposal.AimPosition)
                || float.IsNaN(proposal.Heading) || float.IsInfinity(proposal.Heading))
            {
                Reject(outcome, state, ViolationKind.InvalidPosition, "non-finite float in state update", ViolationAction.Log);
                return outcome;
            }

            if (Math.Abs(proposal.Position.X) > Quantize.WorldExtentXY
                || Math.Abs(proposal.Position.Y) > Quantize.WorldExtentXY
                || Math.Abs(proposal.Position.Z) > Quantize.WorldExtentZ)
            {
                Reject(outcome, state, ViolationKind.InvalidPosition, $"position {proposal.Position} is outside the world bounds", ViolationAction.Log);
                return outcome;
            }

            if (state.WindowStart <= 0 || now - state.WindowStart >= 1d)
            {
                state.WindowStart = now;
                state.UpdatesInWindow = 0;
            }

            state.UpdatesInWindow++;
            if (state.UpdatesInWindow > Settings.MaxUpdatesPerSecond)
            {
                Reject(
                    outcome,
                    state,
                    ViolationKind.PacketRate,
                    $"{state.UpdatesInWindow} state updates in one second (limit {Settings.MaxUpdatesPerSecond})",
                    Settings.Level == AntiCheatLevel.Off ? ViolationAction.Log : Settings.ActionFor(ViolationKind.PacketRate));
                return outcome;
            }

            if (Settings.Level == AntiCheatLevel.Off || now < state.GraceUntil)
            {
                state.LastUpdateTime = now;
                return outcome;
            }

            double deltaTime = state.LastUpdateTime > 0 ? now - state.LastUpdateTime : 0d;
            state.LastUpdateTime = now;

            // --- Movement ------------------------------------------------------
            double speedLimit = (proposal.InVehicle ? Settings.MaxVehicleSpeed : Settings.MaxOnFootSpeed)
                                * Settings.SpeedTolerance;
            double capacity = speedLimit * Settings.MovementBurstSeconds;

            if (state.MovementBudget < 0)
            {
                state.MovementBudget = capacity;
            }
            else
            {
                state.MovementBudget += speedLimit * deltaTime;
                if (state.MovementBudget > capacity)
                {
                    state.MovementBudget = capacity;
                }
            }

            float distance = NetVector3.Distance(current.Position, proposal.Position);

            // A jump is a teleport only if it beats BOTH the fixed floor and what honest
            // movement could have covered in the time available.
            //
            // The floor alone was wrong, and wrong in the direction that punishes honest
            // players. At the vehicle limit a player covers about 71 m per second, so any
            // frame the game spends streaming for a second and a bit -- which GTA V does
            // routinely -- produced a "teleport" and the update was rejected. The server's
            // position then stopped advancing, so the *next* report was further still and
            // was rejected too: once a player got more than 75 m ahead they could never
            // report again, and only a correction dragging them backwards broke the loop.
            // A real session shows the result as a steady 141 m disagreement while
            // driving, and 1042 m after a longer stall.
            //
            // The budget is the honest measure and it is already capped at
            // speedLimit * MovementBurstSeconds, so this does not open the door: a client
            // claiming to have crossed the map is still caught, because no amount of
            // waiting lets the budget exceed that cap.
            double teleportAllowance = Math.Max(Settings.TeleportDistance, state.MovementBudget);
            if (distance > teleportAllowance)
            {
                Reject(
                    outcome,
                    state,
                    ViolationKind.Teleport,
                    $"jumped {distance:0.#} m in one update (limit {teleportAllowance:0} m)",
                    Settings.ActionFor(ViolationKind.Teleport));
            }
            else if (distance > state.MovementBudget)
            {
                Reject(
                    outcome,
                    state,
                    ViolationKind.SpeedHack,
                    $"moved {distance:0.##} m with {state.MovementBudget:0.##} m of movement budget " +
                    $"(sustained limit {speedLimit:0.#} m/s)",
                    Settings.ActionFor(ViolationKind.SpeedHack));
            }
            else
            {
                state.MovementBudget -= distance;
            }

            // --- Health and armour ---------------------------------------------
            if (proposal.Health > current.MaxHealth)
            {
                Reject(
                    outcome,
                    state,
                    ViolationKind.HealthHack,
                    $"health {proposal.Health} exceeds maximum {current.MaxHealth}",
                    Settings.ActionFor(ViolationKind.HealthHack));
            }
            else if (Settings.Level >= AntiCheatLevel.Standard && deltaTime > 0.0001d)
            {
                int gained = proposal.Health - current.Health;
                double allowedGain = (Settings.MaxHealthRegenPerSecond * deltaTime) + 1d;
                if (gained > allowedGain)
                {
                    Reject(
                        outcome,
                        state,
                        ViolationKind.HealthHack,
                        $"gained {gained} health in {deltaTime * 1000:0} ms (budget {allowedGain:0.#})",
                        Settings.ActionFor(ViolationKind.HealthHack));
                }
            }

            if (proposal.Armor > Settings.MaxArmor)
            {
                Reject(
                    outcome,
                    state,
                    ViolationKind.ArmorHack,
                    $"armour {proposal.Armor} exceeds maximum {Settings.MaxArmor}",
                    Settings.ActionFor(ViolationKind.ArmorHack));
            }

            if (Settings.Level >= AntiCheatLevel.Strict && proposal.Invincible)
            {
                Reject(
                    outcome,
                    state,
                    ViolationKind.GodMode,
                    "client reported an invincibility flag",
                    Settings.ActionFor(ViolationKind.GodMode));
            }

            return outcome;
        }

        /// <summary>True once a player has produced more violations than the configured budget.</summary>
        public bool ShouldEscalate(PlayerValidationState state) =>
            Settings.Level != AntiCheatLevel.Off && state.TotalViolations >= Settings.ViolationsBeforeEscalation;

        private static void Reject(
            ValidationOutcome outcome,
            PlayerValidationState state,
            ViolationKind kind,
            string detail,
            ViolationAction action)
        {
            outcome.Accepted = false;
            outcome.Violations.Add(new ViolationRecord(kind, detail, action));
            state.Count(kind);
        }

        private static bool IsFinite(NetVector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X)
            && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y)
            && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
    }

    /// <summary>The subset of a client update the validator looks at.</summary>
    public sealed class PlayerStateProposal
    {
        public NetVector3 Position { get; set; }

        public NetVector3 Velocity { get; set; }

        public NetVector3 AimPosition { get; set; }

        public float Heading { get; set; }

        public int Health { get; set; }

        public int Armor { get; set; }

        public bool InVehicle { get; set; }

        public bool Invincible { get; set; }
    }
}
