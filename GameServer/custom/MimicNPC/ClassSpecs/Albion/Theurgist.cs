using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicTheurgist : MimicNPC
    //{
    //    public MimicTheurgist(byte level) : base(new ClassTheurgist(), level)
    //    { }
    //}

    public class TheurgistSpec : MimicSpec
    {
        public TheurgistSpec(eSpecType  spec)
        {
            SpecName = "TheurgistSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.AirTheur => 0,
                eSpecType.IceTheur => 1,
                eSpecType.EarthTheur => 2,
                _ => Util.Random(2),
            };
            
            switch (randVariance)
            {
                case 0:
                // AirTheur is the classic Theurgist meta — full Wind for
                // permanent Air pet stream. Now matches the Earth/Ice variant
                // by capping Wind_Magic at 50.
                SpecType = eSpecType.AirTheur;
                Add(Specs.Earth_Magic, 16, 0.1f);
                Add(Specs.Cold_Magic, 8, 0.0f);
                Add(Specs.Wind_Magic, 50, 1.0f);
                break;

                case 1:
                SpecType = eSpecType.IceTheur;
                Add(Specs.Earth_Magic, 4, 0.0f);
                Add(Specs.Cold_Magic, 50, 1.0f);
                Add(Specs.Wind_Magic, 20, 0.1f);
                break;

                case 2:
                SpecType = eSpecType.EarthTheur;
                Add(Specs.Earth_Magic, 50, 1.0f);
                Add(Specs.Cold_Magic, 4, 0.0f);
                Add(Specs.Wind_Magic, 20, 0.1f);
                break;
            }
        }
    }
}