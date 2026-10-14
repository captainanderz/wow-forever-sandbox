using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace ForeverSandbox.Server.WorldServer.Game.Entities
{
    [Serializable()]
    [DataContract]
    public class CharacterCustomizationChoice
    {
        [DataMember]
        public uint OptionId { get; set; }
        [DataMember]
        public uint Value { get; set; }
    }
}
