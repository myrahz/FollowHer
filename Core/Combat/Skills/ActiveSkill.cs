using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;

namespace FollowHer.Core.Combat.Skills
{
    /// <summary>A combat skill: what the rule engine fires at monsters. Movement skills are
    /// <see cref="MovementSkill"/>.</summary>
    public class ActiveSkill : SkillBase
    {
        public ListNode LineOfSightType { get; set; } = new ListNode();

        [Menu("Requires Aiming", "Whether this skill needs the cursor placed on the target before casting - uncheck for buffs/supports/cries that fire immediately without targeting")]
        public ToggleNode RequiresAiming { get; set; } = new(true);
    }
}
