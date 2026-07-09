using ExileCore;
using System;
using System.Collections.Generic;

namespace FollowHer.Core.Combat.Skills
{
    public class SkillMonitor
    {
        private class SkillUsage
        {
            public DateTime LastUseTime { get; set; }
            public int UseCount { get; set; }
        }

        private readonly Dictionary<string, SkillUsage> _skillUsage = new();

        public void TrackUse(ActiveSkill skill)
        {
            if (skill == null) return;

            if (!_skillUsage.TryGetValue(skill.Name, out var usage))
            {
                usage = new SkillUsage();
                _skillUsage[skill.Name] = usage;
            }

            usage.LastUseTime = DateTime.Now;
            usage.UseCount++;
        }

        public bool CanUseSkill(ActiveSkill skill, int extraBuffer = 0)
        {
            if (skill == null || !skill.CanUse)
                return false;
            
            if (!_skillUsage.TryGetValue(skill.Name, out var usage))
                return true;

            var cooldownEnd = usage.LastUseTime.AddMilliseconds(extraBuffer);
            return DateTime.Now >= cooldownEnd;
        }

        public DateTime GetLastUseTime(string skillName)
        {
            return _skillUsage.TryGetValue(skillName, out var usage)
                ? usage.LastUseTime
                : DateTime.MinValue;
        }

        public int GetUseCount(string skillName)
        {
            return _skillUsage.TryGetValue(skillName, out var usage)
                ? usage.UseCount
                : 0;
        }

        public void Reset(string skillName)
        {
            _skillUsage.Remove(skillName);
        }

        public void Reset()
        {
            _skillUsage.Clear();
        }
    }
}