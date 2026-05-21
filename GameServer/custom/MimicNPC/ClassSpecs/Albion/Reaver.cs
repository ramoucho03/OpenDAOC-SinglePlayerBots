using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicReaver : MimicNPC
    //{
    //    public MimicReaver(byte level) : base(new ClassReaver(), level)
    //    { }
    //}

    public class ReaverSpec : MimicSpec
    {
        public ReaverSpec()
        {
            SpecName = "ReaverSpec";

            int randBaseWeap = Util.Random(4);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.SlashingWeapon; break;
                case 1: WeaponOneType = eObjectType.ThrustWeapon; break;
                case 2: WeaponOneType = eObjectType.CrushingWeapon; break;
                case 3:
                case 4: WeaponOneType = eObjectType.Flexible; break;
            }

            SpecType = eSpecType.OneHandAndShield;

            // Previously cases 0/1 were strict duplicates, so Util.Random
            // produced essentially two builds. Split into three real Reaver
            // flavours — shield-tank, Soulrending pressure, and balanced.
            int randVariance = Util.Random(2);

            switch (randVariance)
            {
                case 0:
                // Shield-tank Reaver: maxed weapon line for big anytime-style
                // damage, a Slam-capable shield (42), Soulrending kept high
                // enough for the lifetap styles and group debuff utility.
                // The group-friendly off-tank build.
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Soulrending, 39, 0.8f);
                Add(Specs.Shields, 42, 0.7f);
                Add(Specs.Parry, 21, 0.3f);
                break;

                case 1:
                // Soulrending pressure Reaver: max Soulrending for the
                // strongest lifedrain DoT styles and PBAoE procs — best solo
                // sustain and RvR pressure. Shield 28 still reaches Slam.
                Add(Specs.Soulrending, 50, 1.0f);
                Add(ObjToSpec(WeaponOneType), 44, 0.9f);
                Add(Specs.Shields, 28, 0.5f);
                Add(Specs.Parry, 18, 0.3f);
                break;

                case 2:
                // Balanced Reaver: high weapon and Soulrending with a mid
                // shield — flexible across solo, group and RvR.
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Soulrending, 44, 0.9f);
                Add(Specs.Shields, 36, 0.6f);
                Add(Specs.Parry, 12, 0.2f);
                break;
            }
        }
    }
}