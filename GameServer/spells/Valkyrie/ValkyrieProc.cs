using System;
using DOL.Database;
using DOL.Events;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    [SpellHandler(eSpellType.ValkyrieOffensiveProc)]
    public class ValkyrieOffensiveProcSpellHandler : SpellHandler
    {
        /// <summary>
        /// Constants data change this to modify chance increase or decrease
        /// </summary>
        public override void OnEffectStart(GameSpellEffect effect)
        {
            base.OnEffectStart(effect);
            // "Your weapon is blessed by the gods!"
            // "{0}'s weapon glows with the power of the gods!"
            eChatType chatType = eChatType.CT_SpellPulse;
            if (Spell.Pulse == 0)
            {
                chatType = eChatType.CT_Spell;
            }
            MessageToLiving(effect.Owner, Spell.Message1, chatType);
            Message.SystemToArea(effect.Owner, Util.MakeSentence(Spell.Message2, effect.Owner.GetName(0, true)), chatType, effect.Owner);
            GameEventMgr.AddHandler(effect.Owner, GameLivingEvent.AttackFinished, new DOLEventHandler(EventHandler));
        }

        public override int OnEffectExpires(GameSpellEffect effect, bool noMessages)
        {
            if (!noMessages)
            {
                MessageToLiving(effect.Owner, Spell.Message3, eChatType.CT_SpellExpires);
                Message.SystemToArea(effect.Owner, Util.MakeSentence(Spell.Message4, effect.Owner.GetName(0, true)), eChatType.CT_SpellExpires, effect.Owner);
            }
            GameEventMgr.RemoveHandler(effect.Owner, GameLivingEvent.AttackFinished, new DOLEventHandler(EventHandler));
            return 0;
        }

        public void EventHandler(DOLEvent e, object sender, EventArgs arguments)
        {
            AttackFinishedEventArgs args = arguments as AttackFinishedEventArgs;
            if (args == null || args.AttackData == null)
            {
                return;
            }
            AttackData ad = args.AttackData;
            if (ad.AttackResult != eAttackResult.HitUnstyled && ad.AttackResult != eAttackResult.HitStyle)
                return;

            // Use the spell-defined proc chance (stored as percent * 100 in Frequency).
            int baseChance = Spell.Frequency / 100;

            if (ad.AttackType == AttackData.eAttackType.Ranged)
            {
                // Ranged attacks use a reduced chance.
                baseChance = Math.Max(1, baseChance / 2);
            }
            else if (ad.IsMeleeAttack && sender is GamePlayer player)
            {
                DbInventoryItem leftWeapon = player.ActiveLeftWeapon;

                // When dual wielding (non-shield offhand), halve the chance per swing.
                if (player.attackComponent.CanUseLefthandedWeapon && leftWeapon != null && leftWeapon.Object_Type != (int)eObjectType.Shield)
                    baseChance = Math.Max(1, baseChance / 2);
            }

            if (baseChance <= 0 || !Util.Chance(baseChance))
                return;

            Spell m_procSpell = SkillBase.GetSpellByID((int)Spell.Value);

            if (m_procSpell == null)
                return;

            SpellLine reservedLine = SkillBase.GetSpellLine(GlobalSpellsLines.Reserved_Spells);
            ISpellHandler handler = ScriptMgr.CreateSpellHandler((GameLiving)sender, m_procSpell, reservedLine);

            if (handler == null)
                return;

            if (m_procSpell.Target == eSpellTarget.ENEMY)
                handler.StartSpell(ad.Target);
            else
                handler.StartSpell(ad.Attacker);
        }

        // constructor
        public ValkyrieOffensiveProcSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
    }
}
