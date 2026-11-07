# FollowHer

A follower plugin for ExileApi / PoeHelper. It drives a secondary character that **follows a leader you control on another client** — walking to them, following them through zone transitions, and optionally fighting alongside them using a fully configurable, rule-based combat engine.

You control your main; FollowHer plays the follower.

> A lot of the foundation was taken from and/or inspired by [AutoMyAim](https://github.com/DetectiveSquirrel/AutoMyAim).

---

## Contents

- [Requirements & install](#requirements--install)
- [Quick start](#quick-start)
- [How it works: Movement vs Fighting](#how-it-works-movement-vs-fighting)
- [Runtime controls (hotkeys & overlay)](#runtime-controls-hotkeys--overlay)
- [Target navigator (walk to a Radar target)](#target-navigator-walk-to-a-radar-target)
- [Combat rule profiles](#combat-rule-profiles)
  - [Skills](#skills)
  - [Creating a profile](#creating-a-profile)
  - [The condition language](#the-condition-language)
  - [Condition reference](#condition-reference)
  - [Import / export](#import--export)
- [Settings reference](#settings-reference)
  - [Top level](#top-level)
  - [Hotkeys](#hotkeys)
  - [Party](#party)
  - [Movement](#movement)
  - [Navigation](#navigation)
  - [Combat](#combat)
  - [Targeting](#targeting)
  - [Render](#render)
- [Following through zone transitions](#following-through-zone-transitions)
- [Movement skills](#movement-skills)
- [Troubleshooting](#troubleshooting)

---

## Requirements & install

- ExileApi / PoeHelper (HUD) running against Path of Exile.
- Put the source under `Plugins/Source/FollowHer` (HUD compiles it for you) or drop a compiled build under `Plugins/Compiled/FollowHer`.
- Run the leader and the follower as **separate game clients**. FollowHer runs on the follower's client.
- Optional: the **Radar** plugin, installed and enabled. Only the [target navigator](#target-navigator-walk-to-a-radar-target) uses it; everything else works without it.

A note on distances: follow/attack distances (leader distance, movement-skill hops) are in **world units**; targeting radii (scan radius, target range) are in **grid units** (≈ 10.9 world units per grid cell). Each setting's tooltip tells you what it affects.

---

## Quick start

1. Load the follower client and open FollowHer's settings.
2. Set **Leader Name** to the exact in-game name of the character you control.
3. Tick **Enable**.
4. Under **Hotkeys**, bind a **Movement Toggle Key** and a **Fighting Toggle Key**.
5. (Optional) Under **Party**, enable **Auto Accept Leader Party Invite** so the follower joins on its own.
6. Get both characters into the same instance. Press your movement toggle — the follower should start walking to the leader.
7. For combat, pick or build a **Combat Rule Profile** (see below), then press your fighting toggle.

---

## How it works: Movement vs Fighting

FollowHer has two independent runtime states, each driven by its own hotkey:

- **Movement Enabled** — when on, the follower chases the leader (walking, pathfinding, movement skills, zone transitions). When off, it never moves on its own.
- **Fighting Enabled** — when on, the follower is *allowed* to attack. Enabling it does **not** force attacking; a hit only happens when the combat conditions also agree — most importantly **Attack When Leader Is Attacking** and being within **Distance To Leader To Attack**. Turn that setting off and "fighting enabled" simply means "attack any valid target in range".

The overlay reports three lines so you can tell these apart:

- **Movement Enabled** — is it chasing.
- **Fighting Enabled** — did your fighting hotkey take effect (the master switch).
- **Fighting Active** — is it actually permitted to swing *this instant* (switch **and** conditions). `Enabled: true` + `Active: false` means "your hotkey worked, but the leader isn't attacking / you're out of range."

---

## Runtime controls (hotkeys & overlay)

All bound under **Hotkeys**:

| Control | Effect |
|---|---|
| **Movement Toggle Key** | Flips following on/off. Seeded from `Movement > Enable` at startup; the hotkey is authoritative afterward. |
| **Fighting Toggle Key** | Flips the fighting master switch on/off (still gated by combat conditions). |
| **Precision Key** (hold) | Manual force-attack override — fights regardless of the master switch and the leader gate, while held. |
| **Precision Toggle Key** | Same as above, as a toggle instead of hold. |

Movement following survives zone transitions on purpose (that's the point); the fighting/precision toggles reset on area change.

---

## Target navigator (walk to a Radar target)

A small overlay window that lists every point of interest **Radar** marks in the current area — waypoints, zone transitions (labelled with their destination, e.g. *"The Solaris Temple Level 1"*), league mechanics, boss rooms — each with a **Go** button that walks the character there.

It runs on the *same* pathfinding the follow logic uses (A* over the walkable grid, the same smoothing, waypoint advancing and movement-skill selection), which makes it a repeatable way to **test that pathfinding on demand**: pick something across the zone, press Go, watch whether it gets there — no second client and no leader needed.

**Using it**

1. Bind **Navigation > Target Navigator Toggle Key** (or tick **Show Target Navigator**).
2. Press **Go** on a target. Navigation is a manual override: while a destination is set it takes priority over following the leader, and it keeps the plugin ticking even with the movement and fighting toggles off.
3. It stops on arrival, on **Stop**, if no walkable path exists, on timeout, or when you change area.
4. Bind **Stop Navigation Key** / **Resume Navigation Key** to interrupt and pick the same target back up without opening the window — handy for taking manual control through a fight, then carrying on. The buttons show their bound key, and the header shows which target Resume would take.

The stopped target is remembered until you change area (grid coordinates only mean anything within the area they came from). **Resume** replans from wherever you're standing rather than continuing the old route.

The status line reports what it's doing — distance left, waypoints remaining, `final approach`, `no walkable path`, `gave up - timed out`. With **Movement > Visual > Show Follow Path** on you also get the usual line to the current waypoint.

**Where the targets come from.** They're Radar's own, not a second list to maintain: the target *names* are read from Radar's `targets.json` (the current area's block plus the global `*` block) and the *positions* come from Radar itself over the plugin bridge (`Radar.ClusterTarget`), so they're the exact cells Radar draws on its map — its clustering, snapped to walkable tiles by its own code.

That means **Radar must be installed and enabled** for the list to populate; without it the window says so instead of listing anything. Two Radar features the bridge signature can't carry: a target's `Rooms` filter is ignored, and room-scoped entries (those with `Name: "*"`) are skipped since there's no tile pattern to resolve. Coverage is whatever Radar defines — the campaign is well covered (most acts define the transitions of every zone); a map with no entry of its own only shows the global targets.

---

## Combat rule profiles

The combat engine is generic. Instead of hardcoded "builds", you create **profiles**: an ordered list of `(Skill, Condition)` rules. Each tick the engine walks the rules top-to-bottom and fires the **first** rule whose skill is usable and that has a valid target matching its condition. **List order is priority.**

Profiles are edited in the settings panel below the normal Combat settings.

### Skills

Skills are discovered automatically from the follower's **skill bar** — you don't type them in. Each discovered skill appears under **Combat > Skills** (attack/utility) or **Movement > Movement Skills** (movement) with these per-skill options:

| Option | Meaning |
|---|---|
| **Enabled** | Whether the engine may use this skill at all. |
| **Use Click Instead of Hold** | Fire the skill with a single click rather than holding the key. |
| **Additional Delay (ms)** | Extra delay between uses of this skill. |
| **Line Of Sight Type** | `Walkable` (needs a walkable line) or `Targetable` (can shoot over gaps but not through walls). Attack skills default to `Targetable`. |
| **Travels Through Obstacles** | Marks a movement skill as blink-type (teleports through terrain) vs ground-dash (collides). Auto-detected for known skills. |
| **Requires Aiming** | Whether the cursor must be placed on the target before casting. Turn **off** for buffs/auras/cries that fire without a target. |

### Creating a profile

1. In the Combat settings, find **Combat Rule Profiles**.
2. Type a name into **New Profile Name** and click **Add Profile**. It becomes the **Active Profile**.
3. Click **Add New Rule**. Each rule has:
   - **Enabled** checkbox.
   - **Skill** — pick from your discovered skills (click **Expand** to change it).
   - **Condition** — a boolean expression (see below). Blank/`true` means "always, when a target exists".
4. **Drag the `=` handle** to reorder rules — higher = higher priority.
5. The editor shows **Compiled OK** (green), **Disabled** (yellow), or a red compile error if your condition doesn't parse.

Switch between profiles at any time with the **Active Profile** dropdown — this is how you "change build" without touching code.

### The condition language

Each condition is a **single boolean expression** evaluated against the candidate target. It uses [Dynamic LINQ](https://dynamic-linq.net/) syntax:

- Operators: `&&`, `||`, `!`, `==`, `!=`, `<`, `<=`, `>`, `>=`, and parentheses.
- Two roots: **`Target`** (the monster being considered) and **`Player`** (the follower).
- Health/ES values are **0–1 fractions** (e.g. `0.85` = 85%).
- An empty condition evaluates to `false`; the seeded default for a new rule is `true`.

### Condition reference

On both `Target` and `Player`:

| Member | Type | Meaning |
|---|---|---|
| `HealthPercent` | float 0–1 | Life fraction. |
| `EnergyShieldPercent` | float 0–1 | ES fraction. |
| `DistanceToPlayer` | float | Distance from the follower. |
| `Rarity` | enum | `MonsterRarity.Normal` / `Magic` / `Rare` / `Unique`. |
| `IsAlive` | bool | Alive flag. |
| `HasBuff("name")` | bool | Whether a buff/debuff with that internal name is present. |
| `BuffTimer("name")` | float | Remaining time on a buff (0 if absent). |
| `ExertedAttacksRemaining("SkillName")` | int | Remaining exerted/empowered attacks from a warcry. |

Context functions (no root):

| Function | Meaning |
|---|---|
| `NearbyMonsterCount(radius)` | Living monsters within `radius` of the target (excludes the target itself). |
| `HasNearbyEntityWithMetadata("path", radius)` | Whether any entity whose metadata path contains `"path"` is within `radius` of the target — e.g. "is one of my totems already here". |

**Examples** (all from the built-in profiles):

```text
# Curse rares/uniques that aren't already cursed
(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !Target.HasBuff("curse_vulnerability")

# Keep Righteous Fire up
!Player.HasBuff("righteous_fire")

# Recast Enduring Cry when low or the buff is about to expire
Player.HealthPercent <= 0.85 || Player.BuffTimer("enduring_cry") < 1

# Drop a totem on a rare only if one isn't already next to it
(Target.Rarity == MonsterRarity.Rare || Target.Rarity == MonsterRarity.Unique) && !HasNearbyEntityWithMetadata("Metadata/Monsters/Totems/HolyFireSprayTotem", 30)

# Re-warcry only when exerted attacks are used up
Player.ExertedAttacksRemaining("SeismicCry") == 0
```

Several ready-made profiles ship by default (ConcLeveling, RF, Sunder, Retaliation, PaladinSupp, etc.) as working references — copy and adapt them.

### Import / export

- **Export Active Profile** copies the profile to your clipboard as a Base64 string.
- Paste a string into **Import Text** and click **Import Profile** to add it (auto-renamed if the name collides).

Use this to share profiles or back them up.

---

## Settings reference

### Top level

| Setting | Default | Description |
|---|---|---|
| **Enable** | off | Master on/off for the whole plugin. |
| **Leader Name** | — | Exact in-game name of the character to follow. Required for everything. |

### Hotkeys

See [Runtime controls](#runtime-controls-hotkeys--overlay). Precision Key / Precision Toggle Key are manual force-attack overrides; Movement/Fighting Toggle Keys are the main controls.

### Party

| Setting | Default | Description |
|---|---|---|
| **Auto Accept Leader Party Invite** | off | Auto-accepts a **party** invite only when the inviter's name matches **Leader Name**. Party invites from anyone else, and all trade/friend/guild invites, are ignored. |
| **Accept Delay (ms)** | 500 | Wait this long after the leader's invite first appears before clicking Accept. |

### Movement

| Setting | Default | Description |
|---|---|---|
| **Enable** | on | Seeds the Movement Toggle state at startup. After that the hotkey controls following. |
| **Keep Within Distance** | 200 | With Close Follow on, the follower moves whenever it's farther than this from the leader. |
| **Transition Distance** | 500 | Distance at which the follower always moves toward the leader (even with Close Follow off). |
| **Zone Update Buffer (ms)** | 1000 | How long the leader's party-panel zone name must be stable before acting on it — avoids chasing a half-updated zone. |
| **Zone Transition Max Walk Distance** | 300 | Prefer walking to and clicking the matching zone transition over teleporting, unless it's farther than this (or not visible). |
| **Portal Approach Distance** | 80 | How close to get to a transition/portal before clicking it. |
| **Transition Search Radius** | 200 | How far from where the leader was last seen to look for the transition they took. Raise this if the follower reaches the spot but never finds the transition. |
| **Close Follow** | on | Keep tight to the leader (use Keep Within Distance) instead of only closing large gaps. |
| **Disable Movement In Town** | off | Don't follow while in a town/hideout (still follows the leader *out* of town). |
| **Dash Enabled** | off | Use a blink-type movement skill to punch through a blocked path. See [Movement skills](#movement-skills). |
| **Prefer Movement Skills For Travel** | off | Use a ground-dash skill to travel faster on already-clear paths. See [Movement skills](#movement-skills). |
| **Movement Skill Clearance Margin** | 1.5 | Grid cells of side clearance a ground-dash needs before it's used (hitbox width). |
| **Dash Shortcut Path Ratio** | 1.5 | When LOS to the leader is blocked, blink straight to them instead of pathfinding if the walkable route is at least this many times longer than the straight line. `1` = always blink when blocked; higher = only to skip a big detour. |
| **Movement Skill Min Distance** | 100 | Don't use a movement skill for a hop shorter than this. |
| **Movement Skill Max Distance** | 1000 | Don't use a movement skill for a hop longer than this. |
| **Max Waypoint Distance** | 400 | How far apart path waypoints may be placed (world units) where the way ahead is clear. Corners keep tight waypoints on their own; this only stretches the straight sections. See [Movement skills](#movement-skills). |
| **Wall Clearance** | 3 | Grid cells (~11 world units each) the path tries to keep off walls. Shortest-path routes hug walls, which leaves ground dashes no clear corridor; this makes A* pay extra for cells near walls and stops waypoint smoothing from cutting corners. Soft preference - a narrower gap is still used if it's the only way. `0` = plain shortest path. |
| **Input Frequency (ms)** | 50 | Minimum delay between successive movement inputs. |
| **Enable Pathfinding Fallback** | on | Use A* pathfinding around obstacles. Off = always walk straight at the target. |
| **Movement Skills** | — | Auto-discovered movement skills with per-skill options. |

**Movement > Visual**

| Setting | Default | Description |
|---|---|---|
| **Show Follow Path** | on | Draw the current follow/task path. |
| **Task Line Color** / **Task Line Width** | blue / 2 | Style of that path line. |

**Movement > Debug**

| Setting | Default | Description |
|---|---|---|
| **Show Detailed Debug** | off | Verbose movement/transition logging to the debug window. |

**Movement > Tasks** — side-tasks the follower does while following:

| Setting | Default | Description |
|---|---|---|
| **Leader Proximity Range** | 300 | The leader must be within this of a quest item/object for the task to trigger (so the follower doesn't wander off for loot the leader is ignoring). |
| **Pick Up Quest Items** | on | Walk to and pick up nearby quest items. |
| **Quest Item Pickup Range** | 500 | Max distance to a quest item before going for it. |
| **Click Quest Objects** | on | Walk to and click nearby quest objects (levers, etc.). |
| **Quest Object Click Range** | 300 | Max distance to a quest object before going for it. |

### Navigation

Settings for the [target navigator](#target-navigator-walk-to-a-radar-target).

| Setting | Default | Description |
|---|---|---|
| **Show Target Navigator** | off | Show the overlay window listing this area's Radar targets. |
| **Target Navigator Toggle Key** | none | Shows/hides that window. |
| **Stop Navigation Key** | none | Stops walking to the current target. The target is remembered. |
| **Resume Navigation Key** | none | Walks to the last picked target again, replanning from where you're standing. |
| **Arrival Distance** | 60 | Stop this close (world units) to the chosen target — targets sit on a tile cluster's center, which you often can't stand exactly on. |
| **Navigation Timeout (s)** | 180 | Give up on a destination after this long. `0` = never give up. |
| **Radar targets.json Path** | blank | Blank auto-detects it under `Plugins/Source/Radar` (or `Plugins/Compiled/Radar`). Set it only if your Radar install lives elsewhere. |

### Combat

| Setting | Default | Description |
|---|---|---|
| **Attack When Leader Is Attacking** | on | Only fight while the leader is attacking and you're within Distance To Leader To Attack. Off = fight any valid target in range whenever Fighting is enabled. |
| **Distance To Leader To Attack** | 40 | How close you must be to the leader for the "leader is attacking" gate to apply. |
| **Attack Grace Period (ms)** | 500 | Keep attacking for this long after the leader was last seen attacking — smooths the gaps between individual swings. |
| **Log Why Not Attacking** | off | When combat produces no action, log the reason (unresolved skill name, skill unusable, no valid targets, or no target matching the condition). Logs only when the reason changes. |
| **Leader Skill Blacklist** | movement skills | List (with a `+ Add Skill` button and per-row enable checkbox) of skills that do **not** count as the leader attacking. Matching ignores case/spaces and checks display and internal names. Add anything wrongly pulling the follower into combat; uncheck to disable a row without deleting it. |
| **Skills** | — | Auto-discovered attack/utility skills with per-skill options. |
| **Combat Rule Profiles** | seeded defaults | The profile editor. See [Combat rule profiles](#combat-rule-profiles). |

**"Leader is attacking" detection:** every skill the leader uses counts as an attack **unless it's on the blacklist**. This is inverted from a fixed movement-skill list so unusual skills (most spells) register without a code change. Blacklist their movement/utility skills so those don't trip it.

### Targeting

Controls how the combat engine chooses which monster to hit.

| Setting | Default | Description |
|---|---|---|
| **Scan Radius** | 100 | How far out (grid units) monsters are tracked. |
| **Max Target Range** | 100 | Max distance a monster can be to be a valid target. |
| **Prioritize Current Target** | on | Bias toward keeping the current target rather than switching every tick. |
| **Target Switch Cooldown (s)** | 0 | Minimum time between target switches. |
| **Min Weight Difference To Switch** | 0.5 | A new target must out-weight the current one by at least this to switch. |

**Targeting > Priorities**

| Setting | Default | Description |
|---|---|---|
| **Enable Priorities** | on | Use the weighting system below. |
| **Distance Weight** | 1.0 | How much closeness matters. |
| **Health > Consider Health** | on | Factor target health into weight. |
| **Health > Prefer Higher Health** | off | Prefer high-health targets (default prefers low). |
| **Health > Health Weight** | 1.0 | Strength of the health factor. |
| **Rarity > Consider Rarity** | on | Factor rarity into weight. |
| **Rarity > Normal/Magic/Rare/Unique Weight** | 1/2/3/4 | Per-rarity weight multipliers. |

**Targeting > Line Of Sight**

| Setting | Default | Description |
|---|---|---|
| **Require Line Of Sight** | on | Only target monsters with line of sight. **Turn off** if the follower has clear targets but refuses to attack (a LOS/terrain-data issue) — this bypasses the LOS filter entirely. |

**Targeting > Density** — bias toward clumped packs:

| Setting | Default | Description |
|---|---|---|
| **Enable Clustering** | on | Weight targets by how packed the area is. |
| **Cluster Radius** | 25 | Radius used to group monsters into clusters. |
| **Minimum Cluster Size** | 3 | Monsters needed to count as a cluster. |
| **Base / Maximum Cluster Bonus** | 0.1 / 2.0 | Weight multiplier range for cluster density. |
| **Enable Core Bonus** / **Core Bonus Multiplier** / **Core Radius Percent** | on / 1.2 / 0.5 | Extra weight for monsters near a cluster's center. |
| **Enable Isolation Penalty** / **Isolation Penalty Multiplier** | on / 0.8 | Reduce weight for lone monsters. |

### Render

| Setting | Default | Description |
|---|---|---|
| **Enable Rendering** | on | Master switch for all overlays. |
| **Show Debug Info** | off | The Movement/Fighting/target status panel. |
| **Show Terrain Debug** | off | Draw raw terrain-targeting grid values (LOS layer). |
| **Show Walkable Debug** | off | Draw raw walkable grid values (pathfinding layer). |

**Render > Target Visuals**

| Setting | Default | Description |
|---|---|---|
| **Show Target Highlight** + **Color** + **Thickness** | on / orange / 2 | Outline the current target. |
| **Show Target Health** + **Health Text Color** | on / white | Show the target's HP%. |

**Render > Interface** — when overlays/targeting are allowed:

| Setting | Default | Description |
|---|---|---|
| **Enable With Fullscreen UI** | off | Keep running while a fullscreen panel is open. |
| **Enable With Left Panel** / **Enable With Right Panel** | off | Keep running with the inventory/stash side panels open. |
| **Safe Zone > Left/Right/Top/Bottom Margin** | 2/3/8/15 | Screen-edge margins (%) the cursor stays within. |

**Render > Cursor**

| Setting | Default | Description |
|---|---|---|
| **Targeting Radius** | 10 | Cursor placement tolerance around the target. |
| **Enable Randomization** / **Randomization Factor** | off / 0.5 | Jitter the aim point. |
| **Limit Cursor Range** / **Max Cursor Range** | off / 300 | Cap how far from center the cursor may be placed. |

---

## Following through zone transitions

When the leader leaves the area, FollowHer:

1. Notices they're no longer reachable (party panel reports a different zone, or pathfinding to their last spot fails).
2. Walks to where the leader was last seen and looks for the transition/portal they used, within **Transition Search Radius**. Transitions are found from both ground labels **and** the entity list, so label-less doorways still work.
3. Walks up to **Portal Approach Distance** and clicks it.
4. If no matching transition is walkable/visible (or it's beyond **Zone Transition Max Walk Distance**), it falls back to the party-panel **teleport-to-leader** button and confirms the popup.

The teleport confirmation window is treated as modal — it's resolved before any other action, so the follower won't click a transition while that popup is open.

---

## Movement skills

Two mechanically different kinds of movement skill, each controlled by its own setting:

| | **Dash Enabled** | **Prefer Movement Skills For Travel** |
|---|---|---|
| Skill type | Blink (Frostblink, Flame Dash, Lightning Warp, Blink Arrow) — pass through obstacles | Ground-dash (Shield Charge, Whirling Blades) — collide with obstacles |
| Fires when the path is | **Blocked** | **Clear** |
| Purpose | Punch through terrain | Travel faster |
| Gate | Line of sight to target is blocked | Full corridor-clearance check passes |

Both are bounded by **Movement Skill Min/Max Distance**. Which bucket a skill lands in is decided by its **Travels Through Obstacles** flag (auto-set on discovery for known skills).

**Dash Shortcut Path Ratio** ties into Dash Enabled: when LOS to the leader is blocked and a blink skill is ready, the follower compares the walkable detour to the straight-line distance and blinks straight to the leader if the detour is at least that many times longer — instead of pathfinding the long way around.

### Why a movement skill never fires while pathfinding

While following a path, the skill is judged against the **next waypoint**, not the final destination — so a hop only qualifies if the waypoint is at least **Movement Skill Min Distance** away. Two settings decide how far out that waypoint is:

- **Max Waypoint Distance** is the ceiling. A* produces a route cell by cell; waypoints are placed at the furthest cell each one still has a clear walkable line to, so corners stay tight (the line breaks there) and straight stretches collapse into a single long hop. Raise this to get hops worth dashing.
- **Keep Within Distance × 1.5** is the floor — waypoints closer than that count as already reached and are skipped. Waypoint spacing below that ceiling has no effect, so if you follow very tightly, lower Keep Within Distance too.

Worth knowing for **ground dashes** specifically: they also need a clear corridor of **Movement Skill Clearance Margin** cells on each side along the *whole* hop, so a very long waypoint distance can make them fire *less* often in cluttered terrain. Blinks are unaffected by that, but they deliberately only fire where the walkable line is blocked — which a path waypoint never is, by construction — so on a path they only come into play through Dash Shortcut Path Ratio.

---

## Troubleshooting

**Follower won't attack even though "Fighting Enabled" is true.**
Enable **Combat > Log Why Not Attacking** and watch the debug window. It reports exactly which stage fails: an unresolved rule skill name (with the list of discovered skill names), a disabled/unusable skill, no valid targets, or targets that don't match the condition.

**"Usable but no valid targets" with monsters right on top of you.**
This is almost always the line-of-sight filter. Turn off **Targeting > Line Of Sight > Require Line Of Sight** to confirm — if it starts attacking, the terrain LOS layer is the culprit. Use **Render > Show Terrain Debug** to inspect the raw values.

**Follower registers the leader as "attacking" when they just dash/move.**
Add that skill to the **Leader Skill Blacklist** (Combat settings). Movement skills are blacklisted by default; add any others that trip it.

**Follower reaches the leader's last spot but never takes the transition.**
Raise **Movement > Transition Search Radius**. Turn on **Movement > Debug > Show Detailed Debug** to see the transition-follow decisions.

**The navigator says a target is "cut off" that Radar draws a line to.**
Distance alone no longer causes this — a search that exhausts its node budget now returns a best-effort route and extends it as you walk, so only a genuinely disconnected target reports it. Two things can still legitimately differ from Radar: it optionally paths through **triggerable blockades** (its *Clear Triggerable Blockades* option, on by default, opens doors and blockades before pathing) whereas FollowHer reads the raw walkable grid, and its route ignores whether *you* can open what's in the way.

**The target navigator lists nothing.**
The status line next to **Refresh** says why: Radar not loaded (it must be installed **and enabled** — it's what supplies the positions), `targets.json` not found (set **Navigation > Radar targets.json Path**), or Radar defines no targets for this area. Right after a zone load it can take a moment before Radar has processed the area; the window retries on its own, or press **Refresh**.

**A specific movement skill isn't recognized.**
Skills are read off the skill bar on area load. Skill-name matching is case/space-insensitive and checks both display and internal names; if one still isn't picked up, note its internal name and it can be added.

---

*Early-stage plugin — expect rough edges, and read what a setting does before turning it on.*
