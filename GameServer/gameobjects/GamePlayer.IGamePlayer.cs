// MimicNPC module adapter. The mimic system exposes both real GamePlayer
// instances and MimicNPC bots through the unified DOL.GS.Scripts.IGamePlayer
// interface. Several interface members don't line up exactly with the
// GamePlayer signatures upstream uses (return types, setter accessibility,
// fields-vs-properties), so this file bridges them with explicit interface
// implementations. The mimic side (MimicNPC) provides matching implicit
// implementations; only the GamePlayer side needs the adapter.

using DOL.GS.Scripts;

namespace DOL.GS
{
    public partial class GamePlayer
    {
        // Sit: upstream GamePlayer.Sit is void; interface uses bool return.
        bool IGamePlayer.Sit(bool sit)
        {
            Sit(sit);
            return IsSitting;
        }

        // DuelPartner: GamePlayer returns GamePlayer; interface uses GameLiving.
        GameLiving IGamePlayer.DuelPartner => DuelPartner;

        // IsDuelReady: not tracked on GamePlayer upstream — mimics use it to
        // signal /duel acceptance; not meaningful for real players here.
        bool IGamePlayer.IsDuelReady { get => false; set { } }

        // Component fields are lowercase on GameLiving/GamePlayer; interface
        // uses PascalCase. Forward to the underlying fields.
        RangeAttackComponent IGamePlayer.RangeAttackComponent => rangeAttackComponent;
        AttackComponent IGamePlayer.AttackComponent => attackComponent;
        StyleComponent IGamePlayer.StyleComponent => styleComponent;
        EffectListComponent IGamePlayer.EffectListComponent => effectListComponent;

        // Setters that are not public on GamePlayer — interface needs settable
        // properties for the mimic side, so provide explicit no-op/forwarding
        // shims here.
        long IGamePlayer.DeathTick
        {
            get => DeathTick;
            set { /* upstream GamePlayer manages this itself */ }
        }

        bool IGamePlayer.IsEncumbered
        {
            get => IsEncumbered;
            set { /* upstream GamePlayer manages this itself */ }
        }

        double IGamePlayer.MaxSpeedModifierFromEncumbrance
        {
            get => MaxSpeedModifierFromEncumbrance;
            set { /* upstream GamePlayer manages this itself */ }
        }
    }
}
