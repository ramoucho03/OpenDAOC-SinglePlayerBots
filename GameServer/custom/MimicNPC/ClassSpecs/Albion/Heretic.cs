using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Heretic spec table for mimics. Catacombs Albion Acolyte sub-class
    /// integrated with the live DAoC spec layout:
    ///   - Rejuvenation spec: baseline heals + Heretic Rejuvenation Spec
    ///     specline (Arawn's Fire DD/snare/DoT content)
    ///   - Enhancement spec: baseline buffs + Heretic Enhancement Spec
    ///     specline (Cthonic Accretion self buffs / dmg-add / absorb)
    ///   - Flexible weapon for melee + Shield for defensive
    /// Three role orientations:
    ///   - RejuvCleric : heal-first with light Arawn pressure (low Rejuv spec)
    ///   - EnhanceCleric: full self-buffed melee fighter (high Enhance spec)
    ///   - SmiteCleric : Arawn's Fire focused caster DPS (very high Rejuv spec)
    /// </summary>
    public class HereticSpec : MimicSpec
    {
        public HereticSpec(eSpecType spec)
        {
            SpecName = "HereticSpec";
            WeaponOneType = eObjectType.Flexible;

            int randVariance = spec switch
            {
                eSpecType.RejuvCleric => Util.Random(0, 1),
                eSpecType.EnhanceCleric => Util.Random(2, 3),
                eSpecType.SmiteCleric => Util.Random(4, 5),
                _ => Util.Random(5),
            };

            switch (randVariance)
            {
                // Rejuv-leaning: heal-first, light Arawn pressure
                case 0:
                    SpecType = eSpecType.RejuvCleric;
                    Add(Specs.Rejuvenation, 46, 0.8f);
                    Add(Specs.Enhancement, 30, 0.4f);
                    Add(Specs.Flexible, 12, 0.1f);
                    break;
                case 1:
                    SpecType = eSpecType.RejuvCleric;
                    Add(Specs.Rejuvenation, 50, 0.9f);
                    Add(Specs.Enhancement, 24, 0.3f);
                    Add(Specs.Flexible, 14, 0.1f);
                    break;
                // Enhance/melee: high Enhancement → Cthonic Accretion buffs + melee
                case 2:
                    SpecType = eSpecType.EnhanceCleric;
                    Add(Specs.Enhancement, 46, 0.8f);
                    Add(Specs.Flexible, 35, 0.5f);
                    Add(Specs.Rejuvenation, 20, 0.2f);
                    Add(Specs.Shields, 20, 0.2f);
                    break;
                case 3:
                    SpecType = eSpecType.EnhanceCleric;
                    Add(Specs.Enhancement, 50, 0.9f);
                    Add(Specs.Flexible, 38, 0.5f);
                    Add(Specs.Rejuvenation, 15, 0.1f);
                    Add(Specs.Shields, 22, 0.2f);
                    break;
                // Smite-leaning: very high Rejuv → Arawn's Fire offensive caster
                case 4:
                    SpecType = eSpecType.SmiteCleric;
                    Add(Specs.Rejuvenation, 50, 0.9f);
                    Add(Specs.Enhancement, 25, 0.3f);
                    Add(Specs.Flexible, 12, 0.1f);
                    break;
                case 5:
                    SpecType = eSpecType.SmiteCleric;
                    Add(Specs.Rejuvenation, 48, 0.9f);
                    Add(Specs.Enhancement, 30, 0.4f);
                    Add(Specs.Flexible, 10, 0.1f);
                    break;
            }
        }
    }
}
