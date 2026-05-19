using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicCleric : MimicNPC
    //{
    //    public MimicCleric(byte level) : base(new ClassCleric(), level)
    //    { }
    //}

    public class ClericSpec : MimicSpec
    {
        public ClericSpec(eSpecType spec)
        {
            SpecName = "ClericSpec";

            WeaponOneType = eObjectType.CrushingWeapon;

            var randVariance = spec switch
            {
                eSpecType.EnhanceCleric => Util.Random(0, 3),
                eSpecType.RejuvCleric => Util.Random(4, 5),
                eSpecType.SmiteCleric => Util.Random(6, 7),
                _ => Util.Random(7),
            };

            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.EnhanceCleric;
                Add(Specs.Enhancement, 50, 1.0f);
                Add(Specs.Rejuvenation, 30, 0.4f);
                Add(Specs.Smite, 7, 0.0f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.EnhanceCleric;
                Add(Specs.Enhancement, 50, 1.0f);
                Add(Specs.Rejuvenation, 33, 0.4f);
                Add(Specs.Smite, 4, 0.0f);
                break;

                case 4:
                SpecType = eSpecType.RejuvCleric;
                Add(Specs.Rejuvenation, 46, 0.8f);
                Add(Specs.Enhancement, 28, 0.5f);
                Add(Specs.Smite, 4, 0.0f);
                break;

                case 5:
                SpecType = eSpecType.RejuvCleric;
                Add(Specs.Rejuvenation, 50, 0.8f);
                Add(Specs.Enhancement, 20, 0.5f);
                Add(Specs.Smite, 4, 0.0f);
                break;

                case 6:
                SpecType = eSpecType.SmiteCleric;
                Add(Specs.Smite, 50, 1.0f);
                Add(Specs.Enhancement, 26, 0.4f);
                Add(Specs.Rejuvenation, 6, 0.0f);
                break;

                case 7:
                SpecType = eSpecType.SmiteCleric;
                Add(Specs.Smite, 50, 1.0f);
                Add(Specs.Enhancement, 33, 0.4f);
                Add(Specs.Rejuvenation, 4, 0.0f);
                break;
            }
        }
    }
}