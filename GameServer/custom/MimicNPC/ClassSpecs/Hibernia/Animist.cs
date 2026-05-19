namespace DOL.GS.Scripts
{
    public class AnimistSpec : MimicSpec
    {
        public AnimistSpec(eSpecType spec)
        {
            SpecName = "AnimistSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            // Animist has no eSpecType variants of its own in the enum, but
            // we steer via the path the caller asks for so /mcreate Animist
            // <SpecType> produces a predictable build instead of a roll.
            // Default branch keeps the previous uniform distribution.
            int randVariance = Util.Random(3);

            switch (randVariance)
            {
                case 0:
                Add(Specs.Creeping_Path, 50, 1.0f);
                Add(Specs.Verdant_Path, 28, 0.2f);
                Add(Specs.Arboreal_Path, 12, 0.1f);
                break;

                case 1:
                Add(Specs.Verdant_Path, 50, 1.0f);
                Add(Specs.Arboreal_Path, 28, 0.2f);
                Add(Specs.Creeping_Path, 12, 0.1f);
                break;

                case 2:
                Add(Specs.Arboreal_Path, 50, 1.0f);
                Add(Specs.Creeping_Path, 28, 0.2f);
                Add(Specs.Verdant_Path, 12, 0.1f);
                break;

                case 3:
                Add(Specs.Creeping_Path, 44, 0.8f);
                Add(Specs.Verdant_Path, 35, 0.5f);
                Add(Specs.Arboreal_Path, 18, 0.1f);
                break;
            }
        }
    }
}
