using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FollowHer.Features.Following.Pathfinding;

// usage: <grid.bin> <startX> <startY> <targets.txt>
var world = World.Load(args[0]);
(int x, int y) start = (int.Parse(args[1]), int.Parse(args[2]));

// ------------------------------------------------------------------ routes: player -> every target, plus target <-> target
var targets = File.ReadAllLines(args[3]).Select(l => l.Split('|')).Select(p => (name: p[0], cell: world.Snap((int.Parse(p[1]), int.Parse(p[2])))))
    .Where(t => t.cell.HasValue).GroupBy(t => t.cell.Value).Select(g => (name: string.Join("/", g.Select(t => t.name)), cell: g.Key)).ToList();
var points = new List<(string name, (int x, int y) cell)> { ("you", start) };
points.AddRange(targets);
var routes = new List<Route>();
for (var i = 1; i < points.Count; i++) routes.Add(new Route(points[0].name, points[i].name, points[0].cell, points[i].cell));
var rng = new Random(5);
var pairs = (from a in Enumerable.Range(1, points.Count - 1) from b in Enumerable.Range(1, points.Count - 1) where a != b select (a, b)).OrderBy(_ => rng.Next()).Take(28);
foreach (var (a, b) in pairs) routes.Add(new Route(points[a].name, points[b].name, points[a].cell, points[b].cell));
Console.WriteLine($"zone {world.W}x{world.H}; {targets.Count} distinct targets; {routes.Count} routes (you->each target, plus random target->target)");
Console.WriteLine($"assumptions: walking {Sim.WalkSpeed:F0} u/s, one Leap Slam takes {Sim.LeapSeconds:F2}s and has no cooldown, Frostblink cooldown {Sim.FrostblinkCooldown:F0}s, 50 ms input tick");

// ------------------------------------------------------------------ the grid of settings to try
var yours = new Cfg(WallClearance: 0, Straightness: 10, MaxWaypointU: 699, KeepWithinU: 110, MinDistU: 326, MaxDistU: 2158, ClearMargin: 0.5f, JumpMode: 0);
var combos = new List<Cfg>();
foreach (var st in new[] { 0, 5, 10 })
foreach (var wc in new[] { 0, 2, 3, 5 })
foreach (var wp in new[] { 300, 700, 1200 })
foreach (var min in new[] { 100, 200, 326 })
foreach (var cm in new[] { 0.5f, 1.5f })
foreach (var jm in new[] { 0, 7, 20 })
foreach (var kw in new[] { 110, 200 })
    combos.Add(yours with { Straightness = st, WallClearance = wc, MaxWaypointU = wp, MinDistU = min, ClearMargin = cm, JumpMode = jm, KeepWithinU = kw });
if (!combos.Contains(yours)) combos.Add(yours);
Console.WriteLine($"{combos.Count} setting combinations x {routes.Count} routes = {combos.Count * routes.Count} simulated trips");

// ------------------------------------------------------------------ plan every (route, plan-settings) once; everything else is cheap
var sw = Stopwatch.StartNew();
var planKeys = combos.Select(c => c.PlanKey).Distinct().ToList();
var rawPlans = new ConcurrentDictionary<(PlanKey, int), RawPlan>();
Parallel.ForEach(planKeys.SelectMany(k => Enumerable.Range(0, routes.Count).Select(r => (k, r))), new ParallelOptions { MaxDegreeOfParallelism = 4 }, t =>
{
    rawPlans[(t.k, t.r)] = Sim.PlanRaw(world, routes[t.r], t.k);
});
Console.WriteLine($"planned {rawPlans.Count} routes in {sw.Elapsed.TotalSeconds:F0}s ({rawPlans.Values.Count(p => p.Raw == null)} unreachable)");

var smoothed = new ConcurrentDictionary<(PlanKey, int, int), Plan>();
Plan GetPlan(Cfg c, int r) => smoothed.GetOrAdd((c.PlanKey, c.MaxWaypointU, r), _ => Sim.Smooth(world, rawPlans[(c.PlanKey, r)], c));

// ------------------------------------------------------------------ simulate
var results = new ConcurrentBag<(Cfg cfg, double meanTime, double worst, double skillShare, int failed, double blink, double leap)>();
sw.Restart();
var reachable = Enumerable.Range(0, routes.Count).Where(r => planKeys.All(k => rawPlans[(k, r)].Raw != null)).ToList();
Parallel.ForEach(combos, new ParallelOptions { MaxDegreeOfParallelism = 6 }, c =>
{
    double sum = 0, worst = 0, skillD = 0, totalD = 0, nB = 0, nL = 0; var failed = 0;
    foreach (var r in reachable)
    {
        var res = Sim.Run(world, c, GetPlan(c, r), routes[r]);
        if (!res.Ok) failed++;
        sum += res.Time; worst = Math.Max(worst, res.Time); skillD += res.DLeap + res.DBlink; totalD += res.DWalk + res.DLeap + res.DBlink; nB += res.NBlink; nL += res.NLeap;
    }
    results.Add((c, sum / reachable.Count, worst, totalD > 0 ? skillD / totalD : 0, failed, nB / reachable.Count, nL / reachable.Count));
});
Console.WriteLine($"simulated {combos.Count * reachable.Count} trips over {reachable.Count} reachable routes in {sw.Elapsed.TotalSeconds:F0}s\n");

var all = results.OrderBy(r => r.meanTime).ToList();
string Desc(Cfg c) => $"straight {c.Straightness,2} | wall clr {c.WallClearance} | max waypoint {c.MaxWaypointU,4}u | min hop {c.MinDistU,3}u | clr margin {c.ClearMargin:F1} | jump {(c.JumpMode == 0 ? "off      " : $"on (cost {c.JumpMode,2})")} | keep-within {c.KeepWithinU}";
var mine = all.First(r => r.cfg == yours);
Console.WriteLine($"YOUR SETTINGS    : mean {mine.meanTime:F1}s  worst {mine.worst:F1}s  skills carry {100 * mine.skillShare:F0}% of distance  (rank {all.IndexOf(mine) + 1} of {all.Count})");
Console.WriteLine($"                   {Desc(yours)}\n");
Console.WriteLine("TOP 8 COMBINATIONS (lowest mean trip time):");
foreach (var r in all.Take(8))
    Console.WriteLine($"  {r.meanTime,5:F1}s  worst {r.worst,5:F1}s  skills {100 * r.skillShare,3:F0}%  leaps/trip {r.leap,4:F1}  blinks/trip {r.blink,3:F1}   {Desc(r.cfg)}");
var worstRes = all.Last();
Console.WriteLine($"\nWORST combination: {worstRes.meanTime:F1}s   {Desc(worstRes.cfg)}");

Console.WriteLine("\nWHAT MATTERS - mean trip time over every combination that uses each value (lower = better, spread = how much the setting matters):");
void Effect(string name, Func<Cfg, object> key)
{
    var groups = all.GroupBy(r => key(r.cfg)).Select(g => (v: g.Key, mean: g.Average(r => r.meanTime), best: g.Min(r => r.meanTime), failed: g.Sum(r => r.failed), n: g.Count())).OrderBy(g => g.v.ToString()).ToList();
    Console.WriteLine($"  {name,-18} " + string.Join("  ", groups.Select(g => $"{g.v}: {g.mean,5:F1}s best {g.best:F1} stuck {g.failed}")) + $"   spread {groups.Max(g => g.mean) - groups.Min(g => g.mean):F1}s");
}
Effect("Wall Clearance", c => c.WallClearance);
Effect("Min hop distance", c => c.MinDistU);
Effect("Jump planning", c => c.JumpMode == 0 ? "off" : "on" + c.JumpMode);
Effect("Clearance margin", c => c.ClearMargin.ToString("F1"));
Effect("Max waypoint", c => c.MaxWaypointU);
Effect("Path straightness", c => c.Straightness);
Effect("Keep-within", c => c.KeepWithinU);

// per-route comparison of yours vs the best
var best = all.First().cfg;
Console.WriteLine($"\nPER ROUTE, your settings vs the best combination (seconds):");
foreach (var r in reachable.Take(20))
{
    var a = Sim.Run(world, yours, GetPlan(yours, r), routes[r]); var b = Sim.Run(world, best, GetPlan(best, r), routes[r]);
    Console.WriteLine($"  {routes[r].FromName,-24} -> {routes[r].ToName,-24} yours {a.Time,5:F1}s ({100 * (a.DLeap + a.DBlink) / Math.Max(1, a.DWalk + a.DLeap + a.DBlink),3:F0}% skills)   best {b.Time,5:F1}s ({100 * (b.DLeap + b.DBlink) / Math.Max(1, b.DWalk + b.DLeap + b.DBlink),3:F0}% skills)   {(a.Time - b.Time >= 0 ? "-" : "+")}{Math.Abs(a.Time - b.Time):F1}s");
}

// ------------------------------------------------------------------ where do trips get stuck?
Console.WriteLine($"\nSTUCK TRIPS (hit the 240 s cap): {all.Sum(r => r.failed)} of {all.Count * reachable.Count} ({100.0 * all.Sum(r => r.failed) / (all.Count * reachable.Count):F1}%)");
var stuckReasons = new ConcurrentDictionary<string, int>();
Parallel.ForEach(combos.Where((_, i) => i % 3 == 0), c =>
{
    foreach (var r in reachable)
    {
        var res = Sim.Run(world, c, GetPlan(c, r), routes[r]);
        if (res.Ok) continue;
        var kind = res.Stuck.Contains("JUMP") ? "stuck at a planned JUMP" : "stuck walking";
        stuckReasons.AddOrUpdate(kind, 1, (_, n) => n + 1);
    }
});
foreach (var kv in stuckReasons.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key}: {kv.Value} (of a third of all combinations)");
var sample = new List<string>();
foreach (var c in combos.Where(c => c.JumpMode == 7 && c.WallClearance == 0))
{
    foreach (var r in reachable)
    {
        var res = Sim.Run(world, c, GetPlan(c, r), routes[r]);
        if (!res.Ok && sample.Count < 6) sample.Add($"  {routes[r].FromName} -> {routes[r].ToName}: {res.Stuck}   [{Desc(c)}]");
    }
}
Console.WriteLine("examples:"); foreach (var e in sample) Console.WriteLine(e);

// =====================================================================================================================
record Skill(string Name, bool TravelsThrough, int Hop, int MaxJump, float Radius);
readonly record struct PlanKey(int Straightness, int WallClearance, int JumpMode);
record Cfg(int WallClearance, int Straightness, int MaxWaypointU, int KeepWithinU, int MinDistU, int MaxDistU, float ClearMargin, int JumpMode)
{
    public PlanKey PlanKey => new(Straightness, WallClearance, JumpMode);
}
record Route(string FromName, string ToName, (int x, int y) From, (int x, int y) To);
class RawPlan { public List<(int x, int y)> Raw; public HashSet<(int x, int y)> Landings = new(); }
class Plan { public List<(int x, int y)> Wps; public HashSet<(int x, int y)> Landings; public int RawCells; }
class Result { public double Time; public bool Ok; public double DWalk, DLeap, DBlink; public int NLeap, NBlink, NJump; public string Stuck; }

sealed class World
{
    public int W, H; public int[][] Walk, Targ;
    public static World Load(string path)
    {
        var bytes = File.ReadAllBytes(path); var w = new World { H = BitConverter.ToInt32(bytes, 0), W = BitConverter.ToInt32(bytes, 4) };
        w.Walk = new int[w.H][]; w.Targ = new int[w.H][];
        for (var y = 0; y < w.H; y++)
        {
            w.Walk[y] = new int[w.W]; w.Targ[y] = new int[w.W];
            for (var x = 0; x < w.W; x++) { w.Walk[y][x] = bytes[8 + (long)y * w.W + x]; w.Targ[y][x] = bytes[8 + (long)w.H * w.W + (long)y * w.W + x]; }
        }
        return w;
    }
    public (int x, int y)? Snap((int x, int y) c)
    {
        if (GridPathfinder.IsCellWalkable(Walk, c.x, c.y)) return c;
        for (var r = 1; r <= 12; r++) for (var dy = -r; dy <= r; dy++) for (var dx = -r; dx <= r; dx++)
            if ((Math.Abs(dx) == r || Math.Abs(dy) == r) && GridPathfinder.IsCellWalkable(Walk, c.x + dx, c.y + dy)) return (c.x + dx, c.y + dy);
        return null;
    }
}

static class Sim
{
    public const float CW = 250f / 23f;            // world units per grid cell
    public const double WalkSpeed = 480.0;         // ASSUMED world units / second
    public const double LeapSeconds = 0.55;        // ASSUMED: time one Leap Slam takes, no cooldown
    public const double FrostblinkCooldown = 3.0;  // as given
    public const double Dt = 0.05;                 // the plugin's 50 ms input tick
    const int LeapHop = 660, BlinkHop = 500;

    public static RawPlan PlanRaw(World w, Route r, PlanKey k)
    {
        var plan = new RawPlan();
        GridPathfinder.JumpOptions jo = null;
        if (k.JumpMode > 0) jo = new GridPathfinder.JumpOptions { TargetingGrid = w.Targ, MaxCells = (int)((LeapHop - 45 - 15) / CW), Cost = k.JumpMode * 10 };
        plan.Raw = GridPathfinder.AStar(w.Walk, r.From, r.To, 3000000, true, out var partial, k.WallClearance, jo, plan.Landings, k.Straightness * GridPathfinder.TurnPenaltyPerLevel);
        if (partial) plan.Raw = null;
        return plan;
    }

    public static Plan Smooth(World w, RawPlan rp, Cfg c)
    {
        var raw = rp.Raw; var landings = rp.Landings;
        var maxLook = Math.Max(1, (int)Math.Round(c.MaxWaypointU / CW));
        var margin = c.WallClearance > 1 ? c.WallClearance - 1 : 0;
        var res = new List<(int x, int y)>(); var i = 0;
        while (i < raw.Count)
        {
            var best = i; var furthest = Math.Min(raw.Count - 1, i + maxLook);
            if (landings.Contains(raw[i])) { res.Add(raw[i]); i++; continue; }
            for (var k = i + 1; k <= furthest; k++) if (landings.Contains(raw[k])) { furthest = k - 1; break; }
            var span = furthest - i; var step = Math.Max(1, (span + 7) / 8);
            for (var j = furthest; j > i; j -= step)
            {
                var a = new Vector2(raw[i].x, raw[i].y); var b = new Vector2(raw[j].x, raw[j].y);
                if (GridPathfinder.HasCorridorClearance(w.Walk, a, b, 0f) && (margin == 0 || GridPathfinder.HasCorridorClearance(w.Walk, a, b, margin))) { best = j; break; }
            }
            res.Add(raw[best]); i = best + 1;
        }
        return new Plan { Wps = res, Landings = landings, RawCells = raw.Count };
    }

    static bool Jumpable(World w, Vector2 a, Vector2 b)
    {
        var len = Vector2.Distance(a, b); var n = Math.Max(1, (int)Math.Ceiling(len * 2));
        for (var i = 1; i < n; i++) { var p = a + (b - a) * (i / (float)n); if (w.Targ[(int)Math.Round(p.Y)][(int)Math.Round(p.X)] != 5) return false; }
        return true;
    }

    public static Result Run(World w, Cfg c, Plan plan, Route r, List<(Vector2 p, char m)> trail = null)
    {
        var wps = plan.Wps; var landings = plan.Landings;
        var pos = new Vector2(r.From.x, r.From.y); var tgt = new Vector2(r.To.x, r.To.y);
        double t = 0, busyUntil = 0, blinkReadyAt = 0; var idx = 0; var res = new Result(); string last = "";
        var arrival = c.KeepWithinU * 1.5;
        trail?.Add((pos, 'w'));

        while (t < 240)
        {
            if (Vector2.Distance(pos, tgt) * CW <= 60) { res.Ok = true; break; }
            if (t < busyUntil) { t += Dt; continue; }

            (int x, int y)? node = null; var isJump = false;
            while (idx < wps.Count)
            {
                var n = wps[idx]; var d = Vector2.Distance(pos, new Vector2(n.x, n.y)) * CW;
                if (landings.Contains(n)) { if (d <= 80) { idx++; continue; } node = n; isJump = true; break; }
                var takeoff = idx + 1 < wps.Count && landings.Contains(wps[idx + 1]);
                if (d <= (takeoff ? 45 : arrival)) { idx++; continue; }
                node = n; break;
            }
            var aim = node.HasValue ? new Vector2(node.Value.x, node.Value.y) : tgt;
            var distW = Vector2.Distance(pos, aim) * CW;
            var dir = distW < 1e-6 ? Vector2.Zero : (aim - pos) / (distW / CW);
            last = $"wp {idx}/{wps.Count} {(isJump ? "JUMP" : "walk")} to ({aim.X:F0},{aim.Y:F0}) {distW:F0}u away, at ({pos.X:F1},{pos.Y:F1})";

            if (isJump)
            {
                // shortest-range ready skill that reaches: Frostblink (500) first when off cooldown, else Leap Slam (660)
                if (distW <= BlinkHop && t >= blinkReadyAt) { blinkReadyAt = t + FrostblinkCooldown; res.NBlink++; res.DBlink += distW; res.NJump++; pos = aim; trail?.Add((pos, 'b')); }
                else if (distW <= LeapHop) { res.NLeap++; res.DLeap += distW; res.NJump++; busyUntil = t + LeapSeconds; pos = aim; trail?.Add((pos, 'l')); }
                else { t += Dt; continue; }
                t += Dt; continue;
            }

            var acted = false;
            // DashEnabled: a blink when the walkable line is blocked but the way is flyable
            if (distW >= c.MinDistU && distW <= c.MaxDistU && t >= blinkReadyAt && !GridPathfinder.HasCorridorClearance(w.Walk, pos, aim, 0f) && Jumpable(w, pos, aim))
            {
                var hop = Math.Min(distW, BlinkHop); blinkReadyAt = t + FrostblinkCooldown; res.NBlink++; res.DBlink += hop; pos += dir * (float)(hop / CW); trail?.Add((pos, 'b')); acted = true;
            }
            // PreferMovementSkillsForTravel: Leap Slam aimed one hop ahead, needs a clear corridor for that hop
            if (!acted)
            {
                var hop = distW > LeapHop ? LeapHop : distW;
                if (hop >= c.MinDistU && hop <= c.MaxDistU)
                {
                    var end = pos + dir * (float)(hop / CW);
                    if (GridPathfinder.HasCorridorClearance(w.Walk, pos, end, c.ClearMargin))
                    { res.NLeap++; res.DLeap += hop; busyUntil = t + LeapSeconds; pos = end; trail?.Add((pos, 'l')); acted = true; }
                }
            }
            if (!acted)
            {
                var step = Math.Min(WalkSpeed * Dt, distW); res.DWalk += step; pos += dir * (float)(step / CW); trail?.Add((pos, 'w'));
            }
            t += Dt;
        }
        res.Time = t;
        if (!res.Ok) res.Stuck = last;
        return res;
    }
}
