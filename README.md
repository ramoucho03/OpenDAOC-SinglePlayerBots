# OpenDAOC — Single-Player Bots

**🇬🇧 [English](#english) · 🇫🇷 [Français](#français)**

A heavily-modified fork of [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) focused on making **Dark Age of Camelot playable solo or in small groups by having AI-driven companions (MimicNPCs) behave as real players** — same classes, same specs, same combat math, same group rewards.

If you came here looking for the upstream OpenDAoC server, go to the [OpenDAoC project](https://github.com/OpenDAoC). This fork has diverged significantly: the bot system is a first-class subsystem, an optional module system has been added, large parts of the engine have been refactored to treat bots polymorphically, and the strategy/action/trigger framework is being grown into a full utility-AI brain inspired by [mod-playerbots](https://github.com/mod-playerbots/mod-playerbots).

---

# English

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
| `db-seed` | One-shot sidecar. Built from the same image as the gameserver, copies the embedded `combined.sql` into a shared volume, exits 0. Reused on every `compose up` but idempotent. |
| `db` | MariaDB 10.6. Waits on `db-seed` completion via `service_completed_successfully` so the SQL is guaranteed in place before MariaDB's first-run `initdb` ever reads the directory. Exposes a `mariadb-admin ping` healthcheck. |
| `gameserver` | The DOL server. Waits on the DB being **healthy** (not just started) so its first dotnet log is from a successful DB connection. `restart: unless-stopped` covers transient hiccups. |

`db-seed` and `gameserver` share the same `opendaoc-fork:latest` image tag and the same `build:` context, so docker compose builds the image once.

### Tuning

The compose file ships with sensible .NET 10 runtime knobs for the high-allocation game-loop workload (`gcServer=1`, `gcConcurrent=1`, tiered PGO, pre-warmed thread pool with 32 min / 256 max workers). Adjust the `DOTNET_ThreadPool_Force*` values to your VM core count.

### Database schema

The Dockerfile clones [`OpenDAoC-Database`](https://github.com/OpenDAoC/OpenDAoC-Database) over HTTPS with TLS verification on, concatenates every `.sql` from `opendaoc-db-core/` into a single `combined.sql`, and bakes it into the runtime image. For AF buffs to work you need the latest schema — apply [this commit](https://github.com/OpenDAoC/OpenDAoC-Database/commit/c6153398bf65faa61b665b6b4cae68b5fa8c0862) if you're upgrading manually. To fully reset the world: `docker compose down -v` (the `-v` nukes the data volume too).

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

## Testing a feature branch without merging to main

Active development happens on feature branches (e.g. `claude/optimize-dol-server-gho1T`). Three workflows:

**Option 1 — Local build.** `git fetch origin <branch> && git checkout <branch>`, then `dotnet build` + `dotnet build/CoreServer/Release/lib/CoreServer.dll`.

**Option 2 — Docker pulling the remote branch.** Edit the same `context:` line on **both** `db-seed` and `gameserver` services in `docker-compose.yml`:
```yaml
build:
  context: https://github.com/ramoucho03/OpenDAOC-SinglePlayerBots.git#claude/optimize-dol-server-gho1T
```
Then `docker compose up -d --build`. Docker re-clones every `--build`.

**Option 3 — Docker against your local checkout.** Set `context: .` on both services, useful for uncommitted changes.

**Bisecting Bot AI v2 phases.** Each phase is a single commit and compiles independently — `git checkout fc98448` for Phase A only, `d65da20` through Phase C, branch tip for everything.

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
| `/mstrategy list\|add\|remove\|clear <key>` | Live-toggle strategy modules on a targeted bot |

### Battlegrounds

| Command | What it does |
|---|---|
| `/mbattle thid <start\|stop\|clear>` | Auto-spawn three realms in Thidranki (only BG implemented) |
| `/mbstats thid` | BG occupancy snapshot |

### Help

`/mhelp` (alias `/mimichelp`) prints the live command list with descriptions.

## Bot AI v2 — strategy framework

A bot has two parallel brains:

1. **FSM (legacy)** — `MimicBrain` + `MimicState` with explicit transitions (`WAKING_UP`, `AGGRO`, `CAMP`, `FOLLOW_THE_LEADER`, …). Still owns the full combat / heal / spell selection logic.
2. **Strategy framework (additive)** — `BotStrategyManager` ticks before the FSM each tick and runs a set of `IBotStrategy` modules. Each strategy contributes `(IBotTrigger, IBotAction, priority, cooldown, exclusive)` bindings. The manager evaluates them top-down once per tick and lets the highest-priority binding whose trigger fires execute its action.

### Built-in meta strategies (enabled on every bot by default)

| Key | Purpose |
|---|---|
| `survival` | Sit to recover, stand on engage |
| `awareness` | Self callouts (low HP/mana/end, "need cure" when self-afflicted), pulling/tank-engage chat + emote, idle banter, salute when the camp is ready |
| `assist` | Two bindings: re-acquire the assist target when the bot has no live target, and switch off the current target when the main assist switches mob |
| `support` | Localized callouts: announce a critical/mezzed group member by name, signal incoming CC |
| `camp` | Glue layer for `/mcamp` |
| `leader` | Tactical voice of the MainLeader: engage call + Beckon emote on `Pulling → Engaging`, post-combat regen call on `Combat → PostCombat`. Bindings filter on the Leader role so only one bot per group fires the lines |

All chat lines come from translation keys (`Mimic.Chat.*`) under [`GameServer/language/EN/Mimic.txt`](GameServer/language/EN/Mimic.txt) and `FR/Mimic.txt`. Each recipient sees the line in their account language; bots pick a random variant per execution.

### Bot AI v2 role strategies (opt-in per class)

Each role is enabled at bot creation when the bot's class appears in the matching server-property CSV. Strategies are composable — a Druid runs `healer` + `caster_dps`, a Bard runs `healer` + `cc`, a Reaver runs `tank` + `melee_dps`, a Friar runs `healer` + `caster_dps`. Pure tanks like the Paladin stay `tank`-only; assassins like Infiltrator / Nightshade / Shadowblade stay `melee_dps`-only.

| Key | Priority | Cooldown | Drives | Default classes |
|---|---:|---:|---|---|
| `healer` | 100 / 95 / 92 / 90 / 85 | 200–1500 ms | `CheckHeals` split into 5 priority bindings: critical / mezz / poison / disease / low | Cleric, Friar, Heretic, Druid, Bard, Warden, Mentalist, Healer, Shaman |
| `cc` | 85 | 750 ms | `CheckSpells(CrowdControl)`. Skips CCTargets already being CC'd by another group member's in-flight cast (`IsBeingCcedByGroup`) so two CC bots don't lock onto the same add | Sorcerer, Minstrel, Theurgist, Enchanter, Bard, Mentalist, Animist, Druid, Runemaster, Spiritmaster, Warlock, Healer, Vampiir |
| `caster_dps` | 75 | 300 ms | `CheckSpells(Offensive)` nuke rotation, halved cadence so instant casts chain at the brain tick rate | Wizard, Theurgist, Cabalist, Sorcerer, Necromancer, Heretic, Eldritch, Enchanter, Mentalist, Animist, Bainshee, Valewalker, Runemaster, Spiritmaster, Bonedancer, Warlock, Thane |
| `tank` | 70 / 65 | 500 ms / 12 s | Defensive spell/style cycle + lost-aggro callout when the target switches to another group member | Armsman, Paladin, Reaver, Hero, Warden, Champion, Warrior, Thane |
| `melee_dps` | 60 | 500 ms | `CheckSpells(Offensive)` for melee-class procs / hybrid spells | Infiltrator, Mercenary, Minstrel, Blademaster, Nightshade, Vampiir, Valewalker, Berserker, Savage, Shadowblade, Skald, Valkyrie, MaulerAlb, MaulerMid, MaulerHib |
| `ranged_dps` | 60 | 500 ms | `CheckSpells(Offensive)` for archer procs | Scout, Ranger, Hunter |

Note on hybrids: chain-armor "tanks" (Warden, Thane, Reaver) are not strict plate tanks but are kept in `tank` because they hold the iconic peel/guard role in 1.65 group play. Warden, Mentalist and Heretic carry real heal spec lines (Regrowth, Mana, Rejuvenation) and therefore appear in `healer`.

Server properties controlling these whitelists:

```
bot_ai_v2_healer_classes
bot_ai_v2_tank_classes
bot_ai_v2_melee_dps_classes
bot_ai_v2_ranged_dps_classes
bot_ai_v2_caster_dps_classes
bot_ai_v2_cc_classes
```

Live-toggle strategies with `/mstrategy list`, `/mstrategy add healer`, `/mstrategy remove awareness`, `/mstrategy clear`. The trigger / action / strategy contracts live under [`GameServer/custom/MimicNPC/ai/Strategies/`](GameServer/custom/MimicNPC/ai/Strategies/). Third parties register strategies via `BotStrategyRegistry.Register`.

## Sprint and follow

Bots in follow mode mirror their human leader's sprint state every tick. The endurance bookkeeping is special: a grouped bot can't realistically buy an Endurance Regen Potion or the Long Wind RA, so `MimicNPC.EnduranceRegenerationTimerCallback` reads the leader's setup at tick time and mirrors it onto the bot's sprint math:

- If the leader has `eEffect.EnduranceRegenBuff` active, the bot's regen is bumped to the leader's `EnduranceRegenerationAmount`.
- If the leader owns `AtlasOF_LongWindAbility`, the bot uses whichever Long Wind is stronger when computing the sprint drain — a level-5 RA on the leader zeroes the bot's drain too.
- No permanent buffs applied: only this single regen tick is influenced. Non-buffed bot still drains normally and eventually falls behind.

## DPS targeting and AoE

- **Boss veto.** `MimicBrain.CountAoeHostiles` returns the `-1` sentinel when the primary target is `IGameEpicNpc` → `ShouldUseAoe` refuses cluster spells → bot falls through to single-target.
- **AoE only when worth it.** `MimicCombatProfile.DamageAoeMinTargets = 2`. Single-target nukes get `+0` priority score, AoE without a cluster takes `+2` penalty.
- **Focus fire via main assist.** Every DPS bot picks the assist's current target via `BotTargetDiffersFromAssistTrigger`. Switch within 1.5 s.
- **Cadence.** Brain tick is 500 ms in combat. `caster_dps` re-arms every 300 ms, `melee_dps` / `ranged_dps` every 500 ms — aligned with the combat tick so instants chain.
- **Priority order.** `ScoreOffensivePriority`: pet summons → snares → disease → stat debuffs → DoTs → DD-debuff hybrids → bolts → lifedrains → pure DDs. Lower score wins.

## Death and resurrection

When a bot dies while grouped with a player:

1. Corpse stays for a configurable window — **60 s** with a rezzer in the group, **15 s** without (`bot_rez_wait_seconds`, `bot_rez_wait_no_healer_seconds`).
2. Any group member with a `Resurrect` spell (bot or player) can target the corpse. Bots auto-accept; players get the usual dialog.
3. Healer bots actively try to rez during the window via `MimicBrain.CheckResurrect()`, called from FOLLOW / AGGRO / CAMP / city states. Combat rezzing is allowed (realistic — an experienced healer drops everything).
4. On timeout, the bot **releases to bind** by default (`bot_rez_timeout_behavior` = `release`): announce a localized chat line, leave the group, despawn. Alternative `revive` puts the bot back at the owner at 50% vitals.
5. Dead **player** in a group with a bot rezzer works the same way: bot runs to corpse, casts Resurrect, player gets accept/decline dialog.

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

Modules are discovered by reflection across the GameServer assembly and every compiled script assembly, sorted topologically (Kahn's algorithm + cycle detection), initialised in order. Failures are isolated — a buggy module logs and is skipped instead of bringing the server down.

Module entry point: [`GameServer/Managers/Modules/`](GameServer/Managers/Modules/). The reference `SampleLoggingModule` ships disabled.

## Repository layout

```
CoreBase/        networking, config, logging, FTP, MPK primitives
CoreDatabase/    historical mini-ORM, MySQL/SQLite handlers
CoreServer/      executable / Windows service / startup actions
GameServer/      the game itself (~2600 files)
  ECS-Services/  game-loop services (Npc, Attack, Casting, Effect, Movement, …)
  ECS-Components/ per-entity components driven by those services
  ai/            brains and FSM scaffolding for standard mobs
  custom/MimicNPC/ the bot subsystem (most fork-specific work lives here)
  Managers/Modules/ the IGameModule extension point
  packets/       client packet handlers and server packet libraries by protocol version
  serverrules/   PvE / PvP / RvR rule sets
  spells/ styles/ propertycalc/  combat math
Tests/           NUnit tests (utility-level)
Pathing/         pathfinding native bindings
```

See [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md) for navigation tips (in French).

## Roadmap status

| Phase | Status | What it adds |
|---|---|---|
| A | shipped | Strategy framework first role: `healer` drives `CheckHeals` |
| B | shipped | `tank`, `melee_dps`, `ranged_dps`, `caster_dps`, `cc` role strategies |
| C | shipped | `healer` split into 5 priority bindings + new diseased/poisoned triggers |
| D | shipped | Immersion layer: localized callouts, self-cure request, tank/leader emotes |
| E | shipped | Cross-bot coordination — DPS focus switch on main assist swap, tank lost-aggro callout |
| Rez | shipped | Rez-able corpse window, healer auto-cast Resurrect, release-to-bind on timeout |
| Sprint | shipped | Bots inherit the leader's Endurance Regen + Long Wind RA at tick level |
| DPS | shipped | Boss-aware AoE veto + halved DPS cooldowns (300 / 500 / 500 ms) |
| MainCC | shipped | `IsBeingCcedByGroup` dedup so two CC bots don't mez the same add |
| Leader | shipped | `LeaderStrategy` engagement + post-combat callouts filtered on Leader role |
| F | planned | CCTargets thread-safety, kick rotation on enemy casters, travel / quest / gather autonomy |

Class roles in the role CSVs were validated against multiple DAoC 1.65 sources (darkageofcamelot.com Class Library, Camelot Herald wiki, ZAM Allakhazam, Uthgard / Disorder / Phoenix / Eden community guides) — see commit `bd02702`.

## Status / disclaimers

- Experimental and command-driven. Several powerful bot commands are accessible at `ePrivLevel.Player` for testing — review `Commands.cs` before opening a public server.
- Combat math is upstream OpenDAoC's; the bot system layers on top without changing damage formulas (documented exceptions in `propertycalc/` for mimic-specific stat handling).
- 1.65 patch level is the primary target.

## License

GPL v3 — see [LICENSE](LICENSE). Inherited from OpenDAoC, itself inherited from [DOLSharp](https://github.com/Dawn-of-Light/DOLSharp).

## Credits

- [DOLSharp](https://github.com/Dawn-of-Light/DOLSharp) — the original Dawn of Light emulator
- [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) — ECS rewrite this fork is based on
- [mod-playerbots](https://github.com/mod-playerbots/mod-playerbots) — design inspiration for the Bot AI v2 strategy layer

---

# Français

## Ce que ce fork ajoute par-dessus OpenDAoC

| Sous-système | Upstream | Ce fork |
|---|---|---|
| Système de bots | aucun | `MimicNPC` — bots avec vraies classes joueur, brain FSM (`MimicBrain`) et rôles de groupe (`MimicGroup`) |
| IA des bots | n/a | Framework Strategy / Action / Trigger + stratégies de rôle **Bot AI v2** par classe |
| Couplage moteur / bots | n/a | Virtuelle polymorphe `GameNPC.IsMimic`, plus de `is MimicNPC` éparpillés dans spells/styles/calculators |
| Points d'extension | init hardcodé dans `GameServer.Start` | Registry `IGameModule` avec init ordonnée par dépendances, parallèle à l'init legacy |
| Fixes de stabilité | upstream | plusieurs fixes de concurrence sur `EffectService`, `GameClient`, `PacketProcessor` |
| Battlegrounds | manuel | spawning auto Thidranki (`/mbattle`) |
| Craft / housing / quêtes | upstream | inchangé |

Un guide complet du code en français se trouve dans [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md).

## Démarrage rapide (Docker)

Le `docker-compose.yml` fourni build le serveur directement depuis ce dépôt, seed le schéma DB via une sidecar one-shot, et démarre une MariaDB à côté. Une commande, trois services, zéro setup manuel.

```bash
# clone, puis :
docker compose up -d --build
```

Ports : TCP `10300` (login + traffic), UDP `10400` (canal UDP).

### Ce qui tourne

| Service | Rôle |
|---|---|
| `db-seed` | Sidecar one-shot. Build avec la même image que le gameserver, copie `combined.sql` dans un volume partagé, exit 0. Idempotent — relancé sur chaque `compose up` mais ne réécrase pas. |
| `db` | MariaDB 10.6. Attend que `db-seed` ait fini (`service_completed_successfully`) avant son premier `initdb`. Healthcheck `mariadb-admin ping`. |
| `gameserver` | Serveur DOL. Attend la DB **healthy** (pas juste démarrée). `restart: unless-stopped` couvre les pannes transitoires. |

`db-seed` et `gameserver` partagent le tag `opendaoc-fork:latest` et le même `build:` context → l'image est buildée une seule fois.

### Tuning

Le compose inclut des knobs .NET 10 pour la charge game-loop (`gcServer=1`, `gcConcurrent=1`, tiered PGO, thread pool pré-chauffé 32 min / 256 max). Ajuste les `DOTNET_ThreadPool_Force*` selon les cores de ta VM.

### Schéma DB

Le Dockerfile clone [`OpenDAoC-Database`](https://github.com/OpenDAoC/OpenDAoC-Database) en HTTPS avec TLS strict, concatène tous les `.sql` de `opendaoc-db-core/` dans un `combined.sql`, et bake le résultat dans l'image. Pour les buffs AF, applique [ce commit](https://github.com/OpenDAoC/OpenDAoC-Database/commit/c6153398bf65faa61b665b6b4cae68b5fa8c0862) si tu upgrades à la main. Reset complet : `docker compose down -v`.

## Démarrage rapide (build local)

Requiert le SDK .NET 10.

```bash
# une fois : copier la config serveur d'exemple
cp CoreServer/config/serverconfig.example.xml CoreServer/config/serverconfig.xml

# build complet
dotnet build DOLLinux.sln -c Release

# tests
dotnet test DOLLinux.sln -c Release --no-build

# run du serveur
dotnet build/CoreServer/Release/lib/CoreServer.dll
```

## Tester une branche sans merger en main

Le dev se fait sur des branches (`claude/optimize-dol-server-gho1T` par ex). Trois workflows :

**Option 1 — Build local.** `git fetch origin <branche> && git checkout <branche>`, puis `dotnet build` + run.

**Option 2 — Docker sur la branche distante.** Édite la ligne `context:` sur les **deux** services `db-seed` ET `gameserver` :
```yaml
build:
  context: https://github.com/ramoucho03/OpenDAOC-SinglePlayerBots.git#claude/optimize-dol-server-gho1T
```
Puis `docker compose up -d --build`. Docker re-clone à chaque `--build`.

**Option 3 — Docker sur ton checkout local.** Mets `context: .` sur les deux services. Utile pour tester du code non poussé.

**Bisecter les phases Bot AI v2.** Chaque phase est un commit isolé qui compile seul. `git checkout fc98448` = Phase A seule, `d65da20` = jusqu'à Phase C, tip de branche = tout.

## Jouer avec les bots

Les bots sont des `MimicNPC` : ils ont une classe joueur, des vraies specs, un inventaire réel, et castent/combattent/heal via les mêmes chemins de code qu'un joueur. Ils implémentent `IGamePlayer` donc sont traités comme des joueurs par les règles de groupe, les récompenses et la plupart des spells.

### Commandes principales

| Commande | Effet |
|---|---|
| `/mcreate <classe> [niveau] [spec] [inv]` | Crée un bot |
| `/mgroup [royaume] [nb] [niveau] [preventCombat]` | Crée un groupe complet |
| `/mlfg [index]` | Liste les bots LFG à proximité, en invite un |
| `/msummon` | Téléporte le groupe vers toi (changements de zone, ports) |
| `/mfollow` | Annule camp/pull, retour follow leader |
| `/mattack` | Force le groupe à attaquer ta cible courante |
| `/mclear` | Despawn tous les bots que tu as créés |

### Rôles de groupe

| Commande | Rôle |
|---|---|
| `/mrole leader\|tank\|assist\|cc\|puller` | Assigne un rôle tactique au bot ciblé |
| `/mguard <nom>` | Le bot stand-guard la cible nommée (requiert capacité Guard) |
| `/mintercept` | Assigne intercept sur la cible courante |
| `/mprotect` | Assigne protect sur la cible courante |
| `/mheal` | Toggle le flag healer du bot ciblé |

### Camping PvE

| Commande | Effet |
|---|---|
| `/mcamp set` | Pose un camp sur le ground target ; les bots attendent et aggro les mobs en range |
| `/mcamp remove` | Retire le camp ; les bots repassent en follow |
| `/mcamp aggrorange <1-6000>` | Ajuste le rayon d'aggro (défaut 250 en donjon, 550 dehors) |
| `/mcamp filter <green\|blue\|yellow\|orange\|red\|purple>` | Min con que le puller tire |
| `/mpull` | Pose camp + pull la cible courante |
| `/mpullfrom set\|remove` | Point optionnel d'où le bot va pull |

### Toggles de comportement

| Commande | Effet |
|---|---|
| `/mpvp <true\|false>` | Si true, le bot ignore les mobs jusqu'à ce qu'on t'attaque |
| `/mpc <true\|false> [group]` | Force le bot (ou groupe) en mode passif / non-aggro |
| `/mstrategy list\|add\|remove\|clear <clé>` | Toggle live des stratégies sur le bot ciblé |

### Battlegrounds

| Commande | Effet |
|---|---|
| `/mbattle thid <start\|stop\|clear>` | Spawn auto les 3 royaumes à Thidranki (seul BG implémenté) |
| `/mbstats thid` | Snapshot occupation BG |

### Aide

`/mhelp` (alias `/mimichelp`) affiche la liste live des commandes.

## Bot AI v2 — framework strategy

Un bot a deux brains en parallèle :

1. **FSM (legacy)** — `MimicBrain` + `MimicState` avec transitions explicites (`WAKING_UP`, `AGGRO`, `CAMP`, `FOLLOW_THE_LEADER`…). Possède toute la logique combat / heal / sélection de spells.
2. **Framework Strategy (additif)** — `BotStrategyManager` tick avant le FSM à chaque tick et exécute un set de modules `IBotStrategy`. Chaque stratégie contribue des bindings `(IBotTrigger, IBotAction, priorité, cooldown, exclusive)`. Le manager les évalue par priorité décroissante.

### Meta-stratégies built-in (actives sur tous les bots par défaut)

| Clé | Rôle |
|---|---|
| `survival` | Se rassoit pour regen, se relève à l'engage |
| `awareness` | Callouts perso (HP/mana/end bas, "need cure" si afflige soi-même), chat pulling/tank-engage + emote, banter idle, salute quand le camp est ready |
| `assist` | Deux bindings : récupère la cible de l'assist quand le bot n'a plus de cible vive ; bascule sur la nouvelle cible quand le main assist switch |
| `support` | Callouts localisés : annonce un membre critique/mezzé par son nom, signale les CC entrants |
| `camp` | Couche de liaison pour `/mcamp` |
| `leader` | Voix tactique du MainLeader : appel d'engage + emote Beckon sur `Pulling → Engaging`, appel post-combat sur `Combat → PostCombat`. Filtre rôle Leader → un seul bot par groupe parle |

Toutes les phrases chat viennent de clés de traduction (`Mimic.Chat.*`) sous [`GameServer/language/EN/Mimic.txt`](GameServer/language/EN/Mimic.txt) et `FR/Mimic.txt`. Chaque destinataire voit la ligne dans sa langue ; les bots pickent une variante au hasard à chaque exec.

### Stratégies de rôle Bot AI v2 (opt-in par classe)

Chaque rôle est activé à la création du bot quand sa classe apparaît dans le CSV de server property correspondant. Les stratégies sont composables — un Druid a `healer` + `caster_dps`, un Bard `healer` + `cc`, un Reaver `tank` + `melee_dps`, un Friar `healer` + `caster_dps`. Les tanks purs comme le Paladin restent `tank`-only ; les assassins (Infiltrator / Nightshade / Shadowblade) restent `melee_dps`-only.

| Clé | Priorité | Cooldown | Pilote | Classes par défaut |
|---|---:|---:|---|---|
| `healer` | 100 / 95 / 92 / 90 / 85 | 200–1500 ms | `CheckHeals` splitté en 5 bindings priorisés : critical / mezz / poison / disease / low | Cleric, Friar, Heretic, Druid, Bard, Warden, Mentalist, Healer, Shaman |
| `cc` | 85 | 750 ms | `CheckSpells(CrowdControl)`. Skip les CCTargets déjà CC'd par un autre bot du groupe (`IsBeingCcedByGroup`) — deux bots CC ne ciblent pas la même add | Sorcerer, Minstrel, Theurgist, Enchanter, Bard, Mentalist, Animist, Druid, Runemaster, Spiritmaster, Warlock, Healer, Vampiir |
| `caster_dps` | 75 | 300 ms | Rotation `CheckSpells(Offensive)`, cadence halvée pour que les casts instants chainent au rythme du brain tick | Wizard, Theurgist, Cabalist, Sorcerer, Necromancer, Heretic, Eldritch, Enchanter, Mentalist, Animist, Bainshee, Valewalker, Runemaster, Spiritmaster, Bonedancer, Warlock, Thane |
| `tank` | 70 / 65 | 500 ms / 12 s | Cycle defensive spell/style + callout perte d'aggro quand la cible passe sur un autre membre | Armsman, Paladin, Reaver, Hero, Warden, Champion, Warrior, Thane |
| `melee_dps` | 60 | 500 ms | `CheckSpells(Offensive)` pour les procs / spells hybrides melee | Infiltrator, Mercenary, Minstrel, Blademaster, Nightshade, Vampiir, Valewalker, Berserker, Savage, Shadowblade, Skald, Valkyrie, MaulerAlb, MaulerMid, MaulerHib |
| `ranged_dps` | 60 | 500 ms | `CheckSpells(Offensive)` pour les procs archer | Scout, Ranger, Hunter |

Note hybrides : les "tanks" en chain (Warden, Thane, Reaver) ne sont pas des plate-tanks stricts mais sont gardés en `tank` car ils tiennent le rôle peel/guard du group play 1.65. Warden, Mentalist et Heretic ont des vraies spec lines de heal (Regrowth, Mana, Rejuvenation) et apparaissent donc dans `healer`.

Server properties contrôlant ces whitelists :

```
bot_ai_v2_healer_classes
bot_ai_v2_tank_classes
bot_ai_v2_melee_dps_classes
bot_ai_v2_ranged_dps_classes
bot_ai_v2_caster_dps_classes
bot_ai_v2_cc_classes
```

Toggle live avec `/mstrategy list`, `/mstrategy add healer`, `/mstrategy remove awareness`, `/mstrategy clear`. Les contrats trigger/action/strategy vivent sous [`GameServer/custom/MimicNPC/ai/Strategies/`](GameServer/custom/MimicNPC/ai/Strategies/). Les modules tiers s'enregistrent via `BotStrategyRegistry.Register`.

## Sprint et follow

Les bots en mode follow miroir le sprint state de leur leader humain à chaque tick. La gestion d'endurance est spécifique : un bot ne peut pas réalistement acheter une Potion d'Endurance Regen ni la RA Long Wind, donc `MimicNPC.EnduranceRegenerationTimerCallback` lit le setup du leader au tick et le miroir sur la math sprint du bot :

- Si le leader a `eEffect.EnduranceRegenBuff` actif, le regen du bot est bumpé au `EnduranceRegenerationAmount` du leader.
- Si le leader possède `AtlasOF_LongWindAbility`, le bot utilise le Long Wind le plus fort des deux dans le calcul du drain — une RA niveau 5 sur le leader zero le drain du bot.
- Aucun buff permanent appliqué : seul ce tick de regen est influencé. Un bot non-buffé continue à drainer normalement et finit par décrocher.

## Cible DPS et AoE

- **Veto boss.** `MimicBrain.CountAoeHostiles` renvoie le sentinel `-1` quand la cible primaire est `IGameEpicNpc` → `ShouldUseAoe` refuse les cluster spells → le bot retombe sur single-target.
- **AoE seulement si ça vaut le coup.** `MimicCombatProfile.DamageAoeMinTargets = 2`. Single-target nukes ont un score `+0`, AoE sans cluster reçoit `+2` penalty.
- **Focus fire via le main assist.** Chaque DPS bot pick la cible courante de l'assist via `BotTargetDiffersFromAssistTrigger`. Switch en 1.5 s max.
- **Cadence.** Brain tick à 500 ms en combat. `caster_dps` ré-arme toutes les 300 ms, `melee_dps` / `ranged_dps` toutes les 500 ms — alignées sur le tick combat pour que les instants chainent.
- **Ordre de priorité.** `ScoreOffensivePriority` : pet summons → snares → disease → debuffs stats → DoTs → DD-debuff hybrides → bolts → lifedrains → DDs purs. Score bas gagne.

## Mort et résurrection

Quand un bot meurt en groupe avec un joueur :

1. Le cadavre reste pendant un délai configurable — **60 s** avec un rezzer dans le groupe, **15 s** sans (`bot_rez_wait_seconds`, `bot_rez_wait_no_healer_seconds`).
2. N'importe quel membre du groupe avec un spell `Resurrect` (bot ou joueur) peut cibler le cadavre. Les bots auto-acceptent ; les joueurs voient le dialog habituel.
3. Les bots healers essaient activement de rez pendant le délai via `MimicBrain.CheckResurrect()`, appelé depuis les états FOLLOW / AGGRO / CAMP / city. Le rez en combat est autorisé (réaliste — un healer expérimenté lâche tout pour rez).
4. Au timeout, le bot **release-to-bind** par défaut (`bot_rez_timeout_behavior` = `release`) : annonce une ligne localisée, quitte le groupe, despawn. Alternative `revive` ramène le bot chez l'owner à 50% de vie.
5. Un **joueur mort** dans un groupe avec un bot rezzer fonctionne pareil : le bot court au cadavre, cast Resurrect, le joueur reçoit le dialog accept/decline.

## Système de modules game-server

Pour les features non-bot, un registry `IGameModule` optionnel est câblé dans `GameServer.Start` :

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

Les modules sont découverts par réflexion sur l'assembly GameServer et chaque script assembly, triés topologiquement (algorithme de Kahn + détection de cycles), et initialisés en ordre. Les échecs sont isolés — un module buggé log et est skippé sans tuer le serveur.

Point d'entrée : [`GameServer/Managers/Modules/`](GameServer/Managers/Modules/). Le `SampleLoggingModule` de référence est livré désactivé.

## Layout du dépôt

```
CoreBase/        primitives réseau, config, logging, FTP, MPK
CoreDatabase/    mini-ORM historique, handlers MySQL/SQLite
CoreServer/      exécutable / service Windows / actions de démarrage
GameServer/      le jeu lui-même (~2600 fichiers)
  ECS-Services/  services game-loop (Npc, Attack, Casting, Effect, Movement, …)
  ECS-Components/ composants par-entité pilotés par les services
  ai/            brains et FSM des mobs standards
  custom/MimicNPC/ le sous-système bot (la plupart du travail spécifique fork)
  Managers/Modules/ point d'extension IGameModule
  packets/       handlers packets client et libs packets serveur par version protocole
  serverrules/   règles PvE / PvP / RvR
  spells/ styles/ propertycalc/  math combat
Tests/           tests NUnit (niveau utilitaire)
Pathing/         bindings natifs pathfinding
```

Voir [`CODEBASE_GUIDE.md`](CODEBASE_GUIDE.md) pour la navigation détaillée.

## Roadmap

| Phase | État | Apport |
|---|---|---|
| A | livrée | Premier rôle framework : `healer` pilote `CheckHeals` |
| B | livrée | Stratégies de rôle `tank`, `melee_dps`, `ranged_dps`, `caster_dps`, `cc` |
| C | livrée | `healer` splitté en 5 bindings + nouveaux triggers diseased/poisoned |
| D | livrée | Couche immersion : callouts localisés, demande de cure publique, emotes tank/leader |
| E | livrée | Coordination cross-bots — DPS bascule sur switch d'assist, callout tank-lost-aggro |
| Rez | livrée | Fenêtre de cadavre rez-able, healers auto-cast Resurrect, release-to-bind au timeout |
| Sprint | livrée | Les bots héritent Endurance Regen + Long Wind RA du leader au niveau du tick |
| DPS | livrée | Veto AoE boss + cooldowns DPS halvés (300 / 500 / 500 ms) |
| MainCC | livrée | `IsBeingCcedByGroup` dedup — deux bots CC ne mezzent pas la même add |
| Leader | livrée | `LeaderStrategy` engage + post-combat callouts filtrés sur rôle Leader |
| F | planifiée | Thread-safety CCTargets, kick rotation sur casters ennemis, autonomie travel / quest / gather |

Les rôles par classe dans les CSV ont été validés contre plusieurs sources DAoC 1.65 (Class Library officielle, Camelot Herald wiki, ZAM, guides Uthgard / Disorder / Phoenix / Eden) — voir le commit `bd02702`.

## État / disclaimers

- Fork **expérimental** et command-driven. Plusieurs commandes puissantes sont accessibles en `ePrivLevel.Player` pour les tests — review `Commands.cs` avant d'ouvrir un serveur public.
- La math combat est celle d'OpenDAoC upstream ; le système bot s'empile par-dessus sans toucher aux formules de damage (exceptions documentées dans `propertycalc/`).
- Patch level 1.65 = cible principale.

## Licence

GPL v3 — voir [LICENSE](LICENSE). Héritée d'OpenDAoC, elle-même héritée de [DOLSharp](https://github.com/Dawn-of-Light/DOLSharp).

## Crédits

- [DOLSharp](https://github.com/Dawn-of-Light/DOLSharp) — l'émulateur Dawn of Light original
- [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) — la réécriture ECS sur laquelle ce fork est basé
- [mod-playerbots](https://github.com/mod-playerbots/mod-playerbots) — inspiration design pour la couche strategy Bot AI v2
