using System.Collections.Generic;
using DOL.AI.Brain;
using DOL.GS.API;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.GS.Spells;
using DOL.Language;

namespace DOL.GS
{
    public class GameDuel
    {
        private const string DUEL_PREVIOUS_LASTATTACKTICKPVP = "DUEL_PREVIOUS_LASTATTACKTICKPVP";
        private const string DUEL_PREVIOUS_LASTATTACKEDBYENEMYTICKPVP = "DUEL_PREVIOUS_LASTATTACKEDBYENEMYTICKPVP";

        public GameLiving Starter { get; private set; }
        public GameLiving Target { get; private set; }

        public GameDuel(GameLiving starter, GameLiving target)
        {
            Starter = starter;
            Target = target;
        }

        public GameLiving GetPartnerOf(GameLiving living)
        {
            if (living is GameNPC npc && npc.Brain is ControlledMobBrain brain)
                living = brain.GetLivingOwner();

            return living == Starter ? Target : Starter;
        }

        public void Start()
        {
            HandleLiving(Starter, this);
            HandleLiving(Target, this);

            static void HandleLiving(GameLiving living, GameDuel duel)
            {
                ((IGamePlayer)living).OnDuelStart(duel);
                living.TempProperties.SetProperty(DUEL_PREVIOUS_LASTATTACKTICKPVP, living.LastAttackTickPvP);
                living.TempProperties.SetProperty(DUEL_PREVIOUS_LASTATTACKEDBYENEMYTICKPVP, living.LastAttackedByEnemyTickPvP);
            }
        }

        public void Stop()
        {
            HandleLiving(Starter, Target);
            HandleLiving(Target, Starter);

            static void HandleLiving(GameLiving living, GameLiving partner)
            {
                IGamePlayer player = living as IGamePlayer;

                player.OnDuelStop();
                living.LastAttackTickPvP = living.TempProperties.GetProperty<long>(DUEL_PREVIOUS_LASTATTACKTICKPVP);
                living.LastAttackedByEnemyTickPvP = living.TempProperties.GetProperty<long>(DUEL_PREVIOUS_LASTATTACKEDBYENEMYTICKPVP);

                lock (player.XpGainersLock)
                {
                    living.XPGainers.Clear();
                }

                player.Out.SendMessage(LanguageMgr.GetTranslation(player.Client, "GamePlayer.DuelStop.DuelEnds"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);

                StopEffects(living, partner);
            }

            static void StopEffects(GameLiving living, GameLiving caster)
            {
                Loop(living.effectListComponent.GetAllEffects(), caster);

                IControlledBrain controlledBrain = living.ControlledBrain;

                if (controlledBrain != null)
                    Loop(controlledBrain.Body.effectListComponent.GetAllEffects(), caster);

                static void Loop(List<ECSGameEffect> effects, GameLiving caster)
                {
                    GameNPC petCaster = caster.ControlledBrain?.Body;

                    foreach (ECSGameEffect effect in effects)
                    {
                        ISpellHandler spellHandler = effect.SpellHandler;

                        if (spellHandler == null)
                            continue;

                        if (spellHandler.Caster == caster || (spellHandler.Caster != null && spellHandler.Caster == petCaster))
                        {
                            effect.TriggersImmunity = false;
                            EffectService.RequestCancelEffect(effect);
                        }
                    }
                }
            }
        }
    }
}
