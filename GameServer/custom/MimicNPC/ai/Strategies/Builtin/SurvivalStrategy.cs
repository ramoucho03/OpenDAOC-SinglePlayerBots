using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Light-touch out-of-combat recovery. Bot stands up the moment it
    /// enters combat, and sits down when mana/endurance dip out of combat.
    /// Avoids interfering with the FSM during active fights.
    /// </summary>
    public sealed class SurvivalStrategy : IBotStrategy
    {
        public const string Key = "survival";

        public string Name => Key;
        public string Description => "S'assied hors combat quand la mana/endurance est basse, se relève au début d'un combat.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            // Stand up immediately when combat starts — runs first.
            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new StandUpAction(),
                priority: 90,
                cooldownMs: 0,
                exclusive: false);

            // OOC: sit if missing health. The bot's natural regen will catch
            // up faster than walking around.
            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(80),
                new SitDownAction(),
                priority: 35,
                cooldownMs: 3000,
                exclusive: false);

            // OOC: sit if endurance is low. Higher priority than mana so we
            // recover stamina first (cheap, no cast competition).
            yield return new BotTriggerActionBinding(
                new EnduranceBelowTrigger(60),
                new SitDownAction(),
                priority: 30,
                cooldownMs: 3000,
                exclusive: false);

            // OOC: sit if mana is below half (only matters for casters/hybrids).
            yield return new BotTriggerActionBinding(
                new ManaBelowTrigger(50),
                new SitDownAction(),
                priority: 25,
                cooldownMs: 3000,
                exclusive: false);
        }
    }
}
