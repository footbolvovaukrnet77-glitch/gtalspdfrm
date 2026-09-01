using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Gtamp.Client.Core;
using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Diagnostics
{
    /// <summary>How confidently a field can be compared at all.</summary>
    public enum ComparisonOutcome : byte
    {
        /// <summary>Both sides agree, within the field's own tolerance.</summary>
        Match = 0,

        /// <summary>Both sides have a value and they differ.</summary>
        Differs = 1,

        /// <summary>
        /// The game does not let this be read back, so there is nothing to compare.
        /// Reported rather than shown as a match, which is what a blank would look
        /// like.
        /// </summary>
        NotReadable = 2,
    }

    public sealed class FieldComparison
    {
        public FieldComparison(string name, string server, string local, ComparisonOutcome outcome, string note = "")
        {
            Name = name;
            Server = server;
            Local = local;
            Outcome = outcome;
            Note = note;
        }

        public string Name { get; }

        public string Server { get; }

        public string Local { get; }

        public ComparisonOutcome Outcome { get; }

        /// <summary>Why a field is not readable, when it is not.</summary>
        public string Note { get; }

        public string Mark => Outcome switch
        {
            ComparisonOutcome.Match => "=",
            ComparisonOutcome.Differs => "≠",
            _ => "?",
        };
    }

    public sealed class EntityComparison
    {
        public EntityComparison(EntityId id, EntityType type, string subject)
        {
            Id = id;
            Type = type;
            Subject = subject;
        }

        public EntityId Id { get; }

        public EntityType Type { get; }

        /// <summary>What the local side of the comparison actually is: a ped, a vehicle, the local player.</summary>
        public string Subject { get; }

        public List<FieldComparison> Fields { get; } = new List<FieldComparison>();

        public int DifferenceCount
        {
            get
            {
                int count = 0;
                foreach (FieldComparison entry in Fields)
                {
                    if (entry.Outcome == ComparisonOutcome.Differs)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public int UnreadableCount
        {
            get
            {
                int count = 0;
                foreach (FieldComparison entry in Fields)
                {
                    if (entry.Outcome == ComparisonOutcome.NotReadable)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>
    /// Puts the server's view of an entity next to the game's, field by field.
    /// <para>
    /// This is the tool a replication framework most needs and least often has. Every
    /// symptom a player reports — "the car is in the wrong place", "he's driving a
    /// different vehicle", "my health keeps resetting" — is a disagreement between
    /// two states that are normally impossible to see at the same time. Showing only
    /// the replicated state, which is what <c>/entity</c> did, answers "what does the
    /// server think" and leaves the actual question unanswered.
    /// </para>
    /// <para>
    /// <b>What it cannot do, and says so.</b> GTA V exposes far less for reading than
    /// for writing. Vehicle body deformation, a ped's current animation blend and an
    /// object's rotation are all write-only, and a field that cannot be read back is
    /// marked <c>?</c> rather than shown as agreeing — a blank in a diff is worse than
    /// an admission, because it reads as a match.
    /// </para>
    /// </summary>
    public static class EntityInspector
    {
        /// <summary>Positions closer than this are the same position; below it is quantisation and interpolation.</summary>
        public const float PositionTolerance = 0.35f;

        public const float HeadingTolerance = 3f;

        public static EntityComparison Compare(MultiplayerClient client, EntityId id)
        {
            if (!client.ReplicatedWorld.TryGet(id, out NetEntity server))
            {
                var missing = new EntityComparison(id, EntityType.Unknown, "not in the replicated world");
                missing.Fields.Add(new FieldComparison(
                    "entity", "absent", "—", ComparisonOutcome.NotReadable, "the server has not sent this entity"));
                return missing;
            }

            if (id == client.LocalEntityId)
            {
                return CompareLocalPlayer(client, (PlayerEntity)server);
            }

            return server switch
            {
                PlayerEntity player => CompareRemotePlayer(client, player),
                PedEntity npc => CompareNpc(client, npc),
                VehicleEntity vehicle => CompareVehicle(client, vehicle),
                ObjectEntity worldObject => CompareObject(client, worldObject),
                _ => Undecidable(server),
            };
        }

        /// <summary>
        /// The local player is the one case where the game is fully readable, so this
        /// is the comparison that can actually be trusted end to end.
        /// </summary>
        private static EntityComparison CompareLocalPlayer(MultiplayerClient client, PlayerEntity server)
        {
            var comparison = new EntityComparison(server.Id, server.Type, "the local player");
            LocalPlayerSample local = client.Bridge.SampleLocalPlayer();

            comparison.Fields.Add(Position("position", server.Position, local.Position));
            comparison.Fields.Add(Heading("heading", server.Heading, local.Heading));
            comparison.Fields.Add(Integer("health", server.Health, local.Health));
            comparison.Fields.Add(Integer("armour", server.Armor, local.Armor));
            comparison.Fields.Add(Hash("model", server.ModelHash, local.ModelHash));
            comparison.Fields.Add(Hash("weapon", server.CurrentWeaponHash, local.CurrentWeaponHash));
            comparison.Fields.Add(Text("movement", server.Movement.ToString(), local.Movement.ToString()));
            comparison.Fields.Add(Text("flags", server.Flags.ToString(), local.Flags.ToString()));
            comparison.Fields.Add(Integer("interior", server.InteriorId, local.InteriorId));

            // Presence, not values. The two poses are sampled a round-trip apart and a
            // ragdolling body moves every frame, so comparing coordinates would report
            // a difference on every fall and mean nothing. What is worth catching is
            // the pose failing to travel at all — a ped model without the bones, or a
            // flag set with nothing behind it.
            comparison.Fields.Add(Text(
                "ragdollPose",
                server.Ragdoll.IsNone ? "none" : "reported",
                local.Ragdoll.IsNone ? "none" : "reported"));

            return comparison;
        }

        private static EntityComparison CompareRemotePlayer(MultiplayerClient client, PlayerEntity server)
        {
            var comparison = new EntityComparison(server.Id, server.Type, $"the ped standing in for {server.Name}");

            if (!client.RemotePlayers.TryGet(server.Id, out RemotePlayer remote) || remote.PedHandle == 0)
            {
                comparison.Fields.Add(new FieldComparison(
                    "ped", "present", "absent", ComparisonOutcome.Differs,
                    "no ped has been created for this player yet — check /mods for a missing model"));
                return comparison;
            }

            comparison.Fields.Add(Text("pedHandle", "—", remote.PedHandle.ToString(CultureInfo.InvariantCulture)));

            // The interpolated target, not the raw server position: the ped is
            // deliberately rendered behind the server clock, so comparing it against
            // the newest snapshot would report a difference that is the design working.
            comparison.Fields.Add(client.Bridge.TryGetRemotePedPosition(remote.PedHandle, out NetVector3 pedPosition)
                ? Position("position", server.Position, pedPosition,
                    note: $"the ped is rendered {client.Config.InterpolationDelay:0.###} s behind the server clock, " +
                          "so a small difference here is interpolation, not drift")
                : Unreadable("position", server.Position.ToString(), "the ped handle is no longer valid"));

            comparison.Fields.Add(Unreadable(
                "health", server.Health.ToString(CultureInfo.InvariantCulture),
                "a replicated ped's health is written, never read back — the server's value is the only one"));

            comparison.Fields.Add(Unreadable(
                "animation", server.Movement.ToString(),
                "GTA V exposes no way to read a ped's current locomotion blend"));

            return comparison;
        }

        private static EntityComparison CompareVehicle(MultiplayerClient client, VehicleEntity server)
        {
            var comparison = new EntityComparison(server.Id, server.Type, "the replicated vehicle");

            if (!client.RemoteEntities.TryGetVehicle(server.Id, out RemoteVehicle remote) || remote.VehicleHandle == 0)
            {
                comparison.Fields.Add(new FieldComparison(
                    "vehicle", "present", "absent", ComparisonOutcome.Differs,
                    "no vehicle has been created locally — check /mods for a missing model"));
                return comparison;
            }

            var local = new VehicleEntity(server.Id);
            if (!client.Bridge.TryReadVehicle(remote.VehicleHandle, local))
            {
                comparison.Fields.Add(new FieldComparison(
                    "vehicle", "present", "unreadable", ComparisonOutcome.Differs,
                    $"handle {remote.VehicleHandle} did not read back; the game may have culled it"));
                return comparison;
            }

            comparison.Fields.Add(Position("position", server.Position, local.Position));
            comparison.Fields.Add(Heading("heading", server.Heading, local.Heading));
            comparison.Fields.Add(Hash("model", server.ModelHash, local.ModelHash));
            comparison.Fields.Add(Number("engineHealth", server.EngineHealth, local.EngineHealth, 5f));
            comparison.Fields.Add(Number("bodyHealth", server.BodyHealth, local.BodyHealth, 5f));
            comparison.Fields.Add(Number("fuel", server.FuelLevel, local.FuelLevel, 2f));
            comparison.Fields.Add(Text("flags", server.Flags.ToString(), local.Flags.ToString()));
            comparison.Fields.Add(Text("doors", server.Doors.ToString(), local.Doors.ToString()));
            comparison.Fields.Add(Text("tyres", server.Tires.ToString(), local.Tires.ToString()));

            comparison.Fields.Add(Unreadable(
                "deformation", "—",
                "GTA V exposes deformation only through natives that write into a vehicle, never read from one"));

            return comparison;
        }

        private static EntityComparison CompareObject(MultiplayerClient client, ObjectEntity server)
        {
            var comparison = new EntityComparison(server.Id, server.Type, "the replicated object");

            bool present = client.RemoteEntities.TryGetObjectHandle(server.Id, out int handle)
                && client.Bridge.IsRemoteObjectValid(handle);

            comparison.Fields.Add(new FieldComparison(
                "object",
                "present",
                present ? $"handle {handle}" : "absent",
                present ? ComparisonOutcome.Match : ComparisonOutcome.Differs,
                present ? string.Empty : "no object has been created locally — check /mods for a missing model"));

            comparison.Fields.Add(Unreadable(
                "position", server.Position.ToString(),
                "objects are placed, not read back; the bridge exposes no object transform query"));

            return comparison;
        }

        private static EntityComparison CompareNpc(MultiplayerClient client, PedEntity server)
        {
            var comparison = new EntityComparison(server.Id, server.Type, "the networked NPC's ped");

            if (!client.RemoteEntities.TryGetNpc(server.Id, out RemoteNpc npc) || npc.PedHandle == 0)
            {
                comparison.Fields.Add(new FieldComparison(
                    "ped", "present", "absent", ComparisonOutcome.Differs,
                    "no ped has been created for this NPC yet — check /mods for a missing model"));
                return comparison;
            }

            comparison.Fields.Add(Text("pedHandle", "—", npc.PedHandle.ToString(CultureInfo.InvariantCulture)));

            if (client.Bridge.TryGetRemotePedPosition(npc.PedHandle, out NetVector3 position))
            {
                // Against the interpolated target, not the raw snapshot: an NPC is
                // rendered behind the server clock on purpose, so comparing it with the
                // newest sample reports the design as a fault.
                NetVector3 target = npc.TrySample(client.EstimatedServerTime - client.Config.InterpolationDelay,
                    out RemotePedFrame frame)
                    ? frame.Position
                    : server.Position;

                comparison.Fields.Add(Position("position", target, position, "interpolation target, not the raw snapshot"));
            }

            comparison.Fields.Add(Hash("model", server.ModelHash, npc.ModelHash));

            // Health and posture are written to the ped and never read back — the
            // bridge has no ped-state query, only a position one. Comparing the
            // server's value against itself would render as a match and prove
            // nothing, which is worse than saying it cannot be checked.
            comparison.Fields.Add(Unreadable(
                "health", server.Health.ToString(CultureInfo.InvariantCulture),
                "the bridge writes ped health and does not read it back"));
            comparison.Fields.Add(Unreadable(
                "flags", server.Flags.ToString(),
                "posture is applied to the ped and not queryable from it"));
            return comparison;
        }

        private static EntityComparison Undecidable(NetEntity server)
        {
            var comparison = new EntityComparison(server.Id, server.Type, "no local counterpart");
            comparison.Fields.Add(Unreadable(
                "local state", server.Position.ToString(),
                $"{server.Type} has no representation in the game to compare against"));
            return comparison;
        }

        // ------------------------------------------------------------------
        private static FieldComparison Position(string name, NetVector3 server, NetVector3 local, string note = "")
        {
            float distance = NetVector3.Distance(server, local);
            return new FieldComparison(
                name,
                server.ToString(),
                $"{local} ({distance:0.00} m apart)",
                distance <= PositionTolerance ? ComparisonOutcome.Match : ComparisonOutcome.Differs,
                note);
        }

        private static FieldComparison Heading(string name, float server, float local)
        {
            float difference = Math.Abs(NormaliseDegrees(server - local));
            return new FieldComparison(
                name,
                server.ToString("0.#", CultureInfo.InvariantCulture),
                $"{local.ToString("0.#", CultureInfo.InvariantCulture)} ({difference:0.#}° apart)",
                difference <= HeadingTolerance ? ComparisonOutcome.Match : ComparisonOutcome.Differs);
        }

        private static FieldComparison Integer(string name, int server, int local) => new FieldComparison(
            name,
            server.ToString(CultureInfo.InvariantCulture),
            local.ToString(CultureInfo.InvariantCulture),
            server == local ? ComparisonOutcome.Match : ComparisonOutcome.Differs);

        private static FieldComparison Number(string name, float server, float local, float tolerance) =>
            new FieldComparison(
                name,
                server.ToString("0.#", CultureInfo.InvariantCulture),
                local.ToString("0.#", CultureInfo.InvariantCulture),
                Math.Abs(server - local) <= tolerance ? ComparisonOutcome.Match : ComparisonOutcome.Differs);

        private static FieldComparison Hash(string name, uint server, uint local) => new FieldComparison(
            name,
            $"0x{server:X8}",
            $"0x{local:X8}",
            server == local ? ComparisonOutcome.Match : ComparisonOutcome.Differs);

        private static FieldComparison Text(string name, string server, string local) => new FieldComparison(
            name,
            server,
            local,
            string.Equals(server, local, StringComparison.Ordinal)
                ? ComparisonOutcome.Match
                : ComparisonOutcome.Differs);

        private static FieldComparison Unreadable(string name, string server, string why) =>
            new FieldComparison(name, server, "not readable", ComparisonOutcome.NotReadable, why);

        private static float NormaliseDegrees(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f)
            {
                degrees -= 360f;
            }
            else if (degrees < -180f)
            {
                degrees += 360f;
            }

            return degrees;
        }

        /// <summary>Renders a comparison for the console.</summary>
        public static string Format(EntityComparison comparison)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Entity {comparison.Id} ({comparison.Type}) — server vs {comparison.Subject}");
            builder.AppendLine("  field            server                          local");

            foreach (FieldComparison field in comparison.Fields)
            {
                builder.AppendLine(
                    $"  {field.Mark} {Pad(field.Name, 14)} {Pad(field.Server, 31)} {field.Local}");

                if (field.Note.Length > 0)
                {
                    builder.AppendLine($"      {field.Note}");
                }
            }

            builder.Append(
                comparison.DifferenceCount == 0
                    ? $"  everything comparable agrees ({comparison.UnreadableCount} field(s) the game will not read back)"
                    : $"  {comparison.DifferenceCount} difference(s), " +
                      $"{comparison.UnreadableCount} field(s) the game will not read back");

            return builder.ToString();
        }

        private static string Pad(string value, int width) =>
            value.Length >= width ? value : value + new string(' ', width - value.Length);
    }
}
