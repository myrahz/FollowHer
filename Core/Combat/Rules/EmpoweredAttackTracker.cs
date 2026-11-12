using System;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;

namespace FollowHer.Core.Combat.Rules;

/// <summary>Remembers when the player last spent an empowered (exerted) attack. Warcries expose
/// their remaining count as a "display_num_empowered_attacks" buff, so a drop in the summed charges
/// between two ticks means an empowered attack just went off. Rule contexts are rebuilt per
/// evaluation, so this lives in static state that the combat routine updates once per tick.</summary>
public static class EmpoweredAttackTracker
{
    private const string BuffName = "display_num_empowered_attacks";

    private static int _lastCharges;
    private static DateTime _lastSpent = DateTime.MinValue;

    public static void Update(GameController gameController)
    {
        var player = gameController?.Player;
        if (player == null || !player.TryGetComponent<Buffs>(out var buffs)) return;

        var charges = buffs.BuffsList?.Where(b => b.Name == BuffName).Sum(b => b.BuffCharges) ?? 0;

        // Charges falling means one was spent; rising is a fresh cry, which is not an attack.
        if (charges < _lastCharges)
        {
            _lastSpent = DateTime.Now;
        }

        _lastCharges = charges;
    }

    public static bool SpentWithin(float seconds) =>
        (DateTime.Now - _lastSpent).TotalSeconds <= seconds;

    public static void Reset()
    {
        _lastCharges = 0;
        _lastSpent = DateTime.MinValue;
    }
}
