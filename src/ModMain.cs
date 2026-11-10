using System;
using System.Reflection;
using HarmonyLib;

namespace SubLevelPhysics
{
	public class ModMain : IModApi
	{
		public const string HarmonyId = "darkmachine.sublevelphysics";
		public const string TargetVersion = "3.40.10"; // decompile this was written against

		private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
		{
			PlotSpace.OnWorldShuttingDown();
		}

		public void InitMod(Mod _modInstance)
		{
			string running = Constants.cVersionMajor + "." + Constants.cVersionMinor + "." + Constants.cVersionBuild;
			Log.Out("[SubLevel] Phase 1.1 block-geometry collision + block logic carry-over loading. Built for " + TargetVersion + ", running " + running);
			if (running != TargetVersion)
			{
				Log.Warning("[SubLevel] Version mismatch: patch targets may have changed.");
			}
			// Patch class by class so one bad signature (this mod is built blind against a decompile) cannot take the
			// others down, and so the log says exactly which patches applied.
			ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
			var harmony = new Harmony(HarmonyId);
			foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
			{
				if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
				try
				{
					harmony.CreateClassProcessor(t).Patch();
					Log.Out("[SubLevel] patched " + t.Name);
				}
				catch (Exception e)
				{
					Log.Error("[SubLevel] patch FAILED " + t.Name + ": " + e.Message);
				}
			}
		}
	}
}
