using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicMercenary : MimicNPC
    //{
    //    public MimicMercenary(byte level) : base(new ClassMercenary(), level)
    //    { }
    //}

    public class MercenarySpec : MimicSpec
    {
        public MercenarySpec(eSpecType spec)
        {
            SpecName = "MercenarySpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.SlashingWeapon; break;
                case 1: WeaponOneType = eObjectType.ThrustWeapon; break;
                case 2: WeaponOneType = eObjectType.CrushingWeapon; break;
            }

            // The Mercenary is a pure dual-wield class. Its combat profile
            // backpacks any off-hand shield (ShouldBackpackOffhandShield) and
            // the brain only swings a shield when acting as a dedicated tank —
            // which a DW Mercenary never does. A DualWieldAndShield build
            // therefore sank ~42 spec points into a Shields line that was never
            // equipped or used, so it's dropped: always roll one of the three
            // real DW flavours (DW-max, weapon-max, parry-heavy). (`spec` is
            // ignored — every Mercenary build is dual-wield.)
            int randVariance = Util.Random(0, 2);

            switch (randVariance)
            {
                case 0:
                // DW-max DPS: cap the offhand line first, mainline 47 to
                // leave a few points for parry.
                SpecType = eSpecType.DualWield;
                Add(Specs.Dual_Wield, 50, 1.0f);
                Add(ObjToSpec(WeaponOneType), 47, 0.8f);
                Add(Specs.Parry, 22, 0.2f);
                break;

                case 1:
                // Weapon-line max for big anytime style damage, DW 39 for
                // the off-hand procs but secondary.
                SpecType = eSpecType.DualWield;
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Dual_Wield, 39, 0.7f);
                Add(Specs.Parry, 28, 0.3f);
                break;

                case 2:
                // Parry-heavy survivability build with DW 44 / weapon 44.
                SpecType = eSpecType.DualWield;
                Add(ObjToSpec(WeaponOneType), 44, 0.8f);
                Add(Specs.Dual_Wield, 44, 0.8f);
                Add(Specs.Parry, 39, 0.4f);
                break;
            }
        }
    }
}