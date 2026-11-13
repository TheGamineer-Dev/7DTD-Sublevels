using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SubLevelPhysics
{
    /// <summary>
    /// Builds one runtime mesh/collider set per logical storage cell. No Chunk or block data is
    /// inserted into the primary world's ChunkCluster. The storage snapshot is detached.
    /// </summary>
    internal static class CellProjection
    {
        private static readonly LightingAround ProjectionLighting = new LightingAround(15, 0, 15);

        // True only on the calling thread, only while a projection mesh is being built. The UV patch
        // below reads it so vanilla chunk meshing on other threads is never affected.
        [ThreadStatic] private static bool buildingProjection;
        internal static bool BuildingProjection { get { return buildingProjection; } }

        public static void Rebuild(SublevelPlot plot)
        {
            if (plot == null || plot.Root == null) return;
            ClearChildren(plot.Root.transform);
            if (MeshDescription.meshes == null || MeshDescription.meshes.Length == 0)
            {
                Log.Warning("[SubLevel] MeshDescription is not initialized; projection data is stored but cannot be rendered in this process.");
                return;
            }

            IReadOnlyCollection<CellRecord> records = SublevelStorageApi.GetCellRecords(plot.Id);
            int renderedCells = 0;
            ColliderStats total = new ColliderStats();
            foreach (CellRecord record in records)
            {
                BlockValue[] blocks;
                if (!SublevelStorageApi.TryReadCellBlocks(plot.Id, record.Logical.X, record.Logical.Z, out blocks))
                    throw new InvalidOperationException("Could not read projection cell " + record.Logical + ".");
                GameObject cellRoot = new GameObject("Cell_" + record.Logical.X + "_" + record.Logical.Z);
                cellRoot.transform.SetParent(plot.Root.transform, false);
                cellRoot.transform.localPosition = new Vector3(record.Logical.X * 16, 0f, record.Logical.Z * 16);
                BuildCellMeshes(cellRoot, record, blocks);
                ColliderStats cs = CellColliders.Build(cellRoot, blocks);
                total.Boxes += cs.Boxes; total.ConvexPieces += cs.ConvexPieces; total.Volume += cs.Volume;
                renderedCells++;
            }
            plot.ColliderBoxes = total.Boxes;
            plot.ColliderConvex = total.ConvexPieces;
            plot.SolidVolume = total.Volume;
            Physics.SyncTransforms();
            if (plot.Driver != null) plot.Driver.OnCollidersRebuilt();
            Log.Out("[SubLevel] Rebuilt projection " + plot.Id + ": " + renderedCells + " logical cell(s), " + total.Boxes + " box + " +
                total.ConvexPieces + " convex collider piece(s), solid volume " + total.Volume.ToString("F1") + " m3; primary ChunkCluster unchanged.");
        }

        private static void BuildCellMeshes(GameObject cellRoot, CellRecord record, BlockValue[] blocks)
        {
            int count = MeshDescription.meshes.Length;
            VoxelMesh[] meshes = new VoxelMesh[count];
            for (int i = 0; i < count; i++)
            {
                MeshDescription md = MeshDescription.meshes[i];
                if (md != null) meshes[i] = VoxelMesh.Create(i, md.meshType, 1024);
            }

            Vector3[] vertices = new Vector3[8];
            buildingProjection = true;
            try
            {
            for (int y = 1; y <= 254; y++)
            {
                int yBase = y * 256;
                for (int z = 0; z < 16; z++)
                {
                    int row = yBase + z * 16;
                    for (int x = 0; x < 16; x++)
                    {
                        BlockValue value = blocks[row + x];
                        if (value.isair || value.Block == null || value.Block.shape == null || value.Block.shape is BlockShapeModelEntity) continue;
                        Vector3 draw = new Vector3(x - 0.5f, y - PlotSpace.PivotY - 0.5f, z - 0.5f);
                        Vector3i logicalPos = new Vector3i(record.Logical.X * 16 + x, y, record.Logical.Z * 16 + z);
                        value.Block.shape.renderFull(logicalPos, value, draw, vertices, ProjectionLighting,
                            TextureFullArray.Default, meshes, BlockShape.MeshPurpose.World);
                    }
                }
            }
            }
            finally { buildingProjection = false; }

            for (int i = 0; i < count; i++)
            {
                VoxelMesh vm = meshes[i];
                MeshDescription md = MeshDescription.meshes[i];
                if (vm == null || md == null) continue;
                vm.Finished();
                if (vm.m_Vertices.Count > 0 && md.materials != null && md.materials.Length > 0)
                {
                    GameObject visual = new GameObject(string.IsNullOrEmpty(md.Name) ? "Mesh_" + i : md.Name);
                    visual.transform.SetParent(cellRoot.transform, false);
                    MeshFilter filter;
                    MeshRenderer renderer;
                    VoxelMesh.CreateMeshFilter(i, 0, visual, md.Tag, false, out filter, out renderer);
                    if (filter != null && renderer != null)
                    {
                        vm.CopyToMesh(filter, renderer, 0);
                        if (!string.IsNullOrEmpty(md.MeshLayerName) && ChunkCluster.LayerMappingTable.ContainsKey(md.MeshLayerName))
                            visual.layer = ChunkCluster.LayerMappingTable[md.MeshLayerName];
                    }
                    else UnityEngine.Object.Destroy(visual);
                }
            }
        }

        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                // Only the visual meshes are owned per rebuild. Collider meshes come from the shared
                // ShapeColliderCache and must not be destroyed here.
                foreach (MeshFilter filter in child.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null) UnityEngine.Object.Destroy(filter.sharedMesh);
                // Destroy only runs at the end of the frame; deactivating removes the old colliders
                // from the physics scene immediately so they cannot overlap the rebuilt set.
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }
    }

    /// <summary>
    /// Global/triplanar UVs on cube-like BlockShapeNew meshes are evaluated against the block's absolute
    /// position and world axes. A sublevel is moved/rotated as one Unity transform, so those UVs stay
    /// world-anchored and cubes look like windows into a stationary repeating texture. While a projection
    /// mesh is being built on this thread, force the Local mesh purpose so the face UVs are baked into
    /// the mesh instead. Vanilla chunk meshing (other threads) is untouched.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(BlockShapeNew), nameof(BlockShapeNew.renderFace), new[]
    {
        typeof(Vector3i), typeof(BlockValue), typeof(Vector3), typeof(BlockFace), typeof(Vector3[]),
        typeof(LightingAround), typeof(TextureFullArray), typeof(VoxelMesh[]), typeof(BlockShape.MeshPurpose)
    })]
    internal static class SubLevelLocalCubeUvPatch
    {
        private static void Prefix(ref BlockShape.MeshPurpose _purpose)
        {
            if (!CellProjection.BuildingProjection) return;
            if (_purpose != BlockShape.MeshPurpose.World) return;
            _purpose = BlockShape.MeshPurpose.Local;
        }
    }
}
