using System.Collections.Generic;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS
{
    /// <summary>
    /// GameDoor is class for regular doors.
    /// </summary>
    public class GameDoor : GameDoorBase
    {
        private const int STAYS_OPEN_DURATION = 5000;
        private const int REPAIR_INTERVAL = 30 * 1000;

        private static HashSet<int> _borderKeepDoorIds =
        [
            11020501, 11020502,
            12000101, 12000102,
            102093501, 102093502,
            111161301, 111161302,
            206016801, 206016802,
            207156901, 207156902
        ];

        // Region 163 = New Frontiers / Agramon. Its neutral "porte grille"
        // barriers (Realm=Door) are inserted by nf_live.sql and, per operator
        // request, kept PERMANENTLY OPEN — simpler and more reliable than tuning
        // the interaction distance so each grille can be hand-opened.
        //
        // There are 30 of them: 12 in the central zone 163, plus 18 realm-approach
        // grilles spread across zones 169/170 (Midgard side), 173/174 (Hibernia
        // side) and 175/176 (Albion side). The barrier test below is STRUCTURAL
        // (neutral realm + region 163) rather than a hardcoded ID list: the old
        // list only held the 12 central zone-163 IDs, which silently left the
        // realm-approach grilles — notably Midgard's — shut while Albion's and
        // Hibernia's happened to be open. A structural test covers all 30 and is
        // immune to any ID drift between the SQL fixture and the live DB.
        //
        // Safe scoping: keep doors are GameKeepDoor (a different class, and they
        // carry a real realm, not Door), and border-keep doors live in other
        // regions (11/12/102/111/206/207), so neither is caught here.
        private const ushort AGRAMON_REGION_ID = 163;

        private bool _openDead = false;
        private CloseDoorAction _closeDoorAction;

        public override bool CanBeOpenedViaInteraction => !Locked;

        public GameDoor() : base()
        {
            m_model = 0xFFFF;
        }

        public override void Open(GameLiving opener = null)
        {
            if (!Locked)
                State = eDoorState.Open;

            // Agramon barrier grilles stay permanently open — never arm the
            // auto-close timer so they don't shut after STAYS_OPEN_DURATION.
            if (IsAgramonBarrierDoor())
                return;

            if (HealthPercent > 40 || !_openDead)
            {
                lock (_stateLock)
                {
                    _closeDoorAction ??= new(this);

                    // Don't extend the timer for border keep doors, otherwise players can block them.
                    // The client doesn't send any interaction packet during the animation,
                    // ensuring the door's state alternates between closed and open.
                    if (IsBorderKeepDoor() && _closeDoorAction.IsAlive)
                        return;

                    _closeDoorAction.Start(STAYS_OPEN_DURATION);
                }
            }
        }

        public override void Close(GameLiving closer = null)
        {
            // Agramon barrier grilles are permanent passages — ignore every
            // close request (auto-close timer, client close packet, etc.).
            if (IsAgramonBarrierDoor())
                return;

            if (!_openDead)
                State = eDoorState.Closed;

            _closeDoorAction?.Stop();
            _closeDoorAction = null;
        }

        public override int Health
        {
            get => m_health;
            set
            {
                int maxHealth = MaxHealth;

                if (value >= maxHealth)
                {
                    m_health = maxHealth;

                    lock (XpGainersLock)
                    {
                        m_xpGainers.Clear();
                    }
                }
                else
                    m_health = value > 0 ? value : 0;

                if (IsAlive && m_health < maxHealth)
                    StartHealthRegeneration();
            }
        }

        public override int MaxHealth => 5 * GetModified(eProperty.MaxHealth);

        public override void Die(GameObject killer)
        {
            base.Die(killer);
            StartHealthRegeneration();
        }

        public override void StartHealthRegeneration()
        {
            if (m_healthRegenerationTimer.IsAlive || Health >= MaxHealth)
                return;

            m_healthRegenerationTimer.Start(REPAIR_INTERVAL);
        }

        protected override int HealthRegenerationTimerCallback(ECSGameTimer timer)
        {
            if (HealthPercent >= 100)
                return 0;

            if (!InCombat)
            {
                Health += MaxHealth / 100 * 5;

                if (HealthPercent >= 40 && _openDead)
                {
                    _openDead = false;
                    Close();
                }

                // This should normally be done by 'DoorMgr'.
                // But for now it's here because basic doors aren't attackable anyway.
                SaveIntoDatabase();
            }

            return REPAIR_INTERVAL;
        }

        public override void TakeDamage(GameObject source, eDamageType damageType, int damageAmount, int criticalAmount)
        {
            if (!_openDead && Realm != eRealm.Door)
                base.TakeDamage(source, damageType, damageAmount, criticalAmount);

            if (source is not GamePlayer attackerPlayer || _openDead || Realm is eRealm.Door)
                return;

            attackerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(attackerPlayer.Client.Account.Language, "GameDoor.NowOpen", Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            Health -= damageAmount + criticalAmount;

            if (IsAlive)
                return;

            attackerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(attackerPlayer.Client.Account.Language, "GameDoor.NowOpen", Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            Die(source);
            _openDead = true;

            if (!Locked)
                Open();

            Group attackerGroup = attackerPlayer.Group;

            if (attackerGroup != null)
            {
                foreach (GameLiving living in attackerGroup.GetMembersInTheGroup())
                    (living as GamePlayer)?.Out.SendMessage(LanguageMgr.GetTranslation(attackerPlayer.Client.Account.Language, "GameDoor.NowOpen", Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        }

        public override void LoadFromDatabase(DataObject obj)
        {
            base.LoadFromDatabase(obj);

            // Agramon barrier grilles open at load and stay open — force the
            // state regardless of what the DB row stored, and never arm the
            // auto-close timer.
            if (IsAgramonBarrierDoor())
            {
                State = eDoorState.Open;
                return;
            }

            if (State is eDoorState.Open)
            {
                lock (_stateLock)
                {
                    _closeDoorAction ??= new(this);
                    _closeDoorAction.Start(STAYS_OPEN_DURATION);
                }
            }
        }

        public bool IsBorderKeepDoor()
        {
            return IsBorderKeepDoor(DoorId);
        }

        public static bool IsBorderKeepDoor(int doorId)
        {
            return _borderKeepDoorIds.Contains(doorId);
        }

        // A barrier grille = a neutral (Realm.Door) door in the Agramon/NF region.
        // Both pieces of state are set from the DB row in GameDoorBase.LoadFromDatabase
        // BEFORE GameDoor.LoadFromDatabase (the override that consults this) runs,
        // so the test is valid at load time as well as at runtime.
        public bool IsAgramonBarrierDoor()
        {
            return Realm == eRealm.Door && CurrentRegionID == AGRAMON_REGION_ID;
        }

        private class CloseDoorAction : ECSGameTimerWrapperBase
        {
            public CloseDoorAction(GameDoor door) : base(door) { }

            protected override int OnTick(ECSGameTimer timer)
            {
                GameDoor door = (GameDoor) timer.Owner;
                door.Close();
                return 0;
            }
        }
    }
}
