using DOL.GS.API;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.Language;

namespace DOL.GS
{
    public class ShadeECSGameEffect : ECSGameAbilityEffect
    {
        public override ushort Icon => 0x193;
        public override string Name
        {
            get
            {
                if (Owner is IGamePlayer iPlayer)
                    return LanguageMgr.GetTranslation(iPlayer.Client, "Effects.ShadeEffect.Name");
                else
                    return "Shade";
            }
        }

        public override bool HasPositiveEffect => false;

        public ShadeECSGameEffect(in ECSGameEffectInitParams initParams) : base(initParams)
        {
            EffectType = eEffect.Shade;
        }

        public override void OnStartEffect()
        {
            IGamePlayer iPlayer = Owner as IGamePlayer;

            if (iPlayer == null)
                return;

            if (iPlayer.HasShadeModel)
                return;

            iPlayer.Shade(true);
            iPlayer.Model = iPlayer.ShadeModel;
        }

        public override void OnStopEffect()
        {
            IGamePlayer iPlayer = Owner as IGamePlayer;

            if (iPlayer == null)
                return;

            if (!iPlayer.HasShadeModel)
                return;

            iPlayer.Shade(false);
            iPlayer.Model = OwnerPlayer.CreationModel;
            iPlayer.Out.SendMessage(LanguageMgr.GetTranslation(iPlayer.Client.Account.Language, "GamePlayer.Shade.NoLongerShade"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}
