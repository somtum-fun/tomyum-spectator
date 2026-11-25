// Added by TomYum's build of osu-server-spectator (Dockerfile).
using System.Collections.Concurrent;
using System.Linq;
using osu.Game.Online.Spectator;

namespace osu.Server.Spectator.Hubs.Spectator
{
    /// <summary>
    /// Spectators only get a player's frames in time order. lazer's replay player throws on a frame earlier than the
    /// last one (Math.Clamp in FramedReplayInputHandler), and a couple of those close the game: one modified or buggy
    /// client sending them would close the game of everyone watching it. The score's own frames (scoreBuffer) are
    /// left as they came; only what is relayed to watchers is filtered. Reset at the start of each play, whose time
    /// starts again.
    /// </summary>
    public static class TomYumFrameOrder
    {
        private static readonly ConcurrentDictionary<int, double> last_time = new ConcurrentDictionary<int, double>();

        /// <summary>BeginPlaySession: a new play, from its own start.</summary>
        public static void Reset(int userId) => last_time.TryRemove(userId, out _);

        /// <summary>The bundle with only the frames not earlier than the last one relayed; null when none are left.</summary>
        public static FrameDataBundle? Filter(int userId, FrameDataBundle data)
        {
            double last = last_time.TryGetValue(userId, out double t) ? t : double.MinValue;
            if (data.Frames.All(f => f.Time >= last) && isOrdered(data))
            {
                if (data.Frames.Count > 0)
                    last_time[userId] = data.Frames[^1].Time;
                return data;
            }

            var kept = new System.Collections.Generic.List<osu.Game.Replays.Legacy.LegacyReplayFrame>(data.Frames.Count);
            foreach (var frame in data.Frames)
            {
                if (frame.Time < last)
                    continue;
                last = frame.Time;
                kept.Add(frame);
            }

            last_time[userId] = last;
            return kept.Count == 0 ? null : new FrameDataBundle(data.Header, kept);
        }

        private static bool isOrdered(FrameDataBundle data)
        {
            for (int i = 1; i < data.Frames.Count; i++)
            {
                if (data.Frames[i].Time < data.Frames[i - 1].Time)
                    return false;
            }

            return true;
        }
    }
}
