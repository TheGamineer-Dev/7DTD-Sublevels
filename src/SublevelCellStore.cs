using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using UnityEngine;

namespace SubLevelPhysics
{
    /// <summary>
    /// The sole authoritative static block store. Static blocks are persisted only in chunks
    /// belonging to the dedicated Sublevels world. The player world receives no BlockValue copy.
    /// All public operations must execute on the Unity/game main thread.
    /// </summary>
    internal sealed class SublevelCellStore
    {
        public const int CellWidth = 16;
        private readonly CellAllocator allocator = new CellAllocator();
        private readonly object gate = new object();
        private bool loaded;
        private string ledgerPath;

        public CellAllocator Allocator { get { return allocator; } }

        public void Initialize()
        {
            MultiWorldRuntimeBridge.StorageWorld sw = MultiWorldRuntimeBridge.FindStorageWorld();
            if (sw == null || sw.World == null || string.IsNullOrEmpty(sw.SavePath))
                throw new InvalidOperationException("Sublevels world or save path is unavailable.");
            Log.Out("[SubLevel][StorageDiag] Store.Initialize: contextFound=" + (sw != null) +
                "; worldFound=" + (sw != null && sw.World != null) + "; savePath=" + (sw == null ? "<no context>" : (sw.SavePath ?? "<null>")));
            if (sw == null || sw.World == null || string.IsNullOrEmpty(sw.SavePath))
                throw new InvalidOperationException("Sublevels world or save path is unavailable. " + MultiWorldRuntimeBridge.DescribeState());
            ledgerPath = Path.Combine(sw.SavePath, "Sublevels", "cells.xml");
            Log.Out("[SubLevel][StorageDiag] Ledger path resolved: " + ledgerPath);
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath));
            Log.Out("[SubLevel][StorageDiag] Ledger directory exists=" + Directory.Exists(Path.GetDirectoryName(ledgerPath)) +
                "; ledgerExists=" + File.Exists(ledgerPath));
            LoadLedger();
            Log.Out("[SubLevel][StorageDiag] Ledger load complete; mappedCells=" + allocator.Records.Count() +
                "; freeCells=" + allocator.FreeCells.Count() + "; nextSlot=" + allocator.NextSlot);
            // Validate every live mapping against the storage world's disk/cache before accepting writes.
            int validated = 0;
            foreach (CellRecord record in allocator.Records.ToArray())
            {
                Log.Out("[SubLevel][StorageDiag] Validating ledger mapping sublevel=" + record.SublevelId +
                    "; logical=" + record.Logical + "; physical=" + record.Physical);
                bool exists = MultiWorldRuntimeBridge.InStorageWorld(world => LoadExistingChunk(world, record.Physical) != null);
                if (!exists) throw new InvalidDataException("Ledger references missing storage chunk " + record.Physical +
                    " for sublevel " + record.SublevelId + ". Refusing to create a replacement over missing authoritative data.");
                validated++;
            }
            Log.Out("[SubLevel][StorageDiag] Existing mapping validation complete; validated=" + validated);
        }

        public CellRecord EnsureCell(string sublevelId, CellAddress logical)
        {
            CellRecord result;
            bool created;
            lock (gate)
            {
                EnsureLoaded();
                CellRecord existing;
                created = !allocator.TryGet(sublevelId, logical, out existing);
                result = allocator.Allocate(sublevelId, logical,
                    PrepareReusedCell, CreateFreshPhysicalCell);
            }
            // Persist ownership when the mapping is created, not on every individual block edit.
            if (created)
            {
                try { SaveLedger(); }
                catch
                {
                    lock (gate) allocator.RollbackUnusedAllocation(sublevelId, logical);
                    throw;
                }
            }
            return result;
        }

        /// <summary>
        /// Returns the authoritative storage Chunk for a logical cell. The returned Chunk remains
        /// owned by the Sublevels world; callers may read it to build transient main-world meshes,
        /// but must never parent its ChunkGameObject or mutate it outside this store's write methods.
        /// Main-thread only.
        /// </summary>
        public bool TryGetStorageChunk(string sublevelId, CellAddress logical, out Chunk chunk)
        {
            CellRecord record;
            lock (gate)
            {
                EnsureLoaded();
                if (!allocator.TryGet(sublevelId, logical, out record)) { chunk = null; return false; }
            }
            chunk = MultiWorldRuntimeBridge.InStorageWorld(world => LoadExistingChunk(world, record.Physical));
            if (chunk == null) throw new InvalidDataException("Mapped storage chunk is missing: " + record.Physical);
            return true;
        }

        /// <summary>
        /// Copies one authoritative cell into a transient projection snapshot. Index layout is
        /// ((y * 16) + z) * 16 + x. The returned array is detached; edits must go through SetBlock.
        /// </summary>
        public bool TryReadCellBlocks(string sublevelId, CellAddress logical, out BlockValue[] blocks)
        {
            CellRecord record;
            lock (gate)
            {
                EnsureLoaded();
                if (!allocator.TryGet(sublevelId, logical, out record)) { blocks = null; return false; }
            }
            blocks = MultiWorldRuntimeBridge.InStorageWorld(world => {
                Chunk c = LoadExistingChunk(world, record.Physical);
                if (c == null) throw new InvalidDataException("Mapped storage chunk is missing: " + record.Physical);
                BlockValue[] snapshot = new BlockValue[16 * 16 * 256];
                for (int y = 0; y < 256; y++)
                    for (int z = 0; z < 16; z++)
                        for (int x = 0; x < 16; x++)
                            snapshot[((y * 16) + z) * 16 + x] = c.GetBlockNoDamage(x, y, z);
                return snapshot;
            });
            return true;
        }

        public IReadOnlyCollection<CellRecord> GetCellRecords(string sublevelId)
        {
            lock (gate) { EnsureLoaded(); return allocator.GetCells(sublevelId); }
        }

        public bool TryGetBlock(string sublevelId, int localX, int y, int localZ, out BlockValue value)
        {
            ValidateY(y);
            CellAddress logical = new CellAddress(FloorDiv(localX, CellWidth), FloorDiv(localZ, CellWidth));
            CellRecord record;
            lock (gate)
            {
                EnsureLoaded();
                if (!allocator.TryGet(sublevelId, logical, out record)) { value = BlockValue.Air; return false; }
            }
            int bx = Mod(localX, CellWidth), bz = Mod(localZ, CellWidth);
            value = MultiWorldRuntimeBridge.InStorageWorld(world => {
                Chunk c = LoadExistingChunk(world, record.Physical);
                if (c == null) throw new InvalidDataException("Mapped storage chunk is missing: " + record.Physical);
                return c.GetBlockNoDamage(bx, y, bz);
            });
            return true;
        }

        public void SetBlock(string sublevelId, int localX, int y, int localZ, BlockValue value)
        {
            ValidateWritableY(y);
            CellAddress logical = new CellAddress(FloorDiv(localX, CellWidth), FloorDiv(localZ, CellWidth));
            // An air write into an unallocated logical cell is a no-op. Do not consume a
            // physical storage chunk merely because an interaction clears empty space.
            CellRecord record = null;
            lock (gate)
            {
                EnsureLoaded();
                if (value.isair && !allocator.TryGet(sublevelId, logical, out record)) return;
                if (!value.isair) allocator.TryGet(sublevelId, logical, out record);
            }
            if (record == null) record = EnsureCell(sublevelId, logical);
            MultiWorldRuntimeBridge.InStorageWorld(world => {
                Chunk c = LoadExistingChunk(world, record.Physical);
                if (c == null) throw new InvalidDataException("Allocated storage chunk is missing: " + record.Physical);
                c.SetBlockRaw(Mod(localX, CellWidth), y, Mod(localZ, CellWidth), value);
                c.isModified = true;
                record.LastTouchedUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            });
        }

        public bool IsCellEmpty(PhysicalCell cell)
        {
            return MultiWorldRuntimeBridge.InStorageWorld(world => {
                Chunk c = LoadExistingChunk(world, cell);
                if (c == null) return false; // Missing data is not proof of emptiness.
                return IsChunkEmpty(c);
            });
        }

        public bool RecycleEmptyCell(string id, CellAddress logical)
        {
            bool released;
            lock (gate) released = allocator.ReleaseIfEmpty(id, logical, IsCellEmpty);
            if (released) SaveLedger();
            return released;
        }

        public void SaveLedger()
        {
            if (string.IsNullOrEmpty(ledgerPath)) return;
            lock (gate)
            {
                XmlDocument doc = new XmlDocument();
                XmlElement root = doc.CreateElement("SublevelCells");
                root.SetAttribute("version", "3");
                root.SetAttribute("nextSlot", allocator.NextSlot.ToString());
                doc.AppendChild(root);
                foreach (CellRecord r in allocator.Records.OrderBy(x => x.SublevelId).ThenBy(x => x.Logical.X).ThenBy(x => x.Logical.Z))
                {
                    XmlElement e = doc.CreateElement("Cell"); root.AppendChild(e);
                    e.SetAttribute("sublevel", r.SublevelId);
                    e.SetAttribute("lx", r.Logical.X.ToString()); e.SetAttribute("lz", r.Logical.Z.ToString());
                    e.SetAttribute("px", r.Physical.X.ToString()); e.SetAttribute("pz", r.Physical.Z.ToString());
                    e.SetAttribute("touched", r.LastTouchedUtcTicks.ToString());
                }
                foreach (PhysicalCell p in allocator.FreeCells.OrderBy(x => x.X).ThenBy(x => x.Z))
                {
                    XmlElement e = doc.CreateElement("FreeCell"); root.AppendChild(e);
                    e.SetAttribute("px", p.X.ToString()); e.SetAttribute("pz", p.Z.ToString());
                }
                string tmp = ledgerPath + ".tmp";
                doc.Save(tmp);
                if (File.Exists(ledgerPath))
                {
                    string backup = ledgerPath + ".bak";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(tmp, ledgerPath, backup);
                }
                else File.Move(tmp, ledgerPath);
            }
        }

        /// <summary>
        /// Ensures the persistent cell ledger has been loaded before any allocator access.
        /// Initialize normally loads it eagerly; this guard also protects later entry points.
        /// </summary>
        private void EnsureLoaded()
        {
            if (loaded) return;
            if (string.IsNullOrEmpty(ledgerPath))
                throw new InvalidOperationException("Sublevels cell storage has not been initialized.");
            LoadLedger();
        }

        private void LoadLedger()
        {
            lock (gate)
            {
                if (loaded) return;
                List<CellRecord> records = new List<CellRecord>();
                List<PhysicalCell> free = new List<PhysicalCell>();
                long nextSlot = 0;
                if (File.Exists(ledgerPath))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.XmlResolver = null;
                    doc.Load(ledgerPath);
                    XmlElement root = doc.DocumentElement;
                    if (root == null || root.Name != "SublevelCells") throw new InvalidDataException("Invalid Sublevels cell ledger root.");
                    string versionText = root.GetAttribute("version");
                    int version = string.IsNullOrEmpty(versionText) ? 1 : int.Parse(versionText, System.Globalization.CultureInfo.InvariantCulture);
                    if (version < 2 || version > 3) throw new InvalidDataException("Unsupported Sublevels cell ledger version " + version + ".");
                    // v2 used a stride-five candidate sequence; the cursor is a slot counter,
                    // not a persisted physical coordinate. v3 interprets subsequent slots densely.
                    long.TryParse(root.GetAttribute("nextSlot"), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out nextSlot);
                    foreach (XmlNode n in root.ChildNodes)
                    {
                        XmlElement e = n as XmlElement;
                        if (e == null) continue;
                        if (e.Name == "Cell")
                        {
                            records.Add(new CellRecord {
                                SublevelId = Required(e, "sublevel"),
                                Logical = new CellAddress(ParseInt(e, "lx"), ParseInt(e, "lz")),
                                Physical = new PhysicalCell(ParseInt(e, "px"), ParseInt(e, "pz")),
                                LastTouchedUtcTicks = ParseLong(e, "touched")
                            });
                        }
                        else if (e.Name == "FreeCell") free.Add(new PhysicalCell(ParseInt(e, "px"), ParseInt(e, "pz")));
                    }
                }
                allocator.Restore(records, free, nextSlot);
                loaded = true;
            }
        }

        private bool PrepareReusedCell(PhysicalCell p)
        {
            return MultiWorldRuntimeBridge.InStorageWorld(world => {
                Chunk chunk = LoadExistingChunk(world, p);
                if (chunk == null || !IsChunkEmpty(chunk)) return false;
                chunk.SavedInWorldTicks = 0;
                chunk.LastTimeRandomTicked = 0;
                chunk.isModified = true;
                return true;
            });
        }

        private bool CreateFreshPhysicalCell(PhysicalCell p)
        {
            return MultiWorldRuntimeBridge.InStorageWorld(world => {
                ChunkCluster cc = world.ChunkCache;
                if (cc == null) return false;
                long key = WorldChunkCache.MakeChunkKey(p.X, p.Z);
                if (cc.ContainsChunkSync(key)) return false;
                ChunkProviderGenerateWorld provider = cc.ChunkProvider as ChunkProviderGenerateWorld;
                RegionFileManager regions = provider == null ? null : provider.m_RegionFileManager;
                if (regions == null) return false; // Without the region index, disk occupancy cannot be proven.
                // Dense layout: only the target coordinate must be unused. Adjacent storage
                // chunks are intentional and may belong to unrelated logical cells/sublevels.
                // Storage-world neighbor-dependent processing is suppressed by scoped hooks;
                // raw block writes never call ChunkCluster.SetBlock or notify adjacent blocks.
                if (regions.ContainsChunkSync(key)) return false;
                Chunk c = MemoryPools.PoolChunks.AllocSync(true);
                if (c == null) return false;
                c.X = p.X; c.Z = p.Z;
                c.NeedsDecoration = false;
                c.NeedsLightDecoration = false;
                c.NeedsLightCalculation = false;
                c.NeedsRegeneration = false;
                c.ResetStability();
                c.RefreshSunlight();
                c.isModified = true;
                if (!cc.AddChunkSync(c))
                {
                    MemoryPools.PoolChunks.FreeSync(c);
                    return false;
                }
                return true;
            });
        }

        /// <summary>Loads a previously persisted chunk into the live cluster without invoking terrain generation.</summary>
        private static Chunk LoadExistingChunk(World world, PhysicalCell p)
        {
            ChunkCluster cc = world == null ? null : world.ChunkCache;
            if (cc == null) return null;
            Chunk chunk = cc.GetChunkSync(p.X, p.Z);
            if (chunk != null) return chunk;
            ChunkProviderGenerateWorld provider = cc.ChunkProvider as ChunkProviderGenerateWorld;
            RegionFileManager regions = provider == null ? null : provider.m_RegionFileManager;
            if (regions == null) return null;
            long key = WorldChunkCache.MakeChunkKey(p.X, p.Z);
            if (!regions.ContainsChunkSync(key)) return null;
            chunk = regions.GetChunkSync(key);
            if (chunk == null) return null;
            regions.RemoveChunkSync(key);
            if (!cc.AddChunkSync(chunk))
                throw new InvalidOperationException("Could not move saved storage chunk into its world cache: " + p);
            return chunk;
        }

        private static bool IsChunkEmpty(Chunk c)
        {
            if (c == null) return false;
            for (int layer = 0; layer < c.m_BlockLayers.Length; layer++)
            {
                if (c.m_BlockLayers[layer] == null) continue;
                int y0 = layer * 4;
                for (int y = y0; y < y0 + 4 && y < 256; y++)
                    for (int z = 0; z < 16; z++)
                        for (int x = 0; x < 16; x++)
                            if (!c.GetBlockNoDamage(x, y, z).isair) return false;
            }
            return !HasPersistentPayload(c);
        }

        private static bool HasPersistentPayload(Chunk c)
        {
            // A cell is reusable only when all block-associated/persistent payload collections
            // are empty. Unknown collection representations are treated as occupied.
            string[] payloadNames = {
                "tileEntities", "entityLists", "entityStubs", "pendingEntityCreateOps",
                "ChunkCustomData", "insideDevices", "insideDevicesHashSet", "triggerData",
                "props", "propEntities", "propEntitiesToRemove", "blockEntityStubs",
                "blockEntityStubsToRemove", "sleeperVolumes", "triggerVolumes", "wallVolumes",
                "IndexedBlocks", "tickedBlocks", "biomeSpawnData"
            };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in payloadNames)
            {
                FieldInfo field = typeof(Chunk).GetField(name, flags);
                if (field == null) continue;
                object value = field.GetValue(c);
                if (HasItems(value)) return true;
            }

            // Orphaned damage, density, texture, light, stability, or water data can survive
            // after a block is removed. Recycle only if every channel reports its default state.
            foreach (FieldInfo field in typeof(Chunk).GetFields(flags))
            {
                string name = field.Name;
                if (!(name.StartsWith("chn", StringComparison.OrdinalIgnoreCase))) continue;
                object value = field.GetValue(c);
                if (value == null) continue;
                Array channels = value as Array;
                if (channels != null)
                {
                    foreach (object channel in channels)
                        if (channel != null && !IsDefaultChannel(channel)) return true;
                }
                else if (!IsDefaultChannel(value)) return true;
            }
            return false;
        }

        private static bool HasItems(object value)
        {
            if (value == null) return false;
            Array array = value as Array;
            if (array != null)
            {
                foreach (object item in array)
                    if (HasItems(item)) return true;
                return false;
            }
            System.Collections.ICollection collection = value as System.Collections.ICollection;
            if (collection != null) return collection.Count > 0;
            PropertyInfo count = value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (count != null && count.PropertyType == typeof(int))
                return (int)count.GetValue(value, null) > 0;
            // DictionaryKeyValueList (used by ChunkCustomData) has no Count property, but its
            // backing dictionary is authoritative for occupancy.
            FieldInfo dictionary = value.GetType().GetField("dict", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (dictionary != null) return HasItems(dictionary.GetValue(value));
            // Some game collection wrappers expose only enumerators. If the wrapper has no
            // verifiable count, conservatively refuse to recycle it.
            if (value is System.Collections.IEnumerable) return true;
            return true;
        }

        private static bool IsDefaultChannel(object channel)
        {
            MethodInfo method = channel.GetType().GetMethod("IsDefault", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(bool)) return false;
            return (bool)method.Invoke(channel, null);
        }

        private static string Required(XmlElement e, string name)
        {
            string value = e.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing ledger attribute " + name + ".");
            return value;
        }
        private static int ParseInt(XmlElement e, string name) { return int.Parse(Required(e, name), System.Globalization.CultureInfo.InvariantCulture); }
        private static long ParseLong(XmlElement e, string name) { return long.Parse(Required(e, name), System.Globalization.CultureInfo.InvariantCulture); }
        private static int FloorDiv(int value, int divisor) { int q = value / divisor, r = value % divisor; return r < 0 ? q - 1 : q; }
        private static int Mod(int value, int divisor) { int r = value % divisor; return r < 0 ? r + divisor : r; }
        private static void ValidateY(int y) { if (y < 0 || y >= 256) throw new ArgumentOutOfRangeException("y", "7DTD v3.4 cell height is 256 blocks."); }
        private static void ValidateWritableY(int y) { if (y <= 0 || y >= 255) throw new ArgumentOutOfRangeException("y", "7DTD v3.4 accepts block writes only at Y 1 through 254."); }
    }
}
