using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RaceDisplay.Core
{
    /// <summary>Saved to RaceDisplay.json so the gate survives restarts.</summary>
    public sealed class GateSettings
    {
        // Default gate size when placed (metres). Depth is along the flight direction.
        public double Width { get; set; } = 40;
        public double Height { get; set; } = 30;
        public double Depth { get; set; } = 10;

        public double MinLapSeconds { get; set; } = 2.0;
        /// <summary>Metres you must fly away from the gate before the next pass counts.</summary>
        public double RearmDistance { get; set; } = 30.0;

        // Ctrl+J / Ctrl+K: extra space added on every side of a checkpoint grid's box, and how far away a grid can be.
        public double GridPadding { get; set; } = 2.0;
        /// <summary>Fit gates/checkpoints to the hole in a ring/arch instead of the whole grid (legs ignored).</summary>
        public bool FitToOpening { get; set; } = true;
        public double GridSearchDistance { get; set; } = 1000.0;
        public bool ShowGateOutline { get; set; } = true;

        /// <summary>Always-visible markers on checkpoints and the gate (Ctrl+N toggles).</summary>
        public bool ShowMarkers { get; set; } = true;
        /// <summary>Only the next few checkpoints (MarkersAhead) have markers, not the whole course.</summary>
        public bool LimitMarkers { get; set; }
        public int MarkersAhead { get; set; } = 3;
        /// <summary>Marker size as a share of the distance to it (keeps it the same size on screen). 0.03 = 3%.</summary>
        public double MarkerSize { get; set; } = 0.03;

        // Saved gate (null when none placed). Stored per world name so each save keeps its own gate.
        public SavedGate Gate { get; set; }
        /// <summary>Separate finish gate (sprint course); null = the gate is start and finish.</summary>
        public SavedGate FinishGate { get; set; }

        /// <summary>Checkpoints for every world (each one remembers its world).</summary>
        public List<SavedGate> Checkpoints { get; set; } = new List<SavedGate>();

        /// <summary>Seconds added to a lap for each checkpoint missed.</summary>
        public double PenaltySeconds { get; set; } = 5.0;

        // ---- Crash penalty: a sudden big loss of speed while going fast counts as a crash.
        public double CrashPenaltySeconds { get; set; } = 5.0;
        /// <summary>After a crash penalty, no other crash penalty for this many seconds (bouncing off a wall, etc).</summary>
        public double CrashImmunitySeconds { get; set; } = 5.0;
        /// <summary>Only crashes at or above this speed (m/s) count.</summary>
        public double CrashMinSpeed { get; set; } = 20.0;
        /// <summary>How hard the stop must be (m/s²). Lower = more sensitive. 20 is about 2 g.</summary>
        public double CrashDeceleration { get; set; } = 20.0;
        /// <summary>Minimum speed lost in the impact (m/s), so small scrapes don't count.</summary>
        public double CrashMinSpeedLoss { get; set; } = 8.0;
        /// <summary>The impact must also take away at least this share of your speed (0.25 = 25%).</summary>
        public double CrashMinSpeedLossFraction { get; set; } = 0.25;

        /// <summary>Records per course (key: world | checkpoint count | start gate position).</summary>
        public Dictionary<string, CourseRecord> Courses { get; set; } = new Dictionary<string, CourseRecord>();

        public sealed class CourseRecord
        {
            /// <summary>Best lap on this course (including penalties).</summary>
            public double? BestLap { get; set; }
            /// <summary>Lap times at each checkpoint on that best lap (flown, no penalties); null = missed.</summary>
            public List<double?> BestLapSplits { get; set; } = new List<double?>();
            /// <summary>Best ever time at each checkpoint (lap time, flown), from any lap: the 'purple' splits.</summary>
            public List<double?> BestSplits { get; set; } = new List<double?>();
            /// <summary>Best ever time for each sector (start to CP 1, CP 1 to CP 2, ..., last CP to finish).</summary>
            public List<double?> BestSectors { get; set; } = new List<double?>();
            /// <summary>Path of the best lap: [x, y, z, lap time] every quarter second, for the live gap.</summary>
            public List<double[]> BestLapTrace { get; set; }
            /// <summary>Shape of the ship that set the best lap: block boxes in grid coordinates [min x, y, z, max x, y, z] (metres), for the ghost projection.</summary>
            public List<double[]> BestLapShip { get; set; }
            /// <summary>Speed around the best lap: [share of the lap's distance 0..1, speed m/s], for the speed graph.</summary>
            public List<double[]> BestLapSpeed { get; set; }
            /// <summary>Finished races (best 5 per lap count kept).</summary>
            public List<RaceEntry> History { get; set; } = new List<RaceEntry>();
            /// <summary>Best times on this course for each ship you've raced it in, by ship (grid) name.</summary>
            public Dictionary<string, ShipRecord> Ships { get; set; } = new Dictionary<string, ShipRecord>();
            /// <summary>Best total race time, keyed by number of laps.</summary>
            public Dictionary<int, double> BestRace { get; set; } = new Dictionary<int, double>();
        }

        /// <summary>How long the results panel stays up after the finish before fading out (seconds).</summary>
        public double ResultsSeconds { get; set; } = 0.0; // 0 = stay until the next race

        /// <summary>Shortcut per action (key = action name). Missing entries use the defaults.</summary>
        public Dictionary<string, KeyBinding> KeyBindings { get; set; } = new Dictionary<string, KeyBinding>();

        public KeyBinding GetBinding(GateKey key) =>
            KeyBindings.TryGetValue(key.ToString(), out var b) && b != null ? b : Core.KeyBindings.Default(key);

        public void SetBinding(GateKey key, KeyBinding b)
        {
            if (b == null || b.SameAs(Core.KeyBindings.Default(key))) KeyBindings.Remove(key.ToString());
            else KeyBindings[key.ToString()] = b;
        }

        // ---- Countdown start
        /// <summary>The 3-2-1-GO countdown when you stop near the start gate. Off: races only start by flying through the gate.</summary>
        public bool CountdownEnabled { get; set; } = true;
        /// <summary>Course edit mode: show the best lap's racing line.</summary>
        public bool EditRacingLine { get; set; } = true;
        /// <summary>Course edit mode: gates and checkpoints are placed in open air (no grid).</summary>
        public bool EditAirPlace { get; set; }
        /// <summary>Course edit mode: gates and checkpoints are set by picking two corner blocks (like partial copy).</summary>
        public bool EditBlockPick { get; set; }
        /// <summary>Checkpoint dots and sector times coloured like F1 timing (purple / green / yellow).</summary>
        public bool SectorColours { get; set; } = true;
        /// <summary>Racing line colours: 0 = plain magenta, 1 = by speed (red slow, yellow, green fast), 2 = braking / accelerating.</summary>
        public int RacingLineColourMode { get; set; } = 1;
        /// <summary>A small top-down map of the course in a corner of the HUD.</summary>
        public bool ShowMinimap { get; set; } = true;
        /// <summary>Minimap size (pixels at 100 % HUD size).</summary>
        public double MinimapSize { get; set; } = 260;
        /// <summary>While you're moving the minimap zooms in round you: this much of the course across (metres).</summary>
        public double MinimapZoom { get; set; } = 2000;
        /// <summary>Worlds whose current course is reversed (flown backwards); it keeps separate records.</summary>
        public HashSet<string> ReversedWorlds { get; set; } = new HashSet<string>();
        /// <summary>Course edit mode: size of a gate / checkpoint placed in open air (no grid), metres.</summary>
        public double AirCheckpointSize { get; set; } = 30;
        /// <summary>Course edit mode: how far ahead of you an air checkpoint is placed, metres.</summary>
        public double AirCheckpointDistance { get; set; } = 40;
        /// <summary>Replay camera: true = orbit (fly the camera around, it keeps aiming at the ghost), false = chase.</summary>
        public bool ReplayOrbit { get; set; } = true;
        /// <summary>The replay uses the game's cinematic spectator (smooth movement, its look-around keys and lock).</summary>
        public bool ReplayCinematic { get; set; } = true;
        /// <summary>How close to the start gate (m) you must stop for the 3-2-1 countdown.</summary>
        public double CountdownDistance { get; set; } = 50.0;
        /// <summary>Stop within CountdownDistance of the start gate for this long to begin a 3-2-1 countdown (0 = off).</summary>
        public double CountdownHoldSeconds { get; set; } = 1.0;

        /// <summary>Fastest lap ever (all races).</summary>
        public double? BestSeconds { get; set; }

        /// <summary>Laps per race (J > Race tab). 0 = unlimited.</summary>
        public int LapTarget { get; set; } = 0;

        /// <summary>Practice laps before the race starts (0 = none). Timed, but not part of the race or records.</summary>
        public int PracticeLaps { get; set; } = 0;

        // ---- Panels
        public bool ShowLapList { get; set; } = true;
        public bool ShowSplits { get; set; } = true;
        public bool ShowRaceStats { get; set; } = true;
        /// <summary>Race results compare against this ship's best instead of the course best (Ctrl+B switches).</summary>
        public bool ResultsForShip { get; set; }
        /// <summary>Name put on ghost files you export (optional).</summary>
        public string PilotName { get; set; }
        public bool ShowBestBoxes { get; set; } = true;
        /// <summary>Lap list on the right and splits on the left.</summary>
        public bool SwapSides { get; set; } = false;
        /// <summary>HUD labels on checkpoints and an arrow to the next one when it's off screen.</summary>
        public bool ShowCheckpointArrow { get; set; } = true;
        /// <summary>Marker showing where your best lap was at this point in the lap.</summary>
        public bool ShowGhost { get; set; } = true;
        /// <summary>Draw the ghost as a hologram of the ship that set the best lap (instead of a small diamond).</summary>
        public bool GhostProjection { get; set; } = true;
        /// <summary>Line through the world along your best lap's path.</summary>
        public bool ShowRacingLine { get; set; } = true;
        /// <summary>After the finish, the table shows sector times (time between checkpoints) instead of lap times at each checkpoint.</summary>
        public bool SectorTable { get; set; } = true;

        // ---- Sounds (game sound events; any FMOD event path the game has can go here)
        public bool Sounds { get; set; } = true;
        public string SoundCountdown { get; set; } = "event:/UI/HudClick";
        public string SoundGo { get; set; } = "event:/UI/HudClick";
        public string SoundCheckpoint { get; set; } = "event:/UI/HudMouseOver";
        public string SoundLap { get; set; } = "event:/UI/HudClick";
        public string SoundBest { get; set; } = "event:/UI/HudClick";
        public string SoundMissed { get; set; } = "event:/UI/HudMouseOver";

        /// <summary>Draw the HUD with the game's real UI (dark panels, text on top). False = old debug-draw panels.</summary>
        public bool UseGameUi { get; set; } = true;

        // ---- HUD look. Sizes are for 1080p and scale with screen height.
        /// <summary>Overall size of the panels and text.</summary>
        public double HudScale { get; set; } = 1.0;
        /// <summary>Height in pixels (at 1080p) of a line of text at scale 1. Raise it if lines overlap, lower it if there are gaps.</summary>
        public double FontHeightPx { get; set; } = 22;
        /// <summary>
        /// Panel background opacity, 0 (clear) to 255 (solid black). With the game UI the text always stays
        /// bright. (The debug-draw fallback caps this at 110, because there the panel is drawn over the text.)
        /// </summary>
        public byte BackgroundOpacity { get; set; } = 210;

        /// <summary>Use the game's UI background opacity (Options > Interface) for the race panels instead of Panel darkness.</summary>
        public bool UseGameUiOpacity { get; set; } = true;

        /// <summary>Going through the start gate backwards doesn't start a race.</summary>
        public bool ForwardStartOnly { get; set; } = true;

        /// <summary>The minimap zooms in and follows you while you're moving. Off: it always shows the whole course.</summary>
        public bool MinimapFollow { get; set; } = false;

        /// <summary>Race HUD only: the game's own HUD (flight, toolbar, character, hints) is hidden; the race HUD stays.</summary>
        public bool RaceHudOnly { get; set; }

        /// <summary>How many laps the lap list shows before scrolling.</summary>
        public int MaxLapRows { get; set; } = 10;

        /// <summary>A saved course: a name, its world, the start/finish gate and the checkpoints in order.</summary>
        public sealed class SavedCourse
        {
            public string Name { get; set; }
            public string World { get; set; }
            public SavedGate Gate { get; set; }
            public SavedGate Finish { get; set; }
            public List<SavedGate> Checkpoints { get; set; } = new List<SavedGate>();
            /// <summary>Flown the other way round (Reverse course): has its own best times.</summary>
            public bool Reversed { get; set; }
            /// <summary>Named parts of the course, shown on the minimap and Race Map.</summary>
            public List<NamedSector> Sectors { get; set; } = new List<NamedSector>();
        }

        /// <summary>
        /// A named part of the course ("Canyon run"): from one point of the route to a later one. Points count along the
        /// route: 0 = the start (or start/finish) gate, 1 = CP 1, 2 = CP 2 ..., and the last = the finish (or the
        /// start/finish gate again at the end of the lap).
        /// </summary>
        public sealed class NamedSector
        {
            public string Name { get; set; }
            public int From { get; set; }
            public int To { get; set; }
        }

        /// <summary>The current course's named sectors (this world only; kept in the world's save, not here).</summary>
        public List<NamedSector> CourseSectors { get; set; } = new List<NamedSector>();

        /// <summary>Courses kept in a world's save folder (RaceDisplayCourses.json), so they travel with the world (e.g. on the Workshop).</summary>
        public sealed class WorldCourses
        {
            public int Version { get; set; } = 1;
            /// <summary>The course set up when it was saved (loaded automatically by players who have no course in this world).</summary>
            public SavedCourse Current { get; set; }
            public List<SavedCourse> Courses { get; set; } = new List<SavedCourse>();

            public const string FileName = "RaceDisplayCourses.json";
            private static readonly JsonSerializerOptions WJson = new JsonSerializerOptions { WriteIndented = true };

            public static WorldCourses Load(string folder)
            {
                try
                {
                    string path = Path.Combine(folder, FileName);
                    if (File.Exists(path)) return JsonSerializer.Deserialize<WorldCourses>(File.ReadAllText(path), WJson);
                }
                catch (Exception) { }
                return null;
            }

            public string ToJson() => JsonSerializer.Serialize(this, WJson);
            public void Save(string folder) => File.WriteAllText(Path.Combine(folder, FileName), ToJson());
        }

        /// <summary>Courses saved from the Courses tab, for every world.</summary>
        public List<SavedCourse> SavedCourses { get; set; } = new List<SavedCourse>();

        /// <summary>Name of the course loaded in each world (key: world). Missing = not saved.</summary>
        public Dictionary<string, string> CurrentCourseName { get; set; } = new Dictionary<string, string>();

        public sealed class ShipRecord
        {
            public double? BestLap { get; set; }
            /// <summary>Best total race time, keyed by number of laps.</summary>
            public Dictionary<int, double> BestRace { get; set; } = new Dictionary<int, double>();
            public int Races { get; set; }
            public double TopSpeed { get; set; }
            public double MaxTurnG { get; set; }
            public DateTime LastRace { get; set; }
            /// <summary>Best time for each sector in this ship (for its possible lap).</summary>
            public List<double?> BestSectors { get; set; } = new List<double?>();
            /// <summary>Speed round this ship's best lap: [share of the lap, speed], for the graph.</summary>
            public List<double[]> BestLapSpeed { get; set; }
            /// <summary>Path and shape of this ship's best lap (same format as the course's), for its replay and export.</summary>
            public List<double[]> BestLapTrace { get; set; }
            public List<double[]> BestLapShip { get; set; }
            public List<double?> BestLapSplits { get; set; }
        }

        public sealed class RaceEntry
        {
            public string Ship { get; set; }
            public DateTime Date { get; set; }
            public int Laps { get; set; }
            public double Total { get; set; }
            public double Penalty { get; set; }
            public double BestLap { get; set; }
            public int Missed { get; set; }
            public int Crashes { get; set; }
            public double TopSpeed { get; set; }
        }

        public sealed class SavedGate
        {
            public string World { get; set; }
            public double[] Center { get; set; }
            public double[] Forward { get; set; }
            public double[] Up { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Depth { get; set; }

            public static SavedGate From(TimingGate g, string world) => new SavedGate
            {
                World = world,
                Center = new[] { g.Center.X, g.Center.Y, g.Center.Z },
                Forward = new[] { g.Forward.X, g.Forward.Y, g.Forward.Z },
                Up = new[] { g.Up.X, g.Up.Y, g.Up.Z },
                Width = g.Width,
                Height = g.Height,
                Depth = g.Depth,
            };

            public TimingGate ToGate() => new TimingGate(
                new Vec3D(Center[0], Center[1], Center[2]),
                new Vec3D(Forward[0], Forward[1], Forward[2]),
                new Vec3D(Up[0], Up[1], Up[2]),
                Width, Height, Depth);
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static GateSettings Load(string path)
        {
            GateSettings s = null;
            try
            {
                if (File.Exists(path))
                    s = JsonSerializer.Deserialize<GateSettings>(File.ReadAllText(path), Json);
            }
            catch (Exception)
            {
                // Corrupt file: fall back to defaults rather than breaking the game.
            }
            s = s ?? new GateSettings();
            // Courses belong in each world's save, never here (an old file may still have some: drop them).
            s.Gate = null; s.FinishGate = null;
            s.Checkpoints = new List<SavedGate>();
            s.SavedCourses = new List<SavedCourse>();
            s.CurrentCourseName = new Dictionary<string, string>();
            s.ReversedWorlds = new HashSet<string>();
            s.CourseSectors = new List<NamedSector>();
            // Bindings for keys that no longer exist.
            s.KeyBindings ??= new Dictionary<string, KeyBinding>();
            foreach (var name in new List<string>(s.KeyBindings.Keys))
                if (!Enum.TryParse<GateKey>(name, out _)) s.KeyBindings.Remove(name);
            return s;
        }

        /// <summary>
        /// Courses (gates, checkpoints, saved courses) belong to the world they were made in and are kept in its save
        /// folder (RaceDisplayCourses.json, written when the game saves). They're only held in memory here, never
        /// written to RaceDisplay.json, so a course can't turn up in another world that happens to have the same name.
        /// </summary>
        private static readonly string[] WorldOnly = { nameof(Gate), nameof(FinishGate), nameof(Checkpoints), nameof(SavedCourses), nameof(CurrentCourseName), nameof(ReversedWorlds), nameof(CourseSectors) };

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var node = JsonSerializer.SerializeToNode(this, Json).AsObject();
                foreach (var name in WorldOnly) node.Remove(name);
                File.WriteAllText(path, node.ToJsonString(Json));
            }
            catch (Exception)
            {
                // Saving is best-effort.
            }
        }
    }
}
