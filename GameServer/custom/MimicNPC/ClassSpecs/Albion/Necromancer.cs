namespace DOL.GS.Scripts
{
    public class NecromancerSpec : MimicSpec
    {
        public NecromancerSpec(eSpecType spec)
        {
            SpecName = "NecromancerSpec";

            // Necromancer fights through the shade-form pet. The mortal body
            // still carries a staff for the brief unshade window, but the
            // ListCaster default branch in SetWeapons already two-hands it
            // when SpecType isn't set. Explicitly mark TwoHanded so callers
            // (combat profile / equipment debugging) get a meaningful value
            // instead of the enum's first member (MatterCab).
            SpecType = eSpecType.TwoHanded;
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
