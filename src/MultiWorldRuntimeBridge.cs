using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SubLevelPhysics
{
    /// <summary>
    /// MultiWorld 0.7.14 deliberately keeps its WorldContext implementation private. This bridge
    /// binds to that exact installed runtime by reflection, validates the expected members, and
    /// fails closed if the dependency changes. It does not create a second vanilla World itself.
    /// </summary>
    internal static class MultiWorldRuntimeBridge
    {
        private const string ProbeTypeName = "MultiWorldProbe";
        private static Type probeType;
        private static FieldInfo loadedWorldsField;
        private static MethodInfo selectMethod;
        private static MethodInfo activeContextMethod;
        private static FieldInfo contextWorldField;
        private static FieldInfo contextNameField;
        private static FieldInfo contextSavePathField;
        private static FieldInfo mainThreadIdField;
        private static bool resolved;

        public sealed class StorageWorld
        {
            public World World { get; internal set; }
            public string Name { get; internal set; }
            public string SavePath { get; internal set; }
            internal object Context;
        }

        public static bool IsDedicatedServer { get { return GameManager.IsDedicatedServer; } }

        // Single-player and listen-server sessions are authoritative too. The old Sublevels
        // implementation explicitly supported a local host; restricting this bridge to a
        // dedicated process made the storage backend unavailable in ordinary local testing.
        public static bool IsServerAuthority
        {
            get
            {
                if (GameManager.IsDedicatedServer) return true;
                ConnectionManager connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
                return connection == null || connection.IsServer;
            }
        }

        public static bool TryResolve(out string error)
        {
            error = null;
            if (!IsServerAuthority) { error = "Sublevels storage is server-authoritative; a remote client cannot initialize it."; return false; }
            if (!ResolveMembers(out error)) return false;
            StorageWorld storage = FindStorageWorld();
            if (storage == null) { error = "MultiWorld world 'Sublevels' is not present in LoadedWorlds. " + DescribeState(); return false; }
            if (storage.World == null) { error = "MultiWorld context 'Sublevels' exists, but its World field is null. " + DescribeState(); return false; }
            if (string.IsNullOrEmpty(storage.SavePath)) { error = "MultiWorld context 'Sublevels' exists, but SavePath is null or empty. " + DescribeState(); return false; }
            return true;
        }

        private static bool ResolveMembers(out string error)
        {
            error = null;
            if (resolved) return true;
            probeType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(ProbeTypeName, false)).FirstOrDefault(t => t != null);
            if (probeType == null) { error = "MultiWorldProbe type not found. Is MultiWorld 0.7.14 installed?"; return false; }
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            loadedWorldsField = probeType.GetField("LoadedWorlds", flags);
            selectMethod = probeType.GetMethod("Select", flags, null, new[] { loadedWorldsField != null ? loadedWorldsField.FieldType.GetGenericArguments().FirstOrDefault() : null }, null);
            activeContextMethod = probeType.GetMethod("ActiveContext", flags);
            mainThreadIdField = probeType.GetField("mainThreadId", flags);
            if (loadedWorldsField == null || selectMethod == null || activeContextMethod == null || mainThreadIdField == null)
            { error = "MultiWorld private context API differs from the inspected 0.7.14 build; refusing unsafe world access."; return false; }
            Type contextType = loadedWorldsField.FieldType.GetGenericArguments()[0];
            contextWorldField = contextType.GetField("World", flags);
            contextNameField = contextType.GetField("Name", flags);
            contextSavePathField = contextType.GetField("SavePath", flags);
            if (contextWorldField == null || contextNameField == null || contextSavePathField == null)
            { error = "MultiWorld context fields changed; refusing unsafe world access."; return false; }
            resolved = true;
            return true;
        }

        /// <summary>Number of open MultiWorld contexts, or -1 if the bridge cannot be resolved.</summary>
        public static int LoadedWorldCount()
        {
            string ignored;
            if (!ResolveMembers(out ignored)) return -1;
            IEnumerable contexts = loadedWorldsField.GetValue(null) as IEnumerable;
            if (contexts == null) return 0;
            int n = 0;
            foreach (object c in contexts) if (c != null) n++;
            return n;
        }

        public static string DescribeState()
        {
            try
            {
                string resolutionError;
                if (!ResolveMembers(out resolutionError)) return "bridge=unresolved; reason=" + resolutionError;
                IEnumerable contexts = loadedWorldsField.GetValue(null) as IEnumerable;
                if (contexts == null) return "bridge=resolved; LoadedWorlds=null; " + DescribeActiveContext();
                List<string> entries = new List<string>();
                int count = 0;
                foreach (object context in contexts)
                {
                    count++;
                    if (context == null) { entries.Add("<null>"); continue; }
                    Type type = context.GetType();
                    object closedValue = type.GetField("Closed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(context);
                    string name = contextNameField.GetValue(context) as string;
                    World world = contextWorldField.GetValue(context) as World;
                    string path = contextSavePathField.GetValue(context) as string;
                    entries.Add("name=" + (name ?? "<null>") + ",closed=" + (closedValue ?? (object)"<unknown>") +
                        ",world=" + (world == null ? "null" : ((object)world).GetType().Name) +
                        ",savePath=" + (string.IsNullOrEmpty(path) ? "<empty>" : path));
                }
                return "bridge=resolved; mainThread=" + mainThreadIdField.GetValue(null) +
                    "; currentThread=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                    "; loadedWorldCount=" + count + "; worlds=[" + string.Join(" | ", entries.ToArray()) + "]; " + DescribeActiveContext();
            }
            catch (Exception e) { return "diagnosticException=" + e.GetType().Name + ": " + e.Message; }
        }

        private static string DescribeActiveContext()
        {
            try
            {
                object active = activeContextMethod.Invoke(null, null);
                if (active == null) return "activeContext=null";
                string name = contextNameField.GetValue(active) as string;
                World world = contextWorldField.GetValue(active) as World;
                return "activeContext=" + (name ?? "<unnamed>") + "; activeWorld=" + (world == null ? "null" : world.GetType().Name);
            }
            catch (Exception e) { return "activeContextError=" + e.GetType().Name + ": " + e.Message; }
        }

        public static StorageWorld FindStorageWorld()
        {
            string ignored;
            if (!ResolveMembers(out ignored)) return null;
            IEnumerable contexts = loadedWorldsField.GetValue(null) as IEnumerable;
            if (contexts == null) return null;
            foreach (object context in contexts)
            {
                if (context == null || (bool)(context.GetType().GetField("Closed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(context) ?? false)) continue;
                string name = contextNameField.GetValue(context) as string;
                if (!string.Equals(name, "Sublevels", StringComparison.OrdinalIgnoreCase)) continue;
                return new StorageWorld {
                    Context = context, Name = name,
                    World = contextWorldField.GetValue(context) as World,
                    SavePath = contextSavePathField.GetValue(context) as string
                };
            }
            return null;
        }

        /// <summary>
        /// Selects the storage context only for the duration of the operation, then restores the
        /// prior MultiWorld context even if the operation throws. Call only on the game main thread.
        /// </summary>
        public static T InStorageWorld<T>(Func<World, T> action)
        {
            if (action == null) throw new ArgumentNullException("action");
            EnsureMainThread();
            StorageWorld storage = FindStorageWorld();
            if (storage == null || storage.World == null) throw new InvalidOperationException("Sublevels MultiWorld context is unavailable.");
            object previous = activeContextMethod.Invoke(null, null);
            // MultiWorld's Select method requires a non-null WorldContext. If no context is
            // active, we cannot safely restore it after temporarily selecting Sublevels.
            if (previous == null)
                throw new InvalidOperationException("MultiWorld has no active context; refusing a temporary storage-world switch.");
            if (object.ReferenceEquals(previous, storage.Context)) return action(storage.World);
            try
            {
                selectMethod.Invoke(null, new[] { storage.Context });
                return action(storage.World);
            }
            finally
            {
                selectMethod.Invoke(null, new[] { previous });
            }
        }

        private static void EnsureMainThread()
        {
            string error;
            if (!ResolveMembers(out error)) throw new InvalidOperationException(error);
            int expected = (int)mainThreadIdField.GetValue(null);
            if (expected == 0 || System.Threading.Thread.CurrentThread.ManagedThreadId != expected)
                throw new InvalidOperationException("Sublevels storage access must run on MultiWorld's game main thread.");
        }

        public static void FlushStorageChunks()
        {
            InStorageWorld(world =>
            {
                if (world.ChunkCache == null) throw new InvalidOperationException("Sublevels ChunkCluster is unavailable during flush.");
                world.ChunkCache.Save();
            });
        }

        public static void InStorageWorld(Action<World> action)
        {
            InStorageWorld<object>(w => { action(w); return null; });
        }
    }
}
