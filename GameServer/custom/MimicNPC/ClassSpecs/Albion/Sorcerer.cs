using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicSorcerer : MimicNPC
    //{
    //    public MimicSorcerer(byte level) : base(new ClassSorcerer(), level)
    //    { }
    //}

    public class SorcererSpec : MimicSpec
    {
        public SorcererSpec(eSpecType spec)
        {
            SpecName = "SorcererSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            // Sorcerer has three full speclines (Mind / Body / Matter) and
            // a real player picks one to 50 and uses the other two as light
            // utility. Previous table:
            //  - Missed eSpecType.MatterSorc entirely (Util.Random default
            //    branch never produced a Matter-50 roll either).
            //  - Capped the primary line below 50 (Mind 44/47/49, Body 48).
            //  - Case 4 inverted weights (Mind ratio 0.8f vs Body 0.6f
            //    despite SpecType=BodySorc).
            // Rewrite gives 3 BodySorc variants, 3 MindSorc variants,
            // and 3 MatterSorc variants, each with the primary at cap 50.
            var randVariance = spec switch
            {
                eSpecType.MindSorc => Util.Random(0, 2),
                eSpecType.BodySorc => Util.Random(3, 5),
                eSpecType.MatterSorc => Util.Random(6, 8),
                _ => Util.Random(8),
            };

            switch (randVariance)
            {
                // ---- MindSorc: mez/DD pressure ----
                case 0:
                SpecType = eSpecType.MindSorc;
                Add(Specs.Mind_Magic, 50, 1.0f);
                Add(Specs.Body_Magic, 30, 0.4f);
                Add(Specs.Matter_Magic, 8, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.MindSorc;
                Add(Specs.Mind_Magic, 50, 1.0f);
                Add(Specs.Matter_Magic, 22, 0.3f);
                Add(Specs.Body_Magic, 15, 0.2f);
                break;

                case 2:
                SpecType = eSpecType.MindSorc;
                Add(Specs.Mind_Magic, 50, 1.0f);
                Add(Specs.Body_Magic, 26, 0.4f);
                Add(Specs.Matter_Magic, 4, 0.0f);
                break;

                // ---- BodySorc: DoT/disease + pet ----
                case 3:
                SpecType = eSpecType.BodySorc;
                Add(Specs.Body_Magic, 50, 1.0f);
                Add(Specs.Mind_Magic, 30, 0.4f);
                Add(Specs.Matter_Magic, 8, 0.1f);
                break;

                case 4:
                SpecType = eSpecType.BodySorc;
                Add(Specs.Body_Magic, 50, 1.0f);
                Add(Specs.Matter_Magic, 24, 0.3f);
                Add(Specs.Mind_Magic, 15, 0.2f);
                break;

                case 5:
                SpecType = eSpecType.BodySorc;
                Add(Specs.Body_Magic, 50, 1.0f);
                Add(Specs.Mind_Magic, 26, 0.4f);
                Add(Specs.Matter_Magic, 4, 0.0f);
                break;

                // ---- MatterSorc: ramp DoT + debuffs ----
                case 6:
                SpecType = eSpecType.MatterSorc;
                Add(Specs.Matter_Magic, 50, 1.0f);
                Add(Specs.Mind_Magic, 30, 0.4f);
                Add(Specs.Body_Magic, 8, 0.1f);
                break;

                case 7:
                SpecType = eSpecType.MatterSorc;
                Add(Specs.Matter_Magic, 50, 1.0f);
                Add(Specs.Body_Magic, 22, 0.3f);
                Add(Specs.Mind_Magic, 15, 0.2f);
                break;

                case 8:
                SpecType = eSpecType.MatterSorc;
                Add(Specs.Matter_Magic, 50, 1.0f);
                Add(Specs.Body_Magic, 26, 0.4f);
                Add(Specs.Mind_Magic, 4, 0.0f);
                break;
            }
        }
    }
}