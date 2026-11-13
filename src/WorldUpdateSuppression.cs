using System;
using System.Reflection;
using HarmonyLib;

namespace SubLevelPhysics
{
    /// <summary>
    /// Suppresses chunk work only when the target ChunkCluster belongs to the dedicated
    /// Sublevels world. The main world's lighting, stability, meshing and neighbor notifications
    /// are not intercepted by these patches.
    /// </summary>
    internal static class WorldUpdateSuppression
    {
        internal static World StorageWorld;
        internal static bool Enabled = true;
        internal static bool SuppressAmbient = true;
        private static readonly FieldInfo ClusterWorldField = typeof(ChunkCluster).GetField("world",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        internal static bool IsStorageWorldActive()
        {
            return Enabled && StorageWorld != null && GameManager.Instance != null &&
                   GameManager.Instance.World == StorageWorld;
        }

        internal static bool IsStorageCluster(ChunkCluster cluster)
        {
            if (!Enabled || StorageWorld == null || cluster == null || ClusterWorldField == null) return false;
            return ReferenceEquals(ClusterWorldField.GetValue(cluster), StorageWorld);
        }
    }

    [HarmonyPatch(typeof(WorldEnvironment), "AmbientSpectrumFrameUpdate")]
    internal static class SuppressStorageWorldEnvironmentUpdate
    {
        private static bool Prefix()
        {
            return !WorldUpdateSuppression.IsStorageWorldActive() || !WorldUpdateSuppression.SuppressAmbient;
        }
    }

    [HarmonyPatch(typeof(ChunkCluster), nameof(ChunkCluster.LightChunk), new[] { typeof(Chunk), typeof(Chunk[]) })]
    internal static class SuppressStorageChunkLighting
    {
        private static bool Prefix(ChunkCluster __instance) { return !WorldUpdateSuppression.IsStorageCluster(__instance); }
    }

    [HarmonyPatch(typeof(ChunkCluster), nameof(ChunkCluster.CalcStability), new[] { typeof(Chunk) })]
    internal static class SuppressStorageChunkStability
    {
        private static bool Prefix(ChunkCluster __instance) { return !WorldUpdateSuppression.IsStorageCluster(__instance); }
    }

    [HarmonyPatch(typeof(ChunkCluster), nameof(ChunkCluster.RegenerateChunk), new[] { typeof(Chunk), typeof(Chunk[]) })]
    internal static class SuppressStorageChunkMeshing
    {
        private static bool Prefix(ChunkCluster __instance) { return !WorldUpdateSuppression.IsStorageCluster(__instance); }
    }

    [HarmonyPatch(typeof(ChunkCluster), "addDistantDecorationBlocks", new[] { typeof(Chunk) })]
    internal static class SuppressStorageDistantDecorations
    {
        private static bool Prefix(ChunkCluster __instance) { return !WorldUpdateSuppression.IsStorageCluster(__instance); }
    }

    [HarmonyPatch(typeof(ChunkCluster), "chunkPosNeedsRegeneration", new[] { typeof(Chunk), typeof(int), typeof(int), typeof(int), typeof(bool) })]
    internal static class SuppressStorageNeighborMeshInvalidation
    {
        private static bool Prefix(ChunkCluster __instance) { return !WorldUpdateSuppression.IsStorageCluster(__instance); }
    }

    [HarmonyPatch(typeof(ChunkCluster), "notifyBlocksOfNeighborChange", new[] { typeof(Vector3i), typeof(BlockValue), typeof(BlockValue) })]
    internal static class SuppressStorageNeighborBlockNotifications
    {
        private static bool Prefix(ChunkCluster __instance) { return !WorldUpdateSuppression.IsStorageCluster(__instance); }
    }

    // These hooks intentionally do not claim to disable every storage-world subsystem. Water
    // simulation, entity scheduling, random ticks and MultiWorld lifecycle scheduling still
    // require dedicated-server runtime validation before they can be declared fully suppressed.
}
