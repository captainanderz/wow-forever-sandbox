using System;

namespace AuthServer.Game.Chat
{
    public class ChatCommandAttribute : Attribute
    {
        public string ChatCommand { get; set; }
        public string Description { get; set; }

        public ChatCommandAttribute(string chatCommand, string description = "")
        {
            ChatCommand = chatCommand;
            Description = description;
        }
    }

    public class ChatCommand2Attribute : Attribute
    {
        public string ChatCommand
        { get; set; }
        public string Description
        { get; set; }

        public ChatCommand2Attribute(string chatCommand, string description = "")
        {
            ChatCommand = chatCommand;
            Description = description;
        }
    }
}
