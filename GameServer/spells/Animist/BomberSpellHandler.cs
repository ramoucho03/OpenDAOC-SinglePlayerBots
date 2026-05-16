using DOL.AI.Brain;
using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    [SpellHandler(eSpellType.Bomber)]
    public class BomberSpellHandler : SummonSpellHandler
    {
        public override string ShortDescription => "Summons an elemental spirit to attack the target.";

        public BomberSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line)
        {
            m_isSilent = true;
        }

        public override bool CheckBeginCast(GameLiving selectedTarget)
        {
            if (Spell.SubSpellID == 0)
            {
                // The bomber relies on its sub-spell to explode on contact: without it the pet
                // would summon, run to its target and do nothing. The fix is on the data side:
                // populate ItemTemplate.SubSpellID / DBSpell.SubSpellID for this spell so the
                // explosion payload is wired up. Keep the cast-time refusal so the player isn't
                // silently scammed out of mana when the spell would otherwise no-op.
                MessageToCaster($"Bomber spell '{Spell.Name}' (id {Spell.ID}) has no SubSpellID configured. Ask a GM to fix the spell row.", eChatType.CT_Important);
                return false;
            }

            return base.CheckBeginCast(selectedTarget);
        }

        public override void ApplyEffectOnTarget(GameLiving target)
        {
            base.ApplyEffectOnTarget(target);

            if (m_pet is not null)
            {
                m_pet.Level = m_pet.Owner?.Level ?? 1; // No bomber class to override SetPetLevel() in, so set level here.
                m_pet.Name = Spell.Name;
                m_pet.Flags ^= GameNPC.eFlags.DONTSHOWNAME;
                m_pet.Flags ^= GameNPC.eFlags.PEACE;
                m_pet.FixedSpeed = true;
                m_pet.MaxSpeedBase = 350;
                m_pet.TargetObject = target;
                m_pet.Follow(target, 5, Spell.Range * 5);
            }
        }

        public override void OnPetReleased() { }

        protected override IControlledBrain GetPetBrain(GameLiving owner)
        {
            return new BomberBrain(owner, Spell, SpellLine);
        }

        protected override void SetBrainToOwner(IControlledBrain brain) { }

        public override void CastSubSpells(GameLiving target) { }
    }
}
