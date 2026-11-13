using System.Collections.Generic;
using UnityEngine;

namespace SubLevelPhysics
{
    internal struct ColliderStats
    {
        public int Boxes, ConvexPieces;
        public float Volume;
    }

    /// <summary>
    /// Builds a cell's collision from the cell's BLOCKS (the same approach the original chunk-based mod used),
    /// not from the visual mesh. Each non-air block contributes the collision geometry of its own shape
    /// (ShapeColliderCache). Full cubes are greedily merged into large BoxColliders per cell; other shapes
    /// become exact boxes, exact convex pieces, or box unions. Everything is a child of the cell root, which is
    /// a child of the sublevel Rigidbody root, so the same set works kinematic and dynamic and stitches all
    /// cells into one rigid body.
    ///
    /// Tag and layer of each collider come from the block's MeshDescription (Tag, ColliderLayerName), exactly
    /// as vanilla assigns them to chunk collision meshes, so targeting, the layer matrix and player collision
    /// behave like ordinary terrain.
    ///
    /// Space: the visual renderer draws block (x,y,z) of a cell spanning [x-0.5,x+0.5] around the integer
    /// point (x, y-PivotY, z) in cell-root space, so the block centre used here is that integer point.
    /// </summary>
    internal static class CellColliders
    {
        public static ColliderStats Build(GameObject cellRoot, BlockValue[] blocks)
        {
            ColliderStats stats = new ColliderStats();
            if (blocks == null || MeshDescription.meshes == null) return stats;

            GameObject container = new GameObject("BlockColliders");
            container.transform.SetParent(cellRoot.transform, false);

            Dictionary<int, GameObject> holders = new Dictionary<int, GameObject>();
            Dictionary<int, List<Vector3Int>> cubeCells = new Dictionary<int, List<Vector3Int>>();
            int minY = int.MaxValue, maxY = int.MinValue;

            for (int y = 1; y <= 254; y++)
            {
                int yBase = y * 256;
                for (int z = 0; z < 16; z++)
                {
                    int row = yBase + z * 16;
                    for (int x = 0; x < 16; x++)
                    {
                        BlockValue bv = blocks[row + x];
                        if (bv.isair || bv.ischild) continue;
                        ShapePiece[] pieces = ShapeColliderCache.Get(bv);
                        for (int p = 0; p < pieces.Length; p++)
                        {
                            ShapePiece piece = pieces[p];
                            if (piece.IsFullCube)
                            {
                                List<Vector3Int> list;
                                if (!cubeCells.TryGetValue(piece.MeshIndex, out list)) cubeCells[piece.MeshIndex] = list = new List<Vector3Int>();
                                list.Add(new Vector3Int(x, y, z));
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                                continue;
                            }
                            Vector3 blockCentre = new Vector3(x, y - PlotSpace.PivotY, z);
                            GameObject holder = Holder(container, holders, piece.MeshIndex);
                            if (holder == null) continue;
                            if (piece.Kind == PieceKind.Box)
                            {
                                BoxCollider bc = holder.AddComponent<BoxCollider>();
                                bc.center = blockCentre + piece.Center;
                                bc.size = piece.Size;
                                stats.Boxes++;
                            }
                            else
                            {
                                GameObject go = new GameObject("S");
                                go.tag = holder.tag;
                                go.layer = holder.layer;
                                go.transform.SetParent(holder.transform, false);
                                go.transform.localPosition = blockCentre;
                                MeshCollider mc = go.AddComponent<MeshCollider>();
                                mc.sharedMesh = piece.Mesh;
                                mc.convex = true;
                                stats.ConvexPieces++;
                            }
                            stats.Volume += piece.Volume;
                        }
                    }
                }
            }

            foreach (KeyValuePair<int, List<Vector3Int>> group in cubeCells)
            {
                int sy = maxY - minY + 1;
                bool[] occ = new bool[16 * sy * 16];
                foreach (Vector3Int cell in group.Value) occ[((cell.y - minY) * 16 + cell.z) * 16 + cell.x] = true;
                List<int[]> boxes = new List<int[]>();
                ShapeColliderCache.GreedyMerge(occ, 16, sy, 16, boxes);
                GameObject holder = Holder(container, holders, group.Key);
                if (holder == null) continue;
                foreach (int[] b in boxes)
                {
                    Vector3 size = new Vector3(b[3] - b[0], b[4] - b[1], b[5] - b[2]);
                    BoxCollider bc = holder.AddComponent<BoxCollider>();
                    // Block index i spans [i-0.5, i+0.5], so a merged run starting at index b0 begins at b0-0.5.
                    bc.center = new Vector3(b[0] - 0.5f, minY + b[1] - PlotSpace.PivotY - 0.5f, b[2] - 0.5f) + size * 0.5f;
                    bc.size = size;
                    stats.Boxes++;
                    stats.Volume += size.x * size.y * size.z;
                }
            }
            return stats;
        }

        private static GameObject Holder(GameObject container, Dictionary<int, GameObject> holders, int meshIndex)
        {
            GameObject h;
            if (holders.TryGetValue(meshIndex, out h)) return h;
            if (meshIndex < 0 || meshIndex >= MeshDescription.meshes.Length) return null;
            MeshDescription md = MeshDescription.meshes[meshIndex];
            if (md == null || string.IsNullOrEmpty(md.ColliderLayerName) || !ChunkCluster.LayerMappingTable.ContainsKey(md.ColliderLayerName)) return null;
            h = new GameObject("mesh" + meshIndex + "_" + md.Tag);
            if (!string.IsNullOrEmpty(md.Tag)) h.tag = md.Tag;
            h.layer = ChunkCluster.LayerMappingTable[md.ColliderLayerName];
            h.transform.SetParent(container.transform, false);
            holders[meshIndex] = h;
            return h;
        }
    }
}
