using System.Numerics;

namespace FollowHer.Features.Navigation;

/// <summary>One navigable point of interest in the current area, as Radar sees it: a clustered tile
/// location (waypoint, transition, league mechanic, boss room, ...) with the label Radar shows on its
/// map. <see cref="GridPos"/> is a grid cell, the same coordinate space the pathfinder works in.</summary>
public readonly record struct NavTarget(string Label, string Pattern, Vector2 GridPos)
{
    public override string ToString() => Label;
}
