# FollowHer

A follower plugin for ExileApi / PoeHelper. It drives a secondary character that **follows a leader you control on another client** — walking to them, following them through zone transitions, and optionally fighting alongside them using a fully configurable, rule-based combat engine.

You control your main; FollowHer plays the follower.

> A lot of the foundation was taken from and/or inspired by [AutoMyAim](https://github.com/DetectiveSquirrel/AutoMyAim).

---

## Contents

- [Requirements & install](#requirements--install)
- [Quick start](#quick-start)
- [Modes: Follow and Navigator](#modes-follow-and-navigator)
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

## Modes: Follow and Navigator

The plugin runs in exactly one of two **modes**, chosen by the **Mode** setting. Switching stops whatever the other mode was doing.

| | **Follow** | **Navigator** |
|---|---|---|
| Moves toward | the leader (**Leader Name**) | the target last clicked in the navigator window |
| Starts moving | **Movement Toggle Key** | **Move Key** — *Toggle* (press to start, press to stop) or *Hold* (walks while held) |
| Fights | when the leader fights (**Attack When Leader Is Attacking**), gated by **Fighting Toggle Key**; Precision overrides | **only while the Precision key is held** (or Precision Toggle is on); otherwise it moves |
| Pathing | chases a moving target: cheap budgeted search, replanned as the leader moves | walks to a fixed point: one whole-zone search, route cached |
| Style | **AFK** — runs unattended | **Assisted** — you're at the keyboard |
| Unattended clicking | auto-resurrect at the checkpoint, auto-accept the leader's party invite, auto level-up gems | **none** — it never clicks resurrect, invites or gem buttons for you |
| Extras | zone-transition following, quest object/item tasks | the Radar target window, Stop/Resume keys |
| Shared | combat rule profiles, targeting, movement skills and jump planning, overlays | |

In Navigator mode the leader settings, the Movement and Fighting Toggle Keys and the leader attack gate do nothing. Fighting and moving take turns: with the fight key held and an enemy in range the character stops and fights; when the key is released, or nothing is left to hit, it carries on walking.

---

## How it works: Movement vs Fighting

*(Follow mode.)*

FollowHer has two independent runtime states, each driven by its own hotkey:

- **Movement Enabled** — when on, the follower chases the leader (walking, pathfinding, movement skills, zone transitions). When off, it never moves on its own.
- **Fighting Enabled** — when on, the follower is *allowed* to attack. Enabling it does **not** force attacking; a hit only happens when the combat conditions also agree — most importantly **Attack When Leader Is Attacking** and being within **Distance To Leader To Attack**. Turn that setting off and "fighting enabled" simply means "attack any valid target in range".

The overlay reports three lines so you can tell these apart:

- **Movement Enabled** — is it chasing.
- **Fighting Enabled** — did your fighting hotkey take effect (the master switch).
- **Fighting Active** — is it actually permitted to swing *this instant* (switch **and** conditions). `Enabled: true` + `Active: false` means "your hotkey worked, but the leader isn't attacking / you're out of range."

---

## Runtime controls (hotkeys & overlay)

All bound under **Hotkeys** (Movement/Fighting Toggle apply in Follow mode; the Precision keys apply in both — in Navigator mode they are the fight keys):

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

The window only exists in **Navigator mode** (set **Mode** to Navigator).

1. Bind **Navigation > Target Navigator Toggle Key** (or tick **Show Target Navigator**).
2. **Click a target's name** to select it, or press **Go** to select it and start walking at once. The header shows what's selected.
3. Bind **Navigation > Move Key** and pick its behaviour: **Toggle** (press to walk to the selected target, press again to stop) or **Hold** (walk only while the key is down; releasing stops, and the target stays selected).
4. **Fight** by holding the **Precision Key** (or toggling **Precision Toggle Key**) — otherwise the character moves. With an enemy in range it stops and fights while the key is held, and carries on walking when you let go or nothing is left to hit.
5. It stops on arrival, on release (Hold), on **Stop**, if no walkable path exists, on timeout, or when you change area. **Stop Navigation Key** / **Resume Navigation Key** still work alongside the Move Key.

Following and navigating share the pathfinder but use it differently. **Following** chases a moving leader who is normally close: a cheap, budgeted search that reruns as they move. **Navigating** walks to a point that never moves: one whole-zone search up front (expect a brief hitch on Go for a far target), the complete route cached, and a replan only if you get knocked or teleported off it. That's what makes a long, winding route work — a budgeted search ends in whichever dead end happens to be nearest the target in a straight line.

The stopped target is remembered until you change area (grid coordinates only mean anything within the area they came from). **Resume** replans from wherever you're standing rather than continuing the old route.

The status line reports what it's doing — distance left, waypoints remaining, `final approach`, `no walkable path`, `gave up - timed out`. With **Movement > Visual > Show Follow Path** on you also get the usual line to the current waypoint.

**Where the targets come from.** They're Radar's own, not a second list to maintain. FollowHer reads the list Radar has already worked out for the current area — the same one it draws and routes to — so you see exactly Radar's targets: room-scoped ones (boss doors, league mechanics, ...), alternatives, adjacent-tile merging, and entity targets as they load, all as Radar resolved them. The list refreshes about once a second while the window is open, because Radar fills it in shortly after a zone load. Targets that share a name (e.g. two "boss" entries) are numbered `#1`, `#2`.

**Radar must be installed and enabled** for the list to populate; the status line under **Refresh** says what it found. If Radar's list can't be read (a Radar build that doesn't keep it), FollowHer falls back to the older route — names from Radar's `targets.json` (the current area's block plus the global `*` block) with positions from Radar over the plugin bridge (`Radar.ClusterTarget`). That route can't carry Radar's `Rooms` filter, so **room-scoped targets are missing** in it, and the status line says so. Coverage is whatever Radar defines — the campaign is well covered (most acts define the transitions of every zone); a map with no entry of its own only shows the global targets.

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
| **Mode** | Follow | **Follow** or **Navigator** — see [Modes](#modes-follow-and-navigator). Only one runs at a time. |
| **Leader Name** | — | Follow mode only: exact in-game name of the character to follow. |

### Hotkeys

See [Runtime controls](#runtime-controls-hotkeys--overlay). Precision Key / Precision Toggle Key are manual force-attack overrides; Movement/Fighting Toggle Keys are the main controls.

### Party

| Setting | Default | Description |
|---|---|---|
| **Auto Accept Leader Party Invite** | off | Follow mode only. Auto-accepts a **party** invite only when the inviter's name matches **Leader Name**. Party invites from anyone else, and all trade/friend/guild invites, are ignored. |
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
| **Max Waypoint Distance** | 400 | How far apart path waypoints may be placed (world units) where the way ahead is clear. Corners keep tight waypoints on their own; this only stretches the straight sections. See [Movement skills](#movement-skills). |
| **Path Straightness** | 4 | How strongly the route prefers long straight runs over bends (`0` = off, `10` = strongest). Routes of equal length often differ only in how many times they turn; this charges a cost per bend, so the route holds a heading and bends less, at the price of the odd small detour. See [Straighter paths and hop-sized aiming](#straighter-paths-and-hop-sized-aiming). |
| **Wall Clearance** | 3 | Grid cells (~11 world units each) the path tries to keep off walls. Shortest-path routes hug walls, which leaves ground dashes no clear corridor; this makes A* pay extra for cells near walls and stops waypoint smoothing from cutting corners. Soft preference - a narrower gap is still used if it's the only way. `0` = plain shortest path. |
| **Plan Jumps In Pathfinding** | on | Let the path jump over gaps and low obstacles with movement skills (Leap Slam, Flame Dash, Frostblink) when that beats the walk. See [Jumping obstacles](#jumping-obstacles). |
| **Input Frequency (ms)** | 50 | Minimum delay between successive movement inputs. |
| **Enable Pathfinding Fallback** | on | Use A* pathfinding around obstacles. Off = always walk straight at the target. |
| **Movement Skills** | — | Auto-discovered movement skills. Everything that depends on the skill - roles, distances, hitbox, jump reach and cost - is set per skill; see [Movement skills](#movement-skills). |

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
| **Move Key** | none | Navigator mode: walks to the selected target (the last one clicked). Behaviour set by Move Key Behavior. |
| **Move Key Behavior** | Toggle | **Toggle**: press to start, press to stop. **Hold**: walks only while held. |
| **Stop Navigation Key** | none | Stops walking to the current target. The target stays selected. |
| **Resume Navigation Key** | none | Walks to the selected target again, replanning from where you're standing. |
| **Arrival Distance** | 60 | Stop this close (world units) to the chosen target — targets sit on a tile cluster's center, which you often can't stand exactly on. |
| **Navigation Timeout (s)** | 180 | Give up on a destination after this long. `0` = never give up. |
| **Radar targets.json Path** | blank | Blank auto-detects it under `Plugins/Temp/Radar`, then `Plugins/Compiled/Radar`, then `Plugins/Source/Radar`. Set it only if your Radar install lives elsewhere. |

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

---|---|---|
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

Movement skills are different from each other - how far they carry you, how close to a wall they will still fire, whether they cross gaps, how long they take to come back - so every one of those settings lives on the skill itself, in the **Movement Skills** list. The list shows only the movement settings, and the combat skill list only the combat ones.

Two mechanically different kinds of movement skill, told apart by **Travels Through Obstacles** (auto-set on discovery for known skills) and switched on per skill:

| | **Use To Cross Blocked Paths** | **Use For Travel** |
|---|---|---|
| Skill type | Blink (Frostblink, Flame Dash, Lightning Warp, Blink Arrow) — pass over obstacles | Ground-dash (Shield Charge, Whirling Blades, Leap Slam) — collide with obstacles |
| Fires when the path is | **Blocked** (but you can see across) | **Clear** |
| Purpose | Cross a gap or punch past a detour | Travel faster |
| Gate | Line of sight to target is blocked, targeting LOS is clear | The skill's Hitbox Clearance passes along the hop |

Per-skill settings:

| Setting | What it does |
|---|---|
| **Use To Cross Blocked Paths** / **Use For Travel** | The two roles above. They replace the old global *Dash Enabled* and *Prefer Movement Skills For Travel*. |
| **Minimum Travel Distance** | The least the skill ever moves you. Clicking closer than this still carries you this far; clicking further goes to the click, up to the Travel Distance. Frostblink measured in game: closest click moves **352** units, furthest **500**. Jumps are then planned with landings no nearer than the minimum (a nearer one would be overshot), and the skill is skipped if the spot it would actually land on isn't solid ground. `0` for skills that stop at the cursor (Leap Slam). Not the same as **Minimum Distance Per Use**, which is for skills that don't move at all when you click too close (Shield Charge). |
| **Travel Distance Per Use** | How far one use carries you. The cursor is aimed this far ahead; blinks and hops never aim beyond it. |
| **Minimum Distance Per Use** | Don't use the skill to cover less than this; the character walks instead. Measure it: do the shortest use and compare positions (Shield Charge ≈ 260). |
| **Required Floor Value (0-5)** | The lowest walkable-layer value every cell of the hop (and the cell you stand on) may read. `5` = open floor only (margin cells beside walls read 1-4), which keeps Shield Charge off corners and ledges; `0` = any walkable cell. Works with Hitbox Clearance, which sets how wide the checked strip is. |
| **Hitbox Clearance (cells)** | Radius around the character and along the hop that must be walkable. See [Per-skill hitbox clearance](#per-skill-hitbox-clearance). |
| **Max Jump Distance** | Reach when planning jumps over gaps. `0` = never planned for jumps - leave it `0` on skills that collide with walls. |
| **Jump Path Cost (cells)** | What a jump with this skill is worth to the planner: a jump is only taken when the walk is longer than the jump plus this. The planner uses the cheapest cost among the skills that can jump. |
| **Wait For Cooldown (ms)** | How long to wait at a takeoff for this skill to come back before giving up on the jump. |
| **Shortcut Path Ratio** | Blinks only. When LOS to the leader is blocked, blink straight to them if the walkable route is at least this many times longer than the straight line. `1` = always; higher = only to skip a big detour. |

The old global *Movement Skill Min/Max Distance*, *Clearance Margin*, *Dash Shortcut Path Ratio* and *Jump Cost* settings are gone. The first time a skill is loaded they are copied onto it (and the known per-skill values are used for Leap Slam and Shield Charge), so existing setups carry over. The maximum is no longer a setting: it is the skill's own travel distance.

### Jumping obstacles

Leap Slam, Flame Dash and Frostblink can carry you over a gap or low obstacle that you can't walk across. With **Plan Jumps In Pathfinding** on, A* treats those as extra moves: from a cell beside an obstacle it looks along 16 directions for the first walkable cell past it, provided nothing along the way blocks the **targeting** layer (a solid wall stops a jump; a chasm doesn't). The route takes the jump when the walkable detour is longer than the jump plus the skill's **Jump Path Cost**.

How the two debug layers (Render > Show Terrain / Walkable Debug) are read:

- **Walkable layer:** `0` = blocked, **any other value is walkable**. 5 is open floor and 1–4 are the margin cells nearer a wall; you can walk and stand on all of them.
- **Terrain layer:** **`5` = can be jumped over**, whether or not the cell is walkable. Anything lower is solid (pillars, thick walls) and stops a jump.

So in a row where terrain reads `5 5 5` and walkable reads `5 0 5`, you can't walk from the first cell to the third, but you can jump it. A jump may fly over any run of cells that are all terrain 5, as long as it ends on walkable ground within the skill's reach. It prefers to land on open floor (walkable 5), looking up to 8 cells inland from the lip of the gap, and takes the best ground it finds if none is fully open. Measured on a real zone: a 14-cell (152 unit) band separated two areas by a **464-cell walk**; with jumping it is a **15-cell** route.

Each movement skill has its own **Max Jump Distance** (world units, in the Movement Skills list). `0` means that skill is never used for jumps. Flame Dash, Frostblink, Leap Slam, Blink Arrow and Lightning Warp start at 400 / 500 / 660 / 500 / 450 — **Leap Slam's 660 was measured in game; the rest are estimates, not read from the game**. The real reach depends on your gems and gear, so set them to what you actually see. Too low just misses shortcuts; too high and the character falls short of the landing.

How it behaves:
- Jumps are planned with the skills that are **set up** (enabled, Max Jump Distance above 0), whether or not they are on cooldown that instant - a 3 s cooldown is usually over by the time the takeoff is reached.
- The character walks to the takeoff cell (a tight tolerance, unlike normal waypoints), then uses the shortest-range ready skill that reaches the landing, aimed at it.
- **The route adjusts while moving.** If no jump skill is ready at the takeoff it waits up to the longest **Wait For Cooldown** among the jump skills; after that it drops the jump, bans that landing, switches jumps off for about 8 s so it doesn't plan the same gap again, and replans on foot. If the jump skills are disabled, swapped off the bar or shortened *while* the route is being walked, it replans straight away. When a skill reappears that wasn't there at planning time, it replans to take the shortcut.
- If the landing isn't reached within ~2.5 s of the first attempt, that landing is banned for the route and the path is replanned around it.

### Straighter paths and hop-sized aiming

Movement skills work best along long straight lines, so two things shape the route for them:

- **Path Straightness** (Movement settings) charges A* a cost for every change of direction, so among routes of similar length it picks the one with the fewest, longest straight runs. Measured on a real campaign zone: the raw route went from about **7 bends to 2**, and total turning along the smoothed route fell from about **75° to 53°**, with no change in route length and no measurable search cost. It levels off quickly on maps with long corridors, so the default of 4 is already on the plateau; raise it if you still see wiggles. It trades off against **Wall Clearance** — both are soft preferences.
- **Travel Distance Per Use** (per skill, in the Movement Skills list) is how far one use of that skill carries you — e.g. Leap Slam 100. While walking a path the cursor is aimed *that far ahead* along the route instead of at the next waypoint, so each use covers a full hop. Only that hop has to be clear of obstacles, and the skill's minimum distance applies to the hop. Without it, a bend a few hundred units ahead vetoed a 100-unit leap, because the clearance check covered the whole distance to the waypoint. `0` = fall back to the skill's **Max Jump Distance**, and if that is `0` too the skill aims at the waypoint as before. Ground-dash skills (Leap Slam, Shield Charge, Whirling Blades) need **Use For Travel** on.

### Per-skill hitbox clearance

Some movement skills refuse to fire when the character is too close to an obstacle, as if their hitbox were bigger — you have to step around the corner before they work again. Each ground-dash skill has a **Hitbox Clearance (cells)** setting that simulates this (1 cell ≈ 11 world units):

- Before the skill is used, **every cell within that radius** of the character and of the whole hop must be walkable. That includes where you're standing *now*, so hugging a wall or a corner blocks the skill until you've moved clear. The plugin just keeps walking and tries again on the next input, which is the "move around the corner" you'd otherwise do by hand.
- Set it per skill, to whatever that skill actually needs. When one skill is blocked, the next enabled ground-dash skill is tried with its own radius.
- `0` = only the cells on the line itself have to be walkable. Cell values count 1…5 with distance from a wall, so a radius of 5 means "open floor only". Known skills start with a value (Shield Charge 4, Leap Slam 0.5).
- It applies to skills that *collide* with obstacles (Leap Slam, Shield Charge, Whirling Blades), not to blinks.
- Keep **Wall Clearance** at least as large as the biggest hitbox radius you set. If the route is allowed to hug walls closer than a skill's radius, that skill never gets enough room along it.

If a skill never fires, turn on **Show Detailed Debug**: the log names the first blocked cell, whether it's at the start of the hop or along it, and the radius that was used.

### Stuck recovery (Navigator)

If the character should be moving but stays within ~40 units of one spot, navigation escalates, once per episode (any real progress resets it):

1. **1.2 s** - log one `STUCK` line: health, whether the character is moving, hostile monsters within 300 units (and the nearest), which skill keys Windows still sees held, and the last aim point. Any skill key left down is released.
2. **2.5 s** - movement skills are switched off for 6 s and the character just walks.
3. **4 s** - steps aside to the most open ground that isn't toward the blocked direction (up to 12 cells), then replans from there.
4. **9 s** - gives up with a status saying where it stopped, rather than standing still until something kills the character.

Plain `Move` clicks are logged too (`Walking (Move) toward ...`, at most once a second), so the log now shows the difference between "walking" and "doing nothing". A waypoint that does not project onto the screen is aimed at a nearer point on the same line instead of being skipped silently, which could otherwise leave a stationary character waiting for a point that only comes on screen once it moves.

### Why a movement skill never fires while pathfinding

While following a path, the skill is judged against the **next waypoint** (or, if the skill has a Travel Distance Per Use, the hop toward it) — so a hop only qualifies if it is at least that skill's **Minimum Distance Per Use** long. Two settings decide how far out that waypoint is:

- **Max Waypoint Distance** is the ceiling. A* produces a route cell by cell; waypoints are placed at the furthest cell each one still has a clear walkable line to, so corners stay tight (the line breaks there) and straight stretches collapse into a single long hop. Raise this to get hops worth dashing.
- **Keep Within Distance × 1.5** is the floor — waypoints closer than that count as already reached and are skipped. Waypoint spacing below that ceiling has no effect, so if you follow very tightly, lower Keep Within Distance too.

Worth knowing for **ground dashes** specifically: they also need the skill's **Hitbox Clearance** to be clear along the hop (the whole way to the waypoint if the skill has no Travel Distance set), so a very long waypoint distance can make them fire *less* often in cluttered terrain. Blinks are unaffected by that, but they deliberately only fire where the walkable line is blocked — which a path waypoint never is, by construction — so on a path they only come into play through the skill's Shortcut Path Ratio.

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
Distance alone no longer causes this. If the quick search runs out of budget, FollowHer runs a whole-zone search instead (a brief hitch, at most once every 5 s) rather than walking toward "the closest explored cell" — in a winding level that is often a dead end. Only a genuinely disconnected target reports it. Two things can still legitimately differ from Radar: it optionally paths through **triggerable blockades** (its *Clear Triggerable Blockades* option, on by default, opens doors and blockades before pathing) whereas FollowHer reads the raw walkable grid, and its route ignores whether *you* can open what's in the way.

**The target navigator lists nothing.**
The status line next to **Refresh** says why: Radar not loaded or disabled (it must be installed **and enabled** — it's what supplies the list), or Radar has no targets for this area. If it reports a fallback, `targets.json` wasn't found (set **Navigation > Radar targets.json Path**) and room-scoped targets are unavailable. Right after a zone load it can take a moment before Radar has processed the area; the window retries on its own, or press **Refresh**.

**A skill's settings reset after a weapon swap or zone change.**
The skill lists show only what's on the skill bar right now, and are rebuilt from it. Settings for skills that aren't on the bar (the other weapon set) are kept aside and restored when the skill comes back, so Click-instead-of-hold, Hitbox Clearance, jump and travel distances survive a swap. They're matched by skill name, so renamed or replaced skills start fresh.

**A specific movement skill isn't recognized.**
Skills are read off the skill bar on area load. Skill-name matching is case/space-insensitive and checks both display and internal names; if one still isn't picked up, note its internal name and it can be added.

---

*Early-stage plugin — expect rough edges, and read what a setting does before turning it on.*
