using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace SubLevelPhysics
{
	/// <summary>
	/// Owns the sublevel pose. In scripted mode it moves a kinematic body; in physics mode
	/// Unity owns the Rigidbody pose and this component only mirrors it into plot-space state.
	/// Collision comes from PlotBlockColliders (the plot's block geometry) in both modes.
	/// </summary>
	public sealed class PlotDriver : MonoBehaviour
	{
		public Plot Plot;
		public Vector3 Pose;
		public float Yaw;
		public Vector3 Velocity;
		public float YawRate;
		public bool PhysicsEnabled { get; private set; }
		/// <summary>Restored from the save and waiting for its chunks and the ground under it before motion resumes.</summary>
		public bool RestorePending { get; private set; }
		public bool PendingPhysics { get; private set; }
		private Vector3 pendingVelocity;
		private float pendingYawRate, pendingSince;
		/// <summary>Pitch/roll kept under the scripted yaw (a tipped-over sublevel restored from a save stays tipped).</summary>
		private Quaternion tilt = Quaternion.identity;
		private Rigidbody rb;
		internal PlotBlockColliders Colliders;

		private float nextWatch;
		private float nextPersist;
		private Vector3 lastOccPos = new Vector3(float.MaxValue, 0f, 0f);
		private Quaternion lastOccRot = Quaternion.identity;
		private int attachedLogged;
		private bool allDisplayedLogged;
		// 7DTD's chunk collision meshes use layer 16 (TerrainCollision). Some game
		// builds configure that layer to ignore itself. A dynamic sublevel hull copied
		// onto the same layer would then pass straight through ordinary terrain.
		private const int TerrainCollisionLayer = 16;
		// Shared across all sublevels: the collision matrix is global, so it is reference-counted.
		// The first physics sublevel records the original state; the last one to stop restores it.
		private static int terrainOverrideUsers;
		private static bool terrainOriginalIgnored;
		private bool terrainSelfCollisionOverrideApplied; // this driver currently holds a reference

		private void Awake()
		{
			rb = GetComponent<Rigidbody>();
		}

		private void FixedUpdate()
		{
			if (rb == null) return;
			Vector3 originNow = Origin.position;
			if (PhysicsEnabled)
			{
				// The root Rigidbody is registered with Origin at level -1, so 7DTD moves
				// the Rigidbody itself synchronously during Origin.Reposition. Do not apply a
				// second, delayed compensation here: Origin.Reposition performs a physics
				// simulation before the next FixedUpdate, which otherwise lets the terrain
				// move while this body remains behind for one step (tunnelling/penetration).
				Pose = rb.position + originNow;
				Yaw = rb.rotation.eulerAngles.y;
				Velocity = rb.velocity;
				YawRate = rb.angularVelocity.y * Mathf.Rad2Deg;
				return;
			}

			Pose += Velocity * Time.fixedDeltaTime;
			Yaw += YawRate * Time.fixedDeltaTime;
			rb.MovePosition(Pose - originNow);
			rb.MoveRotation(Quaternion.Euler(0f, Yaw, 0f) * tilt);
		}

		/// <summary>Creates the block-geometry collider set for this plot. Call once, after Plot has been assigned.</summary>
		public void InitColliders()
		{
			if (Colliders != null) return;
			Colliders = new PlotBlockColliders(Plot, transform);
			Colliders.MarkAllDirty();
		}

		public void MarkChunkCollisionDirty(long chunkKey)
		{
			if (Colliders != null) Colliders.NotifyChunkCopied(chunkKey);
		}

		public string SetPhysics(bool enabled)
		{
			if (rb == null) rb = GetComponent<Rigidbody>();
			if (rb == null) return "No Rigidbody is attached to this sublevel.";
			if (PhysicsEnabled == enabled) return "Sublevel physics is already " + (enabled ? "enabled." : "disabled.");

			if (enabled)
			{
				if (Colliders == null) InitColliders();
				// Make sure the colliders reflect the current blocks before the body starts to move.
				Colliders.MarkAllDirty();
				Colliders.Flush();
				if (Colliders.PieceCount == 0)
					return "Could not enable dynamic physics: the plot has no collidable blocks yet (are the plot chunks created?).";

				Pose = rb.position + Origin.position;
				Yaw = rb.rotation.eulerAngles.y;
				// Block colliders use the same layer as vanilla terrain collision (layer 16). If the game's collision matrix
				// ignores layer 16 against itself, a dynamic sublevel can never land on terrain. Temporarily enable only this
				// pair while sublevel physics is active.
				AcquireTerrainCollisionMatrix();
				Physics.SyncTransforms();
				rb.isKinematic = false;
				rb.useGravity = true;
				rb.detectCollisions = true;
				rb.interpolation = RigidbodyInterpolation.Interpolate;
				rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
				rb.mass = Colliders.ComputeMass();
				rb.drag = 0.05f;
				rb.angularDrag = 0.1f;
				rb.constraints = RigidbodyConstraints.None;
				PhysicsEnabled = true;
				Physics.SyncTransforms();
				Log.Out("[SubLevel] Dynamic physics enabled. " + Colliders.Describe() + " root=" + rb.position + " origin=" + Origin.position + " centreOfMass=" + rb.worldCenterOfMass);
				return "Unity Rigidbody physics enabled using the block collision of the sublevel: " + Colliders.Boxes + " boxes + " + Colliders.ConvexPieces +
					" convex shape pieces, mass " + rb.mass.ToString("F0") + " kg. Gravity and dynamic collision response are active.";
			}

			PhysicsStaffController staff = GetComponent<PhysicsStaffController>();
			if (staff != null) staff.ReleaseGrab();
			Pose = rb.position + Origin.position;
			Yaw = rb.rotation.eulerAngles.y;
			tilt = Quaternion.identity;
			Velocity = Vector3.zero;
			YawRate = 0f;
			rb.velocity = Vector3.zero;
			rb.angularVelocity = Vector3.zero;
			rb.useGravity = false;
			rb.isKinematic = true;
			RestoreTerrainCollisionMatrix();
			rb.interpolation = RigidbodyInterpolation.Interpolate;
			PhysicsEnabled = false;
			if (Colliders != null) Colliders.RestoreEntityModelColliders();
			Physics.SyncTransforms();
			return "Unity Rigidbody physics disabled. The sublevel keeps its block collision in scripted (kinematic) mode.";
		}

		private void AcquireTerrainCollisionMatrix()
		{
			if (terrainSelfCollisionOverrideApplied) return;
			if (terrainOverrideUsers == 0)
			{
				terrainOriginalIgnored = Physics.GetIgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer);
				if (terrainOriginalIgnored) Physics.IgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer, false);
			}
			terrainOverrideUsers++;
			terrainSelfCollisionOverrideApplied = true;
			Log.Out("[SubLevel] TerrainCollision layer 16 original self-ignore=" + terrainOriginalIgnored + "; sublevels using override=" + terrainOverrideUsers + ".");
		}

		private void RestoreTerrainCollisionMatrix()
		{
			if (!terrainSelfCollisionOverrideApplied) return;
			terrainSelfCollisionOverrideApplied = false;
			terrainOverrideUsers = Mathf.Max(0, terrainOverrideUsers - 1);
			if (terrainOverrideUsers == 0 && terrainOriginalIgnored)
			{
				Physics.IgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer, true);
				Log.Out("[SubLevel] Restored TerrainCollision layer 16 self-ignore=true.");
			}
		}

		private void OnDestroy()
		{
			RestoreTerrainCollisionMatrix();
		}

		private void Update()
		{
			if (PlotSpace.Current == Plot && Time.realtimeSinceStartup >= nextPersist) { nextPersist = Time.realtimeSinceStartup + 5f; PlotSpace.SaveState(); }
			if (Colliders != null) Colliders.Tick(PhysicsEnabled, rb);
			TickRestore();
			TickGroundHold();
			Watchdog();
		}

		private void OnCollisionEnter(Collision collision)
		{
			if (!PhysicsEnabled || collision == null || collision.collider == null) return;
			ContactPoint contact = collision.contacts != null && collision.contacts.Length > 0 ? collision.contacts[0] : default(ContactPoint);
			Log.Out("[SubLevel] Rigidbody contact: other=" + collision.collider.name +
				" layer=" + collision.collider.gameObject.layer + "/" + LayerMask.LayerToName(collision.collider.gameObject.layer) +
				" tag=" + collision.collider.tag + " relativeVelocity=" + collision.relativeVelocity +
				" point=" + contact.point + " normal=" + contact.normal);
		}

		private void LateUpdate() { Resnap(); }

		/// <summary>Keeps every displayed plot chunk object under this root at its plot offset (vanilla moves and recycles them).</summary>
		internal void Resnap()
		{
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			ChunkCluster cc = world != null ? world.ChunkCache : null;
			if (cc == null || Plot == null) return;
			Transform root = transform;
			int attached = 0;
			foreach (long key in Plot.Keys)
			{
				ChunkGameObject cgo;
				if (!cc.DisplayedChunkGameObjects.TryGetValue(key, out cgo) || cgo == null) continue;
				Chunk c = cgo.GetChunk();
				if (c == null) continue;
				Transform t = cgo.transform;
				if (t.parent != root) t.SetParent(root, false);
				t.localPosition = new Vector3(c.X * 16 - Plot.Pivot.x, -Plot.Pivot.y, c.Z * 16 - Plot.Pivot.z);
				t.localRotation = Quaternion.identity;
				attached++;
			}
			RefreshOcclusion(cc);
			if (attached != attachedLogged)
			{
				Log.Out("[SubLevel] ChunkGameObjects attached under SubLevelRoot: " + attached + "/" + Plot.Keys.Count);
				attachedLogged = attached;
			}
		}

		private void RefreshOcclusion(ChunkCluster cc)
		{
			Vector3 pos = transform.position;
			Quaternion rot = transform.rotation;
			if ((pos - lastOccPos).sqrMagnitude < 0.0025f && Quaternion.Angle(rot, lastOccRot) < 0.25f) return;
			lastOccPos = pos; lastOccRot = rot;
			foreach (long key in Plot.Keys)
			{
				ChunkGameObject cgo;
				if (!cc.DisplayedChunkGameObjects.TryGetValue(key, out cgo) || cgo == null) continue;
				for (int i = 0; i < cgo.layers.Length; i++)
				{
					ChunkGameObjectLayer l = cgo.layers[i];
					if (l != null && l.m_ParentGO != null) Occludee.Refresh(l.m_ParentGO);
				}
			}
		}

		private void Watchdog()
		{
			if (Plot == null || Time.realtimeSinceStartup < nextWatch) return;
			nextWatch = Time.realtimeSinceStartup + 1f;
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			ChunkCluster cc = world != null ? world.ChunkCache : null;
			if (cc == null) return;
			int displayed = 0;
			var sb = new StringBuilder();
			foreach (long key in Plot.Keys)
			{
				Chunk c = cc.GetChunkSync(key);
				if (c == null) continue;
				// Do not clear isModified on visible plot chunks here: vanilla's save
				// pipeline uses it to persist block changes, tile entities, and damage.
				ChunkGameObject cgo;
				if (cc.DisplayedChunkGameObjects.TryGetValue(key, out cgo) && cgo != null && c.IsDisplayed && !c.NeedsCopying) { displayed++; continue; }
				sb.Append(" [" + c.X + "," + c.Z + " " + Wait(cc, c) + "]");
			}
			foreach (long key in Plot.SupportKeys)
			{
				Chunk c = cc.GetChunkSync(key);
				if (c != null) c.isModified = false;
			}
			if (displayed == Plot.Keys.Count)
			{
				if (!allDisplayedLogged)
				{
					allDisplayedLogged = true;
					Log.Out("[SubLevel] All " + displayed + " plot chunks displayed " + (Time.realtimeSinceStartup - Plot.CreatedAt).ToString("F1") + "s after creation.");
				}
				return;
			}
			allDisplayedLogged = false;
			float age = Time.realtimeSinceStartup - Plot.CreatedAt;
			if (age < 60f) Log.Warning("[SubLevel] t+" + age.ToString("F0") + "s " + displayed + "/" + Plot.Keys.Count + " displayed. Waiting:" + sb);
		}

		private static string Wait(ChunkCluster cc, Chunk c)
		{
			if (c.NeedsDecoration) return "decoration";
			if (c.NeedsLightCalculation) return "lighting";
			if (c.NeedsRegeneration) return "regeneration";
			if (c.NeedsCopying || c.InProgressCopying) return "copy-to-unity";
			if (!c.IsDisplayed) return "display(not in viewing set?)";
			return "collision/other(collGen=" + c.IsCollisionMeshGenerated + ")";
		}

		/// <summary>Full rotation (used when restoring a sublevel that physics left tilted).</summary>
		public void SetPose(Vector3 p, Quaternion rotation)
		{
			float yaw = rotation.eulerAngles.y;
			SetPose(p, yaw);
			tilt = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * rotation;
			Quaternion q = Quaternion.Euler(0f, yaw, 0f) * tilt;
			transform.rotation = q;
			if (rb != null) rb.rotation = q;
		}

		/// <summary>Called by the restore: hold the saved pose (kinematic) until every plot chunk is displayed with its block
		/// colliders and the ground under the pose has collision, then resume physics and motion. Without the wait a dynamic
		/// sublevel restored while the world is still streaming in falls through terrain that has no collider yet.</summary>
		public void RestoreWhenReady(bool physics, Vector3 velocity, float yawRate)
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
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			ChunkCluster cc = world != null ? world.ChunkCache : null;
			if (cc == null) return;
			bool timedOut = Time.realtimeSinceStartup - pendingSince > 90f;
			if (!timedOut)
			{
				foreach (long key in Plot.Keys)
					if (!cc.DisplayedChunkGameObjects.ContainsKey(key)) return;
				if (Colliders == null || Colliders.PieceCount == 0) return;
				Vector3 w = Pose;
				Chunk ground = cc.GetChunkSync(World.toChunkXZ(Mathf.FloorToInt(w.x)), World.toChunkXZ(Mathf.FloorToInt(w.z)));
				if (ground == null || !ground.IsCollisionMeshGenerated || !cc.DisplayedChunkGameObjects.ContainsKey(ground.Key)) return;
			}
			RestorePending = false;
			string result = "";
			if (PendingPhysics) result = SetPhysics(true);
			if (!PendingPhysics || PhysicsEnabled) RestoreMotion(pendingVelocity, pendingYawRate);
			Log.Out("[SubLevel] " + Plot.Id + " restore complete after " + (Time.realtimeSinceStartup - pendingSince).ToString("F1") + " s" +
				(timedOut ? " (TIMED OUT waiting for chunks/ground)" : "") + (PendingPhysics ? ": " + result : "."));
			PlotSpace.SaveState();
		}

		// --- ground hold --------------------------------------------------------------------------------------------------
		// A dynamic sublevel keeps simulating when nobody is near it, but the server unloads the terrain there (and with it
		// the collision). Multiplayer rig mp3 (11-11): a dropped sublevel left alone while its player was away fell to
		// y -4760. So while any terrain chunk under the footprint is missing or has no collision, the body is frozen
		// (kinematic, velocities kept) and resumes as soon as the ground is back - as vanilla entities do in unloaded areas.
		public bool GroundHeld { get; private set; }
		private Vector3 heldVelocity, heldAngular;
		private float nextGroundCheck, nextHeldLog;

		private void TickGroundHold()
		{
			if (!PhysicsEnabled || RestorePending || rb == null || Plot == null) { GroundHeld = false; return; }
			if (Time.time < nextGroundCheck) return;
			nextGroundCheck = Time.time + 0.25f;
			string why = GroundMissing();
			bool ready = why == null;
			if (!ready && GroundHeld && Time.time >= nextHeldLog) { nextHeldLog = Time.time + 10f; Log.Out("[SubLevel] " + Plot.Id + " still holding: " + why); }
			if (!ready && !GroundHeld)
			{
				nextHeldLog = Time.time + 10f;
				heldVelocity = rb.velocity; heldAngular = rb.angularVelocity;
				rb.isKinematic = true;
				GroundHeld = true;
				Log.Out("[SubLevel] " + Plot.Id + " ground not loaded under " + Pose + " (" + why + "): holding the body still until it is.");
			}
			else if (ready && GroundHeld)
			{
				rb.isKinematic = false;
				rb.velocity = heldVelocity; rb.angularVelocity = heldAngular;
				GroundHeld = false;
				Log.Out("[SubLevel] " + Plot.Id + " ground is back under " + Pose + ": physics resumed.");
			}
		}

		/// <summary>Null when every terrain chunk under the body's colliders (plus 4 m) is loaded, has collision and is in the
		/// scene; otherwise the first chunk that is not, and why.</summary>
		private string GroundMissing()
		{
			World world = GameManager.Instance != null ? GameManager.Instance.World : null;
			ChunkCluster cc = world != null ? world.ChunkCache : null;
			if (cc == null) return "no chunk cluster";
			// Only the chunks under what can actually touch the ground: the body's enabled colliders, plus 4 m. (The whole plot
			// footprint plus a ring was too strict: a dedicated server keeps collision only near players, so a corner chunk
			// 2 chunks out kept flipping between unloaded and no-collision and the body never got released - rig mp5.)
			bool any = false;
			Bounds b = new Bounds();
			foreach (Collider col in GetComponentsInChildren<Collider>())
			{
				if (!col.enabled || col.isTrigger) continue;
				if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
			}
			if (!any) return "no colliders";
			Vector3 lo = b.min + Origin.position - new Vector3(4f, 0f, 4f), hi = b.max + Origin.position + new Vector3(4f, 0f, 4f);
			int x0 = World.toChunkXZ(Mathf.FloorToInt(lo.x)), x1 = World.toChunkXZ(Mathf.FloorToInt(hi.x));
			int z0 = World.toChunkXZ(Mathf.FloorToInt(lo.z)), z1 = World.toChunkXZ(Mathf.FloorToInt(hi.z));
			for (int cx = x0; cx <= x1; cx++)
				for (int cz = z0; cz <= z1; cz++)
				{
					Chunk c = cc.GetChunkSync(cx, cz);
					if (c == null) return "chunk " + cx + "," + cz + " not loaded";
					if (!c.IsCollisionMeshGenerated) return "chunk " + cx + "," + cz + " has no collision mesh";
					if (!cc.DisplayedChunkGameObjects.ContainsKey(c.Key)) return "chunk " + cx + "," + cz + " not in the scene";
				}
			return null;
		}

		public void SetPose(Vector3 p, float yaw)
		{
			tilt = Quaternion.identity;
			Pose = p; Yaw = yaw;
			Vector3 local = p - Origin.position;
			Quaternion q = Quaternion.Euler(0f, yaw, 0f);
			transform.SetPositionAndRotation(local, q);
			if (rb != null)
			{
				rb.position = local;
				rb.rotation = q;
				if (PhysicsEnabled) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
			}
		}

		/// <summary>Applies saved velocities when a sublevel is restored from sublevels.xml. In dynamic
		/// mode the Rigidbody owns velocity, so it must be pushed into the body (FixedUpdate mirrors it back).</summary>
		/// <summary>Client copy (multiplayer): follow the server's pose. Between packets FixedUpdate carries it on with the
		/// server's velocity, so a packet usually only corrects a little; a big jump (first packet, teleport) snaps.</summary>
		internal void ApplyRemote(Vector3 pos, Quaternion rot, Vector3 vel, float yawRate, bool snap)
		{
			Velocity = vel; YawRate = yawRate;
			float yaw = rot.eulerAngles.y;
			if (snap || (pos - Pose).sqrMagnitude > 4f) { SetPose(pos, rot); return; }
			tilt = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * rot;
			Pose = pos; Yaw = yaw;
		}

		public void RestoreMotion(Vector3 velocity, float yawRateDegPerSec)
		{
			Velocity = velocity; YawRate = yawRateDegPerSec;
			if (PhysicsEnabled && rb != null)
			{
				rb.velocity = velocity;
				rb.angularVelocity = new Vector3(0f, yawRateDegPerSec * Mathf.Deg2Rad, 0f);
			}
		}

		public void DetachAll()
		{
			if (PhysicsEnabled) SetPhysics(false);
			PhysicsStaffController staff = GetComponent<PhysicsStaffController>();
			if (staff != null) staff.ReleaseGrab();
			if (Colliders != null) { Colliders.Dispose(); Colliders = null; }
			for (int i = transform.childCount - 1; i >= 0; i--) transform.GetChild(i).SetParent(null, false);
		}
	}
}
