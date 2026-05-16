using DOL.Language;
using DOL.Logging;
using System.Collections.Generic;
using System.Reflection;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Centralised list of every chat translation key the mimic strategies
    /// reference. Two purposes:
    ///
    /// 1. <b>Compile-time safety</b>: strategies can pass <c>MimicChatKeys.LowHealth</c>
    ///    instead of bare <c>"Mimic.Chat.LowHealth.1"</c> string literals,
    ///    eliminating typos that would otherwise show up as the raw key
    ///    leaking into the chat at runtime.
    /// 2. <b>Startup validation</b>: <see cref="ValidateAll"/> walks every
    ///    advertised key and emits a single warning per missing entry the
    ///    first time the manager initialises. Operators see the gap once
    ///    in the boot log instead of discovering it at the next /mlfg.
    ///
    /// Conventions: each property is a tuple-style array of variants. The
    /// chat actions pick one variant at random per execution. Add a new
    /// constant here whenever you wire a new line, then add the matching
    /// entries to language\EN.txt / FR.txt / etc.
    /// </summary>
    public static class MimicChatKeys
    {
        // Awareness — self-state announcements.
        public static readonly string[] LowHealthCrit = { "Mimic.Chat.LowHealthCrit.1", "Mimic.Chat.LowHealthCrit.2", "Mimic.Chat.LowHealthCrit.3" };
        public static readonly string[] LowHealth     = { "Mimic.Chat.LowHealth.1",     "Mimic.Chat.LowHealth.2",     "Mimic.Chat.LowHealth.3" };
        public static readonly string[] LowMana       = { "Mimic.Chat.LowMana.1",       "Mimic.Chat.LowMana.2",       "Mimic.Chat.LowMana.3" };
        public static readonly string[] LowEnd        = { "Mimic.Chat.LowEnd.1",        "Mimic.Chat.LowEnd.2",        "Mimic.Chat.LowEnd.3" };
        public static readonly string[] NeedCure      = { "Mimic.Chat.NeedCure.1",      "Mimic.Chat.NeedCure.2",      "Mimic.Chat.NeedCure.3" };
        public static readonly string[] Banter        = { "Mimic.Chat.Banter.1", "Mimic.Chat.Banter.2", "Mimic.Chat.Banter.3", "Mimic.Chat.Banter.4", "Mimic.Chat.Banter.5", "Mimic.Chat.Banter.6" };

        // Awareness — pulling / engage.
        public static readonly string[] Pulling       = { "Mimic.Chat.Pulling.1",       "Mimic.Chat.Pulling.2",       "Mimic.Chat.Pulling.3" };
        public static readonly string[] ChainPull     = { "Mimic.Chat.ChainPull.1",     "Mimic.Chat.ChainPull.2",     "Mimic.Chat.ChainPull.3" };
        public static readonly string[] TankEngage    = { "Mimic.Chat.TankEngage.1",    "Mimic.Chat.TankEngage.2",    "Mimic.Chat.TankEngage.3" };

        // Tank — lost aggro.
        public static readonly string[] LostAggro     = { "Mimic.Chat.LostAggro.1",     "Mimic.Chat.LostAggro.2",     "Mimic.Chat.LostAggro.3" };

        // Support — group state callouts.
        public static readonly string[] MemberCrit    = { "Mimic.Chat.MemberCrit.1",    "Mimic.Chat.MemberCrit.2",    "Mimic.Chat.MemberCrit.3" };
        public static readonly string[] MemberMezzed  = { "Mimic.Chat.MemberMezzed.1",  "Mimic.Chat.MemberMezzed.2",  "Mimic.Chat.MemberMezzed.3" };
        public static readonly string[] CcCast2       = { "Mimic.Chat.CcCast.1",        "Mimic.Chat.CcCast.2" };

        // Leader — combat narration.
        public static readonly string[] LeaderEngage      = { "Mimic.Chat.LeaderEngage.1",      "Mimic.Chat.LeaderEngage.2",      "Mimic.Chat.LeaderEngage.3" };
        public static readonly string[] LeaderPostCombat  = { "Mimic.Chat.LeaderPostCombat.1",  "Mimic.Chat.LeaderPostCombat.2",  "Mimic.Chat.LeaderPostCombat.3" };

        // Camp — phase transitions.
        public static readonly string[] CampReady     = { "Mimic.Chat.CampReady.1",     "Mimic.Chat.CampReady.2",     "Mimic.Chat.CampReady.3" };
        public static readonly string[] IncomingAdds  = { "Mimic.Chat.IncomingAdds.1",  "Mimic.Chat.IncomingAdds.2",  "Mimic.Chat.IncomingAdds.3" };
        public static readonly string[] CcCast3       = { "Mimic.Chat.CcCast.1", "Mimic.Chat.CcCast.2", "Mimic.Chat.CcCast.3" };
        public static readonly string[] Rezzing       = { "Mimic.Chat.Rezzing.1",       "Mimic.Chat.Rezzing.2",       "Mimic.Chat.Rezzing.3" };
        public static readonly string[] Retreat       = { "Mimic.Chat.Retreat.1",       "Mimic.Chat.Retreat.2",       "Mimic.Chat.Retreat.3" };
        public static readonly string[] AssistCall    = { "Mimic.Chat.AssistCall.1",    "Mimic.Chat.AssistCall.2",    "Mimic.Chat.AssistCall.3" };

        // Death / rez timeout.
        public static readonly string[] ReleaseToBind = { "Mimic.Chat.ReleaseToBind.1", "Mimic.Chat.ReleaseToBind.2", "Mimic.Chat.ReleaseToBind.3" };

        private static readonly Logger _log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Walks every chat key referenced in this class and emits a single
        /// warning per missing entry in the default language file. Called
        /// once at boot from <c>MimicManager.Initialize</c> — silent when
        /// every key resolves.
        /// </summary>
        public static void ValidateAll()
        {
            int missing = 0;
            HashSet<string> seen = new();

            foreach (FieldInfo field in typeof(MimicChatKeys).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not string[] keys)
                    continue;

                foreach (string key in keys)
                {
                    if (!seen.Add(key))
                        continue;

                    if (!LanguageMgr.TryGetTranslation(out string _, LanguageMgr.DefaultLanguage, key))
                    {
                        if (_log.IsWarnEnabled)
                            _log.Warn($"MimicChatKeys: missing translation for \"{key}\" in default language. Mimic chat will fall back to the raw key.");
                        missing++;
                    }
                }
            }

            if (missing == 0 && _log.IsInfoEnabled)
                _log.Info($"MimicChatKeys: validated {seen.Count} key(s), all resolved in the default language file.");
        }
    }
}
