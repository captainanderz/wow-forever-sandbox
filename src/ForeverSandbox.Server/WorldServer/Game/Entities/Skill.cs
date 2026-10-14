using System;
using System.Runtime.Serialization;

namespace AuthServer.Game.WorldEntities
{
    [Serializable()]
    [DataContract]
    public class Skill
    {
        [DataMember]
        public uint Id { get; set; }
        [DataMember]
        public uint SkillLevel { get; set; }
    }
}
