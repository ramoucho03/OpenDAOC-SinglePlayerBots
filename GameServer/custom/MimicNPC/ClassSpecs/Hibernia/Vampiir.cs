namespace DOL.GS.Scripts
{
    /// <summary>
    /// Vampiir spec table for mimics. Catacombs Hibernia melee/caster hybrid
    /// that pays mana cost from CON instead of acuity and self-heals via
    /// Vampiiric Embrace drains. Three orientations:
    ///   - Dementia: weapon + self-buffs + drain DD (default melee DPS)
    ///   - VampiiricEmbrace: heavy drain spec for sustain in long fights
    ///   - ShadowMastery: utility / disease / debuff variant
    /// </summary>
    public class VampiirSpec : MimicSpec
    {
        public VampiirSpec()
        {
            SpecName = "VampiirSpec";

            // Vampiir uses a 1H Piercing weapon. The DB classxspecialization
            // table for Vampiir (class 58) only includes Piercing as a weapon
            // spec — Blades/Blunt are silently rejected by SpendSpecPoints,
            // which would leave the bot under-leveled in its weapon line.
            WeaponOneType = eObjectType.Piercing;

            SpecType = eSpecType.OneHandHybrid;

            int randVariance = Util.Random(2);
            switch (randVariance)
            {
                // Dementia-leaning: heavy weapon, mid drain, light shadow
                case 0:
                    Add(ObjToSpec(WeaponOneType), 50, 0.9f);
                    Add(Specs.Dementia, 39, 0.6f);
                    Add(Specs.VampiiricEmbrace, 30, 0.4f);
                    Add(Specs.ShadowMastery, 12, 0.2f);
                    // No Stealth: Vampiirs do not have Stealth in DAoC live.
                    break;

                // VampiiricEmbrace-leaning: max drains for sustain
                case 1:
                    Add(ObjToSpec(WeaponOneType), 44, 0.8f);
                    Add(Specs.VampiiricEmbrace, 50, 0.9f);
                    Add(Specs.Dementia, 26, 0.4f);
                    Add(Specs.ShadowMastery, 12, 0.2f);
                    break;

                // ShadowMastery-leaning: utility / debuff variant
                case 2:
                    Add(ObjToSpec(WeaponOneType), 44, 0.8f);
                    Add(Specs.ShadowMastery, 50, 0.9f);
                    Add(Specs.Dementia, 30, 0.4f);
                    Add(Specs.VampiiricEmbrace, 18, 0.2f);
                    break;
            }
        }
    }
}
