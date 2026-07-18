namespace FollowHer.Features.Following.Tasks;

/// <summary>
/// A side-task FollowManager can run instead of its normal movement/attack fallback (e.g. picking
/// up a nearby quest item). Checked once per tick, in order, before any follow/movement decision.
/// </summary>
public interface IFollowTask
{
    /// <summary>
    /// Returns true if this task found something to do and acted on it this tick (movement or
    /// attack should be skipped this tick). Returns false if there's nothing for it to do right
    /// now, so FollowManager should fall through to the next task or its normal behavior.
    /// </summary>
    bool TryExecute(FollowTaskContext context);

    /// <summary>Clears any internal state - called on area change or when Follow stops/resets.</summary>
    void Reset();
}
