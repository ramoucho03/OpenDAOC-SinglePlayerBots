using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicRunemaster : MimicNPC
    //{
    //    public MimicRunemaster(byte level) : base(new ClassRunemaster(), level)
    //    { }
    //}

    public class RunemasterSpec : MimicSpec
    {
        public RunemasterSpec(eSpecType spec)
        {
            SpecName = "RunemasterSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.DarkRune => Util.Random(0, 1),
                eSpecType.RuneRune => Util.Random(2, 4),
                eSpecType.SuppRune => Util.Random(5, 7),
                _ => Util.Random(7),
            };
            
            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.DarkRune;
                Add(Specs.Darkness, 50, 1.0f);
                Add(Specs.Suppression, 22, 0.1f);
                Add(Specs.Runecarving, 5, 0.0f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.RuneRune;
                Add(Specs.Runecarving, 50, 1.0f);
                Add(Specs.Darkness, 22, 0.3f);
                Add(Specs.Suppression, 6, 0.1f);
                break;

                case 4:
                SpecType = eSpecType.RuneRune;
                Add(Specs.Runecarving, 50, 1.0f);
                Add(Specs.Suppression, 22, 0.1f);
                Add(Specs.Darkness, 5, 0.0f);
                break;

                case 5:
                case 6:
                SpecType = eSpecType.SuppRune;
                Add(Specs.Suppression, 50, 1.0f);
                Add(Specs.Darkness, 20, 0.1f);
                Add(Specs.Runecarving, 4, 0.0f);
                break;

                case 7:
                SpecType = eSpecType.SuppRune;
                Add(Specs.Suppression, 50, 1.0f);
                Add(Specs.Darkness, 26, 0.2f);
                Add(Specs.Runecarving, 4, 0.0f);
                break;
            }
        }
    }
}