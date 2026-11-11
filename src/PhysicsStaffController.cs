using UnityEngine;

namespace SubLevelPhysics
{
	/// <summary>
	/// Creative-mode held-item grabber. The staff does not move the plot transform directly:
	/// it moves a kinematic anchor and a SpringJoint applies the physical constraint.
	/// </summary>
	public sealed class PhysicsStaffController : MonoBehaviour
	{
		private const string StaffItemName = "toolPhysicsStaff";
		private SpringJoint grabJoint;
		private Rigidbody anchorBody;
		private GameObject anchorObject;
		private float grabDistance = 8f;

		private void Update()
		{
			Plot plot = PlotSpace.Current;
			EntityPlayerLocal player = GameManager.Instance != null && GameManager.Instance.World != null
				? GameManager.Instance.World.GetPrimaryPlayer() : null;

			// The staff is a development tool exposed in CreativeMode=All. Do not gate it
			// on IsAdmin: single-player creative users and server hosts can have different
			// admin flags even though they are legitimately testing their own plot.
			// Each sublevel has its own staff component; only the selected sublevel's component may act.
			bool authorized = plot != null && plot.RootGo != null && plot.RootGo.transform == transform && plot.Driver != null && plot.Driver.PhysicsEnabled &&
				player != null && IsHoldingStaff(player);

			if (!authorized)
			{
				ReleaseGrab();
				return;
			}

			Camera cam = player.playerCamera;
			if (cam == null) cam = Camera.main;
			if (cam == null) { ReleaseGrab(); return; }

			if (Input.GetMouseButtonDown(0) && grabJoint == null)
				TryBeginGrab(plot, cam);

			if (grabJoint != null)
			{
				if (Input.GetMouseButtonUp(0))
				{
					ReleaseGrab();
					return;
				}
				grabDistance = Mathf.Clamp(grabDistance + Input.mouseScrollDelta.y * 0.75f, 2f, 40f);
				Ray ray = cam.ScreenPointToRay(Input.mousePosition);
				Vector3 target = ray.GetPoint(grabDistance);
				if (anchorBody != null) anchorBody.MovePosition(target);
			}
		}

		private static bool IsHoldingStaff(EntityPlayerLocal player)
		{
			if (player.inventory == null) return false;
			ItemClass item = player.inventory.holdingItemItemValue.ItemClass;
			return item != null && item.Name == StaffItemName;
		}

		private void TryBeginGrab(Plot plot, Camera cam)
		{
			Ray ray = cam.ScreenPointToRay(Input.mousePosition);
			RaycastHit hit;
			if (!Physics.Raycast(ray, out hit, 500f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;
			if (hit.collider == null || !hit.collider.transform.IsChildOf(transform)) return;

			anchorObject = new GameObject("SubLevelPhysicsStaffAnchor");
			anchorObject.transform.position = hit.point;
			anchorBody = anchorObject.AddComponent<Rigidbody>();
			anchorBody.isKinematic = true;
			anchorBody.useGravity = false;

			grabJoint = gameObject.AddComponent<SpringJoint>();
			grabJoint.autoConfigureConnectedAnchor = false;
			grabJoint.anchor = transform.InverseTransformPoint(hit.point);
			grabJoint.connectedBody = anchorBody;
			grabJoint.connectedAnchor = Vector3.zero;
			grabJoint.spring = 1400f;
			grabJoint.damper = 120f;
			grabJoint.minDistance = 0f;
			grabJoint.maxDistance = 0f;
			grabJoint.tolerance = 0.025f;
			grabJoint.enableCollision = false;
			grabDistance = Mathf.Clamp(Vector3.Distance(cam.transform.position, hit.point), 2f, 40f);
			Log.Out("[SubLevel] Physics Staff grabbed sublevel using a SpringJoint constraint.");
		}

		public void ReleaseGrab()
		{
			if (grabJoint != null) Destroy(grabJoint);
			grabJoint = null;
			if (anchorObject != null) Destroy(anchorObject);
			anchorObject = null;
			anchorBody = null;
		}

		private void OnDisable() { ReleaseGrab(); }
		private void OnDestroy() { ReleaseGrab(); }
	}
}
