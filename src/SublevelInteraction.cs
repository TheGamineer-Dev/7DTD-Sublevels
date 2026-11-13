using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubLevelPhysics
{
    /// <summary>
    /// Player interaction with sublevel blocks (alpha, single-player / listen-server host).
    ///
    /// Why this exists: vanilla targeting casts a physics ray, then resolves the hit to a block by looking up the hit
    /// POSITION in the player's world ChunkCluster. A sublevel is a moved/rotated Unity transform, so that lookup finds
    /// air (or terrain behind it). The blocks really live in the Sublevels storage world at logical cell coordinates.
    ///
    /// Design: everything is derived from three things that survive WorldRayHitInfo copies: hitInfo.hitCollider (which
    /// sublevel), hitInfo.hit.pos (world hit point) and hitInfo.hit.blockFace (face normal). Resolve() turns those into
    /// logical block coordinates; break/place then go through SublevelStorageApi and rebuild the projection.
    /// </summary>
    internal static class SublevelInteraction
    {
        internal static bool Enabled = true;

        /// <summary>Hit info that vanilla code would otherwise resolve against the primary world.</summary>
        internal struct Target
        {
            public SublevelPlot Plot;
            public int X, Y, Z;          // block that was hit (logical coordinates, Y in storage space)
            public int AdjX, AdjY, AdjZ; // empty neighbour on the hit face (placement target)
            public BlockFace Face;
        }

        internal static bool TryResolve(WorldRayHitInfo hitInfo, out Target t)
        {
            t = default(Target);
            if (!Enabled || hitInfo == null || hitInfo.hitCollider == null || PlotSpace.Count == 0) return false;
            SublevelPlot plot = PlotSpace.FindByTransform(hitInfo.hitCollider.transform);
            if (plot == null || plot.Root == null) return false;

            Transform root = plot.Root.transform;
            Vector3 local = root.InverseTransformPoint(hitInfo.hit.pos - Origin.position);
            Vector3 n = FaceNormal(hitInfo.hit.blockFace);
            Vector3 inside = local - n * 0.02f;
            Vector3 outside = local + n * 0.02f;
            t.Plot = plot;
            t.Face = hitInfo.hit.blockFace;
            t.X = Mathf.RoundToInt(inside.x);
            t.Y = Mathf.RoundToInt(inside.y) + PlotSpace.PivotY;
            t.Z = Mathf.RoundToInt(inside.z);
            t.AdjX = Mathf.RoundToInt(outside.x);
            t.AdjY = Mathf.RoundToInt(outside.y) + PlotSpace.PivotY;
            t.AdjZ = Mathf.RoundToInt(outside.z);
            return true;
        }

        private static Vector3 FaceNormal(BlockFace f)
        {
            switch (f)
            {
                case BlockFace.Top: return Vector3.up;
                case BlockFace.Bottom: return Vector3.down;
                case BlockFace.North: return Vector3.forward;
                case BlockFace.South: return Vector3.back;
                case BlockFace.East: return Vector3.right;
                default: return Vector3.left; // West
            }
        }

        private static BlockFace FaceFromLocalNormal(Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ay >= ax && ay >= az) return n.y >= 0f ? BlockFace.Top : BlockFace.Bottom;
            if (ax >= az) return n.x >= 0f ? BlockFace.East : BlockFace.West;
            return n.z >= 0f ? BlockFace.North : BlockFace.South;
        }

        internal static int FloorDiv(int a, int b) { int q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }

        // ---------------------------------------------------------------- raycast

        /// <summary>
        /// Called from the Voxel.raycastNew prefix. Returns true (and fills Voxel.voxelRayHitInfo) when the nearest physics
        /// hit is a sublevel collider, so vanilla never tries to resolve it against the primary world.
        /// </summary>
        internal static bool TryHandleRay(Ray ray, float distance, int layerMask, float sphereRadius)
        {
            if (!Enabled || PlotSpace.Count == 0 || Voxel.voxelRayHitInfo == null) return false;
            Ray ray2 = new Ray(ray.origin - Origin.position, ray.direction);
            RaycastHit h;
            bool hit = sphereRadius > 0.01f ? Physics.SphereCast(ray2, sphereRadius, out h, distance, layerMask) : Physics.Raycast(ray2, out h, distance, layerMask);
            if (!hit || h.collider == null) return false;
            SublevelPlot plot = PlotSpace.FindByTransform(h.collider.transform);
            if (plot == null || plot.Root == null) return false;

            Vector3 world = h.point + Origin.position;
            Vector3 localNormal = plot.Root.transform.InverseTransformDirection(h.normal);
            WorldRayHitInfo info = Voxel.voxelRayHitInfo;
            info.Clear();
            info.ray = ray;
            info.hitCollider = h.collider;
            info.hitTriangleIdx = h.triangleIndex;
            info.transform = h.collider.transform;
            info.tag = h.collider.transform.tag;
            info.bHitValid = true;
            info.hit.pos = world;
            info.hit.distanceSq = (ray.origin - world).sqrMagnitude;
            info.hit.blockFace = FaceFromLocalNormal(localNormal);
            // blockPos / lastBlockPos / voxelData stay at their cleared (zero / air) values on purpose: vanilla consumers
            // then see "nothing to do" in the primary world. Sublevel-aware handlers read hitCollider + pos + face.
            info.fmcHit = info.hit;
            return true;
        }

        // ---------------------------------------------------------------- break

        internal static void DamageBlock(Target t, float rawDamage, DamageMultiplier multiplier, int attackerId)
        {
            string id = t.Plot.Id;
            BlockValue bv;
            if (!SublevelStorageApi.TryGetBlock(id, t.X, t.Y, t.Z, out bv) || bv.isair) return;
            int px = t.X, py = t.Y, pz = t.Z;
            if (bv.ischild && bv.Block != null)
            {
                Vector3i parent = bv.Block.multiBlockPos.GetParentPos(new Vector3i(t.X, t.Y, t.Z), bv);
                px = parent.x; py = parent.y; pz = parent.z;
                if (!SublevelStorageApi.TryGetBlock(id, px, py, pz, out bv) || bv.isair) return;
            }
            Block block = bv.Block;
            if (block == null || block.blockMaterial == null || !block.blockMaterial.CanDestroy) return;

            EntityAlive attacker = GameManager.Instance.World.GetEntity(attackerId) as EntityAlive;
            float dmg = rawDamage * (multiplier != null ? multiplier.Get(block.blockMaterial.DamageCategory) : 1f);
            if (attacker != null) dmg *= attacker.GetBlockDamageScale(false);
            int add = Mathf.Max(1, Mathf.RoundToInt(dmg / Mathf.Max(block.GetHardness(), 0.1f)));

            int newDamage = bv.damage + add;
            try
            {
                if (newDamage >= block.MaxDamage)
                {
                    SublevelStorageApi.SetBlock(id, px, py, pz, BlockValue.Air);
                    SublevelStorageApi.RecycleEmptyCell(id, FloorDiv(px, 16), FloorDiv(pz, 16));
                    GiveDrops(block, attacker as EntityPlayerLocal);
                    CellProjection.Rebuild(t.Plot);
                }
                else
                {
                    bv.damage = newDamage;
                    SublevelStorageApi.SetBlock(id, px, py, pz, bv); // crack state is not rendered yet; no rebuild needed
                }
            }
            catch (Exception e) { Log.Error("[SubLevel] Break failed for " + id + ": " + e); }
        }

        private static void GiveDrops(Block block, EntityPlayerLocal player)
        {
            if (player == null || block == null) return;
            List<Block.SItemDropProb> list;
            if (!block.itemsToDrop.TryGetValue(EnumDropEvent.Destroy, out list) || list == null) return;
            foreach (Block.SItemDropProb p in list)
            {
                if (string.IsNullOrEmpty(p.name) || UnityEngine.Random.value > p.prob) continue;
                int count = UnityEngine.Random.Range(p.minCount, p.maxCount + 1);
                if (count <= 0) continue;
                ItemValue item = ItemClass.GetItem(p.name);
                if (item == null || item.IsEmpty()) continue;
                player.bag.AddItem(new ItemStack(item, count));
            }
        }

        // ---------------------------------------------------------------- place

        /// <summary>Writes bv into the empty cell next to the hit face and consumes one held item.</summary>
        internal static bool PlaceBlockValue(ItemInventoryData inv, BlockValue bv, Target t)
        {
            EntityPlayerLocal player = inv.holdingEntity as EntityPlayerLocal;
            if (player == null || bv.Block == null) return false;
            string id = t.Plot.Id;
            if (t.AdjY < 1 || t.AdjY > 254) return false;

            BlockValue existing;
            if (SublevelStorageApi.TryGetBlock(id, t.AdjX, t.AdjY, t.AdjZ, out existing) && !existing.isair) return false;
            if (bv.Block.GetPlacementDistanceSq() < player.HitInfo.hit.distanceSq) return false;

            try
            {
                SublevelStorageApi.SetBlock(id, t.AdjX, t.AdjY, t.AdjZ, bv);
                CellProjection.Rebuild(t.Plot);
            }
            catch (Exception e) { Log.Error("[SubLevel] Place failed for " + id + ": " + e); return false; }

            inv.holdingEntity.RightArmAnimationUse = true;
            inv.holdingEntity.PlayOneShot("placeblock");
            inv.holdingEntity.inventory.DecHoldingItem(1);
            return true;
        }

        private static float lastPlaceTime;
        internal static bool PlaceGate()
        {
            if (Time.time - lastPlaceTime < Constants.cBuildIntervall) return false;
            lastPlaceTime = Time.time;
            return true;
        }

        // ---------------------------------------------------------------- outline

        private static readonly Dictionary<string, LineRenderer> boxes = new Dictionary<string, LineRenderer>();
        private static Material boxMaterial;
        private static bool boxMaterialFailed;

        private static LineRenderer GetBox(SublevelPlot plot)
        {
            LineRenderer lr;
            if (boxes.TryGetValue(plot.Id, out lr) && lr != null && lr.transform.parent == plot.Root.transform) return lr;
            if (boxMaterial == null && !boxMaterialFailed)
            {
                Shader sh = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                if (sh == null) { boxMaterialFailed = true; Log.Warning("[SubLevel] No shader available for the sublevel block outline."); }
                else { boxMaterial = new Material(sh); boxMaterial.color = new Color(1f, 1f, 1f, 0.9f); boxMaterial.renderQueue = 4000; }
            }
            if (boxMaterial == null) return null;
            GameObject go = new GameObject("SublevelFocusBox");
            go.transform.SetParent(plot.Root.transform, false);
            lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.material = boxMaterial;
            lr.startWidth = lr.endWidth = 0.02f;
            lr.loop = false;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            float h = 0.505f;
            Vector3[] pts =
            {
                new Vector3(-h,-h,-h), new Vector3(h,-h,-h), new Vector3(h,-h,h), new Vector3(-h,-h,h), new Vector3(-h,-h,-h),
                new Vector3(-h,h,-h), new Vector3(h,h,-h), new Vector3(h,-h,-h), new Vector3(h,h,-h),
                new Vector3(h,h,h), new Vector3(h,-h,h), new Vector3(h,h,h),
                new Vector3(-h,h,h), new Vector3(-h,-h,h), new Vector3(-h,h,h), new Vector3(-h,h,-h)
            };
            lr.positionCount = pts.Length;
            lr.SetPositions(pts);
            boxes[plot.Id] = lr;
            return lr;
        }

        internal static void HideBoxes()
        {
            foreach (KeyValuePair<string, LineRenderer> kv in boxes)
                if (kv.Value != null && kv.Value.enabled) kv.Value.enabled = false;
        }

        /// <summary>Called every frame with the player's current hit info; shows the outline on the sublevel cell being targeted.</summary>
        internal static void UpdateOutline(EntityPlayerLocal player, WorldRayHitInfo info)
        {
            Target t;
            if (player == null || info == null || !info.bHitValid || !TryResolve(info, out t)) { HideBoxes(); return; }
            HideBoxes();
            LineRenderer lr = GetBox(t.Plot);
            if (lr == null) return;
            bool placing = player.inventory != null && player.inventory.holdingItem is ItemClassBlock;
            int x = placing ? t.AdjX : t.X, y = placing ? t.AdjY : t.Y, z = placing ? t.AdjZ : t.Z;
            lr.transform.localPosition = new Vector3(x, y - PlotSpace.PivotY, z);
            lr.enabled = true;
        }
    }

    [HarmonyPatch(typeof(Voxel), nameof(Voxel.raycastNew))]
    internal static class SublevelRaycastPatch
    {
        private static bool Prefix(Ray ray, float distance, int _layerMask, float _sphereRadius, ref bool __result)
        {
            try
            {
                if (SublevelInteraction.TryHandleRay(ray, distance, _layerMask, _sphereRadius)) { __result = true; return false; }
            }
            catch (Exception e) { Log.Warning("[SubLevel] Raycast adapter failed, falling back to vanilla: " + e.Message); }
            return true;
        }
    }

    [HarmonyPatch(typeof(ItemActionAttack), nameof(ItemActionAttack.Hit))]
    internal static class SublevelAttackHitPatch
    {
        private static bool Prefix(WorldRayHitInfo hitInfo, int _attackerEntityId, float _blockDamage, DamageMultiplier _damageMultiplier, ItemActionAttack.EnumAttackMode _attackMode)
        {
            SublevelInteraction.Target t;
            if (!SublevelInteraction.TryResolve(hitInfo, out t)) return true;
            if (_attackMode != ItemActionAttack.EnumAttackMode.Simulate) SublevelInteraction.DamageBlock(t, _blockDamage, _damageMultiplier, _attackerEntityId);
            return false; // never let vanilla resolve this hit against the primary world
        }
    }

    [HarmonyPatch(typeof(ItemActionPlaceAsBlock), nameof(ItemActionPlaceAsBlock.ExecuteAction))]
    internal static class SublevelPlaceBlockPatch
    {
        private static bool Prefix(ItemActionPlaceAsBlock __instance, ItemActionData _actionData, bool _bReleased)
        {
            EntityPlayerLocal player = _actionData.invData.holdingEntity as EntityPlayerLocal;
            if (player == null) return true;
            SublevelInteraction.Target t;
            if (!SublevelInteraction.TryResolve(player.HitInfo, out t)) return true;
            if (!_bReleased || Time.time - _actionData.lastUseTime < __instance.Delay || Time.time - _actionData.lastUseTime < Constants.cBuildIntervall) return false;
            BlockValue bv = _actionData.invData.item.OnConvertToBlockValue(_actionData.invData.itemValue, __instance.blockToPlace);
            try { if (_actionData.invData.IsBlock) bv.rotation = (byte)(_actionData.invData.localRot & 0xFF); } catch (Exception) { }
            if (SublevelInteraction.PlaceBlockValue(_actionData.invData, bv, t)) { _actionData.lastUseTime = Time.time; player.DropTimeDelay = 0.5f; }
            return false;
        }
    }

    /// <summary>Ordinary block items (the toolbelt blocks) are placed by the active block tool, not by ItemActionPlaceAsBlock.</summary>
    [HarmonyPatch(typeof(BlockToolSelection), nameof(BlockToolSelection.ExecuteUseAction))]
    internal static class SublevelBlockToolPlacePatch
    {
        private static bool Prefix(ItemInventoryData _data, bool _bReleased, ref bool __result)
        {
            if (_data == null || !_data.IsBlock) return true;
            EntityPlayerLocal player = _data.holdingEntity as EntityPlayerLocal;
            if (player == null) return true;
            SublevelInteraction.Target t;
            if (!player.HitInfo.bHitValid || !SublevelInteraction.TryResolve(player.HitInfo, out t)) return true;
            __result = true;
            if (_bReleased || !SublevelInteraction.PlaceGate()) return false;
            BlockValue bv = _data.itemValue.ToBlockValue();
            if (bv.Block == null) return false;
            bv.damage = _data.damage;
            bv.rotation = _data.rotation;
            if (bv.damage == 0) bv.damage = bv.Block.StartDamage;
            SublevelInteraction.PlaceBlockValue(_data, bv, t);
            return false;
        }
    }

    /// <summary>Vanilla draws its focus cube at a world block position; a sublevel moves and rotates, so it gets its own outline.</summary>
    [HarmonyPatch(typeof(PlayerMoveController), nameof(PlayerMoveController.HandleInteraction))]
    internal static class SublevelOutlinePatch
    {
        private static void Postfix(PlayerMoveController __instance, WorldRayHitInfo _hitInfo)
        {
            try { SublevelInteraction.UpdateOutline(GameManager.Instance != null ? GameManager.Instance.myEntityPlayerLocal : null, _hitInfo); }
            catch (Exception e) { Log.Warning("[SubLevel] Outline update failed: " + e.Message); }
        }
    }
}
