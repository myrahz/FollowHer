using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;

namespace FollowHer.Features.Following;

public class PartyMemberInfo
{
    public string PlayerName { get; set; } = string.Empty;
    public Element Element { get; set; }
    public Element TpButton { get; set; }
    public string ZoneName { get; set; } = string.Empty;
}

public static class PartyPanelScanner
{
    public static List<PartyMemberInfo> GetPartyMembers(GameController gameController)
    {
        var members = new List<PartyMemberInfo>();

        try
        {
            var partyElementList = gameController?.IngameState?.IngameUi?.PartyElement?.Children?[0]?.Children?[0]?.Children;
            if (partyElementList == null) return members;

            foreach (var partyElement in partyElementList)
            {
                if (partyElement?.Children == null) continue;

                // The party panel's "swirly" teleport-to-player button is normally child index 2, but
                // shifts to index 3 when a 4th child (the member's current zone name) is present.
                members.Add(new PartyMemberInfo
                {
                    PlayerName = partyElement.Children[0]?.Text,
                    Element = partyElement,
                    TpButton = partyElement.Children[partyElement.ChildCount == 4 ? 3 : 2],
                    ZoneName = partyElement.ChildCount == 4
                        ? partyElement.Children[2].Text
                        : gameController?.Area.CurrentArea.DisplayName
                });
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[PartyPanelScanner] Error reading party panel: {ex.Message}");
        }

        return members;
    }

    public static PartyMemberInfo GetLeaderPartyMember(GameController gameController, string leaderName)
    {
        if (string.IsNullOrWhiteSpace(leaderName)) return null;

        try
        {
            return GetPartyMembers(gameController)
                .FirstOrDefault(x => string.Equals(x.PlayerName, leaderName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[PartyPanelScanner] Error finding leader party member: {ex.Message}");
            return null;
        }
    }
}
