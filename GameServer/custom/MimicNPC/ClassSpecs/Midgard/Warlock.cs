namespace DOL.GS.Scripts
{
    /// <summary>
    /// Warlock spec table for mimics. Catacombs Midgard Mystic sub-class that
    /// uses the unique "chamber" system: primary spell from Hexing/Witchcraft
    /// is stored in one of three chambers (Cursing) and released instantly on
    /// demand. We don't model chamber mechanics here, just the spec spread so
    /// the mimic has plenty of harmful spells to pull from. Three orientations:
    ///   - Cursing: snares / DD chambers (main control)
    ///   - Hexing: DD nukes + bolts
    ///   - Witchcraft: DoT-heavy pressure
    /// </summary>
    public class WarlockSpec : MimicSpec
    {
        public WarlockSpec()
        {
            SpecName = "WarlockSpec";

            // Warlock holds a 1H weapon (Sword/Hammer/Axe) only for melee fallback;
            // primary combat is spell-based. Stick to Hammer as a neutral default
            // — base mystic line, no point random-rolling.
            WeaponOneType = eObjectType.Hammer;
            // No matching caster SpecType exists; leave as None (default).

            int randVariance = Util.Random(2);
            switch (randVariance)
            {
                // Cursing-leaning: chamber + snare-heavy
                case 0:
                    Add(Specs.Cursing, 50, 0.9f);
                    Add(Specs.Hexing, 35, 0.5f);
                    Add(Specs.Witchcraft, 22, 0.3f);
                    break;

                // Hexing-leaning: pure nuke
                case 1:
                    Add(Specs.Hexing, 50, 0.9f);
                    Add(Specs.Witchcraft, 35, 0.5f);
                    Add(Specs.Cursing, 18, 0.3f);
                    break;

                // Witchcraft-leaning: DoT-heavy
                case 2:
                    Add(Specs.Witchcraft, 50, 0.9f);
                    Add(Specs.Cursing, 35, 0.5f);
                    Add(Specs.Hexing, 22, 0.3f);
                    break;
            }
        }
    }
}
