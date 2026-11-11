using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Xml;
using UnityEngine;

namespace SubLevelPhysics
{
	/// <summary>Phase 0.1 spike: one dynamically sized square plot inside the real chunk cluster (Option A),
	/// staged with two support rings around its visible footprint.
	///
	/// WHY the two support rings (verified against the decompile):
	///  - ChunkManager.task_Lighting lights a chunk only if WorldChunkCache.GetNeighborChunks() finds all 8 neighbours
	///    and they are all decorated (Chunk.IsNeighbourChunksDecorated).
	///  - ChunkManager.RegenerateNextChunk meshes a chunk only if all 8 neighbours exist AND are lit
	///    (Chunk.IsNeighbourChunksLit).
	///  - ChunkManager.checkChunkNeedsCopying only hands a chunk to Unity once it is not NeedsLightCalculation / NeedsRegeneration.
	///  So a visible N x N footprint needs an N+2 x N+2 lit region and an N+4 x N+4 staged region.
	///  The outer two rings are infrastructure only; they are not displayed or attached to the Rigidbody.
	///
	/// All plot access stays behind this class so Option B could replace it later.</summary>
	public sealed class Plot
	{
		public int PlotRadius = 1;                       // derived from requested odd chunk width/depth
		public int StageRadius { get { return PlotRadius + 2; } } // two support rings for lighting/regeneration

		public string Id;
		public int Cx, Cz;                 // centre chunk
		public Vector3i Pivot;             // plot-space block the pose is applied around
		public readonly List<long> Keys = new List<long>();        // dynamically sized visible footprint
		public readonly List<long> SupportKeys = new List<long>(); // two outer rings: vanilla lighting/meshing infrastructure only
		public ChunkManager.ChunkObserver ViewObserver;    // visual meshes ON -> visible footprint
		public ChunkManager.ChunkObserver SupportObserver; // visual meshes OFF -> expanded staging footprint
		public GameObject RootGo;
		public Rigidbody Rb;
		public PlotDriver Driver;
		public System.Action<Chunk> OnInit, OnRegen, OnCopied;
		public float CreatedAt;
		/// <summary>Client copy of a server sublevel (multiplayer): no chunks or save of its own, pose from the server.</summary>
		public bool Remote;
		/// <summary>Server: one chunk observer per connected player, so vanilla streams this plot's chunks to them.</summary>
		public readonly Dictionary<int, ChunkManager.ChunkObserver> ClientObservers = new Dictionary<int, ChunkManager.ChunkObserver>();

		public Vector3 PivotV { get { return new Vector3(Pivot.x, Pivot.y, Pivot.z); } }

		/// <summary>True if block column (x,z) belongs to the visible, editable plot footprint.
		/// The two outer staging rings are lighting/meshing infrastructure only and must never
		/// receive block interactions or have their placement bounds overridden.</summary>
		public bool ContainsXZ(int x, int z)
		{
			int cx = x >> 4, cz = z >> 4;
			return System.Math.Abs(cx - Cx) <= PlotRadius && System.Math.Abs(cz - Cz) <= PlotRadius;
		}

		// Plot space = the real block coordinates the plot chunks live at (e.g. chunk 4000,4000 => x~64000).
		// "Unity" below = Unity scene coordinates, i.e. true world minus Origin.position.
		// Physics pose = the Rigidbody's simulated pose (what raycasts hit). Render pose = the interpolated transform (what is drawn).
		public Vector3 UnityToPlotPhysics(Vector3 unity) { return Quaternion.Inverse(Rb.rotation) * (unity - Rb.position) + PivotV; }
		public Vector3 PlotToUnityRender(Vector3 plot) { return RootGo.transform.TransformPoint(plot - PivotV); }
		/// <summary>Plot-space position -> true world coordinates of where it is currently drawn (what distance checks should use).</summary>
		public Vector3 PlotToWorldRender(Vector3 plot) { return PlotToUnityRender(plot) + Origin.position; }

		public bool IsPlotKey(long key) { return Keys.Contains(key); }
		public bool IsStageKey(long key) { return Keys.Contains(key) || SupportKeys.Contains(key); }
	}

	public static class PlotSpace
	{
		private static readonly List<Plot> plots = new List<Plot>();
		/// <summary>Immutable copy of <see cref="plots"/>, replaced (never mutated) on every change. The Find* lookups run on
		/// mesh threads (BlockShapeNew.renderFace) and per frame, so they read this instead of the List.</summary>
		private static volatile Plot[] snapshot = new Plot[0];
		private static void Publish() { snapshot = plots.ToArray(); }
		/// <summary>True when the saved file could not be read; the file is then never overwritten or deleted this session.</summary>
		private static bool metadataUnreadable;
		public static Plot Current { get; private set; }
		private static World loadedWorld;
		private static bool metadataLoaded;
		private static string MetadataPath { get { return GameIO.GetSaveGameDir() + "/sublevels.xml"; } }
		public static IList<Plot> All { get { return plots.AsReadOnly(); } }
		public static int Count { get { return plots.Count; } }

		public static Plot FindById(string id)
		{
			if (string.IsNullOrEmpty(id)) return null;
			return plots.Find(x => string.Equals(x.Id, id, System.StringComparison.OrdinalIgnoreCase));
		}
		public static Plot FindByChunk(int x, int z)
		{
			int cx = x >> 4, cz = z >> 4;
			Plot[] s = snapshot;
			for (int i = 0; i < s.Length; i++)
			{
				Plot xp = s[i];
				if (System.Math.Abs(cx - xp.Cx) <= xp.PlotRadius && System.Math.Abs(cz - xp.Cz) <= xp.PlotRadius) return xp;
			}
			return null;
		}
		public static Plot FindByTransform(Transform t)
		{
			Plot[] s = snapshot;
			if (t == null || s.Length == 0) return null;
			for (int i = 0; i < s.Length; i++)
				if (s[i].RootGo != null && t.IsChildOf(s[i].RootGo.transform)) return s[i];
			return null;
		}
		public static bool Select(string id)
		{
			Plot p = FindById(id); if (p == null) return false; Current = p; return true;
		}

		/// <summary>Create a sublevel in the first unused staged chunk area. The search is deterministic,
		/// and checks the complete visible + support footprint against the live chunk cluster and every
		/// registered sublevel before allocating anything.</summary>
		public static string CreateAuto(string blockName, int sizeChunks = 3)
		{
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			ChunkCluster cc = world != null ? world.ChunkCache : null;
			if (cc == null) return "No world/chunk cluster (are you in a game?).";
			if (sizeChunks < 1 || (sizeChunks & 1) == 0) return "Sublevel footprint must be a positive odd number of chunks.";
			int spacing = sizeChunks + 6; // footprint plus two support rings and a safety chunk
			const int baseX = 4000, baseZ = 4000;
			// Search outward in square rings. Chunk coordinates are deliberately kept in the
			// mod's reserved high-coordinate area, away from normal player exploration.
			for (int ring = 0; ring < 2048; ring++)
			{
				if (ring == 0)
				{
					if (CanAllocate(cc, baseX, baseZ, sizeChunks)) return CreateInternal(baseX, baseZ, blockName, sizeChunks, null, false, true);
					continue;
				}
				for (int dx = -ring; dx <= ring; dx++)
				{
					int x1 = baseX + dx * spacing;
					int z1 = baseZ - ring * spacing;
					if (CanAllocate(cc, x1, z1, sizeChunks)) return CreateInternal(x1, z1, blockName, sizeChunks, null, false, true);
					int z2 = baseZ + ring * spacing;
					if (CanAllocate(cc, x1, z2, sizeChunks)) return CreateInternal(x1, z2, blockName, sizeChunks, null, false, true);
				}
				for (int dz = -ring + 1; dz < ring; dz++)
				{
					int z = baseZ + dz * spacing;
					int x1 = baseX - ring * spacing, x2 = baseX + ring * spacing;
					if (CanAllocate(cc, x1, z, sizeChunks)) return CreateInternal(x1, z, blockName, sizeChunks, null, false, true);
					if (CanAllocate(cc, x2, z, sizeChunks)) return CreateInternal(x2, z, blockName, sizeChunks, null, false, true);
				}
			}
			return "Could not find an unused sublevel chunk area. Try a larger world or remove unused chunks.";
		}

		private static bool CanAllocate(ChunkCluster cc, int cx, int cz, int sizeChunks)
		{
			int radius = (sizeChunks - 1) / 2;
			int stage = radius + 2;
			foreach (Plot p in plots)
				if (System.Math.Abs(cx - p.Cx) <= stage + p.StageRadius && System.Math.Abs(cz - p.Cz) <= stage + p.StageRadius) return false;
			for (int dx = -stage; dx <= stage; dx++)
				for (int dz = -stage; dz <= stage; dz++)
				{
					long key = WorldChunkCache.MakeChunkKey(cx + dx, cz + dz);
					if (cc.ContainsChunkSync(key) || OnDisk(cc, key)) return false;
				}
			return RefuseArea(GameManager.Instance.World, cx, cz, stage) == null;
		}

		private static RegionFileManager Regions(ChunkCluster cc)
		{
			return cc != null ? (cc.ChunkProvider as ChunkProviderGenerateWorld)?.m_RegionFileManager : null;
		}

		/// <summary>True if the chunk is saved in the world's region files (it may not be loaded right now).</summary>
		private static bool OnDisk(ChunkCluster cc, long key)
		{
			RegionFileManager rfm = Regions(cc);
			return rfm != null && rfm.ContainsChunkSync(key);
		}

		/// <summary>Sublevel chunks must stay well outside the playable map: a plot there would replace real terrain.</summary>
		private static string RefuseArea(World world, int cx, int cz, int stageRadius)
		{
			if (world == null || !world.GetWorldExtent(out Vector3i min, out Vector3i max)) return null;
			const int margin = 512; // blocks beyond the map edge that stay off limits
			int x0 = (cx - stageRadius) * 16, x1 = (cx + stageRadius + 1) * 16, z0 = (cz - stageRadius) * 16, z1 = (cz + stageRadius + 1) * 16;
			bool overlaps = x1 > min.x - margin && x0 < max.x + margin && z1 > min.z - margin && z0 < max.z + margin;
			return overlaps ? "Chunk area " + cx + "," + cz + " is inside the map or within " + margin + " blocks of its edge (map " + min.x + ".." + max.x + " x " + min.z + ".." + max.z + "); sublevels must live outside it." : null;
		}

		public static string Create(int cx, int cz, string blockName, int sizeChunks = 3)
		{
			return CreateInternal(cx, cz, blockName, sizeChunks, null, false, true);
		}

		private static string CreateInternal(int cx, int cz, string blockName, int sizeChunks, string requestedId, bool restoring, bool poseInFront)
		{
			
			World world = GameManager.Instance?.World;
			ChunkCluster cc = world?.ChunkCache;
			if (cc == null) return "No world/chunk cluster (are you in a game?).";
			Block block = Block.GetBlockByName(blockName, true) ?? Block.GetBlockByName(blockName + ":cube", true);
			if (block == null) return "Unknown block '" + blockName + "' (shape blocks are named like cobblestoneShapes:cube).";
			if (sizeChunks < 1 || (sizeChunks & 1) == 0)
				return "Sublevel footprint must be a positive odd number of chunks (for example 1, 3, 5, 7).";
			BlockValue bv = block.ToBlockValue();

			int radius = (sizeChunks - 1) / 2;
			if (!restoring)
			{
				string refuse = RefuseArea(world, cx, cz, radius + 2);
				if (refuse != null) return refuse;
			}
			var plot = new Plot { Id = string.IsNullOrEmpty(requestedId) ? System.Guid.NewGuid().ToString("N").Substring(0, 8) : requestedId, Cx = cx, Cz = cz, PlotRadius = radius,
				Pivot = new Vector3i(cx * 16 + 7, 100, cz * 16 + 7) };
			for (int dx = -plot.StageRadius; dx <= plot.StageRadius; dx++)
				for (int dz = -plot.StageRadius; dz <= plot.StageRadius; dz++)
				{
					long key = WorldChunkCache.MakeChunkKey(cx + dx, cz + dz);
					if (!restoring && (cc.ContainsChunkSync(key) || OnDisk(cc, key))) return "Chunk " + (cx + dx) + "," + (cz + dz) + " already exists; choose another spot.";
					bool inner = System.Math.Abs(dx) <= plot.PlotRadius && System.Math.Abs(dz) <= plot.PlotRadius;
					(inner ? plot.Keys : plot.SupportKeys).Add(key);
				}

			// Create every chunk ourselves BEFORE adding the observers, so the terrain provider never generates them.
			// The observers are added after all 49 chunks are in the cache, in this same frame, so the generator thread
			// can never see a half-built set.
			foreach (long key in plot.Keys) AddStageChunk(cc, key, plot, bv, false);
			foreach (long key in plot.SupportKeys) AddStageChunk(cc, key, plot, bv, true);

			Vector3 obsPos = new Vector3(cx * 16 + 8, 0f, cz * 16 + 8);
			// Support observer includes the visible footprint plus two support rings. No visual meshes.
			plot.SupportObserver = world.m_ChunkManager.AddChunkObserver(obsPos, false, plot.StageRadius - 1, -1);
			// View observer covers only the requested visible footprint; these chunks are meshed and collided.
			plot.ViewObserver = world.m_ChunkManager.AddChunkObserver(obsPos, true, plot.PlotRadius - 1, -1);

			AttachRuntime(world, plot);

			plots.Add(plot);
			Publish();
			Current = plot;
			Log.Out("[SubLevel] Plot " + plot.Id + " created: centre chunk " + cx + "," + cz + ", footprint=" + sizeChunks + "x" + sizeChunks +
				" chunks, " + plot.Keys.Count + " plot chunks + " + plot.SupportKeys.Count + " support chunks, pivot " + plot.Pivot + ", clusterFixedSize=" + cc.IsFixedSize);

			// Put it somewhere visible straight away (otherwise it sits at world origin).
			EntityPlayer pl = Caller != null ? Caller : world.GetPrimaryPlayer();
			string where = " Run 'sl here' to place it.";
			if (poseInFront && pl != null)
			{
				Vector3 truePos = PosInFront(pl);
				plot.Driver.SetPose(truePos, 0f);
				where = " Posed at " + truePos + ".";
			}
			if (!restoring) SaveMetadata();
			return "Sublevel " + plot.Id + " created at chunk " + cx + "," + cz + " with a " + sizeChunks + "x" + sizeChunks +
				" chunk footprint (pivot " + plot.Pivot + ")." + where + " Chunks need a second or two to light and mesh; run 'sl status' and check [SubLevel] lines in output_log.txt.";
		}

		/// <summary>Root object, kinematic body, driver, block colliders and the chunk-pipeline hooks (server plots and client copies).</summary>
		private static void AttachRuntime(World world, Plot plot)
		{
			plot.RootGo = new GameObject("SubLevelRoot");
			// Register the entire physics root as a floating-origin object. Level -1 moves
			// the root transform itself (including its Rigidbody and all chunk children).
			// This keeps it in the same reposition transaction as the terrain and avoids
			// a one-FixedUpdate window where the world shifts but the sublevel does not.
			Origin.Add(plot.RootGo.transform, -1);
			var rb = plot.RootGo.AddComponent<Rigidbody>();
			rb.isKinematic = true;
			rb.useGravity = false;
			rb.interpolation = RigidbodyInterpolation.Interpolate;
			plot.Rb = rb;
			plot.Driver = plot.RootGo.AddComponent<PlotDriver>();
			plot.Driver.Plot = plot;
			// Collision is built from the plot's BLOCKS (see PlotBlockColliders), starting with the blocks already placed above.
			plot.Driver.InitColliders();
			if (!plot.Remote) plot.RootGo.AddComponent<PhysicsStaffController>();
			plot.CreatedAt = Time.realtimeSinceStartup;

			// Pipeline transition logging (these callbacks fire on the lighting / regen / main threads).
			plot.OnInit = c => { if (plot.IsStageKey(c.Key)) Log.Out("[SubLevel] lit " + c.X + "," + c.Z + " (plot=" + plot.IsPlotKey(c.Key) + ")"); };
			plot.OnRegen = c => { if (plot.IsPlotKey(c.Key)) Log.Out("[SubLevel] regenerated " + c.X + "," + c.Z + " hasMesh=" + c.HasMeshLayer()); };
			plot.OnCopied = c =>
			{
				if (!plot.IsPlotKey(c.Key)) return;
				Log.Out("[SubLevel] copied to Unity " + c.X + "," + c.Z + " collisionMesh=" + c.IsCollisionMeshGenerated);
				if (plot.Driver != null) plot.Driver.MarkChunkCollisionDirty(c.Key);
			};
			world.m_ChunkManager.OnChunkInitialized += plot.OnInit;
			world.m_ChunkManager.OnChunkRegenerated += plot.OnRegen;
			world.m_ChunkManager.OnChunkCopiedToUnity += plot.OnCopied;
		}

		/// <summary>The player a console command came from (a client on a dedicated server), else null: then the host's player.</summary>
		internal static EntityPlayer Caller;

		/// <summary>Client: the server's sublevel packets are applied only once this world is set up here (OnGameUpdate).</summary>
		internal static bool ClientReady(World w) { return w != null && loadedWorld == w && metadataLoaded; }

		/// <summary>Client: a local copy of a server sublevel. The chunks come from the server; this only draws, collides and follows the pose.</summary>
		internal static Plot CreateRemote(string id, int cx, int cz, int radius)
		{
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			if (world == null || world.ChunkCache == null) return null;
			var plot = new Plot { Id = id, Cx = cx, Cz = cz, PlotRadius = radius, Pivot = new Vector3i(cx * 16 + 7, 100, cz * 16 + 7), Remote = true };
			for (int dx = -plot.StageRadius; dx <= plot.StageRadius; dx++)
				for (int dz = -plot.StageRadius; dz <= plot.StageRadius; dz++)
				{
					bool inner = System.Math.Abs(dx) <= plot.PlotRadius && System.Math.Abs(dz) <= plot.PlotRadius;
					(inner ? plot.Keys : plot.SupportKeys).Add(WorldChunkCache.MakeChunkKey(cx + dx, cz + dz));
				}
			// The client's mesher only draws chunks inside a local observer with visual meshes on (ChunkManager.DetermineChunksToLoad);
			// on a client an observer never loads or unloads anything, the server does that.
			plot.ViewObserver = world.m_ChunkManager.AddChunkObserver(new Vector3(cx * 16 + 8, 0f, cz * 16 + 8), true, plot.PlotRadius - 1, -1);
			AttachRuntime(world, plot);
			plots.Add(plot);
			Publish();
			Log.Out("[SubLevel] MP: client copy of sublevel " + id + " at chunk " + cx + "," + cz + " (radius " + radius + ")");
			return plot;
		}

		internal static void DestroyRemote(Plot p)
		{
			plots.Remove(p);
			Publish();
			if (Current == p) Current = null;
			if (p.Driver != null) p.Driver.DetachAll();
			if (p.RootGo != null) { Origin.Remove(p.RootGo.transform); Object.Destroy(p.RootGo); }
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			if (world != null)
			{
				ChunkManager cm = world.m_ChunkManager;
				cm.OnChunkInitialized -= p.OnInit;
				cm.OnChunkRegenerated -= p.OnRegen;
				cm.OnChunkCopiedToUnity -= p.OnCopied;
				if (p.ViewObserver != null) cm.RemoveChunkObserver(p.ViewObserver);
			}
			Log.Out("[SubLevel] MP: client copy of sublevel " + p.Id + " removed (the server no longer lists it)");
		}

		/// <summary>True-world position 6 m in front of the player, 1 m above their feet.</summary>
		public static Vector3 PosInFront(EntityPlayer pl)
		{
			Vector3 fwd = pl.transform.forward; fwd.y = 0f;
			if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
			fwd.Normalize();
			return pl.position + fwd * 6f + Vector3.up * 1f; // Entity.position is true world coords
		}

		private static void AddStageChunk(ChunkCluster cc, long key, Plot plot, BlockValue bv, bool support)
		{
			Chunk existing = cc.GetChunkSync(key);
			if (existing != null) return; // already loaded: retain blocks, damage, tile entities and inventories
			// Saved sublevel chunk on disk (a reload): leave it to the chunk provider, which loads it from the region files on
			// its own thread once the observers ask for it. Building a blank chunk here would hide the saved one (the next
			// save overwrites it), and reading the region files from this (main) thread races the provider's reader.
			if (OnDisk(cc, key))
			{
				Log.Out("[SubLevel] chunk " + WorldChunkCache.extractX(key) + "," + WorldChunkCache.extractZ(key) + " of sublevel " + plot.Id + " is saved: the chunk provider loads it.");
				return;
			}
			Chunk c = MemoryPools.PoolChunks.AllocSync(true);
			c.X = WorldChunkCache.extractX(key);
			c.Z = WorldChunkCache.extractZ(key);
			c.NeedsDecoration = false;      // lighting of a neighbour requires IsNeighbourChunksDecorated
			c.NeedsLightDecoration = false;
			c.ResetStability();
			c.RefreshSunlight();
			if (c.X == plot.Cx && c.Z == plot.Cz && !string.IsNullOrEmpty(bv.Block.GetBlockName())) FillTestCube(c, bv);
			if (support && IsOuterRing(c, plot))
			{
				// The outermost ring can never be lit (its own neighbours would have to exist too). It only has to exist,
				// be decorated and count as "lit" so the ring inside it can be lit. Marking it lit stops the lighting thread
				// retrying it forever. NeedsRegeneration stays true on purpose: checkChunkNeedsCopying treats an empty chunk
				// with no pending work as ready to display, which would give this ring stray ChunkGameObjects.
				c.NeedsLightCalculation = false;
				c.NeedsRegeneration = true;
			}
			else
			{
				c.NeedsLightCalculation = true;
				c.NeedsRegeneration = true;
			}
			c.isModified = !support; // visible sublevel chunks are ordinary persistent world chunks
			cc.AddChunkSync(c);
		}

		private static bool IsOuterRing(Chunk c, Plot p)
		{
			return System.Math.Max(System.Math.Abs(c.X - p.Cx), System.Math.Abs(c.Z - p.Cz)) == p.StageRadius;
		}

		// 9-block floor + 1 pillar = 10 blocks, in the centre chunk.
		private static void FillTestCube(Chunk c, BlockValue bv)
		{
			for (int x = 6; x <= 8; x++)
				for (int z = 6; z <= 8; z++)
					c.SetBlockRaw(x, 100, z, bv);
			c.SetBlockRaw(7, 101, 7, bv);
		}

		/// <summary>Dev helper: set a block at an offset from the plot pivot (centre chunk, y=100; the test floor is at dy=0).
		/// Goes through World.SetBlock so tile entities (workbench, crate) and block-entity models are created normally.</summary>
		public static string SetBlockAt(string blockName, int dx, int dy, int dz, int rotation)
		{
			return SetBlockAt(Current != null ? Current.Id : null, blockName, dx, dy, dz, rotation);
		}

		public static string SetBlockAt(string id, string blockName, int dx, int dy, int dz, int rotation)
		{
			Plot p = FindById(id);
			if (p == null) return "No plot.";
			World world = GameManager.Instance?.World;
			if (world == null) return "No world.";
			Block block = Block.GetBlockByName(blockName, true) ?? Block.GetBlockByName(blockName + ":cube", true);
			if (block == null) return "Unknown block '" + blockName + "'.";
			Vector3i pos = new Vector3i(p.Pivot.x + dx, p.Pivot.y + dy, p.Pivot.z + dz);
			if (!p.ContainsXZ(pos.x, pos.z)) return "Position " + pos + " is outside the visible plot footprint.";
			BlockValue bv = block.ToBlockValue();
			bv.rotation = (byte)rotation;
			world.SetBlock(pos, bv, true, true);
			Chunk changed = world.ChunkCache.GetChunkSync(pos.x >> 4, pos.z >> 4); if (changed != null) changed.isModified = true;
			SaveMetadata();
			return "Set " + block.GetBlockName() + " rot " + rotation + " at plot block " + pos + ".";
		}

		public static string Destroy()
		{
			Plot p = Current;
			if (p == null) return "No plot.";
			plots.Remove(p);
			Publish();
			Current = plots.Count > 0 ? plots[plots.Count - 1] : null;
			SaveMetadata();
			World world = GameManager.Instance?.World;
			if (p.Driver != null) p.Driver.DetachAll();
			if (plots.Count == 0) ShapeColliderCache.Clear();
			if (p.RootGo != null)
			{
				Origin.Remove(p.RootGo.transform);
				Object.Destroy(p.RootGo);
			}
			if (world == null) return "Plot removed (no world).";

			ChunkManager cm = world.m_ChunkManager;
			cm.OnChunkInitialized -= p.OnInit;
			cm.OnChunkRegenerated -= p.OnRegen;
			cm.OnChunkCopiedToUnity -= p.OnCopied;
			if (p.ViewObserver != null) cm.RemoveChunkObserver(p.ViewObserver);
			if (p.SupportObserver != null) cm.RemoveChunkObserver(p.SupportObserver);
			foreach (ChunkManager.ChunkObserver o in p.ClientObservers.Values) cm.RemoveChunkObserver(o);
			p.ClientObservers.Clear();

			ChunkCluster cc = world.ChunkCache;
			if (cc != null)
			{
				var all = new List<long>(p.Keys);
				all.AddRange(p.SupportKeys);
				// Keep the removed sublevel chunks saved as ordinary world data; they can be reused by a later sublevel.
				// A fixed-size cluster never unloads chunks that fall out of observer range (DetermineChunksToLoad only
				// does that for !IsFixedSize), so remove them explicitly. On infinite-terrain clusters the vanilla observer
				// path does it; doing both would unload twice.
				if (cc.IsFixedSize)
				{
					foreach (long key in all)
						if (cc.GetChunkSync(key) != null) cm.RemoveChunk(key);
				}
			}
			Log.Out("[SubLevel] Plot removed (fixedSize=" + (cc != null && cc.IsFixedSize) + ").");
			return "Plot removed. Run 'sl status' in a moment; the chunks should unload.";
		}

		/// <summary>Called from ModEvents.WorldShuttingDown. PlotSpace.Current is a static, so without this it outlives the
		/// world: after exit-to-menu and reload, commands still see a 'plot' whose chunk cluster, ChunkManager observers and
		/// ChunkGameObjects belong to the DEAD world (status/commands work, nothing renders or collides, 'sl plot' refuses
		/// because 'a plot already exists'). The old world and its chunk manager are being torn down, so only managed
		/// references are dropped here - no cluster calls.</summary>
		public static void OnWorldShuttingDown()
		{
			PlatformRiding.Reset();
			SaveMetadata();
			foreach (Plot p in plots)
			{
				try { if (p.Driver != null) p.Driver.DetachAll(); } catch (System.Exception) { }
				try { if (p.RootGo != null) { Origin.Remove(p.RootGo.transform); Object.Destroy(p.RootGo); } } catch (System.Exception) { }
			}
			plots.Clear(); Publish(); Current = null; loadedWorld = null; metadataLoaded = false; metadataUnreadable = false;
			try { ShapeColliderCache.Clear(); } catch (System.Exception) { }
			Log.Out("[SubLevel] World shutting down: saved sublevel metadata and cleared runtime objects.");
		}

		public static void OnGameUpdate()
		{
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			if (world == null || world.ChunkCache == null) return;
			if (loadedWorld != world) { loadedWorld = world; metadataLoaded = false; metadataUnreadable = false; plots.Clear(); Publish(); Current = null; }
			if (metadataLoaded) { MpSync.Tick(world); return; }
			// Not during world load: the chunk provider / region files are not ready and nothing under the saved poses has
			// collision yet. Sublevels live on the server (single player = host); a remote client never builds them.
			GameManager gm = GameManager.Instance;
			if (gm.gameStateManager == null || !gm.gameStateManager.IsGameStarted() || world.ChunkCache.ChunkProvider == null) return;
			ConnectionManager conn = SingletonMonoBehaviour<ConnectionManager>.Instance;
			if (conn != null && !conn.IsServer) { metadataLoaded = true; return; }
			metadataLoaded = true;
			LoadMetadata(world);
		}

		public static void SaveState() { SaveMetadata(); }

		private static void SaveMetadata()
		{
			ConnectionManager cmSave = SingletonMonoBehaviour<ConnectionManager>.Instance;
			if (cmSave != null && !cmSave.IsServer) return;   // a client's copies are the server's to save
			if (loadedWorld == null || !metadataLoaded || metadataUnreadable) return;
			try
			{
				string path = MetadataPath;
				if (plots.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
				string dir = Path.GetDirectoryName(path); if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
				XmlDocument doc = new XmlDocument(); XmlElement root = doc.CreateElement("SubLevels"); doc.AppendChild(root);
				foreach (Plot p in plots)
				{
					if (p.Driver != null && !p.Driver.RestorePending) { p.Driver.Pose = p.Rb.position + Origin.position; p.Driver.Yaw = p.Rb.rotation.eulerAngles.y; }
					XmlElement e = doc.CreateElement("SubLevel"); root.AppendChild(e);
					e.SetAttribute("id", p.Id); e.SetAttribute("cx", p.Cx.ToString()); e.SetAttribute("cz", p.Cz.ToString());
					e.SetAttribute("size", (p.PlotRadius * 2 + 1).ToString());
					e.SetAttribute("x", p.Driver.Pose.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("y", p.Driver.Pose.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("z", p.Driver.Pose.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("yaw", p.Driver.Yaw.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("vx", p.Driver.Velocity.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("vy", p.Driver.Velocity.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("vz", p.Driver.Velocity.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("yawRate", p.Driver.YawRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					Quaternion q = p.Rb.rotation;
					e.SetAttribute("qx", q.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("qy", q.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("qz", q.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("qw", q.w.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
					e.SetAttribute("physics", p.Driver.PhysicsEnabled || p.Driver.RestorePending && p.Driver.PendingPhysics ? "true" : "false");
				}
				string tmp = path + ".tmp";
				doc.Save(tmp);
				if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
			}
			catch (System.Exception ex) { Log.Error("[SubLevel] Could not save sublevel metadata: " + ex); }
		}

		private static void LoadMetadata(World world)
		{
			string path = MetadataPath; if (!File.Exists(path)) return;
			XmlDocument doc = new XmlDocument();
			try { doc.Load(path); }
			catch (System.Exception ex)
			{
				metadataUnreadable = true; // keep the file as it is: overwriting or deleting it would orphan every sublevel
				Log.Error("[SubLevel] Could not read " + path + " - sublevels not restored and the file will not be touched this session: " + ex.Message);
				return;
			}
			int failed = 0;
			foreach (XmlNode n in doc.SelectNodes("/SubLevels/SubLevel"))
			{
				XmlElement e = n as XmlElement; if (e == null) continue;
				string id = e.GetAttribute("id");
				try
				{
					int cx = int.Parse(e.GetAttribute("cx")), cz = int.Parse(e.GetAttribute("cz")), size = int.Parse(e.GetAttribute("size"));
					string result = CreateInternal(cx, cz, "cobblestoneShapes:cube", size, id, true, false);
					Plot p = FindById(id);
					if (p == null) { failed++; Log.Warning("[SubLevel] Could not restore " + id + ": " + result); continue; }
					Vector3 pos = new Vector3(ParseOptional(e, "x"), ParseOptional(e, "y"), ParseOptional(e, "z"));
					float yaw = ParseOptional(e, "yaw");
					if (e.HasAttribute("qw"))
					{
						Quaternion q = new Quaternion(ParseOptional(e, "qx"), ParseOptional(e, "qy"), ParseOptional(e, "qz"), ParseOptional(e, "qw"));
						if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f) p.Driver.SetPose(pos, Quaternion.Normalize(q)); else p.Driver.SetPose(pos, yaw);
					}
					else p.Driver.SetPose(pos, yaw);
					bool.TryParse(e.GetAttribute("physics"), out bool physics);
					p.Driver.RestoreWhenReady(physics, new Vector3(ParseOptional(e, "vx"), ParseOptional(e, "vy"), ParseOptional(e, "vz")), ParseOptional(e, "yawRate"));
				}
				catch (System.Exception ex) { failed++; Log.Error("[SubLevel] Failed to restore sublevel " + id + ": " + ex); }
			}
			if (failed > 0) metadataUnreadable = true; // keep the lost entries in the file rather than dropping them on the next save
			if (plots.Count > 0) Current = plots[0];
			Log.Out("[SubLevel] Restored " + plots.Count + " saved sublevel(s)" + (failed > 0 ? ", " + failed + " FAILED (sublevels.xml left untouched)" : "") + ".");
		}

		private static float ParseOptional(XmlElement e, string name)
		{
			float value;
			return float.TryParse(e.GetAttribute(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0f;
		}

		public static void ReleaseIfAttached(ChunkGameObject cgo)
		{
			if (cgo == null) return;
			Plot p = FindByTransform(cgo.transform);
			if (p != null && cgo.transform.parent == p.RootGo.transform) cgo.transform.SetParent(null, false);
		}

		private static string Flags(Chunk c)
		{
			return "decor=" + (c.NeedsDecoration ? "PENDING" : "ok")
				+ " light=" + (c.NeedsLightCalculation ? "PENDING" : "ok")
				+ " regen=" + (c.NeedsRegeneration ? "PENDING" : "ok") + (c.NeedsRegenerationAt != 0 ? "(layers 0x" + c.NeedsRegenerationAt.ToString("X") + ")" : "")
				+ " meshLayer=" + c.HasMeshLayer()
				+ " needsCopy=" + c.NeedsCopying
				+ " collOnly=" + c.NeedsOnlyCollisionMesh
				+ " collGen=" + c.IsCollisionMeshGenerated
				+ " displayed=" + c.IsDisplayed
				+ " busy[light=" + c.InProgressLighting + " regen=" + c.InProgressRegeneration + " copy=" + c.InProgressCopying + " locked=" + c.IsLocked + "]";
		}

		/// <summary>Reports each neighbour of a chunk: missing / not decorated / not lit. This is what tells a lighting deadlock apart from a later failure.</summary>
		private static string NeighbourReport(ChunkCluster cc, Chunk c)
		{
			int missing = 0, undecorated = 0, unlit = 0;
			for (int dx = -1; dx <= 1; dx++)
				for (int dz = -1; dz <= 1; dz++)
				{
					if (dx == 0 && dz == 0) continue;
					Chunk n = cc.GetChunkSync(c.X + dx, c.Z + dz);
					if (n == null) missing++;
					else { if (n.NeedsDecoration) undecorated++; if (n.NeedsLightCalculation) unlit++; }
				}
			return "neighbours: missing=" + missing + " undecorated=" + undecorated + " unlit=" + unlit;
		}

		public static string ListSublevels()
		{
			if (plots.Count == 0) return "No sublevels exist. Create one with 'sl plot'.";
			var sb = new StringBuilder();
			sb.AppendLine("Sublevels (" + plots.Count + "):");
			foreach (Plot p in plots)
			{
				Vector3 pos = p.Driver != null ? p.Driver.Pose : Vector3.zero;
				float yaw = p.Driver != null ? p.Driver.Yaw : 0f;
				sb.AppendLine("  " + p.Id + (p == Current ? " [selected]" : "") + " | chunks=" + p.Cx + "," + p.Cz + " | size=" + (p.PlotRadius * 2 + 1) + "x" + (p.PlotRadius * 2 + 1) + " | position=" + pos.ToString("F2") + " | yaw=" + yaw.ToString("F1") + " | physics=" + (p.Driver != null && p.Driver.PhysicsEnabled) + (PlatformRiding.Ridden == p ? " | PLAYER ON BOARD" : ""));
			}
			return sb.ToString();
		}

		public static string Destroy(string id)
		{
			Plot p = FindById(id);
			if (p == null) return "Unknown sublevel ID '" + id + "'. Use 'sl list' to see IDs.";
			Current = p;
			return Destroy();
		}

		public static string Status(string id)
		{
			if (!Select(id)) return "Unknown sublevel ID '" + id + "'. Use 'sl list' to see IDs.";
			return Status();
		}

		public static string Status()
		{
			Plot p = Current;
			if (p == null) return "No plot.";
			World world = GameManager.Instance.World;
			ChunkCluster cc = world.ChunkCache;
			var sb = new StringBuilder();
			sb.AppendLine("cluster fixedSize=" + cc.IsFixedSize + " chunkMin=" + cc.ChunkMinPos + " chunkMax=" + cc.ChunkMaxPos + "  age=" + (Time.realtimeSinceStartup - p.CreatedAt).ToString("F1") + "s");
			sb.AppendLine("physics=" + (p.Driver.PhysicsEnabled ? "DYNAMIC (Unity Rigidbody owns movement)" : "SCRIPTED (kinematic)") +
				" rbVelocity=" + p.Rb.velocity + " angularVelocity=" + p.Rb.angularVelocity);
			sb.AppendLine("root pos=" + p.RootGo.transform.position + " pose=" + p.Driver.Pose + " yaw=" + p.Driver.Yaw + " originPos=" + Origin.position);

			if (p.Driver.Colliders != null) sb.AppendLine(p.Driver.Colliders.Describe());
			sb.AppendLine("plot footprint=" + (p.PlotRadius * 2 + 1) + "x" + (p.PlotRadius * 2 + 1) +
				" chunks; staged area=" + (p.StageRadius * 2 + 1) + "x" + (p.StageRadius * 2 + 1) + " chunks");
			int loaded = 0, lit = 0, displayed = 0;
			foreach (long key in p.SupportKeys)
			{
				Chunk c = cc.GetChunkSync(key);
				if (c == null) continue;
				loaded++; if (!c.NeedsLightCalculation) lit++;
			}
			sb.AppendLine("support ring: " + loaded + "/" + p.SupportKeys.Count + " loaded, " + lit + " lit/marked-lit (expect all " + p.SupportKeys.Count + ")");

			foreach (long key in p.Keys)
			{
				Chunk c = cc.GetChunkSync(key);
				if (c == null) { sb.AppendLine(" chunk " + WorldChunkCache.extractX(key) + "," + WorldChunkCache.extractZ(key) + ": NOT LOADED"); continue; }
				loaded++;
				if (!c.NeedsLightCalculation) lit++;
				ChunkGameObject cgo;
				bool shown = cc.DisplayedChunkGameObjects.TryGetValue(key, out cgo);
				if (shown) displayed++;
				sb.Append(" chunk " + c.X + "," + c.Z + (c.X == p.Cx && c.Z == p.Cz ? " [centre]" : "") + ": " + Flags(c) + " inDisplayedDict=" + shown + " | " + NeighbourReport(cc, c));
				if (shown && cgo != null)
				{
					int rend = 0, rendOn = 0, mc = 0, mcOn = 0, mcMesh = 0;
					foreach (var r in cgo.GetComponentsInChildren<MeshRenderer>(true)) { rend++; if (r.enabled && r.gameObject.activeInHierarchy) rendOn++; }
					foreach (var m in cgo.GetComponentsInChildren<MeshCollider>(true)) { mc++; if (m.enabled) mcOn++; if (m.sharedMesh != null) mcMesh++; }
					sb.Append(" | parent=" + (cgo.transform.parent ? cgo.transform.parent.name : "none") + " renderers=" + rendOn + "/" + rend + " meshColliders=" + mcMesh + " withMesh, " + mcOn + " enabled of " + mc + " worldPos=" + cgo.transform.position);
				}
				sb.AppendLine();
			}
			sb.AppendLine("summary: " + loaded + "/" + (p.Keys.Count + p.SupportKeys.Count) + " chunks loaded, plot chunks lit=" + CountLit(cc, p) + "/" + p.Keys.Count + ", displayed=" + displayed + "/" + p.Keys.Count);

			Chunk centre = cc.GetChunkSync(WorldChunkCache.MakeChunkKey(p.Cx, p.Cz));
			if (centre != null)
			{
				int solid = 0;
				for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) for (int y = 99; y < 103; y++) if (!centre.GetBlock(x, y, z).isair) solid++;
				sb.AppendLine(" blocks in centre chunk (y99-102): " + solid + " (expected 10)");
			}
			return sb.ToString();
		}

		/// <summary>Dumps shader / keywords / properties of the materials on the centre chunk's renderers to output_log.txt.
		/// Block textures are drawn by game shaders that are not in the decompile, so this is how we find out what the
		/// shader samples (world position? world normal? vertex data?) before touching it.</summary>
		public static string DumpMaterials()
		{
			return DumpMaterials(Current != null ? Current.Id : null);
		}

		public static string DumpMaterials(string id)
		{
			Plot p = FindById(id);
			if (p == null) return "No plot.";
			ChunkCluster cc = GameManager.Instance.World.ChunkCache;
			ChunkGameObject cgo;
			if (!cc.DisplayedChunkGameObjects.TryGetValue(WorldChunkCache.MakeChunkKey(p.Cx, p.Cz), out cgo) || cgo == null) return "Centre chunk is not displayed yet.";
			var seen = new HashSet<int>();
			int n = 0;
			foreach (var r in cgo.GetComponentsInChildren<MeshRenderer>(true))
			{
				foreach (Material m in r.sharedMaterials)
				{
					if (m == null || !seen.Add(m.GetInstanceID())) continue;
					n++;
					Shader sh = m.shader;
					var sb = new StringBuilder();
					sb.AppendLine("[SubLevel] material '" + m.name + "' shader='" + sh.name + "' renderer='" + r.name + "' layer=" + LayerMask.LayerToName(r.gameObject.layer) + " tag=" + r.transform.tag);
					sb.AppendLine("  keywords: " + string.Join(" ", m.shaderKeywords));
					for (int i = 0; i < sh.GetPropertyCount(); i++)
					{
						string pn = sh.GetPropertyName(i);
						var t = sh.GetPropertyType(i);
						string v = "";
						if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) v = m.GetFloat(pn).ToString("G4");
						else if (t == UnityEngine.Rendering.ShaderPropertyType.Vector) v = m.GetVector(pn).ToString();
						else if (t == UnityEngine.Rendering.ShaderPropertyType.Color) v = m.GetColor(pn).ToString();
						else if (t == UnityEngine.Rendering.ShaderPropertyType.Texture) { Texture tex = m.GetTexture(pn); v = tex == null ? "null" : tex.name + " (" + tex.GetType().Name + " " + tex.width + "x" + tex.height + ")"; }
						sb.AppendLine("  " + t + " " + pn + " = " + v);
					}
					Log.Out(sb.ToString());
				}
			}
			return n + " material(s) dumped to output_log.txt (search for '[SubLevel] material').";
		}

		private static int CountLit(ChunkCluster cc, Plot p)
		{
			int n = 0;
			foreach (long key in p.Keys) { Chunk c = cc.GetChunkSync(key); if (c != null && !c.NeedsLightCalculation) n++; }
			return n;
		}
	}
}
