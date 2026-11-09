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

        public static void StartTrace(double seconds, string why = "start-up")
        {
            traceUntil = DateTime.Now.AddSeconds(seconds);
            lastSummary = DateTime.Now;
            lock (stepMs)
            {
                stepsSeen.Clear();
                stepCounts.Clear();
                stepMs.Clear();
                lastStep = null;
                lastFrameAt = 0;
                frameGaps.Clear();
            }
            Info($"Trace ({why}) on for {seconds:0} s: per second, how often each step ran and the time it took");
        }

        // Timing: the time from one step to the next is put down to the first of the two.
        private static readonly System.Collections.Generic.Dictionary<string, double> stepMs = new System.Collections.Generic.Dictionary<string, double>();
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private static string lastStep;
        private static double lastStepAt, lastFrameAt;
        private static readonly System.Collections.Generic.List<double> frameGaps = new System.Collections.Generic.List<double>();

        /// <summary>Adds time spent somewhere other than the per-frame steps (e.g. the HUD update), while tracing.</summary>
        public static void Time(string name, double ms)
        {
            if (DateTime.Now > traceUntil) return;
            lock (stepMs)
            {
                stepMs.TryGetValue(name, out double t);
                stepMs[name] = t + ms;
                stepCounts.TryGetValue(name, out int n);
                stepCounts[name] = n + 1;
            }
        }

        public static void Step(string name)
        {
            if (DateTime.Now > traceUntil) return;
            if (stepsSeen.Add(name)) Write("TRACE", "first: " + name);
            double now = clock.Elapsed.TotalMilliseconds;
            lock (stepMs)
            {
                // The previous step ran until now. "game" marks the end of the plugin's work, so the game's own frame time isn't counted.
                if (lastStep != null && lastStep != "game" && now - lastStepAt < 200)
                {
                    stepMs.TryGetValue(lastStep, out double t);
                    stepMs[lastStep] = t + (now - lastStepAt);
                }
                lastStep = name; lastStepAt = now;
                if (name == "frame")
                {
                    if (lastFrameAt > 0) frameGaps.Add(now - lastFrameAt);
                    lastFrameAt = now;
                }
                stepCounts.TryGetValue(name, out int n);
                stepCounts[name] = n + 1;
                if ((DateTime.Now - lastSummary).TotalSeconds >= 1)
                {
                    lastSummary = DateTime.Now;
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var kv in stepCounts)
                        parts.Add(stepMs.TryGetValue(kv.Key, out double ms) && ms >= 0.05 ? $"{kv.Key} x{kv.Value} {ms:0.0}ms" : $"{kv.Key} x{kv.Value}");
                    string frames = "";
                    if (frameGaps.Count > 0)
                    {
                        frameGaps.Sort();
                        double avg = 0; foreach (var g in frameGaps) avg += g; avg /= frameGaps.Count;
                        frames = $" | frames: avg {avg:0.0}ms, worst {frameGaps[frameGaps.Count - 1]:0.0}ms";
                    }
                    stepCounts.Clear(); stepMs.Clear(); frameGaps.Clear();
                    Write("TRACE", string.Join(", ", parts) + frames);
                }
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
