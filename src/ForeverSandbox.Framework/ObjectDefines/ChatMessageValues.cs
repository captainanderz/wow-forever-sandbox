using Framework.Constants;
using System.Collections.Generic;

namespace Framework.ObjectDefines
{
    public class ChatMessageValues
    {
        // Bits
        public bool HasLanguage = false;
        public bool HasRealmId  = false;

        // Data
        public List<SmartGuid> Guids = new List<SmartGuid>(4);
        public MessageType ChatType = MessageType.ChatMessageSay;
        public byte Language        = 0;
        public uint RealmId         = 1;
        public string Message       = "";
        public string From          = "";
        public float Time;

        public ChatMessageValues() { }
        public ChatMessageValues(MessageType type, string message, bool hasLanguage = false, bool hasRealmId = false, string source = "")
        {
            ChatType    = type;
            Message     = message;
            HasLanguage = hasLanguage;
            HasRealmId  = hasRealmId;
            From        = source;
        }
    }
}
