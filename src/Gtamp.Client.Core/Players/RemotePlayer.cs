using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Players
{
    /// <summary>
    /// One other player, plus the sample buffer used to render them smoothly.
    /// <para>
    /// Snapshots arrive at 20 Hz while the game renders at 60+; playing them back
    /// raw produces visible stepping. Samples are therefore buffered and replayed at
    /// a fixed delay behind the newest one, interpolating between the two samples
    /// that straddle the render time. Extrapolation is deliberately capped: guessing
    /// far ahead of the last known state produces players who slide through walls
    /// and then snap back, which reads worse than a brief stall.
    /// </para>
    /// </summary>
    public sealed class RemotePlayer
    {
        /// <summary>Never extrapolate further than this past the newest sample.</summary>
        public const double MaxExtrapolation = 0.25;

        private readonly List<Sample> _samples = new List<Sample>();

        public RemotePlayer(EntityId entityId, uint playerId, string name)
        {
            EntityId = entityId;
            PlayerId = playerId;
            Name = name;
        }

        public EntityId EntityId { get; }

        public uint PlayerId { get; }

        public string Name { get; set; }

        /// <summary>Game-side ped handle, 0 when no ped exists yet.</summary>
        public int PedHandle { get; set; }

        public uint ModelHash { get; set; }

        /// <summary>Latest replicated appearance.</summary>
        public PedAppearance Appearance { get; } = new PedAppearance();

        /// <summary>
        /// Incremented whenever the appearance changes. The manager compares it with
        /// what it last applied, so clothing is written to the ped on change rather
        /// than every frame — applying component variations is not cheap.
        /// </summary>
        public int AppearanceVersion { get; private set; }

        public int SampleCount => _samples.Count;

        public double NewestSampleTime => _samples.Count > 0 ? _samples[_samples.Count - 1].Time : 0d;

        public void Push(double serverTime, PlayerEntity state)
        {
            // Out-of-order samples would make interpolation walk backwards.
            if (_samples.Count > 0 && serverTime <= _samples[_samples.Count - 1].Time)
            {
                return;
            }

            _samples.Add(new Sample(serverTime, state));
            Name = state.Name;
            ModelHash = state.ModelHash;

            if (!Appearance.ValueEquals(state.Appearance))
            {
                Appearance.CopyFrom(state.Appearance);
                AppearanceVersion++;
            }

            // Two samples either side of the render time are enough; keep a little
            // slack for jitter and drop the rest.
            while (_samples.Count > 16)
            {
                _samples.RemoveAt(0);
            }
        }

        /// <summary>Builds the frame to apply at <paramref name="renderTime"/>.</summary>
        public bool TrySample(double renderTime, out RemotePedFrame frame)
        {
            frame = default;
            if (_samples.Count == 0)
            {
                return false;
            }

            if (_samples.Count == 1)
            {
                frame = ToFrame(_samples[0].State, _samples[0].State, 0f);
                return true;
            }

            Sample newest = _samples[_samples.Count - 1];
            if (renderTime >= newest.Time)
            {
                double ahead = renderTime - newest.Time;
                if (ahead > MaxExtrapolation)
                {
                    // Too far ahead to guess: hold the last known state.
                    frame = ToFrame(newest.State, newest.State, 0f);
                    return true;
                }

                frame = ToFrame(newest.State, newest.State, 0f);
                frame.Position = newest.State.Position + (newest.State.Velocity * (float)ahead);
                return true;
            }

            Sample oldest = _samples[0];
            if (renderTime <= oldest.Time)
            {
                frame = ToFrame(oldest.State, oldest.State, 0f);
                return true;
            }

            for (int i = _samples.Count - 1; i > 0; i--)
            {
                Sample after = _samples[i];
                Sample before = _samples[i - 1];
                if (renderTime < before.Time || renderTime > after.Time)
                {
                    continue;
                }

                double span = after.Time - before.Time;
                float t = span <= 0.0001d ? 1f : (float)((renderTime - before.Time) / span);
                frame = ToFrame(before.State, after.State, t);
                return true;
            }

            frame = ToFrame(newest.State, newest.State, 0f);
            return true;
        }

        public void Clear() => _samples.Clear();

        private static RemotePedFrame ToFrame(PlayerEntity from, PlayerEntity to, float t) => new RemotePedFrame
        {
            Position = NetVector3.Lerp(from.Position, to.Position, t),
            Velocity = NetVector3.Lerp(from.Velocity, to.Velocity, t),
            Heading = LerpAngle(from.Heading, to.Heading, t),
            Health = to.Health,
            Armor = to.Armor,
            Flags = to.Flags,
            Movement = to.Movement,
            CurrentWeaponHash = to.CurrentWeaponHash,
            WeaponTint = to.WeaponTint,
            WeaponComponents = to.WeaponComponents,
            AimPosition = NetVector3.Lerp(from.AimPosition, to.AimPosition, t),
            AnimationHash = to.AnimationHash,
            Ragdoll = RagdollPose.Lerp(from.Ragdoll, to.Ragdoll, t),
            VehicleId = to.VehicleId,
            VehicleSeat = to.VehicleSeat,
        };

        /// <summary>Interpolates headings the short way round, so 359° -&gt; 1° does not spin the ped.</summary>
        public static float LerpAngle(float from, float to, float t)
        {
            float difference = ((to - from) % 360f + 540f) % 360f - 180f;
            float result = from + (difference * t);
            return (result % 360f + 360f) % 360f;
        }

        private readonly struct Sample
        {
            public Sample(double time, PlayerEntity state)
            {
                Time = time;
                State = (PlayerEntity)state.Clone();
            }

            public double Time { get; }

            public PlayerEntity State { get; }
        }
    }
}
