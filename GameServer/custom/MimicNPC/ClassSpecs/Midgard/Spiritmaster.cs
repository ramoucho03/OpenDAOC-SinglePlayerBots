using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicSpiritmaster : MimicNPC
    //{
    //    public MimicSpiritmaster(byte level) : base(new ClassSpiritmaster(), level)
    //    { }
    //}

    public class SpiritmasterSpec : MimicSpec
    {
        public SpiritmasterSpec(eSpecType spec)
        {
            SpecName = "SpiritmasterSpec";

            // Spiritmaster wields a 2H staff — the previous code left Is2H
            // unset (default false), so the equipment layer requested a 1H
            // staff slot that doesn't exist and bot ended up bare-handed
            // for melee fallback.
            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            var randVariance = spec switch
            {
                eSpecType.DarkSpirit => Util.Random(0, 3),
                eSpecType.SuppSpirit => Util.Random(4, 7),
                eSpecType.SummSpirit => Util.Random(8, 9),
                _ => Util.Random(9),
            };
            
            switch (randVariance)
            {
                case 0:
                case 1:
                SpecType = eSpecType.DarkSpirit;
                Add(Specs.Darkness, 50, 1.0f);
                Add(Specs.Summoning, 26, 0.1f);
                Add(Specs.Suppression, 5, 0.0f);
                break;

                case 2:
                case 3:
                SpecType = eSpecType.DarkSpirit;
                Add(Specs.Darkness, 50, 1.0f);
                Add(Specs.Suppression, 26, 0.1f);
                Add(Specs.Summoning, 6, 0.0f);
                break;

                case 4:
                case 5:
                SpecType = eSpecType.SuppSpirit;
                Add(Specs.Suppression, 50, 1.0f);
                Add(Specs.Summoning, 22, 0.1f);
                Add(Specs.Darkness, 5, 0.0f);
                break;

                case 6:
                SpecType = eSpecType.SuppSpirit;
                Add(Specs.Suppression, 50, 1.0f);
                Add(Specs.Darkness, 24, 0.2f);
                Add(Specs.Summoning, 3, 0.0f);
                break;

                case 7:
                // SpecType says SuppSpirit but the previous weights had
                // Summoning at 1.0f and Suppression at 0.1f, which made this
                // case behave like a Summ variant — inconsistent with the
                // surrounding Supp block. Restore Suppression as primary.
                SpecType = eSpecType.SuppSpirit;
                Add(Specs.Darkness, 12, 0.0f);
                Add(Specs.Suppression, 43, 1.0f);
                Add(Specs.Summoning, 30, 0.1f);
                break;

                case 8:
                SpecType = eSpecType.SummSpirit;
                Add(Specs.Summoning, 50, 1.0f);
                Add(Specs.Darkness, 24, 0.1f);
                Add(Specs.Suppression, 6, 0.0f);
                break;

                case 9:
                SpecType = eSpecType.SummSpirit;
                Add(Specs.Summoning, 50, 1.0f);
                Add(Specs.Darkness, 28, 0.1f);
                Add(Specs.Suppression, 10, 0.0f);
                break;
            }
        }
    }
}