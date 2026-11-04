// TomYum: custom rulesets (rulesets.info) in lazer multiplayer and spectating.
// ppy's server knows osu!'s four rulesets only (LegacyHelper, MAX_LEGACY_RULESET_ID). TomYum's website pins custom
// rulesets (osu-web table tomyum_custom_rulesets: id 10+, the DLL's download_url and sha256, enabled): this loads the
// pinned DLL, checked against its SHA-256, into its own load context sharing the server's osu! libraries, and gives
// its ruleset the website's id, so rooms, user styles, mods and saved replays work for it as for osu!'s own.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Dapper;
using MySqlConnector;
using osu.Game.Rulesets;

namespace osu.Server.Spectator
{
    public static class TomYumRulesets
    {
        private static readonly TimeSpan pinned_checked_every = TimeSpan.FromMinutes(1);
        private const int max_dll_bytes = 32 * 1024 * 1024;

        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly ConcurrentDictionary<string, Ruleset> by_sha256 = new ConcurrentDictionary<string, Ruleset>(StringComparer.OrdinalIgnoreCase);
        private static readonly object gate = new object();
        private static Pin[] pins = Array.Empty<Pin>();
        private static DateTime pinsAt = DateTime.MinValue;

        private sealed record Pin(int Id, string DownloadUrl, string Sha256);

        /// <summary>Whether <paramref name="id"/> is osu!'s or an enabled custom ruleset (no id: nothing to check).</summary>
        public static bool IsKnown(int? id) => id is null || id is >= 0 and <= 3 || pinFor(id.Value) != null;

        /// <summary>Custom ruleset <paramref name="id"/>, loaded (once per pinned DLL).</summary>
        public static Ruleset Get(int id)
        {
            var pin = pinFor(id) ?? throw new ArgumentException("Invalid ruleset ID provided.");
            var ruleset = by_sha256.GetOrAdd(pin.Sha256, _ => load(pin));
            ruleset.RulesetInfo.OnlineID = id;
            return ruleset;
        }

        private static Pin? pinFor(int id)
        {
            if (id < 10)
                return null;

            lock (gate)
            {
                if (DateTime.UtcNow - pinsAt > pinned_checked_every)
                {
                    try
                    {
                        using var connection = new MySqlConnection(
                            $"Server={AppSettings.DatabaseHost};Port={AppSettings.DatabasePort};Database=osu;User ID={AppSettings.DatabaseUser};"
                            + $"Password={Environment.GetEnvironmentVariable("DB_PASS")};ConnectionTimeout=5");
                        pins = connection.Query<(int id, string download_url, string sha256)>(
                                             "SELECT id, download_url, sha256 FROM tomyum_custom_rulesets WHERE enabled = 1")
                                         .Select(r => new Pin(r.id, r.download_url, r.sha256)).ToArray();
                    }
                    catch (Exception ex) when (ex is MySqlException or InvalidOperationException)
                    {
                        // No table (a server without TomYum's website), or the database is away: what was known stays.
                        Console.Error.WriteLine($"TomYum: couldn't read the custom rulesets: {ex.Message}");
                    }

                    pinsAt = DateTime.UtcNow;
                }

                return pins.FirstOrDefault(p => p.Id == id);
            }
        }

        private static Ruleset load(Pin pin)
        {
            byte[] dll = http.GetByteArrayAsync(pin.DownloadUrl).GetAwaiter().GetResult();
            if (dll.Length > max_dll_bytes || !Convert.ToHexString(SHA256.HashData(dll)).Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Custom ruleset {pin.Id}: the DLL isn't the one pinned.");

            return Load(dll, pin.Id);
        }

        /// <summary>The ruleset in <paramref name="dll"/>, as custom ruleset <paramref name="id"/> (public for checks).</summary>
        public static Ruleset Load(byte[] dll, int id)
        {
            var assembly = new RulesetLoadContext(id).LoadFromStream(new MemoryStream(dll, false));
            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            var rulesetType = types.First(t => t.IsSubclassOf(typeof(Ruleset)) && !t.IsAbstract);
            Console.WriteLine($"TomYum: custom ruleset {id} is {rulesetType.FullName}.");
            var ruleset = (Ruleset)Activator.CreateInstance(rulesetType)!;
            ruleset.RulesetInfo.OnlineID = id;
            return ruleset;
        }

        // A ruleset on its own, with osu!'s libraries (and everything else) the server's.
        private sealed class RulesetLoadContext : AssemblyLoadContext
        {
            public RulesetLoadContext(int id)
                : base($"tomyum-ruleset-{id}")
            {
            }

            protected override Assembly? Load(AssemblyName assemblyName) =>
                Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
        }
    }
}
