using System;
using System.Collections.Generic;

namespace FollowHer.Features.Following.Pathfinding;

public static class GridPathfinder
{
    // FollowHer's LineOfSight grid encodes 0=Walkable ... 3=High obstacle, 4=Blocking, 5=Special;
    // existing LOS checks already treat >= 4 as blocking (see Utils/LineOfSight.TARGET_LAYER_VALUE).
    private const int BlockingThreshold = 4;

    private class Node
    {
        public int X;
        public int Y;
        public int G;
        public int H;
        public int F => G + H;
        public Node Parent;

        public Node(int x, int y, int g, int h, Node parent = null)
        {
            X = x; Y = y; G = g; H = h; Parent = parent;
        }
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

    private static bool IsWalkable(int[][] grid, int x, int y)
    {
        return InBounds(grid, x, y) && grid[y][x] >= 0 && grid[y][x] < BlockingThreshold;
    }

    /// <summary>
    /// Computes a path from start to target on the supplied grid. Returns grid points from start
    /// (excluded) to target (included), or null if no path is found.
    /// </summary>
    public static List<(int x, int y)> AStar(int[][] grid, (int x, int y) start, (int x, int y) target, int maxNodesToExplore = 20000)
    {
        if (grid == null) return null;
        if (!IsWalkable(grid, target.x, target.y)) return null;
        if (!IsWalkable(grid, start.x, start.y)) return null;

        var open = new List<Node>();
        var closed = new HashSet<(int, int)>();
        var nodeMap = new Dictionary<(int, int), Node>();

        var startNode = new Node(start.x, start.y, 0, Heuristic(start.x, start.y, target.x, target.y));
        open.Add(startNode);
        nodeMap[(start.x, start.y)] = startNode;

        var nodesExamined = 0;

        while (open.Count > 0 && nodesExamined < maxNodesToExplore)
        {
            var bestI = 0;
            for (var i = 1; i < open.Count; i++)
                if (open[i].F < open[bestI].F || (open[i].F == open[bestI].F && open[i].H < open[bestI].H))
                    bestI = i;

            var cur = open[bestI];
            open.RemoveAt(bestI);
            nodesExamined++;

            if (cur.X == target.x && cur.Y == target.y)
            {
                var path = new List<(int x, int y)>();
                var p = cur;
                while (p != null && !(p.X == start.x && p.Y == start.y))
                {
                    path.Add((p.X, p.Y));
                    p = p.Parent;
                }
                path.Reverse();
                return path;
            }

            closed.Add((cur.X, cur.Y));

            foreach (var n in Neighbors)
            {
                var nx = cur.X + n.dx;
                var ny = cur.Y + n.dy;
                if (!IsWalkable(grid, nx, ny)) continue;

                // Prevent cutting corners when diagonal if either adjacent orthogonal is blocked.
                if (Math.Abs(n.dx) + Math.Abs(n.dy) == 2)
                {
                    if (!IsWalkable(grid, cur.X + n.dx, cur.Y) || !IsWalkable(grid, cur.X, cur.Y + n.dy))
                        continue;
                }

                if (closed.Contains((nx, ny))) continue;

                var tentativeG = cur.G + n.cost;
                var key = (nx, ny);

                if (!nodeMap.TryGetValue(key, out var neighborNode))
                {
                    neighborNode = new Node(nx, ny, tentativeG, Heuristic(nx, ny, target.x, target.y), cur);
                    nodeMap[key] = neighborNode;
                    open.Add(neighborNode);
                }
                else if (tentativeG < neighborNode.G)
                {
                    neighborNode.G = tentativeG;
                    neighborNode.Parent = cur;
                }
            }
        }

        return null;
    }
}
