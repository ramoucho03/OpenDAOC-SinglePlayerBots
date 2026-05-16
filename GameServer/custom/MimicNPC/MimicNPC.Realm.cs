using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Language;
using System;
using System.Collections.Generic;
using System.Linq;
using static DOL.GS.GamePlayer;

namespace DOL.GS.Scripts
{
    // Realm/region/bounty/skillpoints surface of MimicNPC. Houses RP/BP/SP
    // gain hooks, prc table lookups, and the realm-rank UI plumbing
    // delegated to the base GameNPC. Extracted from MimicNPC.cs.
    public partial class MimicNPC
    {

        private long m_bountyPoints;

        /// <summary>
        /// Gets/sets player bounty points
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual long BountyPoints
        {
            get { return m_bountyPoints; }
            set { m_bountyPoints = value; }
        }

        private long m_realmPoints;

        /// <summary>
        /// Gets/sets player realm points
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual long RealmPoints
        {
            get { return m_realmPoints; }
            set { m_realmPoints = value; }
        }

        /// <summary>
        /// Gets/sets player skill specialty points
        /// </summary>
        public virtual int SkillSpecialtyPoints
        {
            get { return VerifySpecPoints(); }
        }

        /// <summary>
        /// Gets/sets player realm specialty points
        /// </summary>
        //public virtual int RealmSpecialtyPoints
        //{
        //    get
        //    {
        //        return GameServer.ServerRules.GetPlayerRealmPointsTotal(this)
        //                 - GetRealmAbilities().Where(ab => !(ab is RR5RealmAbility))
        //                     .Sum(ab => Enumerable.Range(0, ab.Level).Sum(i => ab.CostForUpgrade(i)));
        //    }
        //}

        private int _realmLevel;

        /// <summary>
        /// Gets/sets player realm rank
        /// </summary>
        public virtual int RealmLevel
        {
            get { return _realmLevel; }
            set { _realmLevel = value; }
        }

        /// <summary>
        /// Returns the translated realm rank title of the player.
        /// </summary>
        /// <param name="language"></param>
        /// <returns></returns>
        public virtual string RealmRankTitle(string language)
        {
            string translationId = string.Empty;

            if (Realm != eRealm.None && Realm != eRealm.Door)
            {
                int RR = 0;

                if (RealmLevel > 0)
                    RR = RealmLevel / 10 + 1;

                string realm = string.Empty;
                if (Realm == eRealm.Albion)
                    realm = "Albion";
                else if (Realm == eRealm.Midgard)
                    realm = "Midgard";
                else
                    realm = "Hibernia";

                string gender = Gender == eGender.Female ? "Female" : "Male";

                translationId = string.Format("{0}.RR{1}.{2}", realm, RR, gender);
            }
            else
            {
                translationId = "UnknownRealm";
            }

            string translation;
            if (!LanguageMgr.TryGetTranslation(out translation, language, string.Format("GamePlayer.RealmTitle.{0}", translationId)))
                translation = RealmTitle;

            return translation;
        }

        /// <summary>
        /// Gets player realm rank name
        /// sirru mod 20.11.06
        /// </summary>
        public virtual string RealmTitle
        {
            get
            {
                if (Realm == eRealm.None)
                    return "Unknown Realm";

                try
                {
                    return GlobalConstants.REALM_RANK_NAMES[(int)Realm - 1, (int)Gender - 1, (RealmLevel / 10)];
                }
                catch
                {
                    return "Unknown Rank"; // why aren't all the realm ranks defined above?
                }
            }
        }

        /// <summary>
        /// Called when this player gains realm points
        /// </summary>
        /// <param name="amount">The amount of realm points gained</param>
        public override void GainRealmPoints(long amount)
        {
            GainRealmPoints(amount, true, true);
        }

        /// <summary>
        /// Called when this living gains realm points
        /// </summary>
        /// <param name="amount">The amount of realm points gained</param>
        public void GainRealmPoints(long amount, bool modify)
        {
            GainRealmPoints(amount, modify, true);
        }

        /// <summary>
        /// Called when this player gains realm points
        /// </summary>
        public void GainRealmPoints(long amount, bool modify, bool sendMessage)
        {
            GainRealmPoints(amount, modify, true, true);
        }

        /// <summary>
        /// Called when this player gains realm points
        /// </summary>
        /// <param name="amount">The amount of realm points gained</param>
        /// <param name="modify">Should we apply the rp modifer</param>
        /// <param name="sendMessage">Wether to send a message like "You have gained N realmpoints"</param>
        /// <param name="notify"></param>
        public virtual void GainRealmPoints(long amount, bool modify, bool sendMessage, bool notify)
        {
            if (!GainRP)
                return;

            if (modify)
            {
                //rp rate modifier
                double modifier = ServerProperties.Properties.RP_RATE;
                if (modifier != -1)
                    amount = (long)(amount * modifier);

                //[StephenxPimente]: Zone Bonus Support
                if (ServerProperties.Properties.ENABLE_ZONE_BONUSES)
                {
                    //int zoneBonus = (((int)amount * ZoneBonus.GetRPBonus(this)) / 100);
                    //if (zoneBonus > 0)
                    //{
                    //   /Out.SendMessage(ZoneBonus.GetBonusMessage(this, (int)(zoneBonus * ServerProperties.Properties.RP_RATE), ZoneBonus.eZoneBonusType.RP),
                    //        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    //    GainRealmPoints((long)(zoneBonus * ServerProperties.Properties.RP_RATE), false, false, false);
                    //}
                }

                //[Freya] Nidel: ToA Rp Bonus
                long rpBonus = GetModified(eProperty.RealmPoints);
                if (rpBonus > 0)
                {
                    amount += (amount * rpBonus) / 100;
                }
            }

            if (notify)
                base.GainRealmPoints(amount);

            RealmPoints += amount;
            m_statistics.AddToTotalRealmPointsEarned((uint)amount);

            //if (m_guild != null && Client.Account.PrivLevel == 1)
                //m_guild.RealmPoints += amount;

            if (sendMessage == true && amount > 0)
                while (RealmPoints >= CalculateRPsFromRealmLevel(RealmLevel + 1) && RealmLevel < (REALMPOINTS_FOR_LEVEL.Length - 1))
                {
                    RealmLevel++;

                    if (RealmLevel % 10 == 0)
                    {
                        foreach (GamePlayer plr in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                            plr.Out.SendLivingDataUpdate(this, true);

                        Notify(GamePlayerEvent.RRLevelUp, this);
                    }
                    else
                        Notify(GamePlayerEvent.RLLevelUp, this);

                    //if (GameServer.ServerRules.CanGenerateNews(this) && ((RealmLevel >= 40 && RealmLevel % 10 == 0) || RealmLevel >= 60))
                    //{
                    //    string newsmessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainRealmPoints.ReachedRankNews", Name, RealmLevel + 10, LastPositionUpdateZone.Description);
                    //    NewsMgr.CreateNews(newsmessage, this.Realm, eNewsType.RvRLocal, true);
                    //}
                }

            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Called when this living buy something with realm points
        /// </summary>
        /// <param name="amount">The amount of realm points loosed</param>
        public bool RemoveBountyPoints(long amount)
        {
            return RemoveBountyPoints(amount, null);
        }

        /// <summary>
        /// Called when this living buy something with realm points
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="str"></param>
        /// <returns></returns>
        public bool RemoveBountyPoints(long amount, string str)
        {
            return RemoveBountyPoints(amount, str, eChatType.CT_Say, eChatLoc.CL_SystemWindow);
        }

        /// <summary>
        /// Called when this living buy something with realm points
        /// </summary>
        /// <param name="amount">The amount of realm points loosed</param>
        /// <param name="loc">The chat location</param>
        /// <param name="str">The message</param>
        /// <param name="type">The chat type</param>
        public virtual bool RemoveBountyPoints(long amount, string str, eChatType type, eChatLoc loc)
        {
            if (BountyPoints < amount)
                return false;
            BountyPoints -= amount;
            //Out.SendUpdatePoints();
            // if (str != null && amount != 0)
            //     Out.SendMessage(str, type, loc);
            return true;
        }

        /// <summary>
        /// Player gains bounty points
        /// </summary>
        /// <param name="amount">The amount of bounty points</param>
        public override void GainBountyPoints(long amount)
        {
            GainBountyPoints(amount, true, true);
        }

        /// <summary>
        /// Player gains bounty points
        /// </summary>
        /// <param name="amount">The amount of bounty points</param>
        public void GainBountyPoints(long amount, bool modify)
        {
            GainBountyPoints(amount, modify, true);
        }

        /// <summary>
        /// Called when player gains bounty points
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="modify"></param>
        /// <param name="sendMessage"></param>
        public void GainBountyPoints(long amount, bool modify, bool sendMessage)
        {
            GainBountyPoints(amount, modify, true, true);
        }

        /// <summary>
        /// Called when player gains bounty points
        /// </summary>
        /// <param name="amount">The amount of bounty points gained</param>
        /// <param name="multiply">Should this amount be multiplied by the BP Rate</param>
        /// <param name="sendMessage">Wether to send a message like "You have gained N bountypoints"</param>
        public virtual void GainBountyPoints(long amount, bool modify, bool sendMessage, bool notify)
        {
            if (modify)
            {
                //bp rate modifier
                double modifier = ServerProperties.Properties.BP_RATE;
                if (modifier != -1)
                    amount = (long)(amount * modifier);

                //[StephenxPimente]: Zone Bonus Support
                if (ServerProperties.Properties.ENABLE_ZONE_BONUSES)
                {
                    //int zoneBonus = (((int)amount * ZoneBonus.GetBPBonus(this)) / 100);
                    //if (zoneBonus > 0)
                    //{
                    //    //Out.SendMessage(ZoneBonus.GetBonusMessage(this, (int)(zoneBonus * ServerProperties.Properties.BP_RATE), ZoneBonus.eZoneBonusType.BP),
                    //        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    //    GainBountyPoints((long)(zoneBonus * ServerProperties.Properties.BP_RATE), false, false, false);
                    //}
                }

                //[Freya] Nidel: ToA Bp Bonus
                long bpBonus = GetModified(eProperty.BountyPoints);

                if (bpBonus > 0)
                {
                    amount += (amount * bpBonus) / 100;
                }
            }

            if (notify)
                base.GainBountyPoints(amount);

            BountyPoints += amount;

            //if (m_guild != null && Client.Account.PrivLevel == 1)
            //    m_guild.BountyPoints += amount;

            ///if (sendMessage == true)
               // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainBountyPoints.YouGet", amount.ToString()), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Holds realm points needed for special realm level
        /// </summary>
        public static readonly long[] REALMPOINTS_FOR_LEVEL =
        {
            0,	// for level 0
            0,	// for level 1
            25,	// for level 2
            125,	// for level 3
            350,	// for level 4
            750,	// for level 5
            1375,	// for level 6
            2275,	// for level 7
            3500,	// for level 8
            5100,	// for level 9
            7125,	// for level 10
            9625,	// for level 11
            12650,	// for level 12
            16250,	// for level 13
            20475,	// for level 14
            25375,	// for level 15
            31000,	// for level 16
            37400,	// for level 17
            44625,	// for level 18
            52725,	// for level 19
            61750,	// for level 20
            71750,	// for level 21
            82775,	// for level 22
            94875,	// for level 23
            108100,	// for level 24
            122500,	// for level 25
            138125,	// for level 26
            155025,	// for level 27
            173250,	// for level 28
            192850,	// for level 29
            213875,	// for level 30
            236375,	// for level 31
            260400,	// for level 32
            286000,	// for level 33
            313225,	// for level 34
            342125,	// for level 35
            372750,	// for level 36
            405150,	// for level 37
            439375,	// for level 38
            475475,	// for level 39
            513500,	// for level 40
            553500,	// for level 41
            595525,	// for level 42
            639625,	// for level 43
            685850,	// for level 44
            734250,	// for level 45
            784875,	// for level 46
            837775,	// for level 47
            893000,	// for level 48
            950600,	// for level 49
            1010625,	// for level 50
            1073125,	// for level 51
            1138150,	// for level 52
            1205750,	// for level 53
            1275975,	// for level 54
            1348875,	// for level 55
            1424500,	// for level 56
            1502900,	// for level 57
            1584125,	// for level 58
            1668225,	// for level 59
            1755250,	// for level 60
            1845250,	// for level 61
            1938275,	// for level 62
            2034375,	// for level 63
            2133600,	// for level 64
            2236000,	// for level 65
            2341625,	// for level 66
            2450525,	// for level 67
            2562750,	// for level 68
            2678350,	// for level 69
            2797375,	// for level 70
            2919875,	// for level 71
            3045900,	// for level 72
            3175500,	// for level 73
            3308725,	// for level 74
            3445625,	// for level 75
            3586250,	// for level 76
            3730650,	// for level 77
            3878875,	// for level 78
            4030975,	// for level 79
            4187000,	// for level 80
            4347000,	// for level 81
            4511025,	// for level 82
            4679125,	// for level 83
            4851350,	// for level 84
            5027750,	// for level 85
            5208375,	// for level 86
            5393275,	// for level 87
            5582500,	// for level 88
            5776100,	// for level 89
            5974125,	// for level 90
            6176625,	// for level 91
            6383650,	// for level 92
            6595250,	// for level 93
            6811475,	// for level 94
            7032375,	// for level 95
            7258000,	// for level 96
            7488400,	// for level 97
            7723625,	// for level 98
            7963725,	// for level 99
            8208750,	// for level 100
            9111713,	// for level 101
            10114001,	// for level 102
            11226541,	// for level 103
            12461460,	// for level 104
            13832221,	// for level 105
            15353765,	// for level 106
            17042680,	// for level 107
            18917374,	// for level 108
            20998286,	// for level 109
            23308097,	// for level 110
            25871988,	// for level 111
            28717906,	// for level 112
            31876876,	// for level 113
            35383333,	// for level 114
            39275499,	// for level 115
            43595804,	// for level 116
            48391343,	// for level 117
            53714390,	// for level 118
            59622973,	// for level 119
            66181501,	// for level 120
            73461466,	// for level 121
            81542227,	// for level 122
            90511872,	// for level 123
            100468178,	// for level 124
            111519678,	// for level 125
            123786843,	// for level 126
            137403395,	// for level 127
            152517769,	// for level 128
            169294723,	// for level 129
            187917143,	// for level 130
        };

        /// <summary>
        /// Calculates amount of RealmPoints needed for special realm level
        /// </summary>
        /// <param name="realmLevel">realm level</param>
        /// <returns>amount of realm points</returns>
        protected virtual long CalculateRPsFromRealmLevel(int realmLevel)
        {
            if (realmLevel < REALMPOINTS_FOR_LEVEL.Length)
                return REALMPOINTS_FOR_LEVEL[realmLevel];

            // thanks to Linulo from http://daoc.foren.4players.de/viewtopic.php?t=40839&postdays=0&postorder=asc&start=0
            return (long)(25.0 / 3.0 * (realmLevel * realmLevel * realmLevel) - 25.0 / 2.0 * (realmLevel * realmLevel) + 25.0 / 6.0 * realmLevel);
        }

        /// <summary>
        /// Calculates realm level from realm points. SLOW.
        /// </summary>
        /// <param name="realmPoints">amount of realm points</param>
        /// <returns>realm level: RR5L3 = 43, RR1L2 = 2</returns>
        protected virtual int CalculateRealmLevelFromRPs(long realmPoints)
        {
            if (realmPoints == 0)
                return 0;

            int i;

            for (i = REALMPOINTS_FOR_LEVEL.Length - 1; i > 0; i--)
            {
                if (REALMPOINTS_FOR_LEVEL[i] <= realmPoints)
                    break;
            }

            return i;
        }

        /// <summary>
        /// Realm point value of this player
        /// </summary>
        public override int RealmPointsValue
        {
            get
            {
                // Pre-1.81 formula: https://camelotherald.fandom.com/wiki/Patch_Notes:_Version_1.81
                // 25 at RR1, level 25.
                // 225 at RR1, level 35, 245 at RR3, level 35.
                // 900 at RR1, level 50. 990 at RR10, level 50.
                int modifiedLevel = Level - 20;
                return Math.Max(1, modifiedLevel * modifiedLevel) + RealmLevel;
            }
        }

        /// <summary>
        /// Bounty point value of this player
        /// </summary>
        public override int BountyPointsValue
        {
            // TODO: correct formula!
            get { return (int)(1 + Level * 0.6); }
        }

        /// <summary>
        /// Returns the amount of experience this player is worth
        /// </summary>
        public override long ExperienceValue
        {
            get
            {
                return base.ExperienceValue * 4;
            }
        }

        public static readonly int[] prcRestore =
        {
            // http://www.silicondragon.com/Gaming/DAoC/Misc/XPs.htm
            1,//0
            3,//1
            6,//2
            10,//3
            15,//4
            21,//5
            33,//6
            53,//7
            82,//8
            125,//9
            188,//10
            278,//11
            352,//12
            443,//13
            553,//14
            688,//15
            851,//16
            1048,//17
            1288,//18
            1578,//19
            1926,//20
            2347,//21
            2721,//22
            3146,//23
            3633,//24
            4187,//25
            4820,//26
            5537,//27
            6356,//28
            7281,//29
            8337,//30
            9532,//31 - from logs
            10886,//32 - from logs
            12421,//33 - from logs
            14161,//34
            16131,//35
            18360,//36 - recheck
            19965,//37 - guessed
            21857,//38
            23821,//39
            25928,//40 - guessed
            28244,//41
            30731,//42
            33411,//43
            36308,//44
            39438,//45
            42812,//46
            46454,//47
            50385,//48
            54625,//49
            59195,//50
        };

        /// <summary>
        /// Money value of this player
        /// </summary>
        public override long MoneyValue => 3 * prcRestore[Level < prcRestore.Length ? Level : prcRestore.Length - 1];

    }
}
