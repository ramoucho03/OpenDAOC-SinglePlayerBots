# Rapport d'audit complet — OpenDAOC-SinglePlayerBots

Date : 2026-05-29
Périmètre : **tout le code** — `CoreBase`, `CoreDatabase`, `CoreServer`, `GameServer` (serveur + modules + couche DB), `scripts`, `API`. ~2 620 fichiers `.cs`.

## Méthodologie

3 sources combinées, chaque finding **vérifié manuellement** avant inclusion :

1. **Compilation complète** de `Dawn of Light.sln` → erreurs/warnings objectifs.
2. **Analyzers Roslyn** activés en ligne de commande (sans modifier les `.csproj`) :
   `dotnet build -p:EnableNETAnalyzers=true -p:AnalysisMode=AllEnabledByDefault` → ~40 000 warnings,
   filtrés sur le sous-ensemble **correctness/fiabilité** (rethrow, leaks, exceptions, récursion, etc.).
3. **Swarm de 9 agents d'analyse** partitionnés par sous-système (combat, sorts, IA, monde/RvR,
   systèmes joueur, réseau/commandes, cœur/DB, scripts, modules custom) pour les **bugs de logique**
   que les analyzers ne voient pas.

> ⚠️ **Taux de faux positifs élevé du swarm (~75 %).** Les agents ignorent les sémantiques locales.
> Chaque finding ci-dessous a été **relu dans le code source** ; les faux positifs sont listés en §6
> pour traçabilité (et pour éviter de « corriger » du code correct).

## 1. Résultat objectif (compilation)

```
dotnet build "Dawn of Light.sln"   →   0 Error(s), 2 Warning(s)
```
- Les 2 warnings = `MSB3042` (nommage de manifeste `.resx` sous directive de compilation conditionnelle,
  `CoreServer/GameServerService*.cs`) — bénins.
- `GameServer.csproj` supprime `CS0618` (usage d'API obsolètes) via `<NoWarn>`.
- **Aucun analyzer Roslyn n'est activé** et **nullable reference types est désactivé** → le compilateur
  ne signale ni null derefs ni patterns à risque. C'est pourquoi la baseline paraît « propre ».

## 2. Bugs CONFIRMÉS (vérifiés ligne par ligne)

| # | Fichier:ligne | Sév. | Problème | Correctif |
|---|---|---|---|---|
| 1 | `GameServer/gameobjects/GameNPC.cs:2932` | **Élevée** | `CanUseLefthandedWeapon` : `set => CanUseLefthandedWeapon = value;` → le setter s'appelle lui-même → **récursion infinie / StackOverflow** (crash process non catchable) dès qu'on assigne la propriété. Confirmé par CA2011. | Le `get` est dérivé de `m_leftHandSwingChance`. Faire pointer le `set` sur le champ réel, ou supprimer le setter (vérifier les appelants). |
| 2 | `GameServer/gameobjects/GameLiving.cs:848` | Moyenne | Spymaster Enduring Poison : `Util.Chance((double)(15 * 0.0001))`. L'overload `Chance(double)` traite l'argument comme une **probabilité [0,1]** (`> NextDouble()`). 15×0.0001 = 0,0015 = **0,15 %** au lieu des ~15 % suggérés par la constante (erreur ×100). | `Util.Chance(15)` (overload int = 15 %) ou `Util.Chance(0.15)`. |
| 3 | `GameServer/spells/HealthToFatigue.cs:65` (classe `HealthToEndurance.GiveEndurance`) | Moyenne | Plafonnement inversé : `if (target.Endurance >= amount) amount = MaxEndurance - Endurance;`. À 50/100 avec un sort de +20, on attribue **toute la marge (→100)** au lieu de 20. Sur-attribue l'endurance dans le cas courant. | `if (target.Endurance + amount > target.MaxEndurance) amount = target.MaxEndurance - target.Endurance;` |
| 4 | `GameServer/craft/Repair.cs:150` | Moyenne | `percentNeeded` ∈ [50,100] (doc). `(percentNeeded / 100)` est une **division entière** = 0 pour 50-99 → l'exigence de skill devient 0 → **gate de compétence de réparation contournée**. | `player.GetCraftingSkillValue(skill) < (percentNeeded * CraftingMgr.GetItemCraftLevel(item) / 100)` |
| 5 | `GameServer/commands/admincommands/plvl.cs:355` | Moyenne | Précédence `&&` > `||` : `args[1]=="1" || args[1]=="2" && client.Player==target && target==null`. La 2e branche est toujours fausse (`client.Player==target && target==null` impossible). La condition se réduit à `args[1]=="1"` → le garde-fou de permission **ne se déclenche jamais pour plvl 2 (GM)**. | Parenthéser l'intention réelle, p.ex. `(args[1]=="1" || args[1]=="2") && target == client.Player`. |
| 6 | `GameServer/API/Guild.cs:122` | Moyenne | `guilds.GetRange(0, 10)` lève `ArgumentException` si `< 10` guildes existent (serveur peu peuplé/neuf) → **crash de l'endpoint API**. | `guilds.GetRange(0, Math.Min(10, guilds.Count))` |
| 7 | `GameServer/API/Utils.cs:72` | Moyenne | `GetUptime()` utilise la clé de cache `"api_player_count"` → **collision** avec le compteur de joueurs (mauvaise donnée / mauvais type renvoyé). | Utiliser une clé dédiée, p.ex. `"api_uptime"`. |
| 8 | `GameServer/ai/brain/Special/AlluvianGlobuleBrain.cs:49-53` | Faible-Moy. | Boucle `for (i=0..)` croissante avec `PlayersSeen.SwapRemoveAt(i)` : `SwapRemove` place le dernier élément en position `i`, puis `i++` le **saute** → des joueurs ne sont pas retirés de `PlayersSeen`. | `i--` après la suppression, ou itérer à l'envers. |
| 9 | `GameServer/custom/MimicNPC/MimicNPC.cs:5227` | Faible | `Styles = Styles;` (auto-assignation, CA2245) — no-op. Si un effet de bord du setter était visé, il n'est pas garanti. | Supprimer la ligne (ou appeler la vraie méthode de refresh voulue). |
| 10 | `GameServer/Managers/GameLoop/GameLoopThreadPoolMultiThreaded.cs:35,41,…` | Faible | Champs `IDisposable` jamais disposés (`_workerStartLatch` `CountdownEvent`, `_workContext` `ExecutionContext`, etc. — CA2213). Impact réel faible (singleton à durée de vie process), mais fuite à chaque `RestartWorkers()` qui recrée `_workerStartLatch` sans disposer l'ancien. | Disposer dans `Dispose()` et avant réassignation dans `RestartWorkers()`. |

## 3. SUSPECTS — à confirmer (réalistes, non certains à 100 %)

Nécessitent une décision « intention métier » avant correction :

- **`GameServer/keeps/AbstractGameKeep.cs:200`** — `m_difficultyLevel[(int)Realm - 1]` (tableau de 3). Lève
  `IndexOutOfRange` si `Realm == eRealm.None` (keep non claim) ou realm > Hibernia. Ajouter un garde si
  ce getter peut être appelé sur un keep non claim.
- **`GameServer/spells/Conversion.cs:52-61`** — la logique d'absorption ne s'exécute que
  `if (damageConverted > reduceddmg)` et ne réécrit jamais `reduceddmg` dans `TempProperties` ; semble
  rendre le sort **non fonctionnel** selon l'initialisation de `ConvertDamage`. À tracer (OnEffectStart).
- **`CoreDatabase/SqlObjectDatabase.cs` (≈416, 440, 686-697 ; `Handlers/MySqlObjectDatabase.cs`)** —
  API legacy acceptant des `whereExpression` **string brutes** concaténées dans le SQL (surface
  d'injection si un appelant passe de l'entrée utilisateur) ; transactions/connexions **non disposées**
  après commit/rollback. Hygiène DB à renforcer ; vérifier les appelants de l'API non paramétrée.
- **`GameServer/API/Player.cs` & `Guild.cs` (ctor)** — `new MemoryCache()` réassigne un cache *statique*
  à chaque instanciation → cache vidé. Le rendre `static readonly`.
- **`GameServer/scripts/gameevents/UniqueItemLootGenerator.cs:208`** — `validClasses[Util.Random(Count-1)]`
  lève `IndexOutOfRange` si `validClasses` est vide (`Util.Random(-1)` → index 0 sur liste vide). Garde sur liste vide.
- **`GameServer/scripts/system/Online.cs:341-345`** — division par `total` sans garde si `total == 0`.
- **`GameServer/realmabilities/effects/rr5/MarkofPreyEffect.cs:117`** — `EffectCaster.ChangeMana(EffectOwner, …)`
  donne le mana au **caster** (probablement voulu pour cette RA, mais à confirmer vs la spec).
- **`GameServer/Managers/GameLoop/GameLoopThreadPoolMultiThreaded.cs:194`** — `_workerStartLatch.Wait()`
  sans timeout (deadlock possible si un worker échoue à démarrer — déjà noté en commentaire).
- **`GameServer/scripts/system/stats.php:43`** — division par zéro (script PHP, hors binaire serveur).

## 4. Catégories systémiques (analyzers Roslyn, fiabilité)

Comptes après dédup (sur l'ensemble de la solution), règles **correctness** uniquement :

| Règle | Occ. | Signification |
|---|---|---|
| CA2201 | 74 | Lève un type d'exception réservé (`Exception`/`SystemException`) au lieu d'un type spécifique. |
| CA1851 | 62 | Énumération multiple possible d'un `IEnumerable` (perf/effets de bord). |
| CA2214 | 56 | Appel de méthode **virtuelle dans un constructeur** (peut toucher un état non initialisé). |
| CA2000 | 40 | Objet `IDisposable` non disposé avant de sortir du scope. |
| CA1001 | 28 | Type possède un champ `IDisposable` mais n'implémente pas `IDisposable`. |
| CA1308 | 20 | Normalisation via `ToLower` au lieu de `ToUpper` (comparaisons culture). |
| CA2208 | 14 | `ArgumentException` mal construite (nom de paramètre manquant/erroné). |
| CA1063/CA2213 | 12/4 | Pattern `Dispose` incorrect / champs disposables non libérés. |

Ce sont des **risques** (pas tous des bugs actifs) : à traiter en lot par sous-système si on veut
durcir la fiabilité. Le détail complet est reproductible via la commande analyzers ci-dessus.

## 5. Le reste du code

Hors des points ci-dessus, l'échantillonnage par le swarm sur combat, sorts, IA, monde/keeps, systèmes
joueur, réseau et scripts **n'a pas révélé de bug de logique confirmé supplémentaire**. Le code compile
proprement et les idiomes locaux (`Util.Random` inclusif, composants toujours initialisés,
`InCombatInLast`) sont utilisés correctement à l'écrasante majorité.

## 6. Faux positifs écartés (NE PAS « corriger »)

Documentés pour éviter des régressions. Tous **vérifiés faux** :

- **« Off-by-one sur `Util.Random(X.Count - 1)` »** (~20+ signalements : `StyleProcessor.cs:427`,
  `TurretBrain.cs:110`, `TurretFNFBrain.cs:111/118`, `Zone.cs:613`, `Instance.cs:103`, `MimicNPC.cs`, …).
  `Util.Random(int max)` est **inclusif [0,max]** (`_random.Next(0, max+1)`, cf. `gameutils/Util.cs:19`).
  Donc `tab[Util.Random(tab.Count-1)]` couvre **tous** les indices, y compris le dernier. **Correct.**
- **« Condition impossible `InCombatInLast(30s)==false && InCombatInLast(35s)` »** (6 mobs nommés :
  `JailerVifil`, `Yar`, `Conservator`, `Gnat`, `Evern`, `GlacierGiant`). C'est une **fenêtre valide**
  « combat terminé il y a 30-35 s » (`InCombatInLast(ms)` = vrai si dernier combat ≤ ms, cf.
  `GameLiving.cs:242-255`). **Correct.**
- `StyleProcessor.cs:442` — la ternaire `(weapon.Hand != 1) ? Icon : TwoHandAnimation` est **correcte**
  (Hand==1 = deux mains → TwoHandAnimation).
- `StyleProcessor.cs:471` — division entière avec `+1` correctif **intentionnel** (formule d'endurance DOL).
- `spells/BuffShear.cs:137` — logique correcte (un self-buff non-shearable n'est pas shearé).
- `spells/HoTSpellHandler.cs:60` — `Caster.Equals(target)` couvre le cas valide « pet necro qui se soigne ».
- `craft/Salvage.cs:205` — précédence du `?:` correcte (pas de bug).
- `scripts/quests/Albion/epic/Academy50.cs:53` — « `protected private` » : invalide en C#, ne compilerait
  pas → la build étant propre, c'est une mauvaise lecture de l'agent.

## 7. Recommandations (ordre suggéré)

1. **Corriger les 10 bugs confirmés (§2)** — patchs petits et ciblés ; #1 (StackOverflow) en priorité.
2. **Trancher les SUSPECTS (§3)** — surtout l'API DB non paramétrée et `AbstractGameKeep:200`.
3. (Optionnel) **Durcissement par lot (§4)** — activer un sous-ensemble d'analyzers ciblés en CI
   (`AnalysisMode=Minimum` + règles CA2000/CA2213/CA2214/CA1851) plutôt que `AllEnabledByDefault`.

## Vérification

Après tout correctif : `dotnet build "Dawn of Light.sln"` doit rester à `0 Error(s)`.
La passe analyzers est reproductible avec la commande de la §Méthodologie.
