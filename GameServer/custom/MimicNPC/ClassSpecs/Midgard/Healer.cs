namespace DOL.GS.Scripts
{
    //public class MimicHealer : MimicNPC
    //{
    //    public MimicHealer(byte level) : base(new ClassHealer(), level)
    //    { }
    //}

    public class HealerSpec : MimicSpec
    {
        public HealerSpec(eSpecType spec)
        {
            SpecName = "HealerSpec";

            WeaponOneType = eObjectType.Hammer;
            Is2H = Util.Random(1) > 0;

            var randVariance = spec switch
            {
                eSpecType.PacHealer => Util.Random(0, 5),
                eSpecType.MendHealer => Util.Random(6, 8),
                eSpecType.AugHealer => Util.Random(9, 10),
                _ => Util.Random(10),
            };

            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.PacHealer;
                Add(Specs.Pacification, 50, 1.0f);
                Add(Specs.Mending, 26, 0.4f);
                Add(Specs.Augmentation, 4, 0.1f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.PacHealer;
                Add(Specs.Pacification, 50, 1.0f);
                Add(Specs.Mending, 31, 0.4f);
                Add(Specs.Augmentation, 4, 0.1f);
                break;

                case 4:
                case 5:
                SpecType = eSpecType.PacHealer;
                Add(Specs.Pacification, 50, 1.0f);
                Add(Specs.Mending, 24, 0.4f);
                Add(Specs.Augmentation, 14, 0.1f);
                break;

                case 6:
                SpecType = eSpecType.MendHealer;
                Add(Specs.Mending, 50, 1.0f);
                Add(Specs.Augmentation, 31, 0.5f);
                Add(Specs.Pacification, 4, 0.1f);
                break;

                case 7:
                SpecType = eSpecType.MendHealer;
                Add(Specs.Mending, 50, 1.0f);
                Add(Specs.Augmentation, 26, 0.5f);
                Add(Specs.Pacification, 4, 0.1f);
                break;

                case 8:
                SpecType = eSpecType.MendHealer;
                Add(Specs.Mending, 50, 1.0f);
                Add(Specs.Augmentation, 22, 0.4f);
                Add(Specs.Pacification, 7, 0.2f);
                break;

                case 9:
                SpecType = eSpecType.AugHealer;
                Add(Specs.Augmentation, 50, 1.0f);
                Add(Specs.Mending, 24, 0.4f);
                Add(Specs.Pacification, 4, 0.1f);
                break;

                case 10:
                SpecType = eSpecType.AugHealer;
                Add(Specs.Augmentation, 50, 1.0f);
                Add(Specs.Mending, 26, 0.4f);
                Add(Specs.Pacification, 4, 0.2f);
                break;
            }
        }
    }
}