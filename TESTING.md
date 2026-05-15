# In-Game Testing Checklist

Branch: `claude/optimize-dol-server-gho1T` — bots refactor + Bot AI v2 phases A through "Leader".

This document tracks the in-game scenarios that need to be exercised before the work can be considered production-ready. Most of the recent commits compile and pass the unit tests, but the unit-test coverage stops at utility helpers — the strategy framework, FSM transitions, packet integration, group dynamics and combat math have **never been validated against a real client connected to a real server in this branch**. Everything below is what we want to walk through together.

How to read this file: each section is a self-contained scenario. Tick the box once the scenario behaves as described. If something is wrong, write the symptom under the scenario (one line is enough — the more specific the better, e.g. "bot stays on old target for 4 s instead of 1.5 s"). I'll triage from there.

---

## 0. Smoke test (do this first)

The server must come up cleanly before anything else is meaningful.

- [ ] `docker compose up -d --build` succeeds (or local `dotnet build` + run).
- [ ] `db-seed` container exits with code 0 and the log shows `db-seed: schema ready in shared volume`.
- [ ] `db` container becomes healthy within ~60 s (`docker compose ps` shows `healthy`).
- [ ] `gameserver` log shows the boot banner, no exception traces in the first 30 s.
- [ ] A real player can log in with the standard client.
- [ ] `/mhelp` in-game prints the command list (sanity check that the mimic module loaded).

---

## 1. Bot creation and basic lifecycle

- [ ] `/mcreate Cleric 50` spawns a single Cleric bot next to you.
- [ ] `/mgroup alb 8 50` spawns a full Albion group of 8 level-50 bots.
- [ ] `/mlfg` shows the LFG list. `/mlfg 1` invites the first listed bot.
- [ ] `/mclear` despawns every bot you created.
- [ ] After `/mclear`, no orphan corpses, no lingering MimicGroup, no DB rows hanging.
- [ ] Bots have realistic equipment for their class and level (no naked bots, no level-1 weapons on a level-50).

---

## 2. Follow, sprint and movement

- [ ] You invite a Cleric bot. It follows you when you move.
- [ ] You press Sprint with the Cleric in follow mode — the Cleric sprints too.
- [ ] You drink an Endurance Regen potion. You sprint indefinitely. The Cleric **also** sprints indefinitely (no flicker on the group endurance bar).
- [ ] You stop drinking the potion (or it expires). You start dropping endurance on sprint. The Cleric also drops endurance and eventually falls behind. **This is realistic, not a bug**.
- [ ] You own Long Wind RA level 5. You sprint indefinitely without the potion. The Cleric also sprints indefinitely.
- [ ] `/msummon` teleports the bot group to you, even across zones.

---

## 3. PvE camping — pull / engage / combat / regen cycle

Hardest section because it covers the puller, the tank, the camp state machine and the assist propagation at the same time.

Set up: 1 real player + 1 tank bot + 1 healer bot + 1 puller bot (`/mrole puller`) + 1 CC bot (`/mrole cc`) + a few DPS bots. Stand near a mob area.

- [ ] `/mrole tank` on the Armsman → bot becomes MainTank.
- [ ] `/mrole assist` on the tank too (or on yourself) → becomes MainAssist.
- [ ] `/mrole puller` on the Scout/Ranger/Hunter → bot becomes MainPuller.
- [ ] `/mrole cc` on the Sorcerer/Bard/Enchanter → bot becomes MainCC.
- [ ] `/mcamp set` on a ground target → group settles at the camp anchor.
- [ ] Healer bot sits down to regen. Other bots stand by.
- [ ] Camp transitions to `Ready` and the MainLeader does a `/salute` emote.
- [ ] Puller picks a valid target respecting `/mcamp filter <con>` (e.g. yellow+).
- [ ] Puller fires at distance, runs back to camp.
- [ ] When the mob arrives, the MainLeader announces "Engage!" in chat **and** does a `/bangonshield` emote.
- [ ] Tank acquires aggro on the inbound mob (taunt/shield slam style).
- [ ] DPS bots focus the tank's target via main assist.
- [ ] CC bot mezzes any add brought in by BAF.
- [ ] Combat ends → MainLeader says "Good fight, regen". Healer sits down.
- [ ] Camp phase transitions back through PostCombat → Regen → Ready for the next pull.

### Edge cases

- [ ] `/mcamp aggrorange 1000` — mobs within 1000u get aggroed by the camp; bot waits otherwise.
- [ ] `/mcamp filter purple` — puller only pulls purple-con mobs (small experiment with a known low-con mob to confirm it's filtered out).
- [ ] Puller has 0 mana but a bow → still pulls at distance with the bow.
- [ ] Puller dies mid-pull → tank/healer don't engage the now-orphaned mob.
- [ ] `/mfollow` cancels the camp; bots come back to follow.
- [ ] `/mpullfrom set` on a different ground target → puller pulls from that secondary location.

---

## 4. MainCC — multi-bot dedup (new in this branch)

The most recent MainCC fix prevents two CC bots from mezzing the same add. To validate it you need **two CC bots** in the same group.

Set up: 1 real player + 2 CC bots (Sorcerer + Enchanter, for example) + a tank + a healer. Pull a 3-add pack.

- [ ] Puller brings back 3 mobs. Both CC bots start casting mez.
- [ ] **Each mez targets a different add.** Not both on the same one.
- [ ] If the first CC bot's cast is interrupted, the second CC bot picks up its target — no double-mez race.
- [ ] When a mez expires and the mob comes back, the CC bot re-mezzes it.

If you observe both bots casting mez on the same add: the `IsBeingCcedByGroup` check failed somehow — note the spell types each bot is using and the exact tick. I'll instrument the helper.

---

## 5. Healer — the five priority bindings

Critical heal > mezz cure > poison cure > disease cure > low-health heal. The healer should always pick the highest-priority reason and skip the others that same tick.

- [ ] Heal a tank from full HP down to 80% — healer applies a normal heal at the `low` threshold.
- [ ] Drop the tank to 25% HP — healer **immediately** fires the emergency (critical) heal, even if a cure was queued.
- [ ] Mezz a healer alt with `/cast` from a sorcerer — healer publicly says "I need a cure!" (`Mimic.Chat.NeedCure.*`) and another healer (or a bot Cleric) cures the mezz.
- [ ] Poison a bot — healer announces and cures.
- [ ] Disease a bot — same.
- [ ] **No double-heal**: if 2 healers are in the group, each picks a different target / spell, the `AlreadyCastingHoT` flag prevents stacking.

---

## 6. Tank — taunts, peel, lost-aggro callout

- [ ] Tank engages the mob (you set him as MainAssist or he taunts on intercept) → mob attacks the tank.
- [ ] A caster bot pulls aggro by nuking too hard → mob switches to caster → **tank publicly says "Lost aggro, peel off me!"** (`Mimic.Chat.LostAggro.*`).
- [ ] Tank re-taunts and recovers aggro within a few seconds.
- [ ] `/mguard <name>` on the tank — tank guards the named member.
- [ ] `/mintercept` and `/mprotect` — same, applied successfully.

---

## 7. DPS — focus fire and boss veto

- [ ] You hit a non-boss mob. DPS bots all pile on your target.
- [ ] You switch to a different mob. Within ~1.5 s every DPS bot switches too.
- [ ] You pull an **epic boss** (any `IGameEpicNpc` — e.g. an EpicNamedNPC in a quest area). DPS bots do **not** AoE: they single-target nuke the boss only.
- [ ] You pull a 4-mob pack of grunts. Casters cast **AoE** spells, not single-target nukes (you should see Earth bomber from Theurgist, PBAoE from a Wizard, etc.).
- [ ] Caster bots chain their nukes — no visible idle gap between two instant-cast spells.

---

## 8. Leader callouts (just added)

Set a bot as MainLeader explicitly (`/mrole leader`), then engage a pack.

- [ ] When the camp phase transitions Pulling → Engaging, the MainLeader chats "Engage!" (one of three EN variants, or French equivalent for a FR-locale client).
- [ ] At the same tick, the MainLeader does a `/beckon` emote.
- [ ] After the fight ends and camp goes to PostCombat, the MainLeader chats "Good fight, regen" (or French equivalent).
- [ ] No other bot duplicates the engage / postcombat lines — only the MainLeader.

---

## 9. Death and resurrection

- [ ] A bot dies in your group. Its corpse stays on the ground.
- [ ] A bot Cleric runs to the corpse and casts Resurrect. The corpse stands back up at the cleric's `ResurrectHealth %` (default ~50%).
- [ ] Bot says "Rezzing — protect me!" while casting.
- [ ] You die. The bot Cleric runs to **your** corpse and starts casting Resurrect. You get the standard accept/decline dialog.
- [ ] You let the rez timer expire on the dead bot's corpse. The bot says one of the `Mimic.Chat.ReleaseToBind.*` lines, leaves the group, despawns.
- [ ] Change `bot_rez_timeout_behavior` to `revive` and repeat — the bot teleports back to you at 50% vitals and stays in the group.

---

## 10. Strategy live-toggle

- [ ] `/mstrategy list` on a bot prints all active strategies.
- [ ] `/mstrategy remove awareness` — that bot stops the self-callouts (low HP / low mana lines).
- [ ] `/mstrategy add awareness` — they come back.
- [ ] `/mstrategy remove healer` on a bot Cleric — it stops casting heals but doesn't crash (FSM legacy CheckHeals takes over from the next FSM Think onward).
- [ ] `/mstrategy clear` — strips every active strategy on the targeted bot (or every grouped bot if no target).

---

## 11. Battlegrounds (Thidranki)

- [ ] `/mbattle thid start` populates Thidranki with bots from the three realms.
- [ ] `/mbstats thid` shows the realm distribution.
- [ ] You can join one realm and find PvP against the bots.
- [ ] Bots use Bot AI v2 strategies (cc, caster_dps, tank, etc.) in RvR mode — focus on enemy casters and healers as expected.
- [ ] `/mbattle thid stop` stops spawning. `/mbattle thid clear` removes existing bots.

---

## 12. Stress / scale

- [ ] You spawn 8 bots and play a full PvE session of ~30 min. No memory leak, no FPS drop, no kicked clients.
- [ ] You disconnect and reconnect mid-session. Bots that were yours despawn correctly, no orphans.
- [ ] You make 5 successive `/mgroup` cycles + `/mclear` over 10 min. No DB row leak, no timer leak.
- [ ] You stop the server with `docker compose stop`. Restart with `docker compose start`. No crash on shutdown, no schema corruption.

---

## Known limitations (don't bother testing these — they're documented as out-of-scope)

- `MimicGroup.CCTargets` is a `List<GameLiving>` mutated from multiple threads without a lock. A 4+ bot group can in theory trigger an `InvalidOperationException` or `IndexOutOfRangeException`. The Phase F refactor will introduce a CcLock. Until then, occasional crash logs around `CCTargets.Add` / `CCTargets.Remove` are expected and not blocking.
- `MimicSpawner.Stop()` pauses the timer but doesn't remove the spawner from the static `MimicSpawning.MimicSpawners` list — operator API drift, not a runtime bug.
- The `DUEL` FSM state is registered but never transitioned into. Dead code, no impact.
- 1.65 patch level is the primary target. Higher patches compile but bot behaviour on post-1.65 spec lines is undertested.

---

## How to report a failed scenario

When something doesn't work, give me:

1. **Scenario number + name** ("8. Leader callouts").
2. **What you saw** (one or two sentences).
3. **Optional**: the exact tick or chat line where it broke. Server log excerpt if there's an exception.

I'll triage, instrument if needed, and push the fix as a follow-up commit on the same branch.
