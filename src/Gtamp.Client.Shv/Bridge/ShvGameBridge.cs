using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.NaturalMotion;
using GtaWorld = GTA.World;

namespace Gtamp.Client.Shv.Bridge
{
    /// <summary>
    /// <see cref="IGameBridge"/> over ScriptHookVDotNet 3.
    /// <para>
    /// Remote peds are driven through the game's task system rather than by writing
    /// coordinates, so they animate as they move. Coordinates are still written, but
    /// only as a correction when the ped has drifted past
    /// <see cref="RemotePedController.HardCorrectDistance"/> — tasking alone cannot
    /// guarantee position, and correcting alone cannot produce animation, so both are
    /// needed. The decision between them lives in <see cref="RemotePedController"/>;
    /// this class only executes it.
    /// </para>
    /// </summary>
    public sealed class ShvGameBridge : IGameBridge
    {
        /// <summary>ig_michael, used until a player's own model is known.</summary>
        private const uint DefaultPedModel = 0xD7114C9;

        /// <summary>Re-task a walking ped only when its destination has moved this far.</summary>
        private const float RetaskDistance = 0.75f;

        /// <summary>Task timeout. Long enough to survive several missed snapshots, short enough to expire if we stop.</summary>
        private const int TaskTimeoutMilliseconds = 4000;

        /// <summary>How often the local player's clothing is read back. It changes rarely and each read is ~30 native calls.</summary>
        private const int AppearanceSampleIntervalMilliseconds = 1000;

        private readonly LogBus _log;
        private readonly ShvVehicleBridge _vehicles;
        private readonly Dictionary<int, Ped> _remotePeds = new Dictionary<int, Ped>();

        /// <summary>Counts the local player's rounds. See <see cref="ShotDetector"/> for why the clip is the signal.</summary>
        private readonly ShotDetector _shots = new ShotDetector();

        /// <summary>Rockstar's shared particle library. Holds the muzzle flashes.</summary>
        private ParticleEffectAsset _muzzleAsset = new ParticleEffectAsset("core");

        /// <summary>The weapon model the last remote shot was drawn with, kept so it is requested once.</summary>
        private WeaponAsset? _shotAsset;
        private readonly Dictionary<int, PedDriveState> _driveState = new Dictionary<int, PedDriveState>();
        private readonly PedAppearance _localAppearance = new PedAppearance();

        private int _lastAppearanceSampleTick;

        public ShvGameBridge(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _vehicles = new ShvVehicleBridge(_log);
        }

        public string GameVersion => Game.Version.ToString();

        /// <summary>
        /// Asks the streamer whether a hash names a model this installation has.
        /// <para>
        /// <c>Model.IsValid</c> answers whether the hash is in the game's model index
        /// at all — which is exactly the question "is the mod that adds this car
        /// installed?". A valid model that is not yet loaded is requested and will
        /// resolve on a later frame, so the two cases are reported separately: one is
        /// a missing asset, the other is normal streaming.
        /// </para>
        /// </summary>
        public ModelAvailability GetModelAvailability(uint modelHash)
        {
            if (modelHash == 0)
            {
                return ModelAvailability.Unavailable;
            }

            try
            {
                var model = new Model(unchecked((int)modelHash));
                if (!model.IsValid)
                {
                    return ModelAvailability.Unavailable;
                }

                if (model.IsLoaded)
                {
                    return ModelAvailability.Available;
                }

                model.Request();
                return ModelAvailability.Loading;
            }
            catch (Exception exception)
            {
                // A throwing streamer query is not a reason to stop replicating.
                // Treated as "loading" so the caller retries rather than recording a
                // missing mod that may not be missing.
                _log.Debug(LogCategory.Entity, $"Model query for 0x{modelHash:X8} threw: {exception.Message}");
                return ModelAvailability.Loading;
            }
        }

        public bool IsPlayerReady
        {
            get
            {
                if (Game.IsLoading || Game.IsPaused)
                {
                    return false;
                }

                Ped character = Game.Player.Character;
                return character != null && character.Exists();
            }
        }

        // ------------------------------------------------------------------
        // Local player
        // ------------------------------------------------------------------
        public LocalPlayerSample SampleLocalPlayer()
        {
            Ped ped = Game.Player.Character;
            var sample = new LocalPlayerSample
            {
                Position = ToNet(ped.Position),
                Velocity = ToNet(ped.Velocity),
                Heading = ped.Heading,
                Health = ped.Health,
                MaxHealth = ped.MaxHealth,
                Armor = ped.Armor,
                ModelHash = unchecked((uint)ped.Model.Hash),
                Movement = SampleMovement(ped),
                Flags = SampleFlags(ped),
                InteriorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, ped.Handle),
                AnimationHash = 0,
                AimPosition = SampleAimPosition(ped),
                Appearance = SampleAppearance(ped),
            };

            Weapon weapon = ped.Weapons.Current;
            if (weapon != null)
            {
                sample.CurrentWeaponHash = unchecked((uint)weapon.Hash);
                sample.Ammo = weapon.Ammo;
                sample.WeaponTint = (byte)weapon.Tint;
                sample.WeaponComponents = ReadWeaponComponents(weapon);
            }

            sample.Ragdoll = SampleRagdollPose(ped, sample.Flags, sample.Position);
            return sample;
        }

        /// <summary>
        /// The components actually fitted to a weapon: suppressor, scope, extended
        /// clip, grip, flashlight.
        /// <para>
        /// Enumerating the collection asks the game which variants exist for this
        /// weapon and which are active. Only the active ones travel — the full list of
        /// what *could* be fitted is the same on every client that has the weapon, and
        /// sending it would be a dozen hashes a player to say nothing.
        /// </para>
        /// </summary>
        private static List<uint>? ReadWeaponComponents(Weapon weapon)
        {
            try
            {
                List<uint>? active = null;
                foreach (WeaponComponent component in weapon.Components)
                {
                    if (!component.Active)
                    {
                        continue;
                    }

                    active ??= new List<uint>(4);
                    if (active.Count >= CharacterEntity.MaxWeaponComponents)
                    {
                        break;
                    }

                    active.Add(unchecked((uint)component.ComponentHash));
                }

                // An empty list rather than null when the weapon is bare: null means
                // "not read", and the difference decides whether a remote ped keeps
                // the suppressor it had.
                return active ?? new List<uint>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Head and both feet, as offsets from the ped's root, while it is
        /// ragdolling.
        /// <para>
        /// Offsets rather than world positions for two reasons. On the wire they
        /// quantise into a metre-scale range instead of a world-scale one, which is
        /// four bytes a bone cheaper. On the receiving side they stay correct when
        /// the remote copy's root has been interpolated or corrected — a world
        /// position paired with a root that has moved describes a body pulled apart.
        /// </para>
        /// </summary>
        private static RagdollPose SampleRagdollPose(Ped ped, PlayerFlags flags, NetVector3 root)
        {
            if ((flags & PlayerFlags.Ragdoll) == 0)
            {
                return RagdollPose.None;
            }

            try
            {
                return new RagdollPose(
                    ToNet(ped.Bones[Bone.SkelHead].Position) - root,
                    ToNet(ped.Bones[Bone.SkelRightFoot].Position) - root,
                    ToNet(ped.Bones[Bone.SkelLeftFoot].Position) - root);
            }
            catch (Exception)
            {
                // A model whose skeleton lacks one of these bones. Reporting no pose
                // leaves the remote copy on its own physics, which is what it had
                // before any of this existed.
                return RagdollPose.None;
            }
        }

        /// <summary>
        /// The point the player is aiming at, taken from the gameplay camera.
        /// <para>
        /// GTA V has no native for "where is this ped aiming"; the aim direction is a
        /// property of the camera, not the ped. Projecting the camera ray 150 m gives
        /// a target the remote side can aim its ped at, which is what the pose needs —
        /// it is not a hit position and is not used as one.
        /// </para>
        /// </summary>
        private static NetVector3 SampleAimPosition(Ped ped)
        {
            if (!Game.Player.IsAiming)
            {
                return ToNet(ped.Position + (ped.ForwardVector * 10f));
            }

            Vector3 origin = GameplayCamera.Position;
            Vector3 direction = GameplayCamera.Direction;
            return ToNet(origin + (direction * 150f));
        }

        private PedAppearance? SampleAppearance(Ped ped)
        {
            int now = Game.GameTime;
            if (_lastAppearanceSampleTick != 0 && now - _lastAppearanceSampleTick < AppearanceSampleIntervalMilliseconds)
            {
                return _localAppearance;
            }

            _lastAppearanceSampleTick = now;

            for (int slot = 0; slot < PedAppearance.ComponentSlots; slot++)
            {
                int drawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped.Handle, slot);
                int texture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped.Handle, slot);
                int palette = Function.Call<int>(Hash.GET_PED_PALETTE_VARIATION, ped.Handle, slot);

                _localAppearance.SetComponent(
                    slot,
                    (ushort)Clamp(drawable, 0, ushort.MaxValue),
                    (byte)Clamp(texture, 0, byte.MaxValue),
                    (byte)Clamp(palette, 0, byte.MaxValue));
            }

            for (int slot = 0; slot < PedAppearance.PropSlots; slot++)
            {
                int drawable = Function.Call<int>(Hash.GET_PED_PROP_INDEX, ped.Handle, slot);
                if (drawable < 0)
                {
                    _localAppearance.SetProp(slot, PedAppearance.NoProp, 0);
                    continue;
                }

                int texture = Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, ped.Handle, slot);
                _localAppearance.SetProp(
                    slot, (short)Clamp(drawable, 0, short.MaxValue), (byte)Clamp(texture, 0, byte.MaxValue));
            }

            return _localAppearance;
        }

        public void ApplyLocalCorrection(NetVector3 position, float heading, int health, int armor)
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists())
            {
                return;
            }

            // A respawn arrives as a correction: the server has already moved the
            // player and refilled their health, so the client must revive before
            // placing them or the game leaves them dead at the new position.
            if (ped.IsDead && health > 0)
            {
                Function.Call(
                    Hash.NETWORK_RESURRECT_LOCAL_PLAYER,
                    position.X, position.Y, position.Z, heading, false, false);
            }

            ped.PositionNoOffset = ToGame(position);
            ped.Heading = heading;
            ped.Health = health;
            ped.Armor = armor;
        }

        // ------------------------------------------------------------------
        // Remote peds
        // ------------------------------------------------------------------
        public int CreateRemotePed(uint modelHash, NetVector3 position, float heading)
        {
            try
            {
                var model = new Model(unchecked((int)(modelHash == 0 ? DefaultPedModel : modelHash)));
                if (!model.IsValid)
                {
                    model = new Model(unchecked((int)DefaultPedModel));
                }

                // Request is asynchronous. Returning 0 makes the caller retry on a
                // later frame, which is cheaper than blocking the game thread.
                if (!model.IsLoaded)
                {
                    model.Request();
                    return 0;
                }

                Ped? ped = GtaWorld.CreatePed(model, ToGame(position), heading);
                model.MarkAsNoLongerNeeded();
                if (ped == null || !ped.Exists())
                {
                    return 0;
                }

                // A replicated ped must not be simulated by the local game: no AI
                // reactions, no ragdoll from local physics, no damage from local
                // events. Its state comes from the server and nowhere else.
                // Damageable on purpose, and never actually harmed.
                //
                // A remote ped's health comes from the server and is rewritten every
                // frame, so leaving it invincible cost nothing visible — except that
                // the engine then records no hit against it, and the engine's hit
                // record is the only thing on this machine that knows the local player
                // shot somebody. Letting damage land, reading it, and putting the
                // health straight back is how the arbiter gets told about it at all.
                // The safeguards below stop the local game acting on damage it is
                // allowed to register: no critical hits, no death from injury.
                ped.IsInvincible = false;
                Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, ped.Handle, false);
                Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, ped.Handle, false);

                // Proof against everything that can take a ped from full health to
                // zero inside one frame — fire, explosions, collisions, drowning —
                // and deliberately *not* proof against bullets or melee, which are
                // the two the hit sampler exists to notice. The health is rewritten
                // from the server every frame either way; what these prevent is the
                // local game killing a ped outright before that frame comes round,
                // which GTA V will not let us undo in place.
                Function.Call(
                    Hash.SET_ENTITY_PROOFS, ped.Handle,
                    false, true, true, true, false, true, 0, true);
                ped.BlockPermanentEvents = true;
                ped.CanRagdoll = false;
                ped.RelationshipGroup = Game.Player.Character.RelationshipGroup;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, false);
                Function.Call(Hash.SET_PED_KEEP_TASK, ped.Handle, true);
                Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, ped.Handle, true);

                _remotePeds[ped.Handle] = ped;
                _driveState[ped.Handle] = new PedDriveState();
                return ped.Handle;
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Client, "Could not create a remote ped.", exception);
                return 0;
            }
        }

        public bool TryGetRemotePedPosition(int handle, out NetVector3 position)
        {
            if (_remotePeds.TryGetValue(handle, out Ped ped) && ped.Exists())
            {
                position = ToNet(ped.Position);
                return true;
            }

            position = NetVector3.Zero;
            return false;
        }

        public void ApplyRemotePedCommand(int handle, in RemotePedCommand command)
        {
            if (!_remotePeds.TryGetValue(handle, out Ped ped) || !ped.Exists())
            {
                _remotePeds.Remove(handle);
                _driveState.Remove(handle);
                return;
            }

            if (!_driveState.TryGetValue(handle, out PedDriveState state))
            {
                state = new PedDriveState();
                _driveState[handle] = state;
            }

            ApplyVitals(ped, in command, state);
            ApplyWeapon(ped, command.WeaponHash, state);
            ApplyWeaponAttachments(ped, in command, state);
            ApplyPosture(ped, in command, state);

            if (command.Action != RemotePedAction.InVehicle)
            {
                LeaveVehicle(ped, state);
            }

            switch (command.Action)
            {
                case RemotePedAction.Dead:
                    DriveDead(ped, in command, state);
                    return;

                case RemotePedAction.Ragdoll:
                    DriveRagdoll(ped, in command, state);
                    return;

                case RemotePedAction.InVehicle:
                    DriveSeated(ped, in command, state);
                    return;


                case RemotePedAction.Idle:
                    DriveIdle(ped, in command, state);
                    return;

                default:
                    DriveLocomotion(ped, in command, state);
                    return;
            }
        }

        /// <summary>
        /// Puts the reported weapon in a remote player's hands.
        /// <para>
        /// Nothing did this before. The weapon was read from the local player, sent,
        /// stored, replicated and printed by <c>players</c> and <c>diff</c> — and never
        /// applied, so every remote player stood empty-handed whatever they were
        /// carrying, while the damage arbiter on the server scored their rifle hits.
        /// </para>
        /// <para>
        /// Only on a change, because <c>GiveWeaponToPed</c> every frame re-equips and
        /// visibly interrupts the draw animation. And unarmed is applied explicitly
        /// rather than skipped: holstering is a change like any other, and the version
        /// of this bug that only forgets the unarmed case leaves a player permanently
        /// holding the last thing they drew.
        /// </para>
        /// </summary>
        private static void ApplyWeapon(Ped ped, uint weaponHash, PedDriveState state)
        {
            if (state.AppliedWeapon == weaponHash)
            {
                return;
            }

            try
            {
                if (weaponHash == 0)
                {
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON, ped.Handle, (uint)WeaponHash.Unarmed, true);
                }
                else
                {
                    Function.Call(Hash.GIVE_WEAPON_TO_PED, ped.Handle, weaponHash, 250, false, true);
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON, ped.Handle, weaponHash, true);
                }

                state.AppliedWeapon = weaponHash;
            }
            catch (Exception)
            {
                // A weapon hash from a mod this client does not have. Left unapplied and
                // retried on the next change rather than taking the frame down; the
                // missing-content tracker is what reports an unresolvable hash.
            }
        }

        private void ApplyVitals(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            if (command.Action == RemotePedAction.Dead)
            {
                return;
            }

            if (state.WasDead)
            {
                // Coming back from dead: the ped model has to be respawned, because a
                // dead ped in GTA V cannot be revived in place.
                state.WasDead = false;
            }

            if (ped.IsDead)
            {
                // The local game killed a ped the server says is alive. The proofs
                // above make this unlikely rather than impossible, and a dead ped
                // cannot be revived in place — so it is discarded and the player
                // manager builds a new one next frame, which is the same recovery a
                // model change uses.
                _log.Warning(
                    LogCategory.Client,
                    "A remote ped died locally while the server had it alive; rebuilding it.");
                DestroyRemotePed(ped.Handle);
                return;
            }

            int health = command.Health < 1 ? 1 : command.Health;
            if (ped.Health != health)
            {
                ped.Health = health;
            }

            if (ped.Armor != command.Armor)
            {
                ped.Armor = command.Armor;
            }

            // The baseline the hit sampler measures against. Without it a hit can be
            // detected but not sized, and a damage report with no number in it is not
            // a report.
            state.AppliedHealth = health;
            state.AppliedArmor = command.Armor;
        }

        private void DriveDead(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            if (!state.WasDead)
            {
                state.WasDead = true;
                state.Reset();
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, ped.Handle);
                ped.IsInvincible = false;
                ped.Health = 0;
                ped.IsInvincible = true;
                return;
            }

            // Corpses drift. Nudge, do not re-place, or the body twitches.
            if (NetVector3.Distance(ToNet(ped.Position), command.TargetPosition) > RemotePedController.HardCorrectDistance)
            {
                Place(ped, command.TargetPosition, command.Heading);
            }
        }

        /// <summary>
        /// Starts the ragdoll on the first frame, then keeps the local body in step
        /// with the owner's by pulling on three limbs.
        /// <para>
        /// Before this, the ragdoll was started and then left alone: each machine ran
        /// its own solver from that moment on, and where a fallen player ended up had
        /// nothing to do with where they had fallen on their own screen. The position
        /// kept arriving and was deliberately not applied, because writing
        /// coordinates into a running solver is what makes replicated ragdolls
        /// twitch — so the state was correct, replicated, and visible to nobody.
        /// </para>
        /// </summary>
        private void DriveRagdoll(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            if (!state.Ragdolling)
            {
                state.Ragdolling = true;
                state.RagdollFrames = 0;
                state.Reset();
                Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, true);
                Function.Call(Hash.SET_PED_TO_RAGDOLL, ped.Handle, 2000, 3000, 0, true, true, false);
                return;
            }

            state.RagdollFrames++;

            if (command.HardCorrect)
            {
                // Two different falls. Impulses cannot close that gap; the body is put
                // where it belongs and the solver carries on from there.
                Place(ped, command.TargetPosition, command.Heading);
                state.RagdollFrames = 0;
                return;
            }

            if (!RagdollDriver.ShouldCorrect(state.RagdollFrames))
            {
                return;
            }

            RagdollCorrection correction;
            try
            {
                correction = RagdollDriver.Compute(
                    command.Ragdoll,
                    command.TargetPosition,
                    ToNet(ped.Bones[Bone.SkelHead].Position),
                    ToNet(ped.Bones[Bone.SkelRightFoot].Position),
                    ToNet(ped.Bones[Bone.SkelLeftFoot].Position));
            }
            catch (Exception)
            {
                return;
            }

            if (correction.IsEmpty)
            {
                return;
            }

            var helper = new ApplyImpulseHelper(ped);
            ApplyImpulse(helper, correction, RagdollBones.Head, RagdollDriver.HeadPart);
            ApplyImpulse(helper, correction, RagdollBones.RightFoot, RagdollDriver.RightFootPart);
            ApplyImpulse(helper, correction, RagdollBones.LeftFoot, RagdollDriver.LeftFootPart);
        }

        private static void ApplyImpulse(
            ApplyImpulseHelper helper, in RagdollCorrection correction, RagdollBones bone, int partIndex)
        {
            if (!correction.Has(bone))
            {
                return;
            }

            NetVector3 impulse = bone switch
            {
                RagdollBones.Head => correction.Head,
                RagdollBones.RightFoot => correction.RightFoot,
                _ => correction.LeftFoot,
            };

            helper.EqualizeAmount = 1f;
            helper.PartIndex = partIndex;
            helper.Impulse = ToGame(impulse);
            helper.Start();
            helper.Stop();
        }

        /// <summary>
        /// Puts a riding ped in its seat, once.
        /// <para>
        /// Before this, <c>SeatRemotePedInVehicle</c> was on the bridge interface,
        /// implemented, and called by nothing: a passing car was drawn empty while its
        /// driver stood at the car's coordinates, sliding along the road with it.
        /// </para>
        /// <para>
        /// Once, because seating is a task: re-issuing it every frame restarts the
        /// entry animation and the ped climbs into the same seat forever. The seat is
        /// re-asserted only when the vehicle or the seat index actually changes, or
        /// when the game has taken the ped out of the car on its own.
        /// </para>
        /// </summary>
        private void DriveSeated(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            LeaveRagdoll(ped, state);

            if (command.VehicleHandle == 0)
            {
                // No vehicle to sit in on this client — it has not been created yet, or
                // its model is missing. Holding the ped at the reported position is
                // wrong-looking; leaving it where it was is worse.
                Place(ped, command.TargetPosition, command.Heading);
                state.Reset();
                state.SeatedVehicle = 0;
                return;
            }

            bool alreadySeated = state.SeatedVehicle == command.VehicleHandle
                && state.SeatedIndex == command.VehicleSeat
                && ped.IsInVehicle();

            if (alreadySeated)
            {
                return;
            }

            SeatRemotePedInVehicle(ped.Handle, command.VehicleHandle, command.VehicleSeat);
            state.Reset();
            state.SeatedVehicle = command.VehicleHandle;
            state.SeatedIndex = command.VehicleSeat;
        }

        /// <summary>
        /// Takes a ped back out of a car once the server says it is on foot.
        /// <para>
        /// Placing a ped that is still sitting in a vehicle moves the seat, not the
        /// ped — so without this a player who got out stayed in the car on every other
        /// screen, being driven around by a driver who had also left.
        /// <c>CLEAR_PED_TASKS_IMMEDIATELY</c> ejects rather than tasking an exit,
        /// because an exit animation takes about a second and the next frame is going
        /// to place this ped somewhere else anyway.
        /// </para>
        /// </summary>
        private static void LeaveVehicle(Ped ped, PedDriveState state)
        {
            if (state.SeatedVehicle == 0)
            {
                return;
            }

            state.SeatedVehicle = 0;
            state.SeatedIndex = -2;

            if (ped.IsInVehicle())
            {
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, ped.Handle);
                state.Reset();
            }
        }

        private void DriveIdle(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            LeaveRagdoll(ped, state);

            if (state.Tasked)
            {
                Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);
                state.Reset();
            }

            if (command.HardCorrect
                || NetVector3.Distance(ToNet(ped.Position), command.TargetPosition) > RemotePedController.ArrivalDistance)
            {
                Place(ped, command.TargetPosition, command.Heading);
            }
            else
            {
                ped.Heading = command.Heading;
            }

            ApplyAim(ped, in command);
        }

        private void DriveLocomotion(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            LeaveRagdoll(ped, state);

            if (command.HardCorrect)
            {
                // Too far behind to walk it off without the ped visibly running through
                // scenery for several seconds.
                Place(ped, command.TargetPosition, command.Heading);
                state.Reset();
            }

            bool destinationMoved =
                NetVector3.Distance(state.TaskTarget, command.TargetPosition) > RetaskDistance;

            // Re-issuing the task every frame restarts the animation and produces a
            // ped that jitters in place, so it is only re-issued when the destination
            // has actually moved or the gait changed.
            if (!state.Tasked || destinationMoved || state.TaskBlend != command.MoveBlendRatio)
            {
                Function.Call(
                    Hash.TASK_GO_STRAIGHT_TO_COORD,
                    ped.Handle,
                    command.TargetPosition.X,
                    command.TargetPosition.Y,
                    command.TargetPosition.Z,
                    command.MoveBlendRatio,
                    TaskTimeoutMilliseconds,
                    command.Heading,
                    0f);

                state.Tasked = true;
                state.TaskTarget = command.TargetPosition;
                state.TaskBlend = command.MoveBlendRatio;
            }

            Function.Call(Hash.SET_PED_DESIRED_MOVE_BLEND_RATIO, ped.Handle, command.MoveBlendRatio);
            ApplyAim(ped, in command);
        }

        /// <summary>
        /// The two posture flags this layer can act on: crouching and reloading.
        /// <para>
        /// GTA V has no "crouch" for a ped — what a player sees as crouching is
        /// stealth movement, which is a mode rather than a task, so it is set on
        /// change and left alone. Reloading is a task and has to be issued once per
        /// reload; re-issuing it every frame restarts the animation and the ped
        /// fumbles the magazine forever.
        /// </para>
        /// <para>
        /// The other posture flags are replicated and **not** applied here. They are
        /// listed, with the reason for each, in docs/ENTITY_SYSTEM.md — a flag that
        /// travels to no effect is worth naming rather than leaving to be discovered.
        /// </para>
        /// </summary>
        /// <summary>
        /// Fits the components and tint the owner has on the weapon this ped is
        /// holding.
        /// <para>
        /// Without this a remote player's silenced, scoped rifle appears as a bare
        /// one: the weapon hash names the weapon, not what is bolted to it. Applied
        /// on change, because <c>GIVE_WEAPON_COMPONENT_TO_PED</c> re-equips the weapon
        /// and calling it every frame keeps a ped permanently mid-draw.
        /// </para>
        /// <para>
        /// A null list means the reporting client could not read them, and the safe
        /// answer there is to leave the weapon alone rather than strip it.
        /// </para>
        /// </summary>
        private static void ApplyWeaponAttachments(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            if (command.WeaponComponents == null || command.WeaponHash == 0)
            {
                return;
            }

            if (state.AppliedTint == command.WeaponTint
                && state.AppliedComponentsWeapon == command.WeaponHash
                && SameComponents(state.AppliedComponents, command.WeaponComponents))
            {
                return;
            }

            try
            {
                foreach (uint previous in state.AppliedComponents)
                {
                    if (!command.WeaponComponents.Contains(previous))
                    {
                        Function.Call(
                            Hash.REMOVE_WEAPON_COMPONENT_FROM_PED, ped.Handle, command.WeaponHash, previous);
                    }
                }

                foreach (uint component in command.WeaponComponents)
                {
                    Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED, ped.Handle, command.WeaponHash, component);
                }

                Function.Call(
                    Hash.SET_PED_WEAPON_TINT_INDEX, ped.Handle, command.WeaponHash, (int)command.WeaponTint);

                state.AppliedTint = command.WeaponTint;
                state.AppliedComponentsWeapon = command.WeaponHash;
                state.AppliedComponents.Clear();
                state.AppliedComponents.AddRange(command.WeaponComponents);
            }
            catch (Exception)
            {
                // A component from a weapon mod this client does not have. The weapon
                // stays as it is rather than half-fitted.
            }
        }

        private static bool SameComponents(List<uint> applied, List<uint> wanted)
        {
            if (applied.Count != wanted.Count)
            {
                return false;
            }

            for (int i = 0; i < applied.Count; i++)
            {
                if (applied[i] != wanted[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void ApplyPosture(Ped ped, in RemotePedCommand command, PedDriveState state)
        {
            bool crouching = (command.Flags & PlayerFlags.Crouching) != 0;
            if (state.Crouching != crouching)
            {
                state.Crouching = crouching;
                Function.Call(Hash.SET_PED_STEALTH_MOVEMENT, ped.Handle, crouching, 0);
            }

            bool reloading = (command.Flags & PlayerFlags.Reloading) != 0;
            if (reloading && !state.Reloading)
            {
                Function.Call(Hash.TASK_RELOAD_WEAPON, ped.Handle, true);
            }

            state.Reloading = reloading;
        }

        private static void ApplyAim(Ped ped, in RemotePedCommand command)
        {
            if (!command.Aiming)
            {
                return;
            }

            Function.Call(
                Hash.TASK_AIM_GUN_AT_COORD,
                ped.Handle,
                command.AimPosition.X,
                command.AimPosition.Y,
                command.AimPosition.Z,
                200,
                false,
                false);
        }

        private static void LeaveRagdoll(Ped ped, PedDriveState state)
        {
            if (!state.Ragdolling)
            {
                return;
            }

            state.Ragdolling = false;
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, false);
        }

        private static void Place(Ped ped, NetVector3 position, float heading)
        {
            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET, ped.Handle, position.X, position.Y, position.Z, false, false, false);
            ped.Heading = heading;
        }

        /// <summary>
        /// Reads the rounds the local player fired since the previous frame.
        /// <para>
        /// The counting lives in <see cref="ShotDetector"/>, which is unit-tested;
        /// what is here is the two things only the game can answer — how full the clip
        /// is, and where the round started and ended.
        /// </para>
        /// </summary>
        public LocalShotSample SampleLocalShots()
        {
            var sample = default(LocalShotSample);

            try
            {
                Ped ped = Game.Player.Character;
                if (!ped.Exists() || ped.IsDead)
                {
                    _shots.Reset();
                    return sample;
                }

                Weapon weapon = ped.Weapons.Current;
                if (weapon == null || !weapon.IsPresent || !ShotDetector.IsHitscan(Classify(weapon.Group)))
                {
                    // A thrown grenade or a rocket is an entity that flies; drawing it
                    // as an instant line from muzzle to impact would show everyone an
                    // explosion arriving at the speed of light. Resetting rather than
                    // returning zero keeps the next hitscan weapon from inheriting this
                    // one's clip count.
                    _shots.Reset();
                    return sample;
                }

                uint weaponHash = unchecked((uint)weapon.Hash);
                int rounds = _shots.Observe(weaponHash, weapon.AmmoInClip, ped.IsShooting);
                if (rounds <= 0)
                {
                    return sample;
                }

                sample.Rounds = rounds;
                sample.WeaponHash = weaponHash;
                sample.Origin = ToNet(MuzzlePosition(ped));

                // The impact is where the game's own trace landed. It is zero when the
                // round hit nothing within range, and the aim point is then the honest
                // answer — a tracer to the horizon rather than one to the origin.
                Vector3 impact = ped.LastWeaponImpactPosition;
                sample.Impact = impact == Vector3.Zero ? SampleAimPosition(ped) : ToNet(impact);
                return sample;
            }
            catch (Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// Reads the hits the local player landed on other players since the previous
        /// frame, and restores every ped it touched.
        /// <para>
        /// <b>The damage number comes from the game, not from us.</b> It is the drop
        /// in health plus armour that the engine itself computed, so range falloff,
        /// body armour, weapon components and mod weapons are all already in it. The
        /// server clamps it against its own envelope regardless — this is a claim,
        /// and it is treated as one.
        /// </para>
        /// <para>
        /// A hit the engine recorded but did not size — the damage landed on
        /// something this bridge does not measure — is dropped rather than reported
        /// with an invented number.
        /// </para>
        /// </summary>
        public void SampleLocalHits(List<LocalHitSample> into)
        {
            if (_remotePeds.Count == 0)
            {
                return;
            }

            try
            {
                Ped player = Game.Player.Character;
                if (!player.Exists())
                {
                    return;
                }

                uint weaponHash = 0;
                bool melee = false;
                Weapon weapon = player.Weapons.Current;
                if (weapon != null && weapon.IsPresent)
                {
                    weaponHash = unchecked((uint)weapon.Hash);
                    melee = weapon.Group == WeaponGroup.Melee || weapon.Group == WeaponGroup.Unarmed;
                }

                foreach (KeyValuePair<int, Ped> entry in _remotePeds)
                {
                    Ped ped = entry.Value;
                    if (!ped.Exists()
                        || !Function.Call<bool>(
                            Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, ped.Handle, player.Handle, true))
                    {
                        continue;
                    }

                    Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, ped.Handle);

                    if (!_driveState.TryGetValue(entry.Key, out PedDriveState state) || state.AppliedHealth < 0)
                    {
                        continue;
                    }

                    int damage = (state.AppliedHealth + state.AppliedArmor) - (ped.Health + ped.Armor);

                    // Put it back before the local game can act on it. The server owns
                    // this ped's health; what happened here was a measurement.
                    ped.Health = state.AppliedHealth;
                    ped.Armor = state.AppliedArmor;

                    if (damage <= 0)
                    {
                        continue;
                    }

                    into.Add(new LocalHitSample
                    {
                        PedHandle = entry.Key,
                        WeaponHash = weaponHash,
                        Damage = damage,
                        HitPosition = ToNet(ped.Position),
                        HitBone = LastDamagedBone(ped),
                        IsMelee = melee,
                    });
                }
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Client, "Could not read local hits.", exception);
            }
        }

        /// <summary>
        /// Which bone the game recorded as last damaged, or -1 when it recorded none.
        /// The server uses it for hit-location logic; an invented value would be worse
        /// than an absent one.
        /// </summary>
        private static short LastDamagedBone(Ped ped)
        {
            try
            {
                PedBone bone = ped.Bones.LastDamaged;
                return bone.IsValid ? unchecked((short)bone.Index) : (short)-1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public void PlayRemoteShot(int pedHandle, uint weaponHash, NetVector3 origin, NetVector3 impact)
        {
            if (!_remotePeds.TryGetValue(pedHandle, out Ped ped) || !ped.Exists())
            {
                return;
            }

            try
            {
                if (_shotAsset?.Hash != unchecked((int)weaponHash))
                {
                    _shotAsset?.MarkAsNoLongerNeeded();
                    _shotAsset = new WeaponAsset(weaponHash);
                }

                // Request is asynchronous; the first shot with a newly seen weapon is
                // dropped rather than drawn with the wrong model.
                if (!_shotAsset.Value.IsLoaded)
                {
                    _shotAsset.Value.Request();
                    return;
                }

                // Damage zero, deliberately and permanently. The hit is arbitrated by
                // the server from the shooter's own damage report; a rendered bullet
                // that also wounded would count one trigger pull once per client that
                // drew it.
                GtaWorld.ShootBullet(ToGame(origin), ToGame(impact), ped, _shotAsset.Value, 0, -1f);
                PlayMuzzleFlash(ped, origin);
            }
            catch (Exception)
            {
                // A weapon model this client does not have. The shot is silently not
                // drawn, which is what a missing mod costs here.
            }
        }

        /// <summary>
        /// The muzzle flash. Drawn separately because <c>ShootBullet</c> renders the
        /// round and its impact but nothing at the barrel.
        /// <para>
        /// The effect names are Rockstar's own, taken from the weapon groups. They are
        /// **not verified against a running game** — an unknown name produces no
        /// effect rather than an error, so a wrong one here costs a flash and nothing
        /// else.
        /// </para>
        /// </summary>
        private void PlayMuzzleFlash(Ped ped, NetVector3 origin)
        {
            if (!_muzzleAsset.IsLoaded)
            {
                _muzzleAsset.Request();
                return;
            }

            Prop weaponObject = ped.Weapons.CurrentWeaponObject;
            Vector3 rotation = weaponObject != null && weaponObject.Exists() ? weaponObject.Rotation : ped.Rotation;

            GtaWorld.CreateParticleEffectNonLooped(
                _muzzleAsset, MuzzleEffect(ped.Weapons.Current?.Group ?? WeaponGroup.Unarmed),
                ToGame(origin), rotation, 1f);
        }

        private static string MuzzleEffect(WeaponGroup group) => group switch
        {
            WeaponGroup.Pistol => "muz_pistol",
            WeaponGroup.SMG => "muz_smg",
            WeaponGroup.Shotgun => "muz_shotgun",
            WeaponGroup.Sniper => "muz_sniper_rifle",
            WeaponGroup.MG => "muz_minigun",
            _ => "muz_assault_rifle",
        };

        /// <summary>
        /// Where the round leaves the weapon. The weapon model carries a
        /// <c>gun_muzzle</c> bone; a weapon whose model has not streamed in yet does
        /// not, and the firing hand is close enough that the difference is a few
        /// centimetres over a shot that may be a hundred metres long.
        /// </summary>
        private static Vector3 MuzzlePosition(Ped ped)
        {
            Prop weaponObject = ped.Weapons.CurrentWeaponObject;
            if (weaponObject != null && weaponObject.Exists() && weaponObject.Bones.Contains("gun_muzzle"))
            {
                return weaponObject.Bones["gun_muzzle"].Position;
            }

            return ped.Bones[Bone.SkelRightHand].Position + (ped.ForwardVector * 0.4f);
        }

        /// <summary>
        /// What a weapon sends downrange, from its group.
        /// <para>
        /// Groups rather than a hash list because a hash list cannot classify a
        /// weapon added by a mod. `Heavy` is excluded even though a railgun in it is
        /// hitscan: the same group holds the rocket and grenade launchers, and
        /// drawing a rocket as an instant line is a worse error than not drawing a
        /// railgun at all.
        /// </para>
        /// </summary>
        private static WeaponClass Classify(WeaponGroup group) => group switch
        {
            WeaponGroup.Pistol => WeaponClass.Hitscan,
            WeaponGroup.SMG => WeaponClass.Hitscan,
            WeaponGroup.AssaultRifle => WeaponClass.Hitscan,
            WeaponGroup.MG => WeaponClass.Hitscan,
            WeaponGroup.Shotgun => WeaponClass.Hitscan,
            WeaponGroup.Sniper => WeaponClass.Hitscan,
            WeaponGroup.Thrown => WeaponClass.Projectile,
            WeaponGroup.Heavy => WeaponClass.Projectile,
            _ => WeaponClass.None,
        };

        public void ApplyRemotePedAppearance(int handle, PedAppearance appearance)
        {
            if (!_remotePeds.TryGetValue(handle, out Ped ped) || !ped.Exists())
            {
                return;
            }

            for (int slot = 0; slot < PedAppearance.ComponentSlots; slot++)
            {
                PedAppearance.ComponentVariation component = appearance.GetComponent(slot);
                Function.Call(
                    Hash.SET_PED_COMPONENT_VARIATION,
                    ped.Handle,
                    slot,
                    (int)component.Drawable,
                    (int)component.Texture,
                    (int)component.Palette);
            }

            for (int slot = 0; slot < PedAppearance.PropSlots; slot++)
            {
                PedAppearance.PropVariation prop = appearance.GetProp(slot);
                if (prop.IsEmpty)
                {
                    Function.Call(Hash.CLEAR_PED_PROP, ped.Handle, slot);
                    continue;
                }

                Function.Call(Hash.SET_PED_PROP_INDEX, ped.Handle, slot, (int)prop.Drawable, (int)prop.Texture, true);
            }
        }

        public void DestroyRemotePed(int handle)
        {
            _driveState.Remove(handle);
            if (!_remotePeds.TryGetValue(handle, out Ped ped))
            {
                return;
            }

            _remotePeds.Remove(handle);
            try
            {
                if (ped.Exists())
                {
                    ped.MarkAsNoLongerNeeded();
                    ped.Delete();
                }
            }
            catch (Exception exception)
            {
                _log.Warning(LogCategory.Client, $"Could not delete remote ped {handle}: {exception.Message}");
            }
        }

        public bool IsRemotePedValid(int handle) =>
            handle != 0 && _remotePeds.TryGetValue(handle, out Ped ped) && ped.Exists();

        // ------------------------------------------------------------------
        // Vehicles and objects — delegated so this file stays about peds
        // ------------------------------------------------------------------
        public int CreateRemoteVehicle(uint modelHash, NetVector3 position, float heading) =>
            _vehicles.CreateRemoteVehicle(modelHash, position, heading);

        public void ApplyRemoteVehicle(int handle, in RemoteVehicleFrame frame, int trailerHandle) =>
            _vehicles.ApplyRemoteVehicle(handle, in frame, trailerHandle);

        public void ApplyRemoteVehicleAppearance(int handle, VehicleEntity state) =>
            _vehicles.ApplyRemoteVehicleAppearance(handle, state);

        public bool TryReadVehicle(int handle, VehicleEntity into) => _vehicles.TryReadVehicle(handle, into);

        public void DestroyRemoteVehicle(int handle) => _vehicles.DestroyRemoteVehicle(handle);

        public bool IsRemoteVehicleValid(int handle) => _vehicles.IsRemoteVehicleValid(handle);

        public int GetLocalPlayerVehicleHandle() => _vehicles.GetLocalPlayerVehicleHandle();

        public uint GetVehicleModel(int handle) => _vehicles.GetVehicleModel(handle);

        public void SeatRemotePedInVehicle(int pedHandle, int vehicleHandle, sbyte seat) =>
            _vehicles.SeatRemotePedInVehicle(pedHandle, vehicleHandle, seat);

        public int CreateRemoteObject(uint modelHash, NetVector3 position, float heading) =>
            _vehicles.CreateRemoteObject(modelHash, position, heading);

        public void ApplyRemoteObject(int handle, ObjectEntity state, int attachParentHandle) =>
            _vehicles.ApplyRemoteObject(handle, state, attachParentHandle);

        public void DestroyRemoteObject(int handle) => _vehicles.DestroyRemoteObject(handle);

        public bool IsRemoteObjectValid(int handle) => _vehicles.IsRemoteObjectValid(handle);

        // ------------------------------------------------------------------
        // World
        // ------------------------------------------------------------------
        public void SetWeather(uint weatherHash, uint nextWeatherHash, float transition)
        {
            if (!WeatherCatalog.TryGetName(weatherHash, out string name))
            {
                // A weather type from a mod this client does not have. Leaving the
                // local weather alone is better than snapping it to a wrong value.
                return;
            }

            if (!TryParseWeather(name, out Weather weather))
            {
                return;
            }

            if (nextWeatherHash != 0
                && WeatherCatalog.TryGetName(nextWeatherHash, out string nextName)
                && TryParseWeather(nextName, out Weather next)
                && transition > 0f)
            {
                if (GtaWorld.Weather != weather)
                {
                    GtaWorld.Weather = weather;
                }

                GtaWorld.TransitionToWeather(next, transition);
                return;
            }

            if (GtaWorld.Weather != weather)
            {
                GtaWorld.Weather = weather;
            }
        }

        public void SetClock(int hours, int minutes, int seconds)
        {
            TimeSpan target = new TimeSpan(hours, minutes, seconds);
            TimeSpan current = GtaWorld.CurrentTimeOfDay;

            // Writing the clock every frame makes the sky flicker; only correct when
            // the local clock has drifted more than a few in-game seconds.
            if (Math.Abs((target - current).TotalSeconds) > 20d)
            {
                GtaWorld.CurrentTimeOfDay = target;
            }
        }

        public void ShowNotification(string text) => GTA.UI.Notification.Show(text, false);

        public void ShowSubtitle(string text, int durationMilliseconds) =>
            GTA.UI.Screen.ShowSubtitle(text, durationMilliseconds);

        /// <summary>Removes every replicated ped. Called when the session ends or the script aborts.</summary>
        public void CleanUp()
        {
            foreach (Ped ped in _remotePeds.Values)
            {
                try
                {
                    if (ped.Exists())
                    {
                        ped.MarkAsNoLongerNeeded();
                        ped.Delete();
                    }
                }
                catch (Exception)
                {
                    // Best effort during teardown.
                }
            }

            _remotePeds.Clear();
            _driveState.Clear();
            _vehicles.CleanUp();
        }

        // ------------------------------------------------------------------
        private static MovementState SampleMovement(Ped ped)
        {
            if (ped.IsSprinting)
            {
                return MovementState.Sprint;
            }

            if (ped.IsRunning)
            {
                return MovementState.Run;
            }

            return ped.IsWalking ? MovementState.Walk : MovementState.Idle;
        }

        private static PlayerFlags SampleFlags(Ped ped)
        {
            PlayerFlags flags = PlayerFlags.None;

            if (ped.IsDucking)
            {
                flags |= PlayerFlags.Crouching;
            }

            if (ped.IsSprinting)
            {
                flags |= PlayerFlags.Sprinting;
            }

            if (ped.IsJumping)
            {
                flags |= PlayerFlags.Jumping;
            }

            if (ped.IsFalling)
            {
                flags |= PlayerFlags.Falling;
            }

            if (ped.IsSwimming)
            {
                flags |= PlayerFlags.Swimming;
            }

            if (ped.IsSwimmingUnderWater)
            {
                flags |= PlayerFlags.Diving;
            }

            if (ped.IsClimbing || ped.IsVaulting)
            {
                flags |= PlayerFlags.Climbing;
            }

            if (ped.IsRagdoll)
            {
                flags |= PlayerFlags.Ragdoll;
            }

            if (ped.IsDead)
            {
                flags |= PlayerFlags.Dead;
            }

            if (Game.Player.IsAiming)
            {
                flags |= PlayerFlags.Aiming;
            }

            if (ped.IsShooting)
            {
                flags |= PlayerFlags.Shooting;
            }

            if (ped.IsReloading)
            {
                flags |= PlayerFlags.Reloading;
            }

            if (ped.IsInVehicle())
            {
                flags |= PlayerFlags.InVehicle;
            }

            if (ped.IsGettingIntoVehicle)
            {
                flags |= PlayerFlags.EnteringVehicle;
            }

            if (ped.IsInCover)
            {
                flags |= PlayerFlags.InCover;
            }

            if (ped.IsInvincible)
            {
                flags |= PlayerFlags.Invincible;
            }

            return flags;
        }

        private static bool TryParseWeather(string name, out Weather weather)
        {
            switch (name)
            {
                case "EXTRASUNNY": weather = Weather.ExtraSunny; return true;
                case "CLEAR": weather = Weather.Clear; return true;
                case "CLOUDS": weather = Weather.Clouds; return true;
                case "SMOG": weather = Weather.Smog; return true;
                case "FOGGY": weather = Weather.Foggy; return true;
                case "OVERCAST": weather = Weather.Overcast; return true;
                case "RAIN": weather = Weather.Raining; return true;
                case "THUNDER": weather = Weather.ThunderStorm; return true;
                case "CLEARING": weather = Weather.Clearing; return true;
                case "NEUTRAL": weather = Weather.Neutral; return true;
                case "SNOW": weather = Weather.Snowing; return true;
                case "BLIZZARD": weather = Weather.Blizzard; return true;
                case "SNOWLIGHT": weather = Weather.Snowlight; return true;
                case "XMAS": weather = Weather.Christmas; return true;
                case "HALLOWEEN": weather = Weather.Halloween; return true;
                default: weather = Weather.Clear; return false;
            }
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        private static NetVector3 ToNet(Vector3 value) => new NetVector3(value.X, value.Y, value.Z);

        private static Vector3 ToGame(NetVector3 value) => new Vector3(value.X, value.Y, value.Z);

        /// <summary>Per-ped bookkeeping so tasks are issued on change rather than every frame.</summary>
        private sealed class PedDriveState
        {
            public bool Tasked;
            public NetVector3 TaskTarget;
            public float TaskBlend;
            /// <summary>Stealth movement is a mode, so it is written on change rather than every frame.</summary>
            public bool Crouching;

            /// <summary>Whether the reload task has already been issued for the reload in progress.</summary>
            public bool Reloading;

            /// <summary>The vehicle and seat this ped was last put into, so it is not re-seated every frame.</summary>
            public int SeatedVehicle;

            public sbyte SeatedIndex = -2;

            public bool Ragdolling;

            /// <summary>
            /// Frames since this ped started ragdolling. The first few are left to the
            /// local solver — see <see cref="RagdollDriver.SettleFrames"/>.
            /// </summary>
            public int RagdollFrames;
            public bool WasDead;

            /// <summary>Health and armour as last written from the server, so a hit can be measured as a drop from them.</summary>
            public int AppliedHealth = -1;

            public int AppliedArmor;

            /// <summary>
            /// The weapon last actually given to this ped, so the natives are called on
            /// a change and not on every frame. Unset until the first apply, which is
            /// why it is nullable rather than 0: 0 is unarmed, a real value that must be
            /// applied once like any other.
            /// </summary>
            public uint? AppliedWeapon;

            /// <summary>Tint and components last actually fitted, so the natives fire on a change.</summary>
            public byte AppliedTint;

            public uint AppliedComponentsWeapon;

            public List<uint> AppliedComponents { get; } = new List<uint>();

            public void Reset()
            {
                Tasked = false;
                TaskTarget = NetVector3.Zero;
                TaskBlend = -1f;
            }
        }
    }
}
