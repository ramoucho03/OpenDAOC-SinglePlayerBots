using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.Realm;
using DOL.GS.RealmAbilities;
using DOL.GS.ServerProperties;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.GS.Styles;
using DOL.Language;
using Microsoft.CodeAnalysis;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using static DOL.GS.AttackData;
using static DOL.GS.GamePlayer;
using static DOL.GS.IGameStaticItemOwner;

namespace DOL.GS.Scripts
{
    // MimicNPC is intentionally split across several partial files (see
    // MimicNPC.*.cs) so the per-concern surface (Combat, Equipment, …) stays
    // navigable. Add a new partial file rather than growing this one further.
    public partial class MimicNPC : GameNPC, IGamePlayer, IGameStaticItemOwner
    {
        private DummyPacketLib _dummyLib;
        private DummyClient _dummyClient;

        public AttackComponent AttackComponent { get { return attackComponent; } }
        public RangeAttackComponent RangeAttackComponent { get { return rangeAttackComponent; } }
        public StyleComponent StyleComponent { get { return styleComponent; } }
        public EffectListComponent EffectListComponent { get { return effectListComponent; } }
        public Lock AwardLock { get; } = new();

        private MimicSpawner _mimicSpawner;
        public MimicSpawner MimicSpawner
        {
            get { return _mimicSpawner; }
            set { _mimicSpawner = value; }
        }

        public IPacketLib Out { get { return _dummyLib; } }
        public GameClient Client { get { return _dummyClient; } }

        public bool CanUseSideStyles { get { return StylesSide != null && StylesSide.Count > 0; } }
        public bool CanUseBackStyles { get { return StylesBack != null && StylesBack.Count > 0; } }
        public bool CanUseFrontStyle { get { return StylesFront != null && StylesFront.Count > 0; } }
        public bool CanUseAnytimeStyles { get { return StylesAnytime != null && StylesAnytime.Count > 0; } }
        public bool CanUsePositionalStyles { get { return CanUseSideStyles || CanUseBackStyles; } }
        public bool CanCastInstantCrowdControlSpells { get { return InstantCrowdControlSpells != null && InstantCrowdControlSpells.Count > 0; } }
        public bool CanCastCrowdControlSpells { get { return CrowdControlSpells != null && CrowdControlSpells.Count > 0; } }
        public bool CanCastBolts { get { return BoltSpells != null && BoltSpells.Count > 0; } }

        public List<Style> StylesTaunt { get; protected set; } = null;
        public List<Style> StylesDetaunt { get; protected set; } = null;
        public List<Style> StylesShield { get; protected set; } = null;

        public override int InteractDistance => WorldMgr.VISIBILITY_DISTANCE;

        /// <summary>
		/// Instant Crowd Controll spell list and accessor
		/// </summary>
		public List<Spell> InstantCrowdControlSpells { get; set; } = null;

        /// <summary>
		/// Crowd Controll spell list and accessor
		/// </summary>
        public List<Spell> CrowdControlSpells { get; set; } = null;

        /// <summary>
		/// Bolt spell list and accessor
		/// </summary>
        public List<Spell> BoltSpells { get; set; } = null;

        public MimicSpec MimicSpec = new MimicSpec();
        public MimicCombatProfile CombatProfile { get; private set; }
        public int Kills;

        /// <summary>
        /// Account name of the player who created/owns this bot. Used by
        /// MimicManager to hibernate the bot when the owner disconnects and
        /// restore it on reconnect. Null for bots spawned by world systems
        /// (battlegrounds, spawners) which have no individual owner.
        /// </summary>
        public string OwnerAccount { get; set; }

        /// <summary>
        /// Max distance (in game units) at which an ungrouped owned bot will
        /// mirror its owner's sprint. A bot parked on the other side of the
        /// world (e.g. /camp left in a city while owner runs in a frontier)
        /// must NOT toggle Sprint just because the player did — it wastes
        /// endurance and creates desync at huge range.
        /// </summary>
        public const int SPRINT_MIRROR_RANGE = 2500;

        /// <summary>
        /// Resolves the owning <see cref="GamePlayer"/> to mirror sprint from
        /// when this bot is NOT in a group (personal pet / /mfollow / /msummon
        /// scenario). Returns null when the bot is grouped (the grouped path
        /// handles that), when the owner is offline, in a different region,
        /// or further than <see cref="SPRINT_MIRROR_RANGE"/> units away.
        /// </summary>
        public GamePlayer GetOwnerPlayerForSprintMirror()
        {
            // If the bot is grouped, the group leader path in MimicBrain.Think
            // already provides a reference — don't fight it here.
            if (Group != null)
                return null;

            if (string.IsNullOrEmpty(OwnerAccount))
                return null;

            GameClient client = ClientService.Instance.GetClientFromAccountName(OwnerAccount);
            GamePlayer owner = client?.Player;
            if (owner == null || !owner.IsAlive)
                return null;

            if (owner.CurrentRegion != CurrentRegion)
                return null;

            if (!IsWithinRadius(owner, SPRINT_MIRROR_RANGE))
                return null;

            return owner;
        }

        private MimicBrain m_mimicBrain;

        public MimicBrain MimicBrain
        {
            get { return m_mimicBrain; }
            set { m_mimicBrain = value; }
        }

        private int m_leftOverSpecPoints = 0;

        public MimicNPC(eMimicClass cClass, byte level, eGender gender = eGender.Neutral, eSpecType spec = eSpecType.None) : base(new MimicBrain())
        {
            _dummyClient = new DummyClient(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
            _dummyLib = new DummyPacketLib();

            Inventory = new MimicNPCInventory();
            MaxSpeedBase = PLAYER_BASE_SPEED;

            MimicSpec = MimicSpec.GetSpec(cClass, spec);
            CombatProfile = MimicCombatProfileRegistry.Get(cClass, MimicSpec?.SpecType ?? spec);

            // Hard fail-fast on bad construction inputs. Without this guard
            // a typo in /mcreate (unknown class id) silently produces a mimic
            // with null CharacterClass + MimicSpec, which then NPEs deep in
            // SetWeapons / RefreshSpecDependantSkills / brain Think. Better
            // to throw at construction so the command handler reports the
            // error to the player.
            if (!SetCharacterClass((int)cClass))
                throw new InvalidOperationException($"MimicNPC: unknown character class id {(int)cClass} ({cClass}).");

            if (MimicSpec == null)
                throw new InvalidOperationException($"MimicNPC: no MimicSpec registered for {cClass}/{spec}.");

            if (CombatProfile == null)
                throw new InvalidOperationException($"MimicNPC: no MimicCombatProfile registered for {cClass}/{MimicSpec.SpecType}.");

            SetRaceAndName();
            SetBrain(cClass);
            CreateStatistics();
            SetLevel(level);

            // Force-load specs and spell-dependant skills now. The Level setter
            // gates OnLevelUp behind `if (oldLevel > 0)`, so a fresh mimic spawned
            // at level N from base level 0 NEVER triggers OnLevelUp and thus never
            // calls RefreshSpecDependantSkills. Without this call, the MimicSpec's
            // custom ratios / caps (LoadClassSpecializations) are silently dropped
            // — extension classes like Vampiir / Warlock / Mauler get the
            // hard-coded base class spec list only, ignoring our spec table.
            RefreshSpecDependantSkills(false);

            // Same gate problem for SpendSpecPoints + SetCasterSpells/SetSpells.
            // OnLevelUp also runs these, so a fresh /mcreate'd mimic ended up
            // with every spec at level 0 and an empty Body.Spells list. The brain
            // then fell back to the GameNPC default spell pool — which for
            // Sorcerer / Theurgist / Wizard etc. is whatever the parent NPC
            // template carried, NOT the class's actual spells. Spend the spec
            // points now (level 0 -> Level), re-run RefreshSpecDependantSkills
            // so newly-unlocked spell lines (which depend on the post-spend
            // spec levels) are picked up, and finally build Body.Spells via
            // the right path for the class type.
            SpendSpecPoints(level, 0);
            RefreshSpecDependantSkills(false);
            if (CharacterClass.ClassType == eClassType.ListCaster)
                SetCasterSpells();
            else
                SetSpells();

            SetWeapons();
            SetShield();
            SetRanged();
            SetArmor();
            SetJewelry();
            RefreshItemBonuses();
            IsCloakHoodUp = Util.Random(1) > 0;

            Health = MaxHealth;
            Endurance = MaxEndurance;
            Mana = MaxMana;

            RespawnInterval = -1;
            InitializeRandomDecks();

            m_combatTimer = new ECSGameTimer(this, new ECSGameTimer.ECSTimerCallback(_ =>
            {
                return 0;
            }));
        }

        private void SetBrain(eMimicClass mimicClass)
        {
            switch (mimicClass)
            {
                case eMimicClass.Infiltrator:
                case eMimicClass.Nightshade:
                case eMimicClass.Shadowblade:
                MimicBrain = new AssassinBrain();
                break;

                case eMimicClass.Scout:
                case eMimicClass.Ranger:
                case eMimicClass.Hunter:
                MimicBrain = new ArcherBrain();
                break;

                default:
                MimicBrain = new MimicBrain();
                break;
            }
            
            MimicBrain.MimicBody = this;
            SetOwnBrain(MimicBrain);
        }

        private void SetWeapons()
        {
            switch (MimicSpec.SpecType)
            {
                case eSpecType.DualWield:
                case eSpecType.DualWieldAndShield:
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.leftHand);
                break;

                case eSpecType.LeftAxe:
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.twoHand);
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponTwoType, eHand.leftHand);
                break;

                case eSpecType.OneHandAndShield:
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                break;

                case eSpecType.OneHandHybrid:
                case eSpecType.TwoHandHybrid:
                case eSpecType.TwoHanded when CharacterClass.ID != (int)eCharacterClass.Valewalker:
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponTwoType, eHand.twoHand, MimicSpec.DamageType);
                break;

                case eSpecType.Mid:
                case eSpecType.PacHealer:
                case eSpecType.AugHealer:
                case eSpecType.MendHealer:
                case eSpecType.MendShaman:
                case eSpecType.AugShaman:
                case eSpecType.SubtShaman:
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.twoHand);
                break;

                case eSpecType.Instrument:
                MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                MimicEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.TwoHandWeapon, eInstrumentType.Flute);
                MimicEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.DistanceWeapon, eInstrumentType.Drum);
                MimicEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.FirstEmptyBackpack, eInstrumentType.Lute);
                break;

                default:
                if (CharacterClass.ClassType == eClassType.ListCaster ||
                    CharacterClass.ID == (int)eCharacterClass.Friar ||
                    CharacterClass.ID == (int)eCharacterClass.Valewalker)
                    MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.twoHand);
                else if (CharacterClass.ID != (int)eCharacterClass.Hunter)
                    MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                else
                    MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.twoHand);

                if (MimicSpec.WeaponOneType == eObjectType.Sword)
                    MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponOneType, eHand.oneHand);
                break;
            }

            if (MimicSpec.Is2H)
                SwitchWeapon(eActiveWeaponSlot.TwoHanded);
            else
                SwitchWeapon(eActiveWeaponSlot.Standard);
        }

        private void SetRanged()
        {
            foreach (Ability ability in GetAllAbilities())
            {
                switch (ability.KeyName)
                {
                    case GS.Abilities.Weapon_Thrown: MimicEquipment.SetRangedWeapon(this, eObjectType.Thrown); break;
                    case GS.Abilities.Weapon_Shortbows: MimicEquipment.SetRangedWeapon(this, eObjectType.Fired); break;
                    case GS.Abilities.Weapon_Crossbow: MimicEquipment.SetRangedWeapon(this, eObjectType.Crossbow); break;
                    case GS.Abilities.Weapon_RecurvedBows: MimicEquipment.SetRangedWeapon(this, eObjectType.RecurvedBow); SwitchWeapon(eActiveWeaponSlot.Distance); break;
                    case GS.Abilities.Weapon_Longbows: MimicEquipment.SetRangedWeapon(this, eObjectType.Longbow); SwitchWeapon(eActiveWeaponSlot.Distance); break;
                    case GS.Abilities.Weapon_CompositeBows: MimicEquipment.SetRangedWeapon(this, eObjectType.CompositeBow); SwitchWeapon(eActiveWeaponSlot.Distance); break;
                }
            }
        }

        private void SetJewelry()
        {
            MimicEquipment.SetJewelryROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Magical);
        }

        private void SetShield()
        {
            MimicEquipment.SetShield(this, BestShieldLevel);
        }

        private void SetArmor()
        {
            int armorLevel = BestArmorLevel;
            eObjectType armorType = eObjectType.GenericArmor;

            switch (armorLevel)
            {
                case 1: armorType = eObjectType.Cloth; break;
                case 2: armorType = eObjectType.Leather; break;

                case 3:
                {
                    if (Realm == eRealm.Hibernia)
                        armorType = eObjectType.Reinforced;
                    else
                        armorType = eObjectType.Studded;
                    break;
                }

                case 4:
                {
                    if (Realm == eRealm.Hibernia)
                        armorType = eObjectType.Scale;
                    else
                        armorType = eObjectType.Chain;
                    break;
                }

                case 5: armorType = eObjectType.Plate; break;
            }

            if (MimicConfig.ARMOR_ROG)
            {
                int min = Math.Max(1, Level - 3);
                int max = Math.Min(51, Level + 3);
                byte level = (byte)Util.Random(min, max);

                MimicEquipment.SetArmorROG(this, Realm, (eCharacterClass)CharacterClass.ID, level, armorType);
            }
            else
                MimicEquipment.SetArmor(this, armorType);
        }

        #region Menu localization

        // Canonical action name => translation key for the clickable button label.
        // The canonical name is what the dispatcher in WhisperReceive switches on.
        // For EN players the canonical name and the rendered label match exactly,
        // so all legacy callers (commands sending "[Stay]", etc.) continue to work.
        private static readonly (string Action, string Key)[] _menuButtons = new[]
        {
            ("State",          "Mimic.Button.State"),
            ("Prevent Combat", "Mimic.Button.PreventCombat"),
            ("Brain",          "Mimic.Button.Brain"),
            ("Debug",          "Mimic.Button.Debug"),
            ("Group",          "Mimic.Button.Group"),
            ("Leader",         "Mimic.Button.Leader"),
            ("MainPuller",     "Mimic.Button.MainPuller"),
            ("MainCC",         "Mimic.Button.MainCC"),
            ("MainTank",       "Mimic.Button.MainTank"),
            ("MainAssist",     "Mimic.Button.MainAssist"),
            ("Healer",         "Mimic.Button.Healer"),
            ("Guard",          "Mimic.Button.Guard"),
            ("Protect",        "Mimic.Button.Protect"),
            ("Intercept",      "Mimic.Button.Intercept"),
            ("Stay",           "Mimic.Button.Stay"),
            ("Follow",         "Mimic.Button.Follow"),
            ("Camp",           "Mimic.Button.Camp"),
            ("Reset",          "Mimic.Button.Reset"),
            ("Pull",           "Mimic.Button.Pull"),
            ("Roam",           "Mimic.Button.Roam"),
            ("Spells",         "Mimic.Button.Spells"),
            ("Inst Harmful",   "Mimic.Button.InstHarmful"),
            ("Harmful Spells", "Mimic.Button.HarmfulSpells"),
            ("Inst Misc",      "Mimic.Button.InstMisc"),
            ("Misc Spells",    "Mimic.Button.MiscSpells"),
            ("Inst Heal",      "Mimic.Button.InstHeal"),
            ("Heal Spells",    "Mimic.Button.HealSpells"),
            ("CC",             "Mimic.Button.CC"),
            ("Styles",         "Mimic.Button.Styles"),
            ("Abilities",      "Mimic.Button.Abilities"),
            ("Spec",           "Mimic.Button.Spec"),
            ("Stats",          "Mimic.Button.Stats"),
            ("Ability Effects","Mimic.Button.AbilityEffects"),
            ("Spell Effects",  "Mimic.Button.SpellEffects"),
            ("Inventory",      "Mimic.Button.Inventory"),
            ("Hood",           "Mimic.Button.Hood"),
            ("RightHand",      "Mimic.Button.RightHand"),
            ("LeftHand",       "Mimic.Button.LeftHand"),
            ("TwoHand",        "Mimic.Button.TwoHand"),
            ("Ranged",         "Mimic.Button.Ranged"),
            ("Helm",           "Mimic.Button.Helm"),
            ("Torso",          "Mimic.Button.Torso"),
            ("Legs",           "Mimic.Button.Legs"),
            ("Arms",           "Mimic.Button.Arms"),
            ("Hands",          "Mimic.Button.Hands"),
            ("Boots",          "Mimic.Button.Boots"),
            ("Jewelry",        "Mimic.Button.Jewelry"),
            ("Delete",         "Mimic.Button.Delete"),
            ("Help",           "Mimic.Button.Help"),
        };

        // Renders "[<localized label>]" — clickable in the popup window.
        private static string Lbl(string lang, string key)
        {
            string label = LanguageMgr.TryGetTranslation(out string t, lang, key) ? t : key;
            return "[" + label + "]";
        }

        // Resolves the bracket label clicked by the player back to its canonical action.
        // Accepts both canonical English (for legacy/script callers) and any localized variant.
        // Case-insensitive throughout so a lowercased compact token like "state" still
        // routes to the canonical `State` switch case in WhisperReceive.
        private static string ResolveAction(string str, string lang)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            for (int i = 0; i < _menuButtons.Length; i++)
            {
                if (string.Equals(str, _menuButtons[i].Action, StringComparison.OrdinalIgnoreCase))
                    return _menuButtons[i].Action;
            }

            for (int i = 0; i < _menuButtons.Length; i++)
            {
                if (LanguageMgr.TryGetTranslation(out string t, lang, _menuButtons[i].Key)
                    && string.Equals(t, str, StringComparison.OrdinalIgnoreCase))
                    return _menuButtons[i].Action;
            }

            return str;
        }

        private static string T(string lang, string key)
        {
            return LanguageMgr.TryGetTranslation(out string t, lang, key) ? t : key;
        }

        // Names of mimic-related commands we accept as whisper-routable. When the
        // player clicks a bracket like `[mmenu create]` the client whispers that
        // text to us, and we re-prefix it with `/` before running it as a real
        // command. Keep this list in sync with [CmdAttribute("&xxx", ...)].
        // Stored without leading `/` and lower-case for cheap comparison.
        private static readonly HashSet<string> _routableMimicCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "mmenu", "mhelp", "mlfg", "mclear",
            "mcreate", "mgroup", "mspawner", "mbattle", "mbstats",
            "msummon", "mfollow", "mattack", "mpull", "mpullfrom",
            "mcamp", "mrole", "mheal", "mpvp", "mpc",
            "mguard", "mprotect", "mintercept",
            "mstrategy", "mmimicstats",
            "pvpfrontier",
        };

        /// <summary>
        /// If the whisper text looks like a mimic command keyword (e.g. "mmenu
        /// create", "mlfg 3"), forward it to ScriptMgr.HandleCommand on the
        /// caller's client, prefixed with `/`. Returns true if the whisper was
        /// consumed as a command and no further menu processing should happen.
        /// </summary>
        private static bool TryRouteAsCommand(GamePlayer player, string str)
        {
            if (player?.Client == null || string.IsNullOrWhiteSpace(str))
                return false;

            // Allow either `cmd ...` or `/cmd ...` — the latter is for the rare
            // client where bracketed `[/cmd]` actually does whisper through.
            string trimmed = str[0] == '/' ? str[1..] : str;

            int sp = trimmed.IndexOf(' ');
            string head = sp < 0 ? trimmed : trimmed[..sp];

            if (!_routableMimicCommands.Contains(head))
                return false;

            ScriptMgr.HandleCommand(player.Client, "/" + trimmed);
            return true;
        }

        #endregion Menu localization

        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player))
                return false;

            string lang = player.Client?.Account?.Language;
            System.Text.StringBuilder sb = new();
            sb.Append(T(lang, "Mimic.Menu.Header")).Append('\n');
            sb.Append("---------------------------------------\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.State")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.State")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.PreventCombat")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Brain")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Debug")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Roles")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Group")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.Leader")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.MainPuller")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.MainCC")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.MainTank")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.MainAssist")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.Healer")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Defensive")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Guard")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Protect")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Intercept")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Orders")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Stay")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Follow")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Camp")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Reset")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Pull")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Roam")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Spells")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Spells")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.InstHarmful")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.HarmfulSpells")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.InstMisc")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.MiscSpells")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.InstHeal")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.HealSpells")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.CC")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Combat")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Styles")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.Abilities")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Info")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Spec")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.Stats")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.AbilityEffects")).Append(" - ");
            sb.Append(Lbl(lang, "Mimic.Button.SpellEffects")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Inventory")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Inventory")).Append("\n\n");

            sb.Append(T(lang, "Mimic.Menu.Sec.Equipment")).Append(": ");
            sb.Append(Lbl(lang, "Mimic.Button.Hood")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.RightHand")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.LeftHand")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.TwoHand")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Ranged")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Helm")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Torso")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Legs")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Arms")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Hands")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Boots")).Append(' ');
            sb.Append(Lbl(lang, "Mimic.Button.Jewelry")).Append("\n\n");

            sb.Append(Lbl(lang, "Mimic.Button.Delete"));
            sb.Append("  ").Append(Lbl(lang, "Mimic.Button.Help"));
            sb.Append("\n");

            // The four global sections (LFG/BG, Ordres globaux, Camp, Modes)
            // were removed from the menu in 2026-05: they weren't reliably
            // dispatching the underlying slash commands and just cluttered
            // the popup. The dispatcher in ResolveGlobalMenuKeyword stays
            // alive so existing chat macros keep working; only the buttons
            // are gone.

            player.Out.SendMessage(sb.ToString(), eChatType.CT_Say, eChatLoc.CL_PopupWindow);
            return true;
        }

        /// <summary>
        /// Maps a clickable bracket keyword from the global-commands section of
        /// the right-click menu to its `/slashCommand` equivalent. Returns null
        /// when the keyword isn't a global menu action.
        /// </summary>
        private static string ResolveGlobalMenuKeyword(string str)
        {
            return str switch
            {
                "lfg"          => "/mlfg",
                "lfgPage2"     => "/mlfg page 2",
                "grpAlb"       => "/mgroup alb",
                "grpHib"       => "/mgroup hib",
                "grpMid"       => "/mgroup mid",
                "bgThid"       => "/mbattle thid start",
                "bgStop"       => "/mbattle thid stop",
                "bgClear"      => "/mbattle thid clear",
                "clearAll"     => "/mclear",
                "gSummon"      => "/msummon",
                "gFollow"      => "/mfollow",
                "gAttack"      => "/mattack",
                "gPull"        => "/mpull",
                "gPullHere"    => "/mpullfrom here",
                "gPullClr"     => "/mpullfrom remove",
                "campSet"      => "/mcamp set",
                "campHere"     => "/mcamp here",
                "campOff"      => "/mcamp remove",
                "campAgg550"   => "/mcamp aggrorange 550",
                "campAgg1500"  => "/mcamp aggrorange 1500",
                "campBlue"     => "/mcamp filter blue",
                "campYellow"   => "/mcamp filter yellow",
                "campOrange"   => "/mcamp filter orange",
                "pvpOn"        => "/mpvp true",
                "pvpOff"       => "/mpvp false",
                "pcOn"         => "/mpc true",
                "pcOff"        => "/mpc false",
                "help"         => "/mhelp",
                "bstats"       => "/mbstats",
                // /mmenu navigation tokens — single-word so 1.127 treats them
                // as clickable, mapped here to the matching /mmenu sub-view.
                "mnu"          => "/mmenu",
                "mnuCreate"    => "/mmenu create",
                "mnuOrders"    => "/mmenu orders",
                "mnuCamp"      => "/mmenu camp",
                "mnuRoles"     => "/mmenu roles",
                "mnuModes"     => "/mmenu modes",
                "mnuStrat"     => "/mmenu strat",
                "mnuBg"        => "/mmenu bg",
                "mnuInfo"      => "/mmenu info",
                "mnuAdmin"     => "/mmenu admin",
                // Roles sub-view: assigning the currently targeted mimic.
                "roleLead"     => "/mrole leader",
                "roleTank"     => "/mrole tank",
                "roleAssist"   => "/mrole assist",
                "roleCC"       => "/mrole cc",
                "rolePull"     => "/mrole puller",
                "heal"         => "/mheal",
                // Strategy sub-view.
                "stratList"    => "/mstrategy list",
                // PvP frontier admin sub-view (server filters on PrivLevel).
                "pfStatus"     => "/pvpfrontier status",
                "pfStart"      => "/pvpfrontier start",
                "pfStop"       => "/pvpfrontier stop",
                "pfClear"      => "/pvpfrontier clear",
                _              => null,
            };
        }

        public override bool WhisperReceive(GameLiving source, string str)
        {
            if (!base.WhisperReceive(source, str))
                return false;

            GamePlayer player = source as GamePlayer;

            if (player == null)
                return false;

            // Global-menu shortcut keywords come from the right-click menu's
            // "Groupes / Ordres / Camp / Modes" section. Map them to their full
            // slash command and run it on the player's behalf — this is the
            // working clickable path on v1.127 because the right-click menu
            // popup already has the interaction context the client needs.
            string globalCmd = ResolveGlobalMenuKeyword(str);
            if (globalCmd != null && player.Client != null)
            {
                ScriptMgr.HandleCommand(player.Client, globalCmd);
                return true;
            }

            // LFG bracket clicks come in as compact single-word tokens — the
            // 1.127 client refuses to treat multi-word `[mlfg 5]` brackets as
            // clickable, so the popup uses `[lfg5]` (recruit) and `[lfgp2]`
            // (page nav) instead. Route them back to the real /mlfg command.
            if (player.Client != null && !string.IsNullOrEmpty(str))
            {
                if (str.Length > 4
                    && str.StartsWith("lfgp", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(str.AsSpan(4), out int pageNum)
                    && pageNum >= 1)
                {
                    ScriptMgr.HandleCommand(player.Client, "/mlfg page " + pageNum);
                    return true;
                }

                if (str.Length > 3
                    && str.StartsWith("lfg", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(str.AsSpan(3), out int lfgIdx)
                    && lfgIdx >= 1)
                {
                    ScriptMgr.HandleCommand(player.Client, "/mlfg " + lfgIdx);
                    return true;
                }
            }

            // Routing for clickable popup brackets that whisper a bare command
            // name (e.g. `[mmenu create]`). Kept as a fallback path.
            if (TryRouteAsCommand(player, str))
                return true;

            string lang = player.Client?.Account?.Language;
            string message = string.Empty;
            int itemIndex;

            if (int.TryParse(str, out itemIndex) )
            {
                itemIndex += (int)eInventorySlot.FirstBackpack - 1;

                if (itemIndex < (int)eInventorySlot.LastBackpack)
                {
                    DbInventoryItem item = Inventory.GetItem((eInventorySlot)itemIndex);

                    if (item != null && RemoveAndAddItemToEmptyPlayerInventorySlot(item, player))
                        return true;
                }
            }

            // Translate the clicked label back to a canonical action token so the
            // dispatcher below can stay in EN regardless of the player's language.
            str = ResolveAction(str, lang);

            switch (str)
            {
                case "Debug":
                {
                    MimicBrain.Debug = !MimicBrain.Debug;

                    message = "Debug is " + MimicBrain.Debug;
                    break;
                }

                case "Brain":
                {
                    // Brain reset: rebuild the FSM/aggro state from scratch and put
                    // the bot back into WAKING_UP. Preserve the player-configured
                    // flags (healer/pvp/prevent-combat/strategies) so the reset is
                    // a "stuck unlocker" and not a full config wipe.
                    MimicBrain oldBrain = MimicBrain;
                    bool wasHealer = oldBrain?.IsHealer ?? false;
                    bool wasPvP = oldBrain?.PvPMode ?? false;
                    bool wasPreventCombat = oldBrain?.PreventCombat ?? false;
                    int oldAggroLevel = oldBrain?.AggroLevel ?? 100;
                    int oldAggroRange = oldBrain?.AggroRange ?? 3600;

                    MimicBrain newBrain = new MimicBrain();
                    newBrain.MimicBody = this;
                    newBrain.IsHealer = wasHealer;
                    newBrain.PvPMode = wasPvP;
                    newBrain.PreventCombat = wasPreventCombat;
                    newBrain.AggroLevel = oldAggroLevel;
                    newBrain.AggroRange = oldAggroRange;
                    SetOwnBrain(newBrain);

                    message = T(lang, "Mimic.Reply.BrainReset");
                    if (string.IsNullOrEmpty(message) || message == "Mimic.Reply.BrainReset")
                        message = "Brain reset — I'm back in WAKING_UP.";
                    break;
                }

                case "State":
                {
                    message = Brain.FSM.GetCurrentState().ToString() + "\n";
                    message += "IsSitting: " + IsSitting + "\n";
                    message += "IsMezzed: " + IsMezzed + "\n";
                    message += "IsStunned: " + IsStunned + "\n";
                    message += "IsRooted: " + IsRooted + "\n";
                    message += "InCombat: " + InCombat + "\n";
                    message += "PvPMode: " + MimicBrain.PvPMode + "\n";
                    message += "Prevent Combat: " + MimicBrain.PreventCombat;
                    break;
                }

                case "Prevent Combat": MimicBrain.PreventCombat = !MimicBrain.PreventCombat; break;

                case "Group":
                {
                    if (Group != null && Group.GetMembersInTheGroup().Count < 8)
                    {
                        Group.AddMember(player);
                        break;
                    }

                    if (player.Group == null)
                    {
                        player.Group = new Group(player);
                        player.Group.AddMember(player);
                    }
                    else
                    {
                        if (player.Group.GetMembersInTheGroup().Contains(this) || player.Group.MemberCount > 8)
                            break;
                    }

                    player.Group.AddMember(this);
                    MimicBrain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                }
                break;

                case "Leader": Group?.MimicGroup.SetLeader(this); break;
                case "MainPuller":
                    if (Group?.MimicGroup is { } mgPullerToggle)
                    {
                        if (mgPullerToggle.MainPuller == this)
                            mgPullerToggle.ClearMainPuller();
                        else
                            mgPullerToggle.SetMainPuller(this);
                    }
                    break;
                case "MainCC": Group?.MimicGroup.SetMainCC(this); break;
                case "MainTank": Group?.MimicGroup.SetMainTank(this); break;
                case "MainAssist": Group?.MimicGroup.SetMainAssist(this); break;
                case "Healer": Group?.MimicGroup.SetHealer(this); break;

                case "Guard":
                {
                    if (!HasAbility("Guard"))
                    {
                        message = T(lang, "Mimic.Reply.IDoNotHaveThatAbility");
                        break;
                    }

                    if (Group == null || Group != null && !Group.IsInTheGroup(player))
                    {
                        message = T(lang, "Mimic.Reply.MustBeInSameGroup");
                        break;
                    }

                    if (MimicBrain.SetGuard(player, out bool foundOurEffect))
                        message = T(lang, "Mimic.Reply.IWillGuardYou");
                    else
                    {
                        if (foundOurEffect)
                            message = T(lang, "Mimic.Reply.IWillNoLongerGuard");
                        else
                            message = T(lang, "Mimic.Reply.ICannotGuardYou");
                    }

                    break;
                }

                case "Protect":
                    {
                        if (!HasAbility("Protect"))
                        {
                            message = T(lang, "Mimic.Reply.IDoNotHaveThatAbility");
                            break;
                        }

                        if (Group == null || Group != null && !Group.IsInTheGroup(player))
                        {
                            message = T(lang, "Mimic.Reply.MustBeInSameGroup");
                            break;
                        }

                        if (MimicBrain.SetProtect(player, out bool foundOurEffect))
                            message = T(lang, "Mimic.Reply.IWillProtectYou");
                        else
                        {
                            if (foundOurEffect)
                                message = T(lang, "Mimic.Reply.IWillNoLongerProtect");
                            else
                                message = T(lang, "Mimic.Reply.ICannotProtectYou");
                        }

                        break;
                    }

                case "Intercept":
                    {
                        if (!HasAbility("Intercept"))
                        {
                            message = T(lang, "Mimic.Reply.IDoNotHaveThatAbility");
                            break;
                        }

                        if (Group == null || Group != null && !Group.IsInTheGroup(player))
                        {
                            message = T(lang, "Mimic.Reply.MustBeInSameGroup");
                            break;
                        }

                        if (MimicBrain.SetIntercept(player, out bool foundOurEffect))
                            message = T(lang, "Mimic.Reply.IWillInterceptForYou");
                        else
                        {
                            if (foundOurEffect)
                                message = T(lang, "Mimic.Reply.IWillNoLongerIntercept");
                            else
                                message = T(lang, "Mimic.Reply.ICannotInterceptForYou");
                        }

                        break;
                    }

                case "Stay":
                    if (Group == null)
                    {
                        message = T(lang, "Mimic.Reply.NotInGroup");
                        break;
                    }
                    MimicBrain.FSM.SetCurrentState(eFSMStateType.IDLE);
                    message = T(lang, "Mimic.Reply.OrderStay");
                    break;

                case "Follow":
                    if (Group == null)
                    {
                        message = T(lang, "Mimic.Reply.NotInGroup");
                        break;
                    }
                    MimicBrain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                    message = T(lang, "Mimic.Reply.OrderFollow");
                    break;

                case "Camp":
                    if (Group?.MimicGroup == null)
                    {
                        message = T(lang, "Mimic.Reply.NotInGroup");
                        break;
                    }
                    // Use the player's position as the camp anchor when triggered via the menu.
                    Group.MimicGroup.SetCampPoint(new Point3D(player.X, player.Y, player.Z));
                    MimicBrain.FSM.SetCurrentState(eFSMStateType.CAMP);
                    message = T(lang, "Mimic.Reply.OrderCamp");
                    break;

                case "Reset":
                    MimicBrain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                    message = T(lang, "Mimic.Reply.OrderReset");
                    break;

                case "Pull":
                    if (Group?.MimicGroup == null)
                    {
                        message = T(lang, "Mimic.Reply.NotInGroup");
                        break;
                    }
                    // Force this bot to act as puller this tick. The brain's CheckPuller picks the target.
                    Group.MimicGroup.SetMainPuller(this);
                    MimicBrain.CheckPuller();
                    message = T(lang, "Mimic.Reply.OrderPull");
                    break;

                case "Roam":
                    MimicBrain.Roam = !MimicBrain.Roam;
                    message = T(lang, MimicBrain.Roam ? "Mimic.Reply.RoamOn" : "Mimic.Reply.RoamOff");
                    break;

                case "Help":
                    // Dispatch to /mhelp so the player gets the full command catalogue.
                    ScriptMgr.HandleCommand(player.Client, "/mhelp");
                    break;

                case "Spells":
                {
                    foreach (Spell spell in Spells)
                        message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";

                    break;
                }

                case "Inst Harmful":
                {
                    if (CanCastInstantHarmfulSpells)
                    {
                        foreach (Spell spell in InstantHarmfulSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "Harmful Spells":
                {
                    if (CanCastHarmfulSpells)
                    {
                        foreach (Spell spell in HarmfulSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "Inst Misc":
                {
                    if (CanCastInstantMiscSpells)
                    {
                        foreach (Spell spell in InstantMiscSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "Misc Spells":
                {
                    if (CanCastMiscSpells)
                    {
                        foreach (Spell spell in MiscSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "Inst Heal":
                {
                    if (CanCastInstantHealSpells)
                    {
                        foreach (Spell spell in InstantHealSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "Heal Spells":
                {
                    if (CanCastHealSpells)
                    {
                        foreach (Spell spell in HealSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "CC":
                {
                    if (CanCastCrowdControlSpells)
                    {
                        foreach (Spell spell in CrowdControlSpells)
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                    }

                    break;
                }

                case "Styles":
                {
                    foreach (Style style in Styles)
                        message += style.Name + " " + style.Level + "\n";

                    break;
                }

                case "Abilities":
                {
                    foreach (Ability ability in GetAllAbilities())
                        message += ability.Name + " " + ability.Level + "\n";

                    break;
                }

                case "Ability Effects":
                {
                    List<ECSGameAbilityEffect> abilityEffects = player.effectListComponent.GetAbilityEffects();

                    foreach (ECSGameAbilityEffect abilityEffect in abilityEffects)
                    {
                        if (abilityEffect != null)
                            message += abilityEffect?.Name + "\n";
                    }

                    if (message == string.Empty)
                        message = T(lang, "Mimic.Reply.NoActiveAbilityEffects");

                    break;
                }

                case "Spell Effects":
                {
                    foreach (ECSGameSpellEffect spellEffect in EffectListComponent.GetSpellEffects())
                    {
                        if (spellEffect != null)
                            message += spellEffect.Name + " " + spellEffect.EffectType + "\n";
                    }

                    if (message == string.Empty)
                        message = T(lang, "Mimic.Reply.NoActiveSpellEffects");

                    break;
                }

                case "Stats":
                {
                    message = "Level: " + Level + "\n" +

                   "Str: " + GetBaseStat(eStat.STR) + " (" + Strength + ")" +
                   GetModifiedStats(eStat.STR, eProperty.Strength) +

                   "Con: " + GetBaseStat(eStat.CON) + " (" + Constitution + ")" +
                    GetModifiedStats(eStat.CON, eProperty.Constitution) +

                   "Dex: " + GetBaseStat(eStat.DEX) + " (" + Dexterity + ")" +
                   GetModifiedStats(eStat.DEX, eProperty.Dexterity) +

                   "Qui: " + GetBaseStat(eStat.QUI) + " (" + Quickness + ")" +
                   GetModifiedStats(eStat.QUI, eProperty.Quickness);

                    switch (CharacterClass.ManaStat)
                    {
                        case eStat.UNDEFINED: break;

                        case eStat.INT: message += "Int: " + GetBaseStat(eStat.INT) + " (" + Intelligence + ")" + GetModifiedStats(eStat.INT, eProperty.Intelligence); break;
                        case eStat.PIE: message += "Pie: " + GetBaseStat(eStat.PIE) + " (" + Piety + ")" + GetModifiedStats(eStat.PIE, eProperty.Piety); break;
                        case eStat.EMP: message += "Emp: " + GetBaseStat(eStat.EMP) + " (" + Empathy + ")" + GetModifiedStats(eStat.EMP, eProperty.Empathy); break;
                        case eStat.CHR: message += "Cha: " + GetBaseStat(eStat.CHR) + " (" + Charisma + ")" + GetModifiedStats(eStat.CHR, eProperty.Charisma); break;
                    }

                    message += "Health: " + Health + "/" + MaxHealth + " (" + HealthPercent + "%)\n" +
                               "End: " + Endurance + "/" + MaxEndurance + " (" + EndurancePercent + "%)\n" +
                               "Power: " + Mana + "/" + MaxMana + " (" + ManaPercent + "%)\n" +
                               "AF: " + EffectiveOverallAF + "\n" +
                               "Conc: " + Concentration + "/" + MaxConcentration + "\n";
                    break;
                }

                case "Spec":
                {
                    var specs = GetSpecList();

                    foreach (Specialization spec in specs)
                        message += spec.Name + ": " + spec.Level + " \n";

                    break;
                }

                case "Hood": IsCloakHoodUp = !IsCloakHoodUp; break;

                case "Inventory":
                {
                    message = Money.GetString(GetCurrentMoney()) + "\n";

                    ICollection<DbInventoryItem> items = Inventory.GetItemRange(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);

                    if (items != null && items.Any())
                    {
                        int index = 1;
                        foreach (DbInventoryItem item in items)
                            message += "[" + index + "]" + ". " + item.Name + "\n";
                    }
                    else
                        message += T(lang, "Mimic.Reply.InventoryEmpty");

                    break;
                }

                case "Helm": RemoveItem(eInventorySlot.HeadArmor, player); break;
                case "Torso": RemoveItem(eInventorySlot.TorsoArmor, player); break;
                case "Legs": RemoveItem(eInventorySlot.LegsArmor, player); break;
                case "Arms": RemoveItem(eInventorySlot.ArmsArmor, player); break;
                case "Hands": RemoveItem(eInventorySlot.HandsArmor, player); break;
                case "Boots": RemoveItem(eInventorySlot.FeetArmor, player); break;
                case "RightHand": RemoveItem(eInventorySlot.RightHandWeapon, player); break;
                case "LeftHand": RemoveItem(eInventorySlot.LeftHandWeapon, player); break;
                case "TwoHand": RemoveItem(eInventorySlot.TwoHandWeapon, player); break;
                case "Ranged": RemoveItem(eInventorySlot.DistanceWeapon, player); break;

                case "Jewelry":
                {
                    for (int i = Slot.JEWELRY; i <= Slot.RIGHTRING; i++)
                    {
                        if (i is Slot.TORSO or Slot.LEGS or Slot.ARMS or Slot.FOREARMS or Slot.SHIELD)
                            continue;

                        RemoveItem((eInventorySlot)i, player);
                    }

                    break;
                }

                case "Delete":
                {
                    if (ControlledBrain != null)
                    {
                        if (ControlledBrain.Body != null)
                            ControlledBrain.Body.Delete();
                    }

                    Delete();

                    break;
                }

                default: break;
            }

            if (message != string.Empty)
                player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_PopupWindow);

            return true;
        }

        public override bool SayReceive(GameLiving source, string str)
        {
            if (source == null || str == null)
                return false;

            if (Group != null && Group.IsInTheGroup(source))
            {
                str = str.ToLower();

                switch (str)
                {
                    case "stay": Brain.FSM.SetCurrentState(eFSMStateType.IDLE); break;
                    case "follow": Brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER); break;
                    case "camp": Brain.FSM.SetCurrentState(eFSMStateType.CAMP); break;
                    case "reset": Brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP); break;
                }
            }

            return base.SayReceive(source, str);
        }

        private string GetModifiedStats(eStat stat, eProperty property)
        {
            string str = " Item: " + GetModifiedFromItems(property) +
                         " Buffs: " + GetModifiedFromBuffs(property) + "\n";

            return str;
        }

        public override bool ReceiveItem(GameLiving source, DbInventoryItem item)
        {
            if (source == null || item == null)
                return false;

            GamePlayer player = source as GamePlayer;

            // Only accept items from a player who is currently in the bot's
            // group. Without this check anyone could drop items on a bot's
            // tile — and a subsequent /mclear from the owner would silently
            // delete the corpse and its inventory, losing the gear. The
            // privilege bypass is kept for GMs so they can hand-feed gear
            // during testing.
            if (player == null
                || player.Client?.Account?.PrivLevel > 1)
            {
                // GM (or unknown caller): accept as before, drop straight
                // into the auto-equip branch below.
            }
            else if (Group == null || !Group.IsInTheGroup(source))
            {
                player?.Out.SendMessage($"{GetName(0, true)} won't accept items from outside the group.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            bool equipItem = false;

            foreach (eEquipmentItems equipmentItem in Enum.GetValues(typeof(eEquipmentItems)))
            {
                if (item.Item_Type == (int)equipmentItem)
                {
                    equipItem = true;
                    break;
                }
            }

            if (equipItem)
                return EquipRecievedItem(item, player);


            return base.ReceiveItem(source, item);
        }

        private bool EquipRecievedItem(DbInventoryItem itemToEquip, GamePlayer player)
        {
            if (!HasAbilityToUseItem(itemToEquip.Template))
                return false;

            eInventorySlot slotToEquip = (eInventorySlot)itemToEquip.Item_Type;

            if (slotToEquip == eInventorySlot.LeftHandWeapon && itemToEquip.Object_Type != (int)eObjectType.Shield)
            {
                if (Inventory.GetItem(eInventorySlot.RightHandWeapon) == null)
                    slotToEquip = eInventorySlot.RightHandWeapon;
                else if (!CharacterClass.CanUseLefthandedWeapon)
                    return false;
            }

            DbInventoryItem equippedItem = Inventory.GetItem(slotToEquip);

            if (equippedItem != null && !RemoveAndAddItemToEmptyPlayerInventorySlot(equippedItem, player))
                return false;

            player.Inventory.RemoveItem(itemToEquip);

            if (Inventory.AddItem(slotToEquip, itemToEquip))
            {
                RefreshItemBonuses();
                UpdateNPCEquipmentAppearance();

                return true;
            }
            else
                player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack), itemToEquip);

            return false;
        }

        private void RemoveItem(eInventorySlot inventorySlot, GamePlayer player)
        {
            DbInventoryItem itemToRemove = Inventory.GetItem(inventorySlot);

            if (itemToRemove != null)
                RemoveAndAddItemToEmptyPlayerInventorySlot(itemToRemove, player);
            else
                player.Out.SendMessage(T(player.Client?.Account?.Language, "Mimic.Reply.NothingEquippedInSlot"), eChatType.CT_Say, eChatLoc.CL_ChatWindow);
        }

        private bool RemoveAndAddItemToEmptyPlayerInventorySlot(DbInventoryItem itemToAdd, GamePlayer player)
        {
            eInventorySlot validSlot = player.Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);

            if (validSlot != eInventorySlot.Invalid)
            {
                Inventory.RemoveItem(itemToAdd);
                player.Inventory.AddItem(validSlot, itemToAdd);
            }
            else
            {
                player.Out.SendMessage(T(player.Client?.Account?.Language, "Mimic.Reply.NoRoomInBackpack"), eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                return false;
            }

            UpdateNPCEquipmentAppearance();

            return true;
        }

        #region Money

        /// true if this player want to receive loot with autosplit between members of group
        /// </summary>
        protected bool m_autoSplitLoot = true;

        /// <summary>
        /// Gets/sets the autosplit for loot
        /// </summary>
        public bool AutoSplitLoot
        {
            get { return m_autoSplitLoot; }
            set { m_autoSplitLoot = value; }
        }

        /// <summary>
        /// Player Mithril Amount
        /// </summary>
        public virtual int Mithril { get { return m_Mithril; } protected set { m_Mithril = value; } }
        protected int m_Mithril = 0;

        /// <summary>
        /// Player Platinum Amount
        /// </summary>
        public virtual int Platinum { get { return m_Platinum; } protected set { m_Platinum = value; } }
        protected int m_Platinum = 0;

        /// <summary>
        /// Player Gold Amount
        /// </summary>
        public virtual int Gold { get { return m_Gold; } protected set { m_Gold = value; } }
        protected int m_Gold = 0;

        /// <summary>
        /// Player Silver Amount
        /// </summary>
        public virtual int Silver { get { return m_Silver; } protected set { m_Silver = value; } }
        protected int m_Silver = 0;

        /// <summary>
        /// Player Copper Amount
        /// </summary>
        public virtual int Copper { get { return m_Copper; } protected set { m_Copper = value; } }
        protected int m_Copper = 0;

        /// <summary>
        /// Gets the money value this player owns
        /// </summary>
        /// <returns></returns>
        public virtual long GetCurrentMoney()
        {
            return Money.GetMoney(Mithril, Platinum, Gold, Silver, Copper);
        }

        public long ApplyGuildDues(long money)
        {
            Guild guild = Guild;

            if (guild == null || !guild.IsGuildDuesOn())
                return money;

            long moneyToGuild = money * guild.GetGuildDuesPercent() / 100;

            if (moneyToGuild <= 0 || !guild.AddToBank(moneyToGuild, false))
                return money;

            return money - moneyToGuild;
        }

        /// <summary>
        /// Adds money to this player
        /// </summary>
        /// <param name="money">money to add</param>
        public virtual void AddMoney(long money)
        {
            AddMoney(money, null, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        /// <summary>
        /// Adds money to this player
        /// </summary>
        /// <param name="money">money to add</param>
        /// <param name="messageFormat">null if no message or "text {0} text"</param>
        public virtual void AddMoney(long money, string messageFormat)
        {
            AddMoney(money, messageFormat, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        /// <summary>
        /// Adds money to this player
        /// </summary>
        /// <param name="money">money to add</param>
        /// <param name="messageFormat">null if no message or "text {0} text"</param>
        /// <param name="ct">message chat type</param>
        /// <param name="cl">message chat location</param>
        public virtual void AddMoney(long money, string messageFormat, eChatType ct, eChatLoc cl)
        {
            long newMoney = GetCurrentMoney() + money;

            Copper = Money.GetCopper(newMoney);
            Silver = Money.GetSilver(newMoney);
            Gold = Money.GetGold(newMoney);
            Platinum = Money.GetPlatinum(newMoney);
            Mithril = Money.GetMithril(newMoney);
        }

        /// <summary>
        /// Removes money from the player
        /// </summary>
        /// <param name="money">money value to subtract</param>
        /// <returns>true if successfull, false if player doesn't have enough money</returns>
        public virtual bool RemoveMoney(long money)
        {
            return RemoveMoney(money, null, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        /// <summary>
        /// Removes money from the player
        /// </summary>
        /// <param name="money">money value to subtract</param>
        /// <param name="messageFormat">null if no message or "text {0} text"</param>
        /// <returns>true if successfull, false if player doesn't have enough money</returns>
        public virtual bool RemoveMoney(long money, string messageFormat)
        {
            return RemoveMoney(money, messageFormat, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        /// <summary>
        /// Removes money from the player
        /// </summary>
        /// <param name="money">money value to subtract</param>
        /// <param name="messageFormat">null if no message or "text {0} text"</param>
        /// <param name="ct">message chat type</param>
        /// <param name="cl">message chat location</param>
        /// <returns>true if successfull, false if player doesn't have enough money</returns>
        public virtual bool RemoveMoney(long money, string messageFormat, eChatType ct, eChatLoc cl)
        {
            if (money > GetCurrentMoney())
                return false;

            long newMoney = GetCurrentMoney() - money;

            Mithril = Money.GetMithril(newMoney);
            Platinum = Money.GetPlatinum(newMoney);
            Gold = Money.GetGold(newMoney);
            Silver = Money.GetSilver(newMoney);
            Copper = Money.GetCopper(newMoney);

            return true;
        }

        #endregion Money

        public object GameStaticItemOwnerComparand => AccountName;

        public TryPickUpResult TryAutoPickUpMoney(GameMoney money)
        {
            return TryPickUpMoney(this, money);
        }

        public TryPickUpResult TryAutoPickUpItem(WorldInventoryItem inventoryItem)
        {
            return TryPickUpItem(this, inventoryItem);
        }

        // Interface bridge: upstream changed IGameStaticItemOwner.TryPickUpMoney
        // and TryPickUpItem to take a concrete GamePlayer rather than the
        // IGamePlayer they used to take. Mimics never have a GamePlayer
        // source (the engine's group/leader pickup paths bypass this entry),
        // so the public interface impls return DoesNotWant. The legacy
        // IGamePlayer overloads below are still used by our own auto-pickup
        // helpers when a mimic is acting on its own loot.
        public TryPickUpResult TryPickUpMoney(GamePlayer source, GameMoney money)
            => TryPickUpResult.DoesNotWant;
        public TryPickUpResult TryPickUpItem(GamePlayer source, WorldInventoryItem item)
            => TryPickUpResult.DoesNotWant;

        public TryPickUpResult TryPickUpMoney(IGamePlayer source, GameMoney money)
        {
            money.AssertLockAcquisition();

            if (this != source)
            {
                if (log.IsErrorEnabled)
                    log.Error($"The passed down {nameof(source)} isn't equal to 'this'. Money pick up aborted. ({nameof(source)}: {source}) (this: {this})");

                return TryPickUpResult.DoesNotWant;
            }

            long moneyToPlayer = ApplyGuildDues(money.Value);

            if (moneyToPlayer > 0)
            {
                AddMoney(moneyToPlayer, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.PickupObject.YouPickUp", Money.GetString(moneyToPlayer)));
                InventoryLogging.LogInventoryAction("(ground)", this, eInventoryActionType.Loot, moneyToPlayer);
            }

            money.RemoveFromWorld();
            return TryPickUpResult.Success;
        }

        public TryPickUpResult TryPickUpItem(IGamePlayer source, WorldInventoryItem item)
        {
            item.AssertLockAcquisition();

            if (this != source)
            {
                if (log.IsErrorEnabled)
                    log.Error($"The passed down {nameof(source)} isn't equal to 'this'. Item pick up aborted. ({nameof(source)}: {source}) (this: {this})");

                return TryPickUpResult.DoesNotWant;
            }

            if (!GiveItem(this, item.Item))
            {
                Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.PickupObject.BackpackFull"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return TryPickUpResult.Blocked;
            }

            Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.PickupObject.YouGet", item.Item.GetName(1, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            Message.SystemToOthers(this, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.PickupObject.GroupMemberPicksUp", Name, item.Item.GetName(1, false)), eChatType.CT_System);
            InventoryLogging.LogInventoryAction("(ground)", this, eInventoryActionType.Loot, item.Item.Template, item.Item.IsStackable ? item.Item.Count : 1);
            item.RemoveFromWorld();
            return TryPickUpResult.Success;

            static bool GiveItem(IGamePlayer player, DbInventoryItem item)
            {
                if (item.IsStackable)
                    return player.Inventory.AddTemplate(item, item.Count, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);

                return player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item);
            }
        }

        /// <summary>
        /// Checks to see if an object is viewable from the players perspective
        /// </summary>
        /// <param name="obj">The Object to be seen</param>
        /// <returns>True/False</returns>
        public bool CanSeeObject(GameObject obj)
        {
            return IsWithinRadius(obj, WorldMgr.VISIBILITY_DISTANCE);
        }

        public void SpendSpecPoints(byte level, byte previousLevel)
        {
            MimicSpec.SpecLines = MimicSpec.SpecLines.OrderByDescending(ratio => ratio.levelRatio).ToList();

            int leftOverSpecPoints = m_leftOverSpecPoints;
            bool spentPoints = true;
            bool spendLeftOverPoints = false;
            bool mimicCreation = level - previousLevel > 1;

            byte index = (byte)(previousLevel + 1);

            // 40+ half-level
            if (level == previousLevel)
                index = level;

            // For each level, get the points available. In the while loop, spend points according to the ratio in SpecLines until spentPoints is false.
            // Then set spendLeftOverPoints true and spend any left over points until spentPoints is false again. Exit the while loop, increase level, and repeat.
            for (byte i = index; i <= Level; i++)
            {
                spentPoints = true;
                spendLeftOverPoints = false;

                int totalSpecPointsThisLevel = GetSpecPointsForLevel(i, mimicCreation) + leftOverSpecPoints;

                while (spentPoints)
                {
                    spentPoints = false;

                    foreach (SpecLine specLine in MimicSpec.SpecLines)
                    {
                        // Indicates a dump stat for lvl 50
                        if (specLine.levelRatio <= 0 && Level < 50)
                            continue;

                        Specialization spec = GetSpecializationByName(specLine.Spec);

                        if (spec != null)
                        {
                            if (spec.Level < specLine.SpecCap && spec.Level < i)
                            {
                                int specRatio = (int)(i * specLine.levelRatio);

                                if (!spendLeftOverPoints && spec.Level >= specRatio)
                                    continue;

                                int totalCost = spec.Level + 1;

                                if (totalSpecPointsThisLevel >= totalCost)
                                {
                                    totalSpecPointsThisLevel -= totalCost;
                                    spec.Level++;
                                    spentPoints = true;
                                }
                            }
                        }
                    }

                    if (!spentPoints && !spendLeftOverPoints)
                    {
                        spendLeftOverPoints = true;
                        spentPoints = true;
                    }
                }

                m_leftOverSpecPoints = leftOverSpecPoints = totalSpecPointsThisLevel;
            }
        }

        private int GetSpecPointsForLevel(int level, bool mimicCreation)
        {
            //    // calc spec points player have (autotrain is not anymore processed here - 1.87 livelike)
            //    int usedpoints = 0;
            //        foreach (Specialization spec in GetSpecList().Where(e => e.Trainable))
            //        {
            //            usedpoints += (spec.Level* (spec.Level + 1) - 2) / 2;
            //            usedpoints -= GetAutoTrainPoints(spec, 0);
            //}

            if (IsLevelSecondStage)
                return CharacterClass.SpecPointsMultiplier * level / 20;

            int specpoints = 0;

            if (mimicCreation)
                if (level > 40)
                    specpoints += CharacterClass.SpecPointsMultiplier * (level - 1) / 20;

            if (level > 5)
                specpoints += CharacterClass.SpecPointsMultiplier * level / 10;
            else if (level >= 2)
                specpoints = level;

            return specpoints;
        }

        #region Spells

        /// <summary>
        /// Monotonic counter incremented every time the bot's spellbook is
        /// rebuilt (level-up, spec change, item bonus refresh, etc.). Caches
        /// keyed off the spellbook (e.g. <see cref="MimicBrain.SelectPullSpell"/>)
        /// stamp the revision they computed against and re-evaluate when it
        /// changes — no more relying on every mutation site remembering to
        /// call InvalidateX().
        /// </summary>
        public int SpellbookRevision { get; private set; }

        public void SetSpells()
        {
            List<Spell> spells = new List<Spell>();

            List<(Skill, Skill)> usableSkills = GetAllUsableSkills();

            for (int i = 0; i < usableSkills.Count; i++)
            {
                Skill skill = usableSkills[i].Item1;

                if (skill is Spell)
                    spells.Add((Spell)skill);
            }

            if (spells.Count > 0)
                Spells = spells;
        }

        public void SetCasterSpells()
        {
            IList<Specialization> specs = GetSpecList();
            List<Spell> spells = new List<Spell>();

            foreach (Specialization spec in specs)
            {
                var dict = GetAllUsableListSpells();

                if (dict != null && dict.Count > 0)
                {
                    foreach (var tuple in dict)
                    {
                        if (tuple.Item2.Count > 0)
                        {
                            foreach (Skill skill in tuple.Item2)
                            {
                                if (skill is Spell)
                                {
                                    Spell spell = skill as Spell;

                                    if (!spells.Contains(spell))
                                        spells.Add(spell);
                                }
                            }
                        }
                    }
                }
            }

            List<Spell> highestSpellLevels = GetHighestLevelSpells(spells);

            if (highestSpellLevels.Count > 0)
                Spells = highestSpellLevels;
        }

        public List<Spell> GetHighestLevelSpells(List<Spell> spells)
        {
            spells = spells.OrderByDescending(spell => spell.Level).ToList();

            List<Spell> highestLevelSpells = new List<Spell>();

            foreach (Spell currentSpell in spells)
            {
                Spell existingSpell = highestLevelSpells.FirstOrDefault(x => AreSpellsEqual(x, currentSpell));

                if (existingSpell != null)
                {
                    if (existingSpell.Level < currentSpell.Level)
                        highestLevelSpells.Remove(existingSpell);
                    else
                        continue;
                }

                highestLevelSpells.Add(currentSpell);
            }

            return highestLevelSpells;
        }

        private bool AreSpellsEqual(Spell spellOne, Spell spellTwo)
        {
            bool equalSpells = spellOne.DamageType == spellTwo.DamageType &&
                               spellOne.SpellType == spellTwo.SpellType &&
                               spellOne.Frequency == spellTwo.Frequency &&
                               spellOne.CastTime == spellTwo.CastTime &&
                               spellOne.Target == spellTwo.Target &&
                               spellOne.Group == spellTwo.Group &&
                               spellOne.IsPBAoE == spellTwo.IsPBAoE &&
                            ((!spellOne.IsAoE && !spellTwo.IsAoE) || (spellOne.IsAoE && spellTwo.IsAoE));

            return equalSpells ? true : HandleSpecificSpells(spellOne, spellTwo);
        }

        private bool HandleSpecificSpells(Spell spellOne, Spell spellTwo)
        {
            // Valewalker proc
            if (spellOne.ID == 11236 && (spellTwo.ID >= 11232 && spellTwo.ID <= 11235) ||
                spellTwo.ID == 11236 && (spellOne.ID >= 11232 && spellOne.ID <= 11235))
                return true;

            return false;
        }

        private List<Spell> m_spells = [];

        /// <summary>
		/// property of spell array of NPC
		/// </summary>
		public override List<Spell> Spells
        {
            get => m_spells;
            set
            {
                if (value == null || value.Count < 1)
                {
                    m_spells.Clear();
                    InstantHarmfulSpells = null;
                    HarmfulSpells = null;
                    InstantHealSpells = null;
                    HealSpells = null;
                    BoltSpells = null;
                    InstantCrowdControlSpells = null;
                    CrowdControlSpells = null;
                    InstantMiscSpells = null;
                    MiscSpells = null;
                }
                else
                {
                    // Voluntary copy. This isn't ideal and needs to be changed eventually.
                    m_spells = value.ToList();
                    SortSpells();
                }
            }
        }

        /// <summary>Calculate actual spell power cost, while minimizing calls to MaxMana property calculator</summary>
        /// <param name="spell"></param>
        /// <returns>Actual power cost</returns>
        public int PowerCost(Spell spell)
        {
            // Copied from SpellHandler, leaving code for future classes commented out

            int powerCost = spell.Power;

            // Warlock.
            /* GameSpellEffect effect = SpellHandler.FindEffectOnTarget(m_caster, "Powerless");
			if (effect != null && !m_spell.IsPrimary)
				return 0;*/

            /* Valkyrie
            // 1.108 - Valhalla's Blessing now has a 75% chance to not use power.
            if (m_caster.EffectList.GetOfType<ValhallasBlessingEffect>() != null && Util.Chance(75))
                return 0;
            */

            /* Animist
            // Patch 1.108 increases the chance to not use power to 50%.
            if (m_caster.EffectList.GetOfType<FungalUnionEffect>() != null && Util.Chance(50))
                return 0;
            */

            /* ToA
            // Arcane Syphon.
            int syphon = Caster.GetModified(eProperty.ArcaneSyphon);
            if (syphon > 0 && Util.Chance(syphon))
                return 0;
            */

            // Percent of max power if less than zero.
            if (powerCost < 0)
            {
                if (CharacterClass.ManaStat is not eStat.UNDEFINED)
                    powerCost = (int)(CalculateMaxMana(Level, GetBaseStat(CharacterClass.ManaStat)) * powerCost * -0.01);
                else
                    powerCost = (int)(MaxMana * powerCost * -0.01);
            }

            /* Mimics currently use the mob spell line, which SpecToFocus() doesn't recognize
            if (playerCaster != null && playerCaster.CharacterClass.FocusCaster)
            {
                eProperty focusProp = SkillBase.SpecToFocus(SpellLine.Spec);

                if (focusProp is not eProperty.Undefined)
                {
                    double focusBonus = Caster.GetModified(focusProp) * 0.4;

                    if (Spell.Level > 0)
                        focusBonus /= Spell.Level;

                    if (focusBonus > 0.4)
                        focusBonus = 0.4;
                    else if (focusBonus < 0)
                        focusBonus = 0;

                    focusBonus *= Math.Min(1, playerCaster.GetModifiedSpecLevel(SpellLine.Spec) / (double)Spell.Level);
                    powerCost *= 1.2 - focusBonus; // Between 120% and 80% of base power cost.
                }
            } */

            /* Healing doesn't use quickcast, and it's not really worth it just for mentalists
            // Doubled power usage if using QuickCast.
            if (EffectListService.GetAbilityEffectOnTarget(this, eEffect.QuickCast) != null && spell.CastTime > 0)
                powerCost *= 2;
            */

            return powerCost;
        }

        ///<summary>Calculate heal amount, accounting for percentage based heals</summary>
        public static double HealAmount(Spell spell, GameLiving target)
        {
            // HealAmount is called from the heal cycle with members of the
            // bot's HealBig/HealEfficient slots, which can be null when the
            // caster has not specced into a corresponding spell. Treat
            // missing spells as zero heal so the caller's > comparison
            // falls through to whichever heal IS available.
            if (spell == null || target == null)
                return 0;

            return spell.Value >= 0
                ? spell.Value
                : target.MaxHealth * spell.Value * -0.01d;
        }

        /// <summary>
        /// Direct (non-HoT) healing spell types the heal cycle can size with
        /// HealAmount and treat like a plain Heal. Without this, Spread /
        /// PBAoE / Combat / Omni / Merc heals were dropped into the HealSpells
        /// list but never assigned to a named heal slot, so the heal cycle
        /// never cast them.
        /// </summary>
        private static bool IsDirectHealType(eSpellType type)
        {
            return type is eSpellType.Heal
                or eSpellType.SpreadHeal
                or eSpellType.PBAoEHeal
                or eSpellType.CombatHeal
                or eSpellType.OmniHeal
                or eSpellType.MercHeal;
        }

        public Spell HealBig { get; protected set; } = null;
        public Spell HealEfficient { get; protected set; } = null;
        public Spell HealGroup { get; protected set; } = null;
        public Spell HealInstant { get; protected set; } = null;
        public Spell HealInstantGroup { get; protected set; } = null;
        public Spell HealOverTime { get; protected set; } = null;
        public Spell HealOverTimeGroup { get; protected set; } = null;
        public Spell HealOverTimeInstant { get; protected set; } = null;
        public Spell HealOverTimeInstantGroup { get; protected set; } = null;
        public Spell CureMezz { get; protected set; } = null;
        public Spell CureDisease { get; protected set; } = null;
        public Spell CureDiseaseGroup { get; protected set; } = null;
        public Spell CurePoison { get; protected set; } = null;
        public Spell CurePoisonGroup { get; protected set; } = null;

        public override void SortSpells()
        {
            if (Spells.Count < 1)
                return;

            // Bump the spellbook revision so any cached pick on the brain
            // (pull spell, AoE pull spell, future heal-cycle caches) auto-
            // re-evaluates on next access. InvalidatePullSpellCache is
            // still called for back-compat with code paths that ran before
            // the revision-based cache, but new caches should compare
            // SpellbookRevision rather than expect explicit invalidation.
            SpellbookRevision++;
            MimicBrain?.InvalidatePullSpellCache();

            // Clear the lists
            InstantHarmfulSpells?.Clear();
            HarmfulSpells?.Clear();
            InstantHealSpells?.Clear();
            HealSpells?.Clear();
            InstantCrowdControlSpells?.Clear();
            CrowdControlSpells?.Clear();
            BoltSpells?.Clear();
            InstantMiscSpells?.Clear();
            MiscSpells?.Clear();

            HealBig = null;
            HealEfficient = null;
            HealGroup = null;
            HealInstant = null;
            HealInstantGroup = null;
            HealOverTime = null;
            HealOverTimeGroup = null;
            HealOverTimeInstant = null;
            HealOverTimeInstantGroup = null;
            CureMezz = null;
            CureDisease = null;
            CureDiseaseGroup = null;
            CurePoison = null;
            CurePoisonGroup = null;

            // Sort spells into lists
            foreach (Spell spell in m_spells)
            {
                if (spell == null)
                    continue;

                if (spell.SpellType == eSpellType.Bolt)
                {
                    if (BoltSpells == null)
                        BoltSpells = new List<Spell>(1);

                    BoltSpells.Add(spell);
                }
                else if (spell.SpellType == eSpellType.Mesmerize ||
                        (spell.SpellType == eSpellType.SpeedDecrease && spell.Value >= 99))
                {
                    if (spell.IsInstantCast)
                    {
                        // Instant mez / instant root previously fell through
                        // into the regular CrowdControlSpells list, which the
                        // CC selection logic only iterates while not casting.
                        // Putting them in InstantCrowdControlSpells lets the
                        // bot fire them as emergency interrupt-mez even mid
                        // cast — which is what a real Sorcerer/Minstrel does.
                        if (InstantCrowdControlSpells == null)
                            InstantCrowdControlSpells = new List<Spell>(1);
                        InstantCrowdControlSpells.Add(spell);
                    }
                    else
                    {
                        if (CrowdControlSpells == null)
                            CrowdControlSpells = new List<Spell>(1);
                        CrowdControlSpells.Add(spell);
                    }
                }
                else if (spell.IsHarmful)
                {
                    if (spell.IsInstantCast)
                    {
                        if (InstantHarmfulSpells == null)
                            InstantHarmfulSpells = new List<Spell>(1);

                        InstantHarmfulSpells.Add(spell);
                    }
                    else
                    {
                        if (HarmfulSpells == null)
                            HarmfulSpells = new List<Spell>(1);

                        HarmfulSpells.Add(spell);
                    }
                }
                else if (spell.IsHealing && !spell.IsPulsing && !spell.IsConcentration && spell.Target != eSpellTarget.PET)
                {
                    // Pet-target heals (Animist HoT for shroom, Cabalist
                    // commander heal, Druid pet heal) fall through to the
                    // misc bucket below so they remain castable via the
                    // generic check-defensive path. The previous `continue`
                    // dropped them on the floor entirely — pet-healing
                    // archetypes could never patch up their own pet.
                    double valueNew = HealAmount(spell, this);

                    if (spell.SpellType == eSpellType.CureMezz)
                    {
                        // Prefer the longest-lasting cure mezz when the bot
                        // has several variants (some classes get an instant
                        // + a cast version). Previously this overwrote on
                        // every iteration, so the picked CureMezz depended
                        // on the m_spells iteration order.
                        if (CureMezz == null || spell.Duration > CureMezz.Duration)
                            CureMezz = spell;
                    }
                    else if (spell.SpellType == eSpellType.CureDisease)
                    {
                        if ((spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            && (CureDiseaseGroup == null || spell.Duration > CureDiseaseGroup.Duration))
                            CureDiseaseGroup = spell;
                        else if (spell.Target == eSpellTarget.REALM && (CureDisease == null || spell.Duration > CureDisease.Duration))
                            CureDisease = spell;
                    }
                    else if (spell.SpellType == eSpellType.CurePoison)
                    {
                        if ((spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            && (CurePoisonGroup == null || spell.Duration > CurePoisonGroup.Duration))
                            CurePoisonGroup = spell;
                        else if (spell.Target == eSpellTarget.REALM && (CurePoison == null || spell.Duration > CurePoison.Duration))
                            CurePoison = spell;
                    }
                    else if (spell.IsInstantCast)
                    {
                        if (InstantHealSpells == null)
                            InstantHealSpells = new List<Spell>(1);

                        InstantHealSpells.Add(spell);

                        if (spell.SpellType == eSpellType.HealOverTime || spell.SpellType == eSpellType.HealthRegenBuff)
                        {
                            // Valks have both regens and hots, so we need to compare actual healing over time in combat
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealOverTimeInstantGroup == null)
                                    HealOverTimeInstantGroup = spell;
                                else
                                {
                                    double perSecondOld = HealOverTimeInstantGroup.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTimeInstantGroup, this) / HealOverTimeInstantGroup.Frequency
                                        : HealAmount(HealOverTimeInstantGroup, this) / GetHealthRegenerationInterval() / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / GetHealthRegenerationInterval() / 2;
                                    
                                    if (perSecondNew > perSecondOld)
                                        HealOverTimeInstantGroup = spell;
                                }
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealOverTimeInstant == null)
                                    HealOverTimeInstant = spell;
                                else
                                {
                                    double perSecondOld = HealOverTimeInstant.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTimeInstant, this) / HealOverTimeInstant.Frequency
                                        : HealAmount(HealOverTimeInstant, this) / GetHealthRegenerationInterval() / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / GetHealthRegenerationInterval() / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTimeInstant = spell;
                                }
                            }
                        }
                        else if (IsDirectHealType(spell.SpellType))
                        {
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealInstantGroup == null || valueNew > HealAmount(HealInstantGroup, this))
                                    HealInstantGroup = spell;
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealInstant == null || valueNew > HealAmount(HealInstant, this))
                                    HealInstant = spell;
                            }
                        }
                    }
                    else
                    {
                        if (HealSpells == null)
                            HealSpells = new List<Spell>(1);

                        HealSpells.Add(spell);

                        if (spell.SpellType == eSpellType.HealOverTime || spell.SpellType == eSpellType.HealthRegenBuff)
                        {
                            // Valks have both regens and hots, so we need to compare actual healing over time in combat
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealOverTimeGroup == null)
                                    HealOverTimeGroup = spell;
                                else
                                {
                                    double perSecondOld = HealOverTimeGroup.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTimeGroup, this) / HealOverTimeGroup.Frequency
                                        : HealAmount(HealOverTimeGroup, this) / GetHealthRegenerationInterval() / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / GetHealthRegenerationInterval() / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTimeGroup = spell;
                                }
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealOverTime == null)
                                    HealOverTime = spell;
                                else
                                {
                                    double perSecondOld = HealOverTime.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTime, this) / HealOverTime.Frequency
                                        : HealAmount(HealOverTime, this) / GetHealthRegenerationInterval() / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / GetHealthRegenerationInterval() / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTime = spell;
                                }
                            }
                        }
                        else if (IsDirectHealType(spell.SpellType))
                        {
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealGroup == null || valueNew / spell.CastTime > HealAmount(HealGroup, this) / HealGroup.CastTime)
                                    HealGroup = spell;
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealEfficient == null)
                                    HealEfficient = spell;
                                else
                                {
                                    double perSecondNew = valueNew / spell.CastTime;
                                    double perSecondEff = HealAmount(HealEfficient, this) / HealEfficient.CastTime;
                                    double perSecondBig = HealBig == null ? 0.0 : HealAmount(HealBig, this) / HealBig.CastTime;

                                    // Add a small margin for new spell efficiency, so we use bigger spec heals when they're almost as efficient.
                                    // Guard against zero-cost spells (custom "free" heals or
                                    // chant procs) — div-by-zero produced Infinity which made
                                    // the comparator behave non-deterministically and could
                                    // pick a worse heal over the real efficient one.
                                    double powerNew = PowerCost(spell);
                                    double powerOld = PowerCost(HealEfficient);
                                    double effNew = (powerNew > 0 ? valueNew / powerNew : valueNew) + 0.25;
                                    double effOld = powerOld > 0 ? HealAmount(HealEfficient, this) / powerOld : HealAmount(HealEfficient, this);

                                    if (effNew > effOld)
                                    {
                                        if (perSecondEff > perSecondNew && perSecondEff > perSecondBig)
                                            HealBig = HealEfficient;

                                        HealEfficient = spell;
                                    }
                                    else if (perSecondNew > perSecondEff && perSecondNew > perSecondBig)
                                        HealBig = spell;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Skald speed is instant, but instant misc isn't checked outside combat. Add an exception.
                    if (spell.IsInstantCast && spell.SpellType != eSpellType.SpeedEnhancement)
                    {
                        if (InstantMiscSpells == null)
                            InstantMiscSpells = new List<Spell>(1);

                        InstantMiscSpells.Add(spell);
                    }
                    else
                    {
                        if (MiscSpells == null)
                            MiscSpells = new List<Spell>(1);

                        MiscSpells.Add(spell);
                    }
                }
            }

            // Helps BD's cast higher level minions before fitting in lower level ones
            MiscSpells?.Sort((a, b) =>
            {
                if (a.SpellType == eSpellType.SummonMinion && b.SpellType == eSpellType.SummonMinion)
                {
                    byte levelA = (byte)Math.Min(Level * a.Damage * -0.01, a.Value);
                    byte levelB = (byte)Math.Min(Level * b.Damage * -0.01, b.Value);
                    return levelB.CompareTo(levelA);
                }

                return b.Level.CompareTo(a.Level);
            });
        }

        public SpellLine GetSpellLineForSpell(Spell spell)
        {
            foreach (SpellLine line in GetSpellLines())
            {
                if (SkillBase.GetSpellList(line.KeyName).Contains(spell))
                    return line;
            }

            if (log.IsWarnEnabled)
                log.Warn($"{Name} could not find spell line for spell {spell.Name} ({spell.ID}), falling back to mob spell line.");

            return SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells);
        }

        #endregion

        public override void SortStyles()
        {
            StylesChain?.Clear();
            StylesDefensive?.Clear();
            StylesBack?.Clear();
            StylesSide?.Clear();
            StylesFront?.Clear();
            StylesAnytime?.Clear();
            StylesTaunt?.Clear();
            StylesDetaunt?.Clear();
            StylesShield?.Clear();

            if (Styles == null)
                return;

            foreach (Style s in Styles)
            {
                if (s == null)
                    continue;

                if (s.WeaponTypeRequirement != (int)eObjectType.Shield ||
                    s.WeaponTypeRequirement == (int)eObjectType.Shield && s.OpeningRequirementType == Style.eOpening.Defensive)
                {
                    switch (s.OpeningRequirementType)
                    {
                        case Style.eOpening.Defensive:
                        if (StylesDefensive == null)
                            StylesDefensive = new List<Style>(1);

                        StylesDefensive.Add(s);
                        break;

                        case Style.eOpening.Positional:
                        switch ((Style.eOpeningPosition)s.OpeningRequirementValue)
                        {
                            case Style.eOpeningPosition.Back:
                            if (StylesBack == null)
                                StylesBack = new List<Style>(1);

                            StylesBack.Add(s);
                            break;

                            case Style.eOpeningPosition.Side:
                            if (StylesSide == null)
                                StylesSide = new List<Style>(1);

                            StylesSide.Add(s);
                            break;

                            case Style.eOpeningPosition.Front:
                            if (StylesFront == null)
                                StylesFront = new List<Style>(1);

                            StylesFront.Add(s);
                            break;

                            default:
                            log.Warn($"MimicNPC.SortStyles(): Invalid OpeningRequirementValue for positional style {s.Name}, ID {s.ID}, ClassId {s.ClassID}");
                            break;
                        }
                        break;

                        default:
                        if (s.OpeningRequirementValue > 0)
                        {
                            if (StylesChain == null)
                                StylesChain = new List<Style>(1);

                            StylesChain.Add(s);
                        }
                        else
                        {
                            bool added = false;

                            if (s.Procs.Count > 0)
                            {
                                foreach (StyleProcInfo proc in s.Procs)
                                {
                                    if (proc.Spell.SpellType == eSpellType.StyleTaunt)
                                    {
                                        if (proc.Spell.ID == 20000)
                                        {
                                            if (StylesTaunt == null)
                                                StylesTaunt = new List<Style>(1);

                                            StylesTaunt.Add(s);
                                            added = true;
                                        }
                                        else if (proc.Spell.ID == 20001)
                                        {
                                            if (StylesDetaunt == null)
                                                StylesDetaunt = new List<Style>(1);

                                            StylesDetaunt.Add(s);
                                            added = true;
                                        }
                                    }
                                }
                            }

                            if (!added)
                            {
                                if (StylesAnytime == null)
                                    StylesAnytime = new List<Style>(1);

                                StylesAnytime.Add(s);
                            }
                        }
                        break;
                    }
                }
                else
                {
                    if (StylesShield == null)
                        StylesShield = new List<Style>(1);

                    StylesShield.Add(s);
                }
            }
        }

        public override void DisableSkill(Skill skill, int duration)
        {
            base.DisableSkill(skill, duration);
        }

        public void SetLevel(byte level)
        {
            Level = level;
            //Experience = ExperienceForNextLevel - 1;
            Experience = ExperienceForCurrentLevel;

            if (level >= 20 && level < 25)
                RealmLevel = Util.Random(1, 10);
            else if (level > 24 && level < 30)
                RealmLevel = Util.Random(1, 15);
            else if (level > 29 && level < 35)
                RealmLevel = Util.Random(1, 25);
            else if (level > 34 && level < 40)
                RealmLevel = Util.Random(1, 35);
            else if (level > 39 && level < 50)
                RealmLevel = Util.Random(1, 45);
            else if (level == 50)
                RealmLevel = Util.Random(1, 90);

            RealmPoints = REALMPOINTS_FOR_LEVEL[RealmLevel];
        }

        public void SetRaceAndName()
        {
            // EligibleRaces must contain at least one entry. Several extension
            // classes (Vampiir, Valewalker, Mauler*, Bainshee, Heretic,
            // Warlock, Valkyrie) historically shipped with the list commented
            // out — instantiating any of those threw IndexOutOfRangeException
            // here, which the /mcreate handler silently swallowed. We now
            // throw a clear message so the operator knows exactly which class
            // is broken instead of seeing "Impossible de créer le mimic".
            if (CharacterClass.EligibleRaces == null || CharacterClass.EligibleRaces.Count == 0)
            {
                throw new InvalidOperationException(
                    $"CharacterClass.EligibleRaces is empty for {CharacterClass.Name} (ID {CharacterClass.ID}). " +
                    "Populate the EligibleRaces override in the GameServer/playerclasses/.../Class*.cs file.");
            }

            PlayerRace playerRace = CharacterClass.EligibleRaces[Util.Random(CharacterClass.EligibleRaces.Count - 1)];

            Gender = Util.Random(1) > 0 ? eGender.Female : eGender.Male;
            Race = (short)playerRace.ID;
            Model = (ushort)playerRace.GetModel(Gender);
            Size = (byte)Util.Random(45, 60);

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            // STARTING_STATS_DICT must contain an entry for this race; if it
            // doesn't (custom/extension race not seeded), fall back to a
            // neutral baseline so the bot still spawns instead of crashing.
            if (!GlobalConstants.STARTING_STATS_DICT.TryGetValue((eRace)Race, out Dictionary<eStat, int> statDict)
                || statDict == null)
            {
                statDict = new Dictionary<eStat, int>
                {
                    { eStat.STR, 60 }, { eStat.CON, 60 }, { eStat.DEX, 60 }, { eStat.QUI, 60 },
                    { eStat.INT, 60 }, { eStat.PIE, 60 }, { eStat.EMP, 60 }, { eStat.CHR, 60 },
                };

                if (log.IsWarnEnabled)
                    log.WarnFormat("MimicNPC.SetRaceAndName: no STARTING_STATS_DICT entry for race {0} ({1}); using neutral 60/60.",
                        (eRace)Race, playerRace.ID);
            }

            ChangeBaseStat(eStat.STR, (short)statDict[eStat.STR]);
            ChangeBaseStat(eStat.CON, (short)statDict[eStat.CON]);
            ChangeBaseStat(eStat.DEX, (short)statDict[eStat.DEX]);
            ChangeBaseStat(eStat.QUI, (short)statDict[eStat.QUI]);
            ChangeBaseStat(eStat.INT, (short)statDict[eStat.INT]);
            ChangeBaseStat(eStat.PIE, (short)statDict[eStat.PIE]);
            ChangeBaseStat(eStat.EMP, (short)statDict[eStat.EMP]);
            ChangeBaseStat(eStat.CHR, (short)statDict[eStat.CHR]);
            ChangeBaseStat(CharacterClass.PrimaryStat, 10);
            ChangeBaseStat(CharacterClass.SecondaryStat, 10);
            ChangeBaseStat(CharacterClass.TertiaryStat, 10);

            foreach (KeyValuePair<eRealm, List<eCharacterClass>> keyValuePair in GlobalConstants.STARTING_CLASSES_DICT)
            {
                if (keyValuePair.Value.Contains((eCharacterClass)CharacterClass.ID))
                {
                    Realm = keyValuePair.Key;
                    break;
                }
            }

            // Fallback realm: if the class isn't listed in STARTING_CLASSES_DICT
            // (extension class with the registration commented out), derive
            // the realm from the chosen race so the bot at least has a name
            // and isn't stuck as eRealm.None (which breaks group invites,
            // aggro, AI, and the right-click "[invite]" widget).
            if (Realm == eRealm.None)
            {
                Realm = playerRace.Realm;

                if (log.IsWarnEnabled)
                    log.WarnFormat("MimicNPC.SetRaceAndName: class {0} (ID {1}) not found in STARTING_CLASSES_DICT; derived Realm={2} from race {3}.",
                        CharacterClass.Name, CharacterClass.ID, Realm, playerRace.ID);
            }

            Name = MimicNames.GetName(Gender, Realm);
        }

        public string GetPronoun(GameClient Client, int form, bool capitalize)
        {
            if (Gender == eGender.Male)
                switch (form)
                {
                    default:
                        return Capitalize(capitalize, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pronoun.Male.Subjective"));
                    case 1:
                        return Capitalize(capitalize, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pronoun.Male.Possessive"));
                    case 2:
                        return Capitalize(capitalize, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pronoun.Male.Objective"));
                }
            else
                switch (form)
                {
                    default:
                        return Capitalize(capitalize, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pronoun.Female.Subjective"));
                    case 1:
                        return Capitalize(capitalize, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pronoun.Female.Possessive"));
                    case 2:
                        return Capitalize(capitalize, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pronoun.Female.Objective"));
                }
        }

        public override void StartAttack(GameObject target)
        {
            base.StartAttack(target);
        }

        public override bool AddToWorld()
        {
            if (!(base.AddToWorld()))
                return false;

            UpdateNPCEquipmentAppearance();
            UpdateEncumbrance(true);

            return true;
        }

        // Set by Delete() so the GroupEvent.MemberDisbanded handler in
        // MimicManager can tell "this bot was kicked from the group by the
        // player" (false) from "this bot is being deleted, and Delete is
        // about to trigger MemberDisbanded as a side-effect of RemoveMember" (true).
        // Without this guard the disband handler would re-enter Delete and recurse.
        internal bool _beingDeleted;

        // Campfire visual the bot can deploy while regening in /mcamp set mode.
        // Held here so MimicState_Camp can spawn/remove it without leaking
        // instances between state transitions. Stored as GameObject so it
        // works for both the modern inert-NPC path (animated mob-model fires
        // like 1686 "vte5 fire effect") and the legacy GameStaticItem path
        // (item-model fires controlled by mimic_campfire_use_npc=false).
        private GameObject _campFire;
        private ECSGameTimer _campFireRegenTimer;
        // Range, period and amount of the mana/health/endurance bump that the
        // Modest regen pulse matching the spirit of the official "Comforting
        // Flames" tinderbox spell (id 14800): out-of-combat only, small but
        // continuous mana/health/end top-up over the fire's lifetime.
        private const int CAMPFIRE_REGEN_RANGE = 350;
        private const int CAMPFIRE_REGEN_INTERVAL_MS = 3000;
        private const int CAMPFIRE_MANA_PER_TICK = 6;
        private const int CAMPFIRE_HEALTH_PER_TICK = 8;
        private const int CAMPFIRE_END_PER_TICK = 10;

        /// <summary>
        /// True when this bot currently owns an active campfire object.
        /// Used by group-level "keep the fire alive" logic in MimicState_Camp
        /// so only one bot needs to deploy a fire per camp.
        /// </summary>
        public bool HasActiveCampFire =>
            _campFire != null && _campFire.ObjectState == eObjectState.Active;

        /// <summary>
        /// Spawns a camp-fire static item at the bot's current location and
        /// starts a pulse timer that bumps mana/health/endurance for every
        /// nearby ally in range. Visual model is configurable via the
        /// `mimic_campfire_model` server property (defaults to a brazier).
        /// Idempotent.
        /// </summary>
        public void DeployCampFire() => DeployCampFireAt(null);

        /// <summary>
        /// Drops the camp fire at the given world point if supplied, otherwise
        /// at the bot's own current position. Idempotent.
        /// </summary>
        public void DeployCampFireAt(Point3D origin)
        {
            if (HasActiveCampFire)
                return;

            ushort model = (ushort)MimicConfig.MIMIC_CAMPFIRE_MODEL;
            if (model == 0) model = 3460; // default wood-campfire worldobject model

            int fx = origin?.X ?? X;
            int fy = origin?.Y ?? Y;
            int fz = origin?.Z ?? Z;

            GameObject fire;

            if (MimicConfig.MIMIC_CAMPFIRE_USE_NPC)
            {
                // Inert GameNPC carrying the mob-model fire effect. The mob
                // model space (1686 = "vte5 fire effect", 1822 = "fire effect",
                // 911 = "Bright Flame", etc.) contains the animated flame
                // entities; mapping the same number through GameStaticItem
                // resolves into the item model space and yields random props
                // (the obelisk / sword the operator saw with 2656/2674).
                //
                // Flags make it unselectable, untargetable, no name plate,
                // and the missing brain means no movement or aggro. Level 0
                // and Size kept tiny so any stray collision math stays harmless.
                GameNPC fireNpc = new()
                {
                    Name = "campfire",
                    Model = model,
                    CurrentRegionID = CurrentRegionID,
                    X = fx,
                    Y = fy,
                    Z = fz,
                    Heading = Heading,
                    Realm = eRealm.None,
                    Level = 1,
                    Size = 50,
                    Flags = GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET | GameNPC.eFlags.DONTSHOWNAME,
                };

                if (!fireNpc.AddToWorld())
                {
                    log.WarnFormat("Failed to add campfire NPC (model {0}) for {1} at ({2},{3},{4} in region {5})",
                        model, Name, fx, fy, fz, CurrentRegionID);
                    return;
                }

                fire = fireNpc;
            }
            else
            {
                // Legacy path — kept for back-compat with operators who set
                // mimic_campfire_use_npc=false and reference an item-model
                // campfire (2656 is the historical OpenDAoC worldobject entry).
                GameStaticItem fireItem = new()
                {
                    Name = "campfire",
                    Model = model,
                    CurrentRegionID = CurrentRegionID,
                    X = fx,
                    Y = fy,
                    Z = fz,
                    Heading = Heading,
                    Realm = eRealm.None,
                };

                if (!fireItem.AddToWorld())
                {
                    log.WarnFormat("Failed to add campfire static item (model {0}) for {1} at ({2},{3},{4} in region {5})",
                        model, Name, fx, fy, fz, CurrentRegionID);
                    return;
                }

                fire = fireItem;
            }

            _campFire = fire;

            // Start the regen pulse. The timer reads from the fire object's
            // position so allies can move around the camp without breaking
            // the aura, and stops automatically if the fire is removed.
            _campFireRegenTimer = new ECSGameTimer(this, OnCampFireRegenTick);
            _campFireRegenTimer.Start(CAMPFIRE_REGEN_INTERVAL_MS);
        }

        /// <summary>
        /// Periodic tick from the deployed camp fire: tops up mana/health/end
        /// for friendly players (group members + their pets) within range.
        /// Returns the next tick interval, or 0 to stop the timer when the
        /// fire is gone.
        /// </summary>
        private int OnCampFireRegenTick(ECSGameTimer timer)
        {
            if (!HasActiveCampFire)
                return 0;

            GameObject fire = _campFire;
            ushort regionId = fire.CurrentRegionID;

            // Use the fire's region to find players. Group members are the
            // primary audience but any friendly faction in range benefits —
            // it's a cosy fire, share the warmth.
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null)
                return CAMPFIRE_REGEN_INTERVAL_MS;

            foreach (GamePlayer p in fire.GetPlayersInRadius(CAMPFIRE_REGEN_RANGE))
            {
                if (p == null || !p.IsAlive)
                    continue;
                if (p.InCombat)
                    continue;

                if (p.MaxMana > 0 && p.Mana < p.MaxMana)
                    p.Mana = Math.Min(p.MaxMana, p.Mana + CAMPFIRE_MANA_PER_TICK);

                if (p.Health < p.MaxHealth)
                    p.ChangeHealth(this, eHealthChangeType.Regenerate, CAMPFIRE_HEALTH_PER_TICK);

                if (p.Endurance < p.MaxEndurance)
                    p.Endurance = Math.Min(p.MaxEndurance, p.Endurance + CAMPFIRE_END_PER_TICK);
            }

            foreach (GameNPC npc in fire.GetNPCsInRadius(CAMPFIRE_REGEN_RANGE))
            {
                if (npc is not MimicNPC mimic || !mimic.IsAlive || mimic.InCombat)
                    continue;

                if (mimic.MaxMana > 0 && mimic.Mana < mimic.MaxMana)
                    mimic.Mana = Math.Min(mimic.MaxMana, mimic.Mana + CAMPFIRE_MANA_PER_TICK);

                if (mimic.Health < mimic.MaxHealth)
                    mimic.ChangeHealth(this, eHealthChangeType.Regenerate, CAMPFIRE_HEALTH_PER_TICK);

                if (mimic.Endurance < mimic.MaxEndurance)
                    mimic.Endurance = Math.Min(mimic.MaxEndurance, mimic.Endurance + CAMPFIRE_END_PER_TICK);
            }

            return CAMPFIRE_REGEN_INTERVAL_MS;
        }

        /// <summary>
        /// Removes the camp fire previously deployed by this bot, if any,
        /// and stops the associated regen pulse timer. Idempotent.
        /// </summary>
        public void RemoveCampFire()
        {
            _campFireRegenTimer?.Stop();
            _campFireRegenTimer = null;

            if (_campFire == null)
                return;

            if (_campFire.ObjectState == eObjectState.Active)
                _campFire.Delete();

            _campFire = null;
        }

        public override void Delete()
        {
            _beingDeleted = true;

            // Stop the rez-wait timer if the bot is being deleted while still
            // waiting for a resurrection (typical case: the owner disconnected
            // before the rez window expired). Without this, the ECS timer would
            // keep ticking and eventually call OnRezWaitExpired() on a deleted
            // bot — best case the early-return guard catches it, worst case
            // an NRE on a stale reference.
            _rezWaitTimer?.Stop();
            _rezWaitTimer = null;
            _inRezWait = false;

            RemoveCampFire();
            Group?.RemoveMember(this);
            MimicManager.UnregisterOwned(this);
            // Tear down active strategy bindings before the brain reference
            // goes away — otherwise strategies that subscribed to game events
            // (LeaderStrategy, SurvivalStrategy, etc.) leak references back
            // to this body and brain.
            MimicBrain?.StrategyManager?.Clear();
            // The DummyClient holds a real TCP socket allocated per-bot.
            // Without an explicit close the descriptor stays open until
            // GC + finalizer, which is a real leak on a server that churns
            // through frontier bots every dehydrate cycle.
            try { _dummyClient?.Socket?.Close(); } catch { }
            base.Delete();
        }

        public override bool RemoveFromWorld()
        {
            if (!base.RemoveFromWorld())
                return false;

            Duel?.Stop();
            MimicSpawner?.Remove(this);
            MimicBrain?.StrategyManager?.Clear();

            if (ControlledBrain != null)
                CommandNpcRelease();

            return true;
        }

        public double SpecLock { get; set; }
        public long LastWorldUpdate { get; set; }

        #region Client/Character/VariousFlags

        /// <summary>
        /// This is our gameclient!
        /// </summary>
        protected readonly GameClient m_client;

        /// <summary>
        /// This holds the character this player is
        /// based on!
        /// (renamed and private, cause if derive is needed overwrite PlayerCharacter)
        /// </summary>
        protected DbCoreCharacter m_dbCharacter;

        /// <summary>
        /// The guild id this character belong to
        /// </summary>
        protected string m_guildId;

        /// <summary>
        /// Char spec points checked on load
        /// </summary>
        protected bool SpecPointsOk = true;

        /// <summary>
        /// Has this player entered the game, will be
        /// true after the first time the char enters
        /// the world
        /// </summary>
        protected bool m_enteredGame;

        /// <summary>
        /// Is this player being 'jumped' to a new location?
        /// </summary>
        public bool IsJumping { get; set; }

        /// <summary>
        /// true if the targetObject is visible
        /// </summary>
        protected bool m_targetInView;

        /// <summary>
        /// Property for the optional away from keyboard message.
        /// </summary>
        public static readonly string AFK_MESSAGE = "afk_message";

        /// <summary>
        /// Property for the optional away from keyboard message.
        /// </summary>
        public static readonly string QUICK_CAST_CHANGE_TICK = "quick_cast_change_tick";

        /// <summary>
        /// Last spell cast from a used item
        /// </summary>
        public static readonly string LAST_USED_ITEM_SPELL = "last_used_item_spell";

        /// <summary>
        /// Effectiveness of the rez sick that should be applied. This is set by rez spells just before rezzing.
        /// </summary>
        public static readonly string RESURRECT_REZ_SICK_EFFECTIVENESS = "RES_SICK_EFFECTIVENESS";

        /// <summary>
        /// Array that stores ML step completition
        /// </summary>
        private ArrayList m_mlSteps = new ArrayList();

        protected bool m_usedetailedcombatlog = false;

        public bool UseDetailedCombatLog
        {
            get { return false; }
            set { m_usedetailedcombatlog = value; }
        }

        eXPLogState _xpLogState;

        public eXPLogState XPLogState
        {
            get { return eXPLogState.Off; }
            set { _xpLogState = value; }
        }

        /// <summary>
        /// Gets or sets the targetObject's visibility
        /// </summary>
        public override bool TargetInView
        {
            get
            {
                if (GetDistanceTo(TargetObject) <= TargetInViewAlwaysTrueMinRange)
                    return true;

                return m_targetInView;
            }
            set => m_targetInView = value;
        }

        // Upstream removed the virtual IsMimic on GameNPC; this MimicNPC-only
        // property keeps existing call sites that read `npc.IsMimic` through
        // a MimicNPC reference working. Callers going through the GameNPC
        // base need a pattern match (`obj is MimicNPC`) instead.
        public bool IsMimic => true;

        public override int TargetInViewAlwaysTrueMinRange => (TargetObject is GamePlayer targetPlayer && targetPlayer.IsMoving) ? 100 : 64;

        private Dictionary<RandomDeckEvent, RandomDeck> _randomDecks = new();

        public override bool Chance(RandomDeckEvent deckEvent, int chancePercent)
        {
            return !Properties.OVERRIDE_DECK_RNG && _randomDecks.TryGetValue(deckEvent, out RandomDeck deck) ?
                deck.Draw() < chancePercent :
                base.Chance(deckEvent, chancePercent);
        }

        public override bool Chance(RandomDeckEvent deckEvent, double chancePercent)
        {
            return GetPseudoDouble(deckEvent) < chancePercent;
        }

        public override double GetPseudoDouble(RandomDeckEvent deckEvent)
        {
            return !Properties.OVERRIDE_DECK_RNG && _randomDecks.TryGetValue(deckEvent, out RandomDeck deck) ?
                (deck.Draw() + Util.RandomDouble()) / 100.0 :
                base.GetPseudoDouble(deckEvent);
        }

        public override double GetPseudoDoubleIncl(RandomDeckEvent deckEvent)
        {
            return !Properties.OVERRIDE_DECK_RNG && _randomDecks.TryGetValue(deckEvent, out RandomDeck deck) ?
                (deck.Draw() + Util.RandomDoubleIncl()) / 100.0 :
                base.GetPseudoDoubleIncl(deckEvent);
        }

        public void InitializeRandomDecks()
        {
            foreach (RandomDeckEvent deckEvent in Enum.GetValues<RandomDeckEvent>())
                _randomDecks[deckEvent] = new();
        }

        /// <summary>
        /// Gets or sets the GroundTargetObject's visibility
        /// </summary>
        public override bool GroundTargetInView
        {
            get => GroundTarget.InView;
            set => GroundTarget.InView = value;
        }

        protected int m_OutOfClassROGPercent = 0;

        public int OutOfClassROGPercent
        {
            get { return m_OutOfClassROGPercent; }
            set { m_OutOfClassROGPercent = value; }
        }

        /// <summary>
        /// Player is in BG ?
        /// </summary>
        protected bool _isInBG;

        public bool isInBG
        {
            get { return _isInBG; }
            set { _isInBG = value; }
        }

        /// <summary>
        /// The character the player is based on
        /// </summary>
        internal DbCoreCharacter DBCharacter
        {
            get { return m_dbCharacter; }
        }

        /// <summary>
        /// Can this player use cross realm items
        /// </summary>
        public virtual bool CanUseCrossRealmItems { get { return Properties.ALLOW_CROSS_REALM_ITEMS; } }

        protected bool _lastDeathPvP;

        public bool LastDeathPvP
        {
            get { return _lastDeathPvP; }
            set { _lastDeathPvP = value; }
        }

        protected bool _wasMovedByCorpseSummoner;

        public bool WasMovedByCorpseSummoner
        {
            get { return _wasMovedByCorpseSummoner; }
            set { _wasMovedByCorpseSummoner = value; }
        }


        #region Database Accessor

        /// <summary>
        /// Gets or sets the Database ObjectId for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string ObjectId
        {
            get { return DBCharacter != null ? DBCharacter.ObjectId : InternalID; }
            set { if (DBCharacter != null) DBCharacter.ObjectId = value; }
        }

        /// <summary>
        /// Gets or sets the gain XP flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool GainXP
        {
            get { return true; }
            set { if (DBCharacter != null) DBCharacter.GainXP = value; }
        }

        /// <summary>
        /// Gets or sets the gain RP flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool GainRP
        {
            get { return true; }
            set { if (DBCharacter != null) DBCharacter.GainRP = value; }
        }

        /// <summary>
        /// gets or sets the guildnote for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string GuildNote
        {
            get { return DBCharacter != null ? DBCharacter.GuildNote : String.Empty; }
            set { if (DBCharacter != null) DBCharacter.GuildNote = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseRegion for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseRegion
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseRegion : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseRegion = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseXpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseXpos
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseXpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseXpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseYpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseYpos
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseYpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseYpos = value; }
        }

        /// <summary>
        /// Gets or sets BindHouseZpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseZpos
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseZpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseZpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseHeading for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseHeading
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseHeading : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseHeading = value; }
        }

        long _deathTime;
        /// <summary>
        /// Gets or sets the DeathTime for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public long DeathTime
        {
            get { return _deathTime; }
            set { _deathTime = value; }
        }

        /// <summary>
        /// Gets or sets the BindRegion for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindRegion
        {
            get { return DBCharacter != null ? DBCharacter.BindRegion : 0; }
            set { if (DBCharacter != null) DBCharacter.BindRegion = value; }
        }

        /// <summary>
        /// Gets or sets the BindXpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindXpos
        {
            get { return DBCharacter != null ? DBCharacter.BindXpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindXpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindYpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindYpos
        {
            get { return DBCharacter != null ? DBCharacter.BindYpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindYpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindZpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindZpos
        {
            get { return DBCharacter != null ? DBCharacter.BindZpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindZpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindHeading for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHeading
        {
            get { return DBCharacter != null ? DBCharacter.BindHeading : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHeading = value; }
        }

        /// <summary>
        /// Gets or sets the Database MaxEndurance for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int DBMaxEndurance
        {
            get { return DBCharacter != null ? DBCharacter.MaxEndurance : 100; }
            set { if (DBCharacter != null) DBCharacter.MaxEndurance = value; }
        }

        /// <summary>
        /// Gets AccountName for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string AccountName
        {
            get { return string.Empty; }
        }

        DateTime _creationDate;

        /// <summary>
        /// Gets CreationDate for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public DateTime CreationDate
        {
            get { return _creationDate; }
            set { _creationDate = value; }
        }

        /// <summary>
        /// Gets LastPlayed for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public DateTime LastPlayed
        {
            get { return DateTime.Now; }
        }

        /// <summary>
        /// Gets or sets the BindYpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public byte DeathCount
        {
            get { return DBCharacter != null ? DBCharacter.DeathCount : (byte)0; }
            set { if (DBCharacter != null) DBCharacter.DeathCount = value; }
        }

        private int _killStreak = 0;

        public int KillStreak
        {
            get { return _killStreak; }
            set { _killStreak = value; }
        }

        /// <summary>
        /// Gets the last time this player leveled up
        /// </summary>
        public DateTime LastLevelUp
        {
            get { return DBCharacter != null ? DBCharacter.LastLevelUp : DateTime.MinValue; }
            set { if (DBCharacter != null) DBCharacter.LastLevelUp = value; }
        }

        #endregion Database Accessor

        /// <summary>
        /// Holds the Steed of this player. Mimics don't support mounts, so the
        /// getter is always null and the setter is a no-op (the previous setter
        /// dereferenced an uninitialized field and would throw).
        /// </summary>
        public GameNPC Steed
        {
            get { return null; }
            set { }
        }

        /// <summary>
        /// Returns if the player is riding or not
        /// </summary>
        /// <returns>true if on a steed, false if not</returns>
        public virtual bool IsRiding
        {
            get { return Steed != null; }
        }

        #endregion Client/Character/VariousFlags

        #region Combat timer

        public override long LastAttackTickPvE
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackTickPvE = value;

                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        public override long LastAttackTickPvP
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackTickPvP = value;

                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        public override long LastAttackedByEnemyTickPvE
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackedByEnemyTickPvE = value;

                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        public override long LastAttackedByEnemyTickPvP
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackedByEnemyTickPvP = value;
                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        /// <summary>
        /// Expire Combat Timer Interval
        /// </summary>
        private static int COMBAT_TIMER_INTERVAL => 11000;

        /// <summary>
        /// Combat Timer
        /// </summary>
        private ECSGameTimer m_combatTimer;

        /// <summary>
        /// Reset and Restart Combat Timer
        /// </summary>
        protected virtual void ResetInCombatTimer()
        {
            m_combatTimer.Start(COMBAT_TIMER_INTERVAL);
        }

        #endregion Combat timer

        #region release/bind/pray

        #region Binding

        /// <summary>
        /// Property that holds tick when the player bind last time
        /// </summary>
        public const string LAST_BIND_TICK = "LastBindTick";

        /// <summary>
        /// Min Allowed Interval Between Player Bind
        /// </summary>
        public virtual int BindAllowInterval { get { return 60000; } }

        /// <summary>
        /// Binds this player to the current location
        /// </summary>
        /// <param name="forced">if true, can bind anywhere</param>
        public virtual void Bind()
        {
            if (CurrentRegion.IsInstance)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.CantHere"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (!IsAlive)
                return;

            //60 second rebind timer
            long lastBindTick = TempProperties.GetProperty<long>(LAST_BIND_TICK, 0);
            long changeTime = CurrentRegion.Time - lastBindTick;

            if (changeTime < BindAllowInterval)
                return;

            bool bound = false;

            //var bindarea = CurrentAreas.OfType<Area.BindArea>().FirstOrDefault(ar => GameServer.ServerRules.IsAllowedToBind(this, ar.BindPoint));
            //if (bindarea != null)
            //{
            //    bound = true;
            //    BindRegion = CurrentRegionID;
            //    BindHeading = Heading;
            //    BindXpos = X;
            //    BindYpos = Y;
            //    BindZpos = Z;
            //    if (DBCharacter != null)
            //        GameServer.Database.SaveObject(DBCharacter);
            //}

            //if we are not bound yet lets check if we are in a house where we can bind
            if (!bound && InHouse && CurrentHouse != null)
            {
                var house = CurrentHouse;
                bool canbindhere;
                try
                {
                    canbindhere = house.HousePointItems.Any(kv => ((GameObject)kv.Value.GameObject).GetName(0, false).EndsWith("bindstone", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    canbindhere = false;
                }

                if (canbindhere)
                {
                    // make sure we can actually use the bindstone
                    //if (!house.CanBindInHouse(this))
                    //{
                    //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.CantHere"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    //    return;
                    //}
                    //else
                    //{
                    //    bound = true;
                    //    double angle = house.Heading * ((Math.PI * 2) / 360); // angle*2pi/360;
                    //    int outsideX = (int)(house.X + (0 * Math.Cos(angle) + 500 * Math.Sin(angle)));
                    //    int outsideY = (int)(house.Y - (500 * Math.Cos(angle) - 0 * Math.Sin(angle)));
                    //    ushort outsideHeading = (ushort)((house.Heading < 180 ? house.Heading + 180 : house.Heading - 180) / 0.08789);
                    //    BindHouseRegion = CurrentRegionID;
                    //    BindHouseHeading = outsideHeading;
                    //    BindHouseXpos = outsideX;
                    //    BindHouseYpos = outsideY;
                    //    BindHouseZpos = house.Z;
                    //    if (DBCharacter != null)
                    //        GameServer.Database.SaveObject(DBCharacter);
                    //}
                }
            }

            if (bound)
            {
                if (!IsMoving)
                {
                    eEmote bindEmote = eEmote.Bind;
                    switch (Realm)
                    {
                        case eRealm.Albion: bindEmote = eEmote.BindAlb; break;
                        case eRealm.Midgard: bindEmote = eEmote.BindMid; break;
                        case eRealm.Hibernia: bindEmote = eEmote.BindHib; break;
                    }

                    foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    {
                        if (player == null)
                            return;

                        if ((int)player.Client.Version < (int)GameClient.eClientVersion.Version187)
                            player.Out.SendEmoteAnimation(this, eEmote.Bind);
                        else
                            player.Out.SendEmoteAnimation(this, bindEmote);
                    }
                }

                TempProperties.SetProperty(LAST_BIND_TICK, CurrentRegion.Time);
            }
        }

        #endregion Binding

        #region Releasing

        /// <summary>
        /// tick when player is died
        /// </summary>
        public long DeathTick { get; set; }

        /// <summary>
        /// The current death type
        /// </summary>
        protected eDeathType m_deathtype;

        /// <summary>
        /// Gets the player's current death type.
        /// </summary>
        public eDeathType DeathType
        {
            get { return m_deathtype; }
            set { m_deathtype = value; }
        }

        /// <summary>
        /// Called when player revive
        /// </summary>
        public virtual void OnRevive(DOLEvent e, object sender, EventArgs args)
        {
            IGamePlayer player = sender as IGamePlayer;

            bool applyRezSick = true;

            // Used by spells like Perfect Recovery
            if (TempProperties.GetAllProperties().Contains(RESURRECT_REZ_SICK_EFFECTIVENESS) && TempProperties.GetProperty<double>(RESURRECT_REZ_SICK_EFFECTIVENESS) == 0)
            {
                applyRezSick = false;
                TempProperties.RemoveProperty(RESURRECT_REZ_SICK_EFFECTIVENESS);
            }
            else if (player.Level < ServerProperties.Properties.RESS_SICKNESS_LEVEL)
            {
                applyRezSick = false;
            }

            if (player.IsUnderwater && player.CanBreathUnderWater == false)
                player.UpdateWaterBreathState(eWaterBreath.Holding);

            //We need two different sickness spells because RvR sickness is not curable by Healer NPC -Unty
            if (applyRezSick)
            {
                switch (DeathType)
                {
                    case eDeathType.RvR:
                    SpellLine rvrsick = SkillBase.GetSpellLine(GlobalSpellsLines.Realm_Spells);
                    if (rvrsick == null) return;
                    Spell rvrillness = SkillBase.FindSpell(8181, rvrsick);
                    //player.CastSpell(rvrillness, rvrsick);
                    CastSpell(rvrillness, rvrsick);
                    break;
                    case eDeathType.PvP: //PvP sickness is the same as PvE sickness - Curable
                    case eDeathType.PvE:
                    SpellLine pvesick = SkillBase.GetSpellLine(GlobalSpellsLines.Realm_Spells);
                    if (pvesick == null) return;
                    Spell pveillness = SkillBase.FindSpell(2435, pvesick);
                    //player.CastSpell(pveillness, pvesick);
                    CastSpell(pveillness, pvesick);
                    break;
                }
            }

            // The RemoveHandler call that used to live here was a no-op:
            // no AddHandler(GamePlayerEvent.Revive, OnRevive) ever runs in
            // the MimicNPC lifecycle, so unsubscribing achieved nothing and
            // mostly just hinted (incorrectly) that the bot was subscribed
            // to the revive event. If we ever wire OnRevive up later, add
            // the matching AddHandler in the bot's init path first.
            m_deathtype = eDeathType.None;
            LastDeathPvP = false;
            //UpdatePlayerStatus();
            //Out.SendPlayerRevive(this);
        }

        /// <summary>
        /// Property that saves experience lost on last death
        /// </summary>
        public const string DEATH_EXP_LOSS_PROPERTY = "death_exp_loss";

        /// <summary>
        /// Property that saves condition lost on last death
        /// </summary>
        public const string DEATH_CONSTITUTION_LOSS_PROPERTY = "death_con_loss";

        #endregion Releasing

        #region Praying

        /// <summary>
        /// The timer that will be started when the player wants to pray
        /// </summary>
        private ECSGameTimer m_prayAction;

        /// <summary>
        /// Gets the praying-state of this living
        /// </summary>
        public virtual bool IsPraying => m_prayAction?.IsAlive == true;

        /// <summary>
        /// Prays on a gravestone for XP!
        /// </summary>
        public virtual void Pray()
        {
            string cantPrayMessage = null;
            GameGravestone gravestone = TargetObject as GameGravestone;

            if (!IsAlive)
                cantPrayMessage = "GamePlayer.Pray.CantPrayNow";
            //else if (IsRiding)
            //    cantPrayMessage = "GamePlayer.Pray.CantPrayRiding";
            else if (gravestone == null)
                cantPrayMessage = "GamePlayer.Pray.NeedTarget";
            else if (!gravestone.InternalID.Equals(InternalID))
                cantPrayMessage = "GamePlayer.Pray.SelectGrave";
            else if (!IsWithinRadius(gravestone, 2000))
                cantPrayMessage = "GamePlayer.Pray.MustGetCloser";
            else if (IsMoving)
                cantPrayMessage = "GamePlayer.Pray.MustStandingStill";
            else if (IsPraying)
                cantPrayMessage = "GamePlayer.Pray.AlreadyPraying";

            if (cantPrayMessage != null)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, cantPrayMessage), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            m_prayAction = new ECSGameTimer(this, new ECSGameTimer.ECSTimerCallback(_ =>
            {
                if (gravestone.XPValue > 0)
                {
                    // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pray.GainBack"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    GainExperience(eXPSource.Praying, gravestone.XPValue);
                }

                gravestone.XPValue = 0;
                gravestone.Delete();
                m_prayAction = null;
                return 0;
            }), 5000);

            Sit(true);
            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pray.Begin"), eChatType.CT_System, eChatLoc.CL_SystemWindow);

            foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            {
                if (player == null)
                    continue;

                player.Out.SendEmoteAnimation(this, eEmote.Pray);
            }
        }

        /// <summary>
        /// Stop praying; used when player changes target
        /// </summary>
        public void PrayTimerStop()
        {
            if (!IsPraying)
                return;
            m_prayAction.Stop();
            m_prayAction = null;
        }

        #endregion Praying

        #endregion release/bind/pray

        #region Stats

        private int _totalConstitutionLostAtDeath = 0;

        /// <summary>
        /// Gets/sets the player efficacy percent
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int TotalConstitutionLostAtDeath
        {
            get { return _totalConstitutionLostAtDeath; }
            set { _totalConstitutionLostAtDeath = value; }
        }

        /// <summary>
        /// Change a stat value
        /// (delegate to PlayerCharacter)
        /// </summary>
        /// <param name="stat">The stat to change</param>
        /// <param name="val">The new value</param>
        public override void ChangeBaseStat(eStat stat, short val)
        {
            base.ChangeBaseStat(stat, val);
        }

        /// <summary>
        /// Gets player's strength
        /// </summary>
        public new int Strength
        {
            get { return (short)GetModified(eProperty.Strength); }
        }

        /// <summary>
        /// Gets player's dexterity
        /// </summary>
        public new int Dexterity
        {
            get { return (short)GetModified(eProperty.Dexterity); }
        }

        /// <summary>
        /// Gets player's constitution
        /// </summary>
        public new int Constitution
        {
            get { return (short)GetModified(eProperty.Constitution); }
        }

        /// <summary>
        /// Gets mimic's quickness
        /// </summary>
        public new int Quickness
        {
            get { return (short)GetModified(eProperty.Quickness); }
        }

        /// <summary>
        /// Gets player's intelligence
        /// </summary>
        public new int Intelligence
        {
            get { return (short)GetModified(eProperty.Intelligence); }
        }

        /// <summary>
        /// Gets player's piety
        /// </summary>
        public new int Piety
        {
            get { return (short)GetModified(eProperty.Piety); }
        }

        /// <summary>
        /// Gets player's empathy
        /// </summary>
        public new int Empathy
        {
            get { return (short)GetModified(eProperty.Empathy); }
        }

        /// <summary>
        /// Gets player's charisma
        /// </summary>
        public new int Charisma
        {
            get { return GetModified(eProperty.Charisma); }
        }

        protected PlayerStatistics m_statistics = null;

        /// <summary>
        /// Get the statistics for this player
        /// </summary>
        public virtual PlayerStatistics Statistics
        {
            get { return m_statistics; }
        }

        /// <summary>
        /// Create played statistics for this player
        /// </summary>
        public virtual void CreateStatistics()
        {
            m_statistics = new PlayerStatistics(this);
        }

        #endregion Stats

        #region Health/Mana/Endurance/Regeneration

        private int GetHealthAndPowerRegenerationInterval()
        {
            // From Uthgard.
            // 6s normal, 3s sitting, 14s combat, 10s sitting combat.
            // There is no elegant formula for this. Sitting + in-combat might have been caused by rounding errors on Live.
            bool inCombat = InCombat;
            bool isSitting = IsSitting;
            int interval = 6 - (isSitting ? 3 : 0) + (inCombat ? 8 : 0) - (isSitting && inCombat ? 1 : 0);
            return interval * 1000;
        }

        protected override int GetHealthRegenerationInterval()
        {
            return GetHealthAndPowerRegenerationInterval();
        }

        protected override int GetPowerRegenerationInterval()
        {
            return GetHealthAndPowerRegenerationInterval();
        }

        protected override int GetEnduranceRegenerationInterval()
        {
            return 1000;
        }

        public override void StartPowerRegeneration()
        {
            if (m_health == 0 || ObjectState is not eObjectState.Active || m_powerRegenerationTimer.IsAlive)
                return;

            m_powerRegenerationTimer.Start(GetPowerRegenerationInterval());
        }

        public override void StartEnduranceRegeneration()
        {
            if (m_health == 0 || ObjectState is not eObjectState.Active || m_enduRegenerationTimer.IsAlive)
                return;

            m_enduRegenerationTimer.Start(GetEnduranceRegenerationInterval());
        }

        protected override int HealthRegenerationTimerCallback(ECSGameTimer callingTimer)
        {
            int maxHealth = MaxHealth;

            if (Health >= maxHealth)
            {
                Health = maxHealth;

                lock (XpGainersLock)
                {
                    m_xpGainers.Clear();
                }

                return 0;
            }

            ChangeHealth(this, eHealthChangeType.Regenerate, GetModified(eProperty.HealthRegenerationAmount));
            return GetHealthRegenerationInterval();
        }

        protected override int EnduranceRegenerationTimerCallback(ECSGameTimer selfRegenerationTimer)
        {
            int maxEndurance = MaxEndurance;
            bool sprinting = IsSprinting;

            if (Endurance >= maxEndurance)
            {
                Endurance = maxEndurance;

                if (!sprinting)
                    return 0;
            }

            int regen = GetModified(eProperty.EnduranceRegenerationAmount);

            // EnduranceRegenerationAmountCalculator gates the standing / sitting
            // out-of-combat bonus behind `living is GamePlayer`, so MimicNPCs
            // (which derive from GameNPC) silently get regen == 0 unless they
            // happen to hold a buff/ability/item that grants it. Without this,
            // a bot whose endurance dropped below max from any source (style,
            // sprint, fall damage) would never recover and sprint would be
            // permanently unavailable past the first drain. Mirror the player
            // formula here so the bot ticks back up while idle.
            if (!InCombat && !IsMoving)
                regen += IsSitting ? 4 : 1;

            int endChant = GetModified(eProperty.FatigueConsumption);
            ECSGameEffect charge = EffectListService.GetEffectOnTarget(this, eEffect.Charge);
            int longWind = 5;

            if (sprinting && IsMoving)
            {
                if (charge is null)
                {
                    // The bot's own Long Wind (rare — stock mimics don't buy RAs).
                    AtlasOF_LongWindAbility raLongWind = GetAbility<AtlasOF_LongWindAbility>();
                    int ownLongWindReduction = raLongWind != null
                        ? raLongWind.GetAmountForLevel(CalculateSkillLevel(raLongWind))
                        : 0;

                    // Mirror the human leader's sprint context. Live DAoC groups
                    // sustain infinite sprint by running Endurance Regen potion +
                    // Long Wind RA together — the potion supplies the regen, the
                    // RA cancels the drain. A grouped bot has no real way to buy
                    // either, so we let it inherit the leader's setup for the
                    // duration of the sprint. No permanent buffs are applied;
                    // these values only influence this single tick's math.
                    GamePlayer humanLeader = MimicBrain?.CachedPlayerLeader;

                    if (humanLeader != null && humanLeader.IsAlive
                        && humanLeader.CurrentRegion == CurrentRegion)
                    {
                        AtlasOF_LongWindAbility leaderRa = humanLeader.GetAbility<AtlasOF_LongWindAbility>();
                        int leaderLongWindReduction = leaderRa != null
                            ? leaderRa.GetAmountForLevel(humanLeader.CalculateSkillLevel(leaderRa))
                            : 0;

                        // Use whichever Long Wind is stronger (bot's own RA or the
                        // leader's). Same formula as GamePlayer: a level-5 RA
                        // (reduction = 100) zeroes out longWind, so a 100 %
                        // EnduRegen potion makes the net drain positive.
                        ownLongWindReduction = Math.Max(ownLongWindReduction, leaderLongWindReduction);

                        // If the leader is actively running the Endurance Regen
                        // potion / chant, mirror that regen amount onto the bot.
                        // Take the higher of (bot's own regen, leader's regen) so
                        // a buffed bot stacked on a buffed leader doesn't lose
                        // ground.
                        if (humanLeader.effectListComponent.ContainsEffectForEffectType(eEffect.EnduranceRegenBuff))
                        {
                            int leaderRegen = humanLeader.GetModified(eProperty.EnduranceRegenerationAmount);
                            if (leaderRegen > regen)
                                regen = leaderRegen;
                        }
                    }

                    longWind -= ownLongWindReduction * 5 / 100;
                    regen -= longWind;

                    if (endChant > 1)
                        regen = (int)Math.Ceiling(regen * endChant * 0.01);

                    if (Endurance + regen > maxEndurance - longWind)
                        regen -= Endurance + regen - (maxEndurance - longWind);
                }
            }

            if (regen != 0)
                ChangeEndurance(this, eEnduranceChangeType.Regenerate, regen);

            if (sprinting)
            {
                if (Endurance - 5 <= 0)
                    Sprint(false);
            }

            return GetEnduranceRegenerationInterval();
        }

        /// <summary>
        /// Gets/sets the object health
        /// </summary>
        public override int Health
        {
            get => base.Health;
            set
            {
                value = Math.Clamp(value, 0, MaxHealth);
                //If it is already set, don't do anything
                if (Health == value)
                {
                    base.Health = value; //needed to start regeneration
                    return;
                }

                int oldPercent = HealthPercent;
                base.Health = value;

                if (oldPercent != HealthPercent)
                {
                    if (Group != null)
                        Group.UpdateMember(this, false, false);
                    //UpdatePlayerStatus();
                }
            }
        }

        /// <summary>
        /// Calculates the maximum health for a specific playerlevel and constitution
        /// </summary>
        /// <param name="level">The level of the player</param>
        /// <param name="constitution">The constitution of the player</param>
        /// <returns></returns>
        public virtual int CalculateMaxHealth(int level, int constitution)
        {
            constitution -= 50;

            if (constitution < 0)
                constitution *= 2;

            // hp1 : from level
            // hp2 : from constitution
            // hp3 : from champions level
            // hp4 : from artifacts such Spear of Kings charge
            int hp1 = CharacterClass.BaseHP * level;
            int hp2 = hp1 * constitution / 10000;
            int hp3 = 0;
            if (ChampionLevel >= 1)
                hp3 = ServerProperties.Properties.HPS_PER_CHAMPIONLEVEL * ChampionLevel;
            double hp4 = 20 + hp1 / 50 + hp2 + hp3;
            if (GetModified(eProperty.ExtraHP) > 0)
                hp4 += Math.Round(hp4 * (double)GetModified(eProperty.ExtraHP) / 100);

            return Math.Max(1, (int)hp4);
        }

        public override byte HealthPercentGroupWindow => CharacterClass.HealthPercentGroupWindow;

        /// <summary>
        /// Calculate max mana for this player based on level and mana stat level
        /// </summary>
        public virtual int CalculateMaxMana(int level, int manaStat)
        {
            int maxPower = 0;

            // Special handling for Vampiirs:
            /* There is no stat that affects the Vampiir's power pool or the damage done by its power based spells.
             * The Vampiir is not a focus based class like, say, an Enchanter.
             * The Vampiir is a lot more cut and dried than the typical casting class.
             * EDIT, 12/13/04 - I was told today that this answer is not entirely accurate.
             * While there is no stat that affects the damage dealt (in the way that intelligence or piety affects how much damage a more traditional caster can do),
             * the Vampiir's power pool capacity is intended to be increased as the Vampiir's strength increases.
             *
             * This means that strength ONLY affects a Vampiir's mana pool
             * 
             * http://www.camelotherald.com/more/1913.shtml
             * Strength affects the amount of damage done by spells in all of the Vampiir's spell lines.
             * The amount of said affecting was recently increased slightly (fixing a bug), and that minor increase will go live in 1.74 next week.
             * 
             * Strength ALSO affects the size of the power pool for a Vampiir sort of.
             * Your INNATE strength (the number of attribute points your character has for strength) has no effect at all.
             * Extra points added through ITEMS, however, does increase the size of your power pool.
             */

            // Since 1.62, Augmented Acuity is supposed to increase Nightshade's power pool, but without increasing their actual stat.
            // This isn't implemented currently.

            /*
             * Current formula is within -1 to +1 mana (ignoring a few outliers) of the values displayed on Pendragon when leveling up or training.
             * Tested between level 1 and 50, and with a mana stat between 60 and 233.
             * This included Augmented Acuity, but no buffs as they didn't affect the feedback values.
             * No difference between Sorcerer, Mentalist and Cleric (implying there is no power table).
             * (Feb 2026).
             */

            if (CharacterClass.ManaStat is not eStat.UNDEFINED || (eCharacterClass)CharacterClass.ID is eCharacterClass.Vampiir)
                maxPower = (int)(level * (manaStat + 145) / 41.0) + 5;
            else if (Champion && ChampionLevel > 0)
                maxPower = 100; // This is a guess, need feedback.

            return Math.Max(0, maxPower);
        }

        /// <summary>
        /// Gets/sets the object mana
        /// </summary>
        public override int Mana
        {
            get { return m_mana; }
            set
            {
                int maxMana = MaxMana;
                int mana;
                mana = Math.Min(value, maxMana);
                mana = Math.Max(mana, 0);

                if (IsAlive && (mana < maxMana))
                    StartPowerRegeneration();

                int oldPercent = ManaPercent;
                m_mana = mana;

                if (oldPercent != ManaPercent)
                {
                    if (Group != null)
                        Group.UpdateMember(this, false, false);
                }
            }
        }

        /// <summary>
        /// Gets/sets the object max mana
        /// </summary>
        public override int MaxMana
        {
            get { return GetModified(eProperty.MaxMana); }
        }

        /// <summary>
        /// Gets/sets the object endurance
        /// </summary>
        public override int Endurance
        {
            get { return base.Endurance; }
            set
            {
                int endurance;
                endurance = Math.Min(value, MaxEndurance);
                endurance = Math.Max(endurance, 0);

                if (IsAlive && endurance < MaxEndurance)
                    StartEnduranceRegeneration();

                int oldPercent = EndurancePercent;
                m_endurance = endurance;

                if (oldPercent != EndurancePercent)
                {
                    if (Group != null)
                        Group.UpdateMember(this, false, false);
                }
            }
        }

        public override int MaxEndurance => base.MaxEndurance;
        public override int Concentration => MaxConcentration - effectListComponent.UsedConcentration;
        public override int MaxConcentration => GetModified(eProperty.MaxConcentration);

        #region Calculate Fall Damage

        /// <summary>
        /// Calculates fall damage taking fall damage reduction bonuses into account
        /// </summary>
        /// <returns></returns>
        public virtual double CalcFallDamage(int fallDamagePercent)
        {
            if (fallDamagePercent <= 0)
                return 0;

            int safeFallLevel = GetAbilityLevel("Safe Fall");
            int mythSafeFall = GetModified(eProperty.MythicalSafeFall);

            if (mythSafeFall > 0 & mythSafeFall < fallDamagePercent)
            {
                Client.Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.MythSafeFall"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
                fallDamagePercent = mythSafeFall;
            }
            // if (safeFallLevel > 0 & mythSafeFall == 0)
            //  Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.SafeFall"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);

            Endurance -= MaxEndurance * fallDamagePercent / 100;
            double damage = (0.01 * fallDamagePercent * (MaxHealth - 1));

            // [Freya] Nidel: CloudSong falling damage reduction
#pragma warning disable CS0618 // legacy effect API still used elsewhere; mirror GamePlayer behavior
            GameSpellEffect cloudSongFall = SpellHandler.FindEffectOnTarget(this, "CloudsongFall");
#pragma warning restore CS0618
            if (cloudSongFall != null)
                damage -= (damage * cloudSongFall.Spell.Value) * 0.01;

            //Mattress: SafeFall property for Mythirians, the value of the MythicalSafeFall property represents the percent damage taken in a fall.
            if (mythSafeFall != 0 && damage > mythSafeFall)
                damage = ((MaxHealth - 1) * (mythSafeFall * 0.01));

            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.FallingDamage"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.FallPercent", fallDamagePercent), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            // Out.SendMessage("You lose endurance.", eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            TakeDamage(null, eDamageType.Falling, (int)damage, 0);

            //Update the player's health to all other players around
            //foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            //Out.SendCombatAnimation(null, Client.Player, 0, 0, 0, 0, 0, HealthPercent);

            return damage;
        }

        #endregion Calculate Fall Damage

        #endregion Health/Mana/Endurance/Regeneration

        #region Class/Race

        /// <summary>
        /// Gets/sets the player's race name
        /// </summary>
        public virtual string RaceName
        {
            //get { return RaceToTranslatedName(Race, Gender); }
            //get { return string.Format("!{0} - {1}!", ((eRace)Race).ToString("F"), Gender.ToString("F")); }
            get { return ((eRace)Race).ToString("F"); }
        }

        ///// <summary>
        ///// Gets or sets this player's race id
        ///// (delegate to DBCharacter)
        ///// </summary>
        //public override short Race
        //{
        //    get { return m_race; }
        //    set { m_race = value; }
        //}

        //private short m_race;

        /// <summary>
        /// Players class
        /// </summary>
        protected ICharacterClass m_characterClass;

        /// <summary>
        /// Gets the player's character class
        /// </summary>
        public virtual ICharacterClass CharacterClass
        {
            get { return m_characterClass; }
        }

        /// <summary>
        /// Set the character class to a specific one
        /// </summary>
        /// <param name="id">id of the character class</param>
        /// <returns>success</returns>
        public virtual bool SetCharacterClass(int id)
        {
            ICharacterClass cl = ScriptMgr.FindCharacterClass(id);

            if (cl == null)
            {
                if (log.IsErrorEnabled)
                    log.ErrorFormat("No CharacterClass with ID {0} found", id);
                return false;
            }

            m_characterClass = cl;
            m_characterClass.Init((IGamePlayer) this);

            //DBCharacter.Class = m_characterClass.ID;

            if (Group != null)
            {
                Group.UpdateMember(this, false, true);
            }

            return true;
        }

        /// <summary>
        /// Hold all player face custom attibutes
        /// </summary>
        protected byte[] m_customFaceAttributes = new byte[(int)eCharFacePart._Last + 1];

        /// <summary>
        /// Get the character face attribute you want
        /// </summary>
        /// <param name="part">face part</param>
        /// <returns>attribute</returns>
        public byte GetFaceAttribute(eCharFacePart part)
        {
            return m_customFaceAttributes[(int)part];
        }

        #endregion Class/Race

        #region Spells/Skills/Abilities/Effects

        /// <summary>
        /// Holds the player choosen list of Realm Abilities.
        /// </summary>
        protected readonly ReaderWriterList<RealmAbility> m_realmAbilities = new ReaderWriterList<RealmAbility>();

        /// <summary>
        /// Holds the player specializable skills and style lines
        /// (KeyName -> Specialization)
        /// </summary>
        protected readonly Dictionary<string, Specialization> m_specialization = new Dictionary<string, Specialization>();
        protected readonly Lock _specializationLock = new();

        /// <summary>
        /// Holds the Spell lines the player can use
        /// </summary>
        protected readonly List<SpellLine> m_spellLines = new List<SpellLine>();

        /// <summary>
        /// Object to use when locking the SpellLines list
        /// </summary>
        protected readonly Lock _spellLinesListLock = new();

        /// <summary>
        /// Temporary Stats Boni
        /// </summary>
        protected readonly int[] m_statBonus = new int[8];

        /// <summary>
        /// Temporary Stats Boni in percent
        /// </summary>
        protected readonly int[] m_statBonusPercent = new int[8];

        /// <summary>
        /// Gets/Sets amount of full skill respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountAllSkill
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountAllSkill : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountAllSkill = value; }
        }

        /// <summary>
        /// Gets/Sets amount of single-line respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountSingleSkill
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountSingleSkill : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountSingleSkill = value; }
        }

        /// <summary>
        /// Gets/Sets amount of realm skill respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountRealmSkill
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountRealmSkill : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountRealmSkill = value; }
        }

        /// <summary>
        /// Gets/Sets amount of DOL respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountDOL
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountDOL : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountDOL = value; }
        }

        /// <summary>
        /// Gets/Sets level respec usage flag
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool IsLevelRespecUsed
        {
            get { return DBCharacter != null ? DBCharacter.IsLevelRespecUsed : true; }
            set { if (DBCharacter != null) DBCharacter.IsLevelRespecUsed = value; }
        }

        protected static readonly int[] m_numRespecsCanBuyOnLevel =
        {
            1,1,1,1,1, //1-5
            2,2,2,2,2,2,2, //6-12
            3,3,3,3, //13-16
            4,4,4,4,4,4, //17-22
            5,5,5,5,5, //23-27
            6,6,6,6,6,6, //28-33
            7,7,7,7,7, //34-38
            8,8,8,8,8,8, //39-44
            9,9,9,9,9, //45-49
            10 //50
        };

        /// <summary>
        /// Can this player buy a respec?
        /// </summary>
        public virtual bool CanBuyRespec
        {
            get
            {
                return (RespecBought < m_numRespecsCanBuyOnLevel[Level - 1]);
            }
        }

        /// <summary>
        /// Gets/Sets amount of bought respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecBought
        {
            get { return DBCharacter != null ? DBCharacter.RespecBought : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecBought = value; }
        }

        protected static readonly int[] m_respecCost =
        {
            1,2,3, //13
            2,5,9, //14
            3,9,17, //15
            6,16,30, //16
            10,26,48,75, //17
            16,40,72,112, //18
            22,56,102,159, //19
            31,78,140,218, //20
            41,103,187,291, //21
            54,135,243,378, //22
            68,171,308,480,652, //23
            85,214,385,600,814, //24
            105,263,474,738,1001, //25
            128,320,576,896,1216, //26
            153,383,690,1074,1458, //27
            182,455,820,1275,1731,2278, //28
            214,535,964,1500,2036,2679, //29
            250,625,1125,1750,2375,3125, //30
            289,723,1302,2025,2749,3617, //31
            332,831,1497,2329,3161,4159, //32
            380,950,1710,2661,3612,4752, //33
            432,1080,1944,3024,4104,5400,6696, //34
            488,1220,2197,3417,4638,6103,7568, //35
            549,1373,2471,3844,5217,6865,8513, //36
            615,1537,2767,4305,5843,7688,9533, //37
            686,1715,3087,4802,6517,8575,10633, //38
            762,1905,3429,5335,7240,9526,11813,14099, //39
            843,2109,3796,5906,8015,10546,13078,15609, //40
            930,2327,4189,6516,8844,11637,14430,17222, //41
            1024,2560,4608,7168,9728,1280,15872,18944, //42
            1123,2807,5053,7861,10668,14037,17406,20776, //43
            1228,3070,5527,8597,11668,15353,19037,22722, //44
            1339,3349,6029,9378,12725,16748,20767,24787,28806, //45
            1458,3645,6561,10206,13851,18225,22599,26973,31347, //46
            1582,3957,7123,11080,15037,19786,24535,29283,34032, //47
            1714,4286,7716,12003,16290,21434,26578,31722,36867, //48
            1853,4634,8341,12976,17610,23171,28732,34293,39854, //49
            2000,5000,9000,14000,19000,25000,31000,37000,43000,50000 //50
        };

        /// <summary>
        /// How much does this player have to pay for a respec?
        /// </summary>
        public virtual long RespecCost
        {
            get
            {
                if (Level <= 12) //1-12
                    return m_respecCost[0];

                if (CanBuyRespec)
                {
                    int t = 0;
                    for (int i = 13; i < Level; i++)
                    {
                        t += m_numRespecsCanBuyOnLevel[i - 1];
                    }

                    return m_respecCost[t + RespecBought];
                }

                return -1;
            }
        }

        /// <summary>
        /// give player a new Specialization or improve existing one
        /// </summary>
        /// <param name="skill"></param>
        public void AddSpecialization(Specialization skill)
        {
            AddSpecialization(skill, true);
        }

        /// <summary>
        /// give player a new Specialization or improve existing one
        /// </summary>
        /// <param name="skill"></param>
        protected virtual void AddSpecialization(Specialization skill, bool notify)
        {
            if (skill == null)
                return;

            lock (_specializationLock)
            {
                if (m_specialization.TryGetValue(skill.KeyName, out Specialization specialization))
                {
                    specialization.Level = skill.Level;
                    return;
                }

                m_specialization.Add(skill.KeyName, skill);

                if (notify)
                    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.AddSpecialisation.YouLearn", skill.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        }

        /// <summary>
        /// Removes the existing specialization from the player
        /// </summary>
        /// <param name="specKeyName">The spec keyname to remove</param>
        /// <returns>true if removed</returns>
        public virtual bool RemoveSpecialization(string specKeyName)
        {
            Specialization playerSpec = null;

            lock (_specializationLock)
            {
                if (!m_specialization.TryGetValue(specKeyName, out playerSpec))
                    return false;

                m_specialization.Remove(specKeyName);
            }

            return true;
        }

        /// <summary>
        /// Removes the existing spellline from the player, the line instance should be called with GamePlayer.GetSpellLine ONLY and NEVER SkillBase.GetSpellLine!!!!!
        /// </summary>
        /// <param name="line">The spell line to remove</param>
        /// <returns>true if removed</returns>
        protected virtual bool RemoveSpellLine(SpellLine line)
        {
            lock (_spellLinesListLock)
            {
                if (!m_spellLines.Contains(line))
                {
                    return false;
                }

                m_spellLines.Remove(line);
            }

            return true;
        }

        /// <summary>
        /// Removes the existing specialization from the player
        /// </summary>
        /// <param name="lineKeyName">The spell line keyname to remove</param>
        /// <returns>true if removed</returns>
        public virtual bool RemoveSpellLine(string lineKeyName)
        {
            SpellLine line = GetSpellLine(lineKeyName);
            if (line == null)
                return false;

            return RemoveSpellLine(line);
        }

        /// <summary>
        /// Reset this player to level 1, respec all skills, remove all spec points, and reset stats
        /// </summary>
        public virtual void Reset()
        {
            byte originalLevel = Level;
            Level = 1;
            Experience = 0;
            RespecAllLines();

            if (Level < originalLevel && originalLevel > 5)
            {
                for (int i = 6; i <= originalLevel; i++)
                {
                    if (CharacterClass.PrimaryStat != eStat.UNDEFINED)
                    {
                        ChangeBaseStat(CharacterClass.PrimaryStat, -1);
                    }
                    if (CharacterClass.SecondaryStat != eStat.UNDEFINED && ((i - 6) % 2 == 0))
                    {
                        ChangeBaseStat(CharacterClass.SecondaryStat, -1);
                    }
                    if (CharacterClass.TertiaryStat != eStat.UNDEFINED && ((i - 6) % 3 == 0))
                    {
                        ChangeBaseStat(CharacterClass.TertiaryStat, -1);
                    }
                }
            }

            //CharacterClass.OnLevelUp(this, originalLevel);
        }

        /// <summary>
        /// Load this player Classes Specialization.
        /// </summary>
        public virtual void LoadClassSpecializations(bool sendMessage)
        {
            // Get this Attached Class Specialization from SkillBase.
            IDictionary<Specialization, int> careers = SkillBase.GetSpecializationCareer(CharacterClass.ID);

            // Remove All Trainable Specialization or "Career Spec" that aren't managed by This Data Career anymore
            var speclist = GetSpecList();
            var careerslist = careers.Keys.Select(k => k.KeyName.ToLower());

            foreach (var spec in speclist.Where(sp => sp.Trainable || !sp.AllowSave))
            {
                if (!careerslist.Contains(spec.KeyName.ToLower()))
                    RemoveSpecialization(spec.KeyName);
            }

            foreach (KeyValuePair<Specialization, int> constraint in careers)
            {
                if (constraint.Key is IMasterLevelsSpecialization)
                    continue;

                // load if the spec doesn't exists
                if (Level >= constraint.Value)
                {
                    if (!HasSpecialization(constraint.Key.KeyName))
                        AddSpecialization(constraint.Key, sendMessage);
                }
                else
                {
                    if (HasSpecialization(constraint.Key.KeyName))
                        RemoveSpecialization(constraint.Key.KeyName);
                }
            }
        }

        /// <summary>
        /// Verify this player has the correct number of spec points for the players level
        /// </summary>
        public virtual int VerifySpecPoints()
        {
            // calc normal spec points for the level & classe
            int allpoints = -1;
            for (int i = 1; i <= Level; i++)
            {
                if (i <= 5) allpoints += i; //start levels
                if (i > 5) allpoints += CharacterClass.SpecPointsMultiplier * i / 10; //normal levels
                if (i > 40) allpoints += CharacterClass.SpecPointsMultiplier * (i - 1) / 20; //half levels
            }
            if (IsLevelSecondStage && Level != MAX_LEVEL)
                allpoints += CharacterClass.SpecPointsMultiplier * Level / 20; // add current half level

            // calc spec points player have (autotrain is not anymore processed here - 1.87 livelike)
            int usedpoints = 0;
            foreach (Specialization spec in GetSpecList().Where(e => e.Trainable))
            {
                usedpoints += (spec.Level * (spec.Level + 1) - 2) / 2;
                usedpoints -= GetAutoTrainPoints(spec, 0);
            }

            allpoints -= usedpoints;

            // check if correct, if not respec. Not applicable to GMs
            if (allpoints < 0)
            {
                if (Client.Account.PrivLevel == 1)
                {
                    log.WarnFormat("Spec points total for player {0} incorrect: {1} instead of {2}.", Name, usedpoints, allpoints + usedpoints);
                    RespecAllLines();
                    return allpoints + usedpoints;
                }
            }

            return allpoints;
        }

        public virtual bool RespecAll()
        {
            if (RespecAllLines())
            {
                // Wipe skills and styles.
                RespecAmountAllSkill--; // Decriment players respecs available.
                if (Level == 5)
                    IsLevelRespecUsed = true;

                return true;
            }

            return false;
        }

        public virtual bool RespecDOL()
        {
            if (RespecAllLines()) // Wipe skills and styles.
            {
                RespecAmountDOL--; // Decriment players respecs available.
                return true;
            }

            return false;
        }

        public virtual int RespecSingle(Specialization specLine)
        {
            int specPoints = RespecSingleLine(specLine); // Wipe skills and styles.
            if (!ServerProperties.Properties.FREE_RESPEC)
                RespecAmountSingleSkill--; // Decriment players respecs available.
            if (Level == 20 || Level == 40)
            {
                IsLevelRespecUsed = true;
            }
            return specPoints;
        }

        public virtual bool RespecRealm(bool useRespecPoint = true)
        {
            bool any = m_realmAbilities.Count > 0;

            foreach (Ability ab in m_realmAbilities)
                RemoveAbility(ab.KeyName);

            m_realmAbilities.Clear();
            if (!ServerProperties.Properties.FREE_RESPEC && useRespecPoint)
                RespecAmountRealmSkill--;
            return any;
        }

        protected virtual bool RespecAllLines()
        {
            bool ok = false;
            IList<Specialization> specList = GetSpecList().Where(e => e.Trainable).ToList();
            foreach (Specialization cspec in specList)
            {
                if (cspec.Level < 2)
                    continue;
                RespecSingleLine(cspec);
                ok = true;
            }
            return ok;
        }

        /// <summary>
        /// Respec single line
        /// </summary>
        /// <param name="specLine">spec line being respec'd</param>
        /// <returns>Amount of points spent in that line</returns>
        protected virtual int RespecSingleLine(Specialization specLine)
        {
            int specPoints = (specLine.Level * (specLine.Level + 1) - 2) / 2;
            // Graveen - autotrain 1.87
            specPoints -= GetAutoTrainPoints(specLine, 0);

            //setting directly the autotrain points in the spec
            if (GetAutoTrainPoints(specLine, 4) == 1 && Level >= 8)
            {
                specLine.Level = (int)Math.Floor((double)Level / 4);
            }
            else specLine.Level = 1;

            return specPoints;
        }

        /// <summary>
        /// Send this players trainer window
        /// </summary>
        public virtual void SendTrainerWindow()
        {
            //Out.SendTrainerWindow();
        }

        /// <summary>
        /// returns a list with all specializations
        /// in the order they were added
        /// </summary>
        /// <returns>list of Spec's</returns>
        public virtual IList<Specialization> GetSpecList()
        {
            List<Specialization> list;

            lock (_specializationLock)
            {
                // sort by Level and ID to simulate "addition" order... (try to sort your DB if you want to change this !)
                list = m_specialization.Select(item => item.Value).OrderBy(it => it.LevelRequired).ThenBy(it => it.ID).ToList();
            }

            return list;
        }

        /// <summary>
        /// returns a list with all non trainable skills without styles
        /// This is a copy of Ability until any unhandled Skill subclass needs to go in there...
        /// </summary>
        /// <returns>list of Skill's</returns>
        public virtual IList GetNonTrainableSkillList()
        {
            return GetAllAbilities();
        }

        /// <summary>
        /// Retrives a specific specialization by name
        /// </summary>
        /// <param name="name">the name of the specialization line</param>
        /// <param name="caseSensitive">false for case-insensitive compare</param>
        /// <returns>found specialization or null</returns>
        public virtual Specialization GetSpecializationByName(string name)
        {
            Specialization spec = null;

            lock (_specializationLock)
            {
                foreach (KeyValuePair<string, Specialization> entry in m_specialization)
                {
                    if (entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        spec = entry.Value;
                        break;
                    }
                }
            }

            return spec;
        }

        /// <summary>
        /// The best armor level this player can use.
        /// </summary>
        public virtual int BestArmorLevel
        {
            get
            {
                int bestLevel = -1;
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("AlbArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("HibArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("MidArmor"));
                return bestLevel;
            }
        }

        public virtual int BestShieldLevel { get { return GetAbilityLevel("Shield"); } }

        #region Abilities

        /// <summary>
        /// Adds a new Ability to the player
        /// </summary>
        /// <param name="ability"></param>
        /// <param name="sendUpdates"></param>
        public override void AddAbility(Ability ability, bool sendUpdates)
        {
            if (ability == null)
                return;

            base.AddAbility(ability, sendUpdates);
        }

        /// <summary>
        /// Adds a Realm Ability to the player
        /// </summary>
        /// <param name="ability"></param>
        /// <param name="sendUpdates"></param>
        public virtual void AddRealmAbility(RealmAbility ability, bool sendUpdates)
        {
            if (ability == null)
                return;

            m_realmAbilities.FreezeWhile(list =>
            {
                int index = list.FindIndex(ab => ab.ID == ability.ID);
                if (index > -1)
                {
                    list[index].Level = ability.Level;
                }
                else
                {
                    list.Add(ability);
                }
            });
        }

        #endregion Abilities

        public virtual void RemoveAllAbilities()
        {
            lock (_abilitiesLock)
            {
                m_abilities.Clear();
            }
        }

        public virtual void RemoveAllSpecs()
        {
            lock (_specializationLock)
            {
                m_specialization.Clear();
            }
        }

        public virtual void RemoveAllSpellLines()
        {
            lock (_spellLinesListLock)
            {
                m_spellLines.Clear();
            }
        }

        /// <summary>
        /// Retrieve this player Realm Abilities.
        /// </summary>
        /// <returns></returns>
        public virtual List<RealmAbility> GetRealmAbilities()
        {
            return m_realmAbilities.ToList();
        }

        /// <summary>
        /// Asks for existance of specific specialization
        /// </summary>
        /// <param name="keyName"></param>
        /// <returns></returns>
        public virtual bool HasSpecialization(string keyName)
        {
            bool hasit = false;

            lock (_specializationLock)
            {
                hasit = m_specialization.ContainsKey(keyName);
            }

            return hasit;
        }

        /// <summary>
        /// returns the level of a specialization
        /// if 0 is returned, the spec is non existent on player
        /// </summary>
        /// <param name="keyName"></param>
        /// <returns></returns>
        public override int GetBaseSpecLevel(string keyName)
        {
            Specialization spec = null;
            int level = 0;

            lock (_specializationLock)
            {
                if (m_specialization.TryGetValue(keyName, out spec))
                    level = m_specialization[keyName].Level;
            }

            return level;
        }

        /// <summary>
        /// returns the level of a specialization + bonuses from RR and Items
        /// if 0 is returned, the spec is non existent on the player
        /// </summary>
        /// <param name="keyName"></param>
        /// <returns></returns>
        public override int GetModifiedSpecLevel(string keyName)
        {
            if (keyName.StartsWith(GlobalSpellsLines.Champion_Lines_StartWith))
                return 50;

            if (keyName.StartsWith(GlobalSpellsLines.Realm_Spells))
                return Level;

            Specialization spec = null;
            int level = 0;
            lock (_specializationLock)
            {
                if (!m_specialization.TryGetValue(keyName, out spec))
                {
                    // Combat_Styles_Effect is a virtual spec line — Reaver /
                    // Heretic / Valewalker / Savage proc-style spells route
                    // through it. The original code computed the right
                    // surrogate level here, then immediately overwrote it
                    // with `level = 0;` outside the if — every Combat-Style
                    // proc on these four classes was using spec 0 and
                    // landing for minimum damage / minimum heal. Keep the
                    // 0 fallback for the actual non-special-case path.
                    if (keyName == GlobalSpellsLines.Combat_Styles_Effect)
                    {
                        if (CharacterClass.ID == (int)eCharacterClass.Reaver || CharacterClass.ID == (int)eCharacterClass.Heretic)
                            level = GetModifiedSpecLevel(Specs.Flexible);
                        else if (CharacterClass.ID == (int)eCharacterClass.Valewalker)
                            level = GetModifiedSpecLevel(Specs.Scythe);
                        else if (CharacterClass.ID == (int)eCharacterClass.Savage)
                            level = GetModifiedSpecLevel(Specs.Savagery);
                        else
                            level = 0;
                    }
                    else
                    {
                        level = 0;
                    }
                }
            }

            if (spec != null)
            {
                level = spec.Level;
                // TODO: should be all in calculator later, right now
                // needs specKey -> eProperty conversion to find calculator and then
                // needs eProperty -> specKey conversion to find how much points player has spent
                eProperty skillProp = SkillBase.SpecToSkill(keyName);
                if (skillProp != eProperty.Undefined)
                    level += GetModified(skillProp);
            }

            return level;
        }

        /// <summary>
        /// Adds a spell line to the player
        /// </summary>
        /// <param name="line"></param>
        public virtual void AddSpellLine(SpellLine line)
        {
            AddSpellLine(line, true);
        }

        /// <summary>
        /// Adds a spell line to the player
        /// </summary>
        /// <param name="line"></param>
        public virtual void AddSpellLine(SpellLine line, bool notify)
        {
            if (line == null)
                return;

            SpellLine oldline = GetSpellLine(line.KeyName);
            if (oldline == null)
            {
                lock (_spellLinesListLock)
                {
                    m_spellLines.Add(line);
                }
            }
            else
            {
                oldline.Level = line.Level;
            }
        }

        /// <summary>
        /// return a list of spell lines in the order they were added
        /// this is a copy only.
        /// </summary>
        /// <returns></returns>
        public virtual List<SpellLine> GetSpellLines()
        {
            List<SpellLine> list = new List<SpellLine>();
            lock (_spellLinesListLock)
            {
                list = new List<SpellLine>(m_spellLines);
            }

            return list;
        }

        /// <summary>
        /// find a spell line on player and return them
        /// </summary>
        /// <param name="keyname"></param>
        /// <returns></returns>
        public virtual SpellLine GetSpellLine(string keyname)
        {
            lock (_spellLinesListLock)
            {
                foreach (SpellLine line in m_spellLines)
                {
                    if (line.KeyName == keyname)
                        return line;
                }
            }
            return null;
        }

        private volatile List<(Skill, Skill)> _usableSkills = new();
        private readonly Lock _usableSkillsLock = new();

        private volatile List<(SpellLine, List<Skill>)> _usableListSpells = new();
        private readonly Lock _usableListSpellsLock = new();

        public virtual List<(SpellLine, List<Skill>)> GetAllUsableListSpells(bool update = false)
        {
            // The returned list must not be modified by the caller.

            if (!update)
            {
                var snapshot = _usableListSpells;

                if (snapshot.Count > 0)
                    return snapshot;
            }

            lock (_usableListSpellsLock)
            {
                if (!update && _usableListSpells.Count > 0)
                    return _usableListSpells;

                // Copy-on-write.
                List<(SpellLine, List<Skill>)> newList = [.. _usableListSpells];
                GamePlayerUtils.UpdateUsableListSpells(this, newList);
                _usableListSpells = newList;
                return newList;
            }
        }

        public List<(Skill, Skill)> GetAllUsableSkills(bool update = false)
        {
            // The returned list must not be modified by the caller.

            if (!update)
            {
                var snapshot = _usableSkills;

                if (snapshot.Count > 0)
                    return snapshot;
            }

            lock (_usableSkillsLock)
            {
                if (!update && _usableSkills.Count > 0)
                    return _usableSkills;

                // Copy-on-write.
                List<(Skill, Skill)> newList = [.. _usableSkills];
                GamePlayerUtils.UpdateUsableSkills(this, newList);
                _usableSkills = newList;
                return newList;
            }
        }

        /// <summary>
        /// updates the list of available skills (dependent on caracter specs)
        /// </summary>
        /// <param name="sendMessages">sends "you learn" messages if true</param>
        public virtual void RefreshSpecDependantSkills(bool sendMessages)
        {
            // refresh specs
            LoadClassSpecializations(sendMessages);

            // lock specialization while refreshing...
            lock (_specializationLock)
            {
                foreach (Specialization spec in m_specialization.Values)
                {
                    // check for new Abilities
                    foreach (Ability ab in spec.GetAbilitiesForLiving(this))
                    {
                        if (!HasAbility(ab.KeyName) || GetAbility(ab.KeyName).Level < ab.Level)
                            AddAbility(ab, false);
                    }

                    // check for new Styles
                    foreach (Style st in spec.GetStylesForLiving(this))
                    {
                        if (st.SpecLevelRequirement == 2 && Level > 5)
                        {
                            if (Styles.Contains(st))
                            {
                                log.Info("Removed base style.");
                                Styles.Remove(st);
                            }

                            continue;
                        }

                        AddStyle(st, false);
                    }

                    // check for new SpellLine
                    foreach (SpellLine sl in spec.GetSpellLinesForLiving(this))
                    {
                        AddSpellLine(sl, false);
                    }
                }
            }

            MimicBrain?.OnRefreshSpecDependantSkills();
        }

        public virtual void AddStyle(Style st, bool notify)
        {
            lock (Styles)
            {
                if (!Styles.Contains(st))
                {
                    Styles.Add(st);
                }
            }

            Styles = Styles;
        }

        /// <summary>
        /// effectiveness of the player (resurrection illness)
        /// Effectiveness is used in physical/magic damage (exept dot), in weapon skill and max concentration formula
        /// </summary>
        protected double m_playereffectiveness = 1.0;

        /// <summary>
        /// get / set the player's effectiveness.
        /// Effectiveness is used in physical/magic damage (exept dot), in weapon skill and max concentration
        /// </summary>
        public override double Effectiveness
        {
            get { return m_playereffectiveness; }
            set { m_playereffectiveness = value; }
        }

        /// <summary>
        /// Creates new effects list for this living.
        /// </summary>
        /// <returns>New effects list instance</returns>
        //protected override GameEffectList CreateEffectsList()
        //{
        //    return new GameEffectPlayerList(this);
        //}

        #endregion Spells/Skills/Abilities/Effects




        #region Duel

        public GameDuel Duel { get; private set; }
        public GameLiving DuelPartner => Duel?.GetPartnerOf(this);
        public bool IsDuelReady { get; set; }

        public void OnDuelStart(GameDuel duel)
        {
            Duel?.Stop();
            Duel = duel;
        }

        public void OnDuelStop()
        {
            if (Duel == null)
                return;

            IsDuelReady = false;
            Duel = null;
        }

        public bool IsDuelPartner(GameLiving living)
        {
            if (living == null)
                return false;

            GameLiving partner = DuelPartner;

            if (partner == null)
                return false;

            if (living is GameNPC npc && npc.Brain is ControlledMobBrain brain)
                living = brain.GetLivingOwner();

            return partner == living;
        }

        #endregion Duel

        #region Stealth / Wireframe

        private bool m_isTorchLighted = false;

        /// <summary>
        /// Is player Torch lighted ?
        /// </summary>
        public bool IsTorchLighted
        {
            get { return m_isTorchLighted; }
            set { m_isTorchLighted = value; }
        }

        /// <summary>
        /// Property that holds tick when stealth state was changed last time
        /// </summary>
        public const string STEALTH_CHANGE_TICK = "StealthChangeTick";

        /// <summary>
        /// The stealth state of this player
        /// </summary>
        public override bool IsStealthed => effectListComponent.ContainsEffectForEffectType(eEffect.Stealth);

        /// <summary>
        /// Set player's stealth state
        /// </summary>
        /// <param name="goStealth">true is stealthing, false if unstealthing</param>
        public override void Stealth(bool goStealth)
        {
            if (IsStealthed == goStealth)
                return;

            if (goStealth && !InCombat)
            {
                if (effectListComponent.ContainsEffectForEffectType(eEffect.Pulse))
                    return;

                ECSGameEffectFactory.Create(new(this, 0, 1), static (in i) => new StealthECSGameEffect(i));
                return;
            }

            ECSGameEffect effect = EffectListService.GetEffectOnTarget(this, eEffect.Stealth);
            effect?.End();
        }

        // UncoverStealthAction is what unstealths player if they are too close to mobs.
        public void StartStealthUncoverAction()
        {
            MimicUncoverStealthAction action = TempProperties.GetProperty<MimicUncoverStealthAction>(UNCOVER_STEALTH_ACTION_PROP);
            //start the uncover timer
            if (action == null)
                action = new MimicUncoverStealthAction(this);

            action.Interval = 1000;
            action.Start(1000);
            TempProperties.SetProperty(UNCOVER_STEALTH_ACTION_PROP, action);
        }

        // UncoverStealthAction is what unstealths player if they are too close to mobs.
        public void StopStealthUncoverAction()
        {
            MimicUncoverStealthAction action = TempProperties.GetProperty<MimicUncoverStealthAction>(UNCOVER_STEALTH_ACTION_PROP);
            //stop the uncover timer
            if (action != null)
            {
                action.Stop();
                TempProperties.RemoveProperty(UNCOVER_STEALTH_ACTION_PROP);
            }
        }

        /// <summary>
        /// The temp property that stores the uncover stealth action
        /// </summary>
        protected const string UNCOVER_STEALTH_ACTION_PROP = "UncoverStealthAction";

        /// <summary>
        /// Uncovers the player if a mob is too close
        /// </summary>
        protected class MimicUncoverStealthAction : ECSGameTimerWrapperBase
        {
            /// <summary>
            /// Constructs a new uncover stealth action
            /// </summary>
            /// <param name="actionSource">The action source</param>
            public MimicUncoverStealthAction(MimicNPC actionSource) : base(actionSource) { }

            /// <summary>
            /// Called on every timer tick
            /// </summary>
            protected override int OnTick(ECSGameTimer timer)
            {
                MimicNPC mimic = (MimicNPC)timer.Owner;

                foreach (GameNPC npc in mimic.GetNPCsInRadius(1024))
                {
                    if (npc is MimicNPC)
                        continue;

                    // Friendly mobs do not uncover stealthed players
                    if (!GameServer.ServerRules.IsAllowedToAttack(npc, mimic, true))
                        continue;

                    // Npc with player owner don't uncover
                    if (npc.Brain != null
                        && (npc.Brain as IControlledBrain) != null
                        && (npc.Brain as IControlledBrain).GetIPlayerOwner() != null)
                        continue;

                    double npcLevel = Math.Max(npc.Level, 1.0);
                    double stealthLevel = mimic.GetModifiedSpecLevel(Specs.Stealth);
                    double detectRadius = 125.0 + ((npcLevel - stealthLevel) * 20.0);

                    // we have detect hidden and enemy don't = higher range
                    if (npc.HasAbility(GS.Abilities.DetectHidden) &&
                        !mimic.HasAbility(GS.Abilities.DetectHidden) &&
                        !mimic.effectListComponent.ContainsEffectForEffectType(eEffect.Camouflage) &&
                        !mimic.effectListComponent.ContainsEffectForEffectType(eEffect.Vanish))
                    {
                        detectRadius += 125;
                    }

                    if (detectRadius < 126) detectRadius = 126;

                    double distanceToPlayer = npc.GetDistanceTo(mimic);

                    if (distanceToPlayer > detectRadius)
                        continue;

                    double fieldOfView = 90.0;  //90 degrees  = standard FOV
                    double fieldOfListen = 120.0; //120 degrees = standard field of listening

                    if (npc.Level > 50)
                        fieldOfListen += (npc.Level - mimic.Level) * 3;

                    double angle = npc.GetAngle(mimic);

                    //player in front
                    fieldOfView /= 2.0;
                    bool canSeePlayer = (angle >= 360 - fieldOfView || angle < fieldOfView);

                    //If npc can not see nor hear the player, continue the loop
                    fieldOfListen /= 2.0;
                    if (canSeePlayer == false &&
                        !(angle >= (45 + 60) - fieldOfListen && angle < (45 + 60) + fieldOfListen) &&
                        !(angle >= (360 - 45 - 60) - fieldOfListen && angle < (360 - 45 - 60) + fieldOfListen))
                        continue;

                    double chanceMod = 1.0;

                    //Chance to detect player decreases after 125 coordinates!
                    if (distanceToPlayer > 125)
                        chanceMod = 1f - (distanceToPlayer - 125.0) / (detectRadius - 125.0);

                    double chanceToUncover = 0.1 + (npc.Level - stealthLevel) * 0.01 * chanceMod;

                    if (Util.Chance(chanceToUncover))
                    {
                        if (!canSeePlayer)
                            npc.TurnTo(mimic, 10000);
                    }
                }

                return Interval;
            }
        }

        public virtual bool CanDetect(GameObject enemy)
        {
            if (!enemy.IsStealthed || Client.Account.PrivLevel > 1)
                return true;

            if (!IsAlive)
                return false;

            switch (enemy.GameObjectType)
            {
                case eGameObjectType.NPC when enemy is IGamePlayer:
                case eGameObjectType.PLAYER:
                {
                    IGamePlayer enemyPlayer = enemy as IGamePlayer;

                    // Own group is always visible.
                    if (enemyPlayer.Group != null && Group != null && enemyPlayer.Group == Group)
                        return true;

                    // Why is this still using the old effect list and vanish effect?
                    if (enemyPlayer.EffectList.GetOfType<VanishEffect>() != null || enemyPlayer.Client.Account.PrivLevel > 1)
                        return false;

                    // This ignores the hard cap.
                    if (effectListComponent.ContainsEffectForEffectType(eEffect.TrueSight))
                        return true;

                    /*
                     * http://www.critshot.com/forums/showthread.php?threadid=3142
                     * The person doing the looking has a chance to find them based on their level, minus the stealthed person's stealth spec.
                     *
                     * -Normal detection range = (enemy lvl  your stealth spec) * 20 + 125
                     * -Detect Hidden Range = (enemy lvl  your stealth spec) * 50 + 250
                     * -See Hidden range = 2700 - (38 * your stealth spec)
                     */

                    int enemyStealthLevel = Math.Min(50, enemyPlayer.GetModifiedSpecLevel(Specs.Stealth));
                    int levelDiff = Math.Max(0, Level - enemyStealthLevel);
                    int range = 0;

                    // Detect Hidden works only if the enemy doesn't have it or camouflage or vanish.
                    // According to https://disorder.dk/daoc/stealth/, it's possible that the range per level difference is supposed to be 50 even when Detect Hidden is cancelled out.
                    if (HasAbility(GS.Abilities.DetectHidden) &&
                        !enemyPlayer.HasAbility(GS.Abilities.DetectHidden) &&
                        !enemyPlayer.EffectListComponent.ContainsEffectForEffectType(eEffect.Camouflage) &&
                        !enemyPlayer.EffectListComponent.ContainsEffectForEffectType(eEffect.Vanish))
                    {
                        range = levelDiff * 50 + 250;
                    }
                    else
                        range = levelDiff * 20 + 125; 

                    // See Hidden only works against non assassin classes, if they don't have Camouflage enabled.
                    if (HasAbilityType(typeof(AtlasOF_SeeHidden)) &&
                        !enemyPlayer.CharacterClass.IsAssassin &&
                        !enemyPlayer.EffectListComponent.ContainsEffectForEffectType(eEffect.Camouflage))
                    {
                        //https://forums.freddyshouse.com/threads/scouts-and-stealth.139740/
                        range = Math.Max(range, 2700 - 36 * enemyStealthLevel);
                    }

                        // Mastery of Stealth
                        // Disabled. This is NF MoS. OF Version does not add range, only movement speed.
                        /*RAPropertyEnhancer mos = GetAbility<MasteryOfStealthAbility>();
                        if (mos != null && !enemyHasCamouflage)
                        {
                            if (!HasAbility(Abilities.DetectHidden) || !enemy.HasAbility(Abilities.DetectHidden))
                                range += mos.GetAmountForLevel(CalculateSkillLevel(mos));
                        }*/

                        range += BaseBuffBonusCategory[eProperty.Skill_Stealth];

                        // //Buff (Stealth Detection)
                        // //Increases the target's ability to detect stealthed players and monsters.
                        // GameSpellEffect iVampiirEffect = SpellHandler.FindEffectOnTarget((GameLiving)this, "VampiirStealthDetection");
                        // if (iVampiirEffect != null)
                        //     range += (int)iVampiirEffect.Spell.Value;
                        //
                        // //Infill Only - Greater Chance to Detect Stealthed Enemies for 1 minute
                        // //after executing a klling blow on a realm opponent.
                        // GameSpellEffect HeightenedAwareness = SpellHandler.FindEffectOnTarget((GameLiving)this, "HeightenedAwareness");
                        // if (HeightenedAwareness != null)
                        //     range += (int)HeightenedAwareness.Spell.Value;
                        //
                        // //Nightshade Only - Greater chance of remaining hidden while stealthed for 1 minute
                        // //after executing a killing blow on a realm opponent.
                        // GameSpellEffect SubtleKills = SpellHandler.FindEffectOnTarget((GameLiving)enemy, "SubtleKills");
                        // if (SubtleKills != null)
                        // {
                        //     range -= (int)SubtleKills.Spell.Value;
                        //     if (range < 0) range = 0;
                        // }
                        //
                        // // Apply Blanket of camouflage effect
                        // GameSpellEffect iSpymasterEffect1 = SpellHandler.FindEffectOnTarget((GameLiving)enemy, "BlanketOfCamouflage");
                        // if (iSpymasterEffect1 != null)
                        // {
                        //     range -= (int)iSpymasterEffect1.Spell.Value;
                        //     if (range < 0) range = 0;
                        // }
                        //
                        // // Apply Lookout effect
                        // GameSpellEffect iSpymasterEffect2 = SpellHandler.FindEffectOnTarget((GameLiving)this, "Loockout");
                        // if (iSpymasterEffect2 != null)
                        //     range += (int)iSpymasterEffect2.Spell.Value;
                        //
                        // // Apply Prescience node effect
                        // GameSpellEffect iConvokerEffect = SpellHandler.FindEffectOnTarget((GameLiving)enemy, "Prescience");
                        // if (iConvokerEffect != null)
                        //     range += (int)iConvokerEffect.Spell.Value;

                        // Hard cap.
                        if (range > 1900)
                        range = 1900;

                    return IsWithinRadius(enemy, range);
                }
                case eGameObjectType.NPC:
                {
                    // Custom feature to make stealthed NPCs actually invisible
                    // is currently disabled — NPCs see stealthed mimics at any
                    // range. Keep the level-based formula here as a reference
                    // for re-enabling later (see commented return below).
                    // int detectionRange = Math.Clamp(1500 + (Level - enemy.Level) * 50, 500, 3000);
                    // return IsWithinRadius(enemy, detectionRange);
                    return true;
                }
                default:
                    return true;
            }
        }

        #endregion Stealth / Wireframe


        #region Shade

        /// <summary>
        /// Gets flag indication whether player is in shade mode
        /// </summary>
        public bool HasShadeModel => Model == ShadeModel;

        /// <summary>
        /// The model ID used on character creation.
        /// </summary>
        public ushort CreationModel
        {
            get
            {
                // m_client is a legacy GamePlayer-side field that was never
                // wired up on MimicNPC (the bot uses _dummyClient instead).
                // Reading m_client.Account.Characters[] NPE'd for every code
                // path that asked for CreationModel — notably the Bainshee
                // remorph and shade-toggle helpers. Mimics don't carry a
                // distinct creation model, so fall back to the live Model.
                return Model;
            }
        }

        /// <summary>
        /// The model ID used for shade morphs.
        /// </summary>
        public ushort ShadeModel
        {
            get
            {
                // Aredhel: Bit fishy, necro in caster from could use
                // Traitor's Dagger... FIXME!

                if (CharacterClass is ClassDisciple)
                    return 822;

                switch (Race)
                {
                    // Albion Models.
                    //case (int)eRace.Inconnu: return (ushort)(DBCharacter.Gender + 1351);
                    //case (int)eRace.Briton: return (ushort)(DBCharacter.Gender + 1353);
                    //case (int)eRace.Avalonian: return (ushort)(DBCharacter.Gender + 1359);
                    //case (int)eRace.Highlander: return (ushort)(DBCharacter.Gender + 1355);
                    //case (int)eRace.Saracen: return (ushort)(DBCharacter.Gender + 1357);
                    //case (int)eRace.HalfOgre: return (ushort)(DBCharacter.Gender + 1361);

                    //// Midgard Models.
                    //case (int)eRace.Troll: return (ushort)(DBCharacter.Gender + 1363);
                    //case (int)eRace.Dwarf: return (ushort)(DBCharacter.Gender + 1369);
                    //case (int)eRace.Norseman: return (ushort)(DBCharacter.Gender + 1365);
                    //case (int)eRace.Kobold: return (ushort)(DBCharacter.Gender + 1367);
                    //case (int)eRace.Valkyn: return (ushort)(DBCharacter.Gender + 1371);
                    //case (int)eRace.Frostalf: return (ushort)(DBCharacter.Gender + 1373);

                    //// Hibernia Models.
                    //case (int)eRace.Firbolg: return (ushort)(DBCharacter.Gender + 1375);
                    //case (int)eRace.Celt: return (ushort)(DBCharacter.Gender + 1377);
                    //case (int)eRace.Lurikeen: return (ushort)(DBCharacter.Gender + 1379);
                    //case (int)eRace.Elf: return (ushort)(DBCharacter.Gender + 1381);
                    //case (int)eRace.Sylvan: return (ushort)(DBCharacter.Gender + 1383);
                    //case (int)eRace.Shar: return (ushort)(DBCharacter.Gender + 1385);

                    default: return Model;
                }
            }
        }

        /// <summary>
        /// Changes shade state of the player.
        /// </summary>
        /// <param name="state">The new state.</param>
        public virtual void Shade(bool state)
        {
            CharacterClass.Shade(state, out _);
        }
        #endregion

        #region Guild

        private Guild _guild;

        public Guild Guild
        {
            get { return _guild; }
            set { _guild = value; }
        }

        #endregion Guild


        #region ControlledNpc

        public override bool AddControlledBrain(IControlledBrain controlledBrain)
        {
            return SetControlledBrain(controlledBrain);
        }

        public override bool RemoveControlledBrain(IControlledBrain controlledBrain)
        {
            if (ControlledBrain == controlledBrain)
                return SetControlledBrain(null);

            return false;
        }

        private bool SetControlledBrain(IControlledBrain controlledBrain)
        {
            try
            {
                CharacterClass.SetControlledBrain(controlledBrain);
                return true;
            }
            catch (Exception e)
            {
                if (log.IsErrorEnabled)
                    log.Error(e);

                return false;
            }
        }

        /// <summary>
        /// Releases controlled object
        /// (delegates to CharacterClass)
        /// </summary>
        public virtual void CommandNpcRelease()
        {
            CharacterClass.CommandNpcRelease();
        }

        /// <summary>
        /// Commands controlled object to attack
        /// </summary>
        public virtual void CommandNpcAttack()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null || !GameServer.ServerRules.IsAllowedToAttack(this, TargetObject as GameLiving, false))
                return;

            if (!IsWithinRadius(TargetObject, 2000))
                return;

            npc.Attack(TargetObject);
        }

        /// <summary>
        /// Commands controlled object to follow
        /// </summary>
        public virtual void CommandNpcFollow()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            npc.CheckAggressionStateOnPlayerOrder();
            npc.Disengage();
            npc.Follow(this);
        }

        /// <summary>
        /// Commands controlled object to stay where it is
        /// </summary>
        public virtual void CommandNpcStay()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            npc.CheckAggressionStateOnPlayerOrder();
            npc.Disengage();
            npc.Stay();
        }

        /// <summary>
        /// Commands controlled object to go to players location
        /// </summary>
        public virtual void CommandNpcComeHere()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            npc.CheckAggressionStateOnPlayerOrder();
            npc.Disengage();
            npc.ComeHere();
        }

        /// <summary>
        /// Commands controlled object to go to target
        /// </summary>
        public virtual void CommandNpcGoTarget()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            GameObject target = TargetObject;

            if (target == null)
                return;

            if (GetDistance(new Point2D(target.X, target.Y)) > 1250)
                return;

            npc.CheckAggressionStateOnPlayerOrder();
            npc.Disengage();
            npc.Goto(target);
        }

        /// <summary>
        /// Changes controlled object state to passive
        /// </summary>
        public virtual void CommandNpcPassive()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            npc.SetAggressionState(eAggressionState.Passive);
        }

        /// <summary>
        /// Changes controlled object state to aggressive
        /// </summary>
        public virtual void CommandNpcAgressive()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            npc.SetAggressionState(eAggressionState.Aggressive);
        }

        /// <summary>
        /// Changes controlled object state to defensive
        /// </summary>
        public virtual void CommandNpcDefensive()
        {
            IControlledBrain npc = ControlledBrain;

            if (npc == null)
                return;

            npc.SetAggressionState(eAggressionState.Defensive);
        }

        #endregion ControlledNpc

        // TODO: Add statistics and viewing the information. For now, used for IGamePlayer and some calls as needed.
        #region Statistics
        public void UpdateKillStatsOnPlayerKill(eRealm killedPlayerRealm, bool deathBlow, bool soloKill, int realmPointsEarned)
        {
        }

        public virtual int DeathsPvP
        {
            get => DBCharacter != null ? DBCharacter.DeathsPvP : 0;
            set
            {
                if (DBCharacter != null)
                    DBCharacter.DeathsPvP = value;
            }
        }

        /// <summary>
        /// Subtracts the current time from the last time the character was saved
        /// and adds it in the form of seconds to player.PlayedTime
        /// for the /played command.
        /// </summary>
        public long PlayedTime
        {
            get
            {
                DateTime rightNow = DateTime.Now;
                DateTime creation = CreationDate;

                TimeSpan newPlayed = rightNow.Subtract(creation);
                return (long)newPlayed.TotalSeconds;
            }
        }

        #endregion Statistics

        #region Bodyguard

        /// <summary>
        /// True, if the player has been standing still for at least 3 seconds,
        /// else false.
        /// </summary>
        //private bool IsStandingStill
        //{
        //    get
        //    {
        //        if (IsMoving)
        //            return false;

        //        long lastMovementTick = TempProperties.GetProperty<long>("PLAYERPOSITION_LASTMOVEMENTTICK");
        //        return (CurrentRegion.Time - lastMovementTick > 3000);
        //    }
        //}

        /// <summary>
        /// This player's bodyguard (ML ability) or null, if there is none.
        /// </summary>
        //public IGamePlayer Bodyguard
        //{
        //    get
        //    {
        //        var bodyguardEffects = EffectList.GetAllOfType<BodyguardEffect>();

        //        BodyguardEffect bodyguardEffect = bodyguardEffects.FirstOrDefault();
        //        if (bodyguardEffect == null || bodyguardEffect.GuardTarget != this)
        //            return null;

        //        IGamePlayer guard = bodyguardEffect.GuardSource;
        //        IGamePlayer guardee = this;

        //        return (guard.IsAlive && guard.IsWithinRadius(guardee, BodyguardAbilityHandler.BODYGUARD_DISTANCE) &&
        //                !guard.IsCasting && guardee.IsStandingStill)
        //            ? guard
        //            : null;
        //    }
        //}

        #endregion
    }
}
