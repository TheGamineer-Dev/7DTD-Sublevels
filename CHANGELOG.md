# Changelog

## 0.5.0-alpha

- Added the legacy `sl` / `sublevel` console command surface for create, list, select, pose, move, spin, physics-state inspection, block writes, status, materials, and delete.
- Replaced fixed square plot footprints with sparse logical cells. `sl set` writes at arbitrary logical X/Z coordinates and the storage API allocates cells as boundaries are crossed.
- Added runtime projection roots with one generated mesh/collider set per logical storage cell. Projections are built from detached cell snapshots; no static block values are written into the primary world's ChunkCluster.
- Added scripted kinematic movement and yaw rotation. Dynamic Rigidbody mode is explicitly refused because the current per-cell non-convex colliders are not suitable for a dynamic body.
- Added projection pose metadata persistence and restoration in `sublevels-projections.xml`.
- Allowed server-authoritative single-player/listen-server hosts to initialize the MultiWorld storage bridge, not only dedicated-server processes.
- Added MSBuild copy rules so `ModInfo.xml`, `Config`, and `README.md` are included in the build output automatically.
- Player placement/breaking, tile entities, platform riding, dynamic physics, and multiplayer projection replication remain unimplemented.

## 0.4.0-alpha

- Replaced the five-chunk physical stride with dense one-chunk row-major allocation; no guard cells are reserved.
- Kept logical sublevel coordinates independent from physical storage coordinates and retained sparse logical allocation.
- Added a projection-facing cell enumeration API and detached 65,536-block cell snapshots.
- Made air writes into unallocated logical cells no-ops.
- Added storage-cluster-scoped suppression for lighting, stability, mesh regeneration, distant decorations, mesh invalidation, and neighbor-block notifications.
- Expanded conservative empty-cell checks to cover block-associated collections and non-default block data channels.
- Added allocation rollback if the ledger commit fails before a block write.
- Bumped the ledger writer to v3 while retaining read compatibility with the prior v2 alpha ledger.

## 0.3.0-alpha

- Added persisted allocation cursor and free-cell ledger entries.
- Added revalidation of reusable empty cells and startup validation of mapped chunks.
- Added saved-chunk restoration through the v3.4 `RegionFileManager` path.
- Added main-thread enforcement and safe MultiWorld context restoration.
