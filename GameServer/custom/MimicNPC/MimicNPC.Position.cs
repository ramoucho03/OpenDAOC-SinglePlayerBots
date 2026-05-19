using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.Realm;
using DOL.GS.ServerProperties;
using DOL.Language;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static DOL.GS.GamePlayer;

namespace DOL.GS.Scripts
{
    // Position / region / realm / bind / spawn surface of MimicNPC. Houses
    // MoveTo overrides, region transitions, realm boundary checks and the
    // SpawnPoint accessor. Extracted from MimicNPC.cs verbatim.
    public partial class MimicNPC
    {

        /// <summary>
        /// Holds all areas this player is currently within
        /// </summary>
        private List<IArea> _currentAreas = new();
        private List<IArea> _areaBuffer = new();

        /// <summary>
        /// Holds all areas this player is currently within
        /// </summary>
        public override List<IArea> CurrentAreas
        {
            get => _currentAreas;
            set => _currentAreas = value;
        }

        /// <summary>
        /// Property that saves last maximum Z value
        /// </summary>
        public const string MAX_LAST_Z = "max_last_z";

        /// <summary>
        /// The base speed of the player
        /// </summary>
        public const int PLAYER_BASE_SPEED = 191;

        //private long _nextAreaUpdateTick;

        //public virtual void CheckAreas(Zone newZone)
        //{
        //    // Uses a double-buffer swap.

        //    if (GameLoop.GameLoopTime <= _nextAreaUpdateTick)
        //        return;

        //    List<IArea> activeAreas = CurrentAreas;
        //    List<IArea> areasOfZone = CurrentRegion.GetAreasOfZone(newZone, this, true);

        //    _areaBuffer.Clear();

        //    for (int i = 0; i < activeAreas.Count; i++)
        //    {
        //        IArea area = activeAreas[i];

        //        if (!areasOfZone.Contains(area))
        //            area.OnPlayerLeave(this);
        //        else
        //            _areaBuffer.Add(area);
        //    }

        //    for (int i = 0; i < areasOfZone.Count; i++)
        //    {
        //        IArea area = areasOfZone[i];

        //        if (!activeAreas.Contains(area))
        //        {
        //            area.OnPlayerEnter(this);
        //            _areaBuffer.Add(area);
        //        }
        //    }

        //    _areaBuffer = Interlocked.Exchange(ref _currentAreas, _areaBuffer);

        //    const int UPDATE_INTERVAL = 2000;
        //    _nextAreaUpdateTick = GameLoop.GameLoopTime + UPDATE_INTERVAL;
        //}

        /// <summary>
        /// Gets or sets the current speed of this player
        /// </summary>
        public override short CurrentSpeed
        {
            set
            {
                base.CurrentSpeed = value;

                if (value != 0)
                    OnMimicMove();
            }
        }

        public void OnMimicMove()
        {
            if (IsSitting)
                Sit(false);
        }

        /// <summary>
        /// Holds the players max Z for fall damage
        /// </summary>
        private int m_maxLastZ;

        /// <summary>
        /// Gets or sets the players max Z for fall damage
        /// </summary>
        public int MaxLastZ
        {
            get { return m_maxLastZ; }
            set { m_maxLastZ = value; }
        }

        /// <summary>
        /// Gets or sets the realm of this player
        /// </summary>
        public override eRealm Realm
        {
            get { return base.Realm; }
            set { base.Realm = value; }
        }

        /// <summary>
        /// Gets or sets the heading of this player
        /// </summary>
        public override ushort Heading
        {
            get => base.Heading;

            set
            {
                base.Heading = value;

                attackComponent.attackAction.OnHeadingUpdate();
            }
        }

        protected bool m_climbing;

        /// <summary>
        /// Gets/sets the current climbing state
        /// </summary>
        public bool IsClimbing
        {
            get { return m_climbing; }
            set
            {
                if (value == m_climbing) return;
                m_climbing = value;
            }
        }

        protected bool m_swimming;

        /// <summary>
        /// Gets/sets the current swimming state
        /// </summary>
        public virtual bool IsSwimming
        {
            get
            {
                // Mimics that spawned off-grid (instance edge, between zones,
                // pre-teleport) have a null CurrentZone for one or two ticks.
                // Reading Waterlevel through it NPE'd whenever a heal / movement
                // service polled this property in that window.
                Zone zone = CurrentZone;
                if (zone == null)
                    return false;
                return m_z <= zone.Waterlevel;
            }
            set
            {
                if (value == m_swimming)
                    return;

                m_swimming = value;
            }
        }

        protected long m_beginDrowningTick;
        protected eWaterBreath m_currentWaterBreathState;
        protected ECSGameTimer m_drowningTimer;
        protected ECSGameTimer m_holdBreathTimer;

        protected int DrowningTimerCallback(ECSGameTimer callingTimer)
        {
            if (!IsAlive)
                return 0;

            if (ObjectState != eObjectState.Active)
                return 0;

            if (GameLoop.GameLoopTime - m_beginDrowningTick > 15000)
            {
                TakeDamage(null, eDamageType.Natural, MaxHealth, 0);

                return 0;
            }
            else
                TakeDamage(null, eDamageType.Natural, MaxHealth / 20, 0);

            return 1000;
        }

        /// <summary>
        /// The diving state of this player
        /// </summary>
        protected bool m_diving;

        public bool IsDiving
        {
            get => m_diving;
            set
            {
                // Force the diving state instead of trusting the client.
                if (!value)
                    value = IsUnderwater;

                // CurrentZone can be transiently null during region transitions;
                // ditto for the duplicate `&& value` test which was a refactor
                // artefact. Guard the zone read and drop the redundancy.
                Zone zone = CurrentZone;
                if (value && zone != null && !zone.IsDivingEnabled && Client?.Account?.PrivLevel == 1)
                {
                    Z += 1;
                    Out.SendPlayerJump(false);
                    return;
                }

                if (m_diving == value)
                    return;

                m_diving = value;

                if (m_diving)
                {
                    if (!CanBreathUnderWater)
                        UpdateWaterBreathState(eWaterBreath.Holding);
                }
                else
                    UpdateWaterBreathState(eWaterBreath.None);
            }
        }

        protected bool m_canBreathUnderwater;
        public bool CanBreathUnderWater
        {
            get => m_canBreathUnderwater;
            set
            {
                if (m_canBreathUnderwater == value)
                    return;

                m_canBreathUnderwater = value;

                if (IsDiving)
                {
                    if (m_canBreathUnderwater)
                        UpdateWaterBreathState(eWaterBreath.None);
                    else
                        UpdateWaterBreathState(eWaterBreath.Holding);
                }
            }
        }

        public void UpdateWaterBreathState(eWaterBreath state)
        {
            if (Client.Account.PrivLevel != 1)
                return;

            // Mimics never received an explicit initialiser for the two
            // breath timers, so any path into this method NPE'd the first
            // time a bot dipped below the waterline. Lazy-init both timers
            // here so the rest of the switch can rely on them being alive.
            // Mirror GamePlayer's lambda for hold-breath (escalate to drown).
            m_holdBreathTimer ??= new ECSGameTimer(this, _ =>
            {
                UpdateWaterBreathState(eWaterBreath.Drowning);
                return 0;
            });
            m_drowningTimer ??= new ECSGameTimer(this, DrowningTimerCallback);

            switch (state)
            {
                case eWaterBreath.None:
                {
                    m_holdBreathTimer.Stop();
                    m_drowningTimer.Stop();
                    Out.SendCloseTimerWindow();
                    break;
                }
                case eWaterBreath.Holding:
                {

                    m_drowningTimer.Stop();

                    if (!m_holdBreathTimer.IsAlive)
                    {
                        Out.SendTimerWindow("Holding Breath", 30);
                        m_holdBreathTimer.Start(30000);
                    }

                    break;
                }
                case eWaterBreath.Drowning:
                {
                    if (m_holdBreathTimer.IsAlive)
                    {
                        m_holdBreathTimer.Stop();

                        if (!m_drowningTimer.IsAlive)
                        {
                            Out.SendTimerWindow("Drowning", 15);
                            m_beginDrowningTick = CurrentRegion.Time;
                            m_drowningTimer.Start(0);
                        }
                    }
                    else
                    {
                        // In case the player gets out of water right before the timer is stopped (and ticks instead).
                        UpdateWaterBreathState(eWaterBreath.None);
                        return;
                    }

                    break;
                }
            }

            m_currentWaterBreathState = state;
        }

        protected bool _sitting;

        public override bool IsSitting
        {
            get => _sitting;
            set
            {
                if (!_sitting && value)
                {
                    CurrentSpellHandler?.CasterMoves();

                    if (attackComponent.AttackState && ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                        attackComponent.StopAttack();
                }

                _sitting = value;
            }
        }

        /// <summary>
        /// Gets or sets the max speed of this player
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override short MaxSpeedBase
        {
            get { return base.MaxSpeedBase; }
            set { base.MaxSpeedBase = value; }
        }

        public override bool IsMoving => base.IsMoving || IsStrafing;

        private bool _isOnHorse = false;

        public ControlledHorse ActiveHorse
        {
            get { return null; }
        }

        public virtual bool IsOnHorse
        {
            get { return false; }
            set { _isOnHorse = value; }
        }

        public bool IsSprinting => effectListComponent.ContainsEffectForEffectType(eEffect.Sprint);

        public virtual bool Sprint(bool state)
        {
            if (state == IsSprinting)
                return state;

            if (state)
            {
                // Can't start sprinting with 10 endurance on 1.68 server.
                if (Endurance <= 10)
                    return false;

                if (IsStealthed)
                    return false;

                if (!IsAlive)
                    return false;

                ECSGameEffectFactory.Create(new(this, 0, 1), static (in i) => new SprintECSGameEffect(i));

                // SprintECSGameEffect.OnStartEffect only calls
                // StartEnduranceRegeneration when OwnerPlayer != null, which is
                // never true for a MimicNPC. Without this, a bot starting sprint
                // at full endurance keeps the regen timer dormant (the Endurance
                // setter only restarts it when value < max), so the per-tick
                // drain in EnduranceRegenerationTimerCallback never runs and the
                // bot sprints forever. Kick the timer explicitly here.
                StartEnduranceRegeneration();

                return true;
            }
            else
            {
                ECSGameEffect effect = EffectListService.GetEffectOnTarget(this, eEffect.Sprint);
                effect?.End();

                return false;
            }
        }

        protected bool m_strafing;

        public override bool IsStrafing
        {
            get { return false; }
            set { m_strafing = value; }
        }

        public virtual bool Sit(bool sit)
        {
            Sprint(false);

            if (IsSitting == sit)
                return sit;

            if (!IsAlive)
                return false;

            if (IsCrowdControlled)
                return false;

            if (sit && (CurrentSpeed > 0 || IsStrafing))
                return false;

            // Stop attacking if the player sits down.
            if (sit && attackComponent.AttackState)
                attackComponent.StopAttack();

            IsSitting = sit;

            if (sit)
            {
                Emote(eEmote.Drink);
                return true;
            }
            else
            {
                Emote(eEmote.LetsGo);

                return false;
            }
        }

        protected override bool CanSetGroundTarget()
        {
            ECSGameEffect volley = EffectListService.GetEffectOnTarget(this, eEffect.Volley);

            if (volley != null)
            {
                Out.SendMessage("You can't change ground target under volley effect!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return false;
            }

            return true;
        }

        protected override void OnGroundTargetSet()
        {
            //SiegeWeapon?.SetGroundTarget(GroundTarget.X, GroundTarget.Y, GroundTarget.Z);
        }

        /// <summary>
        /// Holds unique locations array
        /// </summary>
        protected readonly GameLocation[] m_lastUniqueLocations;
        protected readonly Lock _lastUniqueLocationsLock = new();

        /// <summary>
        /// Gets unique locations array
        /// </summary>
        public GameLocation[] LastUniqueLocations
        {
            get { return m_lastUniqueLocations; }
        }

        ///// <summary>
        ///// Updates Health, Mana, Sitting, Endurance, Concentration and Alive status to client
        ///// </summary>
        //public void UpdatePlayerStatus()
        //{
        //    Out.SendStatusUpdate();
        //}

    }
}
