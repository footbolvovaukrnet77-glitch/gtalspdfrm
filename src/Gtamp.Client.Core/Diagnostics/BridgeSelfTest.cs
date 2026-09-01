using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Diagnostics
{
    /// <summary>What one checked capability turned out to be.</summary>
    public enum SelfTestOutcome : byte
    {
        /// <summary>The game answered, and the answer was the expected one.</summary>
        Works = 0,

        /// <summary>The game answered, and the answer was wrong. This is a defect.</summary>
        Broken = 1,

        /// <summary>Nothing to check against yet — no other player, no vehicle, nobody has fired.</summary>
        NotExercised = 2,

        /// <summary>Cannot be checked from inside the game at all; a human has to look.</summary>
        NeedsEyes = 3,
    }

    public readonly struct SelfTestResult
    {
        public SelfTestResult(string name, SelfTestOutcome outcome, string detail)
        {
            Name = name;
            Outcome = outcome;
            Detail = detail;
        }

        public string Name { get; }

        public SelfTestOutcome Outcome { get; }

        /// <summary>What was observed, or what a human should look at. Never empty.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Asks the running game whether each replicated capability actually arrived.
    /// <para>
    /// <b>Why this exists.</b> Every game-layer feature in this project is written
    /// against an engine the test suite cannot reach. The suite proves the decisions;
    /// it cannot prove that <c>SET_PED_STEALTH_MOVEMENT</c> is what a crouch is, that
    /// a particle effect name is spelled the way Rockstar spells it, or that a ped
    /// ends up in seat -1. Those answers exist only in a running game, and until now
    /// getting them meant a person watching for each one and remembering what they
    /// saw.
    /// </para>
    /// <para>
    /// This turns one play session into a report. It deliberately distinguishes four
    /// answers, because "no" and "not tried" and "go and look" are three different
    /// things and collapsing them is how a self-test becomes a green light that means
    /// nothing.
    /// </para>
    /// </summary>
    public static class BridgeSelfTest
    {
        public static List<SelfTestResult> Run(MultiplayerClient client)
        {
            var results = new List<SelfTestResult>();

            if (!client.IsConnected)
            {
                results.Add(new SelfTestResult(
                    "connection", SelfTestOutcome.NotExercised, "not connected; connect and run this again"));
                return results;
            }

            CheckLocalSample(client, results);
            CheckRemotePlayers(client, results);
            CheckVehicles(client, results);
            CheckEventPaths(client, results);
            return results;
        }

        /// <summary>Formats the results as the console prints them.</summary>
        public static string Format(IReadOnlyList<SelfTestResult> results)
        {
            var builder = new System.Text.StringBuilder();
            int works = 0, broken = 0, notExercised = 0, needsEyes = 0;

            foreach (SelfTestResult result in results)
            {
                string mark = result.Outcome switch
                {
                    SelfTestOutcome.Works => "ok  ",
                    SelfTestOutcome.Broken => "FAIL",
                    SelfTestOutcome.NotExercised => "--  ",
                    _ => "look",
                };

                switch (result.Outcome)
                {
                    case SelfTestOutcome.Works: works++; break;
                    case SelfTestOutcome.Broken: broken++; break;
                    case SelfTestOutcome.NotExercised: notExercised++; break;
                    default: needsEyes++; break;
                }

                builder.AppendLine($"  {mark}  {result.Name,-24} {result.Detail}");
            }

            builder.AppendLine();
            builder.Append(
                $"  {works} working, {broken} broken, {notExercised} not exercised, {needsEyes} need a human to look.");

            if (notExercised > 0)
            {
                builder.Append(" Get another player near you, drive, fire a weapon, and run it again.");
            }

            return builder.ToString();
        }

        private static void CheckLocalSample(MultiplayerClient client, List<SelfTestResult> results)
        {
            LocalPlayerSample sample = client.Bridge.SampleLocalPlayer();

            results.Add(sample.ModelHash != 0
                ? new SelfTestResult("local model", SelfTestOutcome.Works, $"0x{sample.ModelHash:X8}")
                : new SelfTestResult("local model", SelfTestOutcome.Broken, "the bridge read no model for your ped"));

            results.Add(sample.MaxHealth > 0
                ? new SelfTestResult("local vitals", SelfTestOutcome.Works, $"{sample.Health}/{sample.MaxHealth} hp, {sample.Armor} armour")
                : new SelfTestResult("local vitals", SelfTestOutcome.Broken, "max health read as zero"));

            results.Add(sample.Appearance != null
                ? new SelfTestResult("local appearance", SelfTestOutcome.Works, "clothing and props read")
                : new SelfTestResult("local appearance", SelfTestOutcome.Broken, "the bridge could not read your clothing"));

            // Zero is a real aim position only if you are standing at the world origin.
            results.Add(sample.AimPosition.Equals(NetVector3.Zero)
                ? new SelfTestResult("aim position", SelfTestOutcome.Broken, "camera ray produced (0,0,0)")
                : new SelfTestResult("aim position", SelfTestOutcome.Works, sample.AimPosition.ToString()));

            results.Add(sample.WeaponComponents == null
                ? new SelfTestResult("weapon components", SelfTestOutcome.NotExercised, "no weapon in hand, or the read threw")
                : new SelfTestResult(
                    "weapon components",
                    SelfTestOutcome.Works,
                    $"{sample.WeaponComponents.Count} fitted, tint {sample.WeaponTint}"));
        }

        private static void CheckRemotePlayers(MultiplayerClient client, List<SelfTestResult> results)
        {
            if (client.RemotePlayers.Count == 0)
            {
                results.Add(new SelfTestResult(
                    "remote ped", SelfTestOutcome.NotExercised, "no other player is connected"));
                return;
            }

            int withPeds = 0;
            int seated = 0;
            int wrongModel = 0;

            foreach (RemotePlayer player in client.RemotePlayers.Players)
            {
                if (player.PedHandle == 0 || !client.Bridge.IsRemotePedValid(player.PedHandle))
                {
                    continue;
                }

                withPeds++;

                PlayerEntity? latest = player.Latest;
                if (latest != null && latest.ModelHash != 0 && latest.ModelHash != player.ModelHash)
                {
                    wrongModel++;
                }

                if (latest != null && latest.VehicleId.IsValid && latest.VehicleSeat > -2)
                {
                    seated++;
                }
            }

            results.Add(withPeds > 0
                ? new SelfTestResult("remote ped", SelfTestOutcome.Works, $"{withPeds} of {client.RemotePlayers.Count} have a ped")
                : new SelfTestResult("remote ped", SelfTestOutcome.Broken, "players are replicated but no ped exists for any of them"));

            results.Add(wrongModel == 0
                ? new SelfTestResult("remote model", SelfTestOutcome.Works, "every ped was built from its player's model")
                : new SelfTestResult("remote model", SelfTestOutcome.Broken, $"{wrongModel} ped(s) built from the wrong model"));

            results.Add(seated > 0
                ? new SelfTestResult("rider seating", SelfTestOutcome.NeedsEyes, $"{seated} player(s) report a seat — look at whether they are in the car")
                : new SelfTestResult("rider seating", SelfTestOutcome.NotExercised, "nobody is in a vehicle"));

            results.Add(client.Config.ShowPlayerBlips
                ? new SelfTestResult("player blips", SelfTestOutcome.NeedsEyes, "look at the minimap for one blip per player")
                : new SelfTestResult("player blips", SelfTestOutcome.NotExercised, "ShowPlayerBlips is off"));

            results.Add(client.Config.ShowPlayerNames
                ? new SelfTestResult("player names", SelfTestOutcome.NeedsEyes, "look for a name over each nearby player")
                : new SelfTestResult("player names", SelfTestOutcome.NotExercised, "ShowPlayerNames is off"));
        }

        private static void CheckVehicles(MultiplayerClient client, List<SelfTestResult> results)
        {
            int local = client.Bridge.GetLocalPlayerVehicleHandle();
            results.Add(local != 0
                ? new SelfTestResult("local vehicle", SelfTestOutcome.Works, $"handle {local}, model 0x{client.Bridge.GetVehicleModel(local):X8}")
                : new SelfTestResult("local vehicle", SelfTestOutcome.NotExercised, "you are not in a vehicle"));

            int replicated = client.RemoteEntities.VehicleCount;
            results.Add(replicated > 0
                ? new SelfTestResult("remote vehicles", SelfTestOutcome.Works, $"{replicated} replicated")
                : new SelfTestResult("remote vehicles", SelfTestOutcome.NotExercised, "no replicated vehicle nearby"));

            results.Add(client.RemoteEntities.NpcCount > 0
                ? new SelfTestResult("networked NPCs", SelfTestOutcome.Works, $"{client.RemoteEntities.NpcCount} drawn")
                : new SelfTestResult("networked NPCs", SelfTestOutcome.NotExercised, "the server has spawned none"));
        }

        private static void CheckEventPaths(MultiplayerClient client, List<SelfTestResult> results)
        {
            results.Add(client.ShotsFired > 0
                ? new SelfTestResult("shots reported", SelfTestOutcome.Works, $"{client.ShotsFired} counted from your clip")
                : new SelfTestResult("shots reported", SelfTestOutcome.NotExercised, "you have not fired a hitscan weapon"));

            results.Add(client.ShotsSeen > 0
                ? new SelfTestResult("shots drawn", SelfTestOutcome.NeedsEyes, $"{client.ShotsSeen} drawn — look for tracers and muzzle flashes")
                : new SelfTestResult("shots drawn", SelfTestOutcome.NotExercised, "nobody else has fired near you"));

            results.Add(client.HitsReported > 0
                ? new SelfTestResult("hits reported", SelfTestOutcome.Works, $"{client.HitsReported} claimed against other players")
                : new SelfTestResult("hits reported", SelfTestOutcome.NotExercised, "you have not hit another player"));

            results.Add(client.CorrectionsApplied == 0
                ? new SelfTestResult("server corrections", SelfTestOutcome.Works, "none needed")
                : new SelfTestResult(
                    "server corrections",
                    SelfTestOutcome.NeedsEyes,
                    $"{client.CorrectionsApplied} so far — occasional is normal, constant means rubber-banding"));

            results.Add(client.ReplicatedWorld.SnapshotsDropped == 0
                ? new SelfTestResult("snapshots", SelfTestOutcome.Works, $"{client.ReplicatedWorld.SnapshotsApplied} applied, none dropped")
                : new SelfTestResult(
                    "snapshots",
                    SelfTestOutcome.Broken,
                    $"{client.ReplicatedWorld.SnapshotsDropped} dropped — a delta could not be decoded"));
        }
    }
}
