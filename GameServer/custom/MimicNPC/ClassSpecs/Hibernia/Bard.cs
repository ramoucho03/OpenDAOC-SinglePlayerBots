namespace DOL.GS.Scripts
{
    //public class MimicBard : MimicNPC
    //{
    //    public MimicBard(byte level) : base(new ClassBard(), level)
    //    { }
    //}

    public class BardSpec : MimicSpec
    {
        public BardSpec()
        {
            SpecName = "BardSpec";

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Blunt; break;
            }

            int randVariance = Util.Random(2);

            SpecType = eSpecType.Instrument;

            switch (randVariance)
            {
                // Music-leaning CC bard: max mez/speed line, decent heals,
                // low weapon. Weapon spec is still trained so the bot can
                // self-defend after instruments swap.
                case 0:
                Add(Specs.Music, 50, 1.0f);
                Add(Specs.Nurture, 39, 0.7f);
                Add(Specs.Regrowth, 18, 0.2f);
                Add(ObjToSpec(WeaponOneType), 18, 0.2f);
                break;

                // Hybrid healer-bard: balanced heals and CC.
                case 1:
                Add(Specs.Music, 39, 0.6f);
                Add(Specs.Nurture, 42, 0.9f);
                Add(Specs.Regrowth, 35, 0.7f);
                Add(ObjToSpec(WeaponOneType), 15, 0.2f);
                break;

                // Battle bard: weapon-spec'd for melee pressure while
                // keeping the speed song and a usable heal pool.
                case 2:
                Add(ObjToSpec(WeaponOneType), 35, 0.7f);
                Add(Specs.Music, 39, 0.5f);
                Add(Specs.Nurture, 43, 0.9f);
                Add(Specs.Regrowth, 18, 0.2f);
                break;
            }
        }
    }
}