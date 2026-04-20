using DOL.GS.Scripts;
using System.Collections.Generic;
using System.Threading;

namespace DOL.GS
{
    public class MimicEffectListComponent : EffectListComponent
    {
        private MimicNPC _owner;

        private EffectHelper.PlayerUpdate _requestedPlayerUpdates;                   // Player updates requested by the effects, to be sent in the next tick.
        private int _lastUpdateEffectsCount;                                         // Number of effects sent in the last player update, used externally.
        private readonly Lock _playerUpdatesLock = new();

        public MimicEffectListComponent(MimicNPC owner) : base(owner)
        {
            _owner = owner;
        }

        public override void BeginTick()
        {
            base.BeginTick();
            SendMimicUpdates();
        }

        private void SendMimicUpdates()
        {
            if (_owner.Group != null)
            {
                _owner.Group?.UpdateMember(_owner, true, false);
                //_owner.Out.SendUpdateIcons(GetEffects(), ref _lastUpdateEffectsCount);
            }
        }
    }
}
