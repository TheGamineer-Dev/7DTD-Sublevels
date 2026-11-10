using System.Collections.Generic;
using UnityEngine;

namespace SubLevelPhysics
{
	internal enum PieceKind { Box, Convex }

	/// <summary>One collision primitive of a single block, in block-centred space (the block cell is [-0.5,0.5]^3).</summary>
	internal sealed class ShapePiece
	{
		public PieceKind Kind;
		/// <summary>Index into MeshDescription.meshes. Vanilla derives the collider's tag (MeshDescription.Tag) and
		/// physics layer (MeshDescription.ColliderLayerName) from this, and so do we.</summary>
		public int MeshIndex;
		public Vector3 Center;     // Box
		public Vector3 Size;       // Box
		public Mesh Mesh;          // Convex: block-centred, shared by every block with the same type+rotation
		public bool IsFullCube;    // box filling the whole cell: eligible for cross-block greedy merging
		public float Volume;       // m^3 of solid, used for mass
	}

	/// <summary>
	/// Turns a block (type + rotation) into collision primitives using the block's OWN collision data, the same
	/// data vanilla bakes into the chunk's collision mesh: <c>shape.renderFull(..., MeshPurpose.SimplifiedCollisionOnly)</c>
	/// fills <c>VoxelMesh.m_CollVertices/m_CollIndices</c> with that block's collision triangles (rotation applied).
	///
	/// Each shape's triangle set is turned into:
	///  1. one BoxCollider piece, if the geometry is a cuboid (every vertex is a corner of its own AABB) - covers cubes,
	///     slabs, half blocks, beams, posts;
	///  2. else one convex MeshCollider piece, if the triangle set is convex (ramps, wedges, corners) - exact, no hull
	///     approximation because the geometry already IS its hull;
	///  3. else (concave: stairs, notched shapes) a union of boxes from an 8x8x8 occupancy sampling of the solid volume.
	/// Legacy shapes without a collision mesh fall back to Block.shape.GetBounds() boxes.
	/// Results are cached per (block type, rotation).
	/// </summary>
	internal static class ShapeColliderCache
	{
		private static readonly Dictionary<long, ShapePiece[]> cache = new Dictionary<long, ShapePiece[]>();
		private static readonly List<Mesh> ownedMeshes = new List<Mesh>();
		private static readonly ShapePiece[] Empty = new ShapePiece[0];
		private static int diagLogged;

		private const float Eps = 1e-3f;
		private const int R = 8;            // concave decomposition resolution per axis (0.125 m cells)
		private const int MaxConvexTris = 255; // Unity convex MeshCollider input limit

		public static int Count { get { return cache.Count; } }

		public static void Clear()
		{
			cache.Clear();
			for (int i = 0; i < ownedMeshes.Count; i++)
				if (ownedMeshes[i] != null) Object.Destroy(ownedMeshes[i]);
			ownedMeshes.Clear();
			diagLogged = 0;
		}

		public static ShapePiece[] Get(BlockValue bv)
		{
			long key = ((long)bv.type << 8) | bv.rotation;
			ShapePiece[] result;
			if (cache.TryGetValue(key, out result)) return result;
			try { result = Build(bv); }
			catch (System.Exception e)
			{
				Log.Warning("[SubLevel] Collision shape build failed for block type " + bv.type + " rot " + bv.rotation + ": " + e.GetType().Name + ": " + e.Message);
				result = Empty;
			}
			cache[key] = result;
			return result;
		}

		private static ShapePiece[] Build(BlockValue bv)
		{
			Block block = bv.Block;
			// Block-entity models (workbenches, crates, doors, ...) bring their own prefab colliders, tagged T_Block, which
			// the game resolves back to the owning block. They are NOT replaced; see PlotBlockColliders.
			if (block == null || block.shape == null || block.shape is BlockShapeModelEntity) return Empty;

			var pieces = new List<ShapePiece>();
			int meshCount = MeshDescription.meshes.Length;
			var meshes = new VoxelMesh[meshCount];
			for (int i = 0; i < meshCount; i++)
				meshes[i] = VoxelMesh.Create(i, MeshDescription.meshes[i].meshType, 1);

			block.shape.renderFull(Vector3i.zero, bv, ItemClassBlock.renderOffsetV, null, new LightingAround(0, 0, 0),
				default(TextureFullArray), meshes, BlockShape.MeshPurpose.SimplifiedCollisionOnly);

			for (int mi = 0; mi < meshCount; mi++)
			{
				VoxelMesh vm = meshes[mi];
				if (vm == null || vm.m_CollIndices == null || vm.m_CollVertices == null || vm.m_CollIndices.Count < 3) continue;
				string layerName = MeshDescription.meshes[mi].ColliderLayerName;
				if (string.IsNullOrEmpty(layerName)) continue; // vanilla creates no collider for this mesh index either

				var verts = new Vector3[vm.m_CollVertices.Count];
				System.Array.Copy(vm.m_CollVertices.Items, verts, verts.Length);
				var idx = new int[vm.m_CollIndices.Count - vm.m_CollIndices.Count % 3];
				System.Array.Copy(vm.m_CollIndices.Items, idx, idx.Length);
				Decompose(verts, idx, mi, pieces);
			}

			// Legacy shapes (not BlockShapeNew) have no collision mesh; use their declared bounds.
			if (pieces.Count == 0 && !(block.shape is BlockShapeNew) && block.IsCollideMovement)
			{
				int mi = block.MeshIndex;
				if (mi >= 0 && mi < meshCount && !string.IsNullOrEmpty(MeshDescription.meshes[mi].ColliderLayerName))
				{
					Bounds[] bounds = block.shape.GetBounds(bv);
					for (int i = 0; bounds != null && i < bounds.Length; i++)
						pieces.Add(MakeBox(mi, bounds[i].center - new Vector3(0.5f, 0.5f, 0.5f), bounds[i].size));
				}
			}

			if (diagLogged < 4 && pieces.Count > 0)
			{
				diagLogged++;
				Bounds b = new Bounds(pieces[0].Center, pieces[0].Size);
				for (int i = 1; i < pieces.Count; i++) if (pieces[i].Kind == PieceKind.Box) b.Encapsulate(new Bounds(pieces[i].Center, pieces[i].Size));
				Log.Out("[SubLevel] collision shape '" + block.GetBlockName() + "' rot " + bv.rotation + ": " + pieces.Count + " piece(s), kind0=" + pieces[0].Kind +
					", boxBounds=" + b.min + ".." + b.max + " (block-centred; a full cube must be -0.5..0.5)");
			}
			return pieces.Count == 0 ? Empty : pieces.ToArray();
		}

		private static ShapePiece MakeBox(int meshIndex, Vector3 center, Vector3 size)
		{
			size = new Vector3(Mathf.Max(size.x, 0.02f), Mathf.Max(size.y, 0.02f), Mathf.Max(size.z, 0.02f));
			return new ShapePiece
			{
				Kind = PieceKind.Box, MeshIndex = meshIndex, Center = center, Size = size,
				IsFullCube = Near(size, Vector3.one) && Near(center, Vector3.zero),
				Volume = size.x * size.y * size.z
			};
		}

		private static bool Near(Vector3 a, Vector3 b) { return (a - b).sqrMagnitude < 4e-6f; }

		// ---------------------------------------------------------------- decomposition

		private static void Decompose(Vector3[] v, int[] idx, int meshIndex, List<ShapePiece> output)
		{
			Bounds b = new Bounds(v[0], Vector3.zero);
			for (int i = 1; i < v.Length; i++) b.Encapsulate(v[i]);

			if (IsCuboid(v, b))
			{
				output.Add(MakeBox(meshIndex, b.center, b.size));
				return;
			}
			int triCount = idx.Length / 3;
			if (triCount <= MaxConvexTris && IsConvex(v, idx))
			{
				var mesh = new Mesh();
				mesh.name = "SubLevelShape";
				mesh.vertices = v;
				mesh.triangles = idx;
				mesh.RecalculateBounds();
				ownedMeshes.Add(mesh);
				output.Add(new ShapePiece
				{
					Kind = PieceKind.Convex, MeshIndex = meshIndex, Mesh = mesh, Center = b.center, Size = b.size,
					Volume = b.size.x * b.size.y * b.size.z * 0.5f
				});
				return;
			}
			DecomposeConcave(v, idx, meshIndex, output);
		}

		/// <summary>True when every vertex sits on a corner of the mesh's own AABB (an axis-aligned box, open or closed).</summary>
		private static bool IsCuboid(Vector3[] v, Bounds b)
		{
			Vector3 min = b.min, max = b.max;
			for (int i = 0; i < v.Length; i++)
			{
				if (Mathf.Abs(v[i].x - min.x) > Eps && Mathf.Abs(v[i].x - max.x) > Eps) return false;
				if (Mathf.Abs(v[i].y - min.y) > Eps && Mathf.Abs(v[i].y - max.y) > Eps) return false;
				if (Mathf.Abs(v[i].z - min.z) > Eps && Mathf.Abs(v[i].z - max.z) > Eps) return false;
			}
			return true;
		}

		/// <summary>Convex iff, for every triangle, all vertices lie on one side of that triangle's plane.</summary>
		private static bool IsConvex(Vector3[] v, int[] idx)
		{
			for (int t = 0; t + 2 < idx.Length; t += 3)
			{
				Vector3 a = v[idx[t]], b = v[idx[t + 1]], c = v[idx[t + 2]];
				Vector3 n = Vector3.Cross(b - a, c - a);
				float len = n.magnitude;
				if (len < 1e-8f) continue;
				n /= len;
				bool pos = false, neg = false;
				for (int i = 0; i < v.Length; i++)
				{
					float d = Vector3.Dot(n, v[i] - a);
					if (d > Eps) pos = true; else if (d < -Eps) neg = true;
					if (pos && neg) return false;
				}
			}
			return true;
		}

		// Concave shapes: sample the solid volume on an R^3 grid (inside = odd ray-crossing parity on at least 2 of 3 axes,
		// which tolerates small defects in the collision mesh), then merge the occupied cells into boxes.
		private static void DecomposeConcave(Vector3[] v, int[] idx, int meshIndex, List<ShapePiece> output)
		{
			int triCount = idx.Length / 3;
			var occ = new bool[R * R * R];
			bool any = false;
			for (int y = 0; y < R; y++)
				for (int z = 0; z < R; z++)
					for (int x = 0; x < R; x++)
					{
						Vector3 p = new Vector3(-0.5f + (x + 0.5f) / R, -0.5f + (y + 0.5f) / R, -0.5f + (z + 0.5f) / R);
						int votes = 0;
						for (int axis = 0; axis < 3; axis++)
							if ((CountCrossings(p, axis, v, idx, triCount) & 1) == 1) votes++;
						if (votes >= 2) { occ[(y * R + z) * R + x] = true; any = true; }
					}

			if (!any)
			{
				// Open / degenerate mesh with no measurable volume: keep its extents as one box rather than losing collision.
				Bounds b = new Bounds(v[0], Vector3.zero);
				for (int i = 1; i < v.Length; i++) b.Encapsulate(v[i]);
				output.Add(MakeBox(meshIndex, b.center, b.size));
				return;
			}

			var boxes = new List<int[]>();
			GreedyMerge(occ, R, R, R, boxes);
			float cell = 1f / R;
			foreach (int[] bx in boxes)
			{
				Vector3 min = new Vector3(-0.5f + bx[0] * cell, -0.5f + bx[1] * cell, -0.5f + bx[2] * cell);
				Vector3 size = new Vector3((bx[3] - bx[0]) * cell, (bx[4] - bx[1]) * cell, (bx[5] - bx[2]) * cell);
				output.Add(MakeBox(meshIndex, min + size * 0.5f, size));
			}
		}

		private static int CountCrossings(Vector3 p, int axis, Vector3[] v, int[] idx, int triCount)
		{
			// Fixed sub-cell jitter keeps rays off shared triangle edges (which would double count).
			int u = (axis + 1) % 3, w = (axis + 2) % 3;
			float pu = Comp(p, u) + 1.3e-4f, pw = Comp(p, w) + 0.7e-4f, pa = Comp(p, axis);
			int hits = 0;
			for (int t = 0; t < triCount; t++)
			{
				Vector3 A = v[idx[t * 3]], B = v[idx[t * 3 + 1]], C = v[idx[t * 3 + 2]];
				float au = Comp(A, u), aw = Comp(A, w);
				float bu = Comp(B, u) - au, bw = Comp(B, w) - aw;
				float cu = Comp(C, u) - au, cw = Comp(C, w) - aw;
				float det = bu * cw - bw * cu;
				if (Mathf.Abs(det) < 1e-9f) continue;   // triangle parallel to the ray
				float du = pu - au, dw = pw - aw;
				float l1 = (du * cw - dw * cu) / det;
				float l2 = (bu * dw - bw * du) / det;
				if (l1 < 0f || l2 < 0f || l1 + l2 > 1f) continue;
				float hitA = Comp(A, axis) + l1 * (Comp(B, axis) - Comp(A, axis)) + l2 * (Comp(C, axis) - Comp(A, axis));
				if (hitA > pa) hits++;
			}
			return hits;
		}

		private static float Comp(Vector3 v, int i) { return i == 0 ? v.x : (i == 1 ? v.y : v.z); }

		/// <summary>Greedy box merge over a dense occupancy grid, index = (y*sz + z)*sx + x. Consumes the grid.
		/// Emits {x0,y0,z0,x1,y1,z1} with exclusive max.</summary>
		public static void GreedyMerge(bool[] occ, int sx, int sy, int sz, List<int[]> output)
		{
			for (int y = 0; y < sy; y++)
				for (int z = 0; z < sz; z++)
					for (int x = 0; x < sx; x++)
					{
						if (!occ[(y * sz + z) * sx + x]) continue;
						int x1 = x + 1;
						while (x1 < sx && occ[(y * sz + z) * sx + x1]) x1++;
						int z1 = z + 1;
						while (z1 < sz && RowFull(occ, sx, sz, y, z1, x, x1)) z1++;
						int y1 = y + 1;
						while (y1 < sy && SlabFull(occ, sx, sz, y1, z, z1, x, x1)) y1++;
						for (int yy = y; yy < y1; yy++)
							for (int zz = z; zz < z1; zz++)
								for (int xx = x; xx < x1; xx++)
									occ[(yy * sz + zz) * sx + xx] = false;
						output.Add(new[] { x, y, z, x1, y1, z1 });
					}
		}

		private static bool RowFull(bool[] occ, int sx, int sz, int y, int z, int x0, int x1)
		{
			for (int x = x0; x < x1; x++) if (!occ[(y * sz + z) * sx + x]) return false;
			return true;
		}

		private static bool SlabFull(bool[] occ, int sx, int sz, int y, int z0, int z1, int x0, int x1)
		{
			for (int z = z0; z < z1; z++) if (!RowFull(occ, sx, sz, y, z, x0, x1)) return false;
			return true;
		}
	}
}
