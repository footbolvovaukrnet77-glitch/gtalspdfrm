using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Players
{
    /// <summary>Blip colour indices, as GTA V numbers them.</summary>
    public static class BlipColour
    {
        public const int White = 0;
        public const int Red = 1;
        public const int Green = 2;
        public const int Blue = 3;
        public const int Yellow = 5;
        public const int Grey = 40;
    }

    /// <summary>What to draw for one remote player this frame.</summary>
    public readonly struct PlayerMarker
    {
        public PlayerMarker(bool showBlip, int blipColour, bool showName, string name, float distance)
        {
            ShowBlip = showBlip;
            BlipColour = blipColour;
            ShowName = showName;
            Name = name;
            Distance = distance;
        }

        public bool ShowBlip { get; }

        public int BlipColour { get; }

        /// <summary>Whether the floating name is drawn. Independent of the blip: the map is not the world.</summary>
        public bool ShowName { get; }

        public string Name { get; }

        /// <summary>Metres from the local player. The bridge fades the name with it.</summary>
        public float Distance { get; }
    }

    /// <summary>
    /// Decides what to draw over and about another player.
    /// <para>
    /// <b>Why this is not a nicety.</b> Until this existed a session gave you no way
    /// to find anyone or to tell who you were looking at: no blip on the map, no name
    /// over the ped. Everything needed to draw both had been replicated since Phase 2
    /// — name, position, health, wanted level — and nothing read any of it for the
    /// screen. Two people who wanted to meet had to say coordinates out loud.
    /// </para>
    /// <para>
    /// The decision lives here rather than in the bridge for the usual reason: the
    /// rules have real content — when a name is legible, what a colour means, what a
    /// dead player looks like — and here they can be tested without the game.
    /// </para>
    /// </summary>
    public static class PlayerMarkers
    {
        /// <summary>
        /// Beyond this a floating name is a few unreadable pixels over a ped too small
        /// to see, and drawing one per player per frame costs more than it says.
        /// </summary>
        public const float NameDistance = 120f;

        /// <summary>Inside this the name is drawn at full strength; beyond it, it fades out to <see cref="NameDistance"/>.</summary>
        public const float NameFullDistance = 40f;

        public static PlayerMarker Decide(PlayerEntity player, NetVector3 viewer, bool blipsEnabled, bool namesEnabled)
        {
            float distance = NetVector3.Distance(viewer, player.Position);
            bool dead = !player.IsAlive;

            return new PlayerMarker(
                showBlip: blipsEnabled,
                blipColour: Colour(player, dead),
                // A dead player's name still shows: knowing who is down and where is
                // the whole point of looking.
                showName: namesEnabled && distance <= NameDistance,
                name: player.Name,
                distance: distance);
        }

        /// <summary>
        /// Name opacity, 0 to 1. Full inside <see cref="NameFullDistance"/> and fading
        /// to nothing at <see cref="NameDistance"/>, because a name that pops out of
        /// existence at a threshold reads as a glitch.
        /// </summary>
        public static float NameOpacity(float distance)
        {
            if (distance <= NameFullDistance)
            {
                return 1f;
            }

            if (distance >= NameDistance)
            {
                return 0f;
            }

            return 1f - ((distance - NameFullDistance) / (NameDistance - NameFullDistance));
        }

        private static int Colour(PlayerEntity player, bool dead)
        {
            if (dead)
            {
                return BlipColour.Grey;
            }

            // Wanted first: a player the police are chasing is the one thing another
            // player most needs to see at a glance, and it outranks how hurt they are.
            if (player.WantedLevel > 0)
            {
                return BlipColour.Red;
            }

            if (player.HasFlag(PlayerFlags.InVehicle))
            {
                return BlipColour.Blue;
            }

            return player.Health <= player.MaxHealth / 4 ? BlipColour.Yellow : BlipColour.Green;
        }
    }
}
