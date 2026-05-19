using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicBonedancer : MimicNPC
    //{
    //    public MimicBonedancer(byte level) : base(new ClassBonedancer(), level)
    //    { }
    //}

    public class BonedancerSpec : MimicSpec
    {
        public BonedancerSpec(eSpecType spec)
        {
            SpecName = "BonedancerSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.SuppBone => Util.Random(0, 2),
                eSpecType.DarkBone => Util.Random(3, 4),
                eSpecType.ArmyBone => 5,
                _ => Util.Random(5),
            };

            // BoneArmy is the spec line that grants the Commander pet summon
            // (along with all subpets). Every Bonedancer build keeps it at
            // least 26 so the bot can summon its core commander — the
            // previous "Dark" and "Supp" variants had BoneArmy 4-6, which
            // meant the bot lost its pet entirely and stood there nuking
            // without its army. That's not a Bonedancer.
            switch (randVariance)
            {
                case 0:
                SpecType = eSpecType.SuppBone;
                Add(Specs.Suppression, 50, 1.0f);
                Add(Specs.BoneArmy, 35, 0.4f);
                Add(Specs.Darkness, 12, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.SuppBone;
                Add(Specs.Suppression, 48, 1.0f);
                Add(Specs.BoneArmy, 26, 0.3f);
                Add(Specs.Darkness, 20, 0.1f);
                break;

                case 2:
                SpecType = eSpecType.SuppBone;
                Add(Specs.Suppression, 44, 0.9f);
                Add(Specs.BoneArmy, 35, 0.4f);
                Add(Specs.Darkness, 18, 0.1f);
                break;

                case 3:
                SpecType = eSpecType.DarkBone;
                Add(Specs.Darkness, 50, 1.0f);
                Add(Specs.BoneArmy, 26, 0.3f);
                Add(Specs.Suppression, 12, 0.1f);
                break;

                case 4:
                SpecType = eSpecType.DarkBone;
                Add(Specs.Darkness, 45, 1.0f);
                Add(Specs.BoneArmy, 35, 0.4f);
                Add(Specs.Suppression, 12, 0.1f);
                break;

                case 5:
                SpecType = eSpecType.ArmyBone;
                Add(Specs.BoneArmy, 50, 1.0f);
                Add(Specs.Suppression, 26, 0.2f);
                Add(Specs.Darkness, 12, 0.1f);
                break;
            }
        }
    }
}