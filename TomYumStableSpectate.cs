// Added by TomYum's build of osu-server-spectator (Dockerfile).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API;
using osu.Game.Online.Metadata;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Users;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Metadata;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Spectator
{
    /// <summary>
    /// Experimental: osu!lazer players watch osu!(stable) players. A TumThai client with "Show osu!(stable) players"
    /// on calls <c>TomYumWatchStablePlayers(true)</c> on MetadataHub; only those clients get the stable players
    /// (in the online list while they watch it, and their stable friends always), and only their users may
    /// spectate them. TomYum's <c>tomyum.stable-spectate</c> plugin keeps <see cref="PLAYERS_KEY"/> (the stable
    /// players online) and publishes the watched players' frame bundles on <see cref="FRAMES_CHANNEL"/>; this
    /// service keeps <see cref="WATCHED_KEY"/> (who is watched: TomYum's bot then spectates them, so their client
    /// streams its frames) and passes the frames to the watchers as lazer frames (the same replay frames, the mods
    /// as lazer's plus Classic, the score so far). A player also on lazer shows lazer's own presence.
    /// SpectatorHub (StartWatchingUser, EndWatchingUser, clean-up) and MetadataHub (TomYumWatchStablePlayers,
    /// Begin/EndWatchingUserPresence, clean-up) call in through the static methods.
    /// </summary>
    public sealed class TomYumStableSpectate : BackgroundService
    {
        public const string PLAYERS_KEY = "tomyum:stable-spectate:players";
        public const string WATCHED_KEY = "tomyum:stable-spectate:watched";
        public const string FRAMES_CHANNEL = "tomyum:stable-spectate:frames";

        private const string playing_verb = "Playing on osu!(stable) (experimental)";

        private static readonly TimeSpan interval = TimeSpan.FromSeconds(2);
        private static readonly JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> watchers = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
        private static readonly ConcurrentDictionary<int, SpectatorState> plays = new ConcurrentDictionary<int, SpectatorState>();
        private static readonly ConcurrentDictionary<string, Subscriber> subscribers = new ConcurrentDictionary<string, Subscriber>();
        private static readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
        private static volatile Dictionary<int, StablePlayer> players = new Dictionary<int, StablePlayer>();
        private static volatile Dictionary<int, UserPresence> shown = new Dictionary<int, UserPresence>();
        private static DateTimeOffset friendsReadAt = DateTimeOffset.MinValue;

        private static IHubContext<SpectatorHub>? spectatorHub;
        private static IHubContext<MetadataHub>? metadataHub;
        private static EntityStore<MetadataClientState>? metadataStore;

        private readonly IConnectionMultiplexer redis;
        private readonly IDatabaseFactory databaseFactory;

        public TomYumStableSpectate(IConnectionMultiplexer redis, IDatabaseFactory databaseFactory, IHubContext<SpectatorHub> spectatorHub,
                                    IHubContext<MetadataHub> metadataHub, EntityStore<MetadataClientState> metadataStore)
        {
            this.redis = redis;
            this.databaseFactory = databaseFactory;
            TomYumStableSpectate.spectatorHub = spectatorHub;
            TomYumStableSpectate.metadataHub = metadataHub;
            TomYumStableSpectate.metadataStore = metadataStore;
        }

        /// <summary>SpectatorHub.StartWatchingUser: a lazer player starts watching <paramref name="userId"/>.</summary>
        public static async Task StartWatching(string connectionId, int watcherUserId, int userId)
        {
            try
            {
                if (!players.TryGetValue(userId, out var player) || !subscribers.Values.Any(s => s.Enabled && s.UserId == watcherUserId))
                    return;

                watchers.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;

                // Mid-play: the watcher gets the play now, and its frames once the bot has joined.
                var state = plays.TryGetValue(userId, out var current) ? current : player.Playing ? begin(userId, player) : null;
                if (state != null && spectatorHub != null)
                    await spectatorHub.Clients.Client(connectionId).SendAsync(nameof(ISpectatorClient.UserBeganPlaying), userId, state);
            }
            catch (Exception e)
            {
                Console.WriteLine($"TomYum stable spectate: watching {userId} failed: {e.Message}");
            }
        }

        /// <summary>SpectatorHub.EndWatchingUser and a connection's clean-up.</summary>
        public static void EndWatching(string connectionId, int userId)
        {
            if (watchers.TryGetValue(userId, out var set) && set.TryRemove(connectionId, out _) && set.IsEmpty)
                watchers.TryRemove(userId, out _);
        }

        /// <summary>MetadataHub.TomYumWatchStablePlayers: a client turns "Show osu!(stable) players" on or off.</summary>
        public static async Task SetEnabled(string connectionId, int userId, bool enabled)
        {
            var subscriber = subscribers.GetOrAdd(connectionId, _ => new Subscriber(userId));
            subscriber.Enabled = enabled;
            if (enabled)
                subscriber.FriendsStale = true;
            await sendTo(connectionId, subscriber);
        }

        /// <summary>MetadataHub.BeginWatchingUserPresence / EndWatchingUserPresence.</summary>
        public static async Task SetWatchingAll(string connectionId, int userId, bool watching)
        {
            var subscriber = subscribers.GetOrAdd(connectionId, _ => new Subscriber(userId));
            subscriber.WatchingAll = watching;
            if (!watching)
                subscriber.SentAll.Clear(); // the client forgets the online list itself
            await sendTo(connectionId, subscriber);
        }

        /// <summary>MetadataHub's clean-up of a connection.</summary>
        public static void Forget(string connectionId) => subscribers.TryRemove(connectionId, out _);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(FRAMES_CHANNEL), (channel, message) => { _ = onFrames(message); });
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                Console.WriteLine($"TomYum stable spectate: subscribing failed: {e.Message}");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await tick();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"TomYum stable spectate: {e.GetType().Name}: {e.Message}");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }

        private async Task tick()
        {
            var db = redis.GetDatabase();

            var raw = await db.StringGetAsync(PLAYERS_KEY);
            var next = raw.IsNullOrEmpty
                ? new Dictionary<int, StablePlayer>()
                : (JsonSerializer.Deserialize<Dictionary<string, StablePlayer>>(raw.ToString(), json) ?? new Dictionary<string, StablePlayer>())
                  .Where(kv => int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                  .ToDictionary(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value);
            players = next;

            // Plays that ended without a last bundle (the bot left, the player stopped): finished for the watchers.
            foreach (int userId in plays.Keys)
            {
                if (!next.TryGetValue(userId, out var p) || !p.Playing)
                    await finish(userId, SpectatedUserState.Quit);
            }

            // Watchers of a player who's gone: nothing more to watch.
            foreach (int userId in watchers.Keys.Where(id => !next.ContainsKey(id)).ToList())
                watchers.TryRemove(userId, out _);

            int[] watched = watchers.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key).ToArray();
            await db.StringSetAsync(WATCHED_KEY, JsonSerializer.Serialize(watched), TimeSpan.FromSeconds(10));

            shown = presences(next);

            // Friends lists: read again every minute (and right after a client turns it on).
            bool readAll = DateTimeOffset.UtcNow - friendsReadAt > TimeSpan.FromMinutes(1);
            if (readAll)
                friendsReadAt = DateTimeOffset.UtcNow;
            foreach (var subscriber in subscribers.Values.Where(s => s.Enabled && (readAll || s.FriendsStale)))
            {
                using (var database = databaseFactory.GetInstance())
                    subscriber.Friends = new HashSet<int>(await database.GetUserFriendsAsync(subscriber.UserId));
                subscriber.FriendsStale = false;
            }

            foreach (var (connectionId, subscriber) in subscribers)
                await sendTo(connectionId, subscriber);
        }

        private static Dictionary<int, UserPresence> presences(Dictionary<int, StablePlayer> stablePlayers)
        {
            var result = new Dictionary<int, UserPresence>();

            foreach (var (userId, player) in stablePlayers)
            {
                // On lazer as well: lazer's own presence is the one shown.
                if (metadataStore?.GetEntityUnsafe(userId)?.ToUserPresence() != null)
                    continue;

                result[userId] = new UserPresence
                {
                    Status = UserStatus.Online,
                    Activity = player.Playing && player.BeatmapId > 0
                        ? new UserActivity.InSoloGame
                        {
                            BeatmapID = (int)player.BeatmapId,
                            BeatmapDisplayTitle = player.Info,
                            RulesetID = player.Mode,
                            RulesetPlayingVerb = playing_verb,
                        }
                        : null,
                };
            }

            return result;
        }

        /// <summary>Brings what a client was sent up to date: the online list (while it watches it) and its friends.</summary>
        private static async Task sendTo(string connectionId, Subscriber subscriber)
        {
            if (metadataHub == null)
                return;

            await sending.WaitAsync();

            try
            {
                var client = metadataHub.Clients.Client(connectionId);
                var current = shown;

                var wantAll = subscriber.Enabled && subscriber.WatchingAll ? current : new Dictionary<int, UserPresence>();
                foreach (var (userId, message) in diff(subscriber.SentAll, wantAll))
                    await client.SendAsync(nameof(IMetadataClient.UserPresenceUpdated), userId, message);

                var wantFriends = subscriber.Enabled
                    ? current.Where(kv => subscriber.Friends.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
                    : new Dictionary<int, UserPresence>();
                foreach (var (userId, message) in diff(subscriber.SentFriends, wantFriends))
                    await client.SendAsync(nameof(IMetadataClient.FriendPresenceUpdated), userId, message);
            }
            catch (Exception e)
            {
                Console.WriteLine($"TomYum stable spectate: sending presences failed: {e.Message}");
            }
            finally
            {
                sending.Release();
            }
        }

        /// <summary>The updates that turn <paramref name="sent"/> into <paramref name="want"/> (null: gone), applied to <paramref name="sent"/>.</summary>
        private static List<(int, UserPresence?)> diff(Dictionary<int, UserPresence> sent, Dictionary<int, UserPresence> want)
        {
            var updates = new List<(int, UserPresence?)>();

            foreach (var (userId, presence) in want)
            {
                if (!sent.TryGetValue(userId, out var before) || !samePresence(before, presence))
                {
                    updates.Add((userId, presence));
                    sent[userId] = presence;
                }
            }

            foreach (int userId in sent.Keys.Where(id => !want.ContainsKey(id)).ToList())
            {
                sent.Remove(userId);
                // Now on lazer: lazer's presence has gone out from MetadataHub itself, don't clear it.
                if (metadataStore?.GetEntityUnsafe(userId)?.ToUserPresence() == null)
                    updates.Add((userId, null));
            }

            return updates;
        }

        private static bool samePresence(UserPresence a, UserPresence b)
        {
            var x = a.Activity as UserActivity.InSoloGame;
            var y = b.Activity as UserActivity.InSoloGame;
            if (x == null || y == null)
                return x == null && y == null;

            return x.BeatmapID == y.BeatmapID && x.RulesetID == y.RulesetID && x.BeatmapDisplayTitle == y.BeatmapDisplayTitle;
        }

        private static async Task onFrames(RedisValue message)
        {
            try
            {
                var m = JsonSerializer.Deserialize<FramesMessage>(message.ToString(), json);
                if (m == null || m.UserId <= 0 || m.UserId > int.MaxValue || spectatorHub == null)
                    return;

                int userId = (int)m.UserId;
                if (!watchers.TryGetValue(userId, out var set) || set.IsEmpty)
                    return;

                var group = spectatorHub.Clients.Group(SpectatorHub.GetGroupId(userId));

                // A new song (or another map than the play we know): a new play for the watchers.
                if (m.Action == 1 || !plays.TryGetValue(userId, out var state) || state.BeatmapID != (int)m.BeatmapId)
                {
                    if (plays.ContainsKey(userId))
                        await finish(userId, SpectatedUserState.Quit);
                    if (m.BeatmapId <= 0 || m.Action is 3 or 4 or 7 or 8)
                        return;

                    state = begin(userId, new StablePlayer { BeatmapId = m.BeatmapId, Mode = m.Mode, Mods = m.Mods, Playing = true });
                    await group.SendAsync(nameof(ISpectatorClient.UserBeganPlaying), userId, state);
                }

                if (m.Frames.Length > 0)
                {
                    var frames = m.Frames
                                  .Where(f => f.Length >= 4)
                                  .Select(f => new LegacyReplayFrame(f[0], f[1], f[2], (ReplayButtonState)(int)f[3]))
                                  .ToList();
                    await group.SendAsync(nameof(ISpectatorClient.UserSentFrames), userId, new FrameDataBundle(header(state, m.Score), frames));
                }

                switch (m.Action)
                {
                    case 3:
                        await finish(userId, SpectatedUserState.Passed);
                        break;

                    case 4:
                        await finish(userId, SpectatedUserState.Failed);
                        break;

                    case 7:
                    case 8:
                        await finish(userId, SpectatedUserState.Quit);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"TomYum stable spectate: frames failed: {e.Message}");
            }
        }

        private static SpectatorState begin(int userId, StablePlayer player)
        {
            var ruleset = LegacyHelper.GetRulesetFromLegacyID(player.Mode);
            var mods = ruleset.ConvertFromLegacyMods((LegacyMods)player.Mods).ToList();
            // Stable's judgements and scoring.
            if (!mods.OfType<ModClassic>().Any() && ruleset.CreateAllMods().OfType<ModClassic>().FirstOrDefault() is { } classic)
                mods.Add(classic);

            var state = new SpectatorState
            {
                BeatmapID = (int)player.BeatmapId,
                RulesetID = player.Mode,
                Mods = mods.Select(mod => new APIMod(mod)).ToArray(),
                State = SpectatedUserState.Playing,
            };
            plays[userId] = state;
            return state;
        }

        private static async Task finish(int userId, SpectatedUserState how)
        {
            if (!plays.TryRemove(userId, out var state) || spectatorHub == null)
                return;

            state.State = how;
            await spectatorHub.Clients.Group(SpectatorHub.GetGroupId(userId)).SendAsync(nameof(ISpectatorClient.UserFinishedPlaying), userId, state);
        }

        private static readonly ConcurrentDictionary<int, ScoreProcessor> score_processors = new ConcurrentDictionary<int, ScoreProcessor>();

        /// <summary>
        /// The frame header for a stable score frame. Lazer recomputes the shown score and accuracy from
        /// <see cref="ScoreProcessorStatistics"/> (ScoreProcessor.ResetFromReplayFrame), so those are filled in from
        /// stable's counts: the accuracy part exactly (lazer's base score of each judgement and of its best result),
        /// the combo part estimated (stable sends counts, not each hit's combo: one run as long as the max combo,
        /// the other hits shared out between the misses).
        /// </summary>
        private static FrameHeader header(SpectatorState state, StableScore? score)
        {
            var statistics = new Dictionary<HitResult, int>();
            var processorStatistics = new ScoreProcessorStatistics();
            double accuracy = 1;

            if (score != null)
            {
                int rulesetId = state.RulesetID ?? 0;
                // (result, count, best result, counts towards combo)
                var counts = rulesetId switch
                {
                    1 => new[]
                    {
                        (HitResult.Great, score.Count300, HitResult.Great, true),
                        (HitResult.Ok, score.Count100, HitResult.Great, true),
                        (HitResult.Miss, score.CountMiss, HitResult.Great, true),
                    },
                    2 => new[]
                    {
                        (HitResult.Great, score.Count300, HitResult.Great, true),
                        (HitResult.LargeTickHit, score.Count100, HitResult.LargeTickHit, true),
                        (HitResult.SmallTickHit, score.Count50, HitResult.SmallTickHit, false),
                        (HitResult.SmallTickMiss, score.CountKatu, HitResult.SmallTickHit, false),
                        (HitResult.Miss, score.CountMiss, HitResult.Great, true),
                    },
                    3 => new[]
                    {
                        (HitResult.Perfect, score.CountGeki, HitResult.Perfect, true),
                        (HitResult.Great, score.Count300, HitResult.Perfect, true),
                        (HitResult.Good, score.CountKatu, HitResult.Perfect, true),
                        (HitResult.Ok, score.Count100, HitResult.Perfect, true),
                        (HitResult.Meh, score.Count50, HitResult.Perfect, true),
                        (HitResult.Miss, score.CountMiss, HitResult.Perfect, true),
                    },
                    _ => new[]
                    {
                        (HitResult.Great, score.Count300, HitResult.Great, true),
                        (HitResult.Ok, score.Count100, HitResult.Great, true),
                        (HitResult.Meh, score.Count50, HitResult.Great, true),
                        (HitResult.Miss, score.CountMiss, HitResult.Great, true),
                    },
                };

                var processor = score_processors.GetOrAdd(rulesetId, id => LegacyHelper.GetRulesetFromLegacyID(id).CreateScoreProcessor());
                int comboHits = 0;
                double comboBase = 0;

                foreach (var (result, count, best, combo) in counts)
                {
                    statistics[result] = count;
                    if (count <= 0)
                        continue;

                    if (best.AffectsAccuracy())
                    {
                        processorStatistics.MaximumBaseScore += (double)processor.GetBaseScoreForResult(best) * count;
                        processorStatistics.AccuracyJudgementCount += count;
                    }

                    if (result.AffectsAccuracy())
                        processorStatistics.BaseScore += (double)processor.GetBaseScoreForResult(result) * count;

                    if (combo && result.IsHit())
                    {
                        comboHits += count;
                        comboBase += (double)processor.GetBaseScoreForResult(best) * count;
                    }
                }

                accuracy = ratio(processorStatistics.BaseScore, processorStatistics.MaximumBaseScore);
                processorStatistics.ComboPortion = comboPortion(comboHits, comboHits > 0 ? comboBase / comboHits : 0, score.MaxCombo, score.CountMiss);
            }

            return new FrameHeader(
                score?.TotalScore ?? 0,
                accuracy,
                score?.Combo ?? 0,
                score?.MaxCombo ?? 0,
                statistics,
                processorStatistics,
                DateTimeOffset.Now,
                state.Mods.ToArray(),
                null,
                null);
        }

        /// <summary>
        /// Lazer's combo portion (base score × combo^0.5 for each hit) for <paramref name="hits"/> hits: one run of
        /// <paramref name="maxCombo"/>, the rest in equal runs between the misses.
        /// </summary>
        private static double comboPortion(int hits, double baseScore, int maxCombo, int misses)
        {
            static double run(int length)
            {
                double sum = 0;
                for (int c = 1; c <= length; c++)
                    sum += Math.Pow(c, ScoreProcessor.COMBO_EXPONENT);
                return sum;
            }

            int longest = Math.Clamp(maxCombo, 0, hits);
            int rest = hits - longest;
            int otherRuns = Math.Max(1, misses);
            double total = run(longest);
            if (rest > 0)
                total += otherRuns * run(rest / otherRuns) + run(rest % otherRuns);

            return total * baseScore;
        }

        private static double ratio(double hit, double total) => total > 0 ? hit / total : 1;

        /// <summary>A MetadataHub connection that asked for stable players (or watches the online list).</summary>
        private sealed class Subscriber
        {
            public readonly int UserId;
            public bool Enabled;
            public bool WatchingAll;
            public bool FriendsStale;
            public HashSet<int> Friends = new HashSet<int>();
            public readonly Dictionary<int, UserPresence> SentAll = new Dictionary<int, UserPresence>();
            public readonly Dictionary<int, UserPresence> SentFriends = new Dictionary<int, UserPresence>();

            public Subscriber(int userId)
            {
                UserId = userId;
            }
        }

        /// <summary>One stable player online in <see cref="PLAYERS_KEY"/> (the plugin's PlayerEntry).</summary>
        public sealed class StablePlayer
        {
            public string Username { get; set; } = string.Empty;
            public bool Playing { get; set; }
            public string Info { get; set; } = string.Empty;
            public long BeatmapId { get; set; }
            public int Mode { get; set; }
            public uint Mods { get; set; }
        }

        /// <summary>One message on <see cref="FRAMES_CHANNEL"/> (the plugin's FramesMessage).</summary>
        public sealed class FramesMessage
        {
            public long UserId { get; set; }
            public long BeatmapId { get; set; }
            public int Mode { get; set; }
            public uint Mods { get; set; }
            public byte Action { get; set; }
            public float[][] Frames { get; set; } = Array.Empty<float[]>();
            public StableScore? Score { get; set; }
        }

        /// <summary>The score frame's counts (the plugin's StableScore).</summary>
        public sealed class StableScore
        {
            public int Count300 { get; set; }
            public int Count100 { get; set; }
            public int Count50 { get; set; }
            public int CountGeki { get; set; }
            public int CountKatu { get; set; }
            public int CountMiss { get; set; }
            public long TotalScore { get; set; }
            public int MaxCombo { get; set; }
            public int Combo { get; set; }
        }
    }
}
