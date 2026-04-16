using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.Language;

namespace DOL.GS.Commands
{
    [CmdAttribute(
         "&duel",
         ePrivLevel.Player,
         "Duel another player",
         "/duel")]

    public class DuelCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private const string DUEL_STARTER_WEAK = "DuelStarter";
        private const string CHALLENGE_TARGET_WEAK = "DuelTarget";

        public void OnCommand(GameClient client, string[] args)
        {
            if (IsSpammingCommand(client.Player, "duel"))
                return;

            switch (client.Player.CurrentRegionID)
            {
                case 10:
                case 101:
                case 201:
                {
                    DisplayMessage(client, LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.SafeZone"), new object[] { });
                    return;
                }
            }

            WeakRef weak = null;
            IGamePlayer duelStarter = null;
            IGamePlayer duelTarget = null;

            if (args.Length > 1)
            {
                switch (args[1].ToLower())
                {
                    case "challenge":
                    {
                        if (client.Player.TargetObject == null || client.Player.TargetObject == client.Player || client.Player.TargetObject is not IGamePlayer)
                        {
                            client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.NeedTarget"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                            return;
                        }

                        GameLiving target = client.Player.TargetObject as GameLiving;

                        if (!CheckDuelStart(client.Player, target))
                            return;

						weak = client.Player.TempProperties.GetProperty<WeakRef>(CHALLENGE_TARGET_WEAK);
						if (weak != null && (duelTarget = weak.Target as IGamePlayer) != null)
						{
							client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouAlreadyChallenging", duelTarget.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
							return;
						}
						weak = client.Player.TempProperties.GetProperty<WeakRef>(DUEL_STARTER_WEAK);
						if (weak != null && (duelStarter = weak.Target as IGamePlayer) != null)
						{
							client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouAlreadyConsidering", duelStarter.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
							return;
						}

						if (target.TempProperties.GetProperty<WeakRef>(DUEL_STARTER_WEAK) != null)
						{
							client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.TargetAlreadyConsidering", target.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
							return;
						}
						if (target.TempProperties.GetProperty<WeakRef>(CHALLENGE_TARGET_WEAK) != null)
						{
							client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.TargetAlreadyChallenging", target.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
							return;
						}

						target.TempProperties.SetProperty(DUEL_STARTER_WEAK, new WeakRef(client.Player));
						client.Player.TempProperties.SetProperty(CHALLENGE_TARGET_WEAK, new WeakRef(target));
						client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouChallenge", target.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						((IGamePlayer)target).Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.ChallengesYou", client.Player.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						return;
					}

					case "accept":
					{
						weak = client.Player.TempProperties.GetProperty<WeakRef>(DUEL_STARTER_WEAK);

                        if (weak == null || (duelStarter = weak.Target as IGamePlayer) == null)
                        {
                            client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.ConsideringDuel"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                            return;
                        }

                        if (!CheckDuelStart(client.Player, (GameLiving)duelStarter))
                            return;

						GameDuel duel = new((GameLiving)duelStarter, client.Player);
						duel.Start();
						duelStarter.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.TargetAccept", client.Player.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						client.Player.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouAccept"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						client.Player.TempProperties.RemoveProperty(DUEL_STARTER_WEAK);
						duelStarter.TempProperties.RemoveProperty(CHALLENGE_TARGET_WEAK);
						return;
					}

					case "decline":
					{
						weak = client.Player.TempProperties.GetProperty<WeakRef>(DUEL_STARTER_WEAK);
						client.Player.TempProperties.RemoveProperty(DUEL_STARTER_WEAK);

                        if (weak == null || (duelStarter = weak.Target as GamePlayer) == null)
                        {
                            client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.NotInDuel"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                            return;
                        }


						duelStarter.TempProperties.RemoveProperty(CHALLENGE_TARGET_WEAK);
						duelStarter.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.TargetDeclines", client.Player.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouDecline", duelStarter.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						return;
					}

					case "cancel":
					{
						weak = client.Player.TempProperties.GetProperty<WeakRef>(CHALLENGE_TARGET_WEAK);
						client.Player.TempProperties.RemoveProperty(CHALLENGE_TARGET_WEAK);

                        if (weak == null || (duelTarget = weak.Target as GamePlayer) == null)
                        {
                            client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouHaventChallenged"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                            return;
                        }

						duelTarget.TempProperties.RemoveProperty(DUEL_STARTER_WEAK);
						duelTarget.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.TargetCancel", client.Player.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouCancel"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						return;
					}

                    case "surrender":
                    {
                        GameLiving target = client.Player.DuelPartner;

                        if (target == null)
                        {
                            client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.NotInDuel"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                            return;
                        }

						client.Player.Duel.Stop();
						client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.YouSurrender", target.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						((IGamePlayer)target).Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.TargetSurrender", client.Player.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
						Message.SystemToArea(client.Player, LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.PlayerVsPlayer", client.Player.Name, target.Name), eChatType.CT_Emote, client.Player, target);
						return;
					}
				}
			}

            client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "Scripts.Players.Duel.DuelOptions"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
        }

        /// <summary>
        /// Checks if a duel can be started between 2 players at this moment
        /// </summary>
        /// <param name="actionSource">The duel starter</param>
        /// <param name="actionTarget">The duel target</param>
        /// <returns>true if players can start a duel</returns>
        private static bool CheckDuelStart(GameLiving actionSource, GameLiving actionTarget)
        {
            IGamePlayer playerActionSource = actionSource as IGamePlayer;

            if (!GameServer.ServerRules.IsSameRealm(actionSource, actionTarget, true))
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.EnemyRealm"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (playerActionSource.DuelPartner != null)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.YouInDuel"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (((IGamePlayer)actionTarget).DuelPartner != null)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.TargetInDuel", actionTarget.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (actionTarget.InCombat)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.TargetInCombat", actionTarget.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (actionSource.InCombat)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.YouInCombat"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (actionTarget.Group != null)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.TargetInGroup", actionTarget.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (actionSource.Group != null)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.YouInGroup"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (actionSource.Health < actionSource.MaxHealth)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.YouHealth"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }
            if (actionTarget.Health < actionTarget.MaxHealth)
            {
                playerActionSource.Out.SendMessage(LanguageMgr.GetTranslation(playerActionSource.Client, "Scripts.Players.Duel.TargetHealth"), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
                return false;
            }

            return true;
        }
    }
}
