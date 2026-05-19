using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicShaman : MimicNPC
    //{
    //    public MimicShaman(byte level) : base(new ClassShaman(), level)
    //    { }
    //}

    public class ShamanSpec : MimicSpec
    {
        public ShamanSpec(eSpecType spec)
        {
            SpecName = "ShamanSpec";

            WeaponOneType = eObjectType.Hammer;
            Is2H = Util.Random(1) > 0;

            var randVariance = spec switch
            {
                eSpecType.AugShaman => Util.Random(0, 4),
                eSpecType.SubtShaman => Util.Random(5, 6),
                eSpecType.MendShaman => 7,
                _ => Util.Random(7),
            };
            
            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.AugShaman;
                Add(Specs.Augmentation, 50, 1.0f);
                Add(Specs.Subterranean, 22, 0.4f);
                Add(Specs.Mending, 8, 0.2f);
                break;

                case 2:
                SpecType = eSpecType.AugShaman;
                Add(Specs.Augmentation, 50, 1.0f);
                Add(Specs.Mending, 22, 0.4f);
                Add(Specs.Subterranean, 5, 0.0f);
                break;

                case 3:
                SpecType = eSpecType.AugShaman;
                Add(Specs.Augmentation, 50, 1.0f);
                Add(Specs.Mending, 26, 0.4f);
                Add(Specs.Subterranean, 7, 0.0f);
                break;

                case 4:
                SpecType = eSpecType.AugShaman;
                Add(Specs.Augmentation, 50, 1.0f);
                Add(Specs.Mending, 18, 0.4f);
                Add(Specs.Subterranean, 12, 0.0f);
                break;

                case 5:
                SpecType = eSpecType.SubtShaman;
                Add(Specs.Subterranean, 50, 1.0f);
                Add(Specs.Augmentation, 24, 0.4f);
                Add(Specs.Mending, 4, 0.2f);
                break;

                case 6:
                SpecType = eSpecType.SubtShaman;
                Add(Specs.Subterranean, 50, 1.0f);
                Add(Specs.Mending, 22, 0.4f);
                Add(Specs.Augmentation, 8, 0.1f);
                break;

                case 7:
                SpecType = eSpecType.MendShaman;
                Add(Specs.Mending, 50, 1.0f);
                Add(Specs.Augmentation, 26, 0.4f);
                Add(Specs.Subterranean, 6, 0.0f);
                break;
            }
        }
    }
}