# 7DTD Sublevel Physics — Alpha 1.2 command/multi-instance fix (collision restored)

Target: 7 Days to Die 3.4.0 (build compatibility target 3.40.10).

## Build

```powershell
dotnet build .\SubLevelPhysics.csproj -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
```

Copy the resulting `bin\SubLevelPhysics\SubLevelPhysics.dll` into the mod's `SubLevelPhysics` folder.

## Commands

- `sl plot [sizeChunks] [block]` — create a sublevel at the next unused reserved chunk area. Default footprint is 3x3 chunks.
- `sl plot <chunkX> <chunkZ> [sizeChunks] [block]` — create at explicit chunk coordinates.
- `sl list` / `sl lists` / `sl plots` — list all sublevels, including their generated IDs, chunk allocation, world position, yaw, and physics state.
- `sl select <id>` — select a sublevel for tools that operate on the selected instance.
- `sl here <id>` — place that sublevel in front of the player.
- `sl pose <id> <x> <y> <z> [yaw]` — set its world pose.
- `sl move <id> <vx> <vy> <vz>` — set velocity in metres/second.
- `sl move <id> (x,y,z) <speed>` — move along a direction at the requested speed.
- `sl spin <id> <degreesPerSecond>` — set scripted yaw rate.
- `sl physics <id> true|false` — toggle dynamic Rigidbody physics on exactly that sublevel.
- `sl set <id> <block> <dx> <dy> <dz> [rotation]` — place a block relative to that sublevel's pivot.
- `sl status [id]` — list sublevels, or inspect the requested one.
- `sl mat <id>` — dump materials for that sublevel.
- `sl delete <id>` — delete only the requested sublevel.

All per-sublevel modification commands require an explicit ID. There is no implicit all-sublevels physics toggle or delete-all command. Use `sl list` to discover IDs.

## Allocation behavior

Automatic creation scans outward from the reserved starting chunk coordinate (4000,4000), checking the entire visible and support-chunk footprint against the loaded chunk cluster and existing sublevels. Each instance receives a generated eight-character ID. Explicit coordinates remain available for advanced use.

## Notes

The project must be compiled against the installed game's Managed assemblies. This source package has not been compiled or exercised in-game in this environment. Back up the world before testing mod builds that create or remove chunk data.

## Collision rollback

The kinematic one-way collision model introduced after Alpha 1.0 made sublevels fall through terrain (kinematic bodies do not collide with static terrain colliders). `PlotDriver` physics and the Physics Staff are restored to the Alpha 1.0 dynamic-Rigidbody behavior; commands, multi-sublevel support and persistence are unchanged. The layer-16 collision-matrix override is now reference-counted so several physics sublevels can coexist.
