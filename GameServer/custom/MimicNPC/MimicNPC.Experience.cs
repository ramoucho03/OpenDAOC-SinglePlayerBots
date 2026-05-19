using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.ServerProperties;
using DOL.Language;
using System;
using System.Collections.Generic;
using System.Linq;
using static DOL.GS.GamePlayer;

namespace DOL.GS.Scripts
{
    // Level / Experience / Champion-level surface of MimicNPC. XP tables,
    // level-up hooks, champion XP. Extracted from MimicNPC.cs verbatim.
    public partial class MimicNPC
    {

        private bool _champion;
        /// <summary>
        /// Is Champion level activated
        /// </summary>
        public virtual bool Champion
        {
            get { return false; }
            set { _champion = value; }
        }

        private int _championLevel;
        /// <summary>
        /// Champion level
        /// </summary>
        public virtual int ChampionLevel
        {
            get { return 0; }
            set { _championLevel = value; }
        }

        public const byte MAX_LEVEL = 50;

        /// <summary>
        /// How much experience is needed for a given level?
        /// </summary>
        public virtual long GetExperienceNeededForLevel(int level)
        {
            if (level > MAX_LEVEL)
                return GetExperienceAmountForLevel(MAX_LEVEL);

            if (level <= 0)
                return GetExperienceAmountForLevel(0);

            return GetExperienceAmountForLevel(level - 1);
        }

        /// <summary>
        /// How Much Experience Needed For Level
        /// </summary>
        /// <param name="level"></param>
        /// <returns></returns>
        public static long GetExperienceAmountForLevel(int level)
        {
            try
            {
                return XPForLevel[level];
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// A table that holds the required XP/Level
        /// This must include a final entry for MAX_LEVEL + 1
        /// </summary>
        private static readonly long[] XPForLevel =
        {
            0, // xp to level 1
            50, // xp to level 2
            250, // xp to level 3
            850, // xp to level 4
            2300, // xp to level 5
            6350, // xp to level 6
            15950, // xp to level 7
            37950, // xp to level 8
            88950, // xp to level 9
            203950, // xp to level 10
            459950, // xp to level 11
            839950, // xp to level 12
            1399950, // xp to level 13
            2199950, // xp to level 14
            3399950, // xp to level 15
            5199950, // xp to level 16
            7899950, // xp to level 17
            11799950, // xp to level 18
            17499950, // xp to level 19
            25899950, // xp to level 20
            38199950, // xp to level 21
            54699950, // xp to level 22
            76999950, // xp to level 23
            106999950, // xp to level 24
            146999950, // xp to level 25
            199999950, // xp to level 26
            269999950, // xp to level 27
            359999950, // xp to level 28
            479999950, // xp to level 29
            639999950, // xp to level 30
            849999950, // xp to level 31
            1119999950, // xp to level 32
            1469999950, // xp to level 33
            1929999950, // xp to level 34
            2529999950, // xp to level 35
            3319999950, // xp to level 36
            4299999950, // xp to level 37
            5499999950, // xp to level 38
            6899999950, // xp to level 39
            8599999950, // xp to level 40
            12899999950, // xp to level 41
            20699999950, // xp to level 42
            29999999950, // xp to level 43
            40799999950, // xp to level 44
            53999999950, // xp to level 45
            69599999950, // xp to level 46
            88499999950, // xp to level 47
            110999999950, // xp to level 48
            137999999950, // xp to level 49
            169999999950, // xp to level 50
            999999999950, // xp to level 51
        };

        private long m_experience;

        /// <summary>
        /// Gets or sets the current xp of this player
        /// </summary>
        public virtual long Experience
        {
            get { return m_experience; }
            set
            {
                m_experience = value;
            }
        }

        /// <summary>
        /// Returns the xp that are needed for the next level
        /// </summary>
        public virtual long ExperienceForNextLevel
        {
            get
            {
                return GetExperienceNeededForLevel(Level + 1);
            }
        }

        /// <summary>
        /// Returns the xp that were needed for the current level
        /// </summary>
        public virtual long ExperienceForCurrentLevel
        {
            get
            {
                return GetExperienceNeededForLevel(Level);
            }
        }

        /// <summary>
        /// Returns the xp that is needed for the second stage of current level
        /// </summary>
        public virtual long ExperienceForCurrentLevelSecondStage
        {
            get { return 1 + ExperienceForCurrentLevel + (ExperienceForNextLevel - ExperienceForCurrentLevel) / 2; }
        }

        /// <summary>
        /// Returns how far into the level we have progressed
        /// A value between 0 and 1000 (1 bubble = 100)
        /// </summary>
        public virtual ushort LevelPermill
        {
            get
            {
                //No progress if we haven't even reached current level!
                if (Experience < ExperienceForCurrentLevel)
                    return 0;
                //No progess after maximum level
                if (Level > MAX_LEVEL)
                    return 0;

                return
                    (ushort)(1000 * (Experience - ExperienceForCurrentLevel) / (ExperienceForNextLevel - ExperienceForCurrentLevel));
            }
        }

        public void ForceGainExperience(long expTotal)
        {
            if (IsLevelSecondStage)
            {
                if (Experience + expTotal < ExperienceForCurrentLevelSecondStage)
                {
                    expTotal = ExperienceForCurrentLevelSecondStage - Experience;
                }
            }
            else if (Experience + expTotal < ExperienceForCurrentLevel)
            {
                expTotal = ExperienceForCurrentLevel - Experience;
            }

            Experience += expTotal;

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.YouGet", expTotal.ToString("N0", System.Globalization.NumberFormatInfo.InvariantInfo)), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            if (expTotal >= 0)
            {
                //Level up
                if (Level >= 5 && !CharacterClass.HasAdvancedFromBaseClass())
                {
                    if (expTotal > 0)
                    {
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.CannotRaise"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.TalkToTrainer"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                }
                else if (Level >= 40 && Level < MAX_LEVEL && !IsLevelSecondStage && Experience >= ExperienceForCurrentLevelSecondStage)
                {
                    OnLevelSecondStage();
                    Notify(GamePlayerEvent.LevelSecondStage, this);
                }
                else if (Level < MAX_LEVEL && Experience >= ExperienceForNextLevel)
                {
                    Level++;
                }
            }
            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Called whenever this player gains experience
        /// </summary>
        public override void GainExperience(GainedExperienceEventArgs arguments, bool notify = true)
        {
            long expTotal = arguments.ExpTotal;

            if (!GainXP && expTotal > 0)
                return;

            if (this.TempProperties.GetProperty<BattleGroup>(BattleGroup.BATTLEGROUP_PROPERTY) != null)
            {
                Out.SendMessage($"You may not gain experience while in a battlegroup.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            long baseXp = arguments.ExpBase;

            if (arguments.AllowMultiply)
            {
                // Recalculate base experience.
                expTotal -= baseXp;

                if (Properties.ENABLE_ZONE_BONUSES)
                {
                    long zoneBonus = baseXp * ZoneBonus.GetXPBonus(this) / 100;

                    if (zoneBonus > 0)
                    {
                        zoneBonus = (long)(zoneBonus * Properties.XP_RATE);
                        //Out.SendMessage(ZoneBonus.GetBonusMessage(this, (int)zoneBonus, ZoneBonus.eZoneBonusType.XP), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        GainExperience(new(zoneBonus, 0, 0, 0, 0, 0, false, false, eXPSource.Other), false);
                    }
                }

                // CurrentZone can lag by a tick during region transitions;
                // either source flagging RvR is enough, and we treat the
                // missing-zone case as non-RvR for XP rate purposes.
                Region region = CurrentRegion;
                Zone zone = CurrentZone;
                bool isRvR = (region != null && region.IsRvR) || (zone != null && zone.IsRvR);
                if (isRvR)
                    baseXp = (long)(baseXp * Properties.RvR_XP_RATE);
                else
                    baseXp = (long)(baseXp * Properties.XP_RATE);

                long xpBonus = GetModified(eProperty.XpPoints);

                if (xpBonus != 0)
                    baseXp += baseXp * xpBonus / 100;

                expTotal += baseXp;
            }

            // Get Champion Experience too
            // GainChampionExperience(expTotal);

            if (IsLevelSecondStage)
            {
                if (Experience + expTotal < ExperienceForCurrentLevelSecondStage)
                {
                    expTotal = ExperienceForCurrentLevelSecondStage - Experience;
                }
            }
            else if (Experience + expTotal < ExperienceForCurrentLevel)
            {
                expTotal = ExperienceForCurrentLevel - Experience;
            }

            if (arguments.SendMessage && expTotal > 0)
            {
                System.Globalization.NumberFormatInfo format = System.Globalization.NumberFormatInfo.InvariantInfo;
                string totalExpStr = expTotal.ToString("N0", format);
                string expCampBonusStr = string.Empty;
                string expGroupBonusStr = string.Empty;
                string expGuildBonusStr = string.Empty;
                string expBafBonusStr = string.Empty;
                string expOutpostBonusStr = string.Empty;

                if (arguments.ExpCampBonus > 0)
                    expCampBonusStr = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.CampBonus", arguments.ExpCampBonus.ToString("N0", format)) + " ";

                if (arguments.ExpGroupBonus > 0)
                    expGroupBonusStr = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.GroupBonus", arguments.ExpGroupBonus.ToString("N0", format)) + " ";

                if (arguments.ExpGuildBonus > 0)
                    expGuildBonusStr = "(" + arguments.ExpGuildBonus.ToString("N0", format) + " guild bonus) ";

                if (arguments.ExpBafBonus > 0)
                    expBafBonusStr = "(" + arguments.ExpBafBonus.ToString("N0", format) + " baf bonus) ";

                if (arguments.ExpOutpostBonus > 0)
                    expOutpostBonusStr = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.OutpostBonus", arguments.ExpOutpostBonus.ToString("N0", format)) + " ";

                string message = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.YouGet", totalExpStr) + " " + expCampBonusStr + expGroupBonusStr + expGuildBonusStr + expBafBonusStr + expOutpostBonusStr;
                Out.SendMessage(message, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }

            Experience += expTotal;

            if (expTotal >= 0)
            {
                //Level up
                if (Level >= 5 && !CharacterClass.HasAdvancedFromBaseClass())
                {
                    if (expTotal > 0)
                    {
                        Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.CannotRaise"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.TalkToTrainer"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                }
                else if (Level >= 40 && Level < MAX_LEVEL && !IsLevelSecondStage && Experience >= ExperienceForCurrentLevelSecondStage)
                {
                    OnLevelSecondStage();
                    Notify(GamePlayerEvent.LevelSecondStage, this);
                }
                else if (Level < MAX_LEVEL && Experience >= ExperienceForNextLevel)
                {
                    Level++;
                }
            }

            Out.SendUpdatePoints();
        }

        /// <summary>
        /// Gets or sets the level of the player
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override byte Level
        {
            get { return base.Level; }
            set
            {
                byte oldLevel = Level;
                base.Level = value;

                if (oldLevel > 0)
                    if (value > oldLevel)
                        OnLevelUp(oldLevel);
            }
        }

        /// <summary>
        /// What is the base, unmodified level of this character.
        /// </summary>
        public override byte BaseLevel
        {
            get { return base.BaseLevel; }
        }

        private int _mlLevel;

        /// <summary>
        /// Gets and sets the last ML the player has completed.
        /// MLLevel is advanced once all steps are completed.
        /// </summary>
        public virtual int MLLevel
        {
            get { return 0; }
            set { _mlLevel = value; }
        }

        private bool _mlGranted;

        /// <summary>
        /// True if player has started Master Levels
        /// </summary>
        public virtual bool MLGranted
        {
            get { return false; }
            set { _mlGranted = value; }
        }

        private byte _ml;

        /// <summary>
        /// What ML line has this character chosen
        /// </summary>
        public virtual byte MLLine
        {
            get { return 0; }
            set { _ml = value; }
        }

        /// <summary>
        /// What level is displayed to another player
        /// </summary>
        public override byte GetDisplayLevel(GamePlayer player)
        {
            return Math.Min((byte)50, Level);
        }

        private bool m_isLevelSecondStage = false;

        /// <summary>
        /// Is this player in second stage of current level
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool IsLevelSecondStage
        {
            get { return m_isLevelSecondStage; }
            set { m_isLevelSecondStage = value; }
        }

        /// <summary>
        /// Called when this player levels
        /// </summary>
        /// <param name="previouslevel"></param>
        public virtual void OnLevelUp(byte previouslevel)
        {
            IsLevelSecondStage = false;

            LoadClassSpecializations(false);
            SpendSpecPoints(Level, previouslevel);

            //if (Level == MAX_LEVEL)
            //{
            //    if (GameServer.ServerRules.CanGenerateNews(this))
            //    {
            //        string newsmessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnLevelUp.Reached", Name, Level, LastPositionUpdateZone.Description);
            //        NewsMgr.CreateNews(newsmessage, Realm, eNewsType.PvE, true);
            //    }
            //}

            //level 20 changes realm title and gives 1 realm skill point
            if (Level == 20)
                GainRealmPoints(0);

            // Adjust stats
            // stat increases start at level 6
            if (Level > 5)
            {
                for (int i = Level; i > Math.Max(previouslevel, (byte)5); i--)
                {
                    if (CharacterClass.PrimaryStat != eStat.UNDEFINED)
                    {
                        ChangeBaseStat(CharacterClass.PrimaryStat, 1);
                    }
                    if (CharacterClass.SecondaryStat != eStat.UNDEFINED && ((i - 6) % 2 == 0))
                    { // base level to start adding stats is 6
                        ChangeBaseStat(CharacterClass.SecondaryStat, 1);
                    }
                    if (CharacterClass.TertiaryStat != eStat.UNDEFINED && ((i - 6) % 3 == 0))
                    { // base level to start adding stats is 6
                        ChangeBaseStat(CharacterClass.TertiaryStat, 1);
                    }
                }
            }

            //CharacterClass.OnLevelUp(this, previouslevel);
            RefreshSpecDependantSkills(false);

            if (CharacterClass.ClassType == eClassType.ListCaster)
                SetCasterSpells();
            else
                SetSpells();

            // Echostorm - Code for display of new title on level up
            // Get old and current rank titles
            //string currenttitle = CharacterClass.GetTitle(this, Level);

            // check for difference
            //if (CharacterClass.GetTitle(this, previouslevel) != currenttitle)
            //{
            //    // Inform player of new title.
            //    //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnLevelUp.AttainedRank", currenttitle), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            //}

            if (IsAlive)
            {
                // workaround for starting regeneration
                StartHealthRegeneration();
                StartPowerRegeneration();
            }

            DeathCount = 0;

            if (Group != null)
            {
                Group.UpdateGroupWindow();
            }

            // update color on levelup
            if (ObjectState == eObjectState.Active)
            {
                foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                {
                    if (player == null)
                        continue;

                    player.Out.SendEmoteAnimation(this, eEmote.LvlUp);
                }
            }

            Emote(eEmote.LvlUp);

            // save player to database
            //SaveIntoDatabase();
        }

        /// <summary>
        /// Called when this player reaches second stage of the current level
        /// </summary>
        public virtual void OnLevelSecondStage()
        {
            IsLevelSecondStage = true;

            //death penalty reset on mini-ding
            DeathCount = 0;

            if (Group != null)
                Group.UpdateGroupWindow();

            Emote(eEmote.LvlUp);

            if (ObjectState == eObjectState.Active)
            {
                foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                {
                    if (player == null)
                        continue;

                    player.Out.SendEmoteAnimation(this, eEmote.LvlUp);
                }
            }

            SpendSpecPoints(Level, Level);
            RefreshSpecDependantSkills(false);
        }

        /// <summary>
        /// Calculate the Autotrain points.
        /// </summary>
        /// <param name="spec">Specialization</param>
        /// <param name="mode">various AT related calculations (amount of points, level of AT...)</param>
        public virtual int GetAutoTrainPoints(Specialization spec, int Mode)
        {
            int max_autotrain = Level / 4;

            if (max_autotrain == 0) 
                max_autotrain = 1;

            foreach (string autotrainKey in CharacterClass.GetAutotrainableSkills())
            {
                if (autotrainKey == spec.KeyName)
                {
                    switch (Mode)
                    {
                        case 0:// return sum of all AT points in the spec
                        {
                            int pts_to_refund = Math.Min(max_autotrain, spec.Level);
                            return ((pts_to_refund * (pts_to_refund + 1) - 2) / 2);
                        }
                        case 1: // return max AT level + message
                        {
                            if (Level % 4 == 0)
                                if (spec.Level >= max_autotrain)
                                    return max_autotrain;
                            //else
                            //    Out.SendDialogBox(eDialogCode.SimpleWarning, 0, 0, 0, 0, eDialogType.Ok, true, LanguageMgr.GetTranslation(Client.Account.Language, "PlayerClass.OnLevelUp.Autotrain", spec.Name, max_autotrain));
                            return 0;
                        }
                        case 2: // return next free points due to AT change on levelup
                        {
                            if (spec.Level < max_autotrain)
                                return (spec.Level + 1);
                            else
                                return 0;
                        }
                        case 3: // return sum of all free AT points
                        {
                            if (spec.Level < max_autotrain)
                                return (((max_autotrain * (max_autotrain + 1) - 2) / 2) - ((spec.Level * (spec.Level + 1) - 2) / 2));
                            else
                                return ((max_autotrain * (max_autotrain + 1) - 2) / 2);
                        }
                        case 4: // spec is autotrainable
                        {
                            return 1;
                        }
                    }
                }
            }
            return 0;
        }

    }
}
