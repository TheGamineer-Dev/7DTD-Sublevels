using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Scripting;

namespace SubLevelPhysics
{
    [Preserve]
    public sealed class ConsoleCmdSubLevel : ConsoleCmdAbstract
    {
        public override int DefaultPermissionLevel { get { return 0; } }
        public override bool IsExecuteOnClient { get { return false; } }
        public override string getDescription() { return "Cell-backed sublevel projections and scripted physics"; }
        public override string[] getCommands() { return new[] { "sublevel", "sl" }; }
        public override string getHelp()
        {
            return "sl plot [block]                         create a sublevel with a 3x3 test platform\n" +
                   "sl list | sl plots                       list sublevels and allocated cells\n" +
                   "sl select <id>                           select a sublevel\n" +
                   "sl here <id>                             place it 6m in front of the local player\n" +
                   "sl pose <id> <x> <y> <z> [yaw]           set world pose\n" +
                   "sl move <id> <vx> <vy> <vz>              set scripted velocity (m/s)\n" +
                   "sl move <id> (x,y,z) <speed>             set velocity along a direction\n" +
                   "sl spin <id> <degreesPerSecond>          set scripted yaw rate\n" +
                   "sl physics <id> true|false               toggle dynamic Rigidbody physics for ONE sublevel\n" +
                   "sl ride [true|false]                     show/toggle the player riding (moving + turning with) sublevels they stand on\n" +
                   "sl set <id> <block|air> <x> <yOffset> <z> [rotation]  write a logical block; cells allocate automatically\n" +
                   "sl status [id]                           inspect logical-to-physical cell mappings\n" +
                   "sl mat <id>                              report projection renderer/collider counts\n" +
                   "sl delete <id>                           delete only after all cells are empty\n" +
                   "Y offsets are relative to pivot Y=100. Writable offsets: -99 through 154. X/Z are unbounded logical block coordinates.";
        }

        private static bool Float(string s, out float value)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static SublevelPlot Require(List<string> p, int index, SdtdConsole con)
        {
            if (p.Count <= index) { con.Output("This command requires a sublevel ID. Use 'sl list'."); return null; }
            SublevelPlot plot = PlotSpace.FindById(p[index]);
            if (plot == null) con.Output("Unknown sublevel ID '" + p[index] + "'. Use 'sl list'.");
            else PlotSpace.Select(plot.Id);
            return plot;
        }

        public override void Execute(List<string> p, CommandSenderInfo info)
        {
            SdtdConsole con = SingletonMonoBehaviour<SdtdConsole>.Instance;
            string command = p.Count > 0 ? p[0].ToLowerInvariant() : "help";
            switch (command)
            {
                case "plot":
                {
                    string block = "cobblestoneShapes:cube";
                    if (p.Count > 1)
                    {
                        int legacySize;
                        if (int.TryParse(p[1], out legacySize))
                        {
                            if (p.Count > 2) block = p[2];
                            con.Output("The old fixed square footprint argument is deprecated. Cells now allocate sparsely as blocks are added.");
                        }
                        else block = p[1];
                    }
                    string result = PlotSpace.CreateAuto(block);
                    Log.Out("[SubLevel] " + result);
                    con.Output(result);
                    break;
                }
                case "list": case "lists": case "plots":
                    con.Output(PlotSpace.ListSublevels());
                    break;
                case "select":
                    if (p.Count < 2) con.Output("Usage: sl select <id>");
                    else con.Output(PlotSpace.Select(p[1]) ? "Selected sublevel " + p[1] + "." : "Unknown sublevel ID '" + p[1] + "'.");
                    break;
                case "here":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot == null) break;
                    EntityPlayerLocal player = GameManager.Instance != null && GameManager.Instance.World != null ? GameManager.Instance.World.GetPrimaryPlayer() : null;
                    if (player == null) { con.Output("No local player is available to determine a position. Use 'sl pose'."); break; }
                    con.Output(PlotSpace.SetPose(plot.Id, PlotSpace.PosInFront(player), 0f));
                    break;
                }
                case "pose":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot == null) break;
                    if (p.Count < 5 || !Float(p[2], out float x) || !Float(p[3], out float y) || !Float(p[4], out float z))
                    { con.Output("Usage: sl pose <id> <x> <y> <z> [yaw]"); break; }
                    float yaw = 0f;
                    if (p.Count > 5 && !Float(p[5], out yaw)) { con.Output("Invalid yaw."); break; }
                    con.Output(PlotSpace.SetPose(plot.Id, new Vector3(x, y, z), yaw));
                    break;
                }
                case "move":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot == null) break;
                    Vector3 velocity;
                    if (p.Count == 4)
                    {
                        string[] parts = p[2].Trim('(', ')').Split(',');
                        if (parts.Length != 3 || !Float(parts[0], out float dx) || !Float(parts[1], out float dy) || !Float(parts[2], out float dz) || !Float(p[3], out float speed))
                        { con.Output("Usage: sl move <id> (x,y,z) <speed>"); break; }
                        Vector3 direction = new Vector3(dx, dy, dz);
                        if (direction.sqrMagnitude < 0.00000001f && speed != 0f) { con.Output("Direction cannot be zero when speed is nonzero."); break; }
                        velocity = direction.normalized * speed;
                    }
                    else if (p.Count == 5 && Float(p[2], out float vx) && Float(p[3], out float vy) && Float(p[4], out float vz))
                        velocity = new Vector3(vx, vy, vz);
                    else { con.Output("Usage: sl move <id> <vx> <vy> <vz> OR sl move <id> (x,y,z) <speed>"); break; }
                    con.Output(PlotSpace.SetVelocity(plot.Id, velocity));
                    break;
                }
                case "spin":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot == null) break;
                    if (p.Count != 3 || !Float(p[2], out float rate)) { con.Output("Usage: sl spin <id> <degreesPerSecond>"); break; }
                    con.Output(PlotSpace.SetYawRate(plot.Id, rate));
                    break;
                }
                case "physics":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot == null) break;
                    if (p.Count != 3 || !bool.TryParse(p[2], out bool enabled)) { con.Output("Usage: sl physics <id> true|false"); break; }
                    con.Output(PlotSpace.SetPhysics(plot.Id, enabled));
                    break;
                }
                case "set":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot == null) break;
                    if (p.Count < 6 || !int.TryParse(p[3], out int bx) || !int.TryParse(p[4], out int by) || !int.TryParse(p[5], out int bz))
                    { con.Output("Usage: sl set <id> <block|air> <x> <yOffset> <z> [rotation]"); break; }
                    int rotation = 0;
                    if (p.Count > 6 && !int.TryParse(p[6], out rotation)) { con.Output("Invalid rotation."); break; }
                    con.Output(PlotSpace.SetBlockAt(plot.Id, p[2], bx, by, bz, rotation));
                    break;
                }
                case "status":
                    con.Output(p.Count > 1 ? PlotSpace.Status(p[1]) : PlotSpace.ListSublevels());
                    break;
                case "mat":
                {
                    SublevelPlot plot = Require(p, 1, con); if (plot != null) con.Output(PlotSpace.DumpMaterials(plot.Id));
                    break;
                }
                case "delete": case "clear":
                    if (p.Count < 2) con.Output("Usage: sl delete <id>. Deleting all sublevels is unsupported.");
                    else con.Output(PlotSpace.Destroy(p[1]));
                    break;
                case "ride":
                    if (p.Count > 1)
                    {
                        bool ride;
                        if (!bool.TryParse(p[1], out ride)) { con.Output("Usage: sl ride [true|false]"); break; }
                        PlatformRiding.Enabled = ride;
                    }
                    con.Output("Player riding is " + (PlatformRiding.Enabled ? "ON" : "OFF") + (PlatformRiding.Ridden != null ? "; standing on sublevel " + PlatformRiding.Ridden.Id : "") + ".");
                    break;
                default:
                    con.Output(getHelp());
                    break;
            }
        }
    }
}
