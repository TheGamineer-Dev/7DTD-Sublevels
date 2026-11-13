using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SubLevelPhysics
{
    internal sealed class SublevelPlot
    {
        public string Id;
        public GameObject Root;
        public Rigidbody Body;
        public SublevelDriver Driver;
        public float CreatedAt;
        public int ColliderBoxes, ColliderConvex;
        public float SolidVolume;
        public int PieceCount { get { return ColliderBoxes + ColliderConvex; } }
    }

    /// <summary>
    /// Runtime sublevel registry. Persistent block data remains exclusively in SublevelCellStore;
    /// this class owns only the transform, runtime projection, and small pose metadata record.
    /// </summary>
    internal static class PlotSpace
    {
        public const int PivotY = 100;
        private static readonly List<SublevelPlot> plots = new List<SublevelPlot>();
        private static volatile SublevelPlot[] snapshot = new SublevelPlot[0];
        private static World loadedWorld;
        private static bool metadataLoaded;
        private static bool metadataUnreadable;
        private static SublevelPlot current;
        private static string MetadataPath { get { return GameIO.GetSaveGameDir() + "/sublevels-projections.xml"; } }

        public static int Count { get { return plots.Count; } }

        /// <summary>The player-facing world, or null while MultiWorld has the hidden Sublevels storage world selected.</summary>
        internal static World PrimaryWorld
        {
            get
            {
                World w = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (w == null) return null;
                if (WorldUpdateSuppression.StorageWorld != null && ReferenceEquals(w, WorldUpdateSuppression.StorageWorld)) return null;
                return w;
            }
        }
        public static SublevelPlot Current { get { return current; } }
        public static IList<SublevelPlot> All { get { return plots.AsReadOnly(); } }

        private static void Publish() { snapshot = plots.ToArray(); }

        public static SublevelPlot FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return plots.Find(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static SublevelPlot FindByTransform(Transform transform)
        {
            if (transform == null) return null;
            SublevelPlot[] items = snapshot;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Root != null && transform.IsChildOf(items[i].Root.transform)) return items[i];
            return null;
        }

        public static bool Select(string id)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return false;
            current = p;
            return true;
        }

        public static void OnGameUpdate()
        {
            World world = GameManager.Instance != null ? GameManager.Instance.World : null;
            if (world == null || world.ChunkCache == null || !MultiWorldRuntimeBridge.IsServerAuthority) return;
            // With MultiWorld, GameManager.Instance.World can be the hidden Sublevels storage world
            // while MultiWorld ticks it. Projections belong to the primary world only; reacting to the
            // storage world would tear down and rebuild every projection on each switch.
            if (WorldUpdateSuppression.StorageWorld != null && ReferenceEquals(world, WorldUpdateSuppression.StorageWorld)) return;
            if (!ReferenceEquals(loadedWorld, world))
            {
                Log.Out("[SubLevel] PlotSpace world changed (previous=" + (loadedWorld == null ? "none" : loadedWorld.GetHashCode().ToString()) +
                    ", new=" + world.GetHashCode() + "); resetting projections.");
                ClearRuntime();
                loadedWorld = world;
                metadataLoaded = false;
                metadataUnreadable = false;
            }
            if (metadataLoaded) return;
            GameManager gm = GameManager.Instance;
            if (gm.gameStateManager == null || !gm.gameStateManager.IsGameStarted() || world.ChunkCache.ChunkProvider == null) return;
            metadataLoaded = true;
            LoadMetadata();
        }

        public static void OnWorldShuttingDown()
        {
            SaveState();
            PlatformRiding.Reset();
            ClearRuntime();
            try { ShapeColliderCache.Clear(); } catch (Exception) { }
            loadedWorld = null;
            metadataLoaded = false;
            metadataUnreadable = false;
        }

        private static void ClearRuntime()
        {
            foreach (SublevelPlot p in plots)
            {
                try { if (p.Root != null) { Origin.Remove(p.Root.transform); UnityEngine.Object.Destroy(p.Root); } }
                catch (Exception e) { Log.Warning("[SubLevel] Runtime cleanup failed for " + p.Id + ": " + e.Message); }
            }
            plots.Clear();
            current = null;
            Publish();
        }

        public static string CreateAuto(string blockName)
        {
            if (!SublevelStorageApi.IsReady) return StorageWorldProvisioner.Status ?? "Sublevels storage is not ready. Check the server log for the storage initialization error.";
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            SublevelPlot plot = CreateRuntime(id, Vector3.zero, 0f);
            if (plot == null) return "Could not create the runtime sublevel.";
            BlockValue testBlock;
            string error;
            if (!TryResolveBlock(blockName, out testBlock, out error))
            {
                RemoveRuntime(plot);
                return error;
            }
            try
            {
                // Small visible starter platform, matching the original mod's 3x3 floor + pillar test.
                for (int x = -1; x <= 1; x++)
                    for (int z = -1; z <= 1; z++) SublevelStorageApi.SetBlock(id, x, PivotY, z, testBlock);
                SublevelStorageApi.SetBlock(id, 0, PivotY + 1, 0, testBlock);
                CellProjection.Rebuild(plot);
                EntityPlayerLocal player = GameManager.Instance.World.GetPrimaryPlayer();
                if (player != null) plot.Driver.SetPose(PosInFront(player), 0f);
                current = plot;
                SaveState();
                IReadOnlyCollection<CellRecord> cells = SublevelStorageApi.GetCellRecords(id);
                return "Sublevel " + id + " created. Initial test platform stored in " + cells.Count + " logical cell(s). Use 'sl set " + id + " <block> <x> <y-offset> <z>' to expand it; cell allocation is automatic.";
            }
            catch (Exception e)
            {
                Log.Error("[SubLevel] Initial sublevel creation failed: " + e);
                RemoveRuntime(plot);
                return "Sublevel creation failed while writing its starter blocks: " + e.Message;
            }
        }

        /// <summary>
        /// MultiWorld gives every world (including the player-facing one) its own Unity scene + PhysicsScene and routes
        /// every Physics.* query/simulation to the active world's PhysicsScene. A plain new GameObject lands in the
        /// default scene, so its colliders are invisible to the player's CharacterController and to targeting raycasts:
        /// the mesh renders (cameras see all scenes) but nothing can touch it. Put the root in the player's scene.
        /// Children created later are parented under the root and follow it.
        /// </summary>
        private static void MoveToPlayerPhysicsScene(GameObject root)
        {
            try
            {
                Scene target = default(Scene);
                EntityPlayerLocal player = GameManager.Instance != null ? GameManager.Instance.myEntityPlayerLocal : null;
                if (player != null) target = player.gameObject.scene;
                if (!target.IsValid() && GameManager.Instance != null && GameManager.Instance.World != null && GameManager.Instance.World.EntitiesTransform != null)
                    target = GameManager.Instance.World.EntitiesTransform.gameObject.scene;
                if (target.IsValid() && root.scene != target) SceneManager.MoveGameObjectToScene(root, target);
                Log.Out("[SubLevel][SceneDiag] root '" + root.name + "' scene='" + root.scene.name + "' (player scene='" + (player != null ? player.gameObject.scene.name : "none") + "'); defaultPhysicsScene==rootScene=" +
                    (Physics.defaultPhysicsScene.Equals(root.scene.GetPhysicsScene())));
            }
            catch (Exception e) { Log.Warning("[SubLevel] Could not move sublevel root to the player's physics scene: " + e.Message); }
        }

        private static SublevelPlot CreateRuntime(string id, Vector3 pose, float yaw)
        {
            if (FindById(id) != null) return FindById(id);
            GameObject root = new GameObject("Sublevel_" + id);
            MoveToPlayerPhysicsScene(root);
            root.transform.SetPositionAndRotation(pose - Origin.position, Quaternion.Euler(0f, yaw, 0f));
            Origin.Add(root.transform, -1);
            Rigidbody rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            SublevelDriver driver = root.AddComponent<SublevelDriver>();
            driver.Initialize(id, rb, pose, yaw);
            SublevelPlot plot = new SublevelPlot { Id = id, Root = root, Body = rb, Driver = driver, CreatedAt = Time.realtimeSinceStartup };
            driver.Plot = plot;
            root.AddComponent<PhysicsStaffController>();
            plots.Add(plot);
            Publish();
            return plot;
        }

        private static void RemoveRuntime(SublevelPlot plot)
        {
            if (plot == null) return;
            plots.Remove(plot);
            if (current == plot) current = plots.Count > 0 ? plots[plots.Count - 1] : null;
            Publish();
            if (plot.Root != null) { Origin.Remove(plot.Root.transform); UnityEngine.Object.Destroy(plot.Root); }
        }

        public static Vector3 PosInFront(EntityPlayerLocal player)
        {
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            return player.position + forward.normalized * 6f + Vector3.up;
        }

        public static string SetBlockAt(string id, string blockName, int dx, int dy, int dz, int rotation)
        {
            SublevelPlot plot = FindById(id);
            if (plot == null) return "Unknown sublevel ID '" + id + "'. Use 'sl list'.";
            if (!SublevelStorageApi.IsReady) return "Sublevels storage is not ready.";
            int y = PivotY + dy;
            if (y < 1 || y > 254) return "Block Y is outside the writable range. The command uses Y offset from the sublevel pivot at 100; valid offsets are -99 through 154.";
            BlockValue value;
            string error;
            if (!TryResolveBlock(blockName, out value, out error)) return error;
            value.rotation = (byte)Mathf.Clamp(rotation, 0, 255);
            try
            {
                SublevelStorageApi.SetBlock(id, dx, y, dz, value);
                if (value.isair)
                    SublevelStorageApi.RecycleEmptyCell(id, FloorDiv(dx, 16), FloorDiv(dz, 16));
                CellProjection.Rebuild(plot);
                SaveState();
                return "Stored " + (value.isair ? "air" : value.Block.GetBlockName()) + " at logical block " + dx + "," + dy + "," + dz + ". Projection rebuilt.";
            }
            catch (Exception e)
            {
                Log.Error("[SubLevel] Block write failed for " + id + ": " + e);
                return "Storage write failed: " + e.Message;
            }
        }

        private static bool TryResolveBlock(string name, out BlockValue value, out string error)
        {
            value = BlockValue.Air;
            error = null;
            if (string.Equals(name, "air", StringComparison.OrdinalIgnoreCase)) return true;
            Block block = Block.GetBlockByName(name, true) ?? Block.GetBlockByName(name + ":cube", true);
            if (block == null) { error = "Unknown block '" + name + "'."; return false; }
            value = block.ToBlockValue();
            return true;
        }

        public static string SetPose(string id, Vector3 position, float yaw)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            p.Driver.SetPose(position, yaw);
            SaveState();
            return "Pose updated for " + id + ".";
        }

        public static string SetVelocity(string id, Vector3 velocity)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            p.Driver.Velocity = velocity;
            SaveState();
            return "Scripted velocity set to " + velocity + ".";
        }

        public static string SetYawRate(string id, float rate)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            p.Driver.YawRate = rate;
            SaveState();
            return "Scripted yaw rate set to " + rate.ToString("F2", CultureInfo.InvariantCulture) + " deg/s.";
        }

        public static string SetPhysics(string id, bool enabled)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            string result = p.Driver.SetPhysics(enabled);
            SaveState();
            return result;
        }

        public static string ListSublevels()
        {
            if (plots.Count == 0) return "No sublevels exist. Create one with 'sl plot'.";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Sublevels (" + plots.Count + "):");
            foreach (SublevelPlot p in plots)
            {
                int cells = SublevelStorageApi.GetCellRecords(p.Id).Count;
                sb.AppendLine("  " + p.Id + (p == current ? " [selected]" : "") + " | cells=" + cells + " | position=" + p.Driver.Pose.ToString("F2") + " | yaw=" + p.Driver.Yaw.ToString("F1") + " | physics=" + p.Driver.PhysicsEnabled + (PlatformRiding.Ridden == p ? " | PLAYER ON BOARD" : ""));
            }
            return sb.ToString();
        }

        public static string Status(string id)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            current = p;
            IReadOnlyCollection<CellRecord> records = SublevelStorageApi.GetCellRecords(id);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Sublevel " + id + " | position=" + p.Driver.Pose + " | yaw=" + p.Driver.Yaw);
            sb.AppendLine("physics=" + (p.Driver.PhysicsEnabled ? "DYNAMIC (Unity Rigidbody owns movement)" : "SCRIPTED (kinematic)") + " | block colliders: " + p.ColliderBoxes + " boxes + " + p.ColliderConvex + " convex pieces, solid volume " + p.SolidVolume.ToString("F1") + " m3");
            sb.AppendLine("Logical storage cells: " + records.Count + " | projection children: " + (p.Root != null ? p.Root.transform.childCount : 0));
            foreach (CellRecord r in records)
                sb.AppendLine("  logical=" + r.Logical + " -> physical=" + r.Physical + " | touched=" + new DateTime(r.LastTouchedUtcTicks, DateTimeKind.Utc).ToString("u"));
            sb.AppendLine("Storage world is authoritative; projection meshes/colliders are runtime-only.");
            return sb.ToString();
        }

        public static string Destroy(string id)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            IReadOnlyCollection<CellRecord> records = SublevelStorageApi.GetCellRecords(id);
            // Do not silently orphan non-empty storage. The user must clear the blocks first.
            foreach (CellRecord r in records)
            {
                BlockValue[] blocks;
                if (!SublevelStorageApi.TryReadCellBlocks(id, r.Logical.X, r.Logical.Z, out blocks))
                    return "Cannot delete: could not read logical cell " + r.Logical + ".";
                for (int i = 0; i < blocks.Length; i++)
                    if (!blocks[i].isair) return "Cannot delete: cell " + r.Logical + " still contains blocks. Clear its blocks with 'sl set " + id + " air <x> <y-offset> <z>' first.";
            }
            foreach (CellRecord r in records)
                if (!SublevelStorageApi.RecycleEmptyCell(id, r.Logical.X, r.Logical.Z))
                    return "Cannot delete: cell " + r.Logical + " contains persistent payload data or failed its emptiness check; it was not reclaimed.";
            RemoveRuntime(p);
            SaveState();
            return "Deleted empty sublevel " + id + "; its verified-empty cells were returned to the global allocator.";
        }

        public static string DumpMaterials(string id)
        {
            SublevelPlot p = FindById(id);
            if (p == null) return "Unknown sublevel ID '" + id + "'.";
            int renderers = p.Root != null ? p.Root.GetComponentsInChildren<MeshRenderer>(true).Length : 0;
            int boxColliders = p.Root != null ? p.Root.GetComponentsInChildren<BoxCollider>(true).Length : 0;
            int meshColliders = p.Root != null ? p.Root.GetComponentsInChildren<MeshCollider>(true).Length : 0;
            return "Runtime projection: " + renderers + " mesh renderer(s), " + boxColliders + " box collider(s), " + meshColliders + " convex mesh collider(s). Material assignment comes from MeshDescription; inspect output_log.txt for renderer/material warnings.";
        }

        public static void SaveState()
        {
            if (!metadataLoaded || metadataUnreadable || loadedWorld == null) return;
            try
            {
                string path = MetadataPath;
                if (plots.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                XmlDocument doc = new XmlDocument();
                XmlElement root = doc.CreateElement("SublevelProjections");
                root.SetAttribute("version", "1");
                doc.AppendChild(root);
                foreach (SublevelPlot p in plots)
                {
                    XmlElement e = doc.CreateElement("Sublevel");
                    e.SetAttribute("id", p.Id);
                    e.SetAttribute("x", F(p.Driver.Pose.x)); e.SetAttribute("y", F(p.Driver.Pose.y)); e.SetAttribute("z", F(p.Driver.Pose.z));
                    e.SetAttribute("yaw", F(p.Driver.Yaw));
                    Vector3 savedVelocity = p.Driver.SavedVelocity;
                    e.SetAttribute("vx", F(savedVelocity.x)); e.SetAttribute("vy", F(savedVelocity.y)); e.SetAttribute("vz", F(savedVelocity.z));
                    e.SetAttribute("yawRate", F(p.Driver.SavedYawRate));
                    Quaternion q = p.Body != null ? p.Body.rotation : Quaternion.identity;
                    e.SetAttribute("qx", F(q.x)); e.SetAttribute("qy", F(q.y)); e.SetAttribute("qz", F(q.z)); e.SetAttribute("qw", F(q.w));
                    e.SetAttribute("physics", (p.Driver.PhysicsEnabled || (p.Driver.RestorePending && p.Driver.PendingPhysics)) ? "true" : "false");
                    root.AppendChild(e);
                }
                string temp = path + ".tmp";
                doc.Save(temp);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception e) { Log.Error("[SubLevel] Could not save projection metadata: " + e); }
        }

        private static void LoadMetadata()
        {
            string path = MetadataPath;
            if (!File.Exists(path)) return;
            XmlDocument doc = new XmlDocument();
            try { doc.Load(path); }
            catch (Exception e)
            {
                metadataUnreadable = true;
                Log.Error("[SubLevel] Projection metadata is unreadable and will not be overwritten: " + e.Message);
                return;
            }
            int restored = 0, failed = 0;
            foreach (XmlNode node in doc.SelectNodes("/SublevelProjections/Sublevel"))
            {
                XmlElement e = node as XmlElement;
                if (e == null) continue;
                string id = e.GetAttribute("id");
                try
                {
                    Vector3 pos = new Vector3(P(e, "x"), P(e, "y"), P(e, "z"));
                    float yaw = P(e, "yaw");
                    SublevelPlot p = CreateRuntime(id, pos, yaw);
                    if (p == null) { failed++; continue; }
                    if (e.HasAttribute("qw"))
                    {
                        Quaternion q = new Quaternion(P(e, "qx"), P(e, "qy"), P(e, "qz"), P(e, "qw"));
                        if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f) p.Driver.SetPose(pos, Quaternion.Normalize(q));
                    }
                    bool physics;
                    bool.TryParse(e.GetAttribute("physics"), out physics);
                    CellProjection.Rebuild(p);
                    // Hold the saved pose (kinematic) until colliders and ground exist, then resume motion/physics.
                    p.Driver.RestoreWhenReady(physics, new Vector3(P(e, "vx"), P(e, "vy"), P(e, "vz")), P(e, "yawRate"));
                    restored++;
                }
                catch (Exception ex) { failed++; Log.Error("[SubLevel] Failed to restore projection " + id + ": " + ex); }
            }
            if (failed > 0) metadataUnreadable = true;
            if (plots.Count > 0) current = plots[0];
            Log.Out("[SubLevel] Restored " + restored + " projection(s)" + (failed > 0 ? "; " + failed + " failed, metadata protected" : "."));
        }

        private static string F(float value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static float P(XmlElement e, string name)
        {
            float value;
            return float.TryParse(e.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
        }
        private static int FloorDiv(int value, int divisor) { int q = value / divisor, r = value % divisor; return r < 0 ? q - 1 : q; }
    }
}
