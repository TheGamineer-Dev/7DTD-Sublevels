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
		private Rigidbody rb;
		internal PlotBlockColliders Colliders;

		private float nextWatch;
		private Vector3 lastOccPos = new Vector3(float.MaxValue, 0f, 0f);
		private Quaternion lastOccRot = Quaternion.identity;
		private int attachedLogged;
		private bool allDisplayedLogged;
		// 7DTD's chunk collision meshes use layer 16 (TerrainCollision). Some game
		// builds configure that layer to ignore itself. A dynamic sublevel hull copied
		// onto the same layer would then pass straight through ordinary terrain.
		private const int TerrainCollisionLayer = 16;
		private bool terrainSelfCollisionWasIgnored;
		private bool terrainSelfCollisionOverrideApplied;

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
			rb.MoveRotation(Quaternion.Euler(0f, Yaw, 0f));
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
				terrainSelfCollisionWasIgnored = Physics.GetIgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer);
				if (terrainSelfCollisionWasIgnored)
				{
					Physics.IgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer, false);
					terrainSelfCollisionOverrideApplied = true;
				}
				Log.Out("[SubLevel] TerrainCollision layer 16 self-ignore=" + terrainSelfCollisionWasIgnored + "; temporary self-collision override=" + terrainSelfCollisionOverrideApplied + ".");
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

		private void RestoreTerrainCollisionMatrix()
		{
			if (!terrainSelfCollisionOverrideApplied) return;
			Physics.IgnoreLayerCollision(TerrainCollisionLayer, TerrainCollisionLayer, terrainSelfCollisionWasIgnored);
			Log.Out("[SubLevel] Restored TerrainCollision layer 16 self-ignore=" + terrainSelfCollisionWasIgnored + ".");
			terrainSelfCollisionOverrideApplied = false;
		}

		private void OnDestroy()
		{
			RestoreTerrainCollisionMatrix();
		}

		private void Update()
		{
			if (Colliders != null) Colliders.Tick(PhysicsEnabled, rb);
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

		private void LateUpdate()
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
				c.isModified = false;
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

		public void SetPose(Vector3 p, float yaw)
		{
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
