using System;
using System.Collections.Generic;
using System.Reflection;
using AuthServer.Network;

namespace AuthServer.Game.Chat
{
    public class ChatCommandParser
    {
        public static Dictionary<string, HandleChatCommand> ChatCommands = new Dictionary<string, HandleChatCommand>();
        public delegate void HandleChatCommand(string[] args, WorldClass session);

        public static Dictionary<string, HandleChatCommand2> ChatCommands2 = new Dictionary<string, HandleChatCommand2>();
        public delegate void HandleChatCommand2(string[] args, WorldClass2 session);

        public static void DefineChatCommands()
        {
            Assembly currentAsm = Assembly.GetExecutingAssembly();
            foreach (var type in currentAsm.GetTypes())
            {
                foreach (var methodInfo in type.GetMethods())
                {
                    var chatAttr = methodInfo.GetCustomAttribute<ChatCommandAttribute>();

                    if (chatAttr != null)
                        ChatCommands[chatAttr.ChatCommand] = (HandleChatCommand)Delegate.CreateDelegate(typeof(HandleChatCommand), methodInfo);
                }
            }
        }

        public static void ExecuteChatHandler(string chatCommand, WorldClass session)
        {
            var args = chatCommand.Split(new string[] { " " }, StringSplitOptions.None);

            if (args.Length > 0 && args[0].Length > 1)
            {
                var command = args[0].Remove(0, 1);

                if (ChatCommands.ContainsKey(command))
                    ChatCommands[command].Invoke(args, session);
            }
        }

        public static void DefineChatCommands2()
        {
            Assembly currentAsm = Assembly.GetExecutingAssembly();
            foreach (var type in currentAsm.GetTypes())
            {
                foreach (var methodInfo in type.GetMethods())
                {
                    var chatAttr = methodInfo.GetCustomAttribute<ChatCommand2Attribute>();

                    if (chatAttr != null)
                        ChatCommands2[chatAttr.ChatCommand] = (HandleChatCommand2)Delegate.CreateDelegate(typeof(HandleChatCommand2), methodInfo);
                }
            }
        }

        public static void ExecuteChatHandler2(string chatCommand, WorldClass2 session)
        {
            var args = chatCommand.Split(new string[] { " " }, StringSplitOptions.None);

            if (args.Length > 0 && args[0].Length > 1)
            {
                var command = args[0].Remove(0, 1);

                if (ChatCommands2.ContainsKey(command))
                    ChatCommands2[command].Invoke(args, session);
            }
        }

        public static bool CheckForCommand(string command)
        {
            var commandStarts = "!";

            if (command.StartsWith(commandStarts))
                return true;

            return false;
        }
    }
}
