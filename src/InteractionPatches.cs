using HarmonyLib;
using UnityEngine;

namespace SubLevelPhysics
{
	// Everything below exists because the plot's chunks live at real block coordinates (e.g. x~64000) but are
	// DRAWN AND COLLIDED at the sublevel's pose near the player. Vanilla assumes a chunk is drawn where it lives, so
	// every "where is that block?" path needs to be told about the transform. Plot space == real block coordinates.

	/// <summary>Targeting. Voxel.raycastNew does a Physics ray, then terrainMeshHit() turns the hit point into a block
	/// position as if the collider were ordinary world terrain. For a plot collider that gave a block position next to
	/// the player in the real world (so placing/breaking hit the wrong place). We map the hit point, ray direction
	/// and surface normal into plot space first, so vanilla resolves the correct plot block and face.</summary>
	[HarmonyPatch(typeof(Voxel), "terrainMeshHit")]
	internal static class VoxelTerrainMeshHitPatch
	{
		internal struct HitState { public bool Active; public Vector3 Normal; }

		private static void Prefix(ref Vector3 dirNormalized, ref Vector3 phsxHitpPoint, out HitState __state)
		{
			__state = default(HitState);
			Plot p = PlotSpace.Current;
			if (p == null || p.Rb == null || p.RootGo == null) return;
			Collider col = Voxel.phyxRaycastHit.collider;
			if (col == null || !col.transform.IsChildOf(p.RootGo.transform)) return;

			Quaternion inv = Quaternion.Inverse(p.Rb.rotation);
			RaycastHit h = Voxel.phyxRaycastHit;
			__state.Active = true;
			__state.Normal = h.normal;
			h.normal = inv * h.normal;               // calcBestNormalToRaycastHit reads this static
			Voxel.phyxRaycastHit = h;
			phsxHitpPoint = p.UnityToPlotPhysics(phsxHitpPoint - Origin.position);
			dirNormalized = inv * dirNormalized;
		}

		private static void Postfix(HitState __state)
		{
			if (!__state.Active) return;
			RaycastHit h = Voxel.phyxRaycastHit;
			h.normal = __state.Normal;               // restore: the rest of raycastNew works in real coordinates
			Voxel.phyxRaycastHit = h;
		}
	}

	/// <summary>Placement. BlockPlacement.Result takes its position from hit.pos, which raycastNew leaves in real world
	/// coordinates; bring it into plot space so it agrees with hit.blockPos.</summary>
	[HarmonyPatch(typeof(BlockPlacement), nameof(BlockPlacement.OnPlaceBlock))]
	internal static class BlockPlacementPosPatch
	{
		private static void Prefix(ref HitInfoDetails _hitInfo)
		{
			Plot p = PlotSpace.Current;
			if (p == null || p.Rb == null) return;
			if (!p.ContainsXZ(_hitInfo.blockPos.x, _hitInfo.blockPos.z)) return;
			Vector3 center = new Vector3(_hitInfo.blockPos.x + 0.5f, _hitInfo.blockPos.y + 0.5f, _hitInfo.blockPos.z + 0.5f);
			if ((_hitInfo.pos - center).sqrMagnitude < 100f * 100f) return; // already plot space
			_hitInfo.pos = p.UnityToPlotPhysics(_hitInfo.pos - Origin.position);
		}
	}

	/// <summary>Placement validity. World.CanPlaceBlockAt rejects anything outside the playable map (the plot is thousands
	/// of blocks outside it), which turned the placement marker red and refused every placement.</summary>
	[HarmonyPatch(typeof(World), nameof(World.InBoundsForPlayersPercent), new[] { typeof(Vector3) })]
	internal static class InBoundsPatch
	{
		private static bool Prefix(Vector3 _pos, ref float __result)
		{
			Plot p = PlotSpace.Current;
			if (p == null || !p.ContainsXZ(Mathf.FloorToInt(_pos.x), Mathf.FloorToInt(_pos.z))) return true;
			__result = 1f;
			return false;
		}
	}

	/// <summary>On fixed-size maps ChunkCluster.SetBlock GROWS the cluster bounds and fills the gap with chunks whenever a
	/// block is set within 2 chunks of (or beyond) the edge. A plot is far beyond the edge, so the first placement would
	/// try to create every chunk between the map and the plot. Temporarily clear IsFixedSize for plot blocks only.
	/// Also tells the block-collider builder which chunk changed, so the sublevel's collision follows block edits.</summary>
	[HarmonyPatch(typeof(ChunkCluster), nameof(ChunkCluster.SetBlock), new[]
	{
		typeof(Vector3i), typeof(bool), typeof(BlockValue), typeof(bool), typeof(sbyte),
		typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(int)
	})]
	internal static class ClusterSetBlockPatch
	{
		internal struct SetState { public bool RestoreFixed; public long ChunkKey; public bool InPlot; }

		private static void Prefix(ChunkCluster __instance, Vector3i _pos, out SetState __state)
		{
			__state = default(SetState);
			Plot p = PlotSpace.Current;
			if (p == null || !p.ContainsXZ(_pos.x, _pos.z)) return;
			__state.InPlot = true;
			__state.ChunkKey = WorldChunkCache.MakeChunkKey(_pos.x >> 4, _pos.z >> 4);
			if (!__instance.IsFixedSize) return;
			__instance.IsFixedSize = false;
			__state.RestoreFixed = true;
		}

		private static void Finalizer(ChunkCluster __instance, SetState __state)
		{
			if (__state.RestoreFixed) __instance.IsFixedSize = true;
			if (!__state.InPlot) return;
			Plot p = PlotSpace.Current;
			if (p != null && p.Driver != null) p.Driver.MarkChunkCollisionDirty(__state.ChunkKey);
		}
	}

	/// <summary>Block logic: entity-model blocks (workbench, crates, forges, doors...). Their prefab colliders are tagged T_Block and
	/// the game maps a hit back to the block via GameUtils.FindMasterBlockForEntityModelBlock, which finds the chunk from the hit POINT.
	/// For a plot collider that point is where the sublevel is drawn, not where the block lives, so no chunk matched and nothing
	/// could be activated. Map the hit point and direction into plot space first (same mapping as terrainMeshHit).</summary>
	[HarmonyPatch(typeof(GameUtils), nameof(GameUtils.FindMasterBlockForEntityModelBlock))]
	internal static class FindMasterBlockPlotPatch
	{
		private static void Prefix(ref Vector3 _dirNormalized, ref Vector3 _hitPointPos, Transform _hitTransform)
		{
			Plot p = PlotSpace.Current;
			if (p == null || p.Rb == null || p.RootGo == null || _hitTransform == null) return;
			if (!_hitTransform.IsChildOf(p.RootGo.transform)) return;
			Quaternion inv = Quaternion.Inverse(p.Rb.rotation);
			_hitPointPos = p.UnityToPlotPhysics(_hitPointPos - Origin.position);
			_dirNormalized = inv * _dirNormalized;
		}
	}

	/// <summary>Block logic: tile entities (loot containers, workstations, ...) report their world centre from their block's PLOT
	/// coordinates. Loot / workstation windows close themselves when the player is more than ~30 m from that point, which with
	/// plot coordinates (tens of km away) closed every container the instant it opened. Report where the block is drawn now.</summary>
	[HarmonyPatch(typeof(TileEntity), nameof(TileEntity.ToWorldCenterPos))]
	internal static class TileEntityCenterPatch
	{
		private static void Postfix(ref Vector3 __result)
		{
			Plot p = PlotSpace.Current;
			if (p == null || p.Rb == null || p.RootGo == null || __result == Vector3.zero) return;
			if (!p.ContainsXZ(Mathf.FloorToInt(__result.x), Mathf.FloorToInt(__result.z))) return;
			__result = p.PlotToWorldRender(__result);
		}
	}

	/// <summary>Open/close/use sounds are played at the block's plot-space position, ~64 km from the player (silent). Redirect to the
	/// drawn position.</summary>
	[HarmonyPatch(typeof(Audio.Manager), nameof(Audio.Manager.BroadcastPlayByLocalPlayer))]
	internal static class BroadcastSoundPlotPatch
	{
		private static void Prefix(ref Vector3 position)
		{
			Plot p = PlotSpace.Current;
			if (p == null || p.Rb == null || p.RootGo == null) return;
			if (!p.ContainsXZ(Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.z))) return;
			position = p.PlotToWorldRender(position);
		}
	}

	/// <summary>Stability. The plot floats at y=100 with nothing under it, so vanilla stability treats its blocks as
	/// unsupported and turns them into falling blocks as soon as one is placed or broken.</summary>
	[HarmonyPatch(typeof(World), nameof(World.AddFallingBlock))]
	internal static class FallingBlockPatch
	{
		private static bool Prefix(Vector3i _blockPos)
		{
			Plot p = PlotSpace.Current;
			return p == null || !p.ContainsXZ(_blockPos.x, _blockPos.z);
		}
	}

	/// <summary>Placement / focus marker. RenderDisplacedCube positions its wireframe and block preview at the block's
	/// plot-space coordinates. When the focused block is in the plot, move them to where that block is actually drawn
	/// (the sublevel's rendered pose) and rotate them with it.</summary>
	[HarmonyPatch(typeof(RenderDisplacedCube), nameof(RenderDisplacedCube.update0))]
	internal static class FocusMarkerPatch
	{
		private static readonly AccessTools.FieldRef<RenderDisplacedCube, Transform> WireRef =
			AccessTools.FieldRefAccess<RenderDisplacedCube, Transform>("transformWireframeCube");
		private static readonly AccessTools.FieldRef<RenderDisplacedCube, Transform> PreviewRef =
			AccessTools.FieldRefAccess<RenderDisplacedCube, Transform>("transformBlockPreview");

		private static bool wasApplied;

		private static void Postfix(RenderDisplacedCube __instance, Vector3i _focusBlockPos)
		{
			Plot p = PlotSpace.Current;
			bool inPlot = p != null && p.RootGo != null && p.ContainsXZ(_focusBlockPos.x, _focusBlockPos.z);
			Transform wire = WireRef(__instance);
			Transform preview = PreviewRef(__instance);

			if (!inPlot)
			{
				// vanilla never resets the preview's rotation, so undo ours when the cursor leaves the sublevel
				if (wasApplied && preview != null) preview.rotation = Quaternion.identity;
				wasApplied = false;
				return;
			}

			Quaternion rot = p.RootGo.transform.rotation;
			if (wire != null)
			{
				wire.position = p.PlotToUnityRender(wire.position + Origin.position);
				wire.rotation = rot * wire.rotation;      // vanilla re-sets the wireframe rotation every frame
			}
			if (preview != null)
			{
				preview.position = p.PlotToUnityRender(preview.position + Origin.position);
				preview.rotation = rot;                   // vanilla leaves it at identity, so set (not multiply)
			}
			wasApplied = true;
		}
	}
}
