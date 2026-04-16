using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.GS.PropertyCalc;
using DOL.GS.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using static DOL.GS.GameObject;
using static DOL.GS.GamePlayer;

namespace DOL.GS.Scripts
{
    public interface IGamePlayer : IGameStaticItemOwner
    {
        public IPacketLib Out { get; }
        public GameClient Client { get; }
        public void Notify(DOLEvent e, object sender);
        public string InternalID { get; set; }
        public eObjectState ObjectState { get; set; }

        public int GetModified(eProperty property);

        public int ChangeHealth(GameObject changeSource, eHealthChangeType healthChangeType, int changeAmount);
        public int ChangeMana(GameObject changeSource, eManaChangeType manaChangeType, int changeAmount);
        public int CalculateMaxHealth(int level, int constitution);
        public int CalculateMaxMana(int level, int manaStat);

        public List<Tuple<Skill, Skill>> GetAllUsableSkills(bool update = false);
        public List<Tuple<SpellLine, List<Skill>>> GetAllUsableListSpells(bool update = false);
        public SpellLine GetSpellLine(string keyname);
        public bool HasAbility(string keyName);
        public T GetAbility<T>() where T : Ability;
        public int GetAbilityLevel(string keyName);
        public bool HasSpecialization(string keyName);
        public int GetBaseSpecLevel(string keyName);
        public int GetWeaponStat(DbInventoryItem weapon);
        public double GetWeaponSkill(DbInventoryItem weapon);
        public int GetModifiedSpecLevel(string keyName);
        public void DisableSkill(Skill skill, int duration);

        public PropertyIndexer AbilityBonus { get; }

        public eArmorSlot CalculateArmorHitLocation(AttackData ad);
        public double WeaponDamageWithoutQualityAndCondition(DbInventoryItem weapon);

        public static double ApplyWeaponQualityAndConditionToDamage(DbInventoryItem weapon, double damage)
        {
            return damage * weapon.Quality * 0.01 * weapon.Condition / weapon.MaxCondition;
        }

        public void Stealth(bool goStealth);
        public void StartStealthUncoverAction();
        public void StopStealthUncoverAction();
        public bool CanDetect(GameObject enemy);
        public bool Sprint(bool state);
        public void UpdateWaterBreathState(eWaterBreath state);
        public void StopCurrentSpellcast();
        public void StartInterruptTimer(int duration, AttackData.eAttackType attackType, GameLiving attacker);

        public void OnDuelStart(GameDuel duel);
        public void OnDuelStop();
        public GameLiving DuelPartner { get; }
        public bool IsDuelPartner(GameLiving living);
        public bool IsDuelReady { get; set; }

        public void GainRealmPoints(long amount, bool modify);
        public void GainRealmPoints(long amount);
        public void GainBountyPoints(long amount);
        public void GainExperience(GainedExperienceEventArgs arguments, bool notify = true);
        public void GainExperience(eXPSource xpSource, long exp, bool allowMultiply = false);
        public int GetDistanceTo(IPoint3D point);
        public int GetDistance(IPoint2D point);
        public bool IsWithinRadius(GameObject obj, int radius);
        public bool IsWithinRadius(IPoint3D point, int radius, bool ignoreZ);
        public List<GamePlayer> GetPlayersInRadius(ushort radiusToCheck);
        public List<GameNPC> GetNPCsInRadius(ushort radiusToCheck);
        public bool CanSeeObject(GameObject obj);
        public int GetConLevel(GameObject compare);

        public void InitControlledBrainArray(int num);
        public bool IsControlledNPC(GameNPC npc);
        public void CommandNpcRelease();

        public bool AutoSplitLoot { get; set; }
        public void AddMoney(long money);
        public void AddMoney(long money, string messageFormat);
        public long ApplyGuildDues(long money);

        public void UpdateKillStatsOnPlayerKill(eRealm killedPlayerRealm, bool deathBlow, bool soloKill, int realmPointsEarned);

        public RangeAttackComponent RangeAttackComponent { get; }
        public AttackComponent AttackComponent { get; }
        public StyleComponent StyleComponent { get; }
        public EffectListComponent EffectListComponent { get; }
        public GameEffectList EffectList { get; }
        public PropertyCollection TempProperties { get; }
        public PropertyIndexer ItemBonus { get; }
        public PropertyIndexer BaseBuffBonusCategory { get; }
        public PropertyIndexer SpecBuffBonusCategory { get; }
        public PropertyIndexer DebuffCategory { get; }

        public GameObject TargetObject { get; set; }

        public Group Group { get; }
        public Guild Guild { get; set; }

        public IGameInventory Inventory { get; set; }
        public DbInventoryItem ActiveWeapon { get; }
        public  DbInventoryItem ActiveLeftWeapon { get; }
        public eActiveWeaponSlot ActiveWeaponSlot { get; }
        public bool CanUseCrossRealmItems { get; }
        public int OutOfClassROGPercent { get; set; }
        public GameNPC Steed { get; set; }

        public IControlledBrain ControlledBrain { get; set; }

        public PlayerDeck RandomNumberDeck { get; set; }
        public List<int> SelfBuffChargeIDs { get; }
        public int TotalConstitutionLostAtDeath { get; set; }

        public int SpellInterruptDuration { get; }

        public string GetName(int article, bool firstLetterUppercase);
        public ICharacterClass CharacterClass { get; }
        public eGender Gender { get; set; }
        public short Race { get; set; }
        public string Name { get; set; }
        public byte Level { get; set; }
        public byte MaxLevel { get; }
        public int RealmLevel { get; set; }
        public long RealmPoints { get; set; }
        public long BountyPoints { get; set; }
        public int MLLevel { get; set; }
        public eRealm Realm { get; set; }
        public bool Champion { get; set; }
        public int ChampionLevel { get; set; }

        public long Experience { get; set; }
        public long ExperienceForCurrentLevel { get; }
        public long ExperienceValue { get; }
        public long MoneyValue { get; }
        public int RealmPointsValue { get; }
        public int BountyPointsValue { get; }

        public long ExperienceForNextLevel { get; }
        public eXPLogState XPLogState { get; set; }
        public bool UseDetailedCombatLog { get; set; }
        public Dictionary<GameLiving, double> XPGainers { get; }
        public Lock XpGainersLock { get; }
        public long DeathTime { get; set; }
        public long PlayedTime { get; }

        public double Effectiveness { get; set; }
        public double SpecLock { get; set; }

        public bool IsAlive { get; }
        public bool InCombat { get; }
        public bool IsAttacking { get; }
        public bool IsCasting { get; }
        public bool IsStealthed { get; }
        public bool IsEncumbered { get; set; }
        public double MaxSpeedModifierFromEncumbrance { get; set; }
        public bool IsIncapacitated { get; }
        public bool IsStunned { get; set; }
        public bool IsMezzed { get; set; }
        public bool IsMoving { get; }
        public bool IsSprinting { get; }
        public bool IsSitting { get; set; }
        public bool IsStrafing { get; set; }
        public bool IsUnderwater { get; }
        public bool CanBreathUnderWater { get; set; }

        public ControlledHorse ActiveHorse { get; }
        public bool IsOnHorse { get; set; }

        public void Shade(bool state);
        public ushort ShadeModel { get; }
        public bool HasShadeModel { get; }
        public ushort Model { get; set; }
        public ushort CreationModel { get; }

        public int Health { get; set; }
        public int MaxHealth { get; }
        public byte HealthPercent { get; }
        
        public int Mana { get; set; }
        public int MaxMana { get; }
        public byte ManaPercent { get; }

        public int Endurance { get; set; }
        public int MaxEndurance { get; set; }
        public short MaxSpeedBase { get; set; }

        public int Strength { get; }
        public int Dexterity { get; }
        public int Quickness { get; }
        public int Intelligence { get; }

        public Zone CurrentZone { get; }
        public Region CurrentRegion { get; set; }
        public ushort CurrentRegionID { get; set; }
        public int Z { get; }

        public PlayerStatistics Statistics { get; }
        public long LastDeathRealmPoints { get; set; }
        public int DeathsPvP { get; set; }
    }
}