using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    //public class MimicWarden : MimicNPC
    //{
    //    public MimicWarden(byte level) : base(new ClassWarden(), level)
    //    { }
    //}

    public class WardenSpec : MimicSpec
    {
        public WardenSpec(eSpecType spec)
        {
            SpecName = "WardenSpec";
            Is2H = false;

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponOneType = eObjectType.Blades; break;
                case 1: WeaponOneType = eObjectType.Blunt; break;
            }

            var randVariance = spec switch
            {
                eSpecType.NurtureWarden => Util.Random(0, 1),
                eSpecType.BattleWarden => 2,
                eSpecType.RegrowthWarden => 3,
                _ => Util.Random(3),
            };
   
            // Warden in OpenDAoC has no Nature spec line — class 58 specs are
            // Nurture / Regrowth / Blades / Blunt / Parry only. The previous
            // tables added Specs.Nature, which SpendSpecPoints silently dropped
            // (GetSpecializationByName returned null), wasting dozens of points
            // and leaving the bot under-leveled in weapons/Regrowth.
            // Redistribute the wasted spec points toward the weapon line, the
            // self-buff baseline (Nurture) and the heal line (Regrowth).
            switch (randVariance)
            {
                // Nurture-leaning healer: maxed Nurture, decent heals, light weapon
                case 0:
                SpecType = eSpecType.NurtureWarden;
                Add(ObjToSpec(WeaponOneType), 35, 0.6f);
                Add(Specs.Nurture, 50, 1.0f);
                Add(Specs.Regrowth, 35, 0.5f);
                Add(Specs.Parry, 18, 0.1f);
                break;

                case 1:
                SpecType = eSpecType.NurtureWarden;
                Add(ObjToSpec(WeaponOneType), 30, 0.3f);
                Add(Specs.Nurture, 50, 1.0f);
                Add(Specs.Regrowth, 39, 0.6f);
                Add(Specs.Parry, 18, 0.1f);
                break;

                // Battle warden: weapon + buffs, decent heals
                case 2:
                SpecType = eSpecType.BattleWarden;
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Nurture, 42, 0.7f);
                Add(Specs.Regrowth, 28, 0.4f);
                Add(Specs.Parry, 22, 0.2f);
                break;

                // Regrowth-leaning healer
                case 3:
                SpecType = eSpecType.RegrowthWarden;
                Add(Specs.Nurture, 42, 0.7f);
                Add(Specs.Regrowth, 50, 1.0f);
                Add(ObjToSpec(WeaponOneType), 28, 0.4f);
                Add(Specs.Parry, 10, 0.0f);
                break;
            }
        }
    }
}