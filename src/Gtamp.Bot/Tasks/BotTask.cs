using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;

namespace Gtamp.Bot.Tasks
{
    public enum TaskResult
    {
        /// <summary>The task could not run: what it needed was not there.</summary>
        Skipped = 0,

        Passed = 1,

        Failed = 2,

        /// <summary>Ran, but the answer needs a person to judge it.</summary>
        Look = 3,
    }

    public readonly struct TaskVerdict
    {
        public TaskVerdict(TaskResult result, string detail)
        {
            Result = result;
            Detail = detail;
        }

        public TaskResult Result { get; }

        public string Detail { get; }

        public static TaskVerdict Pass(string detail) => new TaskVerdict(TaskResult.Passed, detail);

        public static TaskVerdict Fail(string detail) => new TaskVerdict(TaskResult.Failed, detail);

        public static TaskVerdict Skip(string detail) => new TaskVerdict(TaskResult.Skipped, detail);

        public static TaskVerdict Look(string detail) => new TaskVerdict(TaskResult.Look, detail);
    }

    /// <summary>Everything a task is allowed to touch.</summary>
    public sealed class BotContext
    {
        public BotContext(string name, BotBody body, SimulatedGameBridge bridge, MultiplayerClient client)
        {
            Name = name;
            Body = body;
            Bridge = bridge;
            Client = client;
        }

        public string Name { get; }

        public BotBody Body { get; }

        public SimulatedGameBridge Bridge { get; }

        public MultiplayerClient Client { get; }

        public Action<string, string> Say { get; set; } = (_, _) => { };

        public bool Connected => Client.LocalEntityId.IsValid;

        /// <summary>The nearest other player the server has told us about, or null.</summary>
        public RemotePlayer? NearestPlayer()
        {
            RemotePlayer? best = null;
            double bestDistance = double.MaxValue;

            foreach (RemotePlayer player in Client.RemotePlayers.Players)
            {
                PlayerEntity? state = player.Latest;
                if (state == null)
                {
                    continue;
                }

                double distance = SimulatedGameBridge.Distance(Body.Position, state.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }

            return best;
        }

        public NetVector3? NearestPlayerPosition()
        {
            RemotePlayer? player = NearestPlayer();
            return player?.Latest?.Position;
        }
    }

    /// <summary>
    /// One scripted thing the bot does, with a verdict at the end.
    /// <para>
    /// A task is a test, not a script: it says up front what it is checking, drives
    /// the body towards it, and then answers pass, fail, skipped or "a person needs
    /// to look". Skipped is a first-class answer — a task that needs another player
    /// and finds none has not failed, and reporting it as a failure would be the
    /// same lie as reporting an untested thing as working.
    /// </para>
    /// </summary>
    public abstract class BotTask
    {
        /// <summary>Name as typed on the command line.</summary>
        public abstract string Name { get; }

        /// <summary>What this checks, in one line, for the transcript.</summary>
        public abstract string Goal { get; }

        /// <summary>Seconds after which the task gives up and reports what it has.</summary>
        public virtual double TimeLimitSeconds => 20d;

        public virtual void Start(BotContext context)
        {
        }

        /// <summary>One simulation step. Returns true when the task is finished early.</summary>
        public abstract bool Update(BotContext context, double elapsed, double delta);

        public abstract TaskVerdict Finish(BotContext context, double elapsed);

        public static IReadOnlyList<BotTask> All() => new BotTask[]
        {
            new StandTask(),
            new PatrolTask(),
            new DriveTask(),
            new FollowTask(),
            new ShootTask(),
            new DieTask(),
            new ReconnectTask(),
        };

        public static bool TryResolve(string name, out BotTask task)
        {
            foreach (BotTask candidate in All())
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    task = candidate;
                    return true;
                }
            }

            task = null!;
            return false;
        }
    }
}
