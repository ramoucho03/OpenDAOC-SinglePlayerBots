using DOL.GS;

namespace DOL.AI.Brain
{
    public class TheurgistPetBrain : ControlledMobBrain
    {
        private GameObject _target;

        public TheurgistPetBrain(GameLiving owner) : base(owner)
        {
            IsMainPet = false;
        }

        public override void Think()
        {
            _target = Body.TargetObject;

            // Re-target: if the original target died, scan the owner's aggro
            // list for a live hostile before dying outright. Theurgist pets are
            // fire-and-forget but giving them one re-target chance avoids the
            // worst pet-meat-wastage on chain pulls — the pet still expires
            // naturally when nothing valid remains.
            if (_target == null || _target.Health <= 0)
            {
                GameLiving newTgt = FindNextHostileFromOwner();
                if (newTgt != null)
                {
                    Body.TargetObject = newTgt;
                    _target = newTgt;
                }
                else
                {
                    Body.Die(null);
                    return;
                }
            }

            if (CheckSpells(eCheckSpellType.Offensive))
                Body.StopAttack();
            else
                Body.StartAttack(_target);
        }

        private GameLiving FindNextHostileFromOwner()
        {
            if (Body.Owner is not GameLiving owner || owner.attackComponent == null)
                return null;
            // Quick scan: owner is in combat, the closest live hostile from
            // its attacker tracker is the natural reassignment target.
            GameLiving best = null;
            int bestDistSq = int.MaxValue;
            foreach (GameLiving att in owner.attackComponent.AttackerTracker.Attackers)
            {
                if (att == null || !att.IsAlive || att.ObjectState != GameObject.eObjectState.Active)
                    continue;
                int dx = Body.X - att.X;
                int dy = Body.Y - att.Y;
                int dsq = dx * dx + dy * dy;
                if (dsq < bestDistSq)
                {
                    bestDistSq = dsq;
                    best = att;
                }
            }
            return best;
        }

        public override eWalkState WalkState { get => eWalkState.Stay; set { } }
        public override eAggressionState AggressionState { get => eAggressionState.Aggressive; set { } }
        public override void Attack(GameObject target) { }
        public override void Disengage() { }
        public override void Follow(GameObject target) { }
        public override void FollowOwner() { }
        public override void Stay() { }
        public override void ComeHere() { }
        public override void Goto(GameObject target) { }
        public override void UpdatePetWindow() { }
        public override void OnAttackedByEnemy(AttackData ad) { }
    }
}
