// ForeverSandbox.exe: the standalone Forever 1.60.1.70338 sandbox.
//   ForeverSandbox [play]                      start the server and the frozen client (default)
//   ForeverSandbox setup                       local login certificates only
//   ForeverSandbox snapshot create|verify ...  tools\ForeverSnapshot (make or check the frozen client copy)
// Product folder: ForeverSandbox.exe, sandbox.json, server\, tools\, sandbox-data\, certificates\, state\, client\ (snapshot).
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ForeverSandbox;

internal static class Program
{
    // The only build the server speaks (packets, update fields): the exact 70338 executables.
    private static readonly Dictionary<string, string> SupportedBuild = new()
    {
        ["WowB.exe"] = "CFE26CC200FDA36407736C7E6B5575A1F2CA2B774C8176529814E32D51312185",
        ["WowB_loader.dll"] = "3788774B157CC0BACC394E7774C3AA51D56892DD97032394B78A5BD4BDA78518",
    };
    private const int AuthPort = 8000, RestPort = 8001, RealmPort = 8085, InstancePort = 3724;

    private static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        var rootIndex = Array.IndexOf(args, "--root");
        if (rootIndex >= 0 && rootIndex + 1 < args.Length) { root = Path.GetFullPath(args[rootIndex + 1]); args = args.Where((_, i) => i != rootIndex && i != rootIndex + 1).ToArray(); }
        Console.Title = "Forever Sandbox";
        try
        {
            switch (args.FirstOrDefault() ?? "play")
            {
                case "play": return Play(root);
                case "setup": new Certificates(root).Ensure(); return 0;
                case "snapshot": return Tool(Path.Combine(root, "tools", "ForeverSnapshot.exe"), args.Skip(1).ToArray());
                default: Console.WriteLine("ForeverSandbox [play | setup | snapshot create --from <WoW folder> --to <folder> | snapshot verify <folder>]"); return 2;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or CryptographicException or TimeoutException or System.ComponentModel.Win32Exception or JsonException)
        {
            Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("ERROR: " + error.Message); Console.ResetColor();
            Pause();
            return 1;
        }
    }

    private static int Play(string root)
    {
        var settingsPath = Path.Combine(root, "sandbox.json");
        var settings = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject() : new JsonObject();
        var clientRoot = Path.GetFullPath(Path.Combine(root, (string)settings["clientRoot"] ?? "client"));
        var manifestPath = Path.Combine(clientRoot, "snapshot.json");
        if (!File.Exists(manifestPath)) throw new IOException($"No client snapshot in {clientRoot}. Make one with: ForeverSandbox snapshot create --from \"C:\\Blizzard Games\\World of Warcraft\" --to \"{clientRoot}\"");
        var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
        var programFolder = Path.Combine(clientRoot, manifest.GetProperty("programFolder").GetString()!);
        var exe = Path.Combine(programFolder, "WowB.exe");
        foreach (var (file, hash) in SupportedBuild)
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(programFolder, file)))) != hash)
                throw new InvalidOperationException($"{file} in the snapshot is not the 1.60.1.70338 build this sandbox supports.");
        if (Process.GetProcessesByName("WowB").Any(p => SamePath(p, exe))) throw new InvalidOperationException("The sandbox game is already running.");

        // The world connection needs the realm-key write (one 32-byte public key in the running game's memory, like
        // ForeverLauncher). It is opt-in: asked once, remembered in sandbox.json.
        var realmKey = (bool?)settings["realmKeyWrite"];
        if (realmKey == null)
        {
            Console.WriteLine("Entering the world needs one in-memory change in the game this launcher starts: the server's public key is");
            Console.WriteLine("written into the game's key table (32 bytes, nothing on disk, gone when the game closes), as ForeverLauncher does.");
            Console.Write("Allow it? [y/N] ");
            realmKey = Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
            settings["realmKeyWrite"] = realmKey;
            File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        if (realmKey != true) Console.WriteLine("Realm-key write is off (sandbox.json): login and characters work, entering the world does not.");

        var leaf = new Certificates(root).Ensure();
        var state = Path.Combine(root, "state", "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
        Directory.CreateDirectory(state);
        var data = Path.Combine(root, "sandbox-data");

        var server = StartServer(root, state, data, clientRoot, leaf, realmKey == true);
        Process client = null;
        try
        {
            var ready = WaitReady(server, state);
            Preflight(ready);
            WriteClientConfig(Path.Combine(programFolder, "WTF", "ForeverSandbox.wtf"), new()
            {
                ["portal"] = $"127.0.0.1:{AuthPort}", ["accountName"] = "admin", ["textLocale"] = "enUS", ["audioLocale"] = "enUS",
            });
            client = Process.Start(new ProcessStartInfo(exe, "-config ForeverSandbox.wtf") { WorkingDirectory = programFolder, UseShellExecute = false })
                ?? throw new InvalidOperationException("The game did not start.");
            Console.WriteLine($"World of Warcraft {manifest.GetProperty("version").GetString()} started from the frozen snapshot. Log in with admin / admin.");
            Console.WriteLine("Type .help in chat for the sandbox commands, /fog off for no fog. Close the game to stop the sandbox.");
            var monitor = realmKey == true ? new RealmKeyMonitor(client, ready.GetProperty("diagnosticLogPath").GetString()!, Path.Combine(state, "realm-key-patched")) : null;
            // /fog off also clears the map and scene fog no game setting reaches (Fog.cs); "fullFogOff": false turns that off.
            var fog = (bool?)settings["fullFogOff"] != false ? new FogMonitor(client) : null;
            // .walls (or /walls) draws impassable terrain walls over the game window (Walls.cs); the server writes them here.
            using var walls = new WallOverlay(client, Path.Combine(state, "impass-walls.json"));
            var stop = false;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
            while (!stop && !client.WaitForExit(250))
            {
                if (server.HasExited) { Console.WriteLine("The sandbox server stopped unexpectedly; see " + state); break; }
                if (monitor?.Poll() is { } status) Console.WriteLine("Realm key: " + status);
                if (fog?.Poll() is { } fogStatus) Console.WriteLine("Fog: " + fogStatus);
            }
            Console.WriteLine(client.HasExited ? "Game closed." : "Stopping.");
            return 0;
        }
        finally
        {
            if (client is { HasExited: false }) { try { client.Kill(); client.WaitForExit(5000); } catch (InvalidOperationException) { } }
            StopServer(server, state);
        }
    }

    private static bool SamePath(Process p, string exe)
    {
        try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static Process StartServer(string root, string state, string data, string clientRoot, string leaf, bool realmKey)
    {
        var exe = Path.Combine(root, "server", "ForeverSandbox.Server.exe");
        if (!File.Exists(exe)) throw new IOException("The sandbox server is missing: " + exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in new[]
        {
            "--state-directory", state, "--auth-port", AuthPort.ToString(), "--rest-port", RestPort.ToString(), "--realm-port", RealmPort.ToString(), "--instance-port", InstancePort.ToString(),
            "--run-for-seconds", "86400", "--hostname", "localhost", "--diagnostic-events", "1024", "--certificate-thumbprint", leaf, "--world-handshake-probe", "2",
            "--character-store", Path.Combine(data, "characters.json"), "--hotfix-data", Path.Combine(data, "hotfixes.json"), "--account-data", Path.Combine(data, "account-data.json"),
            "--client-install", clientRoot,
        }.Concat(realmKey ? new[] { "--world-key-signal", "1", "--world-encrypt-delay-ms", "10000" } : new[] { "--world-encrypt-delay-ms", "3000" })) start.ArgumentList.Add(a);
        var server = Process.Start(start) ?? throw new InvalidOperationException("The sandbox server did not start.");
        var stdout = File.CreateText(Path.Combine(state, "stdout.log")); var stderr = File.CreateText(Path.Combine(state, "stderr.log"));
        stdout.AutoFlush = stderr.AutoFlush = true;
        server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.WriteLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.WriteLine(e.Data); };
        server.BeginOutputReadLine(); server.BeginErrorReadLine();
        Console.WriteLine("Starting the sandbox server...");
        return server;
    }

    private static JsonElement WaitReady(Process server, string state)
    {
        var path = Path.Combine(state, "ready.json");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (server.HasExited) throw new InvalidOperationException($"The sandbox server exited before it was ready (code {server.ExitCode}); see {state}.");
            if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("The sandbox server did not become ready.");
            Thread.Sleep(100);
        }
        Thread.Sleep(100);
        var ready = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        if (!ready.GetProperty("ready").GetBoolean() || ready.GetProperty("processId").GetInt32() != server.Id || ready.GetProperty("bindAddress").GetString() != "127.0.0.1"
            || ready.GetProperty("advertisedHostname").GetString() != "localhost" || !ready.GetProperty("certificateChainTrusted").GetBoolean())
            throw new InvalidOperationException("The sandbox server's readiness does not match (certificate not trusted or wrong address).");
        Console.WriteLine($"Sandbox server ready (127.0.0.1:{AuthPort}).");
        return ready;
    }

    // Standard TLS to the login port with normal Windows validation, presenting the expected certificate.
    private static void Preflight(JsonElement ready)
    {
        using var tcp = new TcpClient();
        tcp.Connect("127.0.0.1", ready.GetProperty("authPort").GetInt32());
        using var tls = new SslStream(tcp.GetStream());
        tls.AuthenticateAsClient("127.0.0.1");
        var thumbprint = Convert.ToHexString(SHA1.HashData(tls.RemoteCertificate!.GetRawCertData()));
        if (thumbprint != ready.GetProperty("certificateThumbprint").GetString()) throw new InvalidOperationException("The login port presented a different certificate.");
    }

    // One persistent client config: the game saves its own settings into it; only the sandbox's lines are set.
    private static void WriteClientConfig(string path, Dictionary<string, string> required)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pattern = new Regex(@"^\s*SET\s+(\S+)\s", RegexOptions.IgnoreCase);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        lines = lines.Where(l => pattern.Match(l) is not { Success: true } m || !required.ContainsKey(m.Groups[1].Value)).ToList();
        lines.AddRange(required.Select(p => $"SET {p.Key} \"{p.Value}\""));
        File.WriteAllText(path + ".tmp", string.Join("\n", lines) + "\n");
        File.Move(path + ".tmp", path, true);
    }

    private static void StopServer(Process server, string state)
    {
        if (!server.HasExited)
        {
            File.WriteAllText(Path.Combine(state, "stop.request"), "stop");
            if (!server.WaitForExit(10000)) { server.Kill(true); server.WaitForExit(5000); }
        }
        Console.WriteLine("Sandbox server stopped. Run details: " + state);
    }

    private static int Tool(string exe, string[] args)
    {
        if (!File.Exists(exe)) throw new IOException("Missing " + exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Pause()
    {
        if (!Console.IsInputRedirected && Environment.UserInteractive) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
    }
}
