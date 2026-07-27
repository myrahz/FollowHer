using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace FollowHer.Features.Following.Tasks;

/// <summary>What an IFollowTask needs each tick - the shared game/session state plus a handle
/// back to FollowManager's movement/click/logging helpers, so tasks don't duplicate them.</summary>
public class FollowTaskContext
{
    public GameController GameController { get; }
    public Entity Player { get; }
    public FollowManager Manager { get; }
    public Entity LeaderEntity { get; }

    /// <summary>Where to measure "is the leader near this quest" from: the live leader position
    /// when they're visible, otherwise where they were last seen. Null only if the leader has never
    /// been seen this zone. Using the last-seen position when the entity is gone lets quest tasks
    /// still fire for a quest the leader was standing next to when they stepped through a transition
    /// - so the follower grabs it BEFORE teleporting after them (it can't acquire the quest state
    /// from the other side of the transition).</summary>
    public Vector3? LeaderProximityAnchor { get; }

    public FollowTaskContext(GameController gameController, Entity player, FollowManager manager,
        Entity leaderEntity, Vector3? leaderProximityAnchor)
    {
        GameController = gameController;
        Player = player;
        Manager = manager;
        LeaderEntity = leaderEntity;
        LeaderProximityAnchor = leaderProximityAnchor;
    }
}
