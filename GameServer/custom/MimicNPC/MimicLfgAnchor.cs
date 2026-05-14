using System;
using System.Collections.Concurrent;
using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Invisible helper NPC dedicated to the /m popup. The 1.127 client only
    /// makes popup `[brackets]` clickable when the message is wrapped as
    /// `<NpcName> says, "..."` AND the client currently targets that NPC; the
    /// click is then sent as a /whisper to that target. To make /m's clickable
    /// `[lfgN]` / `[lfgpN]` brackets work even when no real mimic is in range,
    /// we spawn a peaceful anchor at the player's feet, target it for them,
    /// and let SayTo() send the popup. The anchor routes the resulting
    /// whispers back to /mlfg, so clicking a bracket is functionally identical
    /// to typing the slash command in chat. The anchor uses the same tiny
    /// indicator model the teleporter logic uses (1923) with DONTSHOWNAME so
    /// the player effectively never sees it; it self-destructs after an idle
    /// timeout.
    /// </summary>
    public class MimicLfgAnchor : GameNPC
    {
        // Idle window after which an unused anchor cleans itself up.
        private const int IDLE_TIMEOUT_MS = 5 * 60 * 1000;

        // Anchor is teleported to the player on every /m; this radius is the
        // "close enough, leave it where it is" threshold to avoid pointless
        // MoveTo() packets when the player hasn't moved.
        private const int SNAP_RADIUS = 200;

        private static readonly ConcurrentDictionary<string, MimicLfgAnchor> _byAccount = new();

        private ECSGameTimer _expireTimer;

        public override eGameObjectType GameObjectType => eGameObjectType.NPC;

        public MimicLfgAnchor() : base()
        {
            Name = "Mimic LFG";
            // Model 1923 is the same tiny / near-invisible indicator the
            // teleporter logic uses (GameNPC.cs ~line 2033); DONTSHOWNAME
            // suppresses the floating name, PEACE keeps the anchor out of
            // every aggro list. We don't set CANTTARGET because the client
            // needs to target the anchor to whisper to it on bracket clicks.
            Model = 1923;
            Size = 1;
            Level = 1;
            Realm = eRealm.None;
            Flags = eFlags.PEACE | eFlags.DONTSHOWNAME | eFlags.FLYING;
        }

        /// <summary>
        /// Returns a live, player-targeted anchor for this player. Re-uses the
        /// existing anchor when possible, otherwise spawns a new one. The
        /// caller must invoke this immediately before sending a clickable
        /// popup (the anchor's lifetime resets on each call).
        /// </summary>
        public static MimicLfgAnchor EnsureFor(GamePlayer player)
        {
            if (player?.Client?.Account == null)
                return null;

            string key = player.Client.Account.Name;
            if (string.IsNullOrEmpty(key))
                return null;

            if (!_byAccount.TryGetValue(key, out MimicLfgAnchor anchor)
                || anchor == null
                || anchor.ObjectState != eObjectState.Active)
            {
                anchor = Spawn(player);
                if (anchor == null)
                    return null;
                _byAccount[key] = anchor;
            }

            if (anchor.CurrentRegionID != player.CurrentRegionID
                || !anchor.IsWithinRadius(player, SNAP_RADIUS))
            {
                anchor.MoveTo(player.CurrentRegionID, player.X, player.Y, player.Z, player.Heading);
            }

            anchor.ResetExpireTimer();

            player.TargetObject = anchor;
            player.Out.SendChangeTarget(anchor);

            return anchor;
        }

        private static MimicLfgAnchor Spawn(GamePlayer player)
        {
            MimicLfgAnchor a = new()
            {
                X = player.X,
                Y = player.Y,
                Z = player.Z,
                Heading = player.Heading,
                CurrentRegionID = player.CurrentRegionID,
            };

            return a.AddToWorld() ? a : null;
        }

        private void ResetExpireTimer()
        {
            _expireTimer?.Stop();
            _expireTimer = new ECSGameTimer(this, ExpireCallback, IDLE_TIMEOUT_MS);
        }

        private int ExpireCallback(ECSGameTimer timer)
        {
            Delete();
            return 0;
        }

        public override bool RemoveFromWorld()
        {
            if (!base.RemoveFromWorld())
                return false;

            _expireTimer?.Stop();
            _expireTimer = null;

            foreach (var kv in _byAccount)
            {
                if (ReferenceEquals(kv.Value, this))
                {
                    _byAccount.TryRemove(kv.Key, out _);
                    break;
                }
            }

            return true;
        }

        public override bool WhisperReceive(GameLiving source, string str)
        {
            if (!base.WhisperReceive(source, str))
                return false;

            if (source is not GamePlayer player || player.Client == null || string.IsNullOrEmpty(str))
                return false;

            // Keep the anchor alive while the player is still interacting with
            // its popup — every click resets the idle timer.
            ResetExpireTimer();

            // `[lfgpN]` — pagination.
            if (str.Length > 4
                && str.StartsWith("lfgp", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(str.AsSpan(4), out int pageNum)
                && pageNum >= 1)
            {
                ScriptMgr.HandleCommand(player.Client, "/mlfg page " + pageNum);
                return true;
            }

            // `[lfgN]` — recruit at index N.
            if (str.Length > 3
                && str.StartsWith("lfg", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(str.AsSpan(3), out int lfgIdx)
                && lfgIdx >= 1)
            {
                ScriptMgr.HandleCommand(player.Client, "/mlfg " + lfgIdx);
                return true;
            }

            return false;
        }
    }
}
