using System;
using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Like <see cref="LocalizedGroupSayAction"/>, but each translation key
    /// can use a single <c>{0}</c> placeholder which is replaced by the name
    /// of a target resolved at execute time.
    ///
    /// The target getter runs once per execution (not per recipient): all
    /// group members see the same name in the same line. Returns false if
    /// the getter returns null, so the binding cooldown does not get
    /// armed for an empty announce.
    /// </summary>
    public sealed class LocalizedGroupSayWithTargetAction : IBotAction
    {
        private readonly string[] _translationKeys;
        private readonly Func<BotContext, GameLiving> _targetGetter;

        public string Name { get; }

        public LocalizedGroupSayWithTargetAction(string actionName, Func<BotContext, GameLiving> targetGetter, params string[] translationKeys)
        {
            _translationKeys = translationKeys ?? new string[0];
            _targetGetter = targetGetter ?? throw new ArgumentNullException(nameof(targetGetter));
            Name = actionName;
        }

        public bool IsPossible(BotContext ctx)
        {
            if (_translationKeys.Length == 0)
                return false;

            // Group-level topic dedup. See LocalizedGroupSayAction.IsPossible
            // for rationale: prevents the chat from being flooded by multiple
            // bots reacting to the same group event in the same tick.
            MimicGroup mg = ctx.MimicGroup;
            if (mg != null && !mg.TryClaimChatTopic(Name))
                return false;

            return true;
        }

        public bool Execute(BotContext ctx)
        {
            if (_translationKeys.Length == 0)
                return false;

            GameLiving target = _targetGetter(ctx);

            if (target == null)
                return false;

            // Pick once per execution so all recipients see the same variant.
            string key = _translationKeys[Util.Random(_translationKeys.Length - 1)];
            string targetName = target.Name;

            MimicNPC bot = ctx.Bot;
            string fromName = bot.GetName(0, true);

            if (ctx.Group != null)
            {
                foreach (GamePlayer player in ctx.Group.GetPlayersInTheGroup())
                {
                    string lang = player.Client?.Account?.Language;
                    string template = LanguageMgr.TryGetTranslation(out string t, lang, key) ? t : key;
                    string text = string.Format(template, targetName);
                    player.Out.SendMessage($"[Party] {fromName}: \"{text}\"", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                }
            }
            else
            {
                string template = LanguageMgr.GetTranslation(LanguageMgr.DefaultLanguage, key);
                bot.Say(string.Format(template, targetName));
            }

            return true;
        }
    }
}
