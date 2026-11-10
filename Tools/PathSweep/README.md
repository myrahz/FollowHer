# PathSweep

An offline simulator and settings sweep for FollowHer's pathing and movement skills. It loads a zone's terrain, plans routes between the player and every target Radar lists, replays the follow logic tick by tick (50 ms) under hundreds of setting combinations, and ranks them by mean trip time.

It compiles the plugin's real `GridPathfinder.cs` (see `PathSweep.csproj`), so the A* search, turn penalty, wall clearance, jump edges and corridor checks are the shipped code, not a copy. The waypoint smoothing and the skill-selection rules (Dash Enabled blink, Prefer Movement Skills For Travel, hop-sized aiming, takeoff/landing tolerances) are re-implemented in `Program.cs` and have to be kept in step with `PathFollower` / `FollowManager` by hand.

## What it answers

- Which settings actually change trip time, and by how much (the "WHAT MATTERS" table).
- The best combination found, and how your current settings rank against it.
- Per route, yours against the best.
- Whether any trip gets **stuck** (hits the 240 s cap) and where. This is how the jump-takeoff stall was found.

## Running it

1. **Dump the zone** (the HUD must be running, in the zone you want). With the ExileAPI MCP `run_csharp` tool, run the script below. It writes the walkable and terrain layers to a file and prints your grid position.
2. **Dump Radar's targets** with the second script.
3. Run the tool:

```
dotnet run -c Release --project Tools/PathSweep -- <grid.bin> <startX> <startY> <targets.txt>
```

`startX`/`startY` are the player's grid cell printed by the first script.

### Dump the zone (`grid.bin`)

```csharp
var data = GameController.IngameState.Data;
var walk = data.RawPathfindingData; var targ = data.RawTerrainTargetingData;
int h = walk.Length, w = walk[0].Length;
var buf = new byte[8 + 2L * h * w];
BitConverter.GetBytes(h).CopyTo(buf, 0); BitConverter.GetBytes(w).CopyTo(buf, 4);
for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
  buf[8 + (long)y * w + x] = (byte)walk[y][x];
  buf[8 + (long)h * w + (long)y * w + x] = (byte)targ[y][x];
}
System.IO.File.WriteAllBytes(@"C:\path\to\grid.bin", buf);
var p = GameController.Player.GridPosNum;
new { area = GameController.Area.CurrentArea.Area.Id, player = $"{(int)Math.Round(p.X)},{(int)Math.Round(p.Y)}" }
```

Run it with `thread: "worker"`.

### Dump Radar's targets (`targets.txt`)

One `label|x|y` line per target, read from the same list Radar draws:

```csharp
var radar = Core.Current.pluginManager.Plugins.First(pl => pl.Name == "Radar");
var plugin = radar.Plugin ?? (object)radar;
var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var dict = (System.Collections.IDictionary)plugin.GetType().GetField("_clusteredTargetLocations", bf).GetValue(plugin);
var rows = new List<string>();
foreach (System.Collections.DictionaryEntry e in dict) {
  var tl = e.Value; var t = tl.GetType();
  var locs = (System.Numerics.Vector2[])t.GetProperty("Locations").GetValue(tl);
  var name = t.GetProperty("DisplayName").GetValue(tl)?.ToString() ?? "?";
  foreach (var l in locs) rows.Add($"{name}|{(int)Math.Round(l.X)}|{(int)Math.Round(l.Y)}");
}
System.IO.File.WriteAllLines(@"C:\path\to\targets.txt", rows);
rows.Count
```

## Assumptions to calibrate

These live in the `Sim` class and are **assumed, not measured**. The ranking of settings is fairly robust to them, but absolute times are not:

| Constant | Value | What it is |
|---|---|---|
| `WalkSpeed` | 480 u/s | Running speed in world units per second. |
| `LeapSeconds` | 0.55 s | How long one Leap Slam takes. Assumed no cooldown. |
| `FrostblinkCooldown` | 3 s | As configured. |
| `LeapHop` / `BlinkHop` | 660 / 500 | Measured in game for Leap Slam; Frostblink's is the configured value. |

A Leap Slam only beats walking above `WalkSpeed * LeapSeconds` (about 260 units with these numbers), which is why a minimum hop distance below that costs time.

## Settings swept

Path Straightness, Wall Clearance, Max Waypoint Distance, Movement Skill Min Distance, Movement Skill Clearance Margin, jump planning (off / on with cost 7 / on with cost 20) and Keep Within Distance. The grids are at the top of `Program.cs`; "yours" is the `yours` record there, so edit it to match your own settings before comparing.

## Caveat

It models one skill pair (Leap Slam and Frostblink) and straight-line walking between waypoints. Results are for the zone you dump; run it on a few different zones before trusting a setting.
