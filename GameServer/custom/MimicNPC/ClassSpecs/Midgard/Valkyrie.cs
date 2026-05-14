namespace DOL.GS.Scripts
{
    public class ValkyrieSpec : MimicSpec
    {
        public ValkyrieSpec(eSpecType spec)
        {
            SpecName = "ValkyrieSpec";

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Sword; break;
                case 1: WeaponOneType = eObjectType.Spear; break;
            }

            int randVariance = spec switch
            {
                eSpecType.OneHandAndShield => 0,
                eSpecType.TwoHanded => 2,
                eSpecType.OneHandHybrid => 1,
                _ => Util.Random(2),
            };

            SpecType = eSpecType.Mid;

            switch (randVariance)
            {
                case 0:
                Add(ObjToSpec(WeaponOneType), 50, 0.9f);
                Add(Specs.OdinsWill, 39, 0.5f);
                Add(Specs.Shields, 35, 0.3f);
                Add(Specs.Parry, 12, 0.1f);
                Add(Specs.Mending, 12, 0.2f);
                break;

                case 1:
                Add(ObjToSpec(WeaponOneType), 44, 0.8f);
                Add(Specs.OdinsWill, 44, 0.6f);
                Add(Specs.Shields, 28, 0.3f);
                Add(Specs.Mending, 18, 0.3f);
                Add(Specs.Parry, 12, 0.1f);
                break;

                case 2:
                Is2H = true;
                SpecType = eSpecType.TwoHandHybrid;
                WeaponTwoType = eObjectType.Spear;
                Add(ObjToSpec(WeaponOneType), 39, 0.7f);
                Add(ObjToSpec(WeaponTwoType), 50, 1.0f);
                Add(Specs.OdinsWill, 35, 0.5f);
                Add(Specs.Mending, 12, 0.2f);
                break;
            }
        }
    }
}
