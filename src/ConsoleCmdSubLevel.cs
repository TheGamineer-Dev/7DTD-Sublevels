using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Scripting;

namespace SubLevelPhysics
{
	[Preserve]
	public class ConsoleCmdSubLevel : ConsoleCmdAbstract
	{
		public override int DefaultPermissionLevel => 0;
		public override bool IsExecuteOnClient => false;

		public override string getDescription() { return "Persistent multi-sublevel physics"; }
		public override string[] getCommands() { return new[] { "sublevel", "sl" }; }
		public override string getHelp()
		{
			return "sl plot [sizeChunks] [block]              create at the next free sublevel location (default size 3)\n" +
			       "sl plot <chunkX> <chunkZ> [size] [block]  create at explicit chunk coordinates\n" +
			       "sl list | sl lists | sl plots              list every sublevel and its ID/position\n" +
			       "sl select <id>                            select a sublevel for inspection\n" +
			       "sl here <id>                              pose a sublevel 6m in front of you\n" +
			       "sl pose <id> <x> <y> <z> [yaw]            set world position/orientation\n" +
			       "sl move <id> <vx> <vy> <vz>               set velocity in m/s\n" +
			       "sl move <id> (x,y,z) <speed>              move along a direction at speed\n" +
			       "sl spin <id> <degPerSec>                  set scripted yaw rate\n" +
			       "sl physics <id> true|false                toggle dynamic Rigidbody physics for ONE sublevel\n" +
			       "sl ride [true|false]                     show/toggle the player riding (moving + turning with) sublevels they stand on\n" +
			       "sl set <id> <block> <dx> <dy> <dz> [rot]  place a block relative to that sublevel\n" +
			       "sl status [id]                            inspect one sublevel or list all\n" +
			       "sl mat <id>                               dump that sublevel's materials\n" +
			       "sl delete <id>                            delete one sublevel\n" +
			       "Physics-changing commands require an explicit ID. Use 'sl list' to find IDs.";
		}

		private static bool TryFloat(string s, out float value)
		{
			return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		private static Plot RequirePlot(List<string> p, int idIndex, SdtdConsole con)
		{
			if (p.Count <= idIndex) { con.Output("This command requires a sublevel ID. Use 'sl list' to see IDs."); return null; }
			Plot plot = PlotSpace.FindById(p[idIndex]);
			if (plot == null) con.Output("Unknown sublevel ID '" + p[idIndex] + "'. Use 'sl list' to see IDs.");
			else PlotSpace.Select(plot.Id);
			return plot;
		}

		public override void Execute(List<string> p, CommandSenderInfo info)
		{
			SdtdConsole con = SingletonMonoBehaviour<SdtdConsole>.Instance;
			string sub = p.Count > 0 ? p[0].ToLowerInvariant() : "help";
			// On a dedicated server there is no primary player: place relative to whoever typed the command.
			PlotSpace.Caller = info.RemoteClientInfo != null && GameManager.Instance.World != null
				? GameManager.Instance.World.GetEntity(info.RemoteClientInfo.entityId) as EntityPlayer : null;
			switch (sub)
			{
				case "plot":
				{
					int size = 3;
					string block = "cobblestoneShapes:cube";
					string result;
					if (p.Count == 1) result = PlotSpace.CreateAuto(block, size);
					else if (p.Count == 2 && int.TryParse(p[1], out size)) result = PlotSpace.CreateAuto(block, size);
					else if (p.Count == 2) { block = p[1]; result = PlotSpace.CreateAuto(block, size); }
					else if (p.Count >= 3 && int.TryParse(p[1], out int cx) && int.TryParse(p[2], out int cz))
					{
						if (p.Count >= 4 && !int.TryParse(p[3], out size)) { con.Output("Usage: sl plot <chunkX> <chunkZ> [oddSize] [block]"); break; }
						if (p.Count >= 5) block = p[4];
						result = PlotSpace.Create(cx, cz, block, size);
					}
					else { con.Output("Usage: sl plot [sizeChunks] [block] OR sl plot <chunkX> <chunkZ> [sizeChunks] [block]"); break; }
					Log.Out("[SubLevel] " + result); con.Output(result); break;
				}
				case "list": case "lists": case "plots":
					con.Output(PlotSpace.ListSublevels()); break;
				case "select":
					if (p.Count < 2) { con.Output("Usage: sl select <id>"); break; }
					con.Output(PlotSpace.Select(p[1]) ? "Selected sublevel " + p[1] + "." : "Unknown sublevel ID '" + p[1] + "'. Use 'sl list'."); break;
				case "here":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					EntityPlayer pl = PlotSpace.Caller != null ? PlotSpace.Caller : GameManager.Instance.World.GetPrimaryPlayer();
					if (pl == null) { con.Output("No player to place it in front of (on a dedicated server, run it from a client)."); break; }
					Vector3 pos = PlotSpace.PosInFront(pl); plot.Driver.SetPose(pos, 0f); PlotSpace.SaveState();
					con.Output("Sublevel " + plot.Id + " posed at " + pos); break;
				}
				case "pose":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					if (p.Count < 5 || !TryFloat(p[2], out float x) || !TryFloat(p[3], out float y) || !TryFloat(p[4], out float z)) { con.Output("Usage: sl pose <id> <x> <y> <z> [yaw]"); break; }
					float yaw = 0f; if (p.Count > 5 && !TryFloat(p[5], out yaw)) { con.Output("Invalid yaw."); break; }
					plot.Driver.SetPose(new Vector3(x, y, z), yaw); PlotSpace.SaveState(); con.Output("Pose set for " + plot.Id + "."); break;
				}
				case "move":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					Vector3 velocity;
					if (p.Count == 4)
					{
						string[] xyz = p[2].Trim('(', ')').Split(',');
						if (xyz.Length != 3 || !TryFloat(xyz[0], out float dx) || !TryFloat(xyz[1], out float dy) || !TryFloat(xyz[2], out float dz) || !TryFloat(p[3], out float speed)) { con.Output("Usage: sl move <id> (x,y,z) <speed>"); break; }
						Vector3 direction = new Vector3(dx, dy, dz);
						if (direction.sqrMagnitude < 1e-8f && speed != 0f) { con.Output("Direction cannot be zero when speed is nonzero."); break; }
						velocity = direction.normalized * speed;
					}
					else if (p.Count == 5 && TryFloat(p[2], out float vx) && TryFloat(p[3], out float vy) && TryFloat(p[4], out float vz)) velocity = new Vector3(vx, vy, vz);
					else { con.Output("Usage: sl move <id> <vx> <vy> <vz> OR sl move <id> (x,y,z) <speed>"); break; }
					if (plot.Driver.PhysicsEnabled) { con.Output("Disable physics for this sublevel before setting scripted velocity."); break; }
					plot.Driver.Velocity = velocity; PlotSpace.SaveState(); con.Output("Velocity set for " + plot.Id + ": " + velocity); break;
				}
				case "spin":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					if (p.Count != 3 || !TryFloat(p[2], out float rate)) { con.Output("Usage: sl spin <id> <degPerSec>"); break; }
					if (plot.Driver.PhysicsEnabled) { con.Output("Scripted spin is unavailable while physics is enabled."); break; }
					plot.Driver.YawRate = rate; PlotSpace.SaveState(); con.Output("Yaw rate set for " + plot.Id + "."); break;
				}
				case "ride":
				{
					if (p.Count == 2 && bool.TryParse(p[1], out bool ride)) PlatformRiding.Enabled = ride;
					else if (p.Count != 1) { con.Output("Usage: sl ride [true|false]"); break; }
					con.Output("Player riding of sublevels is " + (PlatformRiding.Enabled ? "ENABLED" : "DISABLED") + (PlatformRiding.Ridden != null ? " (currently on sublevel " + PlatformRiding.Ridden.Id + ")" : "") + ".");
					break;
				}
				case "physics":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					if (p.Count != 3 || !bool.TryParse(p[2], out bool enabled)) { con.Output("Usage: sl physics <id> true|false. An ID is required; no global toggle exists."); break; }
					con.Output(plot.Driver.SetPhysics(enabled)); PlotSpace.SaveState(); break;
				}
				case "set":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					if (p.Count < 6 || !int.TryParse(p[3], out int dx) || !int.TryParse(p[4], out int dy) || !int.TryParse(p[5], out int dz)) { con.Output("Usage: sl set <id> <block> <dx> <dy> <dz> [rotation]"); break; }
					int rot = 0; if (p.Count > 6 && !int.TryParse(p[6], out rot)) { con.Output("Invalid rotation."); break; }
					con.Output(PlotSpace.SetBlockAt(plot.Id, p[2], dx, dy, dz, rot)); break;
				}
				case "status":
					con.Output(p.Count > 1 ? PlotSpace.Status(p[1]) : PlotSpace.ListSublevels()); break;
				case "mat":
				{
					Plot plot = RequirePlot(p, 1, con); if (plot == null) break;
					con.Output(PlotSpace.DumpMaterials(plot.Id)); break;
				}
				case "delete": case "clear":
					if (p.Count < 2) { con.Output("Usage: sl delete <id>. Deleting all sublevels is intentionally unsupported."); break; }
					con.Output(PlotSpace.Destroy(p[1])); break;
				default: con.Output(getHelp()); break;
			}
		}
	}
}
