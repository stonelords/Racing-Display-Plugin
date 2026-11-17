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
                gcGen0At = GC.CollectionCount(0); gcGen2At = GC.CollectionCount(2);
            }
            Info($"Trace ({why}) on for {seconds:0} s: per second, how often each step ran and the time it took");
        }

        // Timing: the time from one step to the next is put down to the first of the two.
        private static readonly System.Collections.Generic.Dictionary<string, double> stepMs = new System.Collections.Generic.Dictionary<string, double>();
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private static string lastStep;
        private static double lastStepAt, lastFrameAt;
        private static readonly System.Collections.Generic.List<double> frameGaps = new System.Collections.Generic.List<double>();
        private static int gcGen0At = GC.CollectionCount(0), gcGen2At = GC.CollectionCount(2);

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

        // Always on (tracing or not): a step that holds the game up for a noticeable time gets a line of its own, so a
        // freeze can be put down to the part of the plugin it happened in (or to none of it).
        private static string watchStep;
        private static double watchStepAt;

        /// <summary>Notes how long something took when it was slow enough to feel (a sound, a file, a game call).</summary>
        public static void Slow(string what, double ms)
        {
            if (ms >= 150) Write("SLOW ", $"{what} took {ms:0} ms");
        }

        public static void Step(string name)
        {
            double at = clock.Elapsed.TotalMilliseconds;
            if (watchStep != null && watchStep != "game" && at - watchStepAt >= 150)
                Write("SLOW ", $"step '{watchStep}' took {at - watchStepAt:0} ms (until '{name}')");
            watchStep = name; watchStepAt = at;
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
                        int late = 0; foreach (var g in frameGaps) if (g > 25) late++;
                        frames = $" | frames: avg {avg:0.0}ms, worst {frameGaps[frameGaps.Count - 1]:0.0}ms, over 25ms x{late}";
                    }
                    // Garbage collections since the last line: a full (gen 2) one can hold the game up for a frame or more.
                    int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);
                    frames += $" | GC {g0 - gcGen0At} small, {g2 - gcGen2At} full";
                    gcGen0At = g0; gcGen2At = g2;
                    stepCounts.Clear(); stepMs.Clear(); frameGaps.Clear();
                    Write("TRACE", string.Join(", ", parts) + frames);
                }
            }
        }

        public static void Error(string message, Exception e = null) =>
            Write("ERROR", e == null ? message : $"{message}{Environment.NewLine}{e}");

        // Lines are written by a thread of their own: a slow disk (or a virus scanner looking at the file) must never
        // hold up the game. The time on each line is when it was logged, not when it reached the file.
        private static readonly System.Collections.Concurrent.BlockingCollection<string> lines = new System.Collections.Concurrent.BlockingCollection<string>();
        private static System.Threading.Thread writer;

        // The Windows user folder and name, kept out of the log (it gets posted when asking for help).
        private static readonly string userFolder = SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        private static readonly string userName = SafeGet(() => Environment.UserName);
        private static string SafeGet(Func<string> get) { try { return get(); } catch { return null; } }

        private static string Private(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            if (!string.IsNullOrEmpty(userFolder)) message = message.Replace(userFolder, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
                message = message.Replace("\\Users\\" + userName, "\\Users\\<you>", StringComparison.OrdinalIgnoreCase);
            return message;
        }

        private static void Write(string level, string message)
        {
            if (path == null) return;
            try
            {
                lines.Add($"{DateTime.Now:HH:mm:ss.fff} {level} {Private(message)}{Environment.NewLine}");
                if (writer != null) return;
                lock (Sync)
                {
                    if (writer != null) return;
                    writer = new System.Threading.Thread(WriteLoop) { IsBackground = true, Name = "RaceDisplay log" };
                    writer.Start();
                }
            }
            catch (Exception)
            {
                // logging must never break the game
            }
        }

        private static void WriteLoop()
        {
            var batch = new System.Text.StringBuilder();
            while (true)
            {
                try
                {
                    batch.Clear();
                    batch.Append(lines.Take());                       // waits for a line
                    while (lines.TryTake(out string more)) batch.Append(more);   // and takes any others already waiting with it
                    File.AppendAllText(path, batch.ToString());
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }
    }
}
