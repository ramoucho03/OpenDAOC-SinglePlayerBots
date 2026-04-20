using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.Language;

namespace DOL.GS
{
    public class BerserkECSGameEffect : ECSGameAbilityEffect
    {
        public BerserkECSGameEffect(in ECSGameEffectInitParams initParams)
            : base(initParams)
        {
            EffectType = eEffect.Berserk;
        }

        protected ushort m_startModel = 0;

        public override ushort Icon
        { get { return 479; } }

        public override string Name
        {
            get
            {
                if (OwnerPlayer != null)
                    return LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.Name");
                else
                    return "Berserk";
            }
        }

        public override bool HasPositiveEffect
        { get { return true; } }

        public override void OnStartEffect()
        {
            m_startModel = Owner.Model;

            if (Owner is IGamePlayer iGamePlayer)
            {
                // "You go into a berserker frenzy!"
                OwnerPlayer?.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.StartFrenzy"), eChatType.CT_System, eChatLoc.CL_SystemWindow);

                // "{0} goes into a berserker frenzy!"
                Message.SystemToArea(Owner, LanguageMgr.GetTranslation(iGamePlayer.Client, "Effects.BerserkEffect.AreaStartFrenzy", Owner.GetName(0, true)), eChatType.CT_System, Owner);
            }

            if (Owner.Race == (int)eRace.Dwarf)
                Owner.Model = 2032;
            else
                Owner.Model = 582;

            Owner.Emote(eEmote.MidgardFrenzy);
        }

        public override void OnStopEffect()
        {
            Owner.Model = m_startModel;

            // there is no animation on end of the effect
            if (Owner is IGamePlayer iGamePlayer)
            {
                // "Your berserker frenzy ends."
                OwnerPlayer?.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.BerserkEffect.EndFrenzy"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                // "{0}'s berserker frenzy ends."
                Message.SystemToArea(Owner, LanguageMgr.GetTranslation(iGamePlayer.Client, "Effects.BerserkEffect.AreaEndFrenzy", Owner.GetName(0, true)), eChatType.CT_System, Owner);
            }
        }
    }
}