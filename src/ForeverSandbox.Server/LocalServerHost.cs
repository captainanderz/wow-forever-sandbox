// Forever Sandbox server entry point: local Battle.net login (BGS and REST over loopback TLS) and the WoW Forever 1.60.1.70338
// world session, with the sandbox's state directory, data files and client install given on the command line.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AuthServer.Configuration;
using AuthServer.Game.Chat;
using AuthServer.Network;
using AuthServer.Packets;
using Framework.Misc;

namespace AuthServer;

internal static class LocalServerHost
{
    private static volatile bool stopRequested;

    public static void Main(string[] args)
    {
        Server auth = null;
        Server authIpv6 = null;
        Server rest = null;
        FileStream stateLock = null;
        string readyFile = null;
        var exitCode = 0;
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("Forever Sandbox server for WoW Forever beta 1.60.1.70338 (started by ForeverSandbox.exe).");
                Console.WriteLine("--state-directory PATH --auth-port 8000 --rest-port 8001 --realm-port 8085 --instance-port 3724 --run-for-seconds 0");
                Console.WriteLine("Optional: --certificate-thumbprint CURRENTUSER_MY_THUMBPRINT --hostname localhost --diagnostic-events 128");
                return;
            }

            var options = ParseOptions(args);
            var stateDirectory = Path.GetFullPath(options.GetValueOrDefault("--state-directory", Path.Combine(AppContext.BaseDirectory, "state")));
            Directory.CreateDirectory(stateDirectory);
            stateLock = new FileStream(Path.Combine(stateDirectory, ".server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            readyFile = Path.Combine(stateDirectory, "ready.json");
            var stopFile = Path.Combine(stateDirectory, "stop.request");
            File.Delete(readyFile);
            File.Delete(stopFile);
            Environment.SetEnvironmentVariable("FOREVER_SANDBOX_STATE", stateDirectory);
            Directory.SetCurrentDirectory(stateDirectory);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            var authPort = ReadNumber(options, "--auth-port", 8000, 1024, 65535);
            Sandbox.RestPort = ReadNumber(options, "--rest-port", 8001, 1024, 65535);
            Sandbox.RealmPort = ReadNumber(options, "--realm-port", 8085, 1024, 65535);
            Sandbox.InstancePort = ReadNumber(options, "--instance-port", 3724, 1024, 65535);
            ForeverWorldHandshake.RealmPort = Sandbox.RealmPort;
            ForeverWorldHandshake.InstancePort = Sandbox.InstancePort;
            var runSeconds = ReadNumber(options, "--run-for-seconds", 0, 0, 86400);
            var diagnosticEvents = ReadNumber(options, "--diagnostic-events", 0, 0, 1024);
            var enableAuthIpv6 = ReadNumber(options, "--auth-ipv6", 0, 0, 1) == 1;
            // 1 = one-packet handshake measurement; 2 = encrypted Forever world session.
            var worldMode = ReadNumber(options, "--world-handshake-probe", 0, 0, 2);
            ForeverWorldHandshake.EncryptDelayMilliseconds = ReadNumber(options, "--world-encrypt-delay-ms", 2500, 0, 15000);
            if (ReadNumber(options, "--world-key-signal", 0, 0, 1) == 1)
            {
                ForeverWorldHandshake.RealmKeySignalPath = Path.Combine(stateDirectory, "realm-key-patched");
                File.Delete(ForeverWorldHandshake.RealmKeySignalPath);
            }
            // .walls output for the launcher's overlay (ForeverImpass.cs).
            ForeverImpassWalls.OutputPath = Path.Combine(stateDirectory, "impass-walls.json");
            File.Delete(ForeverImpassWalls.OutputPath);
            // Sandbox characters persist across runs only when a store file is given.
            var characterStore = options.GetValueOrDefault("--character-store");
            if (characterStore != null) ForeverCharacterStore.Current = new ForeverCharacterStore(Path.GetFullPath(characterStore));
            // Saved .loc teleport spots live next to the characters.
            if (characterStore != null) ForeverLocations.Current = new ForeverLocations(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(characterStore)), "locations.json"));
            var hotfixData = options.GetValueOrDefault("--hotfix-data");
            if (hotfixData != null) ForeverHotfixStore.Current = ForeverHotfixStore.Load(Path.GetFullPath(hotfixData));
            // GameObjectDisplayInfo rows for the .walls models (ForeverImpass.cs).
            ForeverHotfixStore.Current.SetOverlay(ForeverImpassWalls.DisplayHotfixes());
            // The local client install (for terrain and client tables): opened in the background; TACT keys come from the hotfixes.
            var clientInstall = options.GetValueOrDefault("--client-install");
            if (clientInstall != null) ForeverWallModels.Installed = ForeverWallModels.InstalledIn(Path.GetFullPath(clientInstall));
            if (clientInstall != null)
                ForeverTerrain.StartLoading(Path.GetFullPath(clientInstall), "1.60.1.70338", id => ForeverHotfixStore.Current.RecordData(ForeverHotfixStore.TactKeyTable, id));
            var accountData = options.GetValueOrDefault("--account-data");
            if (accountData != null) ForeverAccountData.Current = new ForeverAccountData(Path.GetFullPath(accountData));
            var diagnosticPath = Path.Combine(stateDirectory, "auth-diagnostics.ndjson");
            LocalAuthDiagnostics.Configure(diagnosticPath, diagnosticEvents);
            LocalTls.Configure(options.GetValueOrDefault("--certificate-thumbprint"), options.GetValueOrDefault("--hostname"));
            if (new[] { authPort, Sandbox.RestPort, Sandbox.RealmPort, Sandbox.InstancePort }.Distinct().Count() != 4)
                throw new ArgumentException("Each listener requires a distinct port.");

            AuthConfig.ReadConfig();
            AuthConfig.BindPort = authPort;
            Helper.PrintHeader("the sandbox server (WoW Forever beta 1.60.1.70338)");
            // Initialize handlers and resources before admitting clients.
            PacketManager.Initialize();
            global::AuthServer.Managers.Manager.Initialize();
            WorldServer.Managers.Manager.Initialize();
            WorldServer.Managers.Manager.Hotfix.Load().GetAwaiter().GetResult();
            WorldServer.Game.Packets.PacketManager.DefineOpcodeHandler();
            WorldServer.Game.Packets.PacketManager.DefineOpcodeHandler2();
            ChatCommandParser.DefineChatCommands();
            ChatCommandParser.DefineChatCommands2();

            auth = new Server(AuthConfig.BindIP, authPort);
            if (enableAuthIpv6) authIpv6 = new Server("::1", authPort);
            rest = new Server(AuthConfig.BindIP, Sandbox.RestPort, true);
            WorldClass.world = new WorldNetwork();
            WorldClass.world.ForeverHandshakeProbe = worldMode == 1;
            WorldClass.world.ForeverWorldSession = worldMode == 2;
            WorldClass2.world = new WorldNetwork();
            WorldClass2.world.ForeverWorldSession = worldMode == 2;
            if (!auth.IsRunning || (enableAuthIpv6 && !authIpv6.IsRunning) || !rest.IsRunning ||
                !WorldClass.world.Start(AuthConfig.BindIP, Sandbox.RealmPort) ||
                !WorldClass2.world.Start(AuthConfig.BindIP, Sandbox.InstancePort))
                throw new IOException("A requested loopback port could not be bound. No readiness file was published.");
            WorldClass.world.AcceptConnectionThread();
            WorldClass2.world.AcceptConnectionThread2();

            var readiness = new
            {
                schemaVersion = 1,
                ready = true,
                processId = Environment.ProcessId,
                startedUtc = DateTimeOffset.UtcNow,
                advertisedHostname = LocalTls.Hostname,
                certificateSource = LocalTls.CertificateThumbprint == null ? "built-in" : "CurrentUser/My",
                certificateThumbprint = LocalTls.CertificateThumbprint,
                certificateChainTrusted = LocalTls.ChainTrusted,
                certificateChainStatus = LocalTls.ChainStatus,
                diagnosticEventLimit = diagnosticEvents,
                diagnosticLogPath = diagnosticEvents == 0 ? null : diagnosticPath,
                bindAddress = AuthConfig.BindIP,
                stateDirectory,
                authPort,
                authIpv6Loopback = enableAuthIpv6,
                restPort = Sandbox.RestPort,
                realmPort = Sandbox.RealmPort,
                instancePort = Sandbox.InstancePort,
                worldProtocol = worldMode switch { 1 => "Forever70338HandshakeProbe", 2 => "Forever70338Session", _ => "legacy" },
                worldEncryptDelayMilliseconds = worldMode == 2 ? ForeverWorldHandshake.EncryptDelayMilliseconds : (int?)null
            };
            var readyTemp = readyFile + ".tmp";
            File.WriteAllText(readyTemp, JsonSerializer.Serialize(readiness, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(readyTemp, readyFile, true);
            Console.WriteLine($"LOCAL_SERVER_READY {readyFile}");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopRequested = true; };
            var deadline = runSeconds == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(runSeconds);
            while (!stopRequested && !File.Exists(stopFile) && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LOCAL_SERVER_ERROR {ex}");
            exitCode = 1;
        }
        finally
        {
            if (stateLock != null)
            {
                if (readyFile != null) File.Delete(readyFile);
                // Process exit closes world sockets and the legacy foreground workers.
                auth?.Dispose();
                authIpv6?.Dispose();
                rest?.Dispose();
                stateLock.Dispose();
            }
            Environment.Exit(exitCode);
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var known = new HashSet<string> { "--state-directory", "--auth-port", "--rest-port", "--realm-port", "--instance-port", "--run-for-seconds", "--certificate-thumbprint", "--hostname", "--diagnostic-events", "--auth-ipv6", "--world-handshake-probe", "--world-encrypt-delay-ms", "--character-store", "--world-key-signal", "--hotfix-data", "--account-data", "--client-install" };
        var result = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!known.Contains(args[i]) || i + 1 >= args.Length || !result.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Unknown, repeated or incomplete option: {args[i]}");
        }
        return result;
    }

    private static int ReadNumber(Dictionary<string, string> options, string name, int fallback, int minimum, int maximum)
    {
        if (!options.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        return value;
    }
}
