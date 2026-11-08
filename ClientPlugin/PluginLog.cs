using System;
using System.IO;

namespace RaceDisplay
{
    /// <summary>Writes to %AppData%\SpaceEngineers2\AppData\RaceDisplay\RaceDisplay.log (overwritten each game start).</summary>
    internal static class PluginLog
    {
        private static readonly object Sync = new object();
        private static string path;

        public static void Start()
        {
            try
            {
                string folder = PluginPaths.Folder;
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, "RaceDisplay.log");
                File.WriteAllText(path, $"Race Display log, started {DateTime.Now}{Environment.NewLine}");
            }
            catch (Exception)
            {
                path = null;
            }
        }

        public static void Info(string message) => Write("INFO ", message);

        // ---- Start-up trace: for the first minute after entering a world, logs the first time each step runs and,
        // every second, which steps ran (so the last lines before a crash show what the plugin was doing).
        private static DateTime traceUntil = DateTime.MinValue, lastSummary;
        private static readonly System.Collections.Generic.Dictionary<string, int> stepCounts = new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.HashSet<string> stepsSeen = new System.Collections.Generic.HashSet<string>();

        public static void StartTrace(double seconds)
        {
            traceUntil = DateTime.Now.AddSeconds(seconds);
            lastSummary = DateTime.Now;
            stepsSeen.Clear();
            stepCounts.Clear();
            Info($"Start-up trace on for {seconds:0} s");
        }

        public static void Step(string name)
        {
            if (DateTime.Now > traceUntil) return;
            if (stepsSeen.Add(name)) Write("TRACE", "first: " + name);
            stepCounts.TryGetValue(name, out int n);
            stepCounts[name] = n + 1;
            if ((DateTime.Now - lastSummary).TotalSeconds >= 1)
            {
                lastSummary = DateTime.Now;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in stepCounts) parts.Add($"{kv.Key} x{kv.Value}");
                stepCounts.Clear();
                Write("TRACE", string.Join(", ", parts));
            }
        }

        public static void Error(string message, Exception e = null) =>
            Write("ERROR", e == null ? message : $"{message}{Environment.NewLine}{e}");

        private static void Write(string level, string message)
        {
            if (path == null) return;
            try
            {
                lock (Sync)
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // logging must never break the game
            }
        }
    }
}
