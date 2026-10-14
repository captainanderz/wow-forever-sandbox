using Framework.Misc;

namespace AuthServer.Game.Managers
{
    public sealed class SpellManager : Singleton<SpellManager>
    {
        SpellManager() { }

//         public void LoadSpells(Character pChar)
//         {
//             SQLResult result = DB.Characters.Select("SELECT * FROM character_spells WHERE guid = ? ORDER BY spellId ASC", pChar.Guid);
// 
//             if (result.Count == 0)
//             {
//                 result = DB.Characters.Select("SELECT spellId FROM character_creation_spells WHERE race = ? AND class = ? ORDER BY spellId ASC", pChar.Race, pChar.Class);
// 
//                 for (int i = 0; i < result.Count; i++)
//                     AddSpell(pChar, result.Read<uint>(i, "spellId"));
// 
//                 SaveSpells(pChar);
//             }
//             else
//             {
//                 for (int i = 0; i < result.Count; i++)
//                     AddSpell(pChar, result.Read<uint>(i, "spellId"));
//             }
//         }
// 
//         public void SaveSpells(Character pChar)
//         {
//             pChar.SpellList.ForEach(spell =>
//                 DB.Characters.Execute("INSERT INTO character_spells (guid, spellId) VALUES (?, ?)", pChar.Guid, spell.SpellId));
//         }
// 
//         public void AddSpell(Character pChar, uint spellId)
//         {
//             PlayerSpell newspell = new PlayerSpell()
//             {
//                 SpellId = spellId,
//                 State = PlayerSpellState.Unchanged,
//                 Dependent = false,
//             };
// 
//             pChar.SpellList.Add(newspell);
//         }        
    }
}
