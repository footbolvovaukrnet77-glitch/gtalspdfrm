using System;
using System.Globalization;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Bot.Tasks
{
    /// <summary>
    /// Stand still and be replicated. The simplest thing that can be wrong, and the
    /// one the byte-budget defect broke: a player who stops moving still has to keep
    /// existing for everybody else.
    /// </summary>
    public sealed class StandTask : BotTask
    {
        private int _correctionsAtStart;

        public override string Name => "stand";

        public override string Goal => "стоять на месте и оставаться видимым (T9)";

        public override double TimeLimitSeconds => 12d;

        public override void Start(BotContext context)
        {
            _correctionsAtStart = context.Bridge.Seen.CorrectionsApplied;
            context.Body.Stop();
        }

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            context.Body.Stop();
            return false;
        }

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            int corrections = context.Bridge.Seen.CorrectionsApplied - _correctionsAtStart;
            if (corrections > 0)
            {
                return TaskVerdict.Fail(
                    $"сервер {corrections} раз(а) двигал бота, который стоял на месте — " +
                    "стоящий игрок расходиться с сервером не может");
            }

            return TaskVerdict.Pass($"{elapsed:F0} с на месте, ни одной коррекции");
        }
    }

    /// <summary>
    /// Walk a square, changing gait. Exercises the movement budget in the anti-cheat
    /// and the posture flags, both of which a standing bot never touches.
    /// </summary>
    public sealed class PatrolTask : BotTask
    {
        private const float Side = 40f;
        private NetVector3 _origin;
        private int _corner;
        private int _correctionsAtStart;

        public override string Name => "patrol";

        public override string Goal => "ходить квадратом, меняя походку (T9)";

        public override double TimeLimitSeconds => 40d;

        public override void Start(BotContext context)
        {
            _origin = context.Body.Position;
            _corner = 0;
            _correctionsAtStart = context.Bridge.Seen.CorrectionsApplied;
        }

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            NetVector3 target = Corner(_corner);
            context.Body.Movement = (_corner % 3) switch
            {
                0 => MovementState.Walk,
                1 => MovementState.Run,
                _ => MovementState.Sprint,
            };

            context.Body.Flags = _corner % 4 == 3 ? PlayerFlags.Crouching : PlayerFlags.None;

            float speed = context.Body.Movement switch
            {
                MovementState.Walk => 1.4f,
                MovementState.Run => 3.5f,
                _ => 7.0f,
            };

            if (context.Body.MoveTowards(target, speed, delta))
            {
                _corner++;
                if (_corner >= 8)
                {
                    return true;
                }
            }

            return false;
        }

        private NetVector3 Corner(int index) => (index % 4) switch
        {
            0 => new NetVector3(_origin.X + Side, _origin.Y, _origin.Z),
            1 => new NetVector3(_origin.X + Side, _origin.Y + Side, _origin.Z),
            2 => new NetVector3(_origin.X, _origin.Y + Side, _origin.Z),
            _ => _origin,
        };

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            int corrections = context.Bridge.Seen.CorrectionsApplied - _correctionsAtStart;
            context.Body.Flags = PlayerFlags.None;
            context.Body.Stop();

            if (corrections > 2)
            {
                return TaskVerdict.Fail(
                    $"{corrections} коррекций за обход — сервер не принимает движение бота, " +
                    $"последняя на {context.Bridge.Seen.LastCorrectionDistance:F1} м");
            }

            return TaskVerdict.Pass(
                $"{_corner} углов пройдено, коррекций {corrections}");
        }
    }

    /// <summary>
    /// Claim a vehicle and drive it. This is the path that asks the server to adopt a
    /// client-created entity, then streams it — the ownership handoff and the
    /// movement budget at vehicle speed in one task.
    /// </summary>
    public sealed class DriveTask : BotTask
    {
        /// <summary>Sultan. Any hash the server will accept; the bot has no streamer to satisfy.</summary>
        private const uint VehicleModel = 0x39DA2754u;

        private NetVector3 _origin;
        private int _leg;
        private int _correctionsAtStart;

        public override string Name => "drive";

        public override string Goal => "взять машину и проехать маршрут (T2, T6, T11)";

        public override double TimeLimitSeconds => 45d;

        public override void Start(BotContext context)
        {
            _origin = context.Body.Position;
            _leg = 0;
            _correctionsAtStart = context.Bridge.Seen.CorrectionsApplied;

            // A handle the bridge will answer TryReadVehicle for is all it takes: the
            // owned-entity streamer then asks the server to adopt it, exactly as it
            // does when a player gets into a car.
            context.Body.VehicleHandle = 4242;
            context.Body.VehicleModel = VehicleModel;
            context.Say(context.Name, "сел в машину, прошу сервер её принять");
        }

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            context.Body.Movement = MovementState.Idle;
            NetVector3 target = Leg(_leg);
            if (context.Body.MoveTowards(target, 22f, delta))
            {
                _leg++;
                if (_leg >= 4)
                {
                    return true;
                }
            }

            return false;
        }

        private NetVector3 Leg(int index) => (index % 4) switch
        {
            0 => new NetVector3(_origin.X + 200f, _origin.Y, _origin.Z),
            1 => new NetVector3(_origin.X + 200f, _origin.Y + 200f, _origin.Z),
            2 => new NetVector3(_origin.X, _origin.Y + 200f, _origin.Z),
            _ => _origin,
        };

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            int owned = context.Client.OwnedEntities.OwnedCount;
            int corrections = context.Bridge.Seen.CorrectionsApplied - _correctionsAtStart;
            context.Body.VehicleHandle = 0;
            context.Body.VehicleModel = 0;
            context.Body.Stop();

            if (owned == 0)
            {
                return TaskVerdict.Fail(
                    "сервер не принял машину бота — в игре это «машина есть только у меня»");
            }

            if (corrections > 2)
            {
                return TaskVerdict.Fail(
                    $"машину приняли, но {corrections} коррекций за поездку — " +
                    "на скорости сервер отклоняет позицию");
            }

            return TaskVerdict.Pass($"машина принята сервером, {_leg} отрезков, коррекций {corrections}");
        }
    }

    /// <summary>
    /// Follow the nearest real player around. Written for the person testing: it puts
    /// a second body next to them that they can watch, walk away from and come back
    /// to, without needing a second pair of hands.
    /// </summary>
    public sealed class FollowTask : BotTask
    {
        private const float StandOff = 6f;
        private int _framesWithTarget;
        private int _lostCount;
        private bool _hadTarget;

        public override string Name => "follow";

        public override string Goal => "идти за живым игроком и не терять его (T9)";

        public override double TimeLimitSeconds => 60d;

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            NetVector3? target = context.NearestPlayerPosition();
            if (target == null)
            {
                if (_hadTarget)
                {
                    _lostCount++;
                    _hadTarget = false;
                }

                context.Body.Stop();
                return false;
            }

            if (!_hadTarget)
            {
                _hadTarget = true;
            }

            _framesWithTarget++;
            double distance = SimulatedGameBridge.Distance(context.Body.Position, target.Value);
            context.Body.AimPosition = target.Value;

            if (distance > StandOff)
            {
                context.Body.Movement = distance > 30d ? MovementState.Sprint : MovementState.Run;
                context.Body.MoveTowards(target.Value, distance > 30d ? 7f : 3.5f, delta);
            }
            else
            {
                context.Body.Stop();
            }

            return false;
        }

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            context.Body.Stop();

            if (_framesWithTarget == 0)
            {
                return TaskVerdict.Skip("рядом не было живого игрока — подключитесь к тому же серверу");
            }

            if (_lostCount > 0)
            {
                return TaskVerdict.Fail(
                    $"игрок пропадал из мира бота {_lostCount} раз(а) — это и есть despawn, " +
                    "который чинился в b9dea81");
            }

            return TaskVerdict.Pass($"игрок был виден непрерывно, {_framesWithTarget} кадров");
        }
    }

    /// <summary>
    /// Fire at the nearest player and claim the hits. Everything here is a claim the
    /// server arbitrates, which is exactly why it has never been tested: it needs
    /// two connections.
    /// </summary>
    public sealed class ShootTask : BotTask
    {
        private const int Rounds = 12;
        private double _nextShot;
        private int _fired;
        private int _shotsSeenAtStart;
        private int _healthAtStart;

        public override string Name => "shoot";

        public override string Goal => "стрелять в живого игрока и заявлять попадания (T10)";

        public override double TimeLimitSeconds => 25d;

        public override void Start(BotContext context)
        {
            _nextShot = 1d;
            _fired = 0;
            _shotsSeenAtStart = context.Bridge.Seen.ShotsDrawn;
            _healthAtStart = context.Body.Health;
            context.Body.Flags = PlayerFlags.Aiming;
        }

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            RemotePlayer? target = context.NearestPlayer();
            PlayerEntity? state = target?.Latest;
            if (target == null || state == null)
            {
                return false;
            }

            context.Body.AimPosition = state.Position;
            if (elapsed < _nextShot)
            {
                return false;
            }

            _nextShot = elapsed + 0.4d;
            context.Body.Fire(state.Position);
            _fired++;

            // A hit is a claim about a ped handle the client drew, so it can only be
            // made once the client has actually built that player's ped.
            if (target.PedHandle != 0)
            {
                context.Body.PendingHits.Add(new LocalHitSample
                {
                    PedHandle = target.PedHandle,
                    WeaponHash = context.Body.WeaponHash,
                    Damage = 12,
                    HitPosition = state.Position,
                    HitBone = -1,
                    IsMelee = false,
                });
            }

            return _fired >= Rounds;
        }

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            context.Body.Flags = PlayerFlags.None;

            if (_fired == 0)
            {
                return TaskVerdict.Skip("не в кого было стрелять — нужен живой игрок рядом");
            }

            int seen = context.Bridge.Seen.ShotsDrawn - _shotsSeenAtStart;
            int lost = _healthAtStart - context.Body.Health;

            // Being shot is the half a single bot can prove. Our own rounds are only
            // a claim until the server rules on them, and the ruling arrives as the
            // *other* player's health changing — which we cannot see. So the verdict
            // is about the damage that reached us: if another shooter's rounds moved
            // our health, the server arbitrated a hit, and the whole path worked.
            if (seen == 0)
            {
                return TaskVerdict.Look(
                    $"выпущено {_fired} патрон(ов), но в бота никто не стрелял — " +
                    "проверьте у себя, падает ли здоровье");
            }

            if (lost <= 0)
            {
                return TaskVerdict.Fail(
                    $"в бота стреляли ({seen} выстрел(ов) видел), но здоровье не изменилось: " +
                    "сервер не применил ни одного попадания");
            }

            return TaskVerdict.Pass(
                $"выпущено {_fired}, чужих выстрелов видел {seen}, " +
                $"здоровье упало на {lost} — сервер попадания применяет");
        }
    }

    /// <summary>
    /// Be killed by another player and let the server put us back.
    /// <para>
    /// The first version of this task set its own health to zero and called the
    /// server's reply a respawn. It was not: the server is authoritative about
    /// health, so it simply corrected the lie back to what it held, and the task
    /// reported a respawn that never happened. Worse, the claimed zero was recorded,
    /// and the next reconnect restored a player with no health — a false alarm the
    /// bot then reported as a server defect.
    /// </para>
    /// <para>
    /// A client cannot kill itself, and that is correct. So this waits to be shot:
    /// the only death worth testing is one the server ruled on.
    /// </para>
    /// </summary>
    public sealed class DieTask : BotTask
    {
        private NetVector3 _diedAt;
        private bool _sawDeath;
        private double _diedAtTime;
        private double _nextShot;
        private int _lowestHealth = int.MaxValue;

        public override string Name => "die";

        public override string Goal => "быть убитым и дождаться респавна от сервера (T12)";

        public override double TimeLimitSeconds => 45d;

        public override void Start(BotContext context)
        {
            _nextShot = 0.5d;
            _sawDeath = false;
            _lowestHealth = context.Body.Health;
            context.Body.Flags = PlayerFlags.Aiming;
        }

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            _lowestHealth = Math.Min(_lowestHealth, context.Body.Health);

            if (!_sawDeath && context.Body.Health <= 0)
            {
                _sawDeath = true;
                _diedAt = context.Body.Position;
                _diedAtTime = elapsed;
                context.Body.Flags = PlayerFlags.Dead;
                context.Body.Stop();
                context.Say(context.Name, "сервер засчитал смерть, жду респавна");
                return false;
            }

            if (_sawDeath)
            {
                // Only the server can end this: our own health is a report, and the
                // number that matters is the one it corrects us with.
                if (context.Body.Health > 0)
                {
                    context.Body.Flags = PlayerFlags.None;
                    return true;
                }

                return false;
            }

            // Shoot back, so two bots running this together finish it for each other.
            RemotePlayer? target = context.NearestPlayer();
            PlayerEntity? state = target?.Latest;
            if (target == null || state == null)
            {
                return false;
            }

            context.Body.AimPosition = state.Position;
            if (elapsed < _nextShot)
            {
                return false;
            }

            _nextShot = elapsed + 0.25d;
            context.Body.Fire(state.Position);
            if (target.PedHandle != 0)
            {
                context.Body.PendingHits.Add(new LocalHitSample
                {
                    PedHandle = target.PedHandle,
                    WeaponHash = context.Body.WeaponHash,
                    Damage = 30,
                    HitPosition = state.Position,
                    HitBone = -1,
                    IsMelee = false,
                });
            }

            return false;
        }

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            context.Body.Flags = PlayerFlags.None;

            if (!_sawDeath)
            {
                return context.NearestPlayer() == null
                    ? TaskVerdict.Skip("некому было застрелить бота — нужен второй игрок или второй бот")
                    : TaskVerdict.Look(
                        $"за {elapsed:F0} с сервер не довёл бота до смерти, здоровье падало до {_lowestHealth}");
            }

            if (context.Body.Health <= 0)
            {
                return TaskVerdict.Fail(
                    $"сервер засчитал смерть на {_diedAtTime:F1} с, но за оставшиеся " +
                    $"{elapsed - _diedAtTime:F0} с не воскресил — игрок остался бы лежать");
            }

            double moved = SimulatedGameBridge.Distance(_diedAt, context.Body.Position);
            return TaskVerdict.Pass(
                $"смерть засчитана сервером, респавн через {elapsed - _diedAtTime:F1} с, " +
                $"здоровье {context.Body.Health}, перенесло на {moved:F0} м");
        }
    }

    /// <summary>
    /// Leave and come back. The point is not the reconnection but what the server
    /// remembers: identity, position, health, and the vehicles it was holding.
    /// </summary>
    public sealed class ReconnectTask : BotTask
    {
        private NetVector3 _leftAt;
        private int _healthAtLeaving;
        private bool _left;
        private double _reconnectAt;

        public override string Name => "reconnect";

        public override string Goal => "переподключиться и проверить, что сервер помнит (T12)";

        public override double TimeLimitSeconds => 30d;

        public override void Start(BotContext context)
        {
            _leftAt = context.Body.Position;
            _healthAtLeaving = context.Body.Health;
            _left = false;
        }

        public override bool Update(BotContext context, double elapsed, double delta)
        {
            if (!_left)
            {
                context.Client.Disconnect("bot reconnect test");
                _left = true;
                _reconnectAt = elapsed + 3d;
                context.Say(context.Name, "отключился, вернусь через 3 с");
                return false;
            }

            if (elapsed >= _reconnectAt && !context.Connected)
            {
                context.Say(context.Name, "переподключаюсь");
                context.Client.Connect(context.Client.Config.ServerAddress, context.Client.Config.ServerPort);
                _reconnectAt = elapsed + 10d;
                return false;
            }

            return context.Connected && elapsed > 4d;
        }

        public override TaskVerdict Finish(BotContext context, double elapsed)
        {
            if (!context.Connected)
            {
                return TaskVerdict.Fail("бот не смог вернуться на сервер за отведённое время");
            }

            double moved = SimulatedGameBridge.Distance(_leftAt, context.Body.Position);
            int health = context.Body.Health;
            string state = $"позиция: {moved:F0} м от места выхода, здоровье было {_healthAtLeaving}, стало {health}";

            if (moved >= 25d)
            {
                return TaskVerdict.Fail("вернулся не туда — " + state);
            }

            // Health is checked too. The first version reported only position and
            // passed a reconnect that brought the bot back with nothing left.
            if (health <= 0 && _healthAtLeaving > 0)
            {
                return TaskVerdict.Fail("вернулся без здоровья — " + state);
            }

            return TaskVerdict.Pass(state);
        }
    }
}
