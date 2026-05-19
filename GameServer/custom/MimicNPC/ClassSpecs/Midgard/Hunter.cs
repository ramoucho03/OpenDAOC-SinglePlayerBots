using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicHunter : MimicNPC
    //{
    //    public MimicHunter(byte level) : base(new ClassHunter(), level)
    //    { }
    //}

    public class HunterSpec : MimicSpec
    {
        public HunterSpec()
        {
            SpecName = "HunterSpec";

            int randBaseWeap = Util.Random(2);

            switch (randBaseWeap)
            {
                case 0:
                case 1: WeaponOneType = eObjectType.Spear; break;
                case 2: WeaponOneType = eObjectType.Sword; break;
            }

            // Hunter Spear is 2H, Sword is 1H. Previously this was forced to
            // true unconditionally, which would request a 2H Sword the
            // equipment layer can't fit in the main slot when the Sword roll
            // landed (case 2).
            Is2H = WeaponOneType == eObjectType.Spear;

            int randVariance = Util.Random(4);

            SpecType = eSpecType.None;

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.CompositeBow, 35, 0.9f);
                Add(Specs.Beastcraft, 40, 0.6f);
                Add(Specs.Stealth, 38, 0.3f);
                break;

                case 2:
                case 3:
                Add(ObjToSpec(WeaponOneType), 39, 0.8f);
                Add(Specs.CompositeBow, 45, 0.9f);
                Add(Specs.Beastcraft, 32, 0.6f);
                Add(Specs.Stealth, 38, 0.3f);
                break;

                case 4:
                // Beastmaster Hunter: heavy melee + pet, but Hunter must
                // still keep some bow spec or it can't open from stealth
                // before closing. 25 CompositeBow is enough for the
                // open-and-switch pattern.
                Add(ObjToSpec(WeaponOneType), 44, 0.9f);
                Add(Specs.CompositeBow, 25, 0.4f);
                Add(Specs.Beastcraft, 50, 0.8f);
                Add(Specs.Stealth, 37, 0.5f);
                break;
            }
        }
    }
}