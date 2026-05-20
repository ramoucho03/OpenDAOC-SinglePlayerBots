using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.Language;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts
{
    // Equipment / encumbrance surface of MimicNPC. Originally lived inline
    // inside the 9 kLoC MimicNPC.cs; moved out so the file is navigable.
    // Keep adjacent helpers (item bonus apply/remove, equipment-change stats
    // timer, charge buff cancellation) co-located here.
    public partial class MimicNPC
    {
        public int MaxCarryingCapacity
        {
            get
            {
                // Patch 1.62
                // Strength (and strength only) debuffs and disease spells should no longer reduce a player's encumbrance below their unbuffed maximum.
                // Debuffers were using the fact that you could reduce an enemy to 0 movement speed as an effective one minute total snare with no counter,
                // which was not the intention of strength debuff spells.

                double result = Math.Max(GetModified(eProperty.Strength), GetModifiedBase(eProperty.Strength));
                RAPropertyEnhancer lifter = GetAbility<AtlasOF_LifterAbility>();

                if (lifter != null)
                    result *= 1 + lifter.Amount * 0.01;

                return (int)result;
            }
        }

        private int _previousInventoryWeight;
        private int _previousMaxCarryingCapacity;

        public bool IsEncumbered { get; set; }
        public double MaxSpeedModifierFromEncumbrance { get; set; }

        public void UpdateEncumbrance(bool forced = false)
        {
            // OnItemEquipped can fire during a ctor-time inventory wire-up
            // before Inventory is assigned. Guard so we don't NPE if the
            // very first equip arrives that early.
            if (Inventory == null)
                return;

            int inventoryWeight = Inventory.InventoryWeight;
            int maxCarryingCapacity = MaxCarryingCapacity;

            if (!forced && _previousInventoryWeight == inventoryWeight && _previousMaxCarryingCapacity == maxCarryingCapacity)
                return;

            double maxCarryingCapacityRatio = maxCarryingCapacity * 0.35;
            double newMaxSpeedModifier = 1 - inventoryWeight / maxCarryingCapacityRatio + maxCarryingCapacity / maxCarryingCapacityRatio;

            if (forced || MaxSpeedModifierFromEncumbrance != newMaxSpeedModifier)
            {
                if (inventoryWeight > maxCarryingCapacity)
                    IsEncumbered = true;
                else
                    IsEncumbered = false;

                MaxSpeedModifierFromEncumbrance = newMaxSpeedModifier;
                //Out.SendUpdateMaxSpeed(); // Should automatically end up updating max speed using `MaxSpeedModifierFromEncumbrance` if `IsEncumbered` is set to true.
            }

            _previousInventoryWeight = inventoryWeight;
            _previousMaxCarryingCapacity = maxCarryingCapacity;
            //Out.SendEncumbrance();
        }

        /// <summary>
        /// Updates the appearance of the equipment this player is using
        /// </summary>
        public void UpdateEquipmentAppearance()
        {
            foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    player.Out.SendLivingEquipmentUpdate(this);
        }

        public override void UpdateHealthManaEndu()
        {
            //Out.SendCharStatsUpdate();
            //Out.SendUpdateWeaponAndArmorStats();
            UpdateEncumbrance();
            //UpdatePlayerStatus();
            base.UpdateHealthManaEndu();
        }

        /// <summary>
        /// Get the bonus names
        /// </summary>
        public string ItemBonusName(int BonusType)
        {
            string BonusName = string.Empty;

            if (BonusType == 1) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus1");//Strength
            if (BonusType == 2) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus2");//Dexterity
            if (BonusType == 3) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus3");//Constitution
            if (BonusType == 4) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus4");//Quickness
            if (BonusType == 5) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus5");//Intelligence
            if (BonusType == 6) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus6");//Piety
            if (BonusType == 7) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus7");//Empathy
            if (BonusType == 8) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus8");//Charisma
            if (BonusType == 9) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus9");//Power
            if (BonusType == 10) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus10");//Hits
            if (BonusType == 11) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus11");//Body
            if (BonusType == 12) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus12");//Cold
            if (BonusType == 13) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus13");//Crush
            if (BonusType == 14) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus14");//Energy
            if (BonusType == 15) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus15");//Heat
            if (BonusType == 16) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus16");//Matter
            if (BonusType == 17) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus17");//Slash
            if (BonusType == 18) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus18");//Spirit
            if (BonusType == 19) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus19");//Thrust
            return BonusName;
        }

        /// <summary>
        /// Adds magical bonuses whenever item was equipped
        /// </summary>
        /// <param name="e"></param>
        /// <param name="sender">inventory</param>
        /// <param name="arguments"></param>
        public virtual void OnItemEquipped(DbInventoryItem item, eInventorySlot slot)
        {
            if (item == null)
                return;

            if (item is IGameInventoryItem inventoryItem)
                inventoryItem.OnEquipped((GameLiving)this);

            // Mimics don't support mounts (ActiveHorse is always null), so
            // horse-related items carry no state and only need to skip the
            // stat-bonus application below.
            if ((eInventorySlot)item.Item_Type is eInventorySlot.Horse
                or eInventorySlot.HorseArmor
                or eInventorySlot.HorseBarding)
                return;

            if (item.Bonus1 != 0)
                ItemBonus[(eProperty)item.Bonus1Type] += item.Bonus1;

            if (item.Bonus2 != 0)
                ItemBonus[(eProperty)item.Bonus2Type] += item.Bonus2;

            if (item.Bonus3 != 0)
                ItemBonus[(eProperty)item.Bonus3Type] += item.Bonus3;

            if (item.Bonus4 != 0)
                ItemBonus[(eProperty)item.Bonus4Type] += item.Bonus4;


            if (item.Bonus5 != 0)
                ItemBonus[(eProperty)item.Bonus5Type] += item.Bonus5;

            if (item.Bonus6 != 0)
                ItemBonus[(eProperty)item.Bonus6Type] += item.Bonus6;

            if (item.Bonus7 != 0)
                ItemBonus[(eProperty)item.Bonus7Type] += item.Bonus7;


            if (item.Bonus8 != 0)
                ItemBonus[(eProperty)item.Bonus8Type] += item.Bonus8;

            if (item.Bonus9 != 0)
                ItemBonus[(eProperty)item.Bonus9Type] += item.Bonus9;

            if (item.Bonus10 != 0)
                ItemBonus[(eProperty)item.Bonus10Type] += item.Bonus10;

            if (item.ExtraBonus != 0)
                ItemBonus[(eProperty)item.ExtraBonusType] += item.ExtraBonus;

            if ((ePrivLevel)Client.Account.PrivLevel == ePrivLevel.Player && Client.Player != null && Client.Player.ObjectState == eObjectState.Active)
            {
                if (item.SpellID > 0 || item.SpellID1 > 0)
                    TempProperties.SetProperty("ITEMREUSEDELAY" + item.Id_nb, CurrentRegion.Time);
            }

            _statsSenderOnEquipmentChange ??= new(this, OnStatsSendCompletionAfterEquipmentChange);
        }

        private StatsSenderOnEquipmentChange _statsSenderOnEquipmentChange;

        private int OnStatsSendCompletionAfterEquipmentChange()
        {
            _statsSenderOnEquipmentChange = null;
            return 0;
        }

        public class StatsSenderOnEquipmentChange : ECSGameTimerWrapperBase
        {
            private new MimicNPC Owner { get; }
            private Func<int> _onCompletion;

            public StatsSenderOnEquipmentChange(GameObject owner, Func<int> OnCompletion) : base(owner)
            {
                Owner = owner as MimicNPC;
                _onCompletion = OnCompletion;
                Start(0);
            }

            protected override int OnTick(ECSGameTimer timer)
            {
                if (Owner == null || Owner.ObjectState is not eObjectState.Active)
                    return _onCompletion();

                Owner.UpdateEncumbrance();

                // IsAlive here resolved to ECSGameTimerWrapperBase.IsAlive
                // (i.e. "is this timer still alive?"), not the bot's alive
                // status — a subtle capture bug that short-circuited the
                // regen reset whenever the timer happened to be ticking
                // its final round. Test the actual mimic instead.
                if (!Owner.IsAlive)
                    return _onCompletion();

                int maxHealth = Owner.MaxHealth;

                if (Owner.Health < maxHealth)
                    Owner.StartHealthRegeneration();
                else if (Owner.Health > maxHealth)
                    Owner.Health = maxHealth;

                int maxMana = Owner.MaxMana;

                if (Owner.Mana < maxMana)
                    Owner.StartPowerRegeneration();
                else if (Owner.Mana > maxMana)
                    Owner.Mana = maxMana;

                int maxEndurance = Owner.MaxEndurance;

                if (Owner.Endurance < maxEndurance)
                    Owner.StartEnduranceRegeneration();
                else if (Owner.Endurance > maxEndurance)
                    Owner.Endurance = maxEndurance;

                return _onCompletion();
            }
        }

        private int m_activeBuffCharges = 0;

        public int ActiveBuffCharges
        {
            get
            {
                return m_activeBuffCharges;
            }
            set
            {
                m_activeBuffCharges = value;
            }
        }

        public static List<int> SelfBuffChargeIDs { get; } =
            [
                31133, // Strength/Constitution Charge
                31132, // Dexterity/Quickness Charge
                31131, // Acuity Charge
                31130  // AF Charge
            ];

        /// <summary>
        /// Removes magical bonuses whenever item was unequipped
        /// </summary>
        /// <param name="e"></param>
        /// <param name="sender">inventory</param>
        /// <param name="arguments"></param>
        public virtual void OnItemUnequipped(DbInventoryItem item, eInventorySlot slot)
        {
            if (item == null)
                return;

            if (slot == eInventorySlot.Mythical && (eInventorySlot)item.Item_Type == eInventorySlot.Mythical && item is GameMythirian mythirian)
                ((IGameInventoryItem)mythirian).OnUnEquipped((GameLiving)this);

            // Mimics don't support mounts (ActiveHorse is always null).
            if ((eInventorySlot)item.Item_Type is eInventorySlot.Horse
                or eInventorySlot.HorseArmor
                or eInventorySlot.HorseBarding)
                return;

            // Cancel any self buffs that are unequipped.
            if (item.SpellID > 0 && SelfBuffChargeIDs.Contains(item.SpellID) && Inventory.EquippedItems.Where(x => x.SpellID == item.SpellID).Count() <= 1)
                CancelChargeBuff(item.SpellID);

            if (item.Bonus1 != 0)
                ItemBonus[(eProperty)item.Bonus1Type] -= item.Bonus1;

            if (item.Bonus2 != 0)
                ItemBonus[(eProperty)item.Bonus2Type] -= item.Bonus2;

            if (item.Bonus3 != 0)
                ItemBonus[(eProperty)item.Bonus3Type] -= item.Bonus3;

            if (item.Bonus4 != 0)
                ItemBonus[(eProperty)item.Bonus4Type] -= item.Bonus4;

            if (item.Bonus5 != 0)
                ItemBonus[(eProperty)item.Bonus5Type] -= item.Bonus5;

            if (item.Bonus6 != 0)
                ItemBonus[(eProperty)item.Bonus6Type] -= item.Bonus6;

            if (item.Bonus7 != 0)
                ItemBonus[(eProperty)item.Bonus7Type] -= item.Bonus7;

            if (item.Bonus8 != 0)
                ItemBonus[(eProperty)item.Bonus8Type] -= item.Bonus8;

            if (item.Bonus9 != 0)
                ItemBonus[(eProperty)item.Bonus9Type] -= item.Bonus9;

            if (item.Bonus10 != 0)
                ItemBonus[(eProperty)item.Bonus10Type] -= item.Bonus10;

            if (item.ExtraBonus != 0)
                ItemBonus[(eProperty)item.ExtraBonusType] -= item.ExtraBonus;

            if (item is IGameInventoryItem inventoryItem)
                inventoryItem.OnUnEquipped((GameLiving)this);

            _statsSenderOnEquipmentChange ??= new(this, OnStatsSendCompletionAfterEquipmentChange);
        }

        private void CancelChargeBuff(int spellID)
        {
            effectListComponent.GetSpellEffects().FirstOrDefault(x => x.SpellHandler.Spell.ID == spellID)?.End();
        }

        public virtual void RefreshItemBonuses()
        {
            ItemBonus.Clear();
            string slotToLoad = string.Empty;
            switch (VisibleActiveWeaponSlots)
            {
                case 16: slotToLoad = "rightandleftHandSlot"; break;
                case 18: slotToLoad = "leftandtwoHandSlot"; break;
                case 31: slotToLoad = "leftHandSlot"; break;
                case 34: slotToLoad = "twoHandSlot"; break;
                case 51: slotToLoad = "distanceSlot"; break;
                case 240: slotToLoad = "righttHandSlot"; break;
                case 242: slotToLoad = "twoHandSlot"; break;
                default: break;
            }

            //log.Debug("VisibleActiveWeaponSlots= " + VisibleActiveWeaponSlots);
            foreach (DbInventoryItem item in Inventory.EquippedItems)
            {
                if (item == null)
                    continue;

                // skip weapons. only active weapons should fire equip event, done in player.SwitchWeapon
                bool add = true;
                if (slotToLoad != string.Empty)
                {
                    switch (item.SlotPosition)
                    {
                        case Slot.TWOHAND:
                        if (slotToLoad.Contains("twoHandSlot") == false)
                        {
                            add = false;
                        }
                        break;

                        case Slot.RIGHTHAND:
                        if (slotToLoad.Contains("right") == false)
                        {
                            add = false;
                        }
                        break;

                        case Slot.SHIELD:
                        case Slot.LEFTHAND:
                        if (slotToLoad.Contains("left") == false)
                        {
                            add = false;
                        }
                        break;

                        case Slot.RANGED:
                        if (slotToLoad != "distanceSlot")
                        {
                            add = false;
                        }
                        break;

                        default: break;
                    }
                }

                if (!add)
                    continue;

                if (item is IGameInventoryItem)
                {
                    //(item as IGameInventoryItem).CheckValid(this);
                }

                if (item.IsMagical)
                {
                    if (item.Bonus1 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus1Type] += item.Bonus1;
                    }
                    if (item.Bonus2 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus2Type] += item.Bonus2;
                    }
                    if (item.Bonus3 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus3Type] += item.Bonus3;
                    }
                    if (item.Bonus4 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus4Type] += item.Bonus4;
                    }
                    if (item.Bonus5 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus5Type] += item.Bonus5;
                    }
                    if (item.Bonus6 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus6Type] += item.Bonus6;
                    }
                    if (item.Bonus7 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus7Type] += item.Bonus7;
                    }
                    if (item.Bonus8 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus8Type] += item.Bonus8;
                    }
                    if (item.Bonus9 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus9Type] += item.Bonus9;
                    }
                    if (item.Bonus10 != 0)
                    {
                        ItemBonus[(eProperty)item.Bonus10Type] += item.Bonus10;
                    }
                    if (item.ExtraBonus != 0)
                    {
                        ItemBonus[(eProperty)item.ExtraBonusType] += item.ExtraBonus;
                    }
                }
            }
        }

        /// <summary>
        /// Handles a bonus change on an item.
        /// </summary>
        /// <param name="e"></param>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        protected virtual void OnItemBonusChanged(eProperty bonusType, int bonusAmount)
        {
            if (bonusType == 0 || bonusAmount == 0)
                return;

            ItemBonus[bonusType] += bonusAmount;

            if (ObjectState is eObjectState.Active)
            {
                //Out.SendCharStatsUpdate();
                //Out.SendCharResistsUpdate();
                //Out.SendUpdateWeaponAndArmorStats();
                //Out.SendUpdateMaxSpeed();
                //Out.SendEncumberance();
                //Out.SendUpdatePlayerSkills();
                //UpdatePlayerStatus();

                if (IsAlive)
                {
                    if (Health < MaxHealth)
                        StartHealthRegeneration();
                    else if (Health > MaxHealth)
                        Health = MaxHealth;

                    if (Mana < MaxMana)
                        StartPowerRegeneration();
                    else if (Mana > MaxMana)
                        Mana = MaxMana;

                    if (Endurance < MaxEndurance)
                        StartEnduranceRegeneration();
                    else if (Endurance > MaxEndurance)
                        Endurance = MaxEndurance;
                }
            }
        }
    }
}
