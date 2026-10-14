using System.Collections.Generic;
using System.Linq;
using Framework.Misc;
using AuthServer.Game.Entities;
using System;
using AuthServer.WorldServer.Managers;
using System.Runtime.Serialization;

[Serializable()]
[DataContract]
public class ActionButton
{
    [DataMember]
    public ulong Action;
    [DataMember]
    public byte SlotId;
    [DataMember]
    public byte SpecGroup;
}

namespace AuthServer.Game.Managers
{
    public class ActionManager : Singleton<ActionManager>
    {
        ActionManager() { }

        public void LoadActionButtons(Character pChar)
        {

        }

        public List<ActionButton> GetActionButtons(Character pChar, byte specGroup)
        {
            return pChar.ActionButtons.Where(action => action.SpecGroup == specGroup).ToList();
        }

        public void AddActionButton(Character pChar, ActionButton actionButton, bool addToDb = false)
        {
            if (pChar == null || actionButton == null)
                return;

            pChar.ActionButtons.Add(actionButton);

            Manager.ObjectMgr.SaveChar(pChar);
        }

        public void RemoveActionButton(Character pChar, ActionButton actionButton, bool deleteFromDb = false)
        {
            if (pChar == null || actionButton == null)
                return;

            var deleted = pChar.ActionButtons.Remove(actionButton);

            Manager.ObjectMgr.SaveChar(pChar);
        }
    }
}
