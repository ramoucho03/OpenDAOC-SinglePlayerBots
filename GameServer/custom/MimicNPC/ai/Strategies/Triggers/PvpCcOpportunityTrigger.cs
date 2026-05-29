namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Vrai quand le bot est un contrôleur (CC) en RvR, en groupe, et qu'il a
    /// une cible mezz « propre » disponible : un ennemi à portée qui n'est ni la
    /// cible focus du groupe, ni déjà mezzé / immunisé au CC.
    ///
    /// Permet à la <see cref="DOL.GS.Scripts.AI.Strategies.Builtin.CcStrategy"/>
    /// de basculer le mezzeur sur le contrôle du groupe adverse en PvP — là où
    /// le trigger PvE <see cref="GroupHasCcTargetsTrigger"/> (qui lit la file
    /// d'adds alimentée par le puller/BAF) ne se déclenche jamais. La sélection
    /// de cible et le cast réels sont délégués à
    /// <c>MimicBrain.CheckSpells(CrowdControl)</c> via <c>PickPvpCcTarget</c>,
    /// donc trigger et cast partagent exactement la même définition de cible.
    /// </summary>
    public sealed class PvpCcOpportunityTrigger : IBotTrigger
    {
        public string Name => "pvp-cc-opportunity";

        public bool Check(BotContext ctx)
        {
            MimicNPC bot = ctx?.Bot;

            if (bot == null || ctx.Brain == null)
                return false;

            // On parle de mezzer « le groupe en face » : pas de groupe, pas de
            // bascule CC (un CC solo garde sa logique de nuke/peel existante).
            if (bot.Group == null)
                return false;

            return ctx.Brain.HasPvpCcOpportunity();
        }
    }
}
