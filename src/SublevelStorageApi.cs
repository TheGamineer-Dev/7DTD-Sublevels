namespace SubLevelPhysics
{
    /// <summary>
    /// Narrow public API for the projection/interaction port. This facade never exposes the
    /// primary world's ChunkCluster and never writes static blocks into the player world.
    /// All calls are main-thread only because MultiWorld context selection is main-thread bound.
    /// </summary>
    public static class SublevelStorageApi
    {
        private static SublevelCellStore store;
        public static bool IsReady { get { return store != null; } }

        internal static void Attach(SublevelCellStore value) { store = value; }
        internal static void Detach() { store = null; }

        public static CellRecord EnsureCell(string sublevelId, int logicalCellX, int logicalCellZ)
        {
            return RequireStore().EnsureCell(sublevelId, new CellAddress(logicalCellX, logicalCellZ));
        }

        public static bool TryGetStorageChunk(string sublevelId, int logicalCellX, int logicalCellZ, out Chunk chunk)
        {
            return RequireStore().TryGetStorageChunk(sublevelId, new CellAddress(logicalCellX, logicalCellZ), out chunk);
        }

        public static bool TryReadCellBlocks(string sublevelId, int logicalCellX, int logicalCellZ, out BlockValue[] blocks)
        {
            return RequireStore().TryReadCellBlocks(sublevelId, new CellAddress(logicalCellX, logicalCellZ), out blocks);
        }

        public static System.Collections.Generic.IReadOnlyCollection<CellRecord> GetCellRecords(string sublevelId)
        {
            return RequireStore().GetCellRecords(sublevelId);
        }

        public static bool TryGetBlock(string sublevelId, int localX, int y, int localZ, out BlockValue value)
        {
            return RequireStore().TryGetBlock(sublevelId, localX, y, localZ, out value);
        }

        public static void SetBlock(string sublevelId, int localX, int y, int localZ, BlockValue value)
        {
            RequireStore().SetBlock(sublevelId, localX, y, localZ, value);
        }

        public static bool RecycleEmptyCell(string sublevelId, int logicalCellX, int logicalCellZ)
        {
            return RequireStore().RecycleEmptyCell(sublevelId, new CellAddress(logicalCellX, logicalCellZ));
        }

        private static SublevelCellStore RequireStore()
        {
            if (store == null) throw new System.InvalidOperationException("Sublevels storage has not initialized.");
            return store;
        }
    }
}
