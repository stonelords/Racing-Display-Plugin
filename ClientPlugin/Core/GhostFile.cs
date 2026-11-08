using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RaceDisplay.Core
{
    /// <summary>
    /// A best lap saved to its own file so players can share it: the path flown (and how the ship was turned),
    /// the ship's shape, its speed round the lap and its checkpoint times. Positions are world positions, so a
    /// ghost only lines up on the same course in the same world (checked against the start gate).
    /// </summary>
    public sealed class GhostFile
    {
        public int Version { get; set; } = 1;
        public string Pilot { get; set; }
        public string Ship { get; set; }
        public string Course { get; set; }
        public DateTime Date { get; set; }
        public double LapTime { get; set; }
        /// <summary>Start/finish gate centre [x, y, z] and checkpoint count, to tell whether it fits the course here.</summary>
        public double[] GateCenter { get; set; }
        public int Checkpoints { get; set; }
        public List<double?> Splits { get; set; }
        public List<double[]> Trace { get; set; }
        public List<double[]> ShipShape { get; set; }
        public List<double[]> Speed { get; set; }

        /// <summary>The file it was loaded from (not saved in the file).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string Path { get; set; }

        public const string Extension = ".rdghost.json";
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>True if it was flown on this course: same number of checkpoints and the start gate within 50 m.</summary>
        public bool Fits(TimingGate gate, int checkpoints)
        {
            if (gate == null || GateCenter == null || GateCenter.Length < 3) return false;
            var c = new Vec3D(GateCenter[0], GateCenter[1], GateCenter[2]);
            return Checkpoints == checkpoints && (c - gate.Center).Length < 50;
        }

        public string Label => $"{(string.IsNullOrWhiteSpace(Pilot) ? "" : Pilot + " - ")}{Ship ?? "ship"} {LapTimer.Format(LapTime)}";

        public string Save(string folder)
        {
            Directory.CreateDirectory(folder);
            string name = Safe($"{Course ?? "course"} - {Ship ?? "ship"} - {LapTimer.Format(LapTime).Replace(':', '.')}");
            string path = System.IO.Path.Combine(folder, name + Extension);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            Path = path;
            return path;
        }

        public static GhostFile Load(string path)
        {
            try
            {
                var g = JsonSerializer.Deserialize<GhostFile>(File.ReadAllText(path), Json);
                if (g?.Trace == null || g.Trace.Count < 2) return null;
                g.Path = path;
                return g;
            }
            catch { return null; }
        }

        public static List<GhostFile> LoadAll(string folder)
        {
            var list = new List<GhostFile>();
            try
            {
                if (!Directory.Exists(folder)) return list;
                foreach (var f in Directory.GetFiles(folder, "*" + Extension).OrderBy(x => x))
                {
                    var g = Load(f);
                    if (g != null) list.Add(g);
                }
            }
            catch { /* unreadable folder: nothing listed */ }
            return list;
        }

        private static string Safe(string s)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }
    }
}
