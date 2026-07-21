using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;

namespace FollowHer.Core.Combat
{
    /// <summary>
    /// Decides whether the leader counts as "attacking" for the purpose of pulling the follower
    /// into combat.
    ///
    /// This is deliberately inverted from the original approach. Previously the check was
    /// isAttacking minus a hardcoded list of movement animations, which meant any skill that
    /// didn't drive one of those animations - most spells - simply never registered. Now every
    /// skill the leader uses counts as attacking unless the user has explicitly blacklisted it,
    /// so an unusual or newly-added skill works without a code change.
    /// </summary>
    public static class LeaderAttackDetector
    {
        public static bool IsAttacking(Actor leaderActor, IReadOnlyList<LeaderSkillBlacklistEntry> blacklist)
        {
            if (leaderActor == null) return false;

            var normalizedBlacklist = BuildNormalizedSet(blacklist);
            var blacklistedInUse = false;
            var otherInUse = false;

            var skills = leaderActor.ActorSkills;
            if (skills != null)
            {
                foreach (var skill in skills)
                {
                    if (skill == null || !skill.IsUsingOrCharging) continue;

                    if (IsBlacklisted(skill, normalizedBlacklist)) blacklistedInUse = true;
                    else otherInUse = true;
                }
            }

            // isAttacking is kept as a fallback for anything the skill list doesn't surface (or if
            // ActorSkills fails to read for a remote player), but a blacklisted skill actively
            // suppresses it - movement skills set isAttacking too, so without this a Shield Charge
            // would still read as an attack even though it's blacklisted.
            return otherInUse || (leaderActor.isAttacking && !blacklistedInUse);
        }

        private static bool IsBlacklisted(ActorSkill skill, HashSet<string> normalizedBlacklist)
        {
            if (normalizedBlacklist.Count == 0) return false;

            return normalizedBlacklist.Contains(Normalize(skill.InternalName)) ||
                   normalizedBlacklist.Contains(Normalize(skill.Name));
        }

        // Only enabled entries are honored, so a skill can be parked in the list (unchecked)
        // without affecting detection. The set is rebuilt each call - the list is tiny (a dozen or
        // so entries) so there's nothing worth caching.
        private static HashSet<string> BuildNormalizedSet(IReadOnlyList<LeaderSkillBlacklistEntry> blacklist)
        {
            var set = new HashSet<string>();
            if (blacklist == null) return set;

            foreach (var entry in blacklist)
            {
                if (entry == null || !entry.Enabled) continue;
                var normalized = Normalize(entry.Name);
                if (normalized.Length > 0) set.Add(normalized);
            }

            return set;
        }

        // Skill names are matched case- and whitespace-insensitively, so a single "Flame Dash"
        // entry matches the game's internal "FlameDash" without needing both spellings listed.
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        }
    }
}
