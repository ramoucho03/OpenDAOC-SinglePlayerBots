using DOL.GS.PacketHandler;

namespace DOL.GS
{
    public class ProcECSGameEffect : ECSGameSpellEffect
    {
        public ProcECSGameEffect(in ECSGameEffectInitParams initParams)
            : base(initParams) { }

        public override void OnStartEffect()
        {
            // chatType was computed but never used downstream — OnEffectStartsMsg
            // currently uses fixed channels. Block removed; restore the
            // conditional channel if/when proc messages need to differentiate
            // pulse vs single-cast.
            OnEffectStartsMsg(true, false, true);

            //GameEventMgr.AddHandler(effect.Owner, EventType, new DOLEventHandler(EventHandler));
        }

        public override void OnStopEffect()
        {
            // "Your crystal shield fades."
            // "{0}'s crystal shield fades."
            OnEffectExpiresMsg(true, false, true);

        }
    }
}
