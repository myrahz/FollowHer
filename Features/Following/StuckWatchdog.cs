using System;
using System.Numerics;

namespace FollowHer.Features.Following;

/// <summary>Notices a character that is supposed to be going somewhere and isn't. It keeps an anchor
/// position and the time it was set; moving more than <see cref="ProgressUnits"/> from it counts as
/// progress and resets the clock. How long the character has been pinned is then what the caller
/// escalates on - each escalation stage fires once per episode (<see cref="Reached"/>).</summary>
public sealed class StuckWatchdog
{
    /// <summary>Less movement than this counts as standing still. Walking and skills both move
    /// the character far more than this in a second; a charge into a wall or a monster does not.</summary>
    public const float ProgressUnits = 40f;

    private Vector3 _anchor;
    private DateTime _since;
    private bool _anchored;

    /// <summary>The highest escalation stage already taken in this episode.</summary>
    public int Stage { get; private set; }

    public void Reset()
    {
        _anchored = false;
        Stage = 0;
    }

    /// <summary>Records where the character is now and returns how many seconds it has been within
    /// <see cref="ProgressUnits"/> of the same spot.</summary>
    public double Observe(Vector3 position)
    {
        var now = DateTime.Now;
        if (!_anchored || Vector3.Distance(position, _anchor) > ProgressUnits)
        {
            _anchor = position;
            _since = now;
            _anchored = true;
            Stage = 0;
            return 0;
        }

        return (now - _since).TotalSeconds;
    }

    /// <summary>True once per episode when <paramref name="seconds"/> reaches
    /// <paramref name="threshold"/> and this stage hasn't been taken yet.</summary>
    public bool Reached(double seconds, double threshold, int stage)
    {
        if (seconds < threshold || Stage >= stage) return false;
        Stage = stage;
        return true;
    }
}
