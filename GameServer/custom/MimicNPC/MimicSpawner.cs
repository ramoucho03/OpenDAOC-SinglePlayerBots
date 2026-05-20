using DOL.AI;
using DOL.GS.PacketHandler;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace DOL.GS.Scripts
{
    public class MimicSpawner : GameNPC
    {
        public bool IsRunning { get { return _timer != null && _timer.IsAlive; } }
        public List<MimicNPC> Mimics { get { return _mimics; } }
        /// <summary>
        /// Thread-safe snapshot of the spawner's tracked mimics. Callers that
        /// iterate must use this instead of <see cref="Mimics"/> directly —
        /// the raw list is mutated under <c>lock(_mimics)</c> by the spawn
        /// task pool, so foreach over the bare reference throws
        /// InvalidOperationException ("Collection was modified") when a
        /// concurrent spawn lands mid-iteration.
        /// </summary>
        public List<MimicNPC> GetMimicsSnapshot()
        {
            lock (_mimics)
                return new List<MimicNPC>(_mimics);
        }
        public int MimicsCount
        {
            get
            {
                lock (_mimics)
                    return _mimics.Count;
            }
        }
        public int SpawnCount { get { return _spawnCount; } }
        public bool SpawnAndStop { get; set; }
        public int SpawnCountMax => _spawnCountMax;

        public int GroupChance 
        { 
            get {  return _groupChance; } 
            set { _groupChance = value; } 
        }

        public int GroupMaxSize
        {
            get { return _groupMaxSize; }
            set { _groupMaxSize = value; }
        }

        public int GroupMinSize
        {
            get { return _groupMinSize; }
            set { _groupMinSize = value; }
        }

        public override eGameObjectType GameObjectType => eGameObjectType.NPC;

        private eRealm _realm;
        private int _levelMin;
        private int _levelMax;
        private int _spawnCountMax;
        private int _groupChance;
        private int _groupMaxSize;
        private int _groupMinSize;

        private Point3D _position;
        private ushort _region;

        private int _spawnCount;

        private ECSGameTimer _timer;
        private int _dormantInterval;
        private int _timerIntervalMin = 2000;
        private int _timerIntervalMax = 3000;

        // Group-in-progress state. The spawner builds ONE bot per timer tick;
        // when a group is rolled, the seed is built immediately and the
        // remaining members fill in over the next ticks.
        private Group _currentGroup;
        private int _groupRemaining;

        private List<MimicNPC> _mimics;

        public MimicSpawner(eRealm realm, int levelMin, int levelMax, int spawnCountMax, int groupChance, Point3D position, ushort region, int dormantInterval = 0, bool spawnAndStop = false)
        {
            _mimics = new List<MimicNPC>();

            _realm = realm;
            _levelMin = levelMin;
            _levelMax = levelMax;
            _spawnCountMax = spawnCountMax;
            _groupChance = groupChance;
            _position = position;
            _region = region;
            _dormantInterval = dormantInterval;
            SpawnAndStop = spawnAndStop;

            _timer = new ECSGameTimer(this, new ECSGameTimer.ECSTimerCallback(TimerCallback), Util.Random(_timerIntervalMin, _timerIntervalMax));
            MimicSpawning.MimicSpawners.Add(this);

            AddToWorld();
            SpawnAndStop = spawnAndStop;
        }

        /// <summary>
        /// Builds EXACTLY ONE MimicNPC per timer tick, entirely on the timer
        /// (GameLoop) thread.
        ///
        /// The previous implementation spawned a whole group inside an
        /// `async Task` running on the thread pool — meaning MimicNPC
        /// construction AND `AddToWorld()` ran off the GameLoop thread.
        /// `AddToWorld` mutates the region/zone object collections that the
        /// GameLoop iterates every frame for visibility; doing it from a
        /// background thread raced the loop and visibly blinked every NPC in
        /// the area (the "/mcreate bot disappears/reappears every few
        /// seconds" bug). One-bot-per-tick on the timer thread is correct and
        /// bounds the per-frame cost to a single construction.
        /// </summary>
        private int TimerCallback(ECSGameTimer timer)
        {
            // Cap reached: reset the counter and either idle for the dormant
            // interval or stop entirely.
            if (SpawnAndStop && _spawnCount >= _spawnCountMax)
            {
                _spawnCount = 0;
                _currentGroup = null;
                _groupRemaining = 0;

                if (_dormantInterval > 0)
                    return _dormantInterval;

                Stop();
                return 0;
            }

            MimicNPC mimic = CreateMimic();

            if (_groupRemaining > 0)
            {
                // Filling a group rolled on a previous tick.
                if (mimic != null && _currentGroup != null)
                    _currentGroup.AddMember(mimic);

                // Decrement even on a failed build so a transient
                // construction failure can't wedge the group forever.
                _groupRemaining--;
                if (_groupRemaining <= 0)
                    _currentGroup = null;
            }
            else if (mimic != null)
            {
                // Not mid-group: this fresh bot may seed a new group.
                int groupSize = RollGroupSize();
                if (groupSize > 1)
                {
                    _currentGroup = new Group(mimic);
                    // Register with GroupMgr so /who, /team and the BG
                    // presence loop see the spawner's groups.
                    GroupMgr.AddGroup(_currentGroup);
                    _currentGroup.AddMember(mimic);
                    _groupRemaining = groupSize - 1;
                }
            }

            return Util.Random(_timerIntervalMin, _timerIntervalMax);
        }

        /// <summary>
        /// Rolls a target group size in [1..GROUP_MAX_MEMBER] from the
        /// configured group chance. Pure dice — constructs nothing; the caller
        /// supplies the seed bot built this tick.
        /// </summary>
        private int RollGroupSize()
        {
            int maxGroupSize = Math.Min(ServerProperties.Properties.GROUP_MAX_MEMBER,
                                        Math.Max(1, _spawnCountMax - _spawnCount));
            int size = 1;
            int groupChance = _groupChance;

            for (int i = 1; i < maxGroupSize; i++)
            {
                if (Util.Chance(groupChance))
                {
                    size++;
                    groupChance -= 5;
                }
            }

            return size;
        }

        private MimicNPC CreateMimic()
        {
            int randomX = Util.Random(-350, 350);
            int randomY = Util.Random(-350, 350);

            Point3D spawnPoint = new Point3D(_position.X + randomX, _position.Y + randomY, _position.Z);
            eMimicClass mimicClass = MimicManager.GetRandomMimicClass(_realm);

            if (mimicClass == eMimicClass.None)
                return null;

            MimicNPC mimicNPC = MimicManager.GetMimic(mimicClass, (byte)Util.Random(_levelMin, _levelMax));

            if (mimicNPC == null)
                return null;

            if (!MimicManager.AddMimicToWorld(mimicNPC, spawnPoint, _region))
                return null;

            // SpawnMimics runs on the task pool; serialize the list write so
            // we don't race with Remove(). _mimics already has its own lock
            // contract — extend it to writes from this path too.
            lock (_mimics)
                _mimics.Add(mimicNPC);

            mimicNPC.MimicSpawner = this;

            if (SpawnAndStop)
                _spawnCount++;

            return mimicNPC;
        }

        public void Remove(MimicNPC mimic)
        {
            if (mimic == null)
                return;

            lock (_mimics)
            {
                if (_mimics.Contains(mimic))
                    _mimics.Remove(mimic);
            }
        }

        public override bool AddToWorld()
        {
            Name = "Mimic Spawner";
            Model = 408;
            Level = 75;
            Size = 50;
            X = _position.X; 
            Y = _position.Y;
            Z = _position.Z;
            CurrentRegionID = _region;
            Heading = 0;

            Flags |= eFlags.PEACE;

            return base.AddToWorld();
        }

        public override int ChangeHealth(GameObject changeSource, eHealthChangeType healthChangeType, int changeAmount)
        {
            return 0;
        }

        public override bool IsVisibleTo(GameObject checkObject)
        {
            // Spawner is a debug / GM-only marker; hide from regular players.
            // Guard against partially-initialized clients (Account can be null
            // briefly during connect/disconnect transitions).
            if (checkObject is GamePlayer player &&
                (player.Client?.Account?.PrivLevel ?? 1) == 1)
                return false;

            return base.IsVisibleTo(checkObject);
        }

        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player))
                return false;

            player.Out.SendMessage(
                "---------------------------------------\n" +
                "Running: " + IsRunning + "\n" +
                "Spawns: " + MimicsCount + "/" + _spawnCountMax + "\n\n" +
                "[Toggle]\n\n" +
                //"[List]\n\n" +
                "[Delete]"
                ,eChatType.CT_Say, eChatLoc.CL_PopupWindow);
            return true;
        }

        public override bool WhisperReceive(GameLiving source, string str)
        {
            if (!base.WhisperReceive(source, str))
                return false;

            GamePlayer player = source as GamePlayer;

            if (player == null)
                return false;

            string message = string.Empty;

            switch (str)
            {
                case "Toggle":
                {
                    if (_timer == null)
                    {
                        message = "Spawner has been deleted.";
                    }
                    else if (_timer.IsAlive)
                    {
                        _timer.Stop();
                        message = "Spawner is no longer running.";
                    }
                    else
                    {
                        _timer.Start();
                        message = "Spawner is now running.";
                    }

                    break;
                }

                case "Delete":
                {
                    if (MimicSpawning.MimicSpawners.Contains(this))
                    {
                        MimicSpawning.MimicSpawners.Remove(this);

                        if (_timer != null && _timer.IsAlive)
                            _timer.Stop();

                        _timer = null;

                        message = "Spawner has been deleted.";
                    }

                    Delete();

                    break;
                }

                //case "List":
                //{
                //    foreach (MimicNPC mimic in _mimics)
                //    {
                //        message += mimic.Name + " " + mimic.CharacterClass.Name + " " + mimic.Level + " Region: " + mimic.CurrentRegionID + "\n";
                //    }

                //    break;
                //}

                default:break;
            }

            if (message.Length > 0)
                player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_PopupWindow);

            return true;
        }

        public void Stop()
        {
            if (_timer != null && _timer.IsAlive)
                _timer.Stop();
        }

        public void Start()
        {
            if (_timer != null && !_timer.IsAlive)
                _timer.Start();
        }

        /// <summary>
        /// Stops the tick timer and removes this spawner from the static
        /// <see cref="MimicSpawning.MimicSpawners"/> registry when it leaves
        /// the world. Without this, every BG cycle leaked the 3 realm spawners
        /// (and their stopped-but-referenced ECSGameTimer fields) into the
        /// static list forever — MimicBattleground.Clear() only called
        /// Stop()/Delete() and never touched the registry.
        /// </summary>
        public override bool RemoveFromWorld()
        {
            if (!base.RemoveFromWorld())
                return false;

            if (_timer != null && _timer.IsAlive)
                _timer.Stop();
            _timer = null;

            MimicSpawning.MimicSpawners.Remove(this);
            return true;
        }
    }
}