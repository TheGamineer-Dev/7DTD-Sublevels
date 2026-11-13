using System;
using System.Reflection;
using HarmonyLib;
using EModEventResult = ModEvents.EModEventResult;

namespace SubLevelPhysics
{
    /// <summary>
    /// The Sublevels world is an internal data store, never a player destination. These patches
    /// guard MultiWorld's direct command and both host/client transfer entry points. They do not
    /// replace network-level validation; the storage world must also remain absent from any
    /// external portal/transfer configuration.
    /// </summary>
    [HarmonyPatch]
    internal static class BlockDirectStorageWorldCommand
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MultiWorldProbe");
            return type == null ? null : AccessTools.Method(type, "OnWorldCommand");
        }

        private static bool Prefix(ref EModEventResult __result, object[] __args)
        {
            if (__args == null || __args.Length == 0 || __args[0] == null) return true;
            object data = __args[0];
            FieldInfo messageField = data.GetType().GetField("Message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            string message = messageField == null ? null : messageField.GetValue(data) as string;
            if (!IsStorageWorldCommand(message)) return true;
            __result = EModEventResult.StopHandlersAndVanilla;
            return false;
        }

        private static bool IsStorageWorldCommand(string message)
        {
            Type type = AccessTools.TypeByName("MultiWorldProbe");
            MethodInfo parser = type == null ? null : AccessTools.Method(type, "ParseWorldCommand");
            if (parser == null) return false;
            object[] args = { message, null };
            try
            {
                if (!(bool)parser.Invoke(null, args)) return false;
                return string.Equals(args[1] as string, "Sublevels", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    [HarmonyPatch]
    internal static class BlockRemoteStorageWorldTransfer
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MultiWorldProbe");
            return type == null ? null : AccessTools.Method(type, "RequestRemoteWorldTransfer");
        }

        private static bool Prefix(string name, ref bool __result)
        {
            if (!string.Equals(name, "Sublevels", StringComparison.OrdinalIgnoreCase)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class BlockLocalStorageWorldTransfer
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MultiWorldProbe");
            return type == null ? null : AccessTools.Method(type, "RequestLocalHostTransfer");
        }

        private static bool Prefix(string name, ref bool __result)
        {
            if (!string.Equals(name, "Sublevels", StringComparison.OrdinalIgnoreCase)) return true;
            __result = false;
            return false;
        }
    }
}
