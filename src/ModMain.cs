using System;
using System.Reflection;
using HarmonyLib;

namespace SubLevelPhysics
{
    public sealed class ModMain : IModApi
    {
        public const string HarmonyId = "darkmachine.sublevelphysics";
        private static Harmony harmony;
        private static SublevelCellStore store;
        private static bool storageReady;
        private static string lastResolveDiagnostic;
        private static int initializationAttempts;

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData data)
        {
            if (!MultiWorldRuntimeBridge.IsServerAuthority) return;
            if (storageReady)
            {
                PlotSpace.OnGameUpdate();
                return;
            }
            string error;
            initializationAttempts++;
            if (!MultiWorldRuntimeBridge.TryResolve(out error))
            {
                // Emit the first failure and any subsequent state change, rather than flooding
                // the game log on every frame while MultiWorld is still starting up.
                if (!string.Equals(lastResolveDiagnostic, error, StringComparison.Ordinal))
                {
                    lastResolveDiagnostic = error;
                    Log.Warning("[SubLevel][StorageDiag] Resolve pending (attempt " + initializationAttempts + "): " + error);
                }
                // MultiWorld has finished loading its worlds but none is named Sublevels:
                // add it to this save's config automatically (takes effect on next load).
                if (MultiWorldRuntimeBridge.LoadedWorldCount() >= 1 && MultiWorldRuntimeBridge.FindStorageWorld() == null)
                    StorageWorldProvisioner.TryProvision();
                return;
            }
            lastResolveDiagnostic = null;
            try
            {
                MultiWorldRuntimeBridge.StorageWorld target = MultiWorldRuntimeBridge.FindStorageWorld();
                Log.Out("[SubLevel][StorageDiag] Resolve succeeded; storageName=" + target.Name +
                    "; worldNull=" + (target.World == null) + "; savePath=" + (target.SavePath ?? "<null>") +
                    "; " + MultiWorldRuntimeBridge.DescribeState());
                store = new SublevelCellStore();
                Log.Out("[SubLevel][StorageDiag] Cell-store Initialize begin.");
                store.Initialize();
                Log.Out("[SubLevel][StorageDiag] Cell-store Initialize succeeded.");
                WorldUpdateSuppression.StorageWorld = target.World;
                SublevelStorageApi.Attach(store);
                storageReady = true;
                PlotSpace.OnGameUpdate();
                Log.Out("[SubLevel] Sublevels storage world ready; cell ledger loaded.");
            }
            catch (Exception e)
            {
                Log.Error("[SubLevel][StorageDiag] Storage initialization failed closed on attempt " + initializationAttempts + ": " + e);
                Log.Error("[SubLevel][StorageDiag] State at failure: " + MultiWorldRuntimeBridge.DescribeState());
                store = null;
                SublevelStorageApi.Detach();
                WorldUpdateSuppression.StorageWorld = null;
            }
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData data)
        {
            PlotSpace.OnWorldShuttingDown();
            if (store != null)
            {
                try { MultiWorldRuntimeBridge.FlushStorageChunks(); }
                catch (Exception e) { Log.Error("[SubLevel] Storage chunk flush failed: " + e); }
                try { store.SaveLedger(); }
                catch (Exception e) { Log.Error("[SubLevel] Cell ledger flush failed: " + e); }
            }
            SublevelStorageApi.Detach();
            store = null; storageReady = false; lastResolveDiagnostic = null; initializationAttempts = 0; StorageWorldProvisioner.Reset(); WorldUpdateSuppression.StorageWorld = null;
        }

        public void InitMod(Mod mod)
        {
            Log.Out("[SubLevel] MultiWorld cell-storage rework initializing.");
            ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
            ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
            harmony = new Harmony(HarmonyId);
            foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try { harmony.CreateClassProcessor(t).Patch(); Log.Out("[SubLevel] patched " + t.Name); }
                catch (Exception e) { Log.Error("[SubLevel] patch FAILED " + t.Name + ": " + e); }
            }
            Log.Out("[SubLevel] Storage access is server-authoritative; dedicated servers and single-player/listen-server hosts are supported when MultiWorld exposes the Sublevels context.");
        }
    }
}
