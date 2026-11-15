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
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Metadata;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Spectator
{
    /// <summary>
    /// Experimental: osu!lazer players watch osu!(stable) players who turned it on (the website option
    /// <c>tomyum_stable_lazer_spectate</c>). TomYum's <c>tomyum.stable-spectate</c> plugin keeps
    /// <see cref="PLAYERS_KEY"/> (the opted-in stable players online) and publishes their frame bundles on
    /// <see cref="FRAMES_CHANNEL"/>; this service
    /// <list type="bullet">
    /// <item>shows them in lazer's online list (an "osu!(stable)" activity while they play, so lazer offers Spectate),
    /// unless they're on lazer too;</item>
    /// <item>keeps <see cref="WATCHED_KEY"/>, the stable players someone here is watching (TomYum's bot then
    /// spectates them, so their client streams its frames);</item>
    /// <item>passes their frames to the watchers as lazer frames: the same replay frames, with the mods as lazer's
    /// (plus Classic) and the score so far.</item>
    /// </list>
    /// SpectatorHub (StartWatchingUser, EndWatchingUser, a connection's clean-up) and MetadataHub
    /// (BeginWatchingUserPresence) call in through the static methods.
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
        private static volatile Dictionary<int, StablePlayer> players = new Dictionary<int, StablePlayer>();
        private static Dictionary<int, UserPresence?> shownPresences = new Dictionary<int, UserPresence?>();

        private static IHubContext<SpectatorHub>? spectatorHub;
        private static IHubContext<MetadataHub>? metadataHub;
        private static EntityStore<MetadataClientState>? metadataStore;

        private readonly IConnectionMultiplexer redis;

        public TomYumStableSpectate(IConnectionMultiplexer redis, IHubContext<SpectatorHub> spectatorHub, IHubContext<MetadataHub> metadataHub,
                                    EntityStore<MetadataClientState> metadataStore)
        {
            this.redis = redis;
            TomYumStableSpectate.spectatorHub = spectatorHub;
            TomYumStableSpectate.metadataHub = metadataHub;
            TomYumStableSpectate.metadataStore = metadataStore;
        }

        /// <summary>SpectatorHub.StartWatchingUser: a lazer player starts watching <paramref name="userId"/>.</summary>
        public static async Task StartWatching(string connectionId, int userId)
        {
            try
            {
                if (!players.TryGetValue(userId, out var player))
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

        /// <summary>MetadataHub.BeginWatchingUserPresence: the opted-in stable players for the new watcher.</summary>
        public static async Task SendPresencesTo(string connectionId)
        {
            if (metadataHub == null)
                return;

            try
            {
                foreach (var (userId, presence) in shownPresences)
                {
                    if (presence != null)
                        await metadataHub.Clients.Client(connectionId).SendAsync(nameof(IMetadataClient.UserPresenceUpdated), userId, presence);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"TomYum stable spectate: sending presences failed: {e.Message}");
            }
        }

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

            await updatePresences(next);
        }

        private static async Task updatePresences(Dictionary<int, StablePlayer> next)
        {
            var shown = new Dictionary<int, UserPresence?>();

            foreach (var (userId, player) in next)
            {
                // On lazer as well: lazer's own presence is the one shown.
                if (metadataStore?.GetEntityUnsafe(userId)?.ToUserPresence() != null)
                    continue;

                shown[userId] = new UserPresence
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

            if (metadataHub != null)
            {
                foreach (var (userId, presence) in shown)
                {
                    if (!shownPresences.TryGetValue(userId, out var before) || !samePresence(before, presence))
                        await broadcastPresence(userId, presence);
                }

                foreach (int userId in shownPresences.Keys.Where(id => !shown.ContainsKey(id)))
                {
                    // Gone, or on lazer now (lazer's presence goes out from MetadataHub itself).
                    if (metadataStore?.GetEntityUnsafe(userId)?.ToUserPresence() == null)
                        await broadcastPresence(userId, null);
                }
            }

            shownPresences = shown;
        }

        private static Task broadcastPresence(int userId, UserPresence? presence) =>
            Task.WhenAll(
                metadataHub!.Clients.Group(MetadataHub.ONLINE_PRESENCE_WATCHERS_GROUP).SendAsync(nameof(IMetadataClient.UserPresenceUpdated), userId, presence),
                metadataHub.Clients.Group(MetadataHub.FRIEND_PRESENCE_WATCHERS_GROUP(userId)).SendAsync(nameof(IMetadataClient.FriendPresenceUpdated), userId, presence));

        private static bool samePresence(UserPresence? a, UserPresence? b)
        {
            if (a == null || b == null)
                return a == null && b == null;

            var x = a.Value.Activity as UserActivity.InSoloGame;
            var y = b.Value.Activity as UserActivity.InSoloGame;
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

        private static FrameHeader header(SpectatorState state, StableScore? score)
        {
            var statistics = new Dictionary<HitResult, int>();
            double accuracy = 1;

            if (score != null)
            {
                switch (state.RulesetID)
                {
                    case 1: // taiko
                        statistics[HitResult.Great] = score.Count300;
                        statistics[HitResult.Ok] = score.Count100;
                        statistics[HitResult.Miss] = score.CountMiss;
                        accuracy = ratio(score.Count300 + score.Count100 * 0.5, score.Count300 + score.Count100 + score.CountMiss);
                        break;

                    case 2: // catch
                        statistics[HitResult.Great] = score.Count300;
                        statistics[HitResult.LargeTickHit] = score.Count100;
                        statistics[HitResult.SmallTickHit] = score.Count50;
                        statistics[HitResult.SmallTickMiss] = score.CountKatu;
                        statistics[HitResult.Miss] = score.CountMiss;
                        accuracy = ratio(score.Count300 + score.Count100 + score.Count50,
                            score.Count300 + score.Count100 + score.Count50 + score.CountKatu + score.CountMiss);
                        break;

                    case 3: // mania
                        statistics[HitResult.Perfect] = score.CountGeki;
                        statistics[HitResult.Great] = score.Count300;
                        statistics[HitResult.Good] = score.CountKatu;
                        statistics[HitResult.Ok] = score.Count100;
                        statistics[HitResult.Meh] = score.Count50;
                        statistics[HitResult.Miss] = score.CountMiss;
                        accuracy = ratio((score.CountGeki + score.Count300) * 300 + score.CountKatu * 200 + score.Count100 * 100 + score.Count50 * 50,
                            (score.CountGeki + score.Count300 + score.CountKatu + score.Count100 + score.Count50 + score.CountMiss) * 300.0);
                        break;

                    default: // osu!
                        statistics[HitResult.Great] = score.Count300;
                        statistics[HitResult.Ok] = score.Count100;
                        statistics[HitResult.Meh] = score.Count50;
                        statistics[HitResult.Miss] = score.CountMiss;
                        accuracy = ratio(score.Count300 * 300 + score.Count100 * 100 + score.Count50 * 50,
                            (score.Count300 + score.Count100 + score.Count50 + score.CountMiss) * 300.0);
                        break;
                }
            }

            return new FrameHeader(
                score?.TotalScore ?? 0,
                accuracy,
                score?.Combo ?? 0,
                score?.MaxCombo ?? 0,
                statistics,
                new ScoreProcessorStatistics(),
                DateTimeOffset.Now,
                state.Mods.ToArray(),
                null,
                null);
        }

        private static double ratio(double hit, double total) => total > 0 ? hit / total : 1;

        /// <summary>One opted-in stable player in <see cref="PLAYERS_KEY"/> (the plugin's PlayerEntry).</summary>
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
