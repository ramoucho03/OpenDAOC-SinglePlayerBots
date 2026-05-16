using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Heretic spec table for mimics. Heretic = Catacombs Acolyte sub-class
    /// (Albion), uses Flexible weapon + Rejuvenation heals/chants +
    /// Enhancement buffs/chants. Three spec orientations:
    ///   - Rejuv: primary healer with chant DPS backup
    ///   - Enhance: buffer + chant pressure
    ///   - Flexible: melee chant nuker (main DPS spec)
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
                // Rejuv-leaning: primarily heals + light melee
                case 0:
                    SpecType = eSpecType.RejuvCleric;
                    Add(Specs.Rejuvenation, 46, 0.8f);
                    Add(Specs.Enhancement, 30, 0.4f);
                    Add(Specs.Flexible, 12, 0.1f);
                    break;
                case 1:
                    SpecType = eSpecType.RejuvCleric;
                    Add(Specs.Rejuvenation, 50, 0.8f);
                    Add(Specs.Enhancement, 24, 0.4f);
                    Add(Specs.Flexible, 14, 0.1f);
                    break;
                // Enhance-leaning: buffer with chant utility
                case 2:
                    SpecType = eSpecType.EnhanceCleric;
                    Add(Specs.Enhancement, 46, 0.8f);
                    Add(Specs.Rejuvenation, 30, 0.4f);
                    Add(Specs.Flexible, 14, 0.1f);
                    break;
                case 3:
                    SpecType = eSpecType.EnhanceCleric;
                    Add(Specs.Enhancement, 50, 0.8f);
                    Add(Specs.Rejuvenation, 28, 0.4f);
                    Add(Specs.Flexible, 12, 0.1f);
                    break;
                // Flexible-leaning: melee + chant DPS (closest equivalent of
                // the offensive Heretic spec — chant procs from melee swings).
                case 4:
                    SpecType = eSpecType.SmiteCleric;
                    Add(Specs.Flexible, 46, 0.8f);
                    Add(Specs.Enhancement, 30, 0.5f);
                    Add(Specs.Rejuvenation, 12, 0.1f);
                    break;
                case 5:
                    SpecType = eSpecType.SmiteCleric;
                    Add(Specs.Flexible, 50, 0.8f);
                    Add(Specs.Enhancement, 35, 0.5f);
                    Add(Specs.Rejuvenation, 6, 0.0f);
                    break;
            }
        }
    }
}
