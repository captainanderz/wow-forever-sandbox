using Framework.Misc;

namespace AuthServer.Game.Managers
{
    public class SkillManager : Singleton<SkillManager>
    {
        SkillManager() { }

//         public void LoadSkills(Character pChar)
//         {
//             SQLResult result = DB.Characters.Select("SELECT * FROM character_skills WHERE guid = ? ORDER BY skill ASC", pChar.Guid);
// 
//             if (result.Count == 0)
//             {
//                 result = DB.Characters.Select("SELECT skill FROM character_creation_skills WHERE race = ? ORDER BY skill ASC", pChar.Race, pChar.Class);
// 
//                 for (int i = 0; i < result.Count; i++)
//                     AddSkill(pChar, result.Read<uint>(i, "skill"));
// 
//                 SaveSkills(pChar);
//             }
//             else
//             {
//                 for (int i = 0; i < result.Count; i++)
//                     AddSkill(pChar, result.Read<uint>(i, "skill"));
//             }
//         }
// 
//         public void SaveSkills(Character pChar)
//         {
//             pChar.Skills.ForEach(skill =>
//                 DB.Characters.Execute("INSERT INTO character_skills (guid, skill) VALUES (?, ?)", pChar.Guid, skill.Id));
//         }
// 
//         public void AddSkill(Character pChar, uint skillId, uint skillLevel = 0)
//         {
//             Skill skill = new Skill()
//             {
//                 Id         = skillId,
//                 SkillLevel = skillLevel,
//             };
// 
//             pChar.Skills.Add(skill);
//         }
    }
}
