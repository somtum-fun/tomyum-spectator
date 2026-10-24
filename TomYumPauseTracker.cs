// Added by TomYum's build of osu-server-spectator (docker/spectator/Dockerfile).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using osu.Game.Online.API;
using osu.Game.Online.Spectator;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Spectator
{
    /// <summary>
    /// Tells whether a lazer play was paused, for TomYum's osu!dan (its "no pausing" rule). Lazer sends no
    /// pause action: while paused it just stops sending frames. So each frame bundle's arrival time is
    /// compared with the game time of its last frame (divided by the play's rate): the difference, the
    /// "offset", stays put while the game plays, and a pause pushes it up for good by the pause's length
    /// (mania's resume countdown alone is 2 s). A lag spike only raises it until the late bundles catch up,
    /// so the settled offset (the lowest from each bundle on) is what's read. Skipping the intro lowers it.
    ///
    /// At the end of a play, <c>tomyum:lazer-play:&lt;score token&gt;</c> gets a JSON summary for a day:
    /// <c>pauses</c> (steps over <see cref="threshold_ms"/>), <c>paused_ms</c> (their total), <c>bundles</c>,
    /// <c>rate</c> (null when a mod changes it during the play: then the pause numbers can't be trusted).
    /// </summary>
    public sealed class TomYumPauseTracker : BackgroundService
    {
        public const string KEY_PREFIX = "tomyum:lazer-play:";

        private const double threshold_ms = 1000;
        private const int max_bundles = 50000;

        private static readonly TimeSpan keep = TimeSpan.FromDays(1);
        private static readonly TimeSpan forget_after = TimeSpan.FromHours(3);

        private static readonly ConcurrentDictionary<long, Play> plays = new ConcurrentDictionary<long, Play>();
        private static IConnectionMultiplexer? redis;

        public TomYumPauseTracker(IConnectionMultiplexer redis)
        {
            TomYumPauseTracker.redis = redis;
        }

        public static void Begin(long scoreToken, SpectatorState state)
        {
            try
            {
                plays[scoreToken] = new Play(rateOf(state.Mods));
            }
            catch
            {
                // never in the way of a play
            }
        }

        public static void Frames(long scoreToken, FrameDataBundle data)
        {
            try
            {
                if (!plays.TryGetValue(scoreToken, out var play) || data.Frames.Count == 0)
                    return;

                double gameTime = data.Frames[^1].Time;

                lock (play)
                {
                    if (play.Offsets.Count >= max_bundles)
                        return;

                    double wall = play.Clock.Elapsed.TotalMilliseconds;
                    play.Offsets.Add(wall - gameTime / (play.Rate ?? 1));
                }
            }
            catch
            {
            }
        }

        public static void End(long scoreToken)
        {
            try
            {
                if (!plays.TryRemove(scoreToken, out var play) || redis == null)
                    return;

                double[] offsets;
                lock (play)
                    offsets = play.Offsets.ToArray();

                // settled[k]: the lowest offset from bundle k on. It only goes up, by one step per pause.
                var settled = new double[offsets.Length];
                double low = double.MaxValue;
                for (int k = offsets.Length - 1; k >= 0; k--)
                    settled[k] = low = Math.Min(low, offsets[k]);

                int pauses = 0;
                double pausedMs = 0;
                double step = offsets.Length > 0 ? settled[0] : 0;
                for (int k = 1; k < settled.Length; k++)
                {
                    if (settled[k] - step > threshold_ms)
                    {
                        pauses++;
                        pausedMs += settled[k] - step;
                        step = settled[k];
                    }
                }

                string json = JsonSerializer.Serialize(new
                {
                    pauses,
                    paused_ms = (long)pausedMs,
                    bundles = offsets.Length,
                    rate = play.Rate,
                    started_at = play.StartedAt.ToUnixTimeSeconds(),
                    ended_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });

                _ = redis.GetDatabase().StringSetAsync(KEY_PREFIX + scoreToken.ToString(CultureInfo.InvariantCulture), json, keep);
            }
            catch
            {
            }
        }

        /// <summary>The play's fixed rate, or null when a mod changes it during the play.</summary>
        private static double? rateOf(IEnumerable<APIMod> mods)
        {
            double rate = 1;

            foreach (var mod in mods)
            {
                switch (mod.Acronym)
                {
                    case "DT":
                    case "NC":
                        rate *= setting(mod, "speed_change", 1.5);
                        break;

                    case "HT":
                    case "DC":
                        rate *= setting(mod, "speed_change", 0.75);
                        break;

                    case "WU":
                    case "WD":
                    case "AS":
                        return null;
                }
            }

            return rate > 0 ? rate : null;
        }

        private static double setting(APIMod mod, string name, double fallback)
        {
            if (!mod.Settings.TryGetValue(name, out object? value))
                return fallback;

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Forgets plays that never ended (the client quit or lost its connection).</summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                foreach (var (token, play) in plays.ToArray())
                {
                    if (DateTimeOffset.UtcNow - play.StartedAt > forget_after)
                        plays.TryRemove(token, out _);
                }
            }
        }

        private sealed class Play
        {
            public readonly double? Rate;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
            public readonly List<double> Offsets = new List<double>();

            public Play(double? rate)
            {
                Rate = rate;
            }
        }
    }
}
