using UnityEngine;

namespace SubLevelPhysics
{
    /// <summary>
    /// Owns the sublevel pose. In scripted mode it moves a kinematic body; in physics mode Unity owns the
    /// Rigidbody pose and this component only mirrors it into pose state. Collision comes from the block
    /// colliders built by CellColliders in both modes.
    /// </summary>
    public sealed class SublevelDriver : MonoBehaviour
    {
        public string SublevelId { get; private set; }
        public Vector3 Pose { get; private set; }
        public float Yaw { get; private set; }
        public Vector3 Velocity;
        public float YawRate;
        public bool PhysicsEnabled { get; private set; }
        /// <summary>Restored from the save; waiting for its colliders and the ground under it before motion resumes.</summary>
        public bool RestorePending { get; private set; }
        public bool PendingPhysics { get; private set; }

        internal SublevelPlot Plot;
        private Rigidbody body;
        private Vector3 pendingVelocity;
        private float pendingYawRate, pendingSince;
        /// <summary>Pitch/roll kept under the scripted yaw (a tipped-over sublevel restored from a save stays tipped).</summary>
        private Quaternion tilt = Quaternion.identity;
        private float nextPersist;

        // 7DTD's chunk collision meshes use layer 16 (TerrainCollision). Some builds configure that layer to ignore
        // itself, so a dynamic hull on the same layer would pass through terrain. Shared by all sublevels, so the
        // override is reference-counted: the first physics sublevel records the original state, the last restores it.
        private const int TerrainCollisionLayer = 16;
        private static int terrainOverrideUsers;
        private static bool terrainOriginalIgnored;
        private bool terrainOverrideApplied;

        internal Vector3 SavedVelocity { get { return RestorePending ? pendingVelocity : Velocity; } }
        internal float SavedYawRate { get { return RestorePending ? pendingYawRate : YawRate; } }

        internal void Initialize(string id, Rigidbody rb, Vector3 pose, float yaw)
        {
            SublevelId = id;
            body = rb;
            SetPose(pose, yaw);
        }

        public void SetPose(Vector3 position, float yaw)
        {
            tilt = Quaternion.identity;
            Pose = position;
            Yaw = yaw;
            if (body == null) body = GetComponent<Rigidbody>();
            Vector3 local = position - Origin.position;
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            transform.SetPositionAndRotation(local, q);
            if (body != null)
            {
                body.position = local;
                body.rotation = q;
                if (PhysicsEnabled) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
        }

        /// <summary>Full rotation (used when restoring a sublevel that physics left tilted).</summary>
        internal void SetPose(Vector3 position, Quaternion rotation)
        {
            float yaw = rotation.eulerAngles.y;
            SetPose(position, yaw);
            tilt = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * rotation;
            Quaternion q = Quaternion.Euler(0f, yaw, 0f) * tilt;
            transform.rotation = q;
            if (body != null) body.rotation = q;
        }

        private void FixedUpdate()
        {
            if (body == null) return;
            Vector3 originNow = Origin.position;
            if (PhysicsEnabled)
            {
                // The root is registered with Origin at level -1, so 7DTD moves the Rigidbody itself during
                // Origin.Reposition. Do not apply a second, delayed compensation here.
                Pose = body.position + originNow;
                Yaw = body.rotation.eulerAngles.y;
                Velocity = body.velocity;
                YawRate = body.angularVelocity.y * Mathf.Rad2Deg;
                return;
            }
            Pose += Velocity * Time.fixedDeltaTime;
            Yaw += YawRate * Time.fixedDeltaTime;
            body.MovePosition(Pose - originNow);
            body.MoveRotation(Quaternion.Euler(0f, Yaw, 0f) * tilt);
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup >= nextPersist)
            {
                nextPersist = Time.realtimeSinceStartup + 5f;
                if (PlotSpace.Current != null && PlotSpace.Current.Id == SublevelId) PlotSpace.SaveState();
            }
            TickRestore();
        }

        private float ComputeMass()
        {
            return Mathf.Max(100f, (Plot != null ? Plot.SolidVolume : 0f) * 100f);
        }

        /// <summary>Called after the projection's colliders were rebuilt (block edits change the mass).</summary>
        internal void OnCollidersRebuilt()
        {
            if (PhysicsEnabled && body != null) body.mass = ComputeMass();
        }

        public string SetPhysics(bool enabled)
        {
            if (body == null) body = GetComponent<Rigidbody>();
            if (body == null) return "No Rigidbody is attached to this sublevel.";
            if (PhysicsEnabled == enabled) return "Sublevel physics is already " + (enabled ? "enabled." : "disabled.");

            if (enabled)
            {
                if (Plot == null || Plot.PieceCount == 0)
                    return "Could not enable dynamic physics: the sublevel has no collidable blocks yet.";
                Pose = body.position + Origin.position;
                Yaw = body.rotation.eulerAngles.y;
                AcquireTerrainCollisionMatrix();
                Physics.SyncTransforms();
                body.isKinematic = false;
                body.useGravity = true;
                body.detectCollisions = true;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.mass = ComputeMass();
                body.drag = 0.05f;
                body.angularDrag = 0.1f;
                body.constraints = RigidbodyConstraints.None;
                PhysicsEnabled = true;
                Physics.SyncTransforms();
                Log.Out("[SubLevel] Dynamic physics enabled for " + SublevelId + ": " + Plot.ColliderBoxes + " boxes + " + Plot.ColliderConvex +
                    " convex pieces, mass " + body.mass.ToString("F0") + " kg, root=" + body.position + " origin=" + Origin.position + " centreOfMass=" + body.worldCenterOfMass);
                return "Unity Rigidbody physics enabled using the block collision of the sublevel: " + Plot.ColliderBoxes + " boxes + " + Plot.ColliderConvex +
                    " convex shape pieces, mass " + body.mass.ToString("F0") + " kg. Gravity and dynamic collision response are active.";
            }

            PhysicsStaffController staff = GetComponent<PhysicsStaffController>();
            if (staff != null) staff.ReleaseGrab();
            Pose = body.position + Origin.position;
            Yaw = body.rotation.eulerAngles.y;
            tilt = Quaternion.identity;
            Velocity = Vector3.zero;
            YawRate = 0f;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.useGravity = false;
            body.isKinematic = true;
            RestoreTerrainCollisionMatrix();
            body.interpolation = RigidbodyInterpolation.Interpolate;
            PhysicsEnabled = false;
            Physics.SyncTransforms();
            return "Unity Rigidbody physics disabled. The sublevel keeps its block collision in scripted (kinematic) mode.";
        }

        private void AcquireTerrainCollisionMatrix()
        {
            if (terrainOverrideApplied) return;
            if (terrainOverrideUsers == 0)
            {
                terrainOriginalIgnored = Physics.GetIgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer);
                if (terrainOriginalIgnored) Physics.IgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer, false);
            }
            terrainOverrideUsers++;
            terrainOverrideApplied = true;
            Log.Out("[SubLevel] TerrainCollision layer 16 original self-ignore=" + terrainOriginalIgnored + "; sublevels using override=" + terrainOverrideUsers + ".");
        }

        private void RestoreTerrainCollisionMatrix()
        {
            if (!terrainOverrideApplied) return;
            terrainOverrideApplied = false;
            terrainOverrideUsers = Mathf.Max(0, terrainOverrideUsers - 1);
            if (terrainOverrideUsers == 0 && terrainOriginalIgnored)
            {
                Physics.IgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer, true);
                Log.Out("[SubLevel] Restored TerrainCollision layer 16 self-ignore=true.");
            }
        }

        private void OnDestroy() { RestoreTerrainCollisionMatrix(); }

        /// <summary>Restore from the save: hold the saved pose (kinematic) until the colliders exist and the ground under the
        /// pose has collision, then resume physics and motion. Without the wait a dynamic sublevel restored while the world
        /// is still streaming in falls through terrain that has no collider yet.</summary>
        internal void RestoreWhenReady(bool physics, Vector3 velocity, float yawRate)
        {
            RestorePending = true;
            PendingPhysics = physics;
            pendingVelocity = velocity;
            pendingYawRate = yawRate;
            pendingSince = Time.realtimeSinceStartup;
        }

        private void TickRestore()
        {
            if (!RestorePending) return;
            World world = PlotSpace.PrimaryWorld;
            ChunkCluster cc = world != null ? world.ChunkCache : null;
            if (cc == null) return;
            bool timedOut = Time.realtimeSinceStartup - pendingSince > 90f;
            if (!timedOut)
            {
                if (Plot == null || Plot.PieceCount == 0) return;
                if (PendingPhysics)
                {
                    Chunk ground = cc.GetChunkSync(World.toChunkXZ(Mathf.FloorToInt(Pose.x)), World.toChunkXZ(Mathf.FloorToInt(Pose.z)));
                    if (ground == null || !ground.IsCollisionMeshGenerated || !cc.DisplayedChunkGameObjects.ContainsKey(ground.Key)) return;
                }
            }
            RestorePending = false;
            string result = "";
            if (PendingPhysics) result = SetPhysics(true);
            if (!PendingPhysics || PhysicsEnabled) RestoreMotion(pendingVelocity, pendingYawRate);
            Log.Out("[SubLevel] " + SublevelId + " restore complete after " + (Time.realtimeSinceStartup - pendingSince).ToString("F1") + " s" +
                (timedOut ? " (TIMED OUT waiting for colliders/ground)" : "") + (PendingPhysics ? ": " + result : "."));
            PlotSpace.SaveState();
        }

        /// <summary>In dynamic mode the Rigidbody owns velocity, so it must be pushed into the body (FixedUpdate mirrors it back).</summary>
        internal void RestoreMotion(Vector3 velocity, float yawRateDegPerSec)
        {
            Velocity = velocity;
            YawRate = yawRateDegPerSec;
            if (PhysicsEnabled && body != null)
            {
                body.velocity = velocity;
                body.angularVelocity = new Vector3(0f, yawRateDegPerSec * Mathf.Deg2Rad, 0f);
            }
        }
    }
}
