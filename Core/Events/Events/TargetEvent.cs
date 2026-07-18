using FollowHer.Features.Targeting.EntityInformation;

namespace FollowHer.Core.Events.Events
{
    public class TargetChangedEvent
    {
        public EntityInfo OldTarget { get; set; }
        public EntityInfo NewTarget { get; set; }
    }
}
