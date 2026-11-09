using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceDisplay.Core
{
    /// <summary>
    /// A world made from a Workshop download doesn't get RaceDisplayCourses.json copied into its new save folder,
    /// but the downloaded copy has it. This finds RaceDisplayCourses.json files in Steam's Workshop downloads, so the one from the Workshop item this
    /// world was started from can be used.
    /// </summary>
    public static class WorkshopCourses
    {
        public static List<string> FindFiles()
        {
            var found = new List<string>();
            var roots = new List<string>();
            foreach (var lib in SteamLibraries())
            {
                string content = Path.Combine(lib, "steamapps", "workshop", "content");
                if (Directory.Exists(content)) roots.Add(content);
            }
            foreach (var root in roots)
            {
                try
                {
                    // content\<app id>\<item id>\ (and one folder further down)
                    foreach (var app in Directory.EnumerateDirectories(root))
                        foreach (var item in Directory.EnumerateDirectories(app))
                        {
                            AddIfThere(item, found);
                            foreach (var sub in Directory.EnumerateDirectories(item)) AddIfThere(sub, found);
                        }
                }
                catch (Exception) { }
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddIfThere(string folder, List<string> found)
        {
            string f = Path.Combine(folder, GateSettings.WorldCourses.FileName);
            if (File.Exists(f)) found.Add(f);
        }

        /// <summary>Steam's install folder and its other library folders (libraryfolders.vdf).</summary>
        private static IEnumerable<string> SteamLibraries()
        {
            var libs = new List<string>();
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (key?.GetValue("SteamPath") is string p) libs.Add(p.Replace('/', Path.DirectorySeparatorChar));
            }
            catch (Exception) { }
            libs.Add(@"C:\Program Files (x86)\Steam");
            foreach (var steam in libs.ToList())
            {
                try
                {
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch (Exception) { }
            }
            return libs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The Workshop item id for a course file in Steam's downloads (content\&lt;app&gt;\&lt;item id&gt;\...), or null
        /// for one that isn't in a Workshop item folder.
        /// </summary>
        public static string ItemId(string courseFile)
        {
            var parts = Path.GetFullPath(courseFile).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i + 2 < parts.Length; i++)
                if (string.Equals(parts[i], "content", StringComparison.OrdinalIgnoreCase) && i > 0
                    && string.Equals(parts[i - 1], "workshop", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 2];   // content\<app id>\<item id>
            return null;
        }
    }
}
