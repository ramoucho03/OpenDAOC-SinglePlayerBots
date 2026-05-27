namespace DOL.GS.RealmAbilities
{
    public static class AtlasRAHelpers
    {
        /// <summary>
        /// 6 stat points per level (Augmented Str, Dex, etc).
        /// </summary>
        public static int GetStatEnhancerAmountForLevel(int level)
        {
            if (level < 1) return 0;

            switch (level)
            {
                case 1: return 6;
                case 2: return 12;
                case 3: return 18;
                case 4: return 24;
                case 5: return 30;
                default: return 30;
            }
        }

        /// <summary>
        /// 3% per level.
        /// </summary>
        public static int GetPropertyEnhancer3AmountForLevel(int level)
        {
            if (level < 1) return 0;

            switch (level)
            {
                case 1: return 3;
                case 2: return 6;
                case 3: return 9;
                case 4: return 12;
                case 5: return 15;
                default: return 15;
            }
        }

        /// <summary>
        /// 5% per level.
        /// </summary>
        public static int GetPropertyEnhancer5AmountForLevel(int level)
        {
            if (level < 1) return 0;

            switch (level)
            {
                case 1: return 5;
                case 2: return 10;
                case 3: return 15;
                case 4: return 20;
                case 5: return 25;
                default: return 25;
            }
        }

        
        /// <summary>
        /// Shared by almost all passive OF Realm Abilities.
        /// </summary>
        public static int GetCommonUpgradeCostFor5LevelsRA(int currentLevel)
        {
            switch (currentLevel)
            {
                case 0: return 1;
                case 1: return 3;
                case 2: return 6;
                case 3: return 10;
                case 4: return 14;
                default: return 1000;
            }
        }

        /// <summary>
        /// Shared by almost all active OF Realm Abilities (that have more than one level).
        /// </summary>
        public static int GetCommonUpgradeCostFor3LevelsRA(int currentLevel)
        {
            switch (currentLevel)
            {
                case 0: return 3;
                case 1: return 6;
                case 2: return 10;
                default: return 1000;
            }
        }

        public static int GetAugDexLevel(GameLiving living)
        {
            AtlasOF_RADexterityEnhancer augDex = living.GetAbility<AtlasOF_RADexterityEnhancer>();
            if (augDex == null)
                return 0;

            return living.CalculateSkillLevel(augDex);
        }

        public static int GetAugStrLevel(GameLiving living)
        {
            AtlasOF_RAStrengthEnhancer augStr = living.GetAbility<AtlasOF_RAStrengthEnhancer>();
            if (augStr == null)
                return 0;

            return living.CalculateSkillLevel(augStr);
        }

        public static int GetAugConLevel(GameLiving living)
        {
            AtlasOF_RAConstitutionEnhancer augCon = living.GetAbility<AtlasOF_RAConstitutionEnhancer>();
            if (augCon == null)
                return 0;

            return living.CalculateSkillLevel(augCon);
        }

        public static int GetAugAcuityLevel(GameLiving living)
        {
            AtlasOF_RAAcuityEnhancer augAcuity = living.GetAbility<AtlasOF_RAAcuityEnhancer>();

            if (augAcuity == null)
                return 0;

            return living.CalculateSkillLevel(augAcuity);
        }

        public static int GetAugQuiLevel(GameLiving living)
        {
            AtlasOF_RAQuicknessEnhancer augQui = living.GetAbility<AtlasOF_RAQuicknessEnhancer>();

            if (augQui == null)
                return 0;

            return living.CalculateSkillLevel(augQui);
        }

        public static int GetSerenityLevel(GameLiving living)
        {
            AtlasOF_SerenityAbility raSerenity = living.GetAbility<AtlasOF_SerenityAbility>();

            if (raSerenity == null)
                return 0;

            return living.CalculateSkillLevel(raSerenity);
        }

        public static int GetFirstAidLevel(GameLiving player)
        {
            AtlasOF_FirstAid raFirstAid = player.GetAbility<AtlasOF_FirstAid>();

            if (raFirstAid == null)
                return 0;

            return player.CalculateSkillLevel(raFirstAid);
        }

        public static int GetLongshotLevel(GameLiving living)
        {
            AtlasOF_Longshot raLongshot = living.GetAbility<AtlasOF_Longshot>();

            if (raLongshot == null)
                return 0;

            return living.CalculateSkillLevel(raLongshot);
        }

        // ---- Friendly prerequisite descriptions ------------------------------
        // Used by RealmAbility.GetRequirementDescription overrides so the train
        // handler can tell the player precisely WHY an RA refused to train
        // ("Requires Augmented Acuity II (you have I).") instead of the
        // misleading generic "You are not experienced enough… come back later."

        private enum AugStat { Str, Dex, Con, Qui, Acuity }

        private static int GetAugLevel(GamePlayer player, AugStat stat) => stat switch
        {
            AugStat.Str    => GetAugStrLevel(player),
            AugStat.Dex    => GetAugDexLevel(player),
            AugStat.Con    => GetAugConLevel(player),
            AugStat.Qui    => GetAugQuiLevel(player),
            AugStat.Acuity => GetAugAcuityLevel(player),
            _              => 0,
        };

        private static string AugName(AugStat stat) => stat switch
        {
            AugStat.Str    => "Augmented Strength",
            AugStat.Dex    => "Augmented Dexterity",
            AugStat.Con    => "Augmented Constitution",
            AugStat.Qui    => "Augmented Quickness",
            AugStat.Acuity => "Augmented Acuity",
            _              => "Augmented Stat",
        };

        private static string RomanLevel(int level) => level switch
        {
            1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => level.ToString(),
        };

        private static string DescribeAug(GamePlayer player, AugStat stat, int needed)
        {
            int have = GetAugLevel(player, stat);
            return $"Requires {AugName(stat)} {RomanLevel(needed)} (you have {(have == 0 ? "none" : RomanLevel(have))}).";
        }

        public static string DescribeAugStr(GamePlayer player, int needed)    => DescribeAug(player, AugStat.Str,    needed);
        public static string DescribeAugDex(GamePlayer player, int needed)    => DescribeAug(player, AugStat.Dex,    needed);
        public static string DescribeAugCon(GamePlayer player, int needed)    => DescribeAug(player, AugStat.Con,    needed);
        public static string DescribeAugQui(GamePlayer player, int needed)    => DescribeAug(player, AugStat.Qui,    needed);
        public static string DescribeAugAcuity(GamePlayer player, int needed) => DescribeAug(player, AugStat.Acuity, needed);

        /// <summary>
        /// Describes a "requires another RA to be trained" prerequisite,
        /// e.g. Grapple requires Trip.
        /// </summary>
        public static string DescribeRequiresAbility(string abilityName) =>
            $"Requires the {abilityName} ability to be trained first.";

        /// <summary>
        /// Describes a "requires another RA at level N" prerequisite,
        /// e.g. Ethereal Bond requires Serenity II.
        /// </summary>
        public static string DescribeRequiresRAAtLevel(string abilityName, int neededLevel, int currentLevel) =>
            $"Requires {abilityName} {RomanLevel(neededLevel)} (you have {(currentLevel == 0 ? "none" : RomanLevel(currentLevel))}).";
    }
}
