using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceDisplay.Core
{
    /// <summary>
    /// A world made from a Workshop download doesn't get RaceDisplayCourses.json copied into its new save folder,
    /// but the downloaded copy has it. This finds RaceDisplayCourses.json files in Steam's Workshop downloads and the
    /// game's own folders (not other saves), so the one that fits this world can be used.
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
            // The game's own folders (downloads it keeps itself), but not the saves.
            try
            {
                string se2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2");
                if (Directory.Exists(se2))
                    foreach (var f in Directory.EnumerateFiles(se2, GateSettings.WorldCourses.FileName, SearchOption.AllDirectories))
                        if (f.IndexOf(Path.DirectorySeparatorChar + "SaveGames" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                            found.Add(f);
            }
            catch (Exception) { }
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
        /// How well a course fits this world: the share of its gates and checkpoints that sit on (inside or next to)
        /// a grid that's here. A course from a different world fits nothing.
        /// </summary>
        public static double Fit(GateSettings.SavedCourse course, List<NamedGrid> grids)
        {
            if (course?.Gate == null || grids == null || grids.Count == 0) return 0;
            var gates = new List<GateSettings.SavedGate> { course.Gate };
            if (course.Finish != null) gates.Add(course.Finish);
            if (course.Checkpoints != null) gates.AddRange(course.Checkpoints);
            int onGrid = 0;
            foreach (var g in gates)
            {
                if (g?.Center == null || g.Center.Length < 3) continue;
                var c = new Vec3D(g.Center[0], g.Center[1], g.Center[2]);
                foreach (var grid in grids)
                {
                    var b = grid.Box;
                    double reach = Math.Max(b.Width, Math.Max(b.Height, b.Depth)) / 2 + 15;
                    if ((b.Center - c).Length <= reach) { onGrid++; break; }
                }
            }
            return gates.Count == 0 ? 0 : onGrid / (double)gates.Count;
        }
    }
}
