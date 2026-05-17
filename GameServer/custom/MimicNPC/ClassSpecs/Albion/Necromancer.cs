namespace DOL.GS.Scripts
{
    public class NecromancerSpec : MimicSpec
    {
        public NecromancerSpec(eSpecType spec)
        {
            SpecName = "NecromancerSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            int randVariance = Util.Random(3);

            switch (randVariance)
            {
                case 0:
                Add(Specs.Painworking, 50, 1.0f);
                Add(Specs.Deathsight, 28, 0.2f);
                Add(Specs.Death_Servant, 12, 0.1f);
                break;

                case 1:
                Add(Specs.Deathsight, 50, 1.0f);
                Add(Specs.Painworking, 28, 0.2f);
                Add(Specs.Death_Servant, 12, 0.1f);
                break;

                case 2:
                Add(Specs.Painworking, 44, 0.8f);
                Add(Specs.Death_Servant, 35, 0.5f);
                Add(Specs.Deathsight, 18, 0.1f);
                break;

                case 3:
                Add(Specs.Deathsight, 44, 0.8f);
                Add(Specs.Painworking, 35, 0.5f);
                Add(Specs.Death_Servant, 18, 0.1f);
                break;
            }
        }
    }
}
