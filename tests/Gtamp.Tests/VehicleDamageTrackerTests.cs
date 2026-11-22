using Gtamp.Client.Entities;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The rule that keeps damage out of an argument with the engine.
    /// <para>
    /// Applying a door, window or tyre state by comparing the report against what the
    /// game says, and acting on every disagreement, calls the native again on every
    /// frame the two disagree. Some states the engine genuinely answers differently
    /// than they were written — a tyre burst on the rim is the usual one — so the
    /// disagreement never resolves and the part flickers.
    /// </para>
    /// <para>
    /// RAGECOOP-V hit this and deleted its repair branches, commit message "causes
    /// break/repair loop in some situations". That stops the flicker and means a
    /// genuinely repaired vehicle stays broken on every other screen forever. Comparing
    /// against the previous <em>report</em> instead keeps the repair direction and still
    /// only speaks once per real change.
    /// </para>
    /// </summary>
    public class VehicleDamageTrackerTests
    {
        private static VehicleDoorStates Door(int index, bool broken) =>
            new VehicleDoorStates(0).WithBroken(index, broken);

        private static VehicleTireStates Tire(int index, bool burst) =>
            new VehicleTireStates(0).WithBurst(index, burst);

        [Fact]
        public void AFirstSightingAppliesEverything()
        {
            // A freshly created vehicle sits at the game's defaults, so the whole state
            // has to be asserted once even when it happens to be undamaged.
            var tracker = new VehicleDamageTracker();

            VehicleDamageTracker.Change change = tracker.Observe(1, default, 0xFF, default);

            Assert.True(change.Doors);
            Assert.True(change.Windows);
            Assert.True(change.Tires);
        }

        [Fact]
        public void AnUnchangedReportSaysNothingToTheGame()
        {
            // The property that matters. Sixty identical reports must produce one
            // application, not sixty.
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, Door(0, true), 0xFF, default);

            for (int frame = 0; frame < 60; frame++)
            {
                Assert.True(tracker.Observe(1, Door(0, true), 0xFF, default).IsEmpty);
            }
        }

        [Fact]
        public void TheGameDisagreeingIsNotAReasonToSpeakAgain()
        {
            // This is the loop, expressed as a test. The tracker is never told what the
            // game thinks, so the engine answering differently than the value written
            // cannot provoke another native call. Only the owner reporting something
            // new can.
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, default, 0xFF, Tire(2, true));

            Assert.True(tracker.Observe(1, default, 0xFF, Tire(2, true)).IsEmpty);
            Assert.True(tracker.Observe(1, default, 0xFF, Tire(2, true)).IsEmpty);
        }

        [Fact]
        public void ARepairIsAppliedBecauseItIsARealTransition()
        {
            // What deleting the repair branch costs, and what this keeps: a vehicle
            // driven through Los Santos Customs reports its tyres fixed, and that has
            // to reach the other screens.
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, Door(0, true), 0x00, Tire(2, true));

            VehicleDamageTracker.Change repaired = tracker.Observe(1, default, 0xFF, default);

            Assert.True(repaired.Doors);
            Assert.True(repaired.Windows);
            Assert.True(repaired.Tires);
        }

        [Fact]
        public void OnlyTheGroupThatChangedIsApplied()
        {
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, default, 0xFF, default);

            VehicleDamageTracker.Change change = tracker.Observe(1, default, 0xFE, default);

            Assert.False(change.Doors);
            Assert.True(change.Windows);
            Assert.False(change.Tires);
        }

        [Fact]
        public void VehiclesDoNotShareHistory()
        {
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, Door(0, true), 0xFF, default);

            Assert.True(tracker.Observe(2, Door(0, true), 0xFF, default).Doors);
        }

        [Fact]
        public void AForgottenHandleIsTreatedAsNew()
        {
            // Handles are reused. A new vehicle inheriting the previous one's history
            // would have its whole state judged unchanged and never applied at all --
            // which is a vehicle that silently renders undamaged forever.
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, Door(0, true), 0xFF, default);
            Assert.True(tracker.Observe(1, Door(0, true), 0xFF, default).IsEmpty);

            tracker.Forget(1);

            Assert.True(tracker.Observe(1, Door(0, true), 0xFF, default).Doors);
            Assert.Equal(1, tracker.Count);
        }

        [Fact]
        public void ClearForgetsEveryVehicle()
        {
            // The resync path: after throwing the replicated world away, nothing about
            // what the game currently holds can be assumed.
            var tracker = new VehicleDamageTracker();
            tracker.Observe(1, default, 0xFF, default);
            tracker.Observe(2, default, 0xFF, default);
            Assert.Equal(2, tracker.Count);

            tracker.Clear();

            Assert.Equal(0, tracker.Count);
            Assert.True(tracker.Observe(1, default, 0xFF, default).Doors);
        }
    }
}
