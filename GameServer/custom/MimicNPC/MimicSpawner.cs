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
        public bool IsRunning { get { return _timer.IsAlive; } }
        public List<MimicNPC> Mimics { get { return _mimics; } }
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
        private int _timerIntervalMin = 1000;
        private int _timerIntervalMax = 2000;
        private int spawningGroupSize;
        private bool _isRunning = false;

        private Group group;

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

        private int TimerCallback(ECSGameTimer timer)
        {
            if (_isRunning)
                return 1000;

            if (SpawnAndStop && _spawnCount > _spawnCountMax)
            {
                _spawnCount = 0;

                if (_dormantInterval > 0)
                    return _dormantInterval;
                else
                    Stop();
            }

            int interval = Util.Random(_timerIntervalMin, _timerIntervalMax);
            int groupSize = GetGroupSize();

            if (groupSize > 1)
                interval += 2000 * groupSize;

            _isRunning = true;
            _ = SpawnMimics(groupSize);

            return interval;
        }

        async Task SpawnMimics(int numberOfMimics)
        {
            try
            {
                for (int i = 0; i < numberOfMimics; i++)
                {
                    MimicNPC mimic = CreateMimic();

                    if (spawningGroupSize > 0 && group != null)
                    {
                        group.AddMember(mimic);
                        spawningGroupSize--;
                    }

                    await Task.Yield();
                }
            }
            finally
            {
                _isRunning = false;
            }
        }

        private int GetGroupSize()
        {
            int maxGroupSize = Math.Min(ServerProperties.Properties.GROUP_MAX_MEMBER, _spawnCountMax - _spawnCount);
            int currentGroupSize = 1;
            int groupChance = _groupChance;

            for (int i = 0; i < maxGroupSize; i++)
            {
                if (Util.Chance(groupChance))
                {
                    currentGroupSize++;
                    groupChance -= 5;
                }
            }

            if (currentGroupSize > 1)
            {
                MimicNPC mimic = CreateMimic();
                group = new Group(mimic);
                group.AddMember(mimic);

                spawningGroupSize = currentGroupSize - 1;
            }

            return currentGroupSize;
        }

        private MimicNPC CreateMimic()
        {
            int randomX = Util.Random(-350, 350);
            int randomY = Util.Random(-350, 350);

            Point3D spawnPoint = new Point3D(_position.X + randomX, _position.Y + randomY, _position.Z);
            eMimicClass mimicClass = MimicManager.GetRandomMimicClass(_realm);
            MimicNPC mimicNPC = MimicManager.GetMimic(mimicClass, (byte)Util.Random(_levelMin, _levelMax));

            MimicManager.AddMimicToWorld(mimicNPC, spawnPoint, _region);
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
            if (checkObject is GamePlayer player && player.Client.Account.PrivLevel == 1)
                return false;

            return base.IsVisibleTo(checkObject);
        }

        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player))
                return false;

            player.Out.SendMessage(
                "---------------------------------------\n" +
                "Running: " + _timer.IsAlive + "\n" +
                "Spawns: " + _mimics.Count + "/" + _spawnCountMax + "\n\n" +
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
                    if (_timer.IsAlive)
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

                        if (_timer.IsAlive)
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
            if (_timer.IsAlive)
                _timer.Stop();
        }

        public void Start()
        {
            if (!_timer.IsAlive)
                _timer.Start();
        }
    }
}