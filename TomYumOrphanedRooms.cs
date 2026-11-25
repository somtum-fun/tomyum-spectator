// Added by TomYum's build of osu-server-spectator (Dockerfile).
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    /// <summary>
    /// Closes the realtime rooms a previous run left open. A room lives only in this server's memory, so one still open
    /// in the database when the server starts is dead: nobody can join it, yet it keeps its host from making another
    /// ("number of simultaneously active rooms reached") and stays in the lounge. ppy's graceful shutdown waits for
    /// rooms to close; a container stop doesn't wait. One spectator server per database: with more, this would close
    /// the others' rooms. Runs before the hubs take connections (hosted services start before the web server).
    /// Closes them the way <c>DatabaseAccess.EndMatchAsync</c> does. Playlists rooms aren't realtime and are left alone.
    /// </summary>
    public sealed class TomYumOrphanedRooms : IHostedService
    {
        private readonly ILogger<TomYumOrphanedRooms> logger;

        public TomYumOrphanedRooms(ILogger<TomYumOrphanedRooms> logger)
        {
            this.logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            const string orphaned = "type <> 'playlists' AND (ends_at IS NULL OR ends_at > NOW())";

            try
            {
                await using var connection = new MySqlConnection(
                    $"Server={AppSettings.DatabaseHost};Port={AppSettings.DatabasePort};Database=osu;User ID={AppSettings.DatabaseUser};ConnectionTimeout=5");
                await connection.OpenAsync(cancellationToken);

                await connection.ExecuteAsync(
                    "UPDATE multiplayer_playlist_items p JOIN multiplayer_rooms r ON r.id = p.room_id"
                    + " SET p.expired = 1, p.played_at = NOW(), p.updated_at = NOW()"
                    + " WHERE r.type <> 'playlists' AND (r.ends_at IS NULL OR r.ends_at > NOW())"
                    + " AND p.expired = 0"
                    + " AND (SELECT COUNT(*) FROM multiplayer_score_links l WHERE l.playlist_item_id = p.id) = 0");
                await connection.ExecuteAsync(
                    "UPDATE multiplayer_rooms_high h JOIN multiplayer_rooms r ON r.id = h.room_id SET h.in_room = 0"
                    + " WHERE r.type <> 'playlists' AND (r.ends_at IS NULL OR r.ends_at > NOW()) AND h.in_room = 1");
                int closed = await connection.ExecuteAsync(
                    "UPDATE multiplayer_rooms r SET r.participant_count = (SELECT COUNT(*) FROM multiplayer_rooms_high h WHERE h.room_id = r.id), r.ends_at = NOW()"
                    + $" WHERE {orphaned}");

                if (closed > 0)
                    logger.LogInformation("Closed {Count} realtime room(s) left open by the last run", closed);
            }
            catch (MySqlException e)
            {
                // Not worth failing the start over: the rooms stay as they were.
                logger.LogWarning(e, "Couldn't close the realtime rooms left open by the last run");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
