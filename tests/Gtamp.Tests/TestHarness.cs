using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Client.Core;
using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Client.Ui;
using Gtamp.Server.Core;
using Gtamp.Server.Persistence;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;

namespace Gtamp.Tests
{
    /// <summary>
    /// An <see cref="IGameBridge"/> with no game behind it. Local player state is
    /// whatever the test sets, and remote peds are recorded rather than spawned.
    /// </summary>
    public sealed class FakeGameBridge : IGameBridge
    {
        private readonly Dictionary<int, NetVector3> _pedPositions = new Dictionary<int, NetVector3>();
        private int _nextHandle = 1;

        public string GameVersion => "test-build";

        public bool IsPlayerReady { get; set; } = true;

        public LocalPlayerSample Sample = new LocalPlayerSample
        {
            Position = new NetVector3(215f, -810f, 30.7f),
            Health = 200,
            MaxHealth = 200,
            ModelHash = 0x9B22DBAF,
        };

        public Dictionary<int, RemotePedCommand> Peds { get; } = new Dictionary<int, RemotePedCommand>();

        /// <summary>How many times each ped has had its clothing written.</summary>
        public Dictionary<int, int> AppearanceApplications { get; } = new Dictionary<int, int>();

        public Dictionary<int, PedAppearance> PedAppearances { get; } = new Dictionary<int, PedAppearance>();

        public List<string> Notifications { get; } = new List<string>();

        public int CorrectionsApplied { get; private set; }

        public uint WeatherHash { get; private set; }

        public int ClockHours { get; private set; } = -1;

        /// <summary>
        /// Model hashes this fake client does not have, so a test can stage the
        /// "your friend is driving a car you did not install" case.
        /// </summary>
        public HashSet<uint> UnavailableModels { get; } = new HashSet<uint>();

        /// <summary>Hashes reported as still streaming in, to separate that from a missing asset.</summary>
        public HashSet<uint> LoadingModels { get; } = new HashSet<uint>();

        public ModelAvailability GetModelAvailability(uint modelHash)
        {
            if (UnavailableModels.Contains(modelHash))
            {
                return ModelAvailability.Unavailable;
            }

            return LoadingModels.Contains(modelHash) ? ModelAvailability.Loading : ModelAvailability.Available;
        }

        public LocalPlayerSample SampleLocalPlayer() => Sample;

        public void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor)
        {
            CorrectionsApplied++;
            Sample.Position = position;
            Sample.Heading = heading;
            Sample.Health = health;
            Sample.Armor = armor;
        }

        public int CreateRemotePed(uint modelHash, NetVector3 position, float heading)
        {
            int handle = _nextHandle++;
            Peds[handle] = new RemotePedCommand(
                RemotePedAction.Idle, position, heading, 0f, true, false, position, 200, 0, 0);
            return handle;
        }

        public void ApplyRemotePedCommand(int handle, in RemotePedCommand command)
        {
            Peds[handle] = command;

            // A fake ped follows its instruction exactly: placed, or walked at the
            // blend ratio. That is enough for the tests to see whether the controller
            // is issuing sensible instructions.
            _pedPositions[handle] = command.TargetPosition;
        }

        public void ApplyRemotePedAppearance(int handle, PedAppearance appearance)
        {
            AppearanceApplications.TryGetValue(handle, out int count);
            AppearanceApplications[handle] = count + 1;
            PedAppearances[handle] = appearance.Clone();
        }

        public bool TryGetRemotePedPosition(int handle, out NetVector3 position) =>
            _pedPositions.TryGetValue(handle, out position);

        public void DestroyRemotePed(int handle)
        {
            Peds.Remove(handle);
            _pedPositions.Remove(handle);
            AppearanceApplications.Remove(handle);
            PedAppearances.Remove(handle);
        }

        public bool IsRemotePedValid(int handle) => Peds.ContainsKey(handle);

        public void SetWeather(uint weatherHash, uint nextWeatherHash, float transition) => WeatherHash = weatherHash;

        public void SetClock(int hours, int minutes, int seconds) => ClockHours = hours;

        public void ShowNotification(string text) => Notifications.Add(text);

        public void ShowSubtitle(string text, int durationMilliseconds) => Notifications.Add(text);

        // --- vehicles and objects -----------------------------------------

        /// <summary>Vehicles this fake game contains, keyed by handle.</summary>
        public Dictionary<int, VehicleEntity> Vehicles { get; } = new Dictionary<int, VehicleEntity>();

        /// <summary>The last frame applied to each replicated vehicle.</summary>
        public Dictionary<int, RemoteVehicleFrame> VehicleFrames { get; } = new Dictionary<int, RemoteVehicleFrame>();

        public Dictionary<int, ObjectEntity> Objects { get; } = new Dictionary<int, ObjectEntity>();

        public Dictionary<int, int> VehicleAppearanceApplications { get; } = new Dictionary<int, int>();

        /// <summary>Handle of the vehicle the local player is driving, or 0.</summary>
        public int LocalVehicleHandle { get; set; }

        /// <summary>Creates a vehicle the local player is driving, as if they had got into one.</summary>
        public int PutLocalPlayerInVehicle(uint modelHash, NetVector3 position, float heading = 0f)
        {
            int handle = _nextHandle++;
            Vehicles[handle] = new VehicleEntity(EntityId.None)
            {
                ModelHash = modelHash,
                Position = position,
                Heading = heading,
                EngineHealth = 1000f,
                BodyHealth = 1000f,
                PetrolTankHealth = 1000f,
                FuelLevel = 65f,
            };

            LocalVehicleHandle = handle;
            return handle;
        }

        public int CreateRemoteVehicle(uint modelHash, NetVector3 position, float heading)
        {
            int handle = _nextHandle++;
            Vehicles[handle] = new VehicleEntity(EntityId.None)
            {
                ModelHash = modelHash,
                Position = position,
                Heading = heading,
            };

            return handle;
        }

        public void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame)
        {
            VehicleFrames[handle] = frame;
            if (Vehicles.TryGetValue(handle, out VehicleEntity? vehicle))
            {
                vehicle.Position = frame.Position;
                vehicle.Heading = frame.Heading;
                vehicle.BodyHealth = frame.BodyHealth;
                vehicle.EngineHealth = frame.EngineHealth;
                vehicle.Flags = frame.Flags;
                vehicle.Doors = frame.Doors;
                vehicle.Tires = frame.Tires;
            }
        }

        public void ApplyRemoteVehicleAppearance(int handle, VehicleEntity state)
        {
            VehicleAppearanceApplications.TryGetValue(handle, out int count);
            VehicleAppearanceApplications[handle] = count + 1;

            if (Vehicles.TryGetValue(handle, out VehicleEntity? vehicle))
            {
                vehicle.Colors = state.Colors;
                vehicle.Livery = state.Livery;
                vehicle.LicensePlate = state.LicensePlate;
            }
        }

        public bool TryReadVehicle(int handle, VehicleEntity into)
        {
            if (!Vehicles.TryGetValue(handle, out VehicleEntity? vehicle))
            {
                return false;
            }

            into.ModelHash = vehicle.ModelHash;
            into.Position = vehicle.Position;
            into.Velocity = vehicle.Velocity;
            into.Heading = vehicle.Heading;
            into.EngineHealth = vehicle.EngineHealth;
            into.BodyHealth = vehicle.BodyHealth;
            into.PetrolTankHealth = vehicle.PetrolTankHealth;
            into.FuelLevel = vehicle.FuelLevel;
            into.Flags = vehicle.Flags;
            into.Doors = vehicle.Doors;
            into.Tires = vehicle.Tires;
            into.Colors = vehicle.Colors;
            into.LicensePlate = vehicle.LicensePlate;
            return true;
        }

        public void DestroyRemoteVehicle(int handle)
        {
            Vehicles.Remove(handle);
            VehicleFrames.Remove(handle);
            VehicleAppearanceApplications.Remove(handle);
        }

        public bool IsRemoteVehicleValid(int handle) => Vehicles.ContainsKey(handle);

        public int GetLocalPlayerVehicleHandle() => LocalVehicleHandle;

        public uint GetVehicleModel(int handle) =>
            Vehicles.TryGetValue(handle, out VehicleEntity? vehicle) ? vehicle.ModelHash : 0u;

        public void SeatRemotePedInVehicle(int pedHandle, int vehicleHandle, sbyte seat)
        {
        }

        public int CreateRemoteObject(uint modelHash, NetVector3 position, float heading)
        {
            int handle = _nextHandle++;
            Objects[handle] = new ObjectEntity(EntityId.None)
            {
                ModelHash = modelHash,
                Position = position,
                Heading = heading,
            };

            return handle;
        }

        public void ApplyRemoteObject(int handle, ObjectEntity state)
        {
            if (Objects.TryGetValue(handle, out ObjectEntity? prop))
            {
                prop.Position = state.Position;
                prop.Heading = state.Heading;
                prop.Flags = state.Flags;
                prop.Health = state.Health;
            }
        }

        public void DestroyRemoteObject(int handle) => Objects.Remove(handle);

        public bool IsRemoteObjectValid(int handle) => Objects.ContainsKey(handle);
    }

    /// <summary>One client plus the pieces a test wants to poke at.</summary>
    public sealed class TestClient
    {
        public TestClient(MultiplayerClient client, FakeGameBridge bridge, ClientConfig config, DeveloperConsole console)
        {
            Client = client;
            Bridge = bridge;
            Config = config;
            Console = console;
        }

        public MultiplayerClient Client { get; }

        public FakeGameBridge Bridge { get; }

        public ClientConfig Config { get; }

        public DeveloperConsole Console { get; }

        public int PlayerCount
        {
            get
            {
                int count = 0;
                foreach (NetEntity entity in Client.ReplicatedWorld.Current.Entities)
                {
                    if (entity is PlayerEntity)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public PlayerEntity? FindPlayer(string name)
        {
            foreach (NetEntity entity in Client.ReplicatedWorld.Current.Entities)
            {
                if (entity is PlayerEntity player && player.Name == name)
                {
                    return player;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A whole session — server plus clients — running on one deterministic virtual
    /// network. Time is advanced explicitly, so every test is reproducible and none
    /// of them sleep.
    /// </summary>
    public sealed class TestHarness : IDisposable
    {
        public static readonly IPEndPoint ServerEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 27015);

        private readonly List<TestClient> _clients = new List<TestClient>();
        private int _nextClientPort = 40000;

        public TestHarness(ServerConfig? config = null, IPersistenceStore? persistence = null, int seed = 1234)
        {
            Network = new LoopbackNetwork(seed);
            ServerLog = new LogBus();
            Config = config ?? new ServerConfig
            {
                ServerName = "test-server",
                PersistenceEnabled = persistence != null,
                SaveIntervalSeconds = 0,
                TickRate = 60,
                SnapshotRate = 20,
            };

            Server = new GameServer(
                Config, ServerLog, Network.CreateTransport(ServerEndPoint), persistence ?? new NullPersistenceStore());

            Server.Start(0);
        }

        public LoopbackNetwork Network { get; }

        public GameServer Server { get; }

        public ServerConfig Config { get; }

        public LogBus ServerLog { get; }

        public IReadOnlyList<TestClient> Clients => _clients;

        public double Now => Network.Now;

        /// <summary>Latency applied in each direction, in seconds.</summary>
        public double Latency
        {
            get => Network.Latency;
            set => Network.Latency = value;
        }

        public double PacketLoss
        {
            get => Network.PacketLoss;
            set => Network.PacketLoss = value;
        }

        /// <summary>
        /// Creates a client. Pass <paramref name="identitySecret"/> — a value taken
        /// from another client's <c>Config.IdentitySecret</c> — to come back as the
        /// same player; otherwise a fresh keypair is generated, exactly as a first run
        /// on a new machine would.
        /// </summary>
        public TestClient CreateClient(string name, string? identitySecret = null)
        {
            var config = new ClientConfig
            {
                PlayerName = name,
                ServerAddress = "127.0.0.1",
                ServerPort = ServerEndPoint.Port,
                IdentitySecret = identitySecret ?? string.Empty,
                InterpolationDelay = 0.1,
            };

            // The same call the real client makes on first run: derive the public
            // identity from the secret, or mint a keypair when there is none.
            config.EnsureIdentity();

            var endPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), _nextClientPort++);
            IDatagramTransport transport = Network.CreateTransport(endPoint);

            var bridge = new FakeGameBridge();
            var log = new LogBus();
            var console = new DeveloperConsole();
            log.AddSink(console);

            var client = new MultiplayerClient(config, bridge, log, transport, console);
            var testClient = new TestClient(client, bridge, config, console);
            _clients.Add(testClient);
            return testClient;
        }

        public void RemoveClient(TestClient client)
        {
            client.Client.Dispose();
            _clients.Remove(client);
        }

        /// <summary>Runs the whole session forward.</summary>
        public void Advance(double seconds, double step = 1d / 60d)
        {
            for (double elapsed = 0; elapsed < seconds; elapsed += step)
            {
                double now = Network.Now;
                Server.Tick(now);
                foreach (TestClient client in _clients)
                {
                    client.Client.Update(now);
                }

                Network.Advance(step);
            }
        }

        /// <summary>Advances until <paramref name="condition"/> holds, or fails the test by timing out.</summary>
        public bool AdvanceUntil(Func<bool> condition, double timeoutSeconds = 5d, double step = 1d / 60d)
        {
            for (double elapsed = 0; elapsed < timeoutSeconds; elapsed += step)
            {
                if (condition())
                {
                    return true;
                }

                Advance(step, step);
            }

            return condition();
        }

        /// <summary>
        /// Walks a client along +X at a plausible on-foot speed, sampling as the game
        /// would. Teleporting the sample straight to the destination would be caught
        /// by the anti-cheat, which is the correct behaviour — so tests move the way
        /// a player does.
        /// </summary>
        public void Walk(TestClient client, float metres, float metresPerSecond = 6f, double step = 1d / 30d)
        {
            float travelled = 0f;
            while (travelled < metres)
            {
                float delta = (float)(metresPerSecond * step);
                if (travelled + delta > metres)
                {
                    delta = metres - travelled;
                }

                client.Bridge.Sample.Position = new NetVector3(
                    client.Bridge.Sample.Position.X + delta,
                    client.Bridge.Sample.Position.Y,
                    client.Bridge.Sample.Position.Z);

                travelled += delta;
                Advance(step, step);
            }
        }

        public void Dispose()
        {
            foreach (TestClient client in _clients)
            {
                client.Client.Dispose();
            }

            _clients.Clear();
            Server.Dispose();
        }
    }
}
