using System.Collections.Generic;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// Decides which door, window and tyre changes are worth telling the game about.
    /// <para>
    /// <b>Why this exists.</b> Damage is applied by calling a native that changes the
    /// world, and the world then answers differently than the value that was written.
    /// Setting a tyre burst on the rim, for example, does not necessarily make
    /// <c>IS_VEHICLE_TYRE_BURST</c> agree. Code that compares the reported state against
    /// what the game says and acts on every disagreement therefore calls the native
    /// again on the next frame, and the next — an argument with the engine, sixty times
    /// a second, that shows up as a tyre or a window flickering between states.
    /// </para>
    /// <para>
    /// RAGECOOP-V hit exactly this and fixed it by deleting the repair branches, with
    /// the commit message "causes break/repair loop in some situations". That stops the
    /// flicker and costs something real: a vehicle genuinely repaired — driven through
    /// Los Santos Customs, or fixed by a mod — stays broken forever on every other
    /// screen.
    /// </para>
    /// <para>
    /// <b>What this does instead.</b> It compares the reported state against the last
    /// reported state, not against the game. A transition is applied once, when the
    /// owner's report actually changes. If the engine then disagrees, the disagreement
    /// stands until the owner reports something new — one frame of divergence is cheaper
    /// than a permanent fight, and a resync or a re-created vehicle re-asserts
    /// everything from scratch.
    /// </para>
    /// <para>
    /// It is deliberately pure: no <c>Rage</c> type, no native call, so the rule can be
    /// tested without Windows, GTA V or the game running.
    /// </para>
    /// </summary>
    public sealed class VehicleDamageTracker
    {
        /// <summary>What a caller should actually push into the game this frame.</summary>
        public readonly struct Change
        {
            public Change(bool doors, bool windows, bool tires)
            {
                Doors = doors;
                Windows = windows;
                Tires = tires;
            }

            public bool Doors { get; }

            public bool Windows { get; }

            public bool Tires { get; }

            /// <summary>True when nothing needs saying to the game at all.</summary>
            public bool IsEmpty => !Doors && !Windows && !Tires;
        }

        private readonly struct Reported
        {
            public Reported(VehicleDoorStates doors, byte windows, VehicleTireStates tires)
            {
                Doors = doors;
                Windows = windows;
                Tires = tires;
            }

            public VehicleDoorStates Doors { get; }

            public byte Windows { get; }

            public VehicleTireStates Tires { get; }
        }

        private readonly Dictionary<int, Reported> _last = new Dictionary<int, Reported>();

        /// <summary>Vehicles currently being tracked. Exposed for the diagnostics command.</summary>
        public int Count => _last.Count;

        /// <summary>
        /// What changed for this vehicle since the last report. The first report for a
        /// handle returns everything, because a freshly created vehicle is at the game's
        /// defaults and needs the whole state asserted once.
        /// </summary>
        public Change Observe(int handle, VehicleDoorStates doors, byte windows, VehicleTireStates tires)
        {
            if (!_last.TryGetValue(handle, out Reported previous))
            {
                _last[handle] = new Reported(doors, windows, tires);
                return new Change(true, true, true);
            }

            var change = new Change(
                !previous.Doors.Equals(doors),
                previous.Windows != windows,
                !previous.Tires.Equals(tires));

            if (!change.IsEmpty)
            {
                _last[handle] = new Reported(doors, windows, tires);
            }

            return change;
        }

        /// <summary>
        /// Forgets a vehicle, so its next report is treated as a first sighting and the
        /// whole state is asserted again. Called when the vehicle is destroyed and on a
        /// resync — the two moments when what the game holds can no longer be assumed.
        /// </summary>
        public void Forget(int handle) => _last.Remove(handle);

        /// <summary>Forgets every vehicle. The resync path.</summary>
        public void Clear() => _last.Clear();
    }
}
