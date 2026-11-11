using HarmonyLib;
using UnityEngine;

namespace SubLevelPhysics
{
	// A pooled ChunkGameObject that was re-parented under a sub-level root must be released
	// before vanilla reuses it for another chunk (SetChunk resets its localPosition).
	[HarmonyPatch(typeof(ChunkGameObject), nameof(ChunkGameObject.SetChunk))]
	internal static class CgoSetChunkPatch
	{
		private static void Prefix(ChunkGameObject __instance)
		{
			PlotSpace.ReleaseIfAttached(__instance);
		}
	}

	/// <summary>ChunkGameObject.CheckLODs measures player-to-chunk distance with the chunk's REAL block coordinates
	/// (plot space, ~x 64000). To vanilla every plot chunk is thousands of metres away, so it switches off the decal
	/// mesh (block-damage cracks) and the grass mesh. Skip the check for plot chunks and keep decals on.</summary>
	[HarmonyPatch(typeof(ChunkGameObject), nameof(ChunkGameObject.CheckLODs))]
	internal static class CgoCheckLodsPatch
	{
		private static bool Prefix(ChunkGameObject __instance)
		{
			Chunk c = __instance.GetChunk();
			if (c == null) return true;
			Plot p = PlotSpace.FindByChunk(c.X * 16, c.Z * 16);
			if (p == null || !p.IsPlotKey(c.Key)) return true;
			for (int i = 0; i < __instance.layers.Length; i++)
			{
				ChunkGameObjectLayer l = __instance.layers[i];
				if (l == null) continue;
				GameObject go = l.m_MeshesGO[MeshDescription.MESH_DECALS];
				if (go != null && !go.activeSelf) go.SetActive(true);
			}
			return false;
		}
	}
	/// <summary>Global/triplanar UVs on cube-like BlockShapeNew meshes are evaluated against the
	/// block's absolute world position and world axes. A sublevel is subsequently moved/rotated as
	/// one Unity transform, so those UVs remain world-anchored and the cubes look like windows into
	/// a stationary repeating texture. Force the Local mesh purpose only while the game is building
	/// faces belonging to this plot. BlockShapeNew uses that purpose to bypass global UV projection
	/// and bake ordinary face UVs into the mesh; non-plot world chunks are untouched. Shapes already
	/// using local UVs are unchanged because the override only affects the global-UV branch.
	/// </summary>
	[HarmonyPatch(typeof(BlockShapeNew), nameof(BlockShapeNew.renderFace), new[]
	{
		typeof(Vector3i), typeof(BlockValue), typeof(Vector3), typeof(BlockFace), typeof(Vector3[]),
		typeof(LightingAround), typeof(TextureFullArray), typeof(VoxelMesh[]), typeof(BlockShape.MeshPurpose)
	})]
	internal static class SubLevelLocalCubeUvPatch
	{
		private static void Prefix(Vector3i _chunkPos, ref BlockShape.MeshPurpose _purpose)
		{
			Plot p = PlotSpace.FindByChunk(_chunkPos.x, _chunkPos.z);
			if (p == null) return;
			_purpose = BlockShape.MeshPurpose.Local;
		}
	}

}
