using System;
using Framework.Constants.Net;

namespace Framework.Network.Packets
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class OpcodeAttribute : Attribute
    {
        public ClientMessage Opcode { get; set; }
        public string WoWBuild { get; set; }

        public OpcodeAttribute(ClientMessage opcode, string wowBuild)
        {
            Opcode = opcode;
            WoWBuild = wowBuild;
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class Opcode2Attribute : Attribute
    {
        public ClientMessage Opcode
        { get; set; }
        public string WoWBuild
        { get; set; }

        public Opcode2Attribute(ClientMessage opcode, string wowBuild)
        {
            Opcode = opcode;
            WoWBuild = wowBuild;
        }
    }
}
