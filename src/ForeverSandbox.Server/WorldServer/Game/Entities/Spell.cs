using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AuthServer.Game.WorldEntities
{
    [Serializable()]
    [DataContract]
    public enum PlayerSpellState
    {
        Unchanged = 0,
        Changed   = 1,
        New       = 2,
        Removed   = 3,
        Temporary = 4
    }

    [Serializable()]
    [DataContract]
    public class PlayerSpell
    {
        [DataMember]
        public uint SpellId;
        [DataMember]
        public PlayerSpellState State;
        [DataMember]
        public bool Active;
        [DataMember]
        public bool Dependent;
        [DataMember]
        public bool Disabled;
    }

    public static class SpellMethods
    {
        public static PlayerSpell FindPlayerSpell(this List<PlayerSpell> SpellList, uint spellId)
        {
            return SpellList.Find(p => p.SpellId == spellId);
        }
    }
}
