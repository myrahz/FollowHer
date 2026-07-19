using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace FollowHer.Features.Following;

public static class LeaderLocator
{
    public static Entity FindLeaderEntity(GameController gameController, string leaderName)
    {
        if (string.IsNullOrWhiteSpace(leaderName)) return null;

        try
        {
            var localPlayerAddress = gameController?.Player?.Address;

            return gameController?.EntityListWrapper?.ValidEntitiesByType?.GetValueOrDefault(EntityType.Player)?
                .FirstOrDefault(x => x != null && x.Address != localPlayerAddress &&
                    string.Equals(x.GetComponent<Player>()?.PlayerName,
                        leaderName,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[LeaderLocator] Error finding leader entity: {ex.Message}");
            return null;
        }
    }
}
