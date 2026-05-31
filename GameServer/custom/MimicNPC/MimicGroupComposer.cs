using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Builds balanced /mgroup compositions and assigns roles after creation.
    /// Role assignment is best-effort: if the comp lacks a class for a role,
    /// the role falls back to the next best available bot.
    /// </summary>
    public static class MimicGroupComposer
    {
        public enum eRole { Tank, Healer, Support, CC, Caster, MeleeDPS }

        // Class catalogs per realm and role. A class can appear in multiple
        // role lists (e.g. Bard is both Healer and Support/CC). Picking is
        // randomized within each role so successive /mgroup calls vary.
        private static readonly Dictionary<eRealm, Dictionary<eRole, eMimicClass[]>> _classesByRole = new()
        {
            [eRealm.Albion] = new()
            {
                [eRole.Tank]     = new[] { eMimicClass.Armsman, eMimicClass.Paladin, eMimicClass.Mercenary },
                [eRole.Healer]   = new[] { eMimicClass.Cleric, eMimicClass.Friar },
                [eRole.Support]  = new[] { eMimicClass.Friar, eMimicClass.Cleric, eMimicClass.Paladin },
                [eRole.CC]       = new[] { eMimicClass.Sorcerer, eMimicClass.Minstrel },
                [eRole.Caster]   = new[] { eMimicClass.Wizard, eMimicClass.Theurgist, eMimicClass.Cabalist, eMimicClass.Sorcerer },
                [eRole.MeleeDPS] = new[] { eMimicClass.Mercenary, eMimicClass.Reaver, eMimicClass.Armsman, eMimicClass.Infiltrator },
            },
            [eRealm.Hibernia] = new()
            {
                [eRole.Tank]     = new[] { eMimicClass.Hero, eMimicClass.Champion, eMimicClass.Blademaster },
                [eRole.Healer]   = new[] { eMimicClass.Druid, eMimicClass.Bard, eMimicClass.Warden },
                [eRole.Support]  = new[] { eMimicClass.Warden, eMimicClass.Bard, eMimicClass.Druid },
                [eRole.CC]       = new[] { eMimicClass.Bard, eMimicClass.Enchanter, eMimicClass.Mentalist },
                [eRole.Caster]   = new[] { eMimicClass.Eldritch, eMimicClass.Enchanter, eMimicClass.Mentalist, eMimicClass.Valewalker },
                [eRole.MeleeDPS] = new[] { eMimicClass.Blademaster, eMimicClass.Champion, eMimicClass.Hero, eMimicClass.Nightshade, eMimicClass.Valewalker },
            },
            [eRealm.Midgard] = new()
            {
                [eRole.Tank]     = new[] { eMimicClass.Warrior, eMimicClass.Thane, eMimicClass.Berserker },
                [eRole.Healer]   = new[] { eMimicClass.Healer, eMimicClass.Shaman },
                [eRole.Support]  = new[] { eMimicClass.Shaman, eMimicClass.Skald, eMimicClass.Thane },
                [eRole.CC]       = new[] { eMimicClass.Healer, eMimicClass.Runemaster, eMimicClass.Spiritmaster },
                [eRole.Caster]   = new[] { eMimicClass.Runemaster, eMimicClass.Spiritmaster, eMimicClass.Bonedancer },
                [eRole.MeleeDPS] = new[] { eMimicClass.Berserker, eMimicClass.Savage, eMimicClass.Warrior, eMimicClass.Shadowblade, eMimicClass.Hunter },
            },
        };

        // Ordered template: as groupSize grows, fill roles in this order.
        // Slots 1..N from this list make a coherent N-man group.
        //
        // Spec target for an 8-man: 1 tank + 1 healer + 1 CC + 1 support/buffer
        // + 4 DPS (mixed melee/caster). We expose a second healer at slot 6 so
        // every 6+ group gets a backup healer (avoids wipes when the sole
        // healer goes down before TryPromoteHealer can kick in).
        private static readonly eRole[] _template =
        {
            eRole.Tank,      // 1
            eRole.Healer,    // 2
            eRole.CC,        // 3
            eRole.Support,   // 4 (buffer/utility)
            eRole.MeleeDPS,  // 5
            eRole.Healer,    // 6 (backup healer)
            eRole.Caster,    // 7
            eRole.MeleeDPS,  // 8
        };

        /// <summary>
        /// Picks an ordered list of mimic classes for the given realm and group size.
        /// The returned list preserves order so role assignment can use it as a parallel array.
        ///
        /// Guarantees:
        ///   - Every picked class is valid for <paramref name="realm"/> via
        ///     <see cref="GlobalConstants.STARTING_CLASSES_DICT"/>.
        ///   - Avoids picking the same class twice when an alternative exists,
        ///     so an 8-man doesn't end up with two Heros and two Druids.
        /// </summary>
        public static List<eMimicClass> BuildComposition(eRealm realm, int groupSize)
        {
            List<eMimicClass> result = new(groupSize);

            if (!_classesByRole.TryGetValue(realm, out Dictionary<eRole, eMimicClass[]> rolesForRealm))
                return result;

            // Fail-safe: bots whose enum value isn't a starting class for the
            // realm would be rejected by MimicManager later, leaving holes in
            // the comp. Pre-filter here so the result is exactly the group
            // size the caller requested.
            GlobalConstants.STARTING_CLASSES_DICT.TryGetValue(realm, out List<eCharacterClass> startingClasses);

            int slots = System.Math.Clamp(groupSize, 1, _template.Length);
            HashSet<eMimicClass> alreadyPicked = new();

            for (int i = 0; i < slots; i++)
            {
                eRole role = _template[i];

                if (!rolesForRealm.TryGetValue(role, out eMimicClass[] candidates) || candidates.Length == 0)
                    continue;

                // Filter candidates against realm starting classes AND
                // duplicates already in the comp. If everything's a dup
                // (small candidate pool, e.g. only 2 healer classes for a
                // 3rd healer slot), fall back to the realm-valid set.
                List<eMimicClass> pool = new(candidates.Length);
                List<eMimicClass> fallback = new(candidates.Length);

                foreach (eMimicClass c in candidates)
                {
                    if (startingClasses != null && !startingClasses.Contains((eCharacterClass) c))
                        continue;
                    fallback.Add(c);
                    if (!alreadyPicked.Contains(c))
                        pool.Add(c);
                }

                List<eMimicClass> chooseFrom = pool.Count > 0 ? pool : fallback;
                if (chooseFrom.Count == 0)
                    continue;

                eMimicClass chosen = chooseFrom[Util.Random(chooseFrom.Count - 1)];
                result.Add(chosen);
                alreadyPicked.Add(chosen);
            }

            return result;
        }

        /// <summary>
        /// Walks the created mimics and assigns leader, main-assist, tank, CC, puller and healer roles
        /// based on each bot's class. The first qualified bot for each role wins; later candidates are skipped.
        /// </summary>
        public static void AutoAssignRoles(List<MimicNPC> mimics)
        {
            if (mimics == null || mimics.Count == 0)
                return;

            MimicGroup mg = mimics[0].Group?.MimicGroup;

            if (mg == null)
                return;

            MimicNPC tank = mimics.FirstOrDefault(m => IsTankClass(m));
            MimicNPC healer = mimics.FirstOrDefault(m => IsHealerClass(m));
            MimicNPC cc = mimics.FirstOrDefault(m => IsCCClass(m));
            MimicNPC puller = mimics.FirstOrDefault(m => MimicGroup.CanPull(m));

            // Leader & main assist default to the tank; the player remains the group leader
            // if they own the group, but inside the MimicGroup we still want a tank-focused assist.
            MimicNPC leader = tank ?? mimics[0];
            MimicNPC assist = tank ?? mimics[0];

            mg.SetLeader(leader);
            mg.SetMainAssist(assist);

            if (tank != null)   mg.SetMainTank(tank);
            if (cc != null)     mg.SetMainCC(cc);
            if (puller != null) mg.SetMainPuller(puller);

            // Flag EVERY healer-class mimic, not just the first. A Druid+Bard
            // or Cleric+Friar comp where only the first one heals leaves the
            // second bot DPSing in a healer kit — same fix as EnsureCampRoles.
            foreach (MimicNPC m in mimics)
            {
                if (m?.MimicBrain == null) continue;
                if (!IsHealerClass(m)) continue;
                if (!m.MimicBrain.IsHealer)
                    m.MimicBrain.IsHealer = true;
            }

            // Refresh the group window so the player sees the new role icons /
            // names immediately — without this the client keeps showing
            // whatever the group looked like at AddMember time.
            mimics[0].Group?.UpdateGroupWindow();
        }

        public static bool IsTankClass(MimicNPC m)
        {
            if (m == null) return false;
            return m.CombatProfile?.HasRole(eMimicCombatRole.Tank) == true;
        }

        public static bool IsHealerClass(MimicNPC m)
        {
            if (m == null) return false;
            if (m.CombatProfile?.HasRole(eMimicCombatRole.Healer) != true)
                return false;

            // Heretic / Warden / Valkyrie carry the Healer role flag as a
            // secondary capability, but their PRIMARY identity is caster DPS
            // (Heretic), tank/support (Warden) or tank + OdinsWill battlecaster
            // (Valkyrie). Auto-flagging them as IsHealer would route their cycle
            // through CheckHeals only and suppress their melee + offensive cast
            // output entirely (AttackMostWanted returns early for healers). They
            // still heal opportunistically via CheckSpells/CheckHeals, and a
            // player who wants a dedicated healer can toggle via /mset healer.
            switch (m.CharacterClass.ID)
            {
                case (int)eCharacterClass.Heretic:
                case (int)eCharacterClass.Warden:
                case (int)eCharacterClass.Valkyrie:
                    return false;
            }
            return true;
        }

        public static bool IsCCClass(MimicNPC m)
        {
            if (m == null) return false;
            return m.CombatProfile?.HasRole(eMimicCombatRole.CrowdControl) == true;
        }

        /// <summary>
        /// Fills in any role still null on the supplied group's MimicGroup.
        /// Cheaper than re-running the whole AutoAssignRoles pass and used by
        /// /mcamp to make sure a camp session has a usable puller/tank/CC
        /// even when the player skipped /mgroup.
        ///
        /// IMPORTANT: the MimicGroup constructor pre-fills every role with
        /// the group leader (the player in mixed groups). A player satisfies
        /// CanPull/IsAlive too, so a naive "only override when null" check
        /// would leave every camp role on the player and no bot would run
        /// puller/tank/CC logic. We therefore actively prefer a mimic over
        /// the player for camp roles whenever a suitable mimic is available.
        /// </summary>
        public static void EnsureCampRoles(Group group)
        {
            if (group?.MimicGroup is not MimicGroup mg)
                return;

            // Collect mimic members once.
            List<MimicNPC> mimics = new();
            foreach (GameLiving gl in group.GetMembersInTheGroup())
                if (gl is MimicNPC m) mimics.Add(m);

            if (mimics.Count == 0)
                return;

            // Puller: prefer a mimic archer, then any mimic that can pull. The
            // current MainPuller is only kept if it is ALREADY a mimic that
            // can pull — a player holder is replaced when a bot can do it.
            //
            // Important: we ALWAYS install a mimic, even when no bot has a
            // bow or harmful spell. A pure-melee group still needs a puller
            // so CheckPuller runs — the brain handles a "body pull" fallback
            // (run to target, hit once, return) for melee-only bots.
            bool currentPullerIsMimic = mg.MainPuller is MimicNPC pullerMimic
                                        && pullerMimic.IsAlive;
            if (!currentPullerIsMimic)
            {
                MimicNPC chosen = PickBestPuller(mimics);
                if (chosen != null)
                    ForceSetMainPuller(mg, chosen);
            }

            // Tank: prefer a mimic tank class, then any living mimic. Replace
            // the player even if alive — the player isn't actually running the
            // tank FSM logic.
            bool currentTankIsMimic = mg.MainTank is MimicNPC tankMimic && tankMimic.IsAlive;
            if (!currentTankIsMimic)
            {
                MimicNPC tank = mimics.FirstOrDefault(m => m.IsAlive && IsTankClass(m))
                                ?? mimics.FirstOrDefault(m => m.IsAlive);
                if (tank != null)
                    mg.SetMainTank(tank);
            }

            // CC: only assign if a mimic CC class is actually present —
            // there's no point parking the role on a non-CC.
            bool currentCCIsMimic = mg.MainCC is MimicNPC ccMimic && ccMimic.IsAlive && IsCCClass(ccMimic);
            if (!currentCCIsMimic)
            {
                MimicNPC cc = mimics.FirstOrDefault(m => m.IsAlive && IsCCClass(m));
                if (cc != null)
                    mg.SetMainCC(cc);
            }

            // Main assist always follows the tank so DPS focuses correctly.
            if (mg.MainTank != null && mg.MainTank.IsAlive && mg.MainAssist != mg.MainTank)
                mg.SetMainAssist(mg.MainTank);

            // Flag every healer-capable mimic as IsHealer in camp mode. The
            // previous "FirstOrDefault if !anyHealer" left additional healer
            // classes (e.g. a 2nd Cleric, a Druid + Bard combo) doing melee/
            // caster DPS instead of healing, which is how groups die on
            // ordinary mobs. In camp mode we want every healer-class bot
            // pulling pure heal duty — DPS is what the tank, melee bots and
            // nukers are there for.
            foreach (MimicNPC healer in mimics)
            {
                if (healer?.MimicBrain == null) continue;
                if (!IsHealerClass(healer)) continue;
                if (!healer.MimicBrain.IsHealer)
                    healer.MimicBrain.IsHealer = true;
            }
        }

        /// <summary>
        /// Installs <paramref name="puller"/> as the camp's MainPuller. With
        /// the new non-toggling <see cref="MimicGroup.SetMainPuller"/> this is
        /// a thin wrapper, but it also covers the body-pull fallback path
        /// where the chosen bot doesn't pass <see cref="MimicGroup.CanPull"/>
        /// (no bow + no harmful spell). In that case we bypass the CanPull
        /// gate and write the field directly via reflection-free means.
        /// </summary>
        private static void ForceSetMainPuller(MimicGroup mg, MimicNPC puller)
        {
            if (mg == null || puller == null)
                return;
            if (mg.MainPuller == puller)
                return;

            if (MimicGroup.CanPull(puller))
            {
                mg.SetMainPuller(puller);
                return;
            }

            // Body-pull fallback: bot has neither ranged weapon nor harmful
            // spell. SetMainPuller would refuse, but the brain's PerformPull
            // already supports a melee body-pull. Use the explicit setter
            // override so the role still sticks.
            mg.ForceSetMainPullerForBodyPull(puller);
        }

        /// <summary>
        /// Scores every alive mimic for puller suitability and returns the
        /// best candidate. The hierarchy matches what a human picks:
        ///   100  + level — has DistanceWeapon (bow / crossbow / thrown)
        ///    70  + level — caster with a real pull spell (snare/dot/debuff,
        ///                  long range, short cast)
        ///    50  + level — anyone with harmful spells (instant cast pref)
        ///    20  + level — body-pull fallback (pure melee, no ranged tool)
        /// A higher level breaks ties so the strongest available pulls.
        /// </summary>
        public static MimicNPC PickBestPuller(List<MimicNPC> mimics)
        {
            if (mimics == null || mimics.Count == 0)
                return null;

            MimicNPC best = null;
            int bestScore = int.MinValue;

            foreach (MimicNPC m in mimics)
            {
                if (m == null || !m.IsAlive)
                    continue;

                int score = ScorePullerCandidate(m);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }

            return best;
        }

        private static int ScorePullerCandidate(MimicNPC m)
        {
            bool hasBow = m.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null;
            bool hasHarmful = m.CanCastHarmfulSpells || m.CanCastInstantHarmfulSpells;

            int score;
            if (hasBow)
                score = 100;
            else if (hasHarmful && m.MimicBrain != null && m.MimicBrain.SelectPullSpell() != null)
                score = 70;
            else if (hasHarmful)
                score = 50;
            else
                score = 20; // body-pull eligible — never null so a group always has one

            // Tank classes are the worst body-pull candidates (they're needed
            // at camp to soak the incoming mob). Penalise unless they're the
            // only option.
            if (IsTankClass(m))
                score -= 15;

            // Higher level wins ties — more pull range, better aim, harder to
            // miss the shot.
            score += m.Level;

            return score;
        }
    }
}
