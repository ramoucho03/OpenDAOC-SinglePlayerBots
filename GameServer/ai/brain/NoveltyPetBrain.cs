/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 * 
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */

using DOL.GS;
using DOL.GS.Scripts;

namespace DOL.AI.Brain
{
	public class NoveltyPetBrain : DOL.AI.ABrain, IControlledBrain
	{
		public const string HAS_PET = "HasNoveltyPet";

		private IGamePlayer m_owner;

		public NoveltyPetBrain(IGamePlayer owner)
			: base()
		{
			m_owner = owner;
		}

        public virtual GameNPC GetNPCOwner()
        {
            return null;
        }
        public virtual GameLiving GetLivingOwner()
        {
            GamePlayer player = GetPlayerOwner();
            if (player != null)
                return player;

            GameNPC npc = GetNPCOwner();
            if (npc != null)
                return npc;

            return null;
        }

		public virtual IGamePlayer GetIPlayerOwner()
		{
			return GetPlayerOwner();
		}

		#region Think

		public override int ThinkInterval
		{
			get { return 5000; }
		}

		public override void Think()
		{

			if (m_owner == null || 
				m_owner.IsAlive == false || 
				m_owner.Client.ClientState != GameClient.eClientState.Playing || 
				Body.IsWithinRadius((GameObject)m_owner, WorldMgr.VISIBILITY_DISTANCE) == false)
			{
				Body.Delete();
				Body = null;
				if (m_owner != null && m_owner.TempProperties.GetProperty<bool>(HAS_PET))
				{
					m_owner.TempProperties.SetProperty(HAS_PET, false);
				}
				m_owner = null;
			}
		}

		#endregion Think

		#region IControlledBrain Members
		public void SetAggressionState(eAggressionState state) { }
		public eWalkState WalkState { get { return eWalkState.Follow; } }
		public eAggressionState AggressionState { get { return eAggressionState.Passive; } set { } }
		public GameLiving Owner { get { return (GameLiving)m_owner; } }
		public void Attack(GameObject target) { }
		public void Disengage() { }
		public void CheckAggressionStateOnPlayerOrder() { }
		public void Follow(GameObject target) { }
		public void FollowOwner() { }
		public void Stay() { }
		public void ComeHere() { }
		public void Goto(GameObject target) { }
		public void UpdatePetWindow() { }
		public GamePlayer GetPlayerOwner() { return m_owner as GamePlayer; }
		public bool IsMainPet { get { return false; } set { } }
		public override void KillFSM(){ }
		#endregion
	}
}
