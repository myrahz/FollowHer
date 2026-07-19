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

    public FollowTaskContext(GameController gameController, Entity player, FollowManager manager, Entity leaderEntity)
    {
        GameController = gameController;
        Player = player;
        Manager = manager;
        LeaderEntity = leaderEntity;
    }
}
