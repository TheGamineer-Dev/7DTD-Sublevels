using System;
using System.Collections.Generic;
using System.Linq;

namespace SubLevelPhysics
{
    /// <summary>Metadata-only logical-to-physical cell allocator. It never stores block data.</summary>
    public sealed class CellAllocator
    {
        // One physical storage chunk is one cell. No guard chunks are reserved: physical
        // storage is densely packed, while logical adjacency is represented by the cell map.
        public const int PhysicalStride = 1;
        public const int PhysicalOrigin = 1024;
        public const int PhysicalRowWidth = 4096;

        private readonly Dictionary<string, Dictionary<CellAddress, CellRecord>> bySublevel =
            new Dictionary<string, Dictionary<CellAddress, CellRecord>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<PhysicalCell, CellRecord> byPhysical = new Dictionary<PhysicalCell, CellRecord>();
        private readonly SortedSet<PhysicalCell> free = new SortedSet<PhysicalCell>(
            Comparer<PhysicalCell>.Create((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z)));
        private long nextSlot;

        public IEnumerable<CellRecord> Records { get { return byPhysical.Values; } }
        public IEnumerable<PhysicalCell> FreeCells { get { return free; } }
        public long NextSlot { get { return nextSlot; } }

        public void Restore(IEnumerable<CellRecord> records, IEnumerable<PhysicalCell> reusableEmptyCells, long savedNextSlot)
        {
            bySublevel.Clear(); byPhysical.Clear(); free.Clear();
            nextSlot = Math.Max(0, savedNextSlot);
            foreach (CellRecord r in records ?? Enumerable.Empty<CellRecord>())
            {
                if (r == null || string.IsNullOrWhiteSpace(r.SublevelId)) continue;
                Dictionary<CellAddress, CellRecord> cells;
                if (!bySublevel.TryGetValue(r.SublevelId, out cells))
                    bySublevel.Add(r.SublevelId, cells = new Dictionary<CellAddress, CellRecord>());
                if (cells.ContainsKey(r.Logical) || byPhysical.ContainsKey(r.Physical))
                    throw new InvalidOperationException("Duplicate cell mapping in ledger: " + r.SublevelId + "/" + r.Logical);
                cells.Add(r.Logical, r); byPhysical.Add(r.Physical, r);
            }
            foreach (PhysicalCell p in reusableEmptyCells ?? Enumerable.Empty<PhysicalCell>())
                if (!byPhysical.ContainsKey(p)) free.Add(p);
        }

        public bool TryGet(string id, CellAddress logical, out CellRecord record)
        {
            Dictionary<CellAddress, CellRecord> cells;
            if (id != null && bySublevel.TryGetValue(id, out cells) && cells.TryGetValue(logical, out record)) return true;
            record = null; return false;
        }

        public IReadOnlyCollection<CellRecord> GetCells(string id)
        {
            Dictionary<CellAddress, CellRecord> cells;
            return id != null && bySublevel.TryGetValue(id, out cells)
                ? (IReadOnlyCollection<CellRecord>)cells.Values.ToArray() : new CellRecord[0];
        }

        public CellRecord Allocate(string id, CellAddress logical,
            Func<PhysicalCell, bool> prepareReusedCell, Func<PhysicalCell, bool> createFreshCell)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Sublevel ID is required.", "id");
            Dictionary<CellAddress, CellRecord> cells;
            if (!bySublevel.TryGetValue(id, out cells)) bySublevel.Add(id, cells = new Dictionary<CellAddress, CellRecord>());
            CellRecord existing;
            if (cells.TryGetValue(logical, out existing)) return existing;

            PhysicalCell physical = default(PhysicalCell);
            bool found = false;
            while (free.Count > 0)
            {
                physical = free.Min;
                free.Remove(physical);
                if (prepareReusedCell != null && prepareReusedCell(physical)) { found = true; break; }
                // A free-list entry that is no longer verifiably empty is quarantined, not reused.
            }
            if (!found)
            {
                bool created = false;
                physical = default(PhysicalCell);
                for (int attempts = 0; attempts < 1000000; attempts++)
                {
                    PhysicalCell candidate = NextCandidate();
                    if (createFreshCell != null && createFreshCell(candidate))
                    {
                        physical = candidate;
                        created = true;
                        break;
                    }
                }
                if (!created) throw new InvalidOperationException("Could not find a safe unused storage cell after 1,000,000 candidates.");
            }

            CellRecord record = new CellRecord {
                SublevelId = id, Logical = logical, Physical = physical,
                LastTouchedUtcTicks = DateTime.UtcNow.Ticks, Retiring = false
            };
            cells.Add(logical, record); byPhysical.Add(physical, record);
            return record;
        }

        private PhysicalCell NextCandidate()
        {
            // Deterministic row-major packing. Each successive slot is one adjacent chunk
            // along X; after PhysicalRowWidth chunks, allocation advances one row along Z.
            // The storage callback separately checks cache and region-file occupancy.
            for (long attempts = 0; attempts < int.MaxValue; attempts++)
            {
                long slot = nextSlot++;
                long row = slot / PhysicalRowWidth;
                if (row > int.MaxValue - (long)PhysicalOrigin)
                    throw new InvalidOperationException("Physical storage-world Z coordinate range exhausted.");
                int x = PhysicalOrigin + (int)(slot % PhysicalRowWidth);
                int z = PhysicalOrigin + (int)row;
                PhysicalCell p = new PhysicalCell(x, z);
                if (!byPhysical.ContainsKey(p) && !free.Contains(p)) return p;
            }
            throw new InvalidOperationException("Physical cell address space exhausted.");
        }


        /// <summary>Undo a just-created mapping if its ledger commit fails before any block write.</summary>
        public bool RollbackUnusedAllocation(string id, CellAddress logical)
        {
            CellRecord record;
            if (!TryGet(id, logical, out record)) return false;
            bySublevel[id].Remove(logical);
            if (bySublevel[id].Count == 0) bySublevel.Remove(id);
            byPhysical.Remove(record.Physical);
            free.Add(record.Physical);
            return true;
        }

        public bool ReleaseIfEmpty(string id, CellAddress logical, Func<PhysicalCell, bool> isTrulyEmpty)
        {
            CellRecord r;
            if (!TryGet(id, logical, out r) || isTrulyEmpty == null || !isTrulyEmpty(r.Physical)) return false;
            bySublevel[id].Remove(logical);
            if (bySublevel[id].Count == 0) bySublevel.Remove(id);
            byPhysical.Remove(r.Physical); free.Add(r.Physical);
            return true;
        }

        public void RemoveSublevel(string id, bool recycleCells, Func<PhysicalCell, bool> isTrulyEmpty)
        {
            Dictionary<CellAddress, CellRecord> cells;
            if (id == null || !bySublevel.TryGetValue(id, out cells)) return;
            foreach (CellRecord r in cells.Values.ToArray())
            {
                byPhysical.Remove(r.Physical);
                if (recycleCells && isTrulyEmpty != null && isTrulyEmpty(r.Physical)) free.Add(r.Physical);
            }
            bySublevel.Remove(id);
        }
    }
}
