using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Heretic spec table for mimics. Catacombs Albion Acolyte sub-class
    /// integrated with the live DAoC spec layout:
    ///   - Rejuvenation baseline (heals) + Arawn's Fire specline (DD/snare/DoT)
    ///   - Enhancement baseline (buffs) + Cthonic Accretion specline (self buffs/dmg-add/absorb)
    ///   - Flexible weapon for melee + Shield for defensive
    /// Three role orientations:
    ///   - RejuvCleric : heal-first, light Arawn pressure
    ///   - EnhanceCleric: full self-buff melee fighter with chants
    ///   - SmiteCleric : Arawn's Fire focused caster DPS (mono + AoE focus)
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
                // Rejuv-leaning healer with Arawn backup DD
                case 0:
                    SpecType = eSpecType.RejuvCleric;
                    Add(Specs.Rejuvenation, 46, 0.8f);
                    Add(Specs.Enhancement, 30, 0.4f);
                    Add(Specs.Arawns_Fire, 20, 0.2f);
                    break;
                case 1:
                    SpecType = eSpecType.RejuvCleric;
                    Add(Specs.Rejuvenation, 50, 0.9f);
                    Add(Specs.Enhancement, 24, 0.3f);
                    Add(Specs.Arawns_Fire, 15, 0.1f);
                    break;
                // Enhance/melee with Cthonic self-buffs + Flexible mélée
                case 2:
                    SpecType = eSpecType.EnhanceCleric;
                    Add(Specs.Enhancement, 30, 0.3f);
                    Add(Specs.Cthonic_Accretion, 46, 0.8f);
                    Add(Specs.Flexible, 35, 0.5f);
                    Add(Specs.Shields, 20, 0.2f);
                    break;
                case 3:
                    SpecType = eSpecType.EnhanceCleric;
                    Add(Specs.Enhancement, 24, 0.3f);
                    Add(Specs.Cthonic_Accretion, 50, 0.8f);
                    Add(Specs.Flexible, 38, 0.5f);
                    Add(Specs.Shields, 22, 0.2f);
                    break;
                // Arawn's Fire caster DPS — mono + AoE focus damage with snare
                case 4:
                    SpecType = eSpecType.SmiteCleric;
                    Add(Specs.Arawns_Fire, 50, 0.9f);
                    Add(Specs.Rejuvenation, 30, 0.4f);
                    Add(Specs.Enhancement, 15, 0.2f);
                    break;
                case 5:
                    SpecType = eSpecType.SmiteCleric;
                    Add(Specs.Arawns_Fire, 48, 0.9f);
                    Add(Specs.Cthonic_Accretion, 25, 0.3f);
                    Add(Specs.Enhancement, 15, 0.2f);
                    break;
            }
        }
    }
}
