using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

using AuthServer.Configuration;
using AuthServer.Game.Chat;
using AuthServer.Managers;
using AuthServer.Network;
using AuthServer.Packets;

using Framework.Constants.Misc;
using Framework.Logging;
using Framework.Misc;
/*
[assembly: Obfuscation(Feature = "Apply to type *: apply to member * when method or constructor: virtualization", Exclude = true)]
[assembly: Obfuscation(Feature = "code control flow obfuscation", Exclude = false)]
[assembly: Obfuscation(Feature = "rename serializable symbols", Exclude = false)]
[assembly: Obfuscation(Feature = "rename symbols", Exclude = false)]
[assembly: Obfuscation(Feature = "Apply to type *: renaming", Exclude = false)]
[assembly: Obfuscation(Feature = "encrypt resources", Exclude = false)]
//[assembly: Obfuscation(Feature = "sanitize resources", Exclude = false)]


[assembly: Obfuscation(Feature = "Apply to type Framework.ObjectDefine*: renaming", Exclude = true)]
[assembly: Obfuscation(Feature = "Apply to type Framework.Misc.HttpHeader: all", Exclude = true, ApplyToMembers = false)]

[assembly: Obfuscation(Feature = "Apply to type Google.*: all", Exclude = true, ApplyToMembers = true)]
[assembly: Obfuscation(Feature = "Apply to type AuthServer.Game.Entities*: renaming", Exclude = true)]
[assembly: Obfuscation(Feature = "Apply to type AuthServer.WorldServer.Game.Entities*: renaming", Exclude = true)]
[assembly: Obfuscation(Feature = "Apply to type AuthServer.Game.WorldEntities*: renaming", Exclude = true)]
[assembly: Obfuscation(Feature = "Apply to type Google.Protobuf.*: all", Exclude = true, ApplyToMembers = true)]
[assembly: Obfuscation(Feature = "Apply to type Google.Protobuf.Reflection.*: all", Exclude = true, ApplyToMembers = true)]*/

namespace AuthServer
{
    class Sandbox
    {
        public static AuthSession AuthSession;
        public static bool IsWindows7 = Environment.OSVersion.Version.Major == 6;

        public static int RestPort;
        public static int RealmPort;
        public static int InstancePort;

        public static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            AuthConfig.ReadConfig();

            //PacketLog.Initialize(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "/Logs/", "Packet.txt");

            Helper.PrintHeader("Sandbox for Shadowlands 9.2.7");

            new Thread(() =>
            {
                var bnetPort = 8000;

                while (!CanUsePort(bnetPort))
                    ++bnetPort;

                if (bnetPort == 8002)
                    bnetPort += 1;

                if (bnetPort != 8000)
                {
                    Log.Message(LogType.Info, $"Aurora portal port is in use. Using {bnetPort} instead.");
                    Log.Message(LogType.Info, $"Please use 'set portal \"127.0.0.1:{bnetPort}\". in your Config2.wtf");
                }
                AuthConfig.BindPort = bnetPort;

                new Server(AuthConfig.BindIP, bnetPort);
                {
                    PacketManager.Initialize();

                    Manager.Initialize();

                    // Prevents closing this stuff...
                    while (true)
                    {
                        Thread.Sleep(5);
                    }
                }
            }).Start();

            // Web Server
            new Thread(() =>
            {
                var restPort = 8001;

                RestPort = restPort;

                new Server(AuthConfig.BindIP, restPort, true);
                {
                    while (true)
                    {
                        Thread.Sleep(5);
                    }
                }
            }).Start();

            new Thread(() =>
            {
                WorldServer.Managers.Manager.Initialize();

                // Initialize hotfixes
                WorldServer.Managers.Manager.Hotfix.Load().GetAwaiter().GetResult();

                var realmPort = 8085;

                while (!CanUsePort(realmPort))
                    ++realmPort;

                RealmPort = realmPort;
                WorldClass.world = new WorldNetwork();
                {
                    if (WorldClass.world.Start("0.0.0.0", realmPort))
                    {
                        WorldClass.world.AcceptConnectionThread();

                        WorldServer.Game.Packets.PacketManager.DefineOpcodeHandler();
                        ChatCommandParser.DefineChatCommands();
                    }
                    else
                    {
                        Log.Message(LogType.Error, "Server couldn't be started.");
                    }
                }

                new Thread(() =>
                {
                    var instancePort = 3724;

                    while (!CanUsePort(instancePort))
                        ++instancePort;

                    InstancePort = instancePort;
                    WorldClass2.world = new WorldNetwork();
                    {
                        if (WorldClass2.world.Start("0.0.0.0", instancePort))
                        {
                            WorldClass2.world.AcceptConnectionThread2();

                            WorldServer.Game.Packets.PacketManager.DefineOpcodeHandler2();
                            ChatCommandParser.DefineChatCommands2();
                        }
                        else
                        {
                            Log.Message(LogType.Error, "Server couldn't be started.");
                        }
                    }
                }).Start();

                Log.Message(LogType.Normal, "Sandbox server started");

                Log.Message(LogType.Info, "Usage:");
                Log.Message(LogType.Info, "1. Start this sandbox :P.");
                Log.Message(LogType.Info, "2. Start the wow launcher.");
                Log.Message(LogType.Info, "3. Login with Email: admin and password: admin.");
                Log.Message(LogType.Info, "Have fun!!!");
            }).Start();

            while (true)
            {
#if PUBLIC == false
                try
                {
                    var line = Console.ReadLine();
                    var session = WorldServer.Managers.Manager.WorldMgr.Sessions.FirstOrDefault().Value;

                    ChatCommandParser.ExecuteChatHandler(line, session);

                    var session2 = WorldServer.Managers.Manager.WorldMgr.Sessions2.FirstOrDefault().Value;

                    ChatCommandParser.ExecuteChatHandler2(line, session2);
                }
                catch (Exception e)
                {
                    Log.Message(LogType.Error, e.ToString());
                    Log.Message(LogType.Error, e.StackTrace);
                }
#endif
                Thread.Sleep(5);
            }
        }

        static bool CanUsePort(int port)
        {
            bool isAvailable = true;

            IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

            foreach (IPEndPoint endpoint in tcpConnInfoArray)
            {
                if (endpoint.Port == port)
                {
                    isAvailable = false;
                    break;
                }
            }

            return isAvailable;
        }
    }
}
