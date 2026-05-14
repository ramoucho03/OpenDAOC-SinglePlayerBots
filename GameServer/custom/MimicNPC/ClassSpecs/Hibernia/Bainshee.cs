namespace DOL.GS.Scripts
{
    public class BainsheeSpec : MimicSpec
    {
        public BainsheeSpec(eSpecType spec)
        {
            SpecName = "BainsheeSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            int randVariance = Util.Random(3);

            switch (randVariance)
            {
                case 0:
                Add(Specs.PhantasmalWail, 50, 1.0f);
                Add(Specs.EtherealShriek, 28, 0.2f);
                Add(Specs.SpectralForce, 12, 0.1f);
                break;

                case 1:
                Add(Specs.EtherealShriek, 50, 1.0f);
                Add(Specs.PhantasmalWail, 28, 0.2f);
                Add(Specs.SpectralForce, 12, 0.1f);
                break;

                case 2:
                Add(Specs.SpectralForce, 44, 0.8f);
                Add(Specs.PhantasmalWail, 35, 0.5f);
                Add(Specs.EtherealShriek, 18, 0.1f);
                break;

                case 3:
                Add(Specs.PhantasmalWail, 44, 0.8f);
                Add(Specs.EtherealShriek, 35, 0.5f);
                Add(Specs.SpectralForce, 18, 0.1f);
                break;
            }
        }
    }
}
