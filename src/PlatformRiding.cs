using HarmonyLib;
using UnityEngine;

namespace SubLevelPhysics
{
	/// <summary>
	/// Makes the local player ride sublevels they stand on (move + yaw with them).
	///
	/// Design: the game's vehicle attach path (Entity.AttachToEntity) turns the player into a seated passenger
	/// (Player.Driving, walking disabled), which is wrong for walking around a sublevel. UFPS' vp_FPController
	/// has the same idea for walkable moving platforms - carry the player in the platform's local space, rotate the
	/// camera yaw by the platform's yaw delta, hand over the platform's momentum on leaving - but it only arms itself
	/// for ground on layer 28, which 7DTD's ground probe mask never hits. So the stock machinery is dormant.
	/// We arm it ourselves: when the ground under the player belongs to a sublevel, point the controller's m_Platform
	/// at that sublevel's root. The stock FixedMove / UpdatePlatformMove / ground-loss code does the rest.
	/// No collider layers are touched, so sublevel collision is unaffected.
	/// </summary>
	internal static class PlatformRiding
	{
		/// <summary>Master switch ('sl ride true|false'), for A/B testing in game.</summary>
		public static bool Enabled = true;
		/// <summary>The sublevel the local player is currently standing on, if any.</summary>
		public static SublevelPlot Ridden { get; private set; }

		public static void Reset() { Ridden = null; }

		internal static void Board(SublevelPlot p)
		{
			if (Ridden == p) return;
			Ridden = p;
			Log.Out("[SubLevel] Player boarded sublevel " + (p != null ? p.Id : "?") + ".");
		}

		internal static void Leave()
		{
			if (Ridden == null) return;
			Log.Out("[SubLevel] Player left sublevel " + Ridden.Id + ".");
			Ridden = null;
		}
	}

	/// <summary>Runs inside vp_FPController.FixedUpdate right after the ground probe and right before
	/// UpdatePlatformMove, so the platform is armed in the same tick the player lands.</summary>
	[HarmonyPatch(typeof(vp_FPController), "UpdateCollisions")]
	internal static class SubLevelPlatformCollisionsPatch
	{
		private static void Postfix(vp_FPController __instance)
		{
			SublevelPlot plot = null;
			if (PlatformRiding.Enabled && PlotSpace.Count > 0)
			{
				Transform ground = __instance.GroundTransform;
				if (ground != null) plot = PlotSpace.FindByTransform(ground);
			}

			if (plot != null && plot.Root != null)
			{
				Transform root = plot.Root.transform;
				if (__instance.m_Platform != root)
				{
					__instance.m_Platform = root;
					__instance.m_LastPlatformAngle = root.eulerAngles.y;
					__instance.m_LastPlatformPos = root.position;
				}
				PlatformRiding.Board(plot);
				return;
			}

			// Not standing on a sublevel (or riding disabled). Drop a sublevel platform the stock code did not clear.
			Transform current = __instance.m_Platform;
			if (current != null && PlotSpace.FindByTransform(current) != null) { __instance.m_Platform = null; current = null; }
			if (current == null && __instance.m_PositionOnPlatform != Vector3.zero) __instance.m_PositionOnPlatform = Vector3.zero;
			if (current == null) PlatformRiding.Leave();
		}
	}

	/// <summary>7DTD periodically shifts the world origin (Origin.Reposition). vp_FPController.Reposition shifts the
	/// player but not m_LastPlatformPos, which the stock code later uses to compute the momentum handed over when
	/// the player leaves the platform. Shift it too, or a jump right after an origin shift would launch the player.</summary>
	[HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.Reposition))]
	internal static class SubLevelPlatformRepositionPatch
	{
		private static void Postfix(vp_FPController __instance, Vector3 _deltaVec)
		{
			if (__instance.m_Platform != null) __instance.m_LastPlatformPos += _deltaVec;
		}
	}
}
