using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Client.Core
{
    /// <summary>
    /// Everything the multiplayer core needs from GTA V, and nothing else.
    /// <para>
    /// This interface is the single seam between engine-independent logic and the
    /// ScriptHookVDotNet layer. Keeping it narrow is what makes the client testable
    /// on a machine with no game installed, and it is also what would let a second
    /// host (a ScriptHookVDotNetCore build, or an RPH-hosted build) reuse the whole
    /// client without touching the networking code.
    /// </para>
    /// </summary>
    public interface IGameBridge
    {
        /// <summary>Game build string, e.g. "1.0.3095.0". Reported in bug reports.</summary>
        string GameVersion { get; }

        /// <summary>True once the player is in a controllable state (not loading, not in a cutscene).</summary>
        bool IsPlayerReady { get; }

        LocalPlayerSample SampleLocalPlayer();

        /// <summary>
        /// Applies a server correction to the local player. The server only ever
        /// corrects; it does not drive normal local movement.
        /// </summary>
        void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor);

        /// <summary>Creates a ped representing another player. Returns a handle, or 0 on failure.</summary>
        int CreateRemotePed(uint modelHash, NetVector3 position, float heading);

        void UpdateRemotePed(int handle, in RemotePedFrame frame);

        void DestroyRemotePed(int handle);

        bool IsRemotePedValid(int handle);

        void SetWeather(uint weatherHash, uint nextWeatherHash, float transition);

        void SetClock(int hours, int minutes, int seconds);

        void ShowNotification(string text);

        void ShowSubtitle(string text, int durationMilliseconds);
    }

    /// <summary>One frame of local player state, read from the game and sent to the server.</summary>
    public struct LocalPlayerSample
    {
        public NetVector3 Position;
        public NetVector3 Velocity;
        public float Heading;
        public int Health;
        public int MaxHealth;
        public int Armor;
        public uint ModelHash;
        public PlayerFlags Flags;
        public MovementState Movement;
        public uint CurrentWeaponHash;
        public int Ammo;
        public NetVector3 AimPosition;
        public int InteriorId;
        public uint AnimationHash;
    }

    /// <summary>Interpolated state applied to another player's ped this frame.</summary>
    public struct RemotePedFrame
    {
        public NetVector3 Position;
        public NetVector3 Velocity;
        public float Heading;
        public int Health;
        public int Armor;
        public PlayerFlags Flags;
        public MovementState Movement;
        public uint CurrentWeaponHash;
        public NetVector3 AimPosition;
        public uint AnimationHash;
    }
}
