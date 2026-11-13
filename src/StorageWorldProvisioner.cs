using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SubLevelPhysics
{
    /// <summary>
    /// Makes the mod work on any save without hand-editing MultiWorld's config. If the save's
    /// multiworldserverconfig.xml has no world named "Sublevels", one is appended (nothing else
    /// in the file is changed). MultiWorld only reads the config when a save loads, so the
    /// session that adds the entry must be reloaded once; every later load is automatic.
    /// </summary>
    internal static class StorageWorldProvisioner
    {
        public const string StorageName = "Sublevels";
        private static bool attempted;
        public static string Status = null;   // shown to the player by 'sl' commands

        public static void Reset() { attempted = false; Status = null; }

        /// <summary>Call once MultiWorld has finished loading its worlds and Sublevels is absent.</summary>
        public static void TryProvision()
        {
            if (attempted) return;
            attempted = true;
            try
            {
                string path = Path.Combine(GameIO.GetSaveGameDir(), "multiworldserverconfig.xml");
                if (!File.Exists(path))
                {
                    Status = "MultiWorld config not found at " + path + "; cannot add the Sublevels world automatically.";
                    Log.Warning("[SubLevel][Provision] " + Status);
                    return;
                }
                XDocument doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                XElement root = doc.Root;
                if (root == null) { Status = "MultiWorld config is empty."; Log.Warning("[SubLevel][Provision] " + Status); return; }

                var worlds = root.Elements("World").ToList();
                if (worlds.Any(w => string.Equals((string)w.Attribute("name"), StorageName, StringComparison.OrdinalIgnoreCase)))
                {
                    Status = "The Sublevels world is already in the MultiWorld config but did not load. Check the MultiWorld log lines for errors.";
                    Log.Warning("[SubLevel][Provision] " + Status);
                    return;
                }

                int nextId = worlds.Select(w => { int v; return int.TryParse((string)w.Attribute("id"), out v) ? v : 0; })
                                   .DefaultIfEmpty(0).Max() + 1;

                // Derive the storage world from the save's own primary world where possible, so
                // we never hard-code anything specific to one player's world.
                XElement primary = worlds.FirstOrDefault(w => string.Equals((string)w.Attribute("nativeWorld"), "true", StringComparison.OrdinalIgnoreCase))
                                   ?? worlds.FirstOrDefault();
                string size = PropertyValue(primary, "WorldGenSize") ?? "6144";
                string mode = PropertyValue(primary, "GameMode") ?? "GameModeSurvival";

                XElement entry = new XElement("World",
                    new XAttribute("id", nextId),
                    new XAttribute("name", StorageName),
                    new XAttribute("icon", nextId + ".png"),
                    Prop("GameWorld", "RWG"),
                    Prop("GameName", "SublevelsStorage"),
                    Prop("WorldGenSeed", "SublevelsStorageSeed"),
                    Prop("WorldGenSize", size),
                    Prop("GameMode", mode));

                File.Copy(path, path + ".sublevels-backup", true);
                root.Add(new XText("  "), entry, new XText(Environment.NewLine));
                doc.Save(path);

                Status = "Sublevels storage world was added to this save's MultiWorld config. Exit to the main menu and load the save again; no further setup is needed.";
                Log.Warning("[SubLevel][Provision] " + Status + " Config: " + path + " (backup: " + path + ".sublevels-backup)");
            }
            catch (Exception e)
            {
                Status = "Could not add the Sublevels world to the MultiWorld config automatically: " + e.Message;
                Log.Error("[SubLevel][Provision] " + e);
            }
        }

        private static XElement Prop(string name, string value)
        {
            return new XElement("property", new XAttribute("name", name), new XAttribute("value", value));
        }

        private static string PropertyValue(XElement world, string name)
        {
            if (world == null) return null;
            XElement p = world.Elements("property").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            return p == null ? null : (string)p.Attribute("value");
        }
    }
}
