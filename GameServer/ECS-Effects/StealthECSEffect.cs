using System;
using System.Collections.Generic;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.Language;
using static DOL.GS.GameNPC;
using static ICSharpCode.SharpZipLib.Zip.ExtendedUnixData;

namespace DOL.GS
{
    public class StealthECSGameEffect : ECSGameAbilityEffect
    {
        public StealthECSGameEffect(ECSGameEffectInitParams initParams) : base(initParams)
        {
            EffectType = eEffect.Stealth;
            EffectService.RequestStartEffect(this);
        }

        public override ushort Icon => 0x193;
        public override string Name => LanguageMgr.GetTranslation(OwnerPlayer?.Client, "Effects.StealthEffect.Name");
        public override bool HasPositiveEffect => true;

        public override void OnStartEffect()
        {
            if (Owner is IGamePlayer gamePlayer)
            {
                if (gamePlayer is MimicNPC mimicNPC)
                    mimicNPC.Flags |= eFlags.STEALTH;

                if (gamePlayer is GamePlayer)
                {
                    if (gamePlayer.ObjectState is GameObject.eObjectState.Active)
                        gamePlayer.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client.Account.Language, "GamePlayer.Stealth.NowHidden"), eChatType.CT_System, eChatLoc.CL_SystemWindow);

                    gamePlayer.Out.SendPlayerModelTypeChange(OwnerPlayer, 3);
                }

                if (gamePlayer.EffectListComponent.ContainsEffectForEffectType(eEffect.MovementSpeedBuff))
                {
                    foreach (var speedBuff in gamePlayer.EffectListComponent.GetSpellEffects(eEffect.MovementSpeedBuff))
                    {
                        EffectService.RequestDisableEffect(speedBuff);
                    }
                }

                // Cancel pulse effects.
                List<ECSPulseEffect> effects = gamePlayer.EffectListComponent.GetAllPulseEffects();

            for (int i = 0; i < effects.Count; i++)
                EffectService.RequestCancelConcEffect(effects[i]);

            gamePlayer.Sprint(false);

            foreach (GamePlayer player in Owner.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            {
                if (player != OwnerPlayer && !player.CanDetect(Owner))
                    player.Out.SendObjectDelete(Owner);
            }

                StealthStateChanged();
            }
        }

        public override void OnStopEffect()
        {
            if (Owner is IGamePlayer gamePlayer)
            {
                if (gamePlayer is MimicNPC mimicNPC)
                    mimicNPC.Flags ^= eFlags.STEALTH;

                gamePlayer.StopStealthUncoverAction();

                if (gamePlayer.ObjectState == GameObject.eObjectState.Active)
                    gamePlayer.Out.SendMessage(LanguageMgr.GetTranslation(gamePlayer.Client.Account.Language, "GamePlayer.Stealth.NoLongerHidden"), eChatType.CT_System, eChatLoc.CL_SystemWindow);

                if (OwnerPlayer != null)
                {
                    foreach (GamePlayer otherPlayer in OwnerPlayer.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    {
                        if (otherPlayer == OwnerPlayer)
                            continue;

                        otherPlayer.Out.SendPlayerCreate(OwnerPlayer);
                        otherPlayer.Out.SendLivingEquipmentUpdate(OwnerPlayer);
                    }
                }

                if (gamePlayer.EffectListComponent.ContainsEffectForEffectType(eEffect.MovementSpeedBuff))
                {
                    var speedBuff = gamePlayer.EffectListComponent.GetBestDisabledSpellEffect(eEffect.MovementSpeedBuff);

                    if (speedBuff != null)
                    {
                        speedBuff.IsBuffActive = false;
                        EffectService.RequestEnableEffect(speedBuff);
                    }
                }

                EffectService.RequestCancelEffect(EffectListService.GetEffectOnTarget(Owner, eEffect.Vanish));
                EffectService.RequestCancelEffect(EffectListService.GetEffectOnTarget(Owner, eEffect.Camouflage));
                StealthStateChanged();
            }
        }

        private void StealthStateChanged()
        {
            if (OwnerPlayer != null)
                OwnerPlayer.Notify(GamePlayerEvent.StealthStateChanged, OwnerPlayer, null);

            Owner.OnMaxSpeedChange();
        }
    }
}
