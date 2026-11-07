using System;
using System.Collections.Generic;
using System.Numerics;

namespace FollowHer.Features.Following.Pathfinding;

public static class GridPathfinder
{
    // Per-cell search state, kept in flat arrays indexed by (y * width + x) rather than in objects in
    // a Dictionary keyed by (x,y). The search touches ~8 cells per expansion, so tuple hashing and a
    // Node allocation per cell dominated the cost - measured at ~42ms for a 40k-node search, against
    // ~5ms for this. That matters because A* runs on the game thread: the budget that decides how far
    // a route can reach is really a frame-time budget.
    //
    // The arrays are reused between calls (allocated once per area size). Instead of clearing them,
    // each run bumps a generation counter and a cell's state only counts when its stamp matches -
    // clearing 350k entries per call would have cost more than the search. [ThreadStatic] so this
    // stays safe if a search is ever run off the game thread.
    [ThreadStatic] private static int[] _gScore;
    [ThreadStatic] private static int[] _parent;
    [ThreadStatic] private static int[] _seenStamp;
    [ThreadStatic] private static int[] _closedStamp;
    [ThreadStatic] private static int _generation;
    [ThreadStatic] private static PriorityQueue<int, (int f, int h)> _open;

    // Wall-proximity cost per cell, computed the first time a search touches the cell and stamped with
    // the generation like the rest (so it's recomputed per search - the grid may have changed).
    [ThreadStatic] private static int[] _penaltyStamp;
    [ThreadStatic] private static int[] _penalty;

    // Equals the current generation when the cell was last reached by a jump, anything else when by
    // walking - lets BuildPath report which steps of the route are jumps.
    [ThreadStatic] private static int[] _jumpStamp;

    /// <summary>What A* needs to consider jumping over obstacles. A jump is an extra edge from a cell
    /// beside an obstacle to the first walkable cell past it, along one of 16
    /// rays: every cell the ray crosses must be see-through on the targeting layer (a wall stops it),
    /// and the part it flies over must be unwalkable on the walkable layer (otherwise it's just a
    /// walk).</summary>
    public sealed class JumpOptions
    {
        public int[][] TargetingGrid;

        /// <summary>Longest jump, in grid cells.</summary>
        public int MaxCells;

        /// <summary>Cost of one jump on the 10-per-straight-step scale, on top of its length.</summary>
        public int Cost;

        /// <summary>Landing cells that already failed in practice - never offered again.</summary>
        public HashSet<(int x, int y)> BannedLandings;
    }

    private const int JumpDirectionCount = 16;
    private const int JumpMinCells = 2;
    private const int JumpTakeoffReach = 2;
    private const int JumpFirstObstacleWithin = 3;
    private const int TargetingBlockValue = 4;

    private static readonly (float dx, float dy)[] JumpDirections = BuildJumpDirections();

    private static (float dx, float dy)[] BuildJumpDirections()
    {
        var dirs = new (float dx, float dy)[JumpDirectionCount];
        for (var i = 0; i < JumpDirectionCount; i++)
        {
            var angle = i * 2 * Math.PI / JumpDirectionCount;
            dirs[i] = ((float)Math.Cos(angle), (float)Math.Sin(angle));
        }

        return dirs;
    }

    private static bool NearBlocked(int[][] grid, int x, int y, int radius)
    {
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (!IsWalkable(grid, x + dx, y + dy)) return true;
            }
        }

        return false;
    }

    /// <summary>Walks one ray from a takeoff cell and reports where a jump along it would land: the
    /// first walkable cell after an unwalkable stretch, provided nothing on the way blocks the
    /// targeting layer and the obstacle starts close to the takeoff. Null when there's no such cell
    /// within range.</summary>
    private static (int x, int y, int steps)? FindJumpLanding(int[][] grid, JumpOptions jumps, int x, int y, int direction)
    {
        var (dx, dy) = JumpDirections[direction];
        var crossed = false;
        var lastX = int.MinValue;
        var lastY = int.MinValue;

        for (var s = 1; s <= jumps.MaxCells; s++)
        {
            var px = x + (int)Math.Round(dx * s);
            var py = y + (int)Math.Round(dy * s);
            if (px == lastX && py == lastY) continue;
            lastX = px;
            lastY = py;

            // A wall (or the edge of the map) stops the jump; a gap or low obstacle doesn't.
            if (!InBounds(jumps.TargetingGrid, px, py) || jumps.TargetingGrid[py][px] >= TargetingBlockValue) return null;

            if (!IsWalkable(grid, px, py))
            {
                crossed = true;
                continue;
            }

            if (!crossed)
            {
                // Still on ground before any obstacle - the jump has to start at the obstacle, not
                // somewhere far back along the ray.
                if (s >= JumpFirstObstacleWithin) return null;
                continue;
            }

            if (s < JumpMinCells) return null;

            // Land with ground ahead as well, not on the last sliver of a ledge.
            var nx = x + (int)Math.Round(dx * (s + 1));
            var ny = y + (int)Math.Round(dy * (s + 1));
            if (!IsWalkable(grid, nx, ny)) return null;

            return (px, py, s);
        }

        return null;
    }

    // Extra cost (on the 10-per-straight-step scale) per cell of encroachment into the clearance
    // radius. A cell touching a wall with a 3-cell radius costs 10 + 3*8 = 34, so the route will take a
    // detour of a few cells to stay off the wall, but a narrow gap is still passable.
    private const int ClearancePenaltyPerStep = 8;

    private static void EnsureBuffers(int cellCount)
    {
        if (_gScore != null && _gScore.Length >= cellCount)
        {
            _generation++;
            return;
        }

        _gScore = new int[cellCount];
        _parent = new int[cellCount];
        _seenStamp = new int[cellCount];
        _closedStamp = new int[cellCount];
        _penaltyStamp = new int[cellCount];
        _penalty = new int[cellCount];
        _jumpStamp = new int[cellCount];
        _generation = 1;
    }

    /// <summary>How much a cell should cost for being near a blocked cell: 0 when nothing blocked lies
    /// within <paramref name="radius"/> cells, rising the closer the nearest blocked cell is. Rings are
    /// scanned outward so a cell hard against a wall exits after the first one.</summary>
    private static int ClearancePenalty(int[][] grid, int x, int y, int radius)
    {
        for (var r = 1; r <= radius; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    if (!IsWalkable(grid, x + dx, y + dy)) return (radius - r + 1) * ClearancePenaltyPerStep;
                }
            }
        }

        return 0;
    }

    // 8-directional neighbors
    private static readonly (int dx, int dy, int cost)[] Neighbors =
    {
        (-1, 0, 10), (1, 0, 10), (0, -1, 10), (0, 1, 10),
        (-1, -1, 14), (-1, 1, 14), (1, -1, 14), (1, 1, 14)
    };

    private static int Heuristic(int sx, int sy, int tx, int ty)
    {
        var dx = Math.Abs(sx - tx);
        var dy = Math.Abs(sy - ty);
        var min = Math.Min(dx, dy);
        var max = Math.Max(dx, dy);
        return 14 * min + 10 * (max - min);
    }

    private static bool InBounds(int[][] grid, int x, int y)
    {
        if (grid == null) return false;
        if (y < 0 || y >= grid.Length) return false;
        if (grid[y] == null) return false;
        if (x < 0 || x >= grid[y].Length) return false;
        return true;
    }

    // This grid is always the Walkable layer (RawPathfindingData) - every caller passes
    // _lineOfSight.GetGrid(LineOfSightDataType.Walkable). Confirmed against Radar's own working
    // pathfinder (PathFinder.cs, constructed with pathable values {1,2,3,4,5}) reading the exact
    // same raw source: value 0 is the only blocking value, everything else is passable.
    private static bool IsWalkable(int[][] grid, int x, int y)
    {
        return InBounds(grid, x, y) && grid[y][x] != 0;
    }

    /// <summary>
    /// Computes a path from start to target on the supplied grid. Returns grid points from start
    /// (excluded) to target (included), or null if no path is found.
    /// </summary>
    public static List<(int x, int y)> AStar(int[][] grid, (int x, int y) start, (int x, int y) target, int maxNodesToExplore = 20000)
    {
        return AStar(grid, start, target, maxNodesToExplore, false, out _);
    }

    /// <summary>
    /// As above, but <paramref name="allowPartialPath"/> asks for a best-effort route when the search
    /// runs out of node budget before reaching the target: the path to the explored cell closest to
    /// it, with <paramref name="isPartial"/> set. That distinguishes the two ways a search can fail,
    /// which mean opposite things:
    ///   - the open set empties     -> the target is genuinely unreachable (walled off), return null
    ///   - the node budget runs out -> the target is just far away, the route so far is still good
    /// Without this, anything beyond the budget's reach reports "no path" exactly like a wall does,
    /// which is why a distant target Radar happily draws a line to used to fail here: Radar floods
    /// the whole area from the target (uncapped, on a background thread, cached as a distance field),
    /// while this runs per-call on the game thread and must stay bounded. Callers that ask a
    /// yes/no reachability question (dash shortcut, leader-jump check) deliberately don't opt in - a
    /// truncated route would answer "reachable" when nothing of the sort was established.
    /// </summary>
    /// <param name="wallClearanceCells">Soft preference for staying this many cells off walls: cells
    /// within it cost extra, scaled by how close they are. 0 = plain shortest path. It never forbids a
    /// cell, so a gap narrower than the margin is still taken when it's the only way.</param>
    /// <param name="jumps">When set, the search may also jump over obstacles (see
    /// <see cref="JumpOptions"/>). The cells of the returned path that were reached by a jump are
    /// added to <paramref name="jumpLandings"/>; the cell before each one (or the start) is where the
    /// jump is taken from.</param>
    public static List<(int x, int y)> AStar(int[][] grid, (int x, int y) start, (int x, int y) target,
        int maxNodesToExplore, bool allowPartialPath, out bool isPartial, int wallClearanceCells = 0,
        JumpOptions jumps = null, HashSet<(int x, int y)> jumpLandings = null)
    {
        isPartial = false;
        if (grid == null) return null;
        if (!IsWalkable(grid, target.x, target.y)) return null;
        if (!IsWalkable(grid, start.x, start.y)) return null;

        var height = grid.Length;

        // Widest row, not row 0's width: the flat index is y * width + x, so a row longer than the
        // one used to size the stride would alias onto the next row's cells.
        var width = 0;
        for (var y = 0; y < height; y++)
        {
            if (grid[y] != null && grid[y].Length > width) width = grid[y].Length;
        }

        if (width == 0) return null;

        EnsureBuffers(width * height);
        var generation = _generation;

        // A binary heap rather than a linear scan for the cheapest open cell: the old scan was O(n)
        // per expansion, which is what kept the node budget too low to reach across a zone.
        var open = _open ??= new PriorityQueue<int, (int f, int h)>();
        open.Clear();

        var startIndex = start.y * width + start.x;
        var targetIndex = target.y * width + target.x;

        var startH = Heuristic(start.x, start.y, target.x, target.y);
        _gScore[startIndex] = 0;
        _parent[startIndex] = -1;
        _seenStamp[startIndex] = generation;
        open.Enqueue(startIndex, (startH, startH));

        // Closest cell to the target seen so far - the fallback when the budget runs out.
        var bestIndex = startIndex;
        var bestH = startH;
        var nodesExamined = 0;

        while (nodesExamined < maxNodesToExplore && open.TryDequeue(out var currentIndex, out _))
        {
            // The heap can hold several entries for one cell (a cheaper route to it was found after
            // it was queued); only the first, cheapest one counts.
            if (_closedStamp[currentIndex] == generation) continue;
            _closedStamp[currentIndex] = generation;
            nodesExamined++;

            var cx = currentIndex % width;
            var cy = currentIndex / width;

            var currentH = Heuristic(cx, cy, target.x, target.y);
            if (currentH < bestH)
            {
                bestH = currentH;
                bestIndex = currentIndex;
            }

            if (currentIndex == targetIndex) return BuildPath(currentIndex, startIndex, width, jumpLandings);

            var currentG = _gScore[currentIndex];

            foreach (var n in Neighbors)
            {
                var nx = cx + n.dx;
                var ny = cy + n.dy;
                if (!IsWalkable(grid, nx, ny)) continue;

                // Prevent cutting corners when diagonal if either adjacent orthogonal is blocked.
                if (Math.Abs(n.dx) + Math.Abs(n.dy) == 2)
                {
                    if (!IsWalkable(grid, cx + n.dx, cy) || !IsWalkable(grid, cx, cy + n.dy))
                        continue;
                }

                var neighborIndex = ny * width + nx;
                if (_closedStamp[neighborIndex] == generation) continue;

                var stepCost = n.cost;
                if (wallClearanceCells > 0)
                {
                    if (_penaltyStamp[neighborIndex] != generation)
                    {
                        _penaltyStamp[neighborIndex] = generation;
                        _penalty[neighborIndex] = ClearancePenalty(grid, nx, ny, wallClearanceCells);
                    }

                    stepCost += _penalty[neighborIndex];
                }

                var tentativeG = currentG + stepCost;
                if (_seenStamp[neighborIndex] == generation && tentativeG >= _gScore[neighborIndex]) continue;

                _seenStamp[neighborIndex] = generation;
                _gScore[neighborIndex] = tentativeG;
                _parent[neighborIndex] = currentIndex;
                _jumpStamp[neighborIndex] = 0;

                var h = Heuristic(nx, ny, target.x, target.y);
                // Re-queued at the improved priority when a cheaper route is found; the closed check
                // above discards the stale entry.
                open.Enqueue(neighborIndex, (tentativeG + h, h));
            }

            // Jump edges: only worth looking for beside an obstacle, which keeps the rays off open
            // ground where there is nothing to jump.
            if (jumps != null && jumps.MaxCells >= JumpMinCells && NearBlocked(grid, cx, cy, JumpTakeoffReach))
            {
                for (var direction = 0; direction < JumpDirectionCount; direction++)
                {
                    var landing = FindJumpLanding(grid, jumps, cx, cy, direction);
                    if (landing == null) continue;

                    var (lx, ly, steps) = landing.Value;
                    if (jumps.BannedLandings != null && jumps.BannedLandings.Contains((lx, ly))) continue;

                    var landingIndex = ly * width + lx;
                    if (_closedStamp[landingIndex] == generation) continue;

                    // 11 per step rather than 10: the heuristic is octile, which overestimates a
                    // diagonal ray's true length slightly, and an inconsistent edge cost would let a
                    // jump look cheaper than it can be.
                    var jumpG = currentG + jumps.Cost + steps * 11;
                    if (_seenStamp[landingIndex] == generation && jumpG >= _gScore[landingIndex]) continue;

                    _seenStamp[landingIndex] = generation;
                    _gScore[landingIndex] = jumpG;
                    _parent[landingIndex] = currentIndex;
                    _jumpStamp[landingIndex] = generation;

                    var jumpH = Heuristic(lx, ly, target.x, target.y);
                    open.Enqueue(landingIndex, (jumpG + jumpH, jumpH));
                }
            }
        }

        // open.Count > 0 means we stopped on the budget rather than running out of reachable cells.
        if (allowPartialPath && open.Count > 0 && bestIndex != startIndex)
        {
            isPartial = true;
            return BuildPath(bestIndex, startIndex, width, jumpLandings);
        }

        return null;
    }

    private static List<(int x, int y)> BuildPath(int endIndex, int startIndex, int width,
        HashSet<(int x, int y)> jumpLandings)
    {
        jumpLandings?.Clear();

        var path = new List<(int x, int y)>();
        var current = endIndex;
        while (current != -1 && current != startIndex)
        {
            var cell = (current % width, current / width);
            path.Add(cell);
            if (jumpLandings != null && _jumpStamp[current] == _generation) jumpLandings.Add(cell);
            current = _parent[current];
        }

        path.Reverse();
        return path;
    }

    /// <summary>Whether a grid cell can be stood on. Exposed so callers can nudge a destination onto
    /// walkable ground before asking for a path (a target cell that isn't walkable fails instantly,
    /// however close it is).</summary>
    public static bool IsCellWalkable(int[][] grid, int x, int y) => IsWalkable(grid, x, y);

    // Checks a band of parallel lines (centerline plus +/-marginCells offsets perpendicular to
    // travel) rather than a single ray, so a skill that travels in a straight line and collides
    // with obstacles (unlike Move, which routes around them) doesn't clip a corner with its
    // hitbox. Uses this file's own IsWalkable definition (confirmed against Radar's working
    // pathfinder: only value 0 blocks on the Walkable grid).
    public static bool HasCorridorClearance(int[][] grid, Vector2 from, Vector2 to, float marginCells) =>
        HasCorridorClearance(grid, from, to, marginCells, out _);

    public static bool HasCorridorClearance(int[][] grid, Vector2 from, Vector2 to, float marginCells, out string failureReason)
    {
        failureReason = null;
        if (grid == null)
        {
            failureReason = "grid is null";
            return false;
        }

        var direction = to - from;
        if (direction.LengthSquared() < 0.0001f) return true;

        var normal = Vector2.Normalize(new Vector2(-direction.Y, direction.X));
        var offsets = new[] { 0f, marginCells, -marginCells };

        foreach (var offset in offsets)
        {
            var offsetVector = normal * offset;
            if (!IsLineWalkable(grid, from + offsetVector, to + offsetVector, out var blockX, out var blockY, out var blockValue))
            {
                failureReason = $"offset {offset:F1} blocked at grid ({blockX},{blockY}) value={blockValue}";
                return false;
            }
        }

        return true;
    }

    private static bool IsLineWalkable(int[][] grid, Vector2 from, Vector2 to, out int blockX, out int blockY, out int blockValue)
    {
        blockX = 0;
        blockY = 0;
        blockValue = -1;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));

        // Same cell after rounding - nothing travelled, so nothing to collide with.
        if (steps <= 0) return true;

        var stepX = dx / steps;
        var stepY = dy / steps;

        // Deliberately skip the first (i=0) and last (i=steps) sampled cells. Those are where the
        // player and the target entity stand, and entities routinely occupy cells the pathfinding
        // grid marks as blocked (value 0) - hard against a wall, in a doorway, on a terrain edge. A
        // ground dash starting or ending on such a cell isn't a collision; only the cells it
        // travels THROUGH are. Without this the check failed at "offset 0.0 ... value 0" on the
        // player's own cell every tick, so a ground-dash skill could never be used (and no clearance
        // margin could fix it, since it's the centerline's start cell, not hitbox width).
        for (var i = 1; i < steps; i++)
        {
            var x = (int)Math.Round(from.X + stepX * i);
            var y = (int)Math.Round(from.Y + stepY * i);
            if (!IsWalkable(grid, x, y))
            {
                blockX = x;
                blockY = y;
                blockValue = InBounds(grid, x, y) ? grid[y][x] : -2;
                return false;
            }
        }

        return true;
    }
}
