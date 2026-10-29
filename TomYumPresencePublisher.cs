// Added by TomYum's build of osu-server-spectator (docker/spectator/Dockerfile).
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using osu.Game.Users;
using osu.Server.Spectator.Entities;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Metadata
{
    /// <summary>
    /// Copies who's online on lazer, and what they're doing, into Redis every few seconds, so the
    /// game server (TomYum) can show lazer players next to stable ones (online lists, the website's
    /// online graph, the staff panel). The hash <c>tomyum:lazer-presence</c> maps user id → JSON; it
    /// expires if this server stops publishing. Users who appear offline are left out.
    /// </summary>
    public sealed class TomYumPresencePublisher : BackgroundService
    {
        public const string Key = "tomyum:lazer-presence";

        private static readonly TimeSpan interval = TimeSpan.FromSeconds(5);

        private readonly EntityStore<MetadataClientState> store;
        private readonly IConnectionMultiplexer redis;

        public TomYumPresencePublisher(EntityStore<MetadataClientState> store, IConnectionMultiplexer redis)
        {
            this.store = store;
            this.redis = redis;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var entries = store.GetAllEntities()
                                       .Select(kv => kv.Value)
                                       .Where(state => state.ToUserPresence() != null)
                                       .Select(state => new HashEntry(state.UserId, JsonSerializer.Serialize(new
                                       {
                                           status = state.UserStatus?.ToString(),
                                           activity = state.UserActivity?.GetType().Name,
                                           text = state.UserActivity?.GetStatus(),
                                           details = state.UserActivity?.GetDetails(),
                                           beatmap_id = state.UserActivity?.GetBeatmapID(),
                                           ruleset_id = (state.UserActivity as UserActivity.InGame)?.RulesetID,
                                           version_hash = state.VersionHash,
                                       })))
                                       .ToArray();

                    var transaction = redis.GetDatabase().CreateTransaction();
                    _ = transaction.KeyDeleteAsync(Key);
                    if (entries.Length > 0)
                    {
                        _ = transaction.HashSetAsync(Key, entries);
                        _ = transaction.KeyExpireAsync(Key, TimeSpan.FromSeconds(30));
                    }
                    await transaction.ExecuteAsync();
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    Console.WriteLine($"TomYum presence: publishing to Redis failed: {e.Message}");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
    }
}
