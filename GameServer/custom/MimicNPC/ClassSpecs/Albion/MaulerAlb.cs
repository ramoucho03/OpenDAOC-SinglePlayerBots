namespace DOL.GS.Scripts
{
    /// <summary>
    /// Mauler (Albion) spec table for mimics. Catacombs Albion light-melee
    /// hybrid using either Mauler Staff (2H reach) or Fist Wraps (DW punch).
    /// Aura Manipulation / Magnetism / Power Strikes form the three magic
    /// paths that deliver self-buffs and disruptive procs. Two orientations:
    ///   - Staff: 2H reach, max Mauler Staff + Power Strikes
    ///   - FistWraps: DW H2H, max Fist Wraps + Magnetism
    /// </summary>
    public class MaulerAlbSpec : MimicSpec
    {
        public MaulerAlbSpec()
        {
            SpecName = "MaulerAlbSpec";

            int randStyle = Util.Random(1);
            switch (randStyle)
            {
                case 0:
                    // Staff variant
                    WeaponOneType = eObjectType.MaulerStaff;
                    Is2H = true;
                    SpecType = eSpecType.TwoHanded;
                    Add(Specs.Mauler_Staff, 50, 0.9f);
                    Add(Specs.Power_Strikes, 39, 0.6f);
                    Add(Specs.Aura_Manipulation, 30, 0.4f);
                    Add(Specs.Magnetism, 18, 0.3f);
                    break;

                case 1:
                    // Fist Wraps DW variant
                    WeaponOneType = eObjectType.FistWraps;
                    SpecType = eSpecType.DualWield;
                    Add(Specs.Fist_Wraps, 50, 0.9f);
                    Add(Specs.Magnetism, 39, 0.6f);
                    Add(Specs.Aura_Manipulation, 30, 0.4f);
                    Add(Specs.Power_Strikes, 18, 0.3f);
                    break;
            }
        }
    }
}
