using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Renders a group chat line per recipient in their own account language.
    /// Optionally picks a random variant from a list of translation keys so
    /// the bot doesn't repeat the same line.
    /// </summary>
    public sealed class LocalizedGroupSayAction : IBotAction
    {
        private readonly string[] _translationKeys;

        public string Name { get; }

        public LocalizedGroupSayAction(string actionName, params string[] translationKeys)
        {
            _translationKeys = translationKeys ?? new string[0];
            Name = actionName;
        }

        public bool IsPossible(BotContext ctx)
        {
            return _translationKeys.Length > 0;
        }

        public bool Execute(BotContext ctx)
        {
            if (_translationKeys.Length == 0)
                return false;

            // Group-level topic dedup: if another bot already shouted on this
            // topic within MIMIC_GROUP_CHAT_DEDUP_MS, stay silent — eight bots
            // saying "I'm low!" within two seconds breaks immersion. The
            // action's Name is used as the topic key (already unique per
            // binding: "say-low-health", "say-pulling", etc.). Claimed here in
            // Execute (not IsPossible) so a refused announce never consumes the
            // topic and mutes the other bots.
            MimicGroup mg = ctx.MimicGroup;
            if (mg != null && !mg.TryClaimChatTopic(Name))
                return false;

            // Pick once per execution so all recipients see the same variant.
            string key = _translationKeys[Util.Random(_translationKeys.Length - 1)];

            MimicNPC bot = ctx.Bot;
            string fromName = bot.GetName(0, true);

            if (ctx.Group != null)
            {
                foreach (GamePlayer player in ctx.Group.GetPlayersInTheGroup())
                {
                    string lang = player.Client?.Account?.Language;
                    string text = LanguageMgr.TryGetTranslation(out string t, lang, key) ? t : key;
                    player.Out.SendMessage($"[Party] {fromName}: \"{text}\"", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                }
            }
            else
            {
                // No group: speak in /say using the server default language.
                string text = LanguageMgr.GetTranslation(LanguageMgr.DefaultLanguage, key);
                bot.Say(text);
            }

            return true;
        }
    }
}
