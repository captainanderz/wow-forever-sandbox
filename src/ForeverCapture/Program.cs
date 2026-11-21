// ForeverCapture.exe: records the game objects the live WoW Forever server sends to your own game.
//   ForeverCapture record [--wow <WoW folder>] [--out <folder>] [--relay-only] [--all-ports] [--map <site folder>]
//                                                                                start the relay, the recording and the game;
//                                                                                --map also opens the live map window
//   ForeverCapture map --site <site folder> [--wow <WoW folder>] [--no-window]   the map window alone: every capture so far
//   ForeverCapture decode <capture folder>                                       read a capture again (after a decoder fix)
//   ForeverCapture stop                                                          stop a packet-monitor recording left running
// The map window (LocalMap.cs) serves the WoW Atlas site's own pages read-only from its folder, with the captures drawn on
// top, on this PC only: nothing of the captures goes into the site's folder, its exports or its uploads.
// How: the game logs in through a local relay (LoginRelay.cs), Windows' packet monitor records the world connections
// (NetCapture.cs), and the decoder (Decode.cs) turns them into gameobjects.json. The game process is never opened or changed.
// A capture folder holds the session's keys and its whole decrypted-able traffic (chat included): keep it on this PC.
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverCapture;

internal static class Program
{
    private const string ProgramFolder = "_forever_", GameExe = "Wow.exe", ConfigName = "ForeverCapture.wtf", Product = "wowf";
    private const int RelayPort = 8010;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IncludeFields = true };

    private static int Main(string[] args)
    {
        Console.Title = "Forever Capture";
        try
        {
            switch (args.FirstOrDefault() ?? "record")
            {
                case "record": return Record(args.Skip(1).ToArray());
                case "decode" when args.Length == 2: DecodeFolder(Path.GetFullPath(args[1])); return 0;
                case "check": return CheckSetup(args.Skip(1).ToArray());
                case "map": return MapWindow(args.Skip(1).ToArray());
                case "stop": PacketMonitor.StopOnly(Path.GetTempPath()); Console.WriteLine("Packet monitor stopped."); return 0;
                default: Console.WriteLine("ForeverCapture [record [--wow <WoW folder>] [--out <folder>] [--relay-only] [--all-ports] [--map <site folder>] | map --site <site folder> [--wow <WoW folder>] [--no-window] | check [--wow <WoW folder>] | decode <capture folder> | stop]"); return 2;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException or JsonException
            or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("ERROR: " + error.Message); Console.ResetColor();
            return 1;
        }
    }

    private static string Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Running(string exe) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Any(p =>
    {
        try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch (System.ComponentModel.Win32Exception) { return false; }
    });

    private static int Record(string[] args)
    {
        var wow = Path.GetFullPath(Option(args, "--wow") ?? @"C:\Blizzard Games\World of Warcraft");
        var program = Path.Combine(wow, ProgramFolder);
        var exe = Path.Combine(program, GameExe);
        if (!File.Exists(exe)) throw new IOException($"No WoW Forever in {wow} (expected {ProgramFolder}\\{GameExe}). Pass --wow <WoW folder>.");
        if (Running(exe)) throw new InvalidOperationException("Close WoW Forever first: the relay has to be in place before the game logs in.");
        var relayOnly = args.Contains("--relay-only");
        var site = Option(args, "--map");
        var livePath = Path.Combine(CaptureRoot, "live.json");
        var folder = Path.GetFullPath(Option(args, "--out") ?? Path.Combine(CaptureRoot, DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")));
        Directory.CreateDirectory(folder);

        var (host, port) = Upstream(Path.Combine(program, "WTF", "Config.wtf"));
        using var certificate = LoopbackCertificate();
        var session = new Session { StartedUtc = DateTime.UtcNow, Client = ClientVersion(wow), Upstream = $"{host}:{port}" };
        var sessionPath = Path.Combine(folder, "session.json");
        void Save() { lock (session) File.WriteAllText(sessionPath, JsonSerializer.Serialize(session, Indented)); }
        Save();

        // relay.log: what the relay saw and when (connection events and request names, never contents).
        var relayLog = Path.Combine(folder, "relay.log");
        void Log(string line) { lock (session) File.AppendAllText(relayLog, $"{DateTime.UtcNow:HH:mm:ss.fff} {line}\n"); Console.WriteLine(line); }
        using var relay = new LoginRelay(certificate, host, port, RelayPort, Log,
            secret =>
            {
                var hex = Convert.ToHexString(secret);
                lock (session) { if (session.GameSecrets.Contains(hex)) return; session.GameSecrets.Add(hex); }
                Save();
                Log($"Game secret seen ({session.GameSecrets.Count}).");
            },
            (secret, servers) =>
            {
                lock (session) session.Joins.Add(new SessionJoin { Utc = DateTime.UtcNow, JoinSecret = Convert.ToHexString(secret), Servers = servers });
                Save();
                Log($"Realm join seen ({session.Joins.Count}): world server {string.Join(", ", servers)}. Keys saved.");
            });
        relay.Start();
        Log($"Relay listening on 127.0.0.1:{RelayPort}, passing the login on to {host}:{port}.");

        var recording = false;
        Process game = null;
        LiveCapture capture = null;
        Timer liveTimer = null;
        LocalMap window = null;
        var streams = new WorldCapture.TcpStreams();
        try
        {
            if (!relayOnly)
            {
                var allPorts = args.Contains("--all-ports");
                capture = LiveCapture.TryStart(Path.Combine(folder, "net.pcapng"), allPorts ? "tcp" : "tcp port " + PacketMonitor.WorldPort, streams.Add, out var reason);
                if (capture != null)
                {
                    Console.WriteLine($"Recording the world port with Npcap on {capture.AdapterCount} network adapter{(capture.AdapterCount == 1 ? "" : "s")}.");
                    {
                        WriteLive(livePath, session, new(), new(), true);
                        var busy = 0;
                        // shortcut: each tick decodes the whole session again (a second or less per hour played); make the decoder
                        // incremental if long sessions make the live map lag.
                        liveTimer = new Timer(_ =>
                        {
                            if (Interlocked.Exchange(ref busy, 1) == 1) return;
                            try
                            {
                                var (objects, track, _) = WorldDecoder.Decode(streams.Connections(), KeyData(session));
                                WriteLive(livePath, session, objects, track, true);
                                // The 3D view reads objects from the capture folders: this session's are there as they come.
                                if (objects.Count > 0) WriteObjects(folder, session, objects);
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }   // the next tick tries again
                            finally { busy = 0; }
                        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
                    }
                }
                else
                {
                    if (File.Exists(livePath)) File.Delete(livePath);
                    Console.WriteLine($"{reason}: recording with Windows' packet monitor instead, without the live position. Windows asks for administrator rights once now and once when the game closes.");
                    PacketMonitor.Start(folder, allPorts);
                    recording = true;
                }
            }
            if (site != null)
            {
                window = new LocalMap(Path.GetFullPath(site), CaptureRoot, wow, session.Client);
                window.OpenWindow(live: capture != null);
                Console.WriteLine("Map window: " + window.Url);
            }
            // The game's own config, with only the login address changed; the game saves its settings back into this copy.
            WriteConfig(Path.Combine(program, "WTF", "Config.wtf"), Path.Combine(program, "WTF", ConfigName), $"127.0.0.1:{RelayPort}");
            game = Process.Start(new ProcessStartInfo(exe, "-config " + ConfigName) { WorkingDirectory = program, UseShellExecute = false })
                ?? throw new InvalidOperationException("The game did not start.");
            Console.WriteLine($"World of Warcraft {session.Client} started. Log in and play as usual; walk every room of the places you want. Close the game to finish.");
            var stop = false;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
            while (!stop && !game.WaitForExit(500)) { }
            Console.WriteLine(game.HasExited ? "Game closed." : "Stopping (the game keeps running, but nothing more is recorded).");
        }
        finally
        {
            liveTimer?.Dispose();
            capture?.Dispose();
            if (recording)
            {
                Console.WriteLine("Stopping the packet monitor (administrator prompt)...");
                try { PacketMonitor.Stop(folder); }
                catch (InvalidOperationException error) { Console.WriteLine("WARNING: " + error.Message + "\nIf a recording is still running, stop it with: ForeverCapture stop"); }
            }
        }
        if (session.Joins.Count == 0) Console.WriteLine("No realm join was seen, so nothing can be decrypted. Did the game reach the character screen?");
        if (relayOnly) return 0;
        var stats = DecodeFolder(folder);
        window?.Dispose();
        if (capture != null)
        {
            var (objects, track, _) = WorldDecoder.Decode(WorldCapture.Read(Path.Combine(folder, "world.fwcap")), KeyData(session));
            WriteLive(livePath, session, objects, track, false);
        }
        // The raw recording holds every program's traffic from the session. It goes once the game's part has proven readable;
        // until then it stays, because walking the dungeons again costs far more than disk space.
        string raw = Path.Combine(folder, "net.pcapng"), etl = Path.Combine(folder, "net.etl");
        if (stats.Decrypted > 0) { File.Delete(raw); File.Delete(etl); }
        else if (File.Exists(raw)) Console.WriteLine($"Nothing could be decrypted, so the raw recording was kept for another try: {raw} ({new FileInfo(raw).Length >> 20} MB).\nIt holds the session's recorded network traffic: keep it private and delete it when it is no longer needed.");
        return 0;
    }

    // Proves the setup without starting the game: the certificate, the relay's two TLS sides and the packet monitor. It makes one
    // TLS handshake with the real login server and sends nothing over it.
    private static int CheckSetup(string[] args)
    {
        var wow = Path.GetFullPath(Option(args, "--wow") ?? @"C:\Blizzard Games\World of Warcraft");
        var program = Path.Combine(wow, ProgramFolder);
        if (!File.Exists(Path.Combine(program, GameExe))) throw new IOException($"No WoW Forever in {wow} (expected {ProgramFolder}\\{GameExe}). Pass --wow <WoW folder>.");
        Console.WriteLine($"Game: {ClientVersion(wow)} in {program}");
        var (host, port) = Upstream(Path.Combine(program, "WTF", "Config.wtf"));
        using var certificate = LoopbackCertificate();
        Console.WriteLine($"Certificate: {certificate.Subject}, trusted, valid until {certificate.NotAfter:yyyy-MM-dd}.");
        using var lines = new System.Collections.Concurrent.BlockingCollection<string>();
        using (var relay = new LoginRelay(certificate, host, port, RelayPort, lines.Add, _ => { }, (_, _) => { }))
        {
            relay.Start();
            using var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect("127.0.0.1", RelayPort);
            using var tls = new System.Net.Security.SslStream(tcp.GetStream());
            tls.AuthenticateAsClient("127.0.0.1");   // normal Windows validation, as the game does it
            Console.WriteLine($"Relay, game side: certificate accepted ({tls.SslProtocol}).");
            if (!lines.TryTake(out var line, 15000)) throw new InvalidOperationException("The relay did not reach the login server within 15 seconds.");
            Console.WriteLine("Relay, server side: " + line);
            if (!line.Contains("relayed to", StringComparison.Ordinal)) return 1;
        }
        var probe = Path.Combine(Path.GetTempPath(), "forever-capture-check-" + Guid.NewGuid().ToString("N") + ".pcapng");
        using (var capture = LiveCapture.TryStart(probe, "tcp port " + PacketMonitor.WorldPort, (_, _, _, _, _, _, _) => { }, out var reason))
            Console.WriteLine(capture != null ? $"Npcap: records on {capture.AdapterCount} network adapters without administrator rights; the live map works."
                : $"Npcap: {reason}. Recording falls back to Windows' packet monitor (two administrator prompts, no live map).");
        File.Delete(probe);
        var monitor = Path.Combine(Environment.SystemDirectory, "PktMon.exe");
        Console.WriteLine(File.Exists(monitor) ? "Packet monitor: found." : "Packet monitor: MISSING (" + monitor + "); record with Wireshark into net.pcapng instead.");
        Console.WriteLine("Ready.");
        return 0;
    }

    private static DecodeStats DecodeFolder(string folder)
    {
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(folder, "session.json")), Indented);
        string capture = Path.Combine(folder, "world.fwcap"), pcap = Path.Combine(folder, "net.pcapng");
        // While the raw recording exists it is read again (a recording made with Wireshark and saved as net.pcapng works too);
        // world.fwcap is the small copy of just the game's connections that outlives it.
        if (File.Exists(pcap))
        {
            Console.WriteLine("Picking the game's world connections out of the recording...");
            WorldCapture.Write(capture, WorldCapture.Extract(pcap));
        }
        if (!File.Exists(capture)) throw new IOException("No world.fwcap or net.pcapng in " + folder);
        var (objects, track, stats) = WorldDecoder.Decode(WorldCapture.Read(capture), KeyData(session));
        var output = WriteObjects(folder, session, objects);
        // The character's own path, to see afterwards where a capture has been. It is yours alone: keep it out of anything published.
        File.WriteAllText(Path.Combine(folder, "track.json"), JsonSerializer.Serialize(new { client = session.Client, capturedUtc = session.StartedUtc,
            segments = track.Select(s => new { map = s.Map, points = s.Points.Select(p => p.Select(v => MathF.Round(v, 1)).ToArray()) }) }, CaptureJson));
        File.WriteAllText(Path.Combine(folder, "decode-stats.json"), JsonSerializer.Serialize(stats, Indented));

        Console.WriteLine($"World connections: {stats.Connections} recorded, {stats.Decrypted} decrypted, {stats.NoKey} without a key, {stats.Truncated} cut short.");
        Console.WriteLine($"Packets read: {stats.Frames} ({stats.CompressedFrames} compressed, {stats.CompressionErrors} unpack errors).");
        Console.WriteLine($"Game objects: {objects.Count} placed ({stats.Sightings} sightings, {stats.PlayerMade} player-made skipped).");
        foreach (var map in objects.GroupBy(o => o.Map).OrderByDescending(g => g.Count()).Take(12)) Console.WriteLine($"  map {map.Key}: {map.Count()}");
        Console.WriteLine(track.Count > 0 ? $"Your path: {stats.TrackPoints} points on map {string.Join(", ", track.Select(s => s.Map).Distinct())}." : "Your path: no movement was read.");
        if (stats.LayoutInexact > 0) Console.WriteLine($"NOTE: {stats.LayoutInexact} sightings did not match the 70888 field layout exactly; their model and position are still read.");
        if (stats.RejectedFlags + stats.RejectedValues > 0) Console.WriteLine($"NOTE: {stats.RejectedFlags} object blocks had an unsupported shape and {stats.RejectedValues} unreadable values (details in decode-stats.json).");
        Console.WriteLine("Written: " + output);
        return stats;
    }

    private static string CaptureRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForeverCapture");

    private static readonly JsonSerializerOptions CaptureJson = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // A capture's gameobjects.json, written whole and then swapped in: the map window may be reading it during a recording.
    private static string WriteObjects(string folder, Session session, List<PlacedObject> objects)
    {
        var output = Path.Combine(folder, "gameobjects.json");
        File.WriteAllText(output + ".tmp", JsonSerializer.Serialize(new { client = session.Client, capturedUtc = session.StartedUtc, objects }, CaptureJson));
        File.Move(output + ".tmp", output, true);
        return output;
    }

    // The map window alone, with every capture so far, until Enter is pressed.
    private static int MapWindow(string[] args)
    {
        var site = Option(args, "--site") ?? throw new InvalidOperationException("Name the WoW Atlas site's folder: ForeverCapture map --site <folder>");
        var wow = Path.GetFullPath(Option(args, "--wow") ?? @"C:\Blizzard Games\World of Warcraft");
        using var window = new LocalMap(Path.GetFullPath(site), CaptureRoot, wow, ClientVersion(wow));
        if (!args.Contains("--no-window")) window.OpenWindow(live: false);
        Console.WriteLine($"Map window: {window.Url} (3D: open a captured place in 3D from the map). Press Enter to stop.");
        // Started without a console to type in (in the background): it serves until it is stopped.
        if (Console.IsInputRedirected) Thread.Sleep(Timeout.Infinite); else Console.ReadLine();
        return 0;
    }

    // Key data = a game secret + a join secret. Every pair is offered; the decoder keeps the one a connection's first encrypted
    // packet proves. (KeyData: captures from before the secrets were stored apart.)
    private static List<byte[]> KeyData(Session session)
    {
        lock (session)
            return session.Joins.Where(j => j.KeyData != null).Select(j => Convert.FromHexString(j.KeyData))
                .Concat(session.Joins.Where(j => j.JoinSecret != null).SelectMany(j => session.GameSecrets.Select(g => Convert.FromHexString(g + j.JoinSecret)))).ToList();
    }

    // The live map's file: where the character is now, this session's object sightings ([map, x, y]) and path stretches
    // ({map, points: x, y, x, y...}), to a tenth of a yard. Written whole and then swapped in, so a reader never sees half.
    internal static void WriteLive(string path, Session session, List<PlacedObject> objects, List<TrackSegment> track, bool recording)
    {
        var last = track.LastOrDefault();
        var document = new
        {
            schemaVersion = 1, generator = "live/1", product = "wow_classic_beta", client = session.Client, utc = DateTime.UtcNow, recording,
            map = last?.Map, position = last?.Points[^1].Select(v => MathF.Round(v, 1)),
            objects = objects.Select(o => new object[] { o.Map, MathF.Round(o.X, 1), MathF.Round(o.Y, 1) }),
            tracks = track.Select(s => new { map = s.Map, points = s.Points.SelectMany(p => new[] { MathF.Round(p[0], 1), MathF.Round(p[1], 1) }) })
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(document));
        // A reader holding the old file makes the swap fail now and then; the next write replaces it.
        try { File.Move(path + ".tmp", path, true); } catch (IOException) { }
    }

    // "EU" -> eu.actual.battle.net:1119; a full host name is taken as it is.
    private static (string Host, int Port) Upstream(string configPath)
    {
        var portal = File.Exists(configPath) ? File.ReadLines(configPath).Select(l => Regex.Match(l, "^\\s*SET\\s+portal\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value : null;
        if (string.IsNullOrWhiteSpace(portal)) throw new InvalidOperationException($"No login region (SET portal) in {configPath}. Start the game normally once, then try again.");
        var parts = portal.Split(':');
        var host = parts[0].Contains('.') ? parts[0] : parts[0].ToLowerInvariant() + ".actual.battle.net";
        if (host is "localhost" or "127.0.0.1" || host.StartsWith("localhost.")) throw new InvalidOperationException($"{configPath} already points the login at this PC ({portal}); set its portal back to your region first.");
        return (host, parts.Length > 1 && int.TryParse(parts[1], out var port) ? port : 1119);
    }

    private static void WriteConfig(string source, string target, string portal)
    {
        var pattern = new Regex(@"^\s*SET\s+portal\s", RegexOptions.IgnoreCase);
        var lines = File.ReadAllLines(File.Exists(target) ? target : source).Where(l => !pattern.IsMatch(l)).Append($"SET portal \"{portal}\"");
        File.WriteAllText(target + ".tmp", string.Join("\n", lines) + "\n");
        File.Move(target + ".tmp", target, true);
    }

    // The sandbox launcher's own loopback certificate (CurrentUser store): 127.0.0.1 only, already trusted on this PC.
    private static X509Certificate2 LoopbackCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var leaf = store.Certificates.Where(c => c.Subject == "CN=127.0.0.1" && c.Issuer == "CN=Forever Sandbox Loopback Test Root" && c.HasPrivateKey && c.NotAfter > DateTime.Now)
            .OrderByDescending(c => c.NotAfter).FirstOrDefault()
            ?? throw new InvalidOperationException("The sandbox's local login certificate is missing. Run \"ForeverSandbox setup\" once, then try again.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(leaf)) throw new InvalidOperationException("The sandbox's local login certificate is no longer trusted. Run \"ForeverSandbox setup\" once, then try again.");
        return leaf;
    }

    private static string ClientVersion(string wow)
    {
        var lines = File.ReadAllLines(Path.Combine(wow, ".build.info"));
        var columns = lines[0].Split('|').Select(c => c.Split('!')[0]).ToArray();
        int product = Array.IndexOf(columns, "Product"), version = Array.IndexOf(columns, "Version");
        return lines.Skip(1).Select(l => l.Split('|')).FirstOrDefault(r => product >= 0 && r.Length > Math.Max(product, version) && r[product] == Product)?[version] ?? "unknown";
    }

    internal sealed class Session
    {
        public DateTime StartedUtc { get; set; }
        public string Client { get; set; }
        public string Upstream { get; set; }
        public List<string> GameSecrets { get; set; } = new();
        public List<SessionJoin> Joins { get; set; } = new();
    }
    internal sealed class SessionJoin
    {
        public DateTime Utc { get; set; }
        public string KeyData { get; set; }
        public string JoinSecret { get; set; }
        public string[] Servers { get; set; }
    }
}
