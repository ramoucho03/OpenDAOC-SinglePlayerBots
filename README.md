# OpenDAOC — Single-Player Bots

A heavily-modified fork of [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) focused on making **Dark Age of Camelot playable solo or in small groups by having AI-driven companions (MimicNPCs) behave as real players** — same classes, same specs, same combat math, same group rewards.

If you came here looking for the upstream OpenDAoC server, go to the [OpenDAoC project](https://github.com/OpenDAoC). This fork has diverged significantly: the bot system is a first-class subsystem, an optional module system has been added, large parts of the engine have been refactored to treat bots polymorphically, and the strategy/action/trigger framework is being grown into a full utility-AI brain inspired by [mod-playerbots](https://github.com/mod-playerbots/mod-playerbots).

---

## 🇫🇷 Présentation rapide

**OpenDAOC SinglePlayerBots** est un fork lourdement modifié d'OpenDAoC orienté **jouer DAoC en solo** : un joueur peut monter un groupe complet de bots (MimicNPC) qui se comportent comme des vrais joueurs — classes du jeu, specs réelles, formules de combat identiques, distribution loot/XP/RP comme un groupe normal.

**Ce que ce fork ajoute par rapport à OpenDAoC officiel :**

- Système de **bots MimicNPC** complet (classes joueur, brains FSM, rôles de groupe)
- Framework **Bot AI v2** d'inspiration mod-playerbots (stratégies / triggers / actions composables par classe)
- Stratégies de rôle par classe (`healer`, `tank`, `melee_dps`, `ranged_dps`, `caster_dps`, `cc`) configurables via server properties
- Couche d'**immersion** : callouts localisés (FR / EN), emotes contextuels, demande de cure publique
- **Coordination cross-bots** : focus fire automatique sur le main assist, callout perte d'aggro tank
- Système de **modules** (`IGameModule`) pour ajouter des features sans toucher au cœur
- Refacto polymorphique (virtuelle `GameNPC.IsMimic`) supprimant ~20 `is MimicNPC` épars dans le moteur
- Plusieurs **fixes de concurrence** (`EffectService` mutation Dictionary, `PacketProcessor` cache race, `GameClient` log)

**Commandes bot principales :**

| Commande | Effet |
|---|---|
| `/mcreate <classe> [niveau]` | Crée un bot |
| `/mgroup [royaume] [nb] [niveau]` | Crée un groupe complet |
| `/mlfg [index]` | Liste les bots disponibles à proximité, en invite un |
| `/mcamp set` | Pose un camp PvE sur le ground target |
| `/mrole tank\|assist\|cc\|puller` | Assigne un rôle au bot ciblé |
| `/msummon` | Téléporte le groupe sur le joueur |
| `/mstrategy enable\|disable <clé>` | Active / désactive une stratégie sur le bot ciblé |
| `/mhelp` | Affiche la liste complète des commandes |

Pour la doc technique complète et les détails d'architecture, voir les sections anglaises ci-dessous et le [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md) (français).

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

The shipped `docker-compose.yml` builds the server directly from this repository, seeds the schema into a one-shot sidecar, and starts a MariaDB beside it. One command, three services, no manual setup.

```bash
# clone, then:
docker compose up -d --build
```

Ports: TCP `10300` (game login + traffic), UDP `10400` (game UDP channel). Mapped both at the host on the same numbers.

### What runs

| Service | Role |
|---|---|
| `db-seed` | One-shot sidecar. Built from the same image as the gameserver, copies the embedded `combined.sql` into a shared volume, exits 0. Reused on every `compose up` but `cp -n` keeps the seed idempotent. |
| `db` | MariaDB 10.6. Waits on `db-seed` completion via `service_completed_successfully` so the SQL is guaranteed in place before MariaDB's first-run `initdb` ever reads the directory. Exposes a `mariadb-admin ping` healthcheck. |
| `gameserver` | The DOL server. Waits on the DB being **healthy** (not just started) so its first dotnet log is from a successful DB connection, not an exception trace. `restart: unless-stopped` covers transient hiccups. |

`db-seed` and `gameserver` share the same `opendaoc-fork:latest` image tag and the same `build:` context, so docker compose builds the image once.

### Tuning

The compose file ships with sensible .NET 10 runtime knobs for the high-allocation game-loop workload (`gcServer=1`, `gcConcurrent=1`, tiered PGO, pre-warmed thread pool with 32 min / 256 max workers). Adjust the `DOTNET_ThreadPool_Force*` values to your VM core count.

### Database schema

The Dockerfile clones [`OpenDAoC-Database`](https://github.com/OpenDAoC/OpenDAoC-Database) over HTTPS with TLS verification on, concatenates every `.sql` from `opendaoc-db-core/` into a single `combined.sql`, and bakes it into the runtime image. The `db-seed` sidecar copies it into the volume MariaDB watches for first-run initialisation.

For AF buffs to work you need the latest schema — apply [this commit](https://github.com/OpenDAoC/OpenDAoC-Database/commit/c6153398bf65faa61b665b6b4cae68b5fa8c0862) if you're upgrading manually. To fully reset the world: `docker compose down -v` (the `-v` nukes the data volume too).

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

## Testing a feature branch without merging to main

Active development happens on feature branches (e.g. `claude/optimize-dol-server-gho1T`). You can run a branch end-to-end without merging — pick whichever option fits your workflow.

### Option 1 — Local build (fastest iteration loop)

```bash
git fetch origin claude/optimize-dol-server-gho1T
git checkout claude/optimize-dol-server-gho1T

cp CoreServer/config/serverconfig.example.xml CoreServer/config/serverconfig.xml
dotnet build DOLLinux.sln -c Release
dotnet test DOLLinux.sln -c Release --no-build

# Run against an already-provisioned MariaDB / SQLite instance:
dotnet build/CoreServer/Release/lib/CoreServer.dll
```

Switch back with `git checkout master` when you're done. No merge, no PR required.

### Option 2 — Docker compose pulling the remote branch

The compose file has **two** services that share the build context (`db-seed` and `gameserver`). Edit the same `context:` line on **both** so the seed sidecar and the running server come from the same code:

```yaml
  db-seed:
    build:
      context: https://github.com/ramoucho03/OpenDAOC-SinglePlayerBots.git#claude/optimize-dol-server-gho1T
  gameserver:
    build:
      context: https://github.com/ramoucho03/OpenDAOC-SinglePlayerBots.git#claude/optimize-dol-server-gho1T
```

Then:

```bash
docker compose up -d --build
```

Docker clones the branch fresh on every `--build`, so any new push to that branch is picked up by re-running the same command. Revert both lines to `#master` to swap back.

### Option 3 — Docker compose against your local checkout

Useful when you have uncommitted changes you want to test before pushing.

```yaml
  db-seed:
    build:
      context: .          # Use the current working tree
      dockerfile: Dockerfile
  gameserver:
    build:
      context: .
      dockerfile: Dockerfile
```

```bash
git checkout claude/optimize-dol-server-gho1T
docker compose up -d --build
```

Every rebuild uses whatever is in your working copy — staged, unstaged, even untracked files. Don't forget to revert `docker-compose.yml` before pushing.

### Testing a specific phase in isolation

The Bot AI v2 work is split across phases A–E with one commit per phase ([see Roadmap status](#roadmap-status)). To bisect or roll back to a single phase:

```bash
git fetch origin claude/optimize-dol-server-gho1T

# Phase A only (healer strategy, no other roles)
git checkout fc98448

# Through Phase C (healer split into granular triggers)
git checkout d65da20

# Through Phase E (full coordination layer — branch tip)
git checkout claude/optimize-dol-server-gho1T
```

Each phase compiles and passes the existing test suite on its own, so any of them is a valid starting point for in-game validation.

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
| `awareness` | Self callouts (low HP/mana/end, "need cure" when self-afflicted), pulling/tank-engage chat + emote, idle banter, salute when the camp is ready |
| `assist` | Two bindings: re-acquire the assist target when the bot has no live target, and switch off the current target when the main assist switches mob (Phase E — keeps focus fire tight on broken-mez adds, etc.) |
| `support` | Localized callouts: announce a critical/mezzed group member by name, and signal incoming CC. Designed to be active on a single bot (leader / main assist) to avoid chat spam |
| `camp` | Glue layer for `/mcamp` |

All chat lines come from translation keys (`Mimic.Chat.*`) under [`GameServer/language/EN/Mimic.txt`](GameServer/language/EN/Mimic.txt) and `FR/Mimic.txt`. Each recipient sees the line in their account language; bots pick a random variant per execution so they don't sound robotic.

### Bot AI v2 role strategies (opt-in per class)

Each role is enabled at bot creation when the bot's class appears in the matching server-property CSV. Strategies are composable — a Druid runs `healer` + `caster_dps`, a Bard runs `healer` + `cc`, a Reaver runs `tank` + `melee_dps`, a Friar runs `healer` + `caster_dps`, and so on. Pure tanks like the Paladin stay `tank`-only; assassins like Infiltrator / Nightshade / Shadowblade stay `melee_dps`-only.

| Key | Priority | Cooldown | Drives | Default classes |
|---|---:|---:|---|---|
| `healer` | 100 / 95 / 92 / 90 / 85 | 200–1500 ms | `CheckHeals` split into 5 priority bindings: critical / mezz / poison / disease / low | Cleric, Friar, Heretic, Druid, Bard, Warden, Mentalist, Healer, Shaman |
| `cc` | 85 | 750 ms | `CheckSpells(CrowdControl)` when the group has tracked CC targets | Sorcerer, Minstrel, Theurgist, Enchanter, Bard, Mentalist, Animist, Druid, Runemaster, Spiritmaster, Warlock, Healer, Vampiir |
| `caster_dps` | 75 | 600 ms | `CheckSpells(Offensive)` (nuke rotation) while engaged | Wizard, Theurgist, Cabalist, Sorcerer, Necromancer, Heretic, Eldritch, Enchanter, Mentalist, Animist, Bainshee, Valewalker, Runemaster, Spiritmaster, Bonedancer, Warlock, Thane |
| `tank` | 70 / 65 | 500 ms / 12 s | Defensive spell/style cycle while engaged + lost-aggro callout when the target switches to another group member | Armsman, Paladin, Reaver, Hero, Warden, Champion, Warrior, Thane |
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

## Sprint and follow

Bots in follow mode mirror their human leader's sprint state every tick (`MimicState.MirrorLeaderSprint` is called from `MimicBrain.Think` regardless of FSM state, so a bot in ROAMING / WAKING_UP / AGGRO keeps up too — the cached human-leader lookup refreshes every 2 s).

What's special in this fork is **endurance bookkeeping for grouped bots**. Live DAoC groups sustain infinite sprint by stacking an **Endurance Regen Potion** with the **Long Wind RA** (the potion supplies the regen, the RA cancels the drain). A grouped bot can't realistically buy either — so `MimicNPC.EnduranceRegenerationTimerCallback` reads the leader's setup at tick time and mirrors it onto the bot's sprint math:

- If the leader has `eEffect.EnduranceRegenBuff` active (potion or chant), the bot's regen is bumped to at least the leader's `EnduranceRegenerationAmount` for the sprint calculation.
- If the leader owns `AtlasOF_LongWindAbility`, the bot uses **whichever Long Wind is stronger** (its own or the leader's) when computing the sprint drain — so a level-5 RA on the leader zeroes the bot's drain too.
- No permanent buffs are applied: these values only influence this single regen tick. The bot's stat sheet is untouched, the leader's potion isn't consumed twice, and a non-buffed bot still drains normally and eventually falls behind — exactly like a real groupmate.

The previous behaviour was a brute-force `if (body.Endurance < 25) body.Endurance = body.MaxEndurance` inside `MirrorLeaderSprint`. It kept bots sprinting forever but flickered the player's group-endurance UI (the refill spammed `Group.UpdateMember` packets at ~2 Hz). That hack is gone now.

---

## Death and resurrection

Dead bots behave like dead players, not like NPCs that despawn instantly.

When a bot dies while grouped with a player:

1. The corpse stays in the world for a configurable window. With a rezzer in the group the default is **60 s**, without one it's **15 s**. Tune with `bot_rez_wait_seconds` and `bot_rez_wait_no_healer_seconds`.
2. Any group member with a `Resurrect` spell — bot healer (Cleric, Druid, Friar, Bard, Heretic, Healer, Warden…) or real player — can target the corpse and cast it. Bots auto-accept; players see the usual accept/decline dialog.
3. Healer bots actively try to rez during this window: `MimicBrain.CheckResurrect()` is called from the FOLLOW, AGGRO, CAMP and city states, so a Cleric will drop a swing or break a nuke to cast a rez. `IsRezzingTrigger` plus `Mimic.Chat.Rezzing.*` make the healer announce "rezzing — protect me" so the group doesn't break the cast.
4. **Combat rezzing is allowed.** It's risky (the rezzer is a stationary target) but realistic — an experienced healer drops everything to rez, and that mirrors what a real player would do.
5. If the timeout expires without a rez, the bot **releases to bind**. Because bots don't have a bindstone, the realistic equivalent of `/release` is to leave the group and despawn — the player can `/mlfg` or `/mcreate` a fresh bot afterwards. Behaviour controlled by `bot_rez_timeout_behavior`:
   - `release` (default): announce `Mimic.Chat.ReleaseToBind.*` to the group, `Group.RemoveMember`, then `Delete()`.
   - `revive`: teleport the bot back to its owner at 50% vitals and keep it in the group. Pre-existing softer behaviour, useful on long PvE runs where re-inviting bots is tedious.

Dead **player** in a group with a bot rezzer works the same way: `CheckResurrect` scans every dead group member, not only mimics. The bot will run to the corpse, cast Resurrect, and the player gets the standard accept/decline dialog.

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

## Roadmap status

The Bot AI v2 layer is being grown in phases. Each phase ships behind the per-class CSV opt-ins so adoption is gradual and rollback is cheap.

| Phase | Status | What it adds |
|---|---|---|
| A | shipped | Strategy framework first role: `healer` drives `CheckHeals` |
| B | shipped | `tank`, `melee_dps`, `ranged_dps`, `caster_dps`, `cc` role strategies covering the rest of the archetypes |
| C | shipped | `healer` split into 5 priority bindings (critical / mezz / poison / disease / low) for diagnostic visibility and per-reason cooldowns; new `GroupMemberDiseasedTrigger` and `GroupMemberPoisonedTrigger` |
| D | shipped | Immersion layer: every announce now uses localized translation keys (per-recipient language, random variant), bot publicly asks for a cure when self-mezzed/diseased/poisoned, tank emote (`/bangonshield`) on engage, salute emote when the camp is ready |
| E | shipped | Cross-bot coordination — DPS bots actively switch off their current target when the main assist switches mob (`BotTargetDiffersFromAssistTrigger`); tanks publicly call lost aggro when their mob hits another group member (`TankLostAggroTrigger`) |
| Rez | shipped | Bots die like players: rez-able corpse window (60 s with rezzer / 15 s without), healers actively cast Resurrect on dead bots AND dead players in the group, "release to bind" semantics on timeout (leave group + despawn — configurable via `bot_rez_timeout_behavior`). See [Death and resurrection](#death-and-resurrection). |
| Sprint | shipped | Bots in follow inherit their human leader's Endurance Regen Potion + Long Wind RA at the tick level — leader running infinite-sprint kit ⇒ bots stay topped up; un-buffed leader ⇒ bots drain realistically and fall behind. Replaces the previous endurance-refill hack which flickered the player's group UI. See [Sprint and follow](#sprint-and-follow). |
| F | planned | CC distribution (claim-and-cast so two bots don't mez the same add), kick rotation on enemy casters, travel / quest / gather autonomy à la mod-playerbots |

Class roles in the role CSVs were validated against multiple DAoC 1.65 sources (darkageofcamelot.com Class Library, Camelot Herald wiki, ZAM Allakhazam, Uthgard / Disorder / Phoenix / Eden community guides) — see commit `bd02702` for the full audit and the corrections that were applied.

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
