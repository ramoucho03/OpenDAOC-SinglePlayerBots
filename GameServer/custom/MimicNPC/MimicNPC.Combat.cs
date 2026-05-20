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
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using static DOL.GS.AttackData;

namespace DOL.GS.Scripts
{
    // Combat surface of MimicNPC: weapon switching, attack/damage/armor maths,
    // death+rez-wait pipeline. Extracted from MimicNPC.cs to keep the core
    // file under control. Field & helper layout preserved verbatim.
    public partial class MimicNPC
    {

        public override bool BenefitsFromRelics => true;

        public override int CalculateCastingTime(SpellHandler spellHandler)
        {
            int castTime;

            if (!CharacterClass.CanChangeCastingSpeed(spellHandler.SpellLine, spellHandler.Spell))
                castTime = spellHandler.Spell.CastTime;
            else
                castTime = base.CalculateCastingTime(spellHandler);

            return castTime;
        }

        /// <summary>
        /// Gets/Sets safety flag
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool SafetyFlag
        {
            get { return DBCharacter != null ? DBCharacter.SafetyFlag : false; }
            set { if (DBCharacter != null) DBCharacter.SafetyFlag = value; }
        }

        /// <summary>
        /// Sets/gets the living's cloak hood state
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override bool IsCloakHoodUp
        {
            get { return base.IsCloakHoodUp; }
            set
            {
                base.IsCloakHoodUp = value;

                BroadcastLivingEquipmentUpdate();
            }
        }

        /// <summary>
        /// Sets/gets the living's cloak visible state
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override bool IsCloakInvisible
        {
            get
            {
                return DBCharacter != null ? DBCharacter.IsCloakInvisible : base.IsCloakInvisible;
            }
            set
            {
                //DBCharacter.IsCloakInvisible = value;

                //Out.SendInventoryItemsUpdate(null);
                UpdateEquipmentAppearance();

                //if (value)
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsCloakInvisible.Invisible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
                //else
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsCloakInvisible.Visible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
            }
        }

        /// <summary>
        /// Sets/gets the living's helm visible state
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override bool IsHelmInvisible
        {
            get
            {
                return DBCharacter != null ? DBCharacter.IsHelmInvisible : base.IsHelmInvisible;
            }
            set
            {
                //DBCharacter.IsHelmInvisible = value;

                //Out.SendInventoryItemsUpdate(null);
                UpdateEquipmentAppearance();

                //if (value)
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsHelmInvisible.Invisible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
                //else
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsHelmInvisible.Visible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
            }
        }

        /// <summary>
        /// Gets or sets the players SpellQueue option
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool SpellQueue
        {
            get { return DBCharacter != null ? DBCharacter.SpellQueue : false; }
            set { if (DBCharacter != null) DBCharacter.SpellQueue = value; }
        }

        /// <summary>
        /// Switches the active weapon to another one
        /// </summary>
        /// <param name="slot">the new eActiveWeaponSlot</param>
        public override void SwitchWeapon(eActiveWeaponSlot slot)
        {
            // Idempotent guard: switching to the slot we're already in must
            // be a no-op. Without it, any caller that re-asserts a weapon slot
            // every tick (archer range probes, minstrel/bard/skald song
            // upkeep, combat re-evaluation) re-ran the full switch body each
            // frame — StopCurrentSpellcast (cancels the in-flight cast), ends
            // every instrument pulse effect, and broadcasts a fresh equipment
            // appearance packet to all nearby clients. That packet flood is a
            // visible NPC "blink"; the cast/song cancellation broke caster and
            // bard bots outright.
            if (slot == ActiveWeaponSlot)
                return;

            if (attackComponent.AttackState)
                attackComponent.StopAttack();

            if (effectListComponent.ContainsEffectForEffectType(eEffect.Volley))
            {
                AtlasOF_VolleyECSEffect volley = EffectListService.GetEffectOnTarget(this, eEffect.Volley) as AtlasOF_VolleyECSEffect;
                volley?.OnPlayerSwitchedWeapon();
            }

            if (CurrentSpellHandler != null)
            {
                Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.SwitchWeapon.SpellCancelled"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                StopCurrentSpellcast();
            }

            foreach (Spell spell in ActivePulseSpells.Values)
            {
                if (spell.InstrumentRequirement != 0)
                {
                    ECSPulseEffect effect = EffectListService.GetPulseEffectOnTarget(this, spell);

                    if (effect != null)
                    {
                        Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.SwitchWeapon.SpellCancelled"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                        effect.End();
                    }
                }
            }

            DbInventoryItem rightHandItem = Inventory.GetItem(eInventorySlot.RightHandWeapon);
            DbInventoryItem leftHandItem = Inventory.GetItem(eInventorySlot.LeftHandWeapon);
            DbInventoryItem twoHandItem = Inventory.GetItem(eInventorySlot.TwoHandWeapon);
            DbInventoryItem distanceItem = Inventory.GetItem(eInventorySlot.DistanceWeapon);

            int mask = VisibleActiveWeaponSlots;
            bool wasRightActive = (mask & 0x0F) == 0;
            bool wasLeftActive = (mask & 0xF0) == 0x10;
            bool wasTwoHandActive = (mask & 0x0F) == 2;
            bool wasDistanceActive = (mask & 0x0F) == 3;

            base.SwitchWeapon(slot);

            mask = VisibleActiveWeaponSlots;
            bool isRightActive = (mask & 0x0F) == 0;
            bool isLeftActive = (mask & 0xF0) == 0x10;
            bool isTwoHandActive = (mask & 0x0F) == 2;
            bool isDistanceActive = (mask & 0x0F) == 3;

            if (rightHandItem != null)
            {
                if (wasRightActive && !isRightActive)
                    OnItemUnequipped(rightHandItem, (eInventorySlot)rightHandItem.SlotPosition);
                else if (!wasRightActive && isRightActive)
                    OnItemEquipped(rightHandItem, (eInventorySlot)rightHandItem.SlotPosition);
            }

            if (leftHandItem != null)
            {
                if (wasLeftActive && !isLeftActive)
                    OnItemUnequipped(leftHandItem, (eInventorySlot)leftHandItem.SlotPosition);
                else if (!wasLeftActive && isLeftActive)
                    OnItemEquipped(leftHandItem, (eInventorySlot)leftHandItem.SlotPosition);
            }

            if (twoHandItem != null)
            {
                if (wasTwoHandActive && !isTwoHandActive)
                    OnItemUnequipped(twoHandItem, (eInventorySlot)twoHandItem.SlotPosition);
                else if (!wasTwoHandActive && isTwoHandActive)
                    OnItemEquipped(twoHandItem, (eInventorySlot)twoHandItem.SlotPosition);
            }

            if (distanceItem != null)
            {
                if (wasDistanceActive && !isDistanceActive)
                    OnItemUnequipped(distanceItem, (eInventorySlot)distanceItem.SlotPosition);
                else if (!wasDistanceActive && isDistanceActive)
                    OnItemEquipped(distanceItem, (eInventorySlot)distanceItem.SlotPosition);
            }

            if (ObjectState == eObjectState.Active)
                UpdateNPCEquipmentAppearance();
        }

        /// <summary>
        /// Switches the active quiver slot to another one
        /// </summary>
        public virtual void SwitchQuiver(eActiveQuiverSlot slot, bool forced)
        {
            eActiveQuiverSlot currentActiveQuiverSlot = rangeAttackComponent.ActiveQuiverSlot;

            if (slot is not eActiveQuiverSlot.None)
            {
                eInventorySlot updatedSlot = eInventorySlot.Invalid;

                if ((slot & eActiveQuiverSlot.Fourth) > 0)
                    updatedSlot = eInventorySlot.FourthQuiver;
                else if ((slot & eActiveQuiverSlot.Third) > 0)
                    updatedSlot = eInventorySlot.ThirdQuiver;
                else if ((slot & eActiveQuiverSlot.Second) > 0)
                    updatedSlot = eInventorySlot.SecondQuiver;
                else if ((slot & eActiveQuiverSlot.First) > 0)
                    updatedSlot = eInventorySlot.FirstQuiver;

                if (Inventory.GetItem(updatedSlot) != null && (rangeAttackComponent.ActiveQuiverSlot != slot || forced))
                {
                    if (currentActiveQuiverSlot != slot)
                        rangeAttackComponent.ActiveQuiverSlot = slot;
                }
                else if (currentActiveQuiverSlot != slot)
                    rangeAttackComponent.ActiveQuiverSlot = eActiveQuiverSlot.None;

                //Out.SendInventorySlotsUpdate([updatedSlot]);
            }
            else
            {
                if (Inventory.GetItem(eInventorySlot.FirstQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.First, true);
                else if (Inventory.GetItem(eInventorySlot.SecondQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.Second, true);
                else if (Inventory.GetItem(eInventorySlot.ThirdQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.Third, true);
                else if (Inventory.GetItem(eInventorySlot.FourthQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.Fourth, true);
                else if (currentActiveQuiverSlot != slot)
                {
                    rangeAttackComponent.ActiveQuiverSlot = eActiveQuiverSlot.None;
                    //Out.SendInventorySlotsUpdate(null);
                }
            }
        }

        /// <summary>
        /// This method is called at the end of the attack sequence to
        /// notify objects if they have been attacked/hit by an attack
        /// </summary>
        /// <param name="ad">information about the attack</param>
        public override void OnAttackedByEnemy(AttackData ad)
        {
            base.OnAttackedByEnemy(ad);
            MimicBrain.OnAttackedByEnemy(ad);

            if (ControlledBrain != null && ControlledBrain is ControlledMobBrain brain)
                brain.OnOwnerAttacked(ad);
        }

        public override bool CanDropLoot => false;

        public override void TakeDamage(GameObject source, eDamageType damageType, int damageAmount, int criticalAmount)
        {
            if (Duel != null && !IsDuelPartner(source as GameLiving))
                Duel.Stop();

            base.TakeDamage(source, damageType, damageAmount, criticalAmount);

            if (HasAbility(GS.Abilities.DefensiveCombatPowerRegeneration))
                Mana += (int)((damageAmount + criticalAmount) * 0.25);
        }

        public override int MeleeAttackRange
        {
            get
            {
                int range = 128;

                if (TargetObject is GameKeepComponent)
                    range += 150;
                else
                {
                    if (TargetObject is GameLiving target && target.IsMoving)
                        range += 32;

                    if (IsMoving)
                        range += 32;
                }

                return range;
            }
        }

        /// <summary>
        /// Gets the effective AF of this living.  This is used for the overall AF display
        /// on the character but not used in any damage equations.
        /// </summary>
        public override int EffectiveOverallAF
        {
            get
            {
                int eaf = 0;
                int abs = 0;
                foreach (DbInventoryItem item in Inventory.VisibleItems)
                {
                    double factor = 0;
                    switch (item.Item_Type)
                    {
                        case Slot.TORSO:
                        factor = 2.2;
                        break;

                        case Slot.LEGS:
                        factor = 1.3;
                        break;

                        case Slot.ARMS:
                        factor = 0.75;
                        break;

                        case Slot.HELM:
                        factor = 0.5;
                        break;

                        case Slot.HANDS:
                        factor = 0.25;
                        break;

                        case Slot.FEET:
                        factor = 0.25;
                        break;
                    }

                    int itemAFCap = Level << 1;
                    if (RealmLevel > 39)
                        itemAFCap += 2;
                    switch ((eObjectType)item.Object_Type)
                    {
                        case eObjectType.Cloth:
                        abs = 0;
                        itemAFCap >>= 1;
                        break;

                        case eObjectType.Leather:
                        abs = 10;
                        break;

                        case eObjectType.Reinforced:
                        abs = 19;
                        break;

                        case eObjectType.Studded:
                        abs = 19;
                        break;

                        case eObjectType.Scale:
                        abs = 27;
                        break;

                        case eObjectType.Chain:
                        abs = 27;
                        break;

                        case eObjectType.Plate:
                        abs = 34;
                        break;
                    }

                    if (factor > 0)
                    {
                        int af = item.DPS_AF;
                        if (af > itemAFCap)
                            af = itemAFCap;
                        double piece_eaf = af * item.Quality / 100.0 * item.ConditionPercent / 100.0 * (1 + abs / 100.0);
                        eaf += (int)(piece_eaf * factor);
                    }
                }

                // Overall AF CAP = 10 * level * (1 + abs%/100)
                int bestLevel = -1;
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("AlbArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("HibArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("MidArmor"));

                switch (bestLevel)
                {
                    default: abs = 0; break; // cloth etc
                    case ArmorLevel.Leather: abs = 10; break;
                    case ArmorLevel.Studded: abs = 19; break;
                    case ArmorLevel.Chain: abs = 27; break;
                    case ArmorLevel.Plate: abs = 34; break;
                }

                eaf += BaseBuffBonusCategory[eProperty.ArmorFactor]; // base buff before cap
                int eafcap = (int)(10 * Level * (1 + abs * 0.01));
                if (eaf > eafcap)
                    eaf = eafcap;
                eaf += (int)Math.Min(Level * 1.875, SpecBuffBonusCategory[eProperty.ArmorFactor])
                       - DebuffCategory[eProperty.ArmorFactor]
                       + OtherBonus[eProperty.ArmorFactor]
                       + Math.Min(Level, ItemBonus[eProperty.ArmorFactor]);

                eaf = (int)(eaf * BuffBonusMultCategory1.Get((int)eProperty.ArmorFactor));

                return eaf;
            }
        }

        /// <summary>
        /// Calc Armor hit location when player is hit by enemy
        /// </summary>
        /// <returns>slotnumber where enemy hits</returns>
        /// attackdata(ad) changed
        public virtual eArmorSlot CalculateArmorHitLocation(AttackData ad)
        {
            if (ad.Style != null)
            {
                if (ad.Style.ArmorHitLocation != eArmorSlot.NOTSET)
                    return ad.Style.ArmorHitLocation;
            }

            int chancehit = Util.Random(1, 100);

            if (chancehit <= 40)
            {
                return eArmorSlot.TORSO;
            }
            else if (chancehit <= 65)
            {
                return eArmorSlot.LEGS;
            }
            else if (chancehit <= 80)
            {
                return eArmorSlot.ARMS;
            }
            else if (chancehit <= 90)
            {
                return eArmorSlot.HEAD;
            }
            else if (chancehit <= 95)
            {
                return eArmorSlot.HAND;
            }
            else
            {
                return eArmorSlot.FEET;
            }
        }

        public override int WeaponSpecLevel(eObjectType objectType, int slotPosition)
        {
            // Use axe spec if left hand axe is not in the left hand slot.
            if (objectType is eObjectType.LeftAxe && slotPosition is not Slot.LEFTHAND)
                return GameServer.ServerRules.GetObjectSpecLevel(this, eObjectType.Axe);

            // Use left axe spec if axe is in the left hand slot.
            if (slotPosition is Slot.LEFTHAND && objectType is eObjectType.Axe)
                return GameServer.ServerRules.GetObjectSpecLevel(this, eObjectType.LeftAxe);

            return GameServer.ServerRules.GetObjectSpecLevel(this, objectType);
        }

        /// <summary>
        /// determines current weaponspeclevel
        /// </summary>
        public override int WeaponSpecLevel(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            return WeaponSpecLevel((eObjectType)weapon.Object_Type, weapon.SlotPosition);
        }

        protected Hashtable m_compatibleObjectTypes = null;

        /// <summary>
        /// Translates object type to compatible object types based on server type
        /// </summary>
        /// <param name="objectType">The object type</param>
        /// <returns>An array of compatible object types</returns>
        protected virtual eObjectType[] GetCompatibleObjectTypes(eObjectType objectType)
        {
            if (m_compatibleObjectTypes == null)
            {
                m_compatibleObjectTypes = new Hashtable();
                m_compatibleObjectTypes[(int)eObjectType.Staff] = new eObjectType[] { eObjectType.Staff };
                m_compatibleObjectTypes[(int)eObjectType.Fired] = new eObjectType[] { eObjectType.Fired };

                m_compatibleObjectTypes[(int)eObjectType.FistWraps] = new eObjectType[] { eObjectType.FistWraps };
                m_compatibleObjectTypes[(int)eObjectType.MaulerStaff] = new eObjectType[] { eObjectType.MaulerStaff };

                //alb
                m_compatibleObjectTypes[(int)eObjectType.CrushingWeapon] = new eObjectType[] { eObjectType.CrushingWeapon, eObjectType.Blunt, eObjectType.Hammer };
                m_compatibleObjectTypes[(int)eObjectType.SlashingWeapon] = new eObjectType[] { eObjectType.SlashingWeapon, eObjectType.Blades, eObjectType.Sword, eObjectType.Axe };
                m_compatibleObjectTypes[(int)eObjectType.ThrustWeapon] = new eObjectType[] { eObjectType.ThrustWeapon, eObjectType.Piercing };
                m_compatibleObjectTypes[(int)eObjectType.TwoHandedWeapon] = new eObjectType[] { eObjectType.TwoHandedWeapon, eObjectType.LargeWeapons };
                m_compatibleObjectTypes[(int)eObjectType.PolearmWeapon] = new eObjectType[] { eObjectType.PolearmWeapon, eObjectType.CelticSpear, eObjectType.Spear };
                m_compatibleObjectTypes[(int)eObjectType.Flexible] = new eObjectType[] { eObjectType.Flexible };
                m_compatibleObjectTypes[(int)eObjectType.Longbow] = new eObjectType[] { eObjectType.Longbow };
                m_compatibleObjectTypes[(int)eObjectType.Crossbow] = new eObjectType[] { eObjectType.Crossbow };
                //TODO: case 5: abilityCheck = Abilities.Weapon_Thrown; break;

                //mid
                m_compatibleObjectTypes[(int)eObjectType.Hammer] = new eObjectType[] { eObjectType.Hammer, eObjectType.CrushingWeapon, eObjectType.Blunt };
                m_compatibleObjectTypes[(int)eObjectType.Sword] = new eObjectType[] { eObjectType.Sword, eObjectType.SlashingWeapon, eObjectType.Blades };
                m_compatibleObjectTypes[(int)eObjectType.LeftAxe] = new eObjectType[] { eObjectType.LeftAxe };
                m_compatibleObjectTypes[(int)eObjectType.Axe] = new eObjectType[] { eObjectType.Axe, eObjectType.SlashingWeapon, eObjectType.Blades }; //eObjectType.LeftAxe removed
                m_compatibleObjectTypes[(int)eObjectType.HandToHand] = new eObjectType[] { eObjectType.HandToHand };
                m_compatibleObjectTypes[(int)eObjectType.Spear] = new eObjectType[] { eObjectType.Spear, eObjectType.CelticSpear, eObjectType.PolearmWeapon };
                m_compatibleObjectTypes[(int)eObjectType.CompositeBow] = new eObjectType[] { eObjectType.CompositeBow };
                m_compatibleObjectTypes[(int)eObjectType.Thrown] = new eObjectType[] { eObjectType.Thrown };

                //hib
                m_compatibleObjectTypes[(int)eObjectType.Blunt] = new eObjectType[] { eObjectType.Blunt, eObjectType.CrushingWeapon, eObjectType.Hammer };
                m_compatibleObjectTypes[(int)eObjectType.Blades] = new eObjectType[] { eObjectType.Blades, eObjectType.SlashingWeapon, eObjectType.Sword, eObjectType.Axe };
                m_compatibleObjectTypes[(int)eObjectType.Piercing] = new eObjectType[] { eObjectType.Piercing, eObjectType.ThrustWeapon };
                m_compatibleObjectTypes[(int)eObjectType.LargeWeapons] = new eObjectType[] { eObjectType.LargeWeapons, eObjectType.TwoHandedWeapon };
                m_compatibleObjectTypes[(int)eObjectType.CelticSpear] = new eObjectType[] { eObjectType.CelticSpear, eObjectType.Spear, eObjectType.PolearmWeapon };
                m_compatibleObjectTypes[(int)eObjectType.Scythe] = new eObjectType[] { eObjectType.Scythe };
                m_compatibleObjectTypes[(int)eObjectType.RecurvedBow] = new eObjectType[] { eObjectType.RecurvedBow };

                m_compatibleObjectTypes[(int)eObjectType.Shield] = new eObjectType[] { eObjectType.Shield };
                m_compatibleObjectTypes[(int)eObjectType.Poison] = new eObjectType[] { eObjectType.Poison };
                //TODO: case 45: abilityCheck = Abilities.instruments; break;
            }

            eObjectType[] res = (eObjectType[])m_compatibleObjectTypes[(int)objectType];
            if (res == null)
                return new eObjectType[0];
            return res;
        }

        /// <summary>
        /// determines current weaponspeclevel
        /// </summary>
        public int WeaponBaseSpecLevel(eObjectType objectType, int slotPosition)
        {
            // Use axe spec if left hand axe is not in the left hand slot.
            if (objectType is eObjectType.LeftAxe && slotPosition is not Slot.LEFTHAND)
                return GameServer.ServerRules.GetObjectBaseSpecLevel(this, eObjectType.Axe);

            // Use left axe spec if axe is in the left hand slot.
            if (slotPosition is Slot.LEFTHAND && objectType is eObjectType.Axe)
                return GameServer.ServerRules.GetObjectBaseSpecLevel(this, eObjectType.LeftAxe);

            return GameServer.ServerRules.GetObjectBaseSpecLevel(this, objectType);
        }

        /// <summary>
        /// determines current weaponspeclevel
        /// </summary>
        public int WeaponBaseSpecLevel(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            return WeaponBaseSpecLevel((eObjectType)weapon.Object_Type, weapon.SlotPosition);
        }

        /// <summary>
        /// Gets the weaponskill of weapon
        /// </summary>
        public override double GetWeaponSkill(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            int classBaseWeaponSkill = (eInventorySlot)weapon.SlotPosition is eInventorySlot.DistanceWeapon ? CharacterClass.WeaponSkillRangedBase : CharacterClass.WeaponSkillBase;
            double weaponSkill = Level * classBaseWeaponSkill / 200.0 * (1 + 0.01 * GetWeaponStat(weapon) / 2) * Effectiveness;
            return Math.Max(1, weaponSkill * GetModified(eProperty.WeaponSkill) * 0.01);
        }

        /// <summary>
        /// calculates weapon stat
        /// </summary>
        /// <param name="weapon"></param>
        /// <returns></returns>
        public override int GetWeaponStat(DbInventoryItem weapon)
        {
            if (weapon != null)
            {
                switch ((eObjectType)weapon.Object_Type)
                {
                    // DEX modifier
                    case eObjectType.Staff:
                    case eObjectType.Fired:
                    case eObjectType.Longbow:
                    case eObjectType.Crossbow:
                    case eObjectType.CompositeBow:
                    case eObjectType.RecurvedBow:
                    case eObjectType.Thrown:
                    case eObjectType.Shield:
                    return GetModified(eProperty.Dexterity);

                    // STR+DEX modifier
                    case eObjectType.ThrustWeapon:
                    case eObjectType.Piercing:
                    case eObjectType.Spear:
                    case eObjectType.Flexible:
                    case eObjectType.HandToHand:
                    return (GetModified(eProperty.Strength) + GetModified(eProperty.Dexterity)) >> 1;
                }
            }
            // STR modifier for others
            return GetModified(eProperty.Strength);
        }

        public int GetArmorFactorCap(eObjectType type, out int itemArmorFactorCap)
        {
            if (!GlobalConstants.IsArmor((int)type))
                throw new ArgumentException($"{nameof(type)} must be an armor type");

            int modifiedCharacterLevel = Level;

            if (RealmLevel > 39)
                modifiedCharacterLevel++;

            // Returns two caps:
            // * One for player AF, which is twice the modified character level and is meant to be applied after base AF buffs.
            // * One for the item AF, which depends on the armor type and is meant to be applied first.
            int playerArmorFactorCap = modifiedCharacterLevel * 2;
            itemArmorFactorCap = type is eObjectType.Cloth ? modifiedCharacterLevel : playerArmorFactorCap;
            return playerArmorFactorCap;
        }

        /// <summary>
        /// calculate item armor factor influenced by quality, con and duration
        /// </summary>
        public override double GetArmorAF(eArmorSlot slot)
        {
            if (slot is eArmorSlot.NOTSET)
                return 0;

            DbInventoryItem item = Inventory.GetItem((eInventorySlot)slot);

            if (item == null)
                return 0;

            int armorFactorCap = GetArmorFactorCap((eObjectType)item.Object_Type, out int itemArmorFactorCap);
            double armorFactor = Math.Min(item.DPS_AF, itemArmorFactorCap); // Cap item AF first.
            armorFactor += BaseBuffBonusCategory[eProperty.ArmorFactor] / 5.0; // Base AF buffs need to be applied manually for players.
            armorFactor *= item.Quality * 0.01 * item.ConditionPercent * 0.01; // Apply condition and quality before the second cap. Maybe incorrect, but it makes base AF buffs a little more useful.
            armorFactor = Math.Min(armorFactor, armorFactorCap);
            armorFactor += GetModified(eProperty.ArmorFactor) / 5.0; // Don't call base here.

            /*GameSpellEffect effect = SpellHandler.FindEffectOnTarget(this, typeof(VampiirArmorDebuff));
            if (effect != null && slot == (effect.SpellHandler as VampiirArmorDebuff).Slot)
                armorFactor -= (int) (effect.SpellHandler as VampiirArmorDebuff).Spell.Value;*/

            return Math.Max(0, armorFactor);
        }

        /// <summary>
        /// Calculates armor absorb level
        /// </summary>
        public override double GetArmorAbsorb(eArmorSlot slot)
        {
            if (slot is eArmorSlot.NOTSET)
                return 0;

            DbInventoryItem item = Inventory.GetItem((eInventorySlot)slot);

            if (item == null)
                return 0;

            // Debuffs can't lower absorb below 0%: https://darkageofcamelot.com/article/friday-grab-bag-08302019
            // ABS debuffs can either be multiplicative (if appended with a minus sign) or flat. In 1.65, all debuffs are believed to be multiplicative.
            double absorb = item.SPD_ABS * 0.01 * (1 + GetModified(eProperty.ArmorAbsorption) * 0.01);
            return Math.Clamp(absorb, 0, 1);
        }

        /// <summary>
        /// Weaponskill thats shown to the player
        /// </summary>
        public int GetDisplayedWeaponSkill()
        {
            DbInventoryItem weapon = ActiveWeapon;

            if (weapon == null)
                return 0;

            int trainedSpec = WeaponBaseSpecLevel(weapon);
            int itemBonus = 0;
            int weaponSpec = 0;

            if (trainedSpec > 0)
            {
                string specName = SkillBase.ObjectTypeToSpec((eObjectType)weapon.Object_Type);

                if (specName != null)
                    itemBonus = GetModifiedFromItems(SkillBase.SpecToSkill(specName));

                int realmBonus = RealmLevel / 10;
                weaponSpec = itemBonus + realmBonus + trainedSpec;
            }

            int baseWeaponSkill = (eInventorySlot)weapon.SlotPosition is eInventorySlot.DistanceWeapon ? CharacterClass.WeaponSkillRangedBase : CharacterClass.WeaponSkillBase;
            int stat = GetWeaponStat(weapon) & ~1; // Live rounds down to the closest even number on both Str and Dex, then on the result. It also adds a penalty when a stat is <50.
            int damageTable = Level * baseWeaponSkill / 20;

            double damageWithBonus = Math.Floor(damageTable * (200 + itemBonus) / 500.0);
            double damageWithStat = Math.Floor(damageWithBonus * (100 + (stat - 50) / 2.0) / 100.0);
            double damageWithSpec = Math.Floor(damageWithStat * (100 + weaponSpec) / 100.0);
            return (int)Math.Floor(damageWithSpec * GetModified(eProperty.WeaponSkill) * 0.01);
        }

        //// <summary>
        /// Gets the weapondamage of currently used weapon
        /// Used to display weapon damage in stats, 16.5dps = 1650
        /// </summary>
        /// <param name="weapon">the weapon used for attack</param>
        public override double WeaponDamage(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            return WeaponDamageWithoutQualityAndCondition(weapon) * attackComponent.GetWeaponQualityConditionModifier(weapon);
        }

        public double GetWeaponDpsCap()
        {
            double dpsCap = 1.2 + 0.3 * Level;

            if (RealmLevel > 39)
                dpsCap += 0.3;

            return dpsCap;
        }

        public double WeaponDamageWithoutQualityAndCondition(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            // Apply dps cap before quality and condition.
            // http://www.classesofcamelot.com/faq.asp?mode=view&cat=10
            double weaponDps = weapon.DPS_AF * 0.1;
            double dps = Math.Min(weaponDps, GetWeaponDpsCap());
            dps *= 1 + GetModified(eProperty.DPS) * 0.01;
            return dps;
        }

        public override bool CanCastWhileAttacking()
        {
            switch (CharacterClass)
            {
                case ClassVampiir:
                case ClassMaulerAlb:
                case ClassMaulerMid:
                case ClassMaulerHib:
                return true;
            }

            return false;
        }

        public override void AddXPGainer(GameLiving xpGainer, double damageAmount)
        {
            // In case a player is attacked by a player of the same realm (e.g. in a duel, due to a confusion spell, a bug, etc.).
            // This also means the amount of damage dealt by the xp gainer won't be taken into account when awarding XP, RPs, BPs.
            if (xpGainer.Realm == Realm)
                return;

            base.AddXPGainer(xpGainer, damageAmount);
        }

        /// <summary>
        /// Stores the amount of realm points gained by other players on last death
        /// </summary>
        protected long m_lastDeathRealmPoints;

        /// <summary>
        /// Gets/sets the amount of realm points gained by other players on last death
        /// </summary>
        public long LastDeathRealmPoints
        {
            get { return m_lastDeathRealmPoints; }
            set { m_lastDeathRealmPoints = value; }
        }

        /// <summary>
        /// Called when the player dies
        /// </summary>
        /// <param name="killer">the killer</param>
        // ===== Rez-wait state =====
        // When a mimic dies in a player's group, we keep its "corpse" in the world
        // for a short window so a Cleric/Druid/Healer/Friar/Warden can resurrect it.
        // The bot stays in the group during that window. If no rez arrives in time,
        // the corpse is removed (Delete) and the bot leaves the group.

        // Window when a rezzer IS available in the group. Sourced from
        // MimicConfig.BOT_REZ_WAIT_SECONDS at use-time so operators can
        // tune it live via a server property without recompiling.
        private static int REZ_WAIT_MS => Math.Max(1, MimicConfig.BOT_REZ_WAIT_SECONDS) * 1000;
        // Auto-release window when no group member can rez. Same tuning model.
        private static int REZ_WAIT_NO_HEALER_MS => Math.Max(1, MimicConfig.BOT_REZ_WAIT_NO_HEALER_SECONDS) * 1000;

        private ECSGameTimer _rezWaitTimer;
        private bool _inRezWait;

        public bool InRezWait => _inRezWait;

        /// <summary>
        /// True if any living member of the bot's current group has a Resurrect-type spell
        /// available. Used to decide whether the corpse should linger waiting for rez,
        /// or release immediately because no rez is possible.
        /// </summary>
        private bool GroupHasRezzer()
        {
            if (Group == null)
                return false;

            foreach (GameLiving member in Group.GetMembersInTheGroup())
            {
                if (member == null || !member.IsAlive || member == this)
                    continue;

                // Real player: scan their spell lines for any Resurrect spell.
                if (member is GamePlayer p)
                {
                    foreach (var line in p.GetSpellLines())
                    {
                        foreach (Spell sp in SkillBase.GetSpellList(line.KeyName))
                        {
                            if (sp.SpellType == eSpellType.Resurrect)
                                return true;
                        }
                    }
                }

                // Other mimic: scan its misc / heal spells (Resurrect is classified as misc).
                if (member is MimicNPC m)
                {
                    if (m.MiscSpells != null && m.MiscSpells.Any(s => s != null && s.SpellType == eSpellType.Resurrect))
                        return true;
                    if (m.InstantMiscSpells != null && m.InstantMiscSpells.Any(s => s != null && s.SpellType == eSpellType.Resurrect))
                        return true;
                    if (m.Spells != null && m.Spells.Any(s => s != null && s.SpellType == eSpellType.Resurrect))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Called by ResurrectSpellHandler when a rez spell lands on this bot.
        /// Restores health/mana/end and pulls the bot back into the world fully alive.
        /// </summary>
        public void OnResurrected(GameLiving rezzer, Spell rezSpell)
        {
            if (IsAlive)
                return;

            _rezWaitTimer?.Stop();
            _rezWaitTimer = null;
            _inRezWait = false;

            // Restore vitals per the rez spell's percentages.
            int healthPct = rezSpell != null && rezSpell.ResurrectHealth > 0 ? rezSpell.ResurrectHealth : 50;
            int manaPct = rezSpell != null && rezSpell.ResurrectMana > 0 ? rezSpell.ResurrectMana : 50;

            Health = Math.Max(1, MaxHealth * healthPct / 100);
            Mana = MaxMana * manaPct / 100;
            Endurance = MaxEndurance * manaPct / 100;

            // Move next to the rezzer so we don't revive in the middle of mobs.
            if (rezzer != null && rezzer.CurrentRegionID == CurrentRegionID)
                MoveTo(rezzer.CurrentRegionID, rezzer.X, rezzer.Y, rezzer.Z, rezzer.Heading);

            // Restart regen + brain.
            StartHealthRegeneration();
            StartPowerRegeneration();
            StartEnduranceRegeneration();

            // If the group is camping, the rezzed bot belongs at the camp,
            // not at its old spawn point. Reset SpawnPoint so WAKING_UP/CAMP
            // transitions don't ping-pong it back to where it died.
            if (Group?.MimicGroup?.CampPoint is Point3D camp)
            {
                SpawnPoint = new Point3D(camp);
                MimicBrain?.FSM.SetCurrentState(eFSMStateType.CAMP);
            }
            else
            {
                MimicBrain?.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
            }

            if (rezzer is GamePlayer rezzerPlayer)
                rezzerPlayer.Out.SendMessage($"You have resurrected {GetName(0, false)}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        private int OnRezWaitExpired(ECSGameTimer timer)
        {
            // Bail if the rez landed in the same tick or if the bot has been
            // deleted/teardown. Also bail if it's already alive — covers the
            // narrow race between OnResurrected and the timer firing.
            if (!_inRezWait || IsAlive || ObjectState != eObjectState.Active)
                return 0;

            _inRezWait = false;
            _rezWaitTimer = null;

            // What happens when no rez arrived in time is controlled by the
            // BOT_REZ_TIMEOUT_BEHAVIOR server property:
            //   - "release" (default): act like a player who has no bind to
            //     return to — leave the group and despawn the corpse. The
            //     player can /mlfg or /mcreate a fresh bot afterwards. This
            //     is the realistic option asked for by operators who want
            //     bots to feel like real party members.
            //   - "revive": teleport the bot back to its owner at half
            //     vitals, keep it in the group. Less realistic but
            //     friendlier on long PvE runs. This was the pre-existing
            //     behaviour, kept for backwards compatibility.
            string mode = (MimicConfig.BOT_REZ_TIMEOUT_BEHAVIOR ?? "release").Trim().ToLowerInvariant();

            if (mode == "revive")
            {
                GamePlayer owner = FindLiveOwnerOrLeader();

                if (owner != null && owner.IsAlive && owner.CurrentRegionID > 0)
                {
                    ReviveAtOwner(owner);
                    return 0;
                }
                // No live owner: fall through to release semantics so the
                // corpse doesn't linger forever.
            }

            // "release" path. Announce to the group via chat (best-effort,
            // localized per recipient) then leave + delete the corpse.
            AnnounceReleaseToGroup();

            // Mark _beingDeleted BEFORE RemoveMember. RemoveMember fires
            // GroupEvent.MemberDisbanded, which MimicManager.OnGroupMember
            // Disbanded handles — and that handler calls mimic.Delete() when
            // it sees the bot leave a group "unexpectedly". Without the flag
            // set first, the handler ran a Delete(), then this method ran a
            // SECOND Delete() below: a double-delete that flickered the bot
            // (group-window removal + world removal as two separate events).
            // The flag tells the disband handler "I'm already tearing down".
            _beingDeleted = true;
            Group?.RemoveMember(this);
            // Untrack only when the corpse is actually being deleted. This
            // was previously fired unconditionally before the rez-wait
            // branch in ProcessDeath, so a successful rez restored the bot
            // but left the spawner without a reference (no cleanup, no
            // /madmin visibility, no auto-respawn hook).
            MimicSpawner?.Remove(this);
            Delete();
            return 0;
        }

        /// <summary>
        /// Sends a localized "I'm releasing to bind" line to every player
        /// in the group, right before the bot leaves and the corpse is
        /// deleted. Best-effort: a missing translation falls back to the
        /// key, a missing group quietly skips the broadcast.
        /// </summary>
        private void AnnounceReleaseToGroup()
        {
            if (Group == null)
                return;

            string fromName = GetName(0, true);
            string[] keys = new[]
            {
                "Mimic.Chat.ReleaseToBind.1",
                "Mimic.Chat.ReleaseToBind.2",
                "Mimic.Chat.ReleaseToBind.3",
            };
            string pickedKey = keys[Util.Random(keys.Length - 1)];

            foreach (GamePlayer player in Group.GetPlayersInTheGroup())
            {
                string lang = player.Client?.Account?.Language;
                string text = LanguageMgr.TryGetTranslation(out string t, lang, pickedKey) ? t : pickedKey;
                player.Out.SendMessage($"[Party] {fromName}: \"{text}\"", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
            }
        }

        /// <summary>
        /// Locates the live player owner of this bot, or, failing that, the
        /// group's live living leader (which might be another player). Returns
        /// null if neither is in a viable state.
        /// </summary>
        private GamePlayer FindLiveOwnerOrLeader()
        {
            if (!string.IsNullOrEmpty(OwnerAccount))
            {
                foreach (GameClient c in ClientService.Instance.GetClients())
                {
                    if (c?.Account != null
                        && string.Equals(c.Account.Name, OwnerAccount, StringComparison.Ordinal)
                        && c.Player != null
                        && c.Player.IsAlive)
                        return c.Player;
                }
            }

            if (Group?.LivingLeader is GamePlayer leader && leader.IsAlive)
                return leader;

            return null;
        }

        /// <summary>
        /// "Release and return" path. The bot revives at reduced vitals (50%)
        /// next to the owner, regen restarts, and the FSM is reset so the bot
        /// resumes follow / aggro normally. Stays in the group, so the player
        /// doesn't have to re-invite or recreate it.
        /// </summary>
        private void ReviveAtOwner(GamePlayer owner)
        {
            // Half health/mana/endurance as a soft penalty for not getting rezzed.
            Health = Math.Max(1, MaxHealth / 2);
            Mana = MaxMana / 2;
            Endurance = MaxEndurance / 2;

            MoveTo(owner.CurrentRegionID, owner.X, owner.Y, owner.Z, owner.Heading);

            StartHealthRegeneration();
            StartPowerRegeneration();
            StartEnduranceRegeneration();

            MimicBrain?.FSM.SetCurrentState(eFSMStateType.WAKING_UP);

            owner.Out.SendMessage(
                $"{GetName(0, true)} returns to your side after releasing.",
                eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        public override void ProcessDeath(GameObject killer)
        {
            // Ambient trigger upon killing player
            if (killer is GameNPC && killer is not MimicNPC)
                (killer as GameNPC).FireAmbientSentence(GameNPC.eAmbientTrigger.killing, killer as GameLiving);

            CharacterClass.Die(killer);

            bool killingBlowByEnemyRealm = killer != null && killer.Realm != eRealm.None && killer.Realm != Realm;

            TargetObject = null;

            if (IsOnHorse)
                IsOnHorse = false;

            string publicMessage;
            ushort messageDistance = WorldMgr.DEATH_MESSAGE_DISTANCE;
            eReleaseType m_releaseType = eReleaseType.Normal;

            string location = string.Empty;
            if (CurrentAreas.Count > 0 && (CurrentAreas[0] is Area.BindArea) == false)
                location = (CurrentAreas[0] as AbstractArea).Description;
            else
                // CurrentZone can be null for bots spawned outside any defined zone
                // (off-grid spawnpoints, instance edge cases). Fallback to region name
                // so ProcessDeath doesn't NRE on the death message construction.
                location = CurrentZone?.Description ?? CurrentRegion?.Description ?? "an unknown place";

            if (killer == null)
            {
                if (killingBlowByEnemyRealm)
                    publicMessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.KilledLocation", GetName(0, true), location);
                else
                    publicMessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.Killed", GetName(0, true));
            }
            else
            {
                if (IsDuelPartner(killer as GameLiving))
                {
                    m_releaseType = eReleaseType.Duel;
                    messageDistance = WorldMgr.YELL_DISTANCE;
                    publicMessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.DuelDefeated", GetName(0, true), killer.GetName(1, false));
                }
                else
                {
                    messageDistance = 0;
                    if (killingBlowByEnemyRealm)
                        publicMessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.KilledByLocation", GetName(0, true), killer.GetName(1, false), location);
                    else
                        publicMessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.KilledBy", GetName(0, true), killer.GetName(1, false));
                }
            }

            Duel?.Stop();
            // MimicSpawner.Remove is deferred to the actual corpse-deletion
            // sites (base.ProcessDeath path and OnRezWaitExpired) so a bot
            // that gets resurrected during the rez wait stays tracked.

            eChatType messageType;
            if (m_releaseType == eReleaseType.Duel)
                messageType = eChatType.CT_Emote;
            else if (killer == null)
            {
                messageType = eChatType.CT_OthersDeath;
            }
            else
            {
                switch (killer.Realm)
                {
                    case eRealm.Albion: messageType = eChatType.CT_KilledByAlb; break;
                    case eRealm.Midgard: messageType = eChatType.CT_KilledByMid; break;
                    case eRealm.Hibernia: messageType = eChatType.CT_KilledByHib; break;
                    default: messageType = eChatType.CT_OthersDeath; break; // killed by mob
                }
            }

            List<GamePlayer> players;

            if (messageDistance == 0)
                players = ClientService.Instance.GetPlayersOfRegion(CurrentRegion);
            else
                players = GetPlayersInRadius(messageDistance);

            foreach (GamePlayer player in players)
            {
                // on normal server type send messages only to the killer and dead players realm
                // check for gameplayer is needed because killers realm don't see deaths by guards
                if (
                    (player != killer) && (
                        (killer != null && killer is IGamePlayer && GameServer.ServerRules.IsSameRealm((GameLiving)killer, player, true))
                        || (GameServer.ServerRules.IsSameRealm(this, player, true))
                        || (Properties.DEATH_MESSAGES_ALL_REALMS && (killer is IGamePlayer || killer is GameKeepGuard))) //Only show Player/Guard kills if shown to all realms
                )
                     player.Out.SendMessage(publicMessage, messageType, eChatLoc.CL_SystemWindow);
            }

            //Dead ppl. dismount ...
            //if (Steed != null)
            //    DismountSteed(true);
            //Dead ppl. don't sit ...
            IsSitting = false;
            IsSwimming = false;

            // Decide whether this bot should linger as a rez-able corpse instead of being
            // immediately removed. Conditions: owned by a player AND currently grouped.
            // Loose/ungrouped/world-spawned mimics fall through to the standard cleanup.
            bool enterRezWait = !string.IsNullOrEmpty(OwnerAccount) && Group != null;

            if (enterRezWait)
            {
                // Inline GameLiving.ProcessDeath body so we get the proper death teardown
                // WITHOUT going through GameNPC.ProcessDeath (which would Group.RemoveMember
                // and Delete the bot, defeating the whole rez window).
                Brain?.KillFSM();
                StopMoving();
                CurrentPathPoint = null;

                attackComponent.StopAttack();
                attackComponent.AttackerTracker.Clear();

                rangeAttackComponent.AutoFireTarget = null;
                TargetObject = null;

                EffectList.CancelAll();
                effectListComponent.CancelAll();

                StopHealthRegeneration();
                StopPowerRegeneration();
                StopEnduranceRegeneration();

                Health = 0;

                LastAttackedByEnemyTickPvE = 0;
                LastAttackedByEnemyTickPvP = 0;

                Notify(GameLivingEvent.Dying, this, new DyingEventArgs(killer));

                // Role takeovers: if the dying bot held a critical role, promote
                // the best surviving candidate immediately so the group doesn't
                // spiral while the corpse waits for rez.
                Group?.MimicGroup?.TryPromoteSecondaryTank(this);
                Group?.MimicGroup?.TryPromoteSecondaryCC(this);
                Group?.MimicGroup?.TryPromoteHealer(this);

                // Choose the rez window length. With a rezzer present we wait the full
                // minute; otherwise release the corpse fast so the group can /assist again.
                int waitMs = GroupHasRezzer() ? REZ_WAIT_MS : REZ_WAIT_NO_HEALER_MS;

                _inRezWait = true;
                _rezWaitTimer = new ECSGameTimer(this, OnRezWaitExpired);
                _rezWaitTimer.Start(waitMs);
            }
            else
            {
                // No owner or no group: original behavior — leaves the group,
                // deletes the corpse. We still want role takeover to happen
                // BEFORE the bot leaves its group, otherwise PvE-population
                // and PvP-frontier groups never promote secondary roles on a
                // member death (those bots have no OwnerAccount, so they
                // skipped the entire enterRezWait branch where the previous
                // promotion calls lived).
                if (Group != null)
                {
                    Group.MimicGroup?.TryPromoteSecondaryTank(this);
                    Group.MimicGroup?.TryPromoteSecondaryCC(this);
                    Group.MimicGroup?.TryPromoteHealer(this);
                }
                MimicSpawner?.Remove(this);
                base.ProcessDeath(killer);
            }

            // sent after buffs drop
            // GamePlayer.Die.CorpseLies:		{0} just died. {1} corpse lies on the ground.
            // base.ProcessDeath in the no-rez-wait branch calls Delete(), so
            // CurrentRegion / Group are cleared by the time we get here.
            // Skip the cosmetic message in that branch to avoid the NPE
            // chain inside Message.SystemToOthers2 (region traversal +
            // GetPronoun reading from a torn-down client).
            if (enterRezWait && ObjectState == eObjectState.Active)
                Message.SystemToOthers2(this, eChatType.CT_OthersDeath, "GamePlayer.Die.CorpseLies", GetName(0, true), GetPronoun(this.Client, 1, true));

            if (m_releaseType == eReleaseType.Duel && killer != null)
            {
                foreach (GamePlayer player in killer.GetPlayersInRadius(WorldMgr.INFO_DISTANCE))
                {
                    if (player != killer)
                        // Message: {0} wins the duel!
                        player.Out.SendMessage(LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.Duel.Die.KillerWinsDuel", killer.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                }
                // Message: {0} wins the duel!
                //Message.SystemToOthers(Client, LanguageMgr.GetTranslation(this, "GamePlayer.Duel.Die.KillerWinsDuel", killer.Name), eChatType.CT_Emote);
            }

            // deal out exp and realm points based on server rules
            // no other way to keep correct message order...
            // Mimic deaths skip the server-rules OnPlayerKilled path: the
            // implementation is hardwired to GamePlayer-specific state
            // (Statistics, LastDeathRealmPoints, XPGainers ordering). Mimic
            // RP/XP awarding happens via the mimic AI / Brain on death
            // instead, so the call is intentionally omitted here.
            CancelAllConcentrationEffects();
        }

        public override void EnemyKilled(GameLiving enemy)
        {
            if (Group != null)
            {
                foreach (GameLiving living in Group.GetMembersInTheGroup())
                {
                    if (living == this)
                        continue;

                    if (enemy.attackComponent.AttackerTracker.ContainsAttacker(living))
                        continue;

                    if (IsWithinRadius(living, WorldMgr.MAX_EXPFORKILL_DISTANCE))
                        Notify(GameLivingEvent.EnemyKilled, living, new EnemyKilledEventArgs(enemy));
                }
            }

            base.EnemyKilled(enemy);
        }

        public override bool InCombatPvE => base.InCombatPvE || ControlledBrain?.Body.InCombatPvE == true;
        public override bool InCombatPvP => base.InCombatPvP || ControlledBrain?.Body.InCombatPvP == true;
        public override bool InCombat => base.InCombat || ControlledBrain?.Body.InCombat == true;
    }
}
