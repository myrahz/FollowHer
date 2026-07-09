namespace FollowHer.Core.Events.Events
{
    public class TickEvent
    {
        public bool IsActive { get; set; }

        public TickEvent(bool isActive)
        {
            IsActive = isActive;
        }
    }
}
