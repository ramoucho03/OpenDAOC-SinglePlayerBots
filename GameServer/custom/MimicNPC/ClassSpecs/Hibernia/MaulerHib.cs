namespace DOL.GS.Scripts
{
    /// <summary>
    /// Mauler (Hibernia) spec table for mimics. Same spec layout as the
    /// Albion and Midgard Maulers.
    /// </summary>
    public class MaulerHibSpec : MimicSpec
    {
        public MaulerHibSpec()
        {
            SpecName = "MaulerHibSpec";

            int randStyle = Util.Random(1);
            switch (randStyle)
            {
                case 0:
                    WeaponOneType = eObjectType.MaulerStaff;
                    Is2H = true;
                    SpecType = eSpecType.TwoHanded;
                    Add(Specs.Mauler_Staff, 50, 0.9f);
                    Add(Specs.Power_Strikes, 39, 0.6f);
                    Add(Specs.Aura_Manipulation, 30, 0.4f);
                    Add(Specs.Magnetism, 18, 0.3f);
                    break;

                case 1:
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
