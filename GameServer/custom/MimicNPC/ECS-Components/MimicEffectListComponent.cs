using DOL.GS.Scripts;

namespace DOL.GS
{
    public class MimicEffectListComponent : EffectListComponent
    {
        private readonly MimicNPC _owner;

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
            // Mimics don't have a real player client, so the only thing we
            // need to do on an effect tick is keep the group window in sync
            // for the human players who can see this bot.
            _owner.Group?.UpdateMember(_owner, true, false);
        }
    }
}
