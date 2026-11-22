using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The vehicle flag set, and the four flags that were worse than unimplemented.
    /// <para>
    /// Of nineteen flags, six worked end to end — sampled from the owner's car and
    /// applied to every copy. One was sampled and never applied. Eight were declared
    /// and did neither.
    /// </para>
    /// <para>
    /// The remaining four — both indicators, the muted siren and the handbrake — were
    /// <em>applied but never sampled</em>, which is not a gap but a defect: a flag
    /// that is written and never read is always false, so every replicated car had
    /// its indicators forced off and its handbrake released on every frame, whatever
    /// its driver was doing.
    /// </para>
    /// </summary>
    public class VehicleFlagTests
    {
        [Fact]
        public void EveryFlagHasADistinctBit()
        {
            // A duplicated bit would make two unrelated states the same state, and the
            // enum is now large enough that a hand-written shift can collide unnoticed.
            var seen = new System.Collections.Generic.Dictionary<uint, string>();
            foreach (VehicleFlags flag in System.Enum.GetValues(typeof(VehicleFlags)))
            {
                if (flag == VehicleFlags.None)
                {
                    continue;
                }

                uint bit = (uint)flag;
                Assert.False(
                    seen.ContainsKey(bit),
                    $"{flag} shares bit {bit} with {(seen.TryGetValue(bit, out string? other) ? other : "?")}");
                seen[bit] = flag.ToString();
            }

            Assert.Equal(19, seen.Count);
        }

        [Fact]
        public void TheFlagSetSurvivesTheWire()
        {
            VehicleFlags all =
                VehicleFlags.EngineRunning | VehicleFlags.Lights | VehicleFlags.HighBeams
                | VehicleFlags.SirenActive | VehicleFlags.SirenMuted | VehicleFlags.HornActive
                | VehicleFlags.Locked | VehicleFlags.RoofOpen | VehicleFlags.LeftIndicator
                | VehicleFlags.RightIndicator | VehicleFlags.InteriorLight | VehicleFlags.TaxiLight
                | VehicleFlags.Undriveable | VehicleFlags.SearchLight;

            var baseline = new VehicleEntity(new EntityId(2));
            var current = new VehicleEntity(new EntityId(2)) { Flags = all };

            var serializer = new VehicleEntitySerializer();
            var writer = new NetWriter(128);
            serializer.WriteDelta(writer, baseline, current);

            var applied = new VehicleEntity(new EntityId(2));
            serializer.ReadDelta(new NetReader(writer.ToArray()), applied);

            Assert.Equal(all, applied.Flags);
        }

        [Fact]
        public void IndicatorsAreIndependent()
        {
            // Left and right are separate bits, because a car signalling left is not a
            // car with its hazards on — and hazards are the two of them together.
            var left = new VehicleEntity(new EntityId(2)) { Flags = VehicleFlags.LeftIndicator };

            Assert.True(left.HasFlag(VehicleFlags.LeftIndicator));
            Assert.False(left.HasFlag(VehicleFlags.RightIndicator));
            Assert.False(left.HasFlag(VehicleFlags.HazardLights));
        }

        [Fact]
        public void SettingAFlagOffLeavesTheOthersAlone()
        {
            var vehicle = new VehicleEntity(new EntityId(2))
            {
                Flags = VehicleFlags.Lights | VehicleFlags.SirenActive | VehicleFlags.TaxiLight,
            };

            vehicle.SetFlag(VehicleFlags.SirenActive, false);

            Assert.True(vehicle.HasFlag(VehicleFlags.Lights));
            Assert.False(vehicle.HasFlag(VehicleFlags.SirenActive));
            Assert.True(vehicle.HasFlag(VehicleFlags.TaxiLight));
        }

        [Fact]
        public void AMutedSirenIsStillASiren()
        {
            // Muted is the absence of audio while the siren runs, which is how a police
            // car runs its lights quietly. It is not "siren off".
            var quiet = new VehicleEntity(new EntityId(2))
            {
                Flags = VehicleFlags.SirenActive | VehicleFlags.SirenMuted,
            };

            Assert.True(quiet.HasFlag(VehicleFlags.SirenActive));
            Assert.True(quiet.HasFlag(VehicleFlags.SirenMuted));
        }

        [Fact]
        public void UndriveableIsWhatIsDriveableReads()
        {
            var wreck = new VehicleEntity(new EntityId(2))
            {
                EngineHealth = 1000f, Flags = VehicleFlags.Undriveable,
            };
            var running = new VehicleEntity(new EntityId(2)) { EngineHealth = 1000f };
            var burntOut = new VehicleEntity(new EntityId(2)) { EngineHealth = 0f };

            Assert.False(wreck.IsDriveable);
            Assert.True(running.IsDriveable);
            Assert.False(burntOut.IsDriveable);
        }
    }
}
