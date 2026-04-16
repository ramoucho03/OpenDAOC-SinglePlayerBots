using System;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.Language;

namespace DOL.GS
{
    public class SprintECSGameEffect : ECSGameAbilityEffect
    {
        public SprintECSGameEffect(ECSGameEffectInitParams initParams) : base(initParams) 
        {
            EffectType = eEffect.Sprint;
            NextTick = GameLoop.GameLoopTime + 1;
            PulseFreq = 200;
            EffectService.RequestStartEffect(this);
        }

        private int _idleTicks = 0;

        public override ushort Icon => 0x199;
        public override string Name => LanguageMgr.GetTranslation(OwnerPlayer?.Client, "Effects.SprintEffect.Name");
        public override bool HasPositiveEffect => true;

        public override long GetRemainingTimeForClient()
        {
            return 1000;
        }

        public override void OnStartEffect()
        {
            if (Owner != null && Owner is IGamePlayer gamePlayer)
            {
                int regen = gamePlayer.GetModified(eProperty.EnduranceRegenerationAmount);
                var enduranceChant = gamePlayer.GetModified(eProperty.FatigueConsumption);
                var cost = -5 + regen;

                if (enduranceChant > 1)
                    cost = (int) Math.Ceiling(cost * enduranceChant * 0.01);

                gamePlayer.Endurance += cost;
                gamePlayer.Out.SendUpdateMaxSpeed();
                gamePlayer.Out.SendMessage(LanguageMgr.GetTranslation(gamePlayer.Client.Account.Language, "GamePlayer.Sprint.PrepareSprint"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                ((GameLiving)gamePlayer).StartEnduranceRegeneration();
            }
        }

        public override void OnStopEffect()
        {
            if (OwnerPlayer != null)
            {
                OwnerPlayer.Out.SendUpdateMaxSpeed();
                OwnerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client.Account.Language, "GamePlayer.Sprint.NoLongerReady"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        }

        public override void OnEffectPulse()
        {
            if (Owner.IsMoving)
                _idleTicks = 0;
            else
                _idleTicks++;

            if (Owner.Endurance - 5 <= 0 || _idleTicks >= 30)
                EffectService.RequestCancelEffect(this);
        }
    }
}
