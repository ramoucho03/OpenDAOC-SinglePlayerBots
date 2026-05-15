using System.Collections.Generic;
using DOL.Database;
using DOL.GS.Housing;
using DOL.GS.PacketHandler;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Virtual consignment merchant for the dynamic auction-house economy.
    /// It is NOT placed in the world, has no House behind it, and does not persist money.
    /// Players reach it through the Market Explorer (HouseMgr fallback).
    ///
    /// Storage: a fixed array of 100 slots matching the DB slot range
    /// [Consignment_First..Consignment_Last]. A parallel List of occupied slot indices
    /// supports O(1) random pop with swap-with-last. All MarketCache add/remove calls
    /// happen INSIDE _itemsLock so the cache and the merchant cannot drift out of sync.
    /// </summary>
    public class EconomyConsignmentMerchant : GameConsignmentMerchant
    {
        private const int SLOT_COUNT = GameConsignmentMerchant.CONSIGNMENT_SIZE; // 100

        private readonly object _itemsLock = new();
        // Indexed by (dbSlot - FirstDbSlot); fixed-size for O(1) lookup and cache locality.
        private readonly DbInventoryItem[] _items = new DbInventoryItem[SLOT_COUNT];
        // List of occupied array indices. Random pop swaps the picked index with the last
        // entry so removal is O(1) instead of an O(n) dictionary enumeration.
        private readonly List<int> _occupied = new(SLOT_COUNT);
        private int _nextFreeHint;

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
            // Stays Inactive: the merchant is never in any region. Active+null-region would
            // confuse any future ECS sweep that touches all active objects.
            ObjectState = GameObject.eObjectState.Inactive;
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
                    return _occupied.Count;
            }
        }

        public int FreeSlots
        {
            get
            {
                lock (_itemsLock)
                    return SLOT_COUNT - _occupied.Count;
            }
        }

        /// <summary>
        /// Adds a freshly built listing to the merchant's stock. The MarketCache insertion
        /// happens INSIDE the lock so concurrent pops cannot orphan an in-flight item.
        /// Returns true if added, false if the merchant is full.
        /// </summary>
        public bool TryAddListing(DbInventoryItem item)
        {
            if (item == null)
                return false;

            lock (_itemsLock)
            {
                if (_occupied.Count >= SLOT_COUNT)
                    return false;

                int idx = FindFreeIndexLocked();
                if (idx < 0)
                    return false;

                int dbSlot = idx + FirstDbSlot;
                item.OwnerID = _ownerId;
                item.OwnerLot = (ushort) _houseNumber;
                item.SlotPosition = dbSlot;

                _items[idx] = item;
                _occupied.Add(idx);
                _nextFreeHint = idx + 1;

                // Inside the lock: MarketCache must agree with _items at all times.
                return MarketCache.AddItem(item);
            }
        }

        /// <summary>
        /// Picks a random listing, removes it, returns it. Null if empty.
        /// O(1) thanks to swap-with-last on _occupied.
        /// </summary>
        public DbInventoryItem PopRandomListing()
        {
            lock (_itemsLock)
            {
                int n = _occupied.Count;
                if (n == 0)
                    return null;

                int pick = Util.Random(n - 1);
                int idx = _occupied[pick];
                int last = n - 1;
                if (pick != last)
                    _occupied[pick] = _occupied[last];
                _occupied.RemoveAt(last);

                DbInventoryItem item = _items[idx];
                _items[idx] = null;
                if (idx < _nextFreeHint)
                    _nextFreeHint = idx;

                if (item != null)
                    MarketCache.RemoveItem(item);

                return item;
            }
        }

        /// <summary>
        /// Removes every listing this merchant owns from the MarketCache. In-memory only.
        /// </summary>
        public int ClearStock()
        {
            lock (_itemsLock)
            {
                int n = _occupied.Count;
                for (int i = 0; i < n; i++)
                {
                    int idx = _occupied[i];
                    DbInventoryItem item = _items[idx];
                    _items[idx] = null;
                    if (item != null)
                        MarketCache.RemoveItem(item);
                }
                _occupied.Clear();
                _nextFreeHint = 0;
                return n;
            }
        }

        // ---- IGameInventoryObject overrides (called from the buy / move pipeline) ----

        public override IEnumerable<DbInventoryItem> GetDbItems()
        {
            lock (_itemsLock)
            {
                int n = _occupied.Count;
                var copy = new List<DbInventoryItem>(n);
                for (int i = 0; i < n; i++)
                    copy.Add(_items[_occupied[i]]);
                return copy;
            }
        }

        public override bool TryGetItem(int clientSlot, out DbInventoryItem item)
        {
            int idx = clientSlot - (int) FirstClientSlot;
            if ((uint) idx >= SLOT_COUNT)
            {
                item = null;
                return false;
            }
            lock (_itemsLock)
            {
                item = _items[idx];
                return item != null;
            }
        }

        public override Dictionary<int, DbInventoryItem> GetClientInventory()
        {
            lock (_itemsLock)
            {
                int n = _occupied.Count;
                var inventory = new Dictionary<int, DbInventoryItem>(n);
                int firstClient = (int) FirstClientSlot;
                for (int i = 0; i < n; i++)
                {
                    int idx = _occupied[i];
                    inventory[idx + firstClient] = _items[idx];
                }
                return inventory;
            }
        }

        // The base class `MoveItem` bails out when CurrentHouse is null. Our virtual merchant
        // has no House, so we replace it with a House-free version that still delegates to
        // ConsignmentState.ProcessMoveItem for the actual buy flow.
        public override bool MoveItem(GamePlayer player, eInventorySlot fromClientSlot, eInventorySlot toClientSlot, ushort count)
        {
            if (fromClientSlot == toClientSlot)
                return false;

            if (!CanHandleMove(player, fromClientSlot, toClientSlot))
                return false;

            var state = ConsignmentStateManager.GetState(this);
            return state?.ProcessMoveItem(player, this, fromClientSlot, toClientSlot, count) ?? false;
        }

        // Called when a player buys the item. The base class would also clear OwnerLot/SellPrice
        // and remove from MarketCache - we do the same but on our array, atomically.
        public override bool OnRemoveItem(GamePlayer player, DbInventoryItem item, int previousSlot)
        {
            int idx = previousSlot - FirstDbSlot;
            if ((uint) idx < SLOT_COUNT)
            {
                lock (_itemsLock)
                {
                    if (_items[idx] == item)
                    {
                        _items[idx] = null;
                        int pos = _occupied.IndexOf(idx);
                        if (pos >= 0)
                        {
                            int last = _occupied.Count - 1;
                            if (pos != last)
                                _occupied[pos] = _occupied[last];
                            _occupied.RemoveAt(last);
                        }
                        if (idx < _nextFreeHint)
                            _nextFreeHint = idx;
                        MarketCache.RemoveItem(item);
                    }
                }
            }
            item.OwnerLot = 0;
            item.SellPrice = 0;
            return true;
        }

        public override bool OnAddItem(GamePlayer player, DbInventoryItem item, int previousSlot) => false;

        // Defense-in-depth: never let a player interact with the virtual NPC directly.
        public override bool Interact(GamePlayer player) => false;

        public override bool SearchInventory(GamePlayer player, MarketSearch.SearchData searchData) => false;

        public override void AddObserver(GamePlayer player) { /* No state needed. */ }
        public override void RemoveObserver(GamePlayer player) { /* No state needed. */ }

        // Looks for an empty index. The dense slot range + _nextFreeHint makes the typical
        // case O(1); worst case O(SLOT_COUNT).
        private int FindFreeIndexLocked()
        {
            if (_occupied.Count >= SLOT_COUNT)
                return -1;
            int start = (uint) _nextFreeHint < SLOT_COUNT ? _nextFreeHint : 0;
            for (int i = start; i < SLOT_COUNT; i++)
            {
                if (_items[i] == null)
                    return i;
            }
            for (int i = 0; i < start; i++)
            {
                if (_items[i] == null)
                    return i;
            }
            return -1;
        }
    }
}
