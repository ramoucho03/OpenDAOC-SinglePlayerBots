# OpenDAOC — Single-Player Bots

A heavily-modified fork of [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) focused on making **Dark Age of Camelot playable solo or in small groups by having AI-driven companions (MimicNPCs) behave as real players** — same classes, same specs, same combat math, same group rewards.

If you came here looking for the upstream OpenDAoC server, go to the [OpenDAoC project](https://github.com/OpenDAoC). This fork has diverged significantly: the bot system is a first-class subsystem, an optional module system has been added, large parts of the engine have been refactored to treat bots polymorphically, and the strategy/action/trigger framework is being grown into a full utility-AI brain inspired by [mod-playerbots](https://github.com/mod-playerbots/mod-playerbots).

---

## What this fork adds on top of OpenDAoC

| Subsystem | Upstream | This fork |
|---|---|---|
| Bot system | none | `MimicNPC` — full player-class bots with FSM brain (`MimicBrain`) and group roles (`MimicGroup`) |
| Bot AI | n/a | Strategy / Action / Trigger framework + per-class **Bot AI v2** role strategies |
| Engine bot coupling | n/a | Polymorphic `GameNPC.IsMimic` virtual, no `is MimicNPC` scattered across spells/styles/calculators |
| Extension points | hard-coded init in `GameServer.Start` | `IGameModule` registry with dependency-ordered init, side-by-side with legacy init |
| Stability fixes | upstream | several concurrency fixes on `EffectService`, `GameClient`, `PacketProcessor` |
| Battlegrounds | manual | automated Thidranki spawning (`/mbattle`) |
| Crafting / housing / quests | upstream | unchanged |

A French-language deep-dive of the codebase, including where to go to change combat / spells / rewards / group rules, lives in [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md).

---

## Quick start (Docker)

The shipped `docker-compose.yml` builds the server directly from this repository and starts a MariaDB beside it.

```bash
# clone, then:
docker compose up -d --build
```

Ports: TCP `10300` (game), UDP `10400` (game).

The compose file ships with sensible .NET 10 runtime tuning for high-allocation game-loop workloads (`gcServer=1`, `gcConcurrent=1`, tiered PGO, pre-warmed thread pool). Edit `docker-compose.yml` to point at your own image registry or to switch back to the upstream `ghcr.io/opendaoc/opendaoc-core` image.

Database schema: the Dockerfile clones [`OpenDAoC-Database`](https://github.com/OpenDAoC/OpenDAoC-Database) and concatenates the SQL files into a single bootstrap script. For AF buffs to work you need the latest schema — apply [this commit](https://github.com/OpenDAoC/OpenDAoC-Database/commit/c6153398bf65faa61b665b6b4cae68b5fa8c0862) if you're upgrading manually.

---

## Quick start (local build)

Requires .NET 10 SDK.

```bash
# one-time: copy the example server config
cp CoreServer/config/serverconfig.example.xml CoreServer/config/serverconfig.xml

# build everything
dotnet build DOLLinux.sln -c Release

# run the tests
dotnet test DOLLinux.sln -c Release --no-build

# run the server
dotnet build/CoreServer/Release/lib/CoreServer.dll
```

---

## Playing with bots

Bots are **MimicNPC** instances: they have a player class, real specs, a real inventory, and they cast / fight / heal using the same code paths as a real player. They implement `IGamePlayer` so they're treated as players by group rules, rewards and most spells.

### Most-used commands

| Command | What it does |
|---|---|
| `/mcreate <class> [level] [spec] [inv]` | Spawn a single bot |
| `/mgroup [realm] [count] [level] [preventCombat]` | Spawn a full group |
| `/mlfg [index]` | List nearby bots looking for a group; pick one to invite |
| `/msummon` | Teleport your bot group to you (zone changes, ports) |
| `/mfollow` | Cancel camp/pull, return to follow-the-leader |
| `/mattack` | Force your bot group to attack your current target |
| `/mclear` | Despawn every bot you created |

### Group roles

| Command | Role |
|---|---|
| `/mrole leader\|tank\|assist\|cc\|puller` | Assign a tactical role to the targeted bot |
| `/mguard <name>` | Stand-guard the named target (requires Guard ability) |
| `/mintercept` | Assign intercept to current target |
| `/mprotect` | Assign protect to current target |
| `/mheal` | Toggle the healer flag on the targeted bot |

### PvE camping

| Command | What it does |
|---|---|
| `/mcamp set` | Use ground target as a camp anchor; bots wait and aggro mobs in range |
| `/mcamp remove` | Clear the camp; bots go back to follow mode |
| `/mcamp aggrorange <1-6000>` | Tune the aggro radius (default 250 in dungeons, 550 outside) |
| `/mcamp filter <green\|blue\|yellow\|orange\|red\|purple>` | Min con the puller pulls |
| `/mpull` | Set camp + pull current target |
| `/mpullfrom set\|remove` | Optional secondary point bots pull from |

### Bot behaviour toggles

| Command | What it does |
|---|---|
| `/mpvp <true\|false>` | When true, bot ignores mobs until you're attacked |
| `/mpc <true\|false> [group]` | Force a bot (or group) into passive / non-aggro mode |
| `/mstrategy list\|enable\|disable <key>` | Live-toggle strategy modules on a targeted bot |

### Battlegrounds

| Command | What it does |
|---|---|
| `/mbattle thid <start\|stop\|clear>` | Auto-spawn three realms in Thidranki (only BG implemented) |
| `/mbstats thid` | BG occupancy snapshot |

### Help

`/mhelp` (alias `/mimichelp`) prints the live command list with descriptions.

---

## Bot AI v2 — strategy framework

This is the area that is actively converging towards mod-playerbots-style decision making.

A bot has two parallel brains:

1. **FSM (legacy)** — `MimicBrain` + `MimicState` with explicit transitions (`WAKING_UP`, `AGGRO`, `CAMP`, `FOLLOW_THE_LEADER`, …). Still owns the full combat / heal / spell selection logic.
2. **Strategy framework (additive)** — `BotStrategyManager` ticks before the FSM each tick and runs a set of `IBotStrategy` modules. Each strategy contributes `(IBotTrigger, IBotAction, priority, cooldown, exclusive)` bindings. The manager evaluates them top-down once per tick and lets the highest-priority binding whose trigger fires execute its action.

### Built-in meta strategies (enabled on every bot by default)

| Key | Purpose |
|---|---|
| `survival` | Sit to recover, stand on engage |
| `awareness` | Self callouts, idle banter |
| `assist` | Focus the group's main assist target |
| `support` | Announce mezz / criticals / CC targets in group chat |
| `camp` | Glue layer for `/mcamp` |

### Bot AI v2 role strategies (opt-in per class)

Each role is enabled at bot creation when the bot's class appears in the matching server-property CSV. Strategies are composable — a Druid runs `healer` + `caster_dps`, a Bard runs `healer` + `cc`, a Reaver runs `tank` + `melee_dps`, a Friar runs `healer` + `caster_dps`, and so on. Pure tanks like the Paladin stay `tank`-only; assassins like Infiltrator / Nightshade / Shadowblade stay `melee_dps`-only.

| Key | Priority | Cooldown | Drives | Default classes |
|---|---:|---:|---|---|
| `healer` | 100 / 95 / 92 / 90 / 85 | 200–1500 ms | `CheckHeals` split into 5 priority bindings: critical / mezz / poison / disease / low | Cleric, Friar, Heretic, Druid, Bard, Warden, Mentalist, Healer, Shaman |
| `cc` | 85 | 750 ms | `CheckSpells(CrowdControl)` when the group has tracked CC targets | Sorcerer, Minstrel, Theurgist, Enchanter, Bard, Mentalist, Animist, Druid, Runemaster, Spiritmaster, Warlock, Healer, Vampiir |
| `caster_dps` | 75 | 600 ms | `CheckSpells(Offensive)` (nuke rotation) while engaged | Wizard, Theurgist, Cabalist, Sorcerer, Necromancer, Heretic, Eldritch, Enchanter, Mentalist, Animist, Bainshee, Valewalker, Runemaster, Spiritmaster, Bonedancer, Warlock, Thane |
| `tank` | 70 | 500 ms | `CheckSpells(Defensive)` (taunts, peels) while engaged | Armsman, Paladin, Reaver, Hero, Warden, Champion, Warrior, Thane |
| `melee_dps` | 60 | 1000 ms | `CheckSpells(Offensive)` for melee-class procs / hybrid spells | Infiltrator, Mercenary, Minstrel, Blademaster, Nightshade, Vampiir, Valewalker, Berserker, Savage, Shadowblade, Skald, Valkyrie, MaulerAlb, MaulerMid, MaulerHib |
| `ranged_dps` | 60 | 1000 ms | `CheckSpells(Offensive)` for archer procs while engaged | Scout, Ranger, Hunter |

Note on hybrids: chain-armor "tanks" (Warden, Thane, Reaver) are not strict plate tanks but are kept in `tank` because they hold the iconic peel/guard role in 1.65 group play. Warden, Mentalist and Heretic carry real heal spec lines (Regrowth, Mana, Rejuvenation) and therefore appear in `healer` alongside the obvious cleric/druid/healer trio.

Server properties controlling these whitelists:

```
bot_ai_v2_healer_classes
bot_ai_v2_tank_classes
bot_ai_v2_melee_dps_classes
bot_ai_v2_ranged_dps_classes
bot_ai_v2_caster_dps_classes
bot_ai_v2_cc_classes
```

Priorities mean: in a single tick, when several bindings could fire, the higher one wins. Exclusive bindings end the tick after a successful execute so lower-priority chatter doesn't immediately stack on top. The CSV is read at bot spawn — change it, then respawn (or `/mstrategy enable …` manually) to apply.

You can flip strategies live on a targeted bot with:

```
/mstrategy list
/mstrategy enable healer
/mstrategy disable awareness
```

The trigger / action / strategy contracts live under [`GameServer/custom/MimicNPC/ai/Strategies/`](GameServer/custom/MimicNPC/ai/Strategies/). Anyone can plug in a third-party strategy by registering it with `BotStrategyRegistry.Register`.

---

## Game-server module system

For features that aren't bot-specific, an optional `IGameModule` registry is wired into `GameServer.Start`:

```csharp
[GameModule]
public sealed class MyFeatureModule : IGameModule
{
    public string Name => nameof(MyFeatureModule);
    public IEnumerable<Type> DependsOn => new[] { typeof(SomeOtherModule) };

    public bool Init(IGameServerContext ctx) { /* … */ return true; }
    public void Shutdown() { /* … */ }
}
```

Modules are discovered by reflection across the GameServer assembly and every compiled script assembly, sorted topologically (Kahn's algorithm + cycle detection), and initialised in order. Failures are isolated — a buggy module logs and is skipped instead of bringing the server down.

Module entry point: [`GameServer/Managers/Modules/`](GameServer/Managers/Modules/). The reference implementation `SampleLoggingModule` ships disabled (`Enabled = false`) so a stock server has zero behaviour change.

---

## Repository layout

```
CoreBase/         networking, config, logging, FTP, MPK primitives
CoreDatabase/    historical mini-ORM, MySQL/SQLite handlers
CoreServer/      executable / Windows service / startup actions
GameServer/      the game itself (~2600 files)
  ECS-Services/  game-loop services (Npc, Attack, Casting, Effect, Movement, …)
  ECS-Components/ per-entity components driven by those services
  ai/            brains and FSM scaffolding for standard mobs
  custom/MimicNPC/ the bot subsystem (this is where most fork-specific work lives)
  Managers/Modules/ the IGameModule extension point
  packets/       client packet handlers and server packet libraries by protocol version
  serverrules/   PvE / PvP / RvR rule sets
  spells/ styles/ propertycalc/  combat math
Tests/           NUnit tests (utility-level)
Pathing/         pathfinding native bindings
```

For navigation tips and a full picture of where to go to change a specific behaviour, see [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md) (français).

---

## Status / disclaimers

- This fork is **experimental** and command-driven. Several powerful bot commands are accessible at `ePrivLevel.Player` for testing — review `Commands.cs` before opening a public server.
- Combat math is upstream OpenDAoC's; the bot system layers on top without changing damage formulas (with documented exceptions in `propertycalc/` for mimic-specific stat handling).
- The 1.65 patch level is the primary target, but the codebase compiles and runs other ranges.

## License

GPL v3 — see [LICENSE](LICENSE). Inherited from OpenDAoC, itself inherited from [DOLSharp](https://github.com/Dawn-of-Light/DOLSharp).

## Credits

- [DOLSharp](https://github.com/Dawn-of-Light/DOLSharp) — the original Dawn of Light emulator
- [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) — ECS rewrite this fork is based on
- [mod-playerbots](https://github.com/mod-playerbots/mod-playerbots) — design inspiration for the Bot AI v2 strategy layer
