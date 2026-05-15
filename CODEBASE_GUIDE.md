# Guide de lecture du code OpenDAoC SinglePlayerBots

Ce fichier sert de carte de navigation pour revenir vite dans le code. Le depot est un fork OpenDAoC/Dawn of Light oriente bots solo: le coeur du sujet n'est pas nomme `Bot`, mais `MimicNPC`.

## Vue d'ensemble

Le depot est une solution C#/.NET 10 avec cinq projets principaux:

- `CoreBase`: primitives bas niveau, reseau, config, logging, temps, FTP, MPK.
- `CoreDatabase`: mini-ORM historique, attributs de mapping, tables `Db*`, handlers MySQL/SQLite.
- `CoreServer`: executable/service Windows, actions de demarrage, config serveur.
- `GameServer`: quasi tout le jeu: objets monde, combat, spells, ECS, IA, commandes, scripts, quetes, packets, regles serveur.
- `Tests`: quelques tests NUnit cibles sur des utilitaires (`DrainArray`, `SessionIdAllocator`, validation stats).

Le volume est tres concentre dans `GameServer` avec environ 2600 fichiers. Les dossiers les plus utiles pour comprendre le runtime sont:

- `GameServer/gameobjects`: hierarchie `GameObject` -> `GameLiving` -> `GameNPC` / `GamePlayer`.
- `GameServer/ECS-Services` et `GameServer/ECS-Components`: boucle ECS, attaques, casts, effets, mouvement.
- `GameServer/ai`: brains et machines a etats des NPC.
- `GameServer/serverrules`: regles de groupage, recompenses, PvE/PvP/RvR.
- `GameServer/commands`: commandes chargees via attributs.
- `GameServer/scripts`: contenu de jeu, mobs nommes, quetes, NPC custom.
- `GameServer/custom/MimicNPC`: module bots.

## Demarrage

Le chemin d'execution standard est:

1. `CoreServer/MainClass.cs` choisit une action, par defaut `--start`.
2. `CoreServer/Actions/ConsoleStart.cs` instancie et lance le `GameServer`.
3. `GameServer/GameServer.cs` initialise dans l'ordre les metriques, Discord, scripts, proprietes serveur, commandes, DB, scheduler, managers, monde, pathfinding, composants de scripts, regles serveur, ECS/game loop.
4. Les scripts declenchent leurs handlers `[ScriptLoadedEvent]`, ce qui initialise aussi `MimicManager`.

Les commandes sont detectees par `ScriptMgr.LoadCommands()` grace aux `[CmdAttribute]`. Dans le code les attributs utilisent `&mcreate`, `&mlfg`, etc.; cote joueur cela correspond aux commandes slash documentees.

## Concepts moteur a garder en tete

`GameObject` est la base de position/region/etat. `GameLiving` ajoute vie, mana, combat, effets et statistiques vivantes. `GameNPC` porte la logique NPC et un `Brain`. `GamePlayer` porte le contrat joueur reel.

Le fork ajoute une interface `IGamePlayer` dans `GameServer/custom/MimicNPC/iGamePlayer.cs`. `GamePlayer` et `MimicNPC` l'implementent, ce qui permet aux regles serveur, groupes, recompenses et effets de traiter un bot comme un joueur sans qu'il herite de `GamePlayer`.

Le combat moderne passe par:

- `AttackComponent`, `AttackAction`, `StyleComponent`, `EffectListComponent`.
- `PlayerAttackAction`, `NpcAttackAction`, et ici `MimicAttackAction`.
- `StyleProcessor` pour valider/endurer/appliquer les styles.
- `SpellHandler` et composants de cast pour spells et interruptions.

L'IA NPC passe par `ABrain`, `FSM` et des etats `FSMState`. Les mobs standards utilisent `StandardMobBrain`; les bots utilisent `MimicBrain`.

## Module Bot: `MimicNPC`

Chemin principal: `GameServer/custom/MimicNPC`.

Le module contient ~122 fichiers (le strategy framework Bot AI v2 et ses ~25 triggers + ~12 actions + 12 strategies built-in ont quasi doublé la taille du module depuis ce guide). Les fichiers les plus importants sont:

- `MimicNPC.cs` (~8920 lignes): corps du bot. C'est un `GameNPC` qui implemente `IGamePlayer` et recopie une grande partie des capacites de `GamePlayer`.
- `ai/MimicBrain.cs` (~5100 lignes): logique de decision, aggro, sorts, soins, pull, assist, flee/flank. A presque double depuis l'ajout du strategy manager (Phases A-E + Rez/Sprint/DPS/MainCC/Leader).
- `ai/MimicState.cs` (~1680 lignes): etats FSM du bot.
- `MimicGroup.cs`: roles de groupe, point de camp, point de pull, cache de soins.
- `MimicManager.cs`: init, battlegrounds, spawners globaux, creation de mimics, equipement, specs, LFG, noms.
- `MimicSpawner.cs`: spawner periodique de groupes ou bots.
- `Commands.cs`: commandes `/m*`.
- `Enums.cs`: classes bot, roles, types de specs.
- `MimicConfig.cs`: flags statiques de config bot.
- `DummyClient.cs` et `packets/DummyPacket.cs`: client/packet lib factices pour satisfaire le contrat joueur sans client reseau.
- `ECS-Components/*`: variantes bot pour attaque, styles, effets.
- `ClassSpecs/{Albion,Hibernia,Midgard}`: tables de specs, armes et ratios par classe.

### Cycle de vie d'un bot

1. Une commande ou un spawner appelle `MimicManager.GetMimic(...)`.
2. Le constructeur `MimicNPC(eMimicClass, level, gender, spec)` cree un `DummyClient`, un `DummyPacketLib`, un inventaire bot, choisit `MimicSpec`, classe, race, nom, brain, stats, level et equipement.
3. `SetBrain` choisit `AssassinBrain` pour assassins, `ArcherBrain` pour archers, sinon `MimicBrain`.
4. `MimicManager.AddMimicToWorld(...)` positionne et appelle `AddToWorld`.
5. Selon le contexte, le brain passe en `WAKING_UP`, `FOLLOW_THE_LEADER`, `CAMP`, `AGGRO`, etc.
6. Si le bot rejoint un groupe, `Group.MimicGroup` devient le point central pour roles, soins et camp.

### Etats IA

Les etats dans `MimicState.cs` sont:

- `WAKING_UP`: initialise le mode PvP selon zone/RvR, puis choisit follow/camp/idle/roam.
- `IDLE`: repos et buffs defensifs.
- `FOLLOW_THE_LEADER`: suit le leader, buff, heal, reagit aux aggro du groupe.
- `AGGRO`: combat actif, selection de cible, assist, spells, styles.
- `ROAMING`: comportement libre autour du spawn.
- `CAMP`: tient un point de camp, gere puller/main CC/main tank.
- `RETURN_TO_SPAWN`: retour au spawn/camp.
- `PATROLLING`: patrouille basique.
- `DUEL`: duel bot contre bot.
- `DEAD`: etat mort.

### Roles de groupe

`MimicGroup` expose:

- `MainLeader`: cible de follow logique, partiellement implemente.
- `MainAssist`: source de target focus, partiellement implemente.
- `MainTank`: favorise taunts et reprise d'aggro.
- `MainCC`: maintient `CCTargets` et tente mez/root des adds.
- `MainPuller`: tire/pull vers le camp.
- `Healer`: force un mimic soigneur a rester hors combat.
- `CampPoint`: point d'attente/aggression.
- `PullFromPoint`: point optionnel d'ou chercher/puller.
- Cache de soins: `AmountToHeal`, `MemberToHeal`, cures disease/poison/mezz, flags "already casting".

### Commandes bot

Dans `Commands.cs`:

- `/mcreate class (level) (spec) (inv)`: cree un mimic precis.
- `/mgroup realm amount level preventCombat`: cree un groupe de mimics.
- `/mspawner realm levelMin levelMax max`: cree un spawner.
- `/mpvp true|false`: active/desactive PvPMode sur cible ou groupe.
- `/mpc true|false [group]`: active/desactive PreventCombat.
- `/mheal`: bascule un bot healer.
- `/mbattle thid start|stop|clear`: battleground bot Thidranki.
- `/msummon`: teleporte les mimics groupes vers le joueur.
- `/mlfg [index]`: liste/recrute un bot LFG.
- `/mrole leader|tank|assist|cc|puller`: assigne un role.
- `/mcamp here|set|remove|aggrorange|filter`: camp PvE.
- `/mpull`: set camp/pull et force le pull de la cible.
- `/mpullfrom here|set|remove`: point de pull.
- `/mfollow`: supprime camp/pull et repasse en follow.
- `/mattack`: force les mimics groupes a attaquer la cible.
- `/mintercept`, `/mguard`, `/mprotect`: assigne protections.
- `/mbstats thid`: stats battleground.

Attention: beaucoup de commandes sont actuellement `ePrivLevel.Player`, y compris des commandes tres puissantes ou de test.

### Integrations hors module

Le bot n'est pas isole. Les fichiers moteur ont des adaptations importantes:

- `GameServer/gameutils/Group.cs`: ajoute `MimicGroup`, utilise `IGamePlayer`, accepte `MimicNPC` comme membre, split loot/argent via interface.
- `GameServer/ECS-Components/Actions/AttackAction.cs`: `AttackAction.Create` retourne `MimicAttackAction` pour `MimicNPC`.
- `GameServer/gameobjects/GameLiving.cs`: propage `OnGroupMemberAttacked` aux mimics du groupe; adapte certaines regles mez/equipement pour `MimicNPC`.
- `GameServer/gameobjects/GameNPC.cs`: evite l'autoset stats NPC pour mimics, selectionne armes selon `MimicSpec`, evite `OnNpcKilled` pour les mimics.
- `GameServer/serverrules/AbstractServerRules.cs`: recompenses, loot et kills passent par `IGamePlayer`; un mimic tue n'est pas traite comme simple NPC.
- `GameServer/serverrules/NormalServerRules.cs`: les NPC classiques ne peuvent pas attaquer selon les regles player si ce ne sont pas des mimics.
- `GameServer/ai/brain/StandardMob/StandardMobBrain.cs`: les adds BAF peuvent alimenter `MimicGroup.CCTargets`.
- `GameServer/packets/Server/PacketLib1124.cs` et `PacketLib1125.cs`: affichage nom/classe en groupe ou enemy realm.
- Plusieurs `spells`, `propertycalc`, `effects` et `StyleProcessor` ont des exceptions `GameNPC && is not MimicNPC` pour traiter le bot comme joueur.

## Pratiques observees

Points solides:

- Le module bot est majoritairement co-localise sous `custom/MimicNPC`, ce qui rend son perimetre identifiable.
- L'approche `IGamePlayer` donne une integration assez directe avec groupes, recompenses, spells et effets existants.
- Le comportement est structure par FSM, donc les transitions principales sont lisibles.
- Les specs de classes sont petites et faciles a modifier individuellement.
- Certaines zones sensibles utilisent locks ou structures concurrentes (`MimicBrain.AggroList`, `MimicGroup.HealLock`, locks de `Group`).

Dette/risques:

- `MimicNPC.cs` et `MimicBrain.cs` sont des classes monolithiques. Le bot depend de beaucoup de details internes de `GamePlayer` et `GameNPC`; chaque evolution du joueur reel peut necessiter un miroir cote mimic.
- L'interface `IGamePlayer` est enorme. Elle stabilise l'integration mais transforme le contrat joueur en surface tres large a maintenir.
- Les exceptions `is MimicNPC` sont dispersees dans le moteur. C'est efficace a court terme, mais cela augmente le risque de regression lors de modifications combat/spells/rewards.
- Les commandes bot utilisent plusieurs `int.Parse`, `byte.Parse`, `bool.Parse` sans garde; une mauvaise entree joueur peut lever une exception.
- Le module n'a pas de tests dedies; les tests existants couvrent surtout des utilitaires hors bot.
- La config bot est en `static readonly` dans `MimicConfig.cs`, pas branchee sur les server properties.
- `DummyPacketLib` masque beaucoup de sorties reseau. C'est necessaire pour un bot sans client, mais les comportements visibles cote client doivent etre testes via vrais joueurs autour.
- Plusieurs TODO importants restent dans le coeur bot: LoS archer, spells instruments, keep guards, class weighting LFG, formules de stats/degats, ROG weapons.

## Problemes concrets reperes

Ces points valent une revue avant de developper plus loin:

- `MimicBrain.CheckPuller`: la condition `class != Hunter || class != Ranger || class != Scout` est toujours vraie pour une classe donnee; si l'intention est "ni Hunter ni Ranger ni Scout", il faut probablement des `&&`.
- `MimicGroup.CheckGroupHealth`: dans le bloc poison, le code assigne `m_diseasePercent = m_poisonPercent` au lieu de mettre a jour `m_poisonPercent` avec le pourcentage courant.
- `MimicSpawnerCommandHandler`: le constructeur `MimicSpawner` ajoute deja le spawner a `MimicSpawning.MimicSpawners` et appelle `AddToWorld`; la commande rappelle `AddToWorld` et re-ajoute le spawner.
- `MimicSpawnerCommandHandler`: le switch contient `case "hibernia:"` avec un deux-points, probablement une faute pour `hibernia`.
- `Commands.cs`: `/mgroup`, `/mspawner`, `/mlfg`, `/mcamp aggrorange` parsent sans `TryParse`.
- `Commands.cs`: `/mgroup` laisse un `Console.WriteLine(preventCombat)` de debug.
- Plusieurs commandes puissantes sont accessibles a `Player`; c'est coherent avec le README de test, mais dangereux sur un serveur ouvert.

## Ou aller selon la tache

- Ajouter/changer une commande bot: `GameServer/custom/MimicNPC/Commands.cs`.
- Changer l'IA de combat generale: `ai/MimicBrain.cs`, region `Aggro` ou `Spells`.
- Changer le comportement camp/pull: `ai/MimicState.cs` etat `MimicState_Camp`, et `MimicBrain` region `MimicGroup AI`.
- Changer les roles de groupe: `MimicGroup.cs` et commandes `/mrole`, `/mcamp`, `/mpull`.
- Changer une classe/spec de bot: fichier sous `ClassSpecs/<Realm>/<Class>.cs`, puis verifier `MimicSpec.GetSpec`.
- Changer l'equipement genere: `MimicManager.cs`, region `Equipment`, et `MimicNPC.SetWeapons/SetArmor/SetRanged`.
- Changer les regles XP/RP/loot bot: `serverrules/AbstractServerRules.cs`, `Group.cs`, puis `MimicNPC.ProcessDeath`.
- Changer affichage client/group: `PacketLib1124.cs`, `PacketLib1125.cs`, `MimicEffectListComponent.cs`.
- Deboguer un bot qui n'attaque pas: verifier `PreventCombat`, `PvPMode`, etat FSM, `AggroList`, puis `MimicAttackAction`.
- Deboguer un healer: `MimicGroup.CheckGroupHealth`, `MimicBrain.CheckHeals`, flags `AlreadyCasting*`.

## Checklist avant modification bot

1. Chercher les usages hors module avec `rg "MimicNPC|IGamePlayer|MimicGroup" GameServer`.
2. Identifier si le changement touche NPC, joueur reel, ou seulement mimic.
3. Verifier les etats FSM concernes et les transitions d'entree/sortie.
4. Pour toute commande joueur, remplacer les `Parse` directs par `TryParse`.
5. Pour combat/spells/rewards, tester au moins: bot solo, bot groupe avec joueur, bot contre mob, bot contre player-like target.
6. Si une regle depend de `GameNPC && is not MimicNPC`, verifier le cas joueur reel et le cas NPC classique.
7. Ajouter un test cible si le changement est purement logique; sinon prevoir un scenario manuel serveur.
