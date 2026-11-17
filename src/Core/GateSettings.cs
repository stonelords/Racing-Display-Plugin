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
        /// <summary>Timer in the top corner (opposite the minimap), with best lap, best race and splits under it.</summary>
        public bool TimerInCorner { get; set; } = true;   // (with SwapSides: the Left layout by default)
        public int MarkersAhead { get; set; } = 3;
        /// <summary>Marker size as a share of the distance to it (keeps it the same size on screen). 0.03 = 3%.</summary>
        public double MarkerSize { get; set; } = 0.03;
        /// <summary>The marker's icon: 0 = ring (faces you), 1 = diamond, 2 = chevron, 3 = beacon.</summary>
        public int MarkerIcon { get; set; }
        /// <summary>Markers are drawn with thick lines through the game's 3D UI layer (thin plain lines if off, or if it can't be used).</summary>
        public bool MarkerThick { get; set; } = true;

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
        /// <summary>After a crash penalty, no other crash penalty until you're back above CrashRearmSpeed.</summary>
        public bool CrashImmuneUntilSpeed { get; set; }
        public double CrashRearmSpeed { get; set; } = 25.0;
        /// <summary>Only crashes at or above this speed (m/s) count.</summary>
        public double CrashMinSpeed { get; set; } = 20.0;
        /// <summary>How hard the stop must be (m/s²). Lower = more sensitive. 20 is about 2 g.</summary>
        public double CrashDeceleration { get; set; } = 20.0;
        /// <summary>How crashes are found: 0 = hull damage (falls back to speed drop until damage has been seen), 1 = speed drop, 2 = either.</summary>
        public int CrashDetection { get; set; }
        /// <summary>With hits counted (Hull damage or Impact): how hard a hit must be (the game's strength, relative to the ship's mass). 0 = any.</summary>
        public double CrashMinHit { get; set; } = 0;
        /// <summary>Damage mode: collision damage needed in one hit (all blocks together) to count as a crash. 0 = any.</summary>
        public double CrashMinDamage { get; set; }
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
            /// <summary>Path of each best sector (index as <see cref="BestSectors"/>), times from the sector's start: joined up they make the possible lap.</summary>
            public List<List<double[]>> BestSectorTraces { get; set; }
            /// <summary>The course record race's path, lap by lap (keyed by number of laps): its ghost can be raced lap for lap.</summary>
            public Dictionary<int, List<List<double[]>>> BestRaceTraces { get; set; }
        }

        /// <summary>How long the results panel stays up after the finish before fading out (seconds).</summary>
        public double ResultsSeconds { get; set; } = 0.0; // 0 = stay until the next race
        /// <summary>Seconds each results graph (speed, time gap) shows before switching. 0 = speed graph only.</summary>
        /// <summary>Results graphs: 0 off, 1 speed only, 2 speed and time gap.</summary>
        public int ResultsGraphs { get; set; } = 2;
        /// <summary>Lap list penalties as icons (ring = missed checkpoint, cross = crash) or in words.</summary>
        public bool PenaltyIcons { get; set; } = true;
        /// <summary>Reset race (Ctrl+R) also teleports your ship back behind the start gate.</summary>
        public bool ResetTeleports { get; set; }

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
        public bool CountdownEnabled { get; set; } = false;
        /// <summary>Remove grids the game finds lost inside a planet as the world loads (they stop it loading).</summary>
        public bool RemoveLostGrids { get; set; } = true;
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
        /// <summary>Racing line thickness (1 = normal), opacity (%) and how far from you it's drawn (m).</summary>
        public double RacingLineWidth { get; set; } = 1.0;
        public double RacingLineOpacity { get; set; } = 100;
        public double RacingLineDistance { get; set; } = 2000;
        /// <summary>Markers on the racing line where the best lap started braking hard (BRAKE) and sped up again (GO).</summary>
        public bool BrakeMarkers { get; set; }
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
        /// <summary>Course edit mode: gates and checkpoints placed in open air are rings instead of boxes.</summary>
        public bool AirGateRound { get; set; } = true;
        /// <summary>Course edit mode: height of a gate placed in open air, metres; 0 = the same as its size (a circle / square).</summary>
        public double AirGateHeight { get; set; }
        /// <summary>Gates and checkpoints placed in open air are drawn in the world (there's no grid to see).</summary>
        public bool AirGateDraw { get; set; } = true;
        /// <summary>How air gates are drawn in a race: 0 = lines, 1 = glow (soft bands), 2 = glow drawn through the game's own 3D UI layer (its shader).</summary>
        public int AirGateLook { get; set; } = 1;
        /// <summary>The racing line and the ghost's outline are drawn through the game's 3D UI layer: thicker, smoother lines.</summary>
        public bool GlowLines { get; set; } = true;
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
        /// <summary>The settings preset last loaded or saved (Settings tab), just to show it.</summary>
        public string CurrentPreset { get; set; }
        /// <summary>The built-in presets (Racing, Practice) have been added to the preset list once (deleted ones stay deleted).</summary>
        public bool DefaultPresetsAdded { get; set; }
        /// <summary>The example Showcase cinematic has been made once (the first time you sat in a ship).</summary>
        public bool DefaultShowcaseAdded { get; set; }
        public bool ShowBestBoxes { get; set; } = true;
        /// <summary>Lap list on the right and splits on the left.</summary>
        public bool SwapSides { get; set; } = true;
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
        /// <summary>Beep at each checkpoint flown through in a race.</summary>
        public bool CheckpointSound { get; set; } = true;
        /// <summary>Play the penalty melody (Penalty.wav) for a missed checkpoint or a crash, instead of the double beep.</summary>
        public bool PenaltyMelody { get; set; } = true;
        /// <summary>Volume of the penalty melody, 0..100 %.</summary>
        public double PenaltyVolume { get; set; } = 80;

        /// <summary>Draw the HUD with the game's real UI (dark panels, text on top). False = old debug-draw panels.</summary>
        public bool UseGameUi { get; set; } = true;

        // ---- HUD look. Sizes are for 1080p and scale with screen height.
        /// <summary>Overall size of the panels and text.</summary>
        public double HudScale { get; set; } = 1.0;
        /// <summary>The whole race HUD's opacity, percent (20 to 100).</summary>
        public double HudOpacity { get; set; } = 100;
        /// <summary>Minimal HUD: while racing only the timer (with the gap), the speedometer and the next gate show.</summary>
        public bool MinimalHud { get; set; }
        /// <summary>The ghost ship's hologram: 0 = full (see-through hull and outline), 1 = outline only, 2 = a plain box.</summary>
        public int GhostDetail { get; set; } = 0;
        /// <summary>A soft glow round the edge of the screen when you pass a checkpoint (green / purple) or get a penalty (red).</summary>
        public bool EdgeGlow { get; set; } = true;
        /// <summary>A strip at the top centre as you pass a checkpoint: its number and the gap to your best lap.</summary>
        public bool CheckpointFlash { get; set; } = true;
        /// <summary>The minimap turns so your ship always points up.</summary>
        public bool MinimapHeadingUp { get; set; }
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
        /// <summary>RPI (race performance index) classes made in the RPI designer, and the one in use.</summary>
        public List<RpiClass> RpiClasses { get; set; } = new List<RpiClass>();
        public string RpiClassName { get; set; }
        /// <summary>One race spotlight page: 0 results, 1 race stats, 2 the ship, 4 lap replay, 5 race graphs, 6 ship card, 7 lap by lap, 8 intro, 9 outro.</summary>
    public sealed class SpotPage
    {
        public int Page { get; set; }
        public bool On { get; set; } = true;
        public double Seconds { get; set; }
    }

    /// <summary>Cinematics auto switch: on this course (its key), when the filmed ship flies through this gate (0 = start, n = CP n, -1 = the finish), play this cinematic ("*next*" = the next one).</summary>
    /// <summary>Race spotlight, per race file (its file name): when the spotlight starts, fly the race with the autopilot from the start, and play this cinematic.</summary>
    public sealed class SpotAuto
    {
        public bool Race { get; set; }
        public string Cinematic { get; set; }
        /// <summary>The grid (by name) this race's spotlight goes on, when it isn't the one named in the file. Set by clicking a ship in the spotlight.</summary>
        public string Grid { get; set; }
    }

    public sealed class CineTrigger
    {
        public string Course { get; set; }
        public int Gate { get; set; }
        public string Cinematic { get; set; }
        /// <summary>When it starts: 0 = as the ship flies through the gate, below 0 = this many seconds before
        /// it gets there (on the approach), above 0 = this many seconds after it's through.</summary>
        public double Offset { get; set; }
    }

    public sealed class RpiClass
        {
            public string Name { get; set; }
            /// <summary>The most RPI a ship may use, and the least it may weigh in kg (0 = no limit).</summary>
            public double Budget { get; set; }
            public double MinWeight { get; set; }
            /// <summary>RPI for each block of a type (by the game's internal block name).</summary>
            public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>();
            /// <summary>The most of a block type a ship may have (0 = banned). Not listed = no limit.</summary>
            public Dictionary<string, int> Limits { get; set; } = new Dictionary<string, int>();
            /// <summary>Lighter ships with a smaller budget: a ship under MinWeight uses the heaviest step it reaches.</summary>
            public List<RpiStep> Steps { get; set; } = new List<RpiStep>();
        }
        public sealed class RpiStep
        {
            public double Weight { get; set; }
            public double Budget { get; set; }
        }

        /// <summary>The budget a ship of this mass gets, and the least any ship may weigh, for a class with weight steps.</summary>
        public static double RpiBudgetFor(double mass, double mainWeight, double mainBudget, IEnumerable<(double Weight, double Budget)> steps)
        {
            mass = Math.Round(mass);   // (to the whole kilogram, as weights are shown)
            if (mainWeight <= 0 || mass >= mainWeight) return mainBudget;
            double reached = -1, budget = mainBudget, lowest = mainWeight, lowestBudget = mainBudget;
            foreach (var step in steps)
            {
                if (step.Weight <= mass && step.Weight > reached) { reached = step.Weight; budget = step.Budget; }
                if (step.Weight < lowest) { lowest = step.Weight; lowestBudget = step.Budget; }
            }
            return reached >= 0 ? budget : lowestBudget;   // (under every step: the lightest step's budget, and under weight)
        }
        public static double RpiLowestWeight(double mainWeight, IEnumerable<(double Weight, double Budget)> steps)
        {
            double lowest = mainWeight;
            if (mainWeight > 0) foreach (var step in steps) if (step.Weight > 0 && step.Weight < lowest) lowest = step.Weight;
            return lowest;
        }
        /// <summary>(Old: one set of values before classes. Turned into a class when loaded.)</summary>
        public Dictionary<string, double> RpiValues { get; set; }
        public double RpiBudget { get; set; }
        public double RpiMinWeight { get; set; }
        /// <summary>Race speedometer: 0 = off, 1 = with Race HUD only, 2 = always.</summary>
        public int Speedometer { get; set; } = 1;
        /// <summary>Top of the speedometer dial, in the game's speed unit. 0 = automatic (grows to suit the ship).</summary>
        public double SpeedometerMax { get; set; }
        /// <summary>With Race HUD only, keep the game's flight gyro showing; and switch it to target based.</summary>
        public bool KeepGyro { get; set; } = true;
        public bool GyroTargetBased { get; set; }
        /// <summary>Flight keys (Q W E A S D C Space) lighting up as you press them, bottom right: 0 = off, anything else = on.</summary>
        public int FlightKeys { get; set; } = 2;
        /// <summary>(Flight keys became on by default: turned on once for settings saved before that.)</summary>
        public bool FlightKeysOnByDefault { get; set; }
        /// <summary>Roll indicator: how far the ship is rolled from level (in gravity).</summary>
        public bool RollIndicator { get; set; } = true;
        /// <summary>Pitch indicator: how far the nose is above or below the horizon (in gravity).</summary>
        public bool PitchIndicator { get; set; } = true;
        /// <summary>(The roll indicator became on by default: turned on once for settings saved before that.)</summary>
        public bool RollOnByDefault { get; set; }
        /// <summary>Reset race teleport: the speed (m/s) the ship comes out of it with, along the way it faces (0 = stopped).</summary>
        public double ResetExitSpeed { get; set; }
        /// <summary>After the race, the minimap plays the laps back one after another.</summary>
        public bool MinimapLapReplay { get; set; } = true;
        /// <summary>Race spotlight: how long each page stays up, and how long a page takes to slide out or in.</summary>
        public double SpotlightPageSeconds { get; set; } = 8;
        /// <summary>Race spotlight: seconds the ship waits at the start before the autopilot and the cinematic set off.</summary>
        public double SpotlightStartDelay { get; set; } = 3;
        /// <summary>Leaderboard: seconds a ship takes over each place as it climbs (the tour) or comes on (the reveal).</summary>
        public double SpotBoardClimbSeconds { get; set; } = 1.6;
        /// <summary>The spotlight tour leaves out burned races (reset before the finish).</summary>
        public bool SpotTourSkipBurned { get; set; } = true;
        /// <summary>The cinematic that films every ship on the tour ("" = each race's own, else the current one).</summary>
        public string SpotTourCinematic { get; set; } = "";
        /// <summary>After the tour's last ship: a podium card with the top three.</summary>
        public bool SpotTourPodium { get; set; } = true;
        public double SpotTourPodiumSeconds { get; set; } = 10;
        /// <summary>The kind of race: 0 = time trial (one ship against the clock), 1 = grand prix (ships racing each other, for multiplayer).</summary>
        public int RaceType { get; set; } = 0;
        [System.Text.Json.Serialization.JsonIgnore] public bool TimeTrial => RaceType != 1;
        [System.Text.Json.Serialization.JsonIgnore] public bool GrandPrix => RaceType == 1;
        /// <summary>Filming tools (the Cinematics tab, the race spotlight and their settings) show in the menu.</summary>
        public bool Filming { get; set; } = false;
        /// <summary>The Filming setting as it is now, for the menu and the terminal's tabs.</summary>
        public static volatile bool FilmingOn = false;
        public double SpotlightSlideSeconds { get; set; } = 0.35;
        /// <summary>Race spotlight: which pages show, in what order, and how long each (0 s = the usual time).</summary>
        public List<SpotPage> SpotlightPages { get; set; } = new List<SpotPage>();
        /// <summary>Race spotlight leaderboard: ships left off it (by name), and one row a ship (its fastest race) or every race.</summary>
        public List<string> SpotBoardHiddenShips { get; set; } = new List<string>();
        public bool SpotBoardBestPerShip { get; set; } = true;
        /// <summary>Leaderboard: the order the ships ran in (names), and whether the page brings them on one at a time in that order.</summary>
        public List<string> SpotBoardOrder { get; set; } = new List<string>();
        public bool SpotBoardReveal { get; set; } = true;
        /// <summary>The cinematic being edited / played (its name), and whether race spotlight plays it on the picked ship.</summary>
        public string CinematicName { get; set; }
        public bool SpotlightCinematic { get; set; }
        /// <summary>Race spotlight: what each race file starts by itself (auto race, a cinematic), by file name.</summary>
        public Dictionary<string, SpotAuto> SpotlightAuto { get; set; } = new Dictionary<string, SpotAuto>();
        /// <summary>Debug: while a cinematic plays, its timeline shows on screen and the race HUD is hidden.</summary>
        public bool CineDebug { get; set; }
        /// <summary>Recording speed, %: the autopilot and cinematics run this much slower (the orbit camera's turn too), for a smoother recording to speed up afterwards.</summary>
        public double RecordingSpeed { get; set; } = 100;
        /// <summary>While a cinematic films the autopilot flying a race: lap, race clock and speed along the bottom.</summary>
        public bool FilmStrip { get; set; } = true;
        /// <summary>While a cinematic or the race spotlight films a ship: its name (and speed) on a tag that follows it.</summary>
        public bool FilmNameTag { get; set; } = true;
        /// <summary>While a cinematic plays: no race HUD (timer, laps, splits, minimap, speedometer, markers, messages) and none of the game's.</summary>
        public bool CineHideHud { get; set; } = true;
        /// <summary>Orbit and chase shots (and the orbit camera) follow the ship with the game's own spectator lock: smoother.</summary>
        public bool CineGameLock { get; set; } = true;
        /// <summary>Short reminders of the keys when a camera mode starts (orbit, cinematic, spotlight, replay, autopilot).</summary>
        public bool KeyHints { get; set; } = true;
        /// <summary>Cinematics: draw the picked shot's camera views (and a path's curve) in the world while editing.</summary>
        public bool CinePreview { get; set; } = true;
        /// <summary>Cinematics: switch to another cinematic when the filmed ship flies through a gate (per course).</summary>
        public bool CineAutoSwitch { get; set; }
        public List<CineTrigger> CineTriggers { get; set; } = new List<CineTrigger>();

        /// <summary>Autopilot: how fast it flies the racing line, as a % of the lap it was made from.</summary>
        public double AutopilotSpeed { get; set; } = 100;
        /// <summary>Possible line: seconds either side of each checkpoint over which the joins between laps are smoothed (0 = off).</summary>
        public double PossibleSmoothing { get; set; } = 1.5;

        /// <summary>Orbit camera: true = the view turns with the ship, false = it keeps its angle in the world.</summary>
        public bool OrbitLocked { get; set; }
        /// <summary>Orbit camera: how loosely it follows the ship (0 = fixed to it, 100 = loosest).</summary>
        public double OrbitSmooth { get; set; } = 40;
        /// <summary>Orbit camera: degrees a second it circles the ship by itself when left alone (0 = off).</summary>
        public double OrbitAutoRotate { get; set; } = 6;
        /// <summary>Orbit camera: Ctrl + mouse wheel changes the auto rotate (needs a Windows mouse hook while the camera is on).</summary>
        public bool OrbitWheel { get; set; } = true;
        /// <summary>A marker ahead of the ship where it's heading (its velocity), not where it's pointing.</summary>
        public bool VelocityMarker { get; set; } = true;
        /// <summary>Under the speedometer: the height above the ground below the ship (on a planet).</summary>
        public bool GroundHeight { get; set; } = true;
        /// <summary>Race HUD only: seconds the game's toolbar slot shows for after its number key is pressed (0 = never).</summary>
        public double ToolbarPeekSeconds { get; set; } = 2;
        /// <summary>Race HUD only: the game's toolbar stays on screen as a small strip (its slots only, shrunk).</summary>
        public bool ToolbarMini { get; set; } = true;
        /// <summary>A small gravity panel beside the attitude panel: how strong gravity is and which way it pulls on the ship.</summary>
        public bool GravityPanel { get; set; } = true;
        /// <summary>The small toolbar strip's size, percent of the toolbar's own (30 to 90).</summary>
        public double ToolbarMiniSize { get; set; } = 50;
        /// <summary>Flip ship upright: how high (m) it lifts the ship before turning it level.</summary>
        public double FlipHeight { get; set; } = 5;
        /// <summary>Flip ship upright during a race: 0 = no penalty, 1 = time penalty, 2 = counts as a crash, 3 = resets the race.</summary>
        public int FlipPenalty { get; set; } = 1;
        /// <summary>The time penalty for a flip (seconds).</summary>
        public double FlipPenaltySeconds { get; set; } = 5;
        /// <summary>Round the speedometer: the hydrogen, battery and power use rings (and their names in its side pod).</summary>
        public bool SpeedoResourceBars { get; set; } = true;
        /// <summary>The speedometer's indicator tiles and its safe speed icon, each on its own.</summary>
        public bool SpeedoPowerTile { get; set; } = true;
        public bool SpeedoDampenerTile { get; set; } = true;
        public bool SpeedoParkingTile { get; set; } = true;
        public bool SpeedoAntennaTile { get; set; } = true;
        public bool SpeedoSafeSpeed { get; set; } = true;
        /// <summary>The gap to the best lap (+0.140 / -0.140) under the timer.</summary>
        public bool TimerGap { get; set; } = true;

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
            /// <summary>Where the reset race key teleports you to (seat position and facing); null = behind the start gate.</summary>
            public SavedGate StartSpot { get; set; }
            /// <summary>The course's designer wants its checkpoints and gates on grids to get the glowing frame too (air gates always do).</summary>
            public bool GridGlow { get; set; }
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
        /// <summary>The current course's teleport spot (this world only; kept in the world's save, not here). Null = behind the start gate.</summary>
        public SavedGate CourseStartSpot { get; set; }
        /// <summary>The current course's choice (its designer's): checkpoints and gates on grids get the glowing frame too (this world only; kept in the world's save, not here).</summary>
        public bool CourseGridGlow { get; set; }

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
                    if (File.Exists(path)) return JsonSerializer.Deserialize<WorldCourses>(SafeRead.Text(path), WJson);
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
            /// <summary>The last lap raced in this ship (time, path, shape, speed, checkpoint times), to race or watch again.</summary>
            public double? LastLap { get; set; }
            public List<double[]> LastLapTrace { get; set; }
            public List<double[]> LastLapShip { get; set; }
            public List<double[]> LastLapSpeed { get; set; }
            public List<double?> LastLapSplits { get; set; }
            public DateTime LastLapDate { get; set; }
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
            /// <summary>Placed in open air (drawn by the plugin), and whether it's a ring instead of a box.</summary>
            public bool Air { get; set; }
            public bool Round { get; set; }

            public static SavedGate From(TimingGate g, string world) => new SavedGate
            {
                World = world,
                Center = new[] { g.Center.X, g.Center.Y, g.Center.Z },
                Forward = new[] { g.Forward.X, g.Forward.Y, g.Forward.Z },
                Up = new[] { g.Up.X, g.Up.Y, g.Up.Z },
                Width = g.Width,
                Height = g.Height,
                Depth = g.Depth,
                Air = g.Air,
                Round = g.Round,
            };

            public TimingGate ToGate() => new TimingGate(
                new Vec3D(Center[0], Center[1], Center[2]),
                new Vec3D(Forward[0], Forward[1], Forward[2]),
                new Vec3D(Up[0], Up[1], Up[2]),
                Width, Height, Depth, Air, Round);
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static GateSettings Load(string path)
        {
            GateSettings s = null;
            FlushWrites();   // (a save still on its way to the file is read back too)
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
            s.CourseStartSpot = null;
            s.CourseGridGlow = false;
            if (s.AirGateLook > 2) s.AirGateLook = 2;   // (a look that was tried and taken out)
            // Bindings for keys that no longer exist.
            s.KeyBindings ??= new Dictionary<string, KeyBinding>();
            if (!s.FlightKeysOnByDefault) { s.FlightKeysOnByDefault = true; s.FlightKeys = 2; }
            if (!s.RollOnByDefault) { s.RollOnByDefault = true; s.RollIndicator = true; }
            s.RpiClasses ??= new List<RpiClass>();
            s.RpiClasses.RemoveAll(rc => rc == null || string.IsNullOrWhiteSpace(rc.Name));
            foreach (var rc in s.RpiClasses)
            {
                rc.Values ??= new Dictionary<string, double>();
                rc.Limits ??= new Dictionary<string, int>();
                rc.Steps ??= new List<RpiStep>();
            }
            if (s.RpiValues != null && s.RpiValues.Count > 0 && s.RpiClasses.Count == 0)
            {
                s.RpiClasses.Add(new RpiClass { Name = "My class", Budget = s.RpiBudget, MinWeight = s.RpiMinWeight, Values = s.RpiValues });
                s.RpiClassName = "My class";
            }
            s.RpiValues = null;
            foreach (var name in new List<string>(s.KeyBindings.Keys))
                if (!Enum.TryParse<GateKey>(name, out _)) s.KeyBindings.Remove(name);
            WarmUpSaving();
            FilmingOn = s.Filming;
            return s;
        }

        /// <summary>
        /// Courses (gates, checkpoints, saved courses) belong to the world they were made in and are kept in its save
        /// folder (RaceDisplayCourses.json, written when the game saves). They're only held in memory here, never
        /// written to RaceDisplay.json, so a course can't turn up in another world that happens to have the same name.
        /// </summary>
        private static readonly string[] WorldOnly = { nameof(Gate), nameof(FinishGate), nameof(Checkpoints), nameof(SavedCourses), nameof(CurrentCourseName), nameof(ReversedWorlds), nameof(CourseSectors), nameof(CourseStartSpot), nameof(CourseGridGlow) };

        // Saving: compact (no indents, about a third the size, most of it lap traces), and the world-only parts left out
        // as it's written (no in-between copy of the whole file to take them out of afterwards).
        private static readonly JsonSerializerOptions SaveJson = new JsonSerializerOptions
        {
            WriteIndented = false,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver { Modifiers = { SkipWorldOnly } },
        };

        private static void SkipWorldOnly(System.Text.Json.Serialization.Metadata.JsonTypeInfo info)
        {
            if (info.Type != typeof(GateSettings)) return;
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (Array.IndexOf(WorldOnly, info.Properties[i].Name) >= 0) info.Properties.RemoveAt(i);
        }

        private static bool warmedUp;

        /// <summary>
        /// Gets the saving ready on another thread (the serializer works out how to write every type the settings hold,
        /// the slow part of the first save), so the first save in a world doesn't hold the game up for seconds.
        /// </summary>
        public static void WarmUpSaving()
        {
            if (warmedUp) return;
            warmedUp = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var seen = new HashSet<Type>();
                    void Visit(Type t)
                    {
                        if (t == null || !seen.Add(t)) return;
                        var info = SaveJson.GetTypeInfo(t);
                        Visit(info.ElementType);
                        Visit(info.KeyType);
                        foreach (var p in info.Properties) Visit(p.PropertyType);
                    }
                    Visit(typeof(GateSettings));
                    JsonSerializer.Serialize(new GateSettings(), SaveJson);   // (and the writing code itself)
                    PluginLog.Info($"Settings: saving made ready ({seen.Count} types, {clock.Elapsed.TotalMilliseconds:0} ms, in the background)");
                }
                catch (Exception e) { PluginLog.Info("Settings: getting saving ready failed: " + e.Message); }
            });
        }

        public void Save(string path)
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string json = JsonSerializer.Serialize(this, SaveJson);
                PluginLog.Slow($"Settings: making the save ({json.Length / 1024} KB)", clock.Elapsed.TotalMilliseconds);
                QueueWrite(path, json);
            }
            catch (Exception)
            {
                // Saving is best-effort.
            }
        }

        // The file itself is written by another thread, so a slow disk never holds up the game. Only the newest save of
        // a file is kept waiting; whatever is still waiting when the game closes is written then.
        private static readonly object WriteLock = new object();
        private static readonly Dictionary<string, string> WaitingWrites = new Dictionary<string, string>();
        private static bool writerBusy, exitHooked;

        private static void QueueWrite(string path, string json)
        {
            lock (WriteLock)
            {
                WaitingWrites[path] = json;
                if (!exitHooked)
                {
                    exitHooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => FlushWrites();
                }
                if (writerBusy) return;
                writerBusy = true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    string file, text;
                    lock (WriteLock)
                    {
                        if (WaitingWrites.Count == 0) { writerBusy = false; return; }
                        file = null; text = null;
                        foreach (var next in WaitingWrites) { file = next.Key; text = next.Value; break; }
                        WaitingWrites.Remove(file);
                    }
                    WriteNow(file, text);
                }
            });
        }

        private static readonly object FileLock = new object();

        private static void WriteNow(string file, string text)
        {
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, text);
                }
            }
            catch (Exception e) { PluginLog.Info("Settings couldn't be written: " + e.Message); }
        }

        /// <summary>Writes any save still waiting, now (before the file is read back, and when the game closes).</summary>
        public static void FlushWrites()
        {
            List<KeyValuePair<string, string>> waiting;
            lock (WriteLock)
            {
                waiting = new List<KeyValuePair<string, string>>(WaitingWrites);
                WaitingWrites.Clear();
            }
            foreach (var w in waiting) WriteNow(w.Key, w.Value);
            lock (FileLock) { }   // (and let a write that's under way finish)
        }
    }
}
