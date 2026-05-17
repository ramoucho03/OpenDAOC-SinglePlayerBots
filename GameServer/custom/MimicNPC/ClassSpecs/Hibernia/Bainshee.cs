namespace DOL.GS.Scripts
{
    /// <summary>
    /// Bainshee spec table for mimics. Catacombs Hibernia caster DPS with
    /// three magic lines:
    ///   - PhantasmalWail: single-target debuff + DD
    ///   - EtherealShriek: pure single-target nuke
    ///   - SpectralForce: PBAoE damage + AoE pressure (the closest thing
    ///     a Bainshee has to a CC role — splash damage interrupts and
    ///     softens multiple adds, useful when no Sorcerer/Bard is around)
    /// </summary>
    public class BainsheeSpec : MimicSpec
    {
        public BainsheeSpec(eSpecType spec)
        {
            SpecName = "BainsheeSpec";

            WeaponOneType = eObjectType.Staff;
            Is2H = true;

            // Util.Random(4) returns 0..4 inclusive => 5 variants.
            int randVariance = Util.Random(4);

            switch (randVariance)
            {
                // PhantasmalWail max — debuff DPS
                case 0:
                Add(Specs.PhantasmalWail, 50, 1.0f);
                Add(Specs.EtherealShriek, 28, 0.2f);
                Add(Specs.SpectralForce, 12, 0.1f);
                break;

                // EtherealShriek max — single-target nuker
                case 1:
                Add(Specs.EtherealShriek, 50, 1.0f);
                Add(Specs.PhantasmalWail, 28, 0.2f);
                Add(Specs.SpectralForce, 12, 0.1f);
                break;

                // SpectralForce 44 — AoE pressure with PW backup
                case 2:
                Add(Specs.SpectralForce, 44, 0.8f);
                Add(Specs.PhantasmalWail, 35, 0.5f);
                Add(Specs.EtherealShriek, 18, 0.1f);
                break;

                // PW + ES balanced — hybrid nuke/debuff
                case 3:
                Add(Specs.PhantasmalWail, 44, 0.8f);
                Add(Specs.EtherealShriek, 35, 0.5f);
                Add(Specs.SpectralForce, 18, 0.1f);
                break;

                // AoE-CC variant: SpectralForce maxed for AoE lockdown.
                // Group filler when no dedicated CC class is available —
                // SF shouts interrupt and soften multiple adds in PBAoE
                // range, letting the tank and DPS clean up afterwards.
                case 4:
                Add(Specs.SpectralForce, 50, 1.0f);
                Add(Specs.PhantasmalWail, 22, 0.3f);
                Add(Specs.EtherealShriek, 18, 0.2f);
                break;
            }
        }
    }
}
