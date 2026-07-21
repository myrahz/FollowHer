namespace FollowHer.Core.Combat
{
    /// <summary>One entry in the leader-skill blacklist: a skill name plus an enable checkbox, so
    /// a skill can be kept in the list but temporarily switched off without deleting it. Plain
    /// properties (not ExileCore nodes) - this is rendered by a custom ImGui editor, and stored as
    /// plain data the same way CombatRule is.</summary>
    public class LeaderSkillBlacklistEntry
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;

        public LeaderSkillBlacklistEntry() { }

        public LeaderSkillBlacklistEntry(string name)
        {
            Name = name;
        }
    }
}
