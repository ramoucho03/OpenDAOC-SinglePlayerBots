using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    [SpellHandler(eSpellType.BainsheePulseDmg)]
	public class BainsheePulseDmgSpellHandler : SpellHandler
	{
		public const string FOCUS_WEAK = "FocusSpellHandler.Online";

		public override void FinishSpellCast(GameLiving target)
		{
			m_caster.Mana -= PowerCost(target);
			base.FinishSpellCast(target);
		}

		#region LOS on Keeps

		public override void OnDirectEffect(GameLiving target)
		{
			if (target == null)
				return;

			if (Spell.Target is eSpellTarget.CONE || (Spell.Target is eSpellTarget.ENEMY && Spell.IsPBAoE))
			{
				if (!Caster.castingComponent.StartEndOfCastLosCheck(target, this))
					DealDamage(target);
			}
			else
				DealDamage(target);
		}

		public override void OnEndOfCastLosCheck(GameLiving target, LosCheckResponse response)
		{
			if (response is LosCheckResponse.True)
				DealDamage(target);
		}

		private void DealDamage(GameLiving target)
		{
			if (!target.IsAlive || target.ObjectState != GameLiving.eObjectState.Active) return;

			AttackData ad = CalculateDamageToTarget(target);
			DamageTarget(ad, true);
			SendDamageMessages(ad);
			target.StartInterruptTimer(target.SpellInterruptDuration, ad.AttackType, Caster);
		}

		#endregion

        public BainsheePulseDmgSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
	}
}
