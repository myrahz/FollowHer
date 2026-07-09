using System;
using System.Numerics;
using ExileCore.PoEMemory.Elements;

namespace FollowHer.Features.Following;

public enum FollowTaskType
{
    Movement,
    Transition,
    FollowLeaderPortal
}

public class FollowTaskNode
{
    public Vector3 WorldPosition { get; set; }
    public FollowTaskType Type { get; }
    public int Bounds { get; set; }
    public int AttemptCount { get; set; }
    public LabelOnGround PortalLabel { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.MinValue;

    public FollowTaskNode(Vector3 worldPosition, int bounds, FollowTaskType type = FollowTaskType.Movement)
    {
        WorldPosition = worldPosition;
        Bounds = bounds;
        Type = type;
    }

    public FollowTaskNode(LabelOnGround portalLabel, int bounds, FollowTaskType type)
    {
        PortalLabel = portalLabel;
        WorldPosition = portalLabel?.ItemOnGround?.PosNum ?? Vector3.Zero;
        Bounds = bounds;
        Type = type;
    }
}
