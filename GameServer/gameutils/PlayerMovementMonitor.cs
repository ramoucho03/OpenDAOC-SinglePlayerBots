using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using DOL.Logging;
using DOL.Timing;

namespace DOL.GS
{
    public class PlayerMovementMonitor
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly GamePlayer _player;                   // Player being monitored for movement and speed hacks.

        // Stuck/safe position snapshot fields.
        private PositionSample _safePosition;                  // The last validated safe position.
        private PositionSample _candidateSafePosition;         // The position currently being tested.
        private long _nextSafePosCheckTime;                    // Timestamp for the next safe position check.

        // Speed hack fields.
        private PositionSample _previous;                      // Previous position sample recorded.
        private PositionSample _current;                       // Current position sample being recorded.
        private PositionSample _teleport;                      // Position sample to use to teleport the player back to if a speed hack is detected.
        private readonly Queue<long> _violationTimestamps;     // Queue of timestamps for recent violations.
        private int _teleportCount;                            // Counter for consecutive teleports due to speed hacks.
        private long _pausedUntil;                             // Time until which recording is paused after a teleport.
        private long _accumulatedPauseTime;                    // Accumulated time for which recording is paused, used to increase violation validity duration after a teleport.
        private long _lastSpeedDecreaseTime;                   // Time when the last speed decrease was recorded, used to handle latency issues.
        private short _previousMaxSpeed;                       // Previous maximum speed of the player, used to determine if speed has decreased.
        private short _cachedMaxSpeed;                         // Cached max speed to avoid recalculating it too often.
        private long _cachedMaxSpeedTime;                      // Time when max speed was calculated.

        // Stuck/safe position snapshot configuration.
        public static int SafePositionUpdateInterval { get; set; } = 1500;  // How often a new safe spot check occurs.
        public static int SafePositionMinDistance { get; set; } = 125;      // Player must move this far from the previous safe spot for a new one to be set.

        // Speed hack configuration.
        // Master switch. DISABLED by default: this is a single-player + bots
        // server where speed-hack policing has little value, but its corrective
        // teleport-back was rubber-banding legit players on the busy frontier map
        // (the "stuck, bouncing up and down, can't move" symptom). It can be
        // re-enabled live with the /speedhack admin command ("/speedhack Enabled
        // true"); the thresholds below are also set very tolerant for that case.
        public static bool Enabled { get; set; } = false;
        public static int ViolationThreshold { get; set; } = 20;            // Consecutive detections before action. Raised (very tolerant).
        public static int ViolationValidityDuration { get; set; } = 1500;   // Duration in milliseconds for which violations are considered valid.
        public static int TeleportThreshold { get; set; } = 5;              // Number of consecutive teleports before kicking.
        public static double MaxSpeedToleranceFactor { get; set; } = 3.0;   // Speed tolerance. Raised to 3x (very tolerant) so normal RvR movement never trips it.
        public static int LatencyBuffer { get; set; } = 750;                // Buffer time to account for latency when checking speed changes.

        public PlayerMovementMonitor(GamePlayer player)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _violationTimestamps = new();
        }

        public void RecordPosition()
        {
            long timestamp = MonotonicTime.NowMs;

            if (_pausedUntil > timestamp)
                return;

            short currentMaxSpeed = GetCachedPlayerMaxSpeed();

            // Detect speed decrease.
            if (_current.MaxSpeed > 0 && currentMaxSpeed < _current.MaxSpeed)
            {
                _lastSpeedDecreaseTime = timestamp;
                _previousMaxSpeed = _current.MaxSpeed;
            }

            PositionSample sample = new(_player.X, _player.Y, _player.Z, GameLoop.GameLoopTime, timestamp, currentMaxSpeed);

            // Check if more than one position sample is being recorded in the same game loop tick.
            if (_current.GameLoopTime == GameLoop.GameLoopTime)
                _current = sample;
            else
            {
                _previous = _current;
                _current = sample;
            }

            // Update safe position.
            // Note: Currently affected by _pausedUntil. Not sure if that's a problem.
            if (timestamp > _nextSafePosCheckTime)
                UpdateSafePosition(sample, timestamp);
        }

        public void ValidateMovement()
        {
            // Disabled (default on this server): never police speed / teleport
            // the player back. Recording still runs so /stuck's safe position
            // keeps working.
            if (!Enabled)
                return;

            // We don't know when the position update was actually received, only when it was processed by the game loop.
            // We account for processing delay uncertainty by adding a small buffer to the time difference, equal to one game loop tick.
            // Ad a side effect, if the server is lagging, the speed hack detection becomes more lenient.

            double timeDiff = _current.Timestamp - _previous.Timestamp;

            // Skip if timestamps are invalid (should not happen).
            if (timeDiff <= 0)
                return;

            timeDiff += GameLoop.TickDuration;
            bool distancedViolationDetected = false;
            long dx = _current.X - _previous.X;
            long dy = _current.Y - _previous.Y;
            long squaredDistance = dx * dx + dy * dy;
            double allowedMaxSpeed = CalculateAllowedMaxSpeed(_current) * MaxSpeedToleranceFactor;
            double allowedMaxDistance = allowedMaxSpeed * timeDiff / 1000.0;
            double allowedMaxDistanceSquared = allowedMaxDistance * allowedMaxDistance;

            if (squaredDistance > allowedMaxDistanceSquared)
                distancedViolationDetected = true;

            if (distancedViolationDetected)
            {
                int validViolationCount = GetAndUpdateValidViolationCount(_current.Timestamp);

                if (validViolationCount == 0)
                {
                    _accumulatedPauseTime = 0;
                    _teleportCount = 0;
                    _teleport = default;
                }

                _violationTimestamps.Enqueue(_current.Timestamp);
                validViolationCount++;

                // Set the teleport position on the first speed violation.
                if (_teleport.Timestamp == 0)
                {
                    if (_previous.Timestamp > 0)
                        _teleport = _previous;
                    else if (_current.Timestamp > 0)
                        _teleport = _current;
                    else
                    {
                        if (log.IsErrorEnabled)
                            log.Error($"Speed hack detected but no previous position available for player {_player.Name}. Cannot teleport back.");

                        return;
                    }
                }

                if (validViolationCount >= ViolationThreshold)
                {
                    if (!_player.IsAllowedToFly && (ePrivLevel) _player.Client.Account.PrivLevel <= ePrivLevel.Player)
                    {
                        // Sustained-average gate. A single pair of position
                        // samples can spike over the per-sample limit for reasons
                        // that are NOT a speed hack: bursty packet processing on a
                        // busy region, a brief client/server desync, the velocity
                        // momentarily exceeding the (flat) MaxSpeed model when
                        // running down a slope or off an edge, or a speed buff
                        // dropping/applying with latency in RvR. Acting on those
                        // teleports a legit player back to a stale position over
                        // and over — the "stuck, bouncing up and down, can't move
                        // forward or back" symptom on the frontier map (the snap
                        // also resets Z, which is the visible vertical bounce).
                        //
                        // Only teleport when the AVERAGE speed across the entire
                        // violation window (from the teleport anchor to now) also
                        // exceeds what's allowed. A real speed hack sustains the
                        // distance and still trips this; transient spikes average
                        // back into the legal envelope and are dropped.
                        bool sustained = true;

                        if (_teleport.Timestamp > 0)
                        {
                            double windowTime = _current.Timestamp - _teleport.Timestamp + GameLoop.TickDuration;

                            if (windowTime > 0)
                            {
                                long wdx = _current.X - _teleport.X;
                                long wdy = _current.Y - _teleport.Y;
                                double windowDistance = Math.Sqrt((double) wdx * wdx + (double) wdy * wdy);
                                double windowSpeed = windowDistance * 1000.0 / windowTime;
                                sustained = windowSpeed > allowedMaxSpeed;
                            }
                        }

                        if (sustained)
                        {
                            double actualDistance = Math.Sqrt(squaredDistance);
                            double actualSpeed = actualDistance * 1000.0 / timeDiff;
                            HandleSpeedHack(actualDistance, allowedMaxDistance, actualSpeed, allowedMaxSpeed);
                        }
                        else
                        {
                            // Benign burst — forget the accumulated violations so
                            // the very next sample doesn't immediately re-trigger.
                            _violationTimestamps.Clear();
                            _teleport = default;
                            _accumulatedPauseTime = 0;
                            _teleportCount = 0;
                        }
                    }
                }
            }
        }

        public void OnTeleportOrRegionChange()
        {
            _previous = default;
            _current = default;
            _safePosition = default;
            _candidateSafePosition = default;
            _nextSafePosCheckTime = 0;

            long now = MonotonicTime.NowMs;
            long previousPauseUntil = Math.Max(_pausedUntil, now);
            _pausedUntil = now + LatencyBuffer;
            _accumulatedPauseTime += _pausedUntil - previousPauseUntil;
        }

        public bool TryGetSafePosition(out Vector3 safePosition)
        {
            if (_safePosition.Timestamp == 0)
            {
                safePosition = Vector3.Zero;
                return false;
            }

            safePosition = new(_safePosition.X, _safePosition.Y, _safePosition.Z);
            return true;
        }

        private double CalculateAllowedMaxSpeed(PositionSample current)
        {
            double newerSpeed = current.MaxSpeed;

            // If within latency buffer after a speed decrease, allow previous max speed.
            if (_lastSpeedDecreaseTime > 0 && (current.Timestamp - _lastSpeedDecreaseTime) <= LatencyBuffer)
                return Math.Max(_previousMaxSpeed, newerSpeed);

            return newerSpeed;
        }

        private int GetAndUpdateValidViolationCount(long currentTimestamp)
        {
            // Is the violation older than the allowed window?
            while (_violationTimestamps.Count > 0 && currentTimestamp - _violationTimestamps.Peek() > ViolationValidityDuration + _accumulatedPauseTime)
                _violationTimestamps.Dequeue();

            return _violationTimestamps.Count;
        }

        private void HandleSpeedHack(double actualDistance, double allowedMaxDistance, double actualSpeed, double allowedMaxSpeed)
        {
            // Make sure we have a valid previous position, otherwise this is a false positive.
            if (_previous.Timestamp == 0)
                return;

            string action = _teleportCount >= TeleportThreshold ? "kick" : "teleport";
            string msg = BuildSpeedHackMessage(action, actualDistance, allowedMaxDistance, actualSpeed, allowedMaxSpeed, _teleportCount);
            GameServer.Instance.LogCheatAction(msg);

            // If we've teleported too many times consecutively, kick the player.
            if (_teleportCount >= TeleportThreshold)
            {
                _player.Out.SendPlayerQuit(true);
                _player.Client.Disconnect();
            }
            else
            {
                _previous = _current;
                _current = _teleport;
                _player.MoveTo(_player.CurrentRegionID, _teleport.X, _teleport.Y, _teleport.Z, _player.Heading); // Will call `OnTeleport`.
                _teleportCount++;
            }

            if (log.IsInfoEnabled)
                log.Info(msg);
        }

        private string BuildSpeedHackMessage(string action, double actualDistance, double allowedMaxDistance, double actualSpeed, double allowedMaxSpeed, int teleportCount)
        {
            return $"Speed hack ({action}): " +
                   $"CharName={_player.Name} " +
                   $"Account={_player.Client?.Account?.Name} " +
                   $"IP={_player.Client?.TcpEndpointAddress} " +
                   $"Distance={actualDistance:0.##} " +
                   $"AllowedDistance={allowedMaxDistance:0.##} " +
                   $"Speed={actualSpeed:0.##} " +
                   $"AllowedSpeed={allowedMaxSpeed:0.##} " +
                   $"TeleportCount={teleportCount}";
        }

        private short GetCachedPlayerMaxSpeed()
        {
            long now = GameLoop.GameLoopTime;

            if (_cachedMaxSpeedTime != now)
            {
                _cachedMaxSpeed = _player.Steed?.MaxSpeed ?? _player.MaxSpeed;
                _cachedMaxSpeedTime = now;
            }

            return _cachedMaxSpeed;
        }

        private void UpdateSafePosition(PositionSample currentSample, long now)
        {
            _nextSafePosCheckTime = now + SafePositionUpdateInterval;

            // If we have no history yet.
            if (_safePosition.Timestamp == 0)
            {
                _safePosition = currentSample;
                _candidateSafePosition = currentSample;
                return;
            }

            long dx = currentSample.X - _candidateSafePosition.X;
            long dy = currentSample.Y - _candidateSafePosition.Y;
            long squaredDistance = dx * dx + dy * dy;

            if (squaredDistance > SafePositionMinDistance * SafePositionMinDistance)
            {
                _safePosition = _candidateSafePosition;
                _candidateSafePosition = currentSample;
            }
        }

        private readonly record struct PositionSample(int X, int Y, int Z, long GameLoopTime, long Timestamp, short MaxSpeed);
    }
}
