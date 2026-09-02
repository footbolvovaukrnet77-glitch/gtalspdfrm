using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Bot
{
    /// <summary>
    /// The body the bot pretends to have.
    /// <para>
    /// GTA V is where a real client's position, health and vehicle come from. There
    /// is no GTA V here, so this class <em>is</em> the game: tasks move it, and
    /// <see cref="SimulatedGameBridge"/> reads it exactly as the ScriptHookVDotNet
    /// bridge reads the real one. Nothing above the bridge can tell the difference,
    /// which is the entire point — the client, the protocol and the server are the
    /// real ones, and only the fifty centimetres between the bridge and the engine
    /// are simulated.
    /// </para>
    /// <para>
    /// What this therefore cannot test is anything the engine decides: whether a ped
    /// actually plays a crouch animation, whether a car falls through the map,
    /// whether the camera ends up where a person expects. Those need eyes on a
    /// screen. What it can test is everything the <em>server</em> decides, which is
    /// the half that has never been exercised with two players.
    /// </para>
    /// </summary>
    public sealed class BotBody
    {
        /// <summary>Michael's model, so the bot is a shape every install already has.</summary>
        public const uint DefaultModel = 0x0D7114C9u;

        /// <summary>Carbine rifle. Any hash a real install resolves would do.</summary>
        public const uint DefaultWeapon = 0x83BF0278u;

        /// <summary>
        /// Where the server puts a player it has never seen (GameServer.DefaultSpawn).
        /// <para>
        /// Starting anywhere else means two fresh bots are placed by the server while
        /// still reporting their own position, and the first run of this ended with
        /// them 2,296 m apart and every damage claim refused as out of range — a
        /// result that looked like a defect in the hit path and was a defect in the
        /// bot's spawn point.
        /// </para>
        /// </summary>
        public NetVector3 Position { get; set; } = new NetVector3(215.0f, -810.0f, 30.7f);

        public NetVector3 Velocity { get; set; }

        public float Heading { get; set; }

        public int Health { get; set; } = 200;

        public int MaxHealth { get; set; } = 200;

        public int Armor { get; set; }

        public uint ModelHash { get; set; } = DefaultModel;

        public PlayerFlags Flags { get; set; }

        public MovementState Movement { get; set; }

        public uint WeaponHash { get; set; } = DefaultWeapon;

        public int Ammo { get; set; } = 250;

        public NetVector3 AimPosition { get; set; }

        public byte WantedLevel { get; set; }

        /// <summary>Handle of the vehicle the bot is driving, or 0 on foot.</summary>
        public int VehicleHandle { get; set; }

        public uint VehicleModel { get; set; }

        /// <summary>Shots fired since the bridge last sampled. Read and cleared there.</summary>
        public int PendingRounds { get; set; }

        public NetVector3 ShotOrigin { get; set; }

        public NetVector3 ShotImpact { get; set; }

        /// <summary>Hits the bot claims to have landed, drained by the bridge.</summary>
        public List<LocalHitSample> PendingHits { get; } = new List<LocalHitSample>();

        public bool IsDead => Health <= 0;

        /// <summary>
        /// Moves towards a point at a speed, and reports arrival.
        /// <para>
        /// Velocity is set from the step actually taken rather than from the
        /// intended direction, so a body that has arrived reports zero and the
        /// server's movement budget sees the same number a real client would send.
        /// </para>
        /// </summary>
        public bool MoveTowards(NetVector3 target, float metresPerSecond, double deltaSeconds)
        {
            float dx = target.X - Position.X;
            float dy = target.Y - Position.Y;
            float dz = target.Z - Position.Z;
            float distance = (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            if (distance < 0.5f)
            {
                Velocity = default;
                return true;
            }

            var step = (float)Math.Min(distance, metresPerSecond * deltaSeconds);
            float nx = dx / distance;
            float ny = dy / distance;
            float nz = dz / distance;

            NetVector3 before = Position;
            Position = new NetVector3(
                Position.X + (nx * step),
                Position.Y + (ny * step),
                Position.Z + (nz * step));

            Velocity = deltaSeconds > 0d
                ? new NetVector3(
                    (float)((Position.X - before.X) / deltaSeconds),
                    (float)((Position.Y - before.Y) / deltaSeconds),
                    (float)((Position.Z - before.Z) / deltaSeconds))
                : default;

            Heading = (float)((Math.Atan2(ny, nx) * 180d / Math.PI) - 90d);
            if (Heading < 0f)
            {
                Heading += 360f;
            }

            return false;
        }

        public void Stop()
        {
            Velocity = default;
            Movement = MovementState.Idle;
        }

        /// <summary>Fires one round towards a point, for the bridge to report next sample.</summary>
        public void Fire(NetVector3 at)
        {
            PendingRounds++;
            ShotOrigin = new NetVector3(Position.X, Position.Y, Position.Z + 0.6f);
            ShotImpact = at;
            AimPosition = at;
            Ammo = Math.Max(0, Ammo - 1);
        }
    }
}
