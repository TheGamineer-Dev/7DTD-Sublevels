using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace SubLevelPhysics
{
	/// <summary>
	/// The sublevel's collision, built from the BLOCKS of the out-of-bounds plot (the static build this sublevel is a
	/// projection of) instead of from rendered chunk meshes or convex hulls.
	///
	///  * Source of truth: the plot chunks' block data. Every non-air block contributes the collision geometry of its own
	///    shape (see ShapeColliderCache). Nothing is derived from the visual meshes.
	///  * Full cubes are greedily merged into large BoxColliders per chunk; other shapes become exact boxes / exact convex
	///    pieces / box unions. Every collider is a child of the sublevel Rigidbody, so the same set works kinematic and dynamic.
	///  * Tag + layer of each collider come from the block's MeshDescription (Tag, ColliderLayerName), exactly as vanilla
	///    assigns them to chunk collision meshes. That keeps targeting (T_Mesh), the physics layer matrix and the player
	///    collision behaviour identical to ordinary terrain.
	///  * Block-entity models (workbench, crates, doors...) keep their own prefab colliders (tagged T_Block, resolved back to
	///    the block by GameUtils.FindMasterBlockForEntityModelBlock). They are children of the chunk GameObject, which is under
	///    the sublevel root, so they already move with it. Only non-convex ones are adapted for dynamic mode.
	///  * The vanilla chunk MeshColliders of the plot are switched off (they cannot be used by a dynamic body and would
	///    duplicate ours).
	///  * Edits: ChunkCluster.SetBlock on a plot block and OnChunkCopiedToUnity mark that chunk dirty; only dirty chunks are
	///    rebuilt, shortly afterwards.
	/// </summary>
	internal sealed class PlotBlockColliders
	{
		// Mass model: bodies are as dense as this per cubic metre of SOLID geometry (not bounding volume).
		public const float KgPerSolidCubicMetre = 100f;

		private sealed class ChunkSet
		{
			public GameObject Go;
			public int Boxes, ConvexPieces;
			public float Volume;
		}

		private readonly Plot plot;
		private readonly Transform root;
		private readonly GameObject container;
		private readonly Dictionary<long, ChunkSet> sets = new Dictionary<long, ChunkSet>();
		private readonly HashSet<long> dirty = new HashSet<long>();
		private float pendingSince = -1f;
		private bool chunkCollidersRecheck = true;
		private float nextChunkColliderCheck;
		private float nextModelFix;

		private readonly Dictionary<MeshCollider, bool> modelOriginalConvex = new Dictionary<MeshCollider, bool>();
		private readonly List<Component> modelAddedBoxes = new List<Component>();

		public int Boxes { get; private set; }
		public int ConvexPieces { get; private set; }
		public float SolidVolume { get; private set; }
		public int Rebuilds { get; private set; }
		public float LastRebuildMs { get; private set; }
		public int ChunkMeshCollidersDisabled { get; private set; }
		public int EntityModelCollidersAdapted { get { return modelOriginalConvex.Count + modelAddedBoxes.Count; } }
		public int PieceCount { get { return Boxes + ConvexPieces; } }

		public PlotBlockColliders(Plot plot, Transform root)
		{
			this.plot = plot;
			this.root = root;
			container = new GameObject("SubLevelBlockColliders");
			container.transform.SetParent(root, false);
			container.transform.localPosition = Vector3.zero;
			container.transform.localRotation = Quaternion.identity;
		}

		public float ComputeMass() { return Mathf.Max(100f, SolidVolume * KgPerSolidCubicMetre); }

		public void MarkDirty(long chunkKey)
		{
			lock (dirty) dirty.Add(chunkKey);
		}

		public void MarkAllDirty()
		{
			lock (dirty) foreach (long k in plot.Keys) dirty.Add(k);
		}

		/// <summary>Called from the main thread every frame. Returns true when a rebuild happened.</summary>
		public bool Tick(bool dynamicPhysics, Rigidbody rb)
		{
			bool rebuilt = false;
			int pending;
			lock (dirty) pending = dirty.Count;
			if (pending > 0)
			{
				if (pendingSince < 0f) pendingSince = Time.time;
				else if (Time.time - pendingSince >= 0.05f) { RebuildDirty(); rebuilt = true; }
			}
			else pendingSince = -1f;

			if (rebuilt)
			{
				chunkCollidersRecheck = true;
				if (dynamicPhysics && rb != null) rb.mass = ComputeMass();
			}
			if (chunkCollidersRecheck || Time.realtimeSinceStartup >= nextChunkColliderCheck)
			{
				chunkCollidersRecheck = false;
				nextChunkColliderCheck = Time.realtimeSinceStartup + 1f;
				DisableVanillaChunkColliders();
			}
			if (dynamicPhysics && (rebuilt || Time.realtimeSinceStartup >= nextModelFix))
			{
				nextModelFix = Time.realtimeSinceStartup + 1f;
				AdaptEntityModelColliders();
			}
			return rebuilt;
		}

		public void NotifyChunkCopied(long key)
		{
			MarkDirty(key);
			chunkCollidersRecheck = true;
		}

		/// <summary>Rebuild everything that is dirty right now (used before switching to dynamic physics).</summary>
		public void Flush()
		{
			RebuildDirty();
			DisableVanillaChunkColliders();
		}

		// ------------------------------------------------------------------ build

		private void RebuildDirty()
		{
			List<long> work;
			lock (dirty) { work = new List<long>(dirty); dirty.Clear(); }
			pendingSince = -1f;
			if (work.Count == 0) return;

			var sw = Stopwatch.StartNew();
			ChunkCluster cc = GameManager.Instance != null && GameManager.Instance.World != null ? GameManager.Instance.World.ChunkCache : null;
			if (cc == null) return;
			foreach (long key in work)
			{
				if (!plot.IsPlotKey(key)) continue;
				RebuildChunk(key, cc.GetChunkSync(key));
			}
			Boxes = 0; ConvexPieces = 0; SolidVolume = 0f;
			foreach (ChunkSet s in sets.Values) { Boxes += s.Boxes; ConvexPieces += s.ConvexPieces; SolidVolume += s.Volume; }
			Rebuilds++;
			LastRebuildMs = (float)sw.Elapsed.TotalMilliseconds;
			Log.Out("[SubLevel] block colliders rebuilt for " + work.Count + " chunk(s) in " + LastRebuildMs.ToString("F1") + " ms: " +
				Boxes + " boxes, " + ConvexPieces + " convex pieces, solid volume " + SolidVolume.ToString("F1") + " m3, shape cache " + ShapeColliderCache.Count);
			Physics.SyncTransforms();
		}

		private void RebuildChunk(long key, Chunk c)
		{
			ChunkSet old;
			if (sets.TryGetValue(key, out old))
			{
				// SetActive(false) removes the colliders from the physics scene immediately; Destroy only runs at end of frame.
				if (old.Go != null) { old.Go.SetActive(false); Object.Destroy(old.Go); }
				sets.Remove(key);
			}
			if (c == null) return;

			var set = new ChunkSet();
			set.Go = new GameObject("C_" + c.X + "_" + c.Z);
			set.Go.transform.SetParent(container.transform, false);
			set.Go.transform.localPosition = Vector3.zero;
			set.Go.transform.localRotation = Quaternion.identity;

			// Local (root-space) position of the minimum corner of block (x,y,z) of this chunk.
			float ox = c.X * 16 - plot.Pivot.x, oz = c.Z * 16 - plot.Pivot.z, oy = -plot.Pivot.y;

			var holders = new Dictionary<int, GameObject>();
			var cubeCells = new Dictionary<int, List<Vector3Int>>();
			int minY = int.MaxValue, maxY = int.MinValue;

			for (int ly = 0; ly < c.m_BlockLayers.Length; ly++)
			{
				if (c.m_BlockLayers[ly] == null) continue;
				for (int y = ly * 4; y < ly * 4 + 4; y++)
					for (int z = 0; z < 16; z++)
						for (int x = 0; x < 16; x++)
						{
							BlockValue bv = c.GetBlockNoDamage(x, y, z);
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
								Vector3 cellCentre = new Vector3(ox + x + 0.5f, oy + y + 0.5f, oz + z + 0.5f);
								GameObject holder = Holder(set, holders, piece.MeshIndex);
								if (piece.Kind == PieceKind.Box)
								{
									BoxCollider bc = holder.AddComponent<BoxCollider>();
									bc.center = cellCentre + piece.Center;
									bc.size = piece.Size;
									set.Boxes++;
								}
								else
								{
									var go = new GameObject("S");
									go.tag = holder.tag;
									go.layer = holder.layer;
									go.transform.SetParent(holder.transform, false);
									go.transform.localPosition = cellCentre;
									MeshCollider mc = go.AddComponent<MeshCollider>();
									mc.sharedMesh = piece.Mesh;
									mc.convex = true;
									set.ConvexPieces++;
								}
								set.Volume += piece.Volume;
							}
						}
			}

			// Greedy-merge the full cubes of each mesh group into as few boxes as possible.
			foreach (KeyValuePair<int, List<Vector3Int>> group in cubeCells)
			{
				int sy = maxY - minY + 1;
				var occ = new bool[16 * sy * 16];
				foreach (Vector3Int cell in group.Value) occ[((cell.y - minY) * 16 + cell.z) * 16 + cell.x] = true;
				var boxes = new List<int[]>();
				ShapeColliderCache.GreedyMerge(occ, 16, sy, 16, boxes);
				GameObject holder = Holder(set, holders, group.Key);
				foreach (int[] b in boxes)
				{
					Vector3 size = new Vector3(b[3] - b[0], b[4] - b[1], b[5] - b[2]);
					BoxCollider bc = holder.AddComponent<BoxCollider>();
					bc.center = new Vector3(ox + b[0], oy + minY + b[1], oz + b[2]) + size * 0.5f;
					bc.size = size;
					set.Boxes++;
					set.Volume += size.x * size.y * size.z;
				}
			}
			sets[key] = set;
		}

		private GameObject Holder(ChunkSet set, Dictionary<int, GameObject> holders, int meshIndex)
		{
			GameObject h;
			if (holders.TryGetValue(meshIndex, out h)) return h;
			MeshDescription md = MeshDescription.meshes[meshIndex];
			h = new GameObject("mesh" + meshIndex + "_" + md.Tag);
			if (!string.IsNullOrEmpty(md.Tag)) h.tag = md.Tag;
			h.layer = ChunkCluster.LayerMappingTable[md.ColliderLayerName];
			h.transform.SetParent(set.Go.transform, false);
			h.transform.localPosition = Vector3.zero;
			h.transform.localRotation = Quaternion.identity;
			holders[meshIndex] = h;
			return h;
		}

		// ------------------------------------------------------------------ vanilla chunk colliders

		/// <summary>The chunk MeshColliders are replaced by the block colliders above; leave them enabled and every ray / body
		/// would hit two copies, and a dynamic body cannot use them at all.</summary>
		private void DisableVanillaChunkColliders()
		{
			ChunkCluster cc = GameManager.Instance != null && GameManager.Instance.World != null ? GameManager.Instance.World.ChunkCache : null;
			if (cc == null) return;
			int off = 0;
			foreach (long key in plot.Keys)
			{
				ChunkGameObject cgo;
				if (!cc.DisplayedChunkGameObjects.TryGetValue(key, out cgo) || cgo == null) continue;
				for (int i = 0; i < cgo.layers.Length; i++)
				{
					ChunkGameObjectLayer l = cgo.layers[i];
					if (l == null || l.m_MeshCollider == null) continue;
					for (int k = 0; k < l.m_MeshCollider.Length; k++)
					{
						MeshCollider mc = l.m_MeshCollider[k];
						if (mc == null) continue;
						if (mc.enabled) mc.enabled = false;
						off++;
					}
				}
			}
			ChunkMeshCollidersDisabled = off;
		}

		// ------------------------------------------------------------------ block-entity model colliders

		/// <summary>Unity rejects non-convex MeshColliders on a dynamic body. Block-entity prefabs are normally Box/Capsule/convex,
		/// but if one is a concave MeshCollider, give it a convex hull (small per-model approximation) or, when it is too big for a
		/// convex mesh, its bounding box. Undone when dynamic mode ends.</summary>
		private void AdaptEntityModelColliders()
		{
			ChunkCluster cc = GameManager.Instance != null && GameManager.Instance.World != null ? GameManager.Instance.World.ChunkCache : null;
			if (cc == null) return;
			foreach (long key in plot.Keys)
			{
				ChunkGameObject cgo;
				if (!cc.DisplayedChunkGameObjects.TryGetValue(key, out cgo) || cgo == null || cgo.blockEntitiesParentT == null) continue;
				foreach (MeshCollider mc in cgo.blockEntitiesParentT.GetComponentsInChildren<MeshCollider>(true))
				{
					if (mc == null || mc.convex || mc.isTrigger || modelOriginalConvex.ContainsKey(mc)) continue;
					Mesh m = mc.sharedMesh;
					if (m == null) continue;
					int tris = 0;
					for (int s = 0; s < m.subMeshCount; s++) tris += (int)m.GetIndexCount(s) / 3;
					if (tris <= 255)
					{
						modelOriginalConvex[mc] = false;
						mc.convex = true;
					}
					else
					{
						modelOriginalConvex[mc] = false;
						mc.enabled = false;
						BoxCollider bc = mc.gameObject.AddComponent<BoxCollider>();
						bc.center = m.bounds.center;
						bc.size = m.bounds.size;
						modelAddedBoxes.Add(bc);
					}
				}
			}
		}

		public void RestoreEntityModelColliders()
		{
			foreach (KeyValuePair<MeshCollider, bool> e in modelOriginalConvex)
			{
				if (e.Key == null) continue;
				e.Key.enabled = true;
				try { e.Key.convex = e.Value; } catch (System.Exception) { }
			}
			modelOriginalConvex.Clear();
			foreach (Component c in modelAddedBoxes) if (c != null) Object.Destroy(c);
			modelAddedBoxes.Clear();
		}

		// ------------------------------------------------------------------ lifetime / info

		public void Dispose()
		{
			RestoreEntityModelColliders();
			if (container != null) { container.SetActive(false); Object.Destroy(container); }
			sets.Clear();
			lock (dirty) dirty.Clear();
		}

		public string Describe()
		{
			var sb = new StringBuilder();
			sb.Append("block colliders: " + Boxes + " boxes + " + ConvexPieces + " convex pieces in " + sets.Count + " chunk set(s); solid volume " +
				SolidVolume.ToString("F1") + " m3 (mass " + ComputeMass().ToString("F0") + " kg at " + KgPerSolidCubicMetre + " kg/m3); rebuilds=" + Rebuilds +
				" last=" + LastRebuildMs.ToString("F1") + "ms; vanilla chunk MeshColliders switched off=" + ChunkMeshCollidersDisabled +
				"; entity-model colliders adapted for dynamic=" + EntityModelCollidersAdapted + "; shape cache=" + ShapeColliderCache.Count);
			return sb.ToString();
		}
	}
}
