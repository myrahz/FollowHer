using System;
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
        private static string _cachedRaw;
        private static HashSet<string> _cachedNames = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsAttacking(Actor leaderActor, string blacklistRaw)
        {
            if (leaderActor == null) return false;

            var blacklist = GetBlacklist(blacklistRaw);
            var blacklistedInUse = false;
            var otherInUse = false;

            var skills = leaderActor.ActorSkills;
            if (skills != null)
            {
                foreach (var skill in skills)
                {
                    if (skill == null || !skill.IsUsingOrCharging) continue;

                    if (IsBlacklisted(skill, blacklist)) blacklistedInUse = true;
                    else otherInUse = true;
                }
            }

            // isAttacking is kept as a fallback for anything the skill list doesn't surface (or if
            // ActorSkills fails to read for a remote player), but a blacklisted skill actively
            // suppresses it - movement skills set isAttacking too, so without this a Shield Charge
            // would still read as an attack even though it's blacklisted.
            return otherInUse || (leaderActor.isAttacking && !blacklistedInUse);
        }

        private static bool IsBlacklisted(ActorSkill skill, HashSet<string> blacklist)
        {
            if (blacklist.Count == 0) return false;

            return (!string.IsNullOrEmpty(skill.InternalName) && blacklist.Contains(skill.InternalName)) ||
                   (!string.IsNullOrEmpty(skill.Name) && blacklist.Contains(skill.Name));
        }

        // Reparsed only when the settings string actually changes, since this runs every tick.
        private static HashSet<string> GetBlacklist(string raw)
        {
            raw ??= "";
            if (raw != _cachedRaw)
            {
                _cachedNames = new HashSet<string>(
                    raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                _cachedRaw = raw;
            }

            return _cachedNames;
        }
    }
}
