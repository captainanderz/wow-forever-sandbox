// Bounded metadata only. Never pass request bodies, credentials, tickets or keys here.
using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AuthServer;

internal static class LocalAuthDiagnostics
{
    private static readonly object gate = new();
    private static string path;
    private static int limit;
    private static int count;
    private static int connectionCount;
    public static int NewConnection() => Interlocked.Increment(ref connectionCount);

    public static void Configure(string outputPath, int eventLimit)
    {
        path = outputPath;
        limit = eventLimit;
        if (limit > 0) File.WriteAllText(path, "");
    }

    public static void Emit(string kind, object metadata)
    {
        lock (gate)
        {
            if (limit == 0 || count >= limit) return;
            var record = new { sequence = ++count, utc = DateTimeOffset.UtcNow, kind, metadata };
            try { File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine); }
            catch (IOException) { limit = 0; }
            catch (UnauthorizedAccessException) { limit = 0; }
        }
    }
}
