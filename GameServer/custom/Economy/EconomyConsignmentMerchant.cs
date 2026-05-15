using System.Collections.Generic;
using DOL.Database;
using DOL.GS.Housing;
using DOL.GS.PacketHandler;

namespace DOL.GS.Economy
{
    /// <summary>
    /// A virtual consignment merchant that owns a bag of bot-generated items.
    /// It is NOT placed in the world, has no House behind it, and does not persist money.
    /// Players interact with it through the Market Explorer; the resolution path is
    /// HouseMgr.GetConsignmentByHouseNumber -> EconomyManager.GetVirtualMerchant.
    ///
    /// Items live in-memory until purchased. When the existing move/buy pipeline saves
    /// the item it ends up in the player's inventory exactly like a real CM sale.
    /// The "money paid" by the player is silently sunk (no DB row for our fake house),
    /// which gives the dynamic economy a natural gold sink.
    /// </summary>
    public class EconomyConsignmentMerchant : GameConsignmentMerchant
    {
        private readonly object _itemsLock = new();
        private readonly Dictionary<int, DbInventoryItem> _items = new(); // db-slot -> item
        private readonly string _ownerId;
        private readonly int _houseNumber;
        private readonly eRealm _realm;

        public EconomyConsignmentMerchant(string ownerId, int houseNumber, eRealm realm, string displayName)
        {
            _ownerId = ownerId;
            _houseNumber = houseNumber;
            _realm = realm;
            HouseNumber = (ushort) houseNumber;
            Realm = realm;
            Name = displayName;
            ObjectState = GameObject.eObjectState.Active;
        }

        public eRealm SellerRealm => _realm;
        public string OwnerId => _ownerId;

        public override House CurrentHouse
        {
            get => null;
            set { }
        }

        public override string GetOwner() => _ownerId;

        public int ItemCount
        {
            get
            {
                lock (_itemsLock)
                    return _items.Count;
            }
        }

        public int FreeSlots
        {
            get
            {
                int cap = EconomyConfig.ECONOMY_SELLER_CAPACITY;
                if (cap <= 0)
                    cap = GameConsignmentMerchant.CONSIGNMENT_SIZE;
                lock (_itemsLock)
                    return cap - _items.Count;
            }
        }

        /// <summary>
        /// Adds a freshly built listing to the merchant's stock. The item is registered
        /// with the global MarketCache so the Market Explorer can find it.
        /// Returns true if added, false if the merchant is full or slot is in use.
        /// </summary>
        public bool TryAddListing(DbInventoryItem item)
        {
            if (item == null)
                return false;

            int cap = EconomyConfig.ECONOMY_SELLER_CAPACITY;
            if (cap <= 0)
                cap = GameConsignmentMerchant.CONSIGNMENT_SIZE;

            lock (_itemsLock)
            {
                if (_items.Count >= cap)
                    return false;

                int slot = FindFreeSlotLocked();
                if (slot < 0)
                    return false;

                item.OwnerID = _ownerId;
                item.OwnerLot = (ushort) _houseNumber;
                item.SlotPosition = slot;
                _items[slot] = item;
            }

            return MarketCache.AddItem(item);
        }

        /// <summary>
        /// Picks a random listing from this merchant, removes it, and returns it. Null if empty.
        /// Used by the periodic refresh to rotate stock.
        /// </summary>
        public DbInventoryItem PopRandomListing()
        {
            DbInventoryItem picked = null;
            lock (_itemsLock)
            {
                if (_items.Count == 0)
                    return null;

                int idx = Util.Random(_items.Count - 1);
                int i = 0;
                int keyToRemove = -1;
                foreach (var kv in _items)
                {
                    if (i++ == idx)
                    {
                        keyToRemove = kv.Key;
                        picked = kv.Value;
                        break;
                    }
                }

                if (keyToRemove >= 0)
                    _items.Remove(keyToRemove);
            }

            if (picked != null)
                MarketCache.RemoveItem(picked);

            return picked;
        }

        /// <summary>
        /// Removes every listing this merchant owns from MarketCache. In-memory only.
        /// </summary>
        public int ClearStock()
        {
            List<DbInventoryItem> all;
            lock (_itemsLock)
            {
                all = new List<DbInventoryItem>(_items.Values);
                _items.Clear();
            }

            foreach (var it in all)
                MarketCache.RemoveItem(it);

            return all.Count;
        }

        // ---- IGameInventoryObject overrides (called from the buy / move pipeline) ----

        public override IEnumerable<DbInventoryItem> GetDbItems()
        {
            lock (_itemsLock)
                return new List<DbInventoryItem>(_items.Values);
        }

        public override bool TryGetItem(int clientSlot, out DbInventoryItem item)
        {
            int dbSlot = clientSlot - (int) FirstClientSlot + FirstDbSlot;
            lock (_itemsLock)
                return _items.TryGetValue(dbSlot, out item);
        }

        public override Dictionary<int, DbInventoryItem> GetClientInventory()
        {
            Dictionary<int, DbInventoryItem> inventory = new();
            int slotOffset = (int) FirstClientSlot - FirstDbSlot;

            lock (_itemsLock)
            {
                foreach (var kv in _items)
                {
                    int clientSlot = kv.Key + slotOffset;
                    inventory[clientSlot] = kv.Value;
                }
            }

            return inventory;
        }

        // The base class `MoveItem` bails out when CurrentHouse is null. Our virtual
        // merchant has no House, so we replace the implementation with a House-free version
        // that still delegates to `ConsignmentState.ProcessMoveItem` for the actual buy flow.
        public override bool MoveItem(GamePlayer player, eInventorySlot fromClientSlot, eInventorySlot toClientSlot, ushort count)
        {
            if (fromClientSlot == toClientSlot)
                return false;

            if (!CanHandleMove(player, fromClientSlot, toClientSlot))
                return false;

            var state = ConsignmentStateManager.GetState(this);
            return state?.ProcessMoveItem(player, this, fromClientSlot, toClientSlot, count) ?? false;
        }

        public override bool OnRemoveItem(GamePlayer player, DbInventoryItem item, int previousSlot)
        {
            // Called when a player buys the item. The base class would clear OwnerLot/SellPrice
            // and remove from MarketCache - we do the same but also drop our slot record.
            MarketCache.RemoveItem(item);
            lock (_itemsLock)
                _items.Remove(previousSlot);

            item.OwnerLot = 0;
            item.SellPrice = 0;
            return true;
        }

        public override bool OnAddItem(GamePlayer player, DbInventoryItem item, int previousSlot)
        {
            // Players cannot deposit items into a bot seller. The buy flow never reaches here.
            return false;
        }

        // `HasPermissionToMove` on the base class is not virtual but it returns false for
        // any merchant whose CurrentHouse is null - which is our case - so we don't override it.

        public override bool SearchInventory(GamePlayer player, MarketSearch.SearchData searchData)
        {
            // Search is handled by MarketExplorer over the whole MarketCache.
            return false;
        }

        public override void AddObserver(GamePlayer player) { /* No state needed. */ }
        public override void RemoveObserver(GamePlayer player) { /* No state needed. */ }

        private int FindFreeSlotLocked()
        {
            int first = FirstDbSlot;
            int last = LastDbSlot;
            for (int s = first; s <= last; s++)
            {
                if (!_items.ContainsKey(s))
                    return s;
            }
            return -1;
        }
    }
}
