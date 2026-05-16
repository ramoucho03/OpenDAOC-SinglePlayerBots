using System;
using System.Collections.Generic;
using System.Threading;
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

        // Pending DB mutations, flushed asynchronously in batches by EconomyManager.
        // Cancel-out: an item added then popped before the next flush is removed from
        // _pendingAdds instead of being added to _pendingDeletes, so the DB never sees it.
        private readonly List<DbInventoryItem> _pendingAdds = new();
        private readonly List<DbInventoryItem> _pendingDeletes = new();
        // Items currently being persisted (DrainPending moved them here; the actual
        // AddObject is in flight on the flush worker). PopRandomListing / OnRemoveItem /
        // ClearStock can race against the in-flight AddObject; the flush completion logic
        // reconciles the race by inspecting in-memory state and item.OwnerID.
        private readonly List<DbInventoryItem> _inFlightAdds = new();
        // Set when no AddObject is in flight for this merchant. OnRemoveItem (player buy)
        // waits on this if it sees the item is in flight, so the player save sees the
        // post-AddObject IsPersisted=true state (and does UPDATE instead of duplicate INSERT).
        private readonly ManualResetEventSlim _addBatchIdle = new(true);

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
                if (!MarketCache.AddItem(item))
                {
                    _items[idx] = null;
                    _occupied.RemoveAt(_occupied.Count - 1);
                    return false;
                }

                if (EconomyConfig.ECONOMY_PERSIST)
                    _pendingAdds.Add(item);

                EconomyManager.OnListingAdded(item.Id_nb);
                return true;
            }
        }

        /// <summary>
        /// Re-attach an item already loaded from the DB into this merchant's slot map.
        /// Used at startup when the MarketCache already contains our persisted listings.
        /// The item is NOT enqueued for save and is NOT re-added to MarketCache (it's
        /// already there). Returns false on slot conflict (which would mean DB corruption).
        /// </summary>
        public bool AttachExistingListing(DbInventoryItem item)
        {
            if (item == null) return false;
            int idx = item.SlotPosition - FirstDbSlot;
            if ((uint) idx >= SLOT_COUNT)
                return false;

            lock (_itemsLock)
            {
                if (_items[idx] != null)
                    return false;
                _items[idx] = item;
                _occupied.Add(idx);
                // Reset to 0: subsequent TryAddListing will find the lowest free slot via
                // its wrap-around scan. Cheaper than incrementally tracking on reload.
                _nextFreeHint = 0;
                EconomyManager.OnListingAdded(item.Id_nb);
                return true;
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
                {
                    MarketCache.RemoveItem(item);
                    EnqueueDeleteLocked(item);
                    EconomyManager.OnListingRemoved(item.Id_nb);
                }

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
                    {
                        MarketCache.RemoveItem(item);
                        EnqueueDeleteLocked(item);
                        EconomyManager.OnListingRemoved(item.Id_nb);
                    }
                }
                _occupied.Clear();
                _nextFreeHint = 0;
                return n;
            }
        }

        /// <summary>
        /// Drains the pending DB mutations under the lock and moves drained adds to the
        /// in-flight list, returning them for batched flush. CompleteAddBatch MUST be
        /// called after the AddObject finishes so the in-flight list is cleared.
        /// </summary>
        public (List<DbInventoryItem> adds, List<DbInventoryItem> deletes) DrainPending()
        {
            lock (_itemsLock)
            {
                if (_pendingAdds.Count == 0 && _pendingDeletes.Count == 0)
                    return (null, null);
                List<DbInventoryItem> a = null;
                if (_pendingAdds.Count > 0)
                {
                    a = new List<DbInventoryItem>(_pendingAdds);
                    _inFlightAdds.AddRange(_pendingAdds);
                    _pendingAdds.Clear();
                    _addBatchIdle.Reset(); // a buy of an in-flight item must wait
                }
                List<DbInventoryItem> d = _pendingDeletes.Count > 0 ? new List<DbInventoryItem>(_pendingDeletes) : null;
                _pendingDeletes.Clear();
                return (a, d);
            }
        }

        /// <summary>
        /// Called after the batched AddObject completes. Reconciles items that were
        /// popped or transferred to a player while their AddObject was in flight:
        ///   - if item.OwnerID changed (player bought it): leave the row alone; the
        ///     player inventory pipeline will UPDATE it with the new owner.
        ///   - if the item is no longer in _items and OwnerID is still ours: it was
        ///     popped by rotation/clear; schedule a delete for the next flush.
        ///   - otherwise the item is still ours and persistent: nothing to do.
        /// </summary>
        public List<DbInventoryItem> CompleteAddBatch()
        {
            lock (_itemsLock)
            {
                if (_inFlightAdds.Count == 0)
                    return null;

                List<DbInventoryItem> toDeleteNow = null;
                for (int i = 0; i < _inFlightAdds.Count; i++)
                {
                    DbInventoryItem item = _inFlightAdds[i];
                    string owner = item.OwnerID;

                    // Player took ownership during flight: leave the row, player save will UPDATE.
                    if (string.IsNullOrEmpty(owner) || !owner.StartsWith(EconomyManager.OWNER_PREFIX, StringComparison.Ordinal))
                        continue;

                    // Still ours - is the slot still occupied by this exact instance?
                    int idx = item.SlotPosition - FirstDbSlot;
                    bool stillPresent = (uint) idx < SLOT_COUNT && ReferenceEquals(_items[idx], item);
                    if (stillPresent)
                        continue;

                    // Popped during flight - delete the row we just inserted.
                    toDeleteNow ??= new List<DbInventoryItem>();
                    toDeleteNow.Add(item);
                }

                _inFlightAdds.Clear();
                _addBatchIdle.Set();
                return toDeleteNow;
            }
        }

        // Cancel-out: if the item was added since the last flush (not yet in DB), drop the
        // pending-add. If it's in flight (drained but AddObject hasn't returned yet),
        // leave it - CompleteAddBatch will detect the slot is empty / OwnerID still ours
        // and queue a follow-up delete. Otherwise (already persisted) enqueue a DELETE.
        private void EnqueueDeleteLocked(DbInventoryItem item)
        {
            if (!EconomyConfig.ECONOMY_PERSIST)
                return;
            for (int i = _pendingAdds.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pendingAdds[i], item))
                {
                    _pendingAdds.RemoveAt(i);
                    return;
                }
            }
            for (int i = 0; i < _inFlightAdds.Count; i++)
            {
                if (ReferenceEquals(_inFlightAdds[i], item))
                {
                    // Reconciled by CompleteAddBatch when AddObject returns.
                    return;
                }
            }
            if (item.IsPersisted)
                _pendingDeletes.Add(item);
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

        // Called when a player buys the item. The player inventory pipeline takes
        // ownership of the row and will update/insert via SaveIntoDatabase, so we must
        // NOT enqueue a delete here - that would race the player's update.
        public override bool OnRemoveItem(GamePlayer player, DbInventoryItem item, int previousSlot)
        {
            bool waitForAddBatch = false;

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
                        EconomyManager.OnListingRemoved(item.Id_nb);

                        // If still queued for insert (never flushed), drop it. The player
                        // inventory save will AddObject it under the player's OwnerID.
                        for (int i = _pendingAdds.Count - 1; i >= 0; i--)
                        {
                            if (ReferenceEquals(_pendingAdds[i], item))
                            {
                                _pendingAdds.RemoveAt(i);
                                break;
                            }
                        }

                        // If the item is in flight (AddObject is currently running on the
                        // flush worker), we must wait for it to finish so player.IsPersisted
                        // is true when player.SaveIntoDatabase runs - otherwise the player
                        // save would route to AddObject and we'd get a duplicate INSERT.
                        for (int i = 0; i < _inFlightAdds.Count; i++)
                        {
                            if (ReferenceEquals(_inFlightAdds[i], item))
                            {
                                waitForAddBatch = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (waitForAddBatch)
                _addBatchIdle.Wait(TimeSpan.FromSeconds(EconomyManager.ADD_BATCH_WAIT_SECONDS));

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
