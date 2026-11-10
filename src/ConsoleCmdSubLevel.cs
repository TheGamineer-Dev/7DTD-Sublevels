using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace SubLevelPhysics
{
	[Preserve]
	public class ConsoleCmdSubLevel : ConsoleCmdAbstract
	{
		public override int DefaultPermissionLevel => 0;
		public override bool IsExecuteOnClient => false;

		public override string getDescription() { return "Sub-level physics Phase 0 spikes"; }
		public override string[] getCommands() { return new[] { "sublevel", "sl" }; }
		public override string getHelp()
		{
			return "sl plot [chunkX chunkZ] [sizeChunks] [block]   create a dynamically sized square plot (odd chunk width/depth; two support rings) + 10-block test cube, posed in front of you (defaults: chunk 4000 4000, size 3, cobblestoneShapes:cube)\n" +
			       "sl here                            re-pose the plot 6 m in front of you, 1 m above your feet\n" +
			       "sl pose <x> <y> <z> [yaw]          set pose (true world coords)\n" +
			       "sl move <vx> <vy> <vz>             constant velocity (m/s) ; 'sl move 0 0 0' stops\n" +
			       "sl spin <degPerSec>                constant yaw rate (scripted mode only)\n" +
			       "sl physics true|false               enable Unity dynamic Rigidbody physics / return to scripted mode\n" +
			       "   Physics Staff: Creative Mode item; hold left mouse to grab, release to drop, scroll to change reach\n" +
			       "sl set <block> <dx> <dy> <dz> [rot]  set a block relative to the plot pivot (test floor is dy=0); e.g. 'sl set cobblestoneShapes:ramp 0 1 0' or a workbench / crate to test block logic\n" +
			       "sl status                          dump plot/chunk/pipeline/mesh/collider state (also written to output_log.txt)\n" +
			       "sl mat                             dump block shader/material properties of the plot to output_log.txt\n" +
			       "sl clear                           remove plot and observer";
		}

		public override void Execute(List<string> p, CommandSenderInfo info)
		{
			var con = SingletonMonoBehaviour<SdtdConsole>.Instance;
			string sub = p.Count > 0 ? p[0].ToLowerInvariant() : "help";
			Plot plot = PlotSpace.Current;
			switch (sub)
			{
				case "plot":
				{
					int cx = 4000, cz = 4000, sizeChunks = 3; string block = "cobblestoneShapes:cube";
					if (p.Count >= 3 && int.TryParse(p[1], out int a) && int.TryParse(p[2], out int b)) { cx = a; cz = b; }
					if (p.Count == 2) block = p[1];
					else if (p.Count == 4)
					{
						if (int.TryParse(p[3], out int parsedSize)) sizeChunks = parsedSize;
						else block = p[3]; // legacy syntax: sl plot cx cz block
					}
					else if (p.Count >= 5)
					{
						if (!int.TryParse(p[3], out sizeChunks)) { con.Output("Usage: sl plot [chunkX chunkZ] [sizeChunks] [block]"); break; }
						block = p[4];
					}
					string res = PlotSpace.Create(cx, cz, block, sizeChunks);
					Log.Out("[SubLevel] sl plot -> " + res);
					con.Output(res);
					break;
				}
				case "here":
				{
					if (plot == null) { con.Output("No plot."); break; }
					EntityPlayerLocal pl = GameManager.Instance.World.GetPrimaryPlayer();
					if (pl == null) { con.Output("No local player."); break; }
					Vector3 truePos = PlotSpace.PosInFront(pl);
					plot.Driver.SetPose(truePos, 0f);
					con.Output("Pose set to " + truePos);
					break;
				}
				case "pose":
				{
					if (plot == null || p.Count < 4) { con.Output("Usage: sl pose x y z [yaw] (needs a plot)"); break; }
					float yaw = p.Count > 4 ? float.Parse(p[4]) : 0f;
					plot.Driver.SetPose(new Vector3(float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3])), yaw);
					con.Output("ok");
					break;
				}
				case "move":
					if (plot == null || p.Count < 4) { con.Output("Usage: sl move vx vy vz"); break; }
					if (plot.Driver.PhysicsEnabled) { con.Output("Scripted velocity is unavailable while physics is enabled."); break; }
					plot.Driver.Velocity = new Vector3(float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3]));
					con.Output("ok");
					break;
				case "spin":
					if (plot == null || p.Count < 2) { con.Output("Usage: sl spin degPerSec"); break; }
					if (plot.Driver.PhysicsEnabled) { con.Output("Scripted spin is unavailable while physics is enabled."); break; }
					plot.Driver.YawRate = float.Parse(p[1]);
					con.Output("ok");
					break;
				case "physics":
					if (plot == null || p.Count < 2) { con.Output("Usage: sl physics true|false"); break; }
					bool physicsEnabled;
					if (!bool.TryParse(p[1], out physicsEnabled)) { con.Output("Usage: sl physics true|false"); break; }
					con.Output(plot.Driver.SetPhysics(physicsEnabled));
					break;
				case "status":
				{
					string st = PlotSpace.Status();
					con.Output(st);
					Log.Out("[SubLevel] status:\n" + st);
					break;
				}
				case "set":
				{
					int dx, dy, dz, rot = 0;
					if (p.Count < 5 || !int.TryParse(p[2], out dx) || !int.TryParse(p[3], out dy) || !int.TryParse(p[4], out dz) || (p.Count > 5 && !int.TryParse(p[5], out rot)))
					{ con.Output("Usage: sl set <block> <dx> <dy> <dz> [rotation]"); break; }
					con.Output(PlotSpace.SetBlockAt(p[1], dx, dy, dz, rot));
					break;
				}
				case "mat": con.Output(PlotSpace.DumpMaterials()); break;
				case "clear": con.Output(PlotSpace.Destroy()); break;
				default: con.Output(getHelp()); break;
			}
		}
	}
}
