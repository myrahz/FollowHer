namespace FollowHer.Core.Events.Events
{
    public class TickEvent
    {
        public bool IsActive { get; set; }
        public bool CombatAllowed { get; set; }

        public TickEvent(bool isActive, bool combatAllowed = true)
        {
            IsActive = isActive;
            CombatAllowed = combatAllowed;
        }
    }
}
