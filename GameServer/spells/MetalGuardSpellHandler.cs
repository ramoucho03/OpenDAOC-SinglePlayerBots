using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.GS.Scripts;

namespace DOL.GS.Spells
{
    [SpellHandler(eSpellType.MetalGuard)]
    public class MetalGuardSpellHandler : ArmorAbsorptionBuff
    {
        public override List<GameLiving> SelectTargets(GameObject castTarget)
        {
            var list = GameLoop.GetListForTick<GameLiving>();
            GameLiving target = castTarget as GameLiving;

            if (Caster is IGamePlayer)
            {
                IGamePlayer casterPlayer = (IGamePlayer)Caster;
                Group group = casterPlayer.Group;

                if (group == null) 
                    return list; // Should not appen since it is checked in ability handler

                int spellRange = Spell.CalculateEffectiveRange(Caster);

                if (group != null)
                {
                    List<IGamePlayer> iGamePlayers = 
                        casterPlayer.GetPlayersInRadius((ushort)m_spell.Radius)
                        .Cast<IGamePlayer>().Concat(casterPlayer.GetNPCsInRadius((ushort)m_spell.Radius)
                        .OfType<IGamePlayer>()).ToList();

                    lock (group)
                    {
                        foreach (IGamePlayer groupPlayer in iGamePlayers)
                        {
                            if (casterPlayer.Group.IsInTheGroup((GameLiving)groupPlayer))
                            {
                                if (groupPlayer != casterPlayer && groupPlayer.IsAlive)
                                {
                                    list.Add((GameLiving)groupPlayer);
                                    IControlledBrain npc = groupPlayer.ControlledBrain;

                                    if (npc != null)
                                        if (casterPlayer.IsWithinRadius( npc.Body, spellRange ))
                                            list.Add(npc.Body);
                                }
                            }
                        }
                    }
                }
            }
            return list;
        }    	    	
        public MetalGuardSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
    }
}
