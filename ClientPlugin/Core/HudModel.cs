using System.Collections.Generic;

namespace RaceDisplay.Core
{
    /// <summary>
    /// What the HUD should show this frame, independent of how it's drawn.
    /// Built by <see cref="HudRenderer.Build"/>; drawn either by the game's real UI (Avalonia overlay)
    /// or, as a fallback, with debug draw.
    /// </summary>
    public sealed class HudModel
    {
        /// <summary>Set by the game-UI HUD while it re-places markers every rendered frame itself.</summary>
        public static volatile bool MarkersPlacedByUi;

        /// <summary>Timer panel (top centre). Null = hidden.</summary>
        public TimerPanel Timer;

        /// <summary>Lap list (left). Null = hidden.</summary>
        public LapsPanel Laps;

        /// <summary>Race message just above the lap list (GO, crash, checkpoint, lap). Null = none.</summary>
        public Message RaceMessage;

        /// <summary>Setup message in the centre (gate placed, etc.) or the startup hint. Null = none.</summary>
        public Message CentreMessage;

        /// <summary>Checkpoint splits for the current lap (right side). Null = hidden.</summary>
        public SplitsPanel Splits;
        /// <summary>Race stats box (top corner) after the finish: label, value, colour. Null = hidden.</summary>
        public List<(string Label, string Value, HudColor Colour)> RaceStats;
        /// <summary>Title of the race stats box ("RACE STATS" when null), and one column instead of two.</summary>
        public string RaceStatsTitle;
        public bool RaceStatsOneColumn;

        /// <summary>Race results (after the finish). Null = hidden.</summary>
        public ResultsPanel Results;

        /// <summary>Old in-HUD controls screen (unused: the controls menu is opened from the pause menu).</summary>
        /// <summary>Top-down course map (null = hidden). Coordinates are 0..1 across the map.</summary>
        public MinimapPanel Minimap;

        public sealed class MinimapPanel
        {
            public double Size;                 // pixels at HUD scale 1
            public string StaticSig;            // changes when the route / lines change (they're redrawn then)
            // Route, lines, line colours and sectors only change with the course: the same lists are handed on
            // every frame (until StaticSig changes), so they must not be changed once made.
            public List<(double X, double Y)> Route = new List<(double, double)>();
            public List<(double X, double Y)> Line = new List<(double, double)>();
            public List<(double X, double Y)> Line2 = new List<(double, double)>();
            public HudColor LineColour = HudColor.Purple;
            public List<HudColor> LineColours = new List<HudColor>();   // per segment of Line (racing line colours); empty = LineColour
            public readonly List<(double X, double Y, HudColor Colour, bool Gate, string Label)> Points = new List<(double, double, HudColor, bool, string)>();
            public readonly List<(double X, double Y, HudColor Colour)> Ghosts = new List<(double, double, HudColor)>();
            public double? PlayerX, PlayerY;
            public double ViewX = 0.5, ViewY = 0.5, ViewSpan = 1;   // the part of the map shown: centre and width (1 = whole course)
            public int NextIndex = -1;          // Points index of the gate / checkpoint to fly to next (highlighted)
            public double PlayerAngle;          // degrees, 0 = up the map, clockwise
            /// <summary>Named sectors: the route's points from one gate / checkpoint to another, a name and a colour.</summary>
            public List<(List<(double X, double Y)> Points, string Name, HudColor Colour)> Sectors = new List<(List<(double X, double Y)>, string, HudColor)>();
            /// <summary>After the race (Race Map): stretches where your lap's line (Line2) was well away from the best lap's (Line).</summary>
            public List<List<(double X, double Y)>> Diverge = new List<List<(double X, double Y)>>();
            /// <summary>After the race (Race Map): what the lines are, for its key; null = not comparing.</summary>
            public string LegendMine, LegendRef;
            /// <summary>Race Map: your lap's colour per segment of Line2 by gap trend (green gaining, red losing); empty = plain.</summary>
            public List<HudColor> Line2Colours = new List<HudColor>();
            /// <summary>Race Map: each gate as a bar across the course (start / finish chequered).</summary>
            public List<(double X1, double Y1, double X2, double Y2, bool StartFinish)> GateBars = new List<(double, double, double, double, bool)>();
            /// <summary>Race Map: height round the lap (share of the lap, metres above the start gate) and the gates on it.</summary>
            public List<(double Share, double Height)> Profile = new List<(double, double)>();
            public List<(double Share, string Name, bool Missed)> ProfileMarks = new List<(double, string, bool)>();
            /// <summary>Race Map: points of the main line to point at: speed (m/s), lap time (-1 = unknown) and gap to the record.</summary>
            public List<(double X, double Y, double Speed, double Time, double? Gap)> HoverPoints = new List<(double, double, double, double, double?)>();
            /// <summary>Race Map: metres across the whole map, and which way is north on it (unit vector, y down).</summary>
            public double MetresAcross, NorthX, NorthY;
            public bool HasNorth;
            /// <summary>After the race: where you crashed on the shown lap (red X).</summary>
            public readonly List<(double X, double Y)> Crashes = new List<(double, double)>();
            /// <summary>After the race: the lap time of each crash (same order as Crashes), each checkpoint's index in Points with when it was flown or
            /// counted as missed, and the lap's flown time (0 = no replay): for the Race Map to play the lap back.</summary>
            public readonly List<double> CrashTimes = new List<double>();
            public readonly List<(int Point, double? Hit, double? MissedAt)> AnimCheckpoints = new List<(int, double?, double?)>();
            public double AnimLapTime;
            /// <summary>After the race: how many laps the race had (the Race Map's lap picker).</summary>
            public int Laps;
        }

        /// <summary>Big centre countdown text ("3", "2", "1", "GO!"). Null = none.</summary>
        public string Countdown;
        public HudColor CountdownColour = HudColor.White;

        /// <summary>Brief border flash on the timer panel (best lap = green, penalty = red). Null = none.</summary>
        public HudColor? Flash;

        /// <summary>Labels on checkpoints/gate, placed on screen (0..1). Empty = none.</summary>
        public readonly List<ScreenMarker> Markers = new List<ScreenMarker>();

        /// <summary>Arrow at the screen edge pointing to the next checkpoint when it's off screen. Null = none.</summary>
        public EdgeArrow Arrow;

        /// <summary>Lap list (and results) on the right, splits on the left.</summary>
        public bool SwapSides;
        /// <summary>The timer sits in the top right corner (top left with SwapSides) above the best lap, best race and splits.</summary>
        public bool TimerInCorner;
        /// <summary>Hide the game's own HUD screens and show only the race HUD.</summary>
        public bool HideGameHud;
        /// <summary>With HideGameHud: keep the game's gyro; and switch it to target based.</summary>
        public bool KeepGyro, GyroTargetBased;

        /// <summary>Race speedometer: the speed and its unit. Null = hidden.</summary>
        public string Speedo, SpeedoUnit;
        /// <summary>Speedometer dial: the speed and the dial's top end (both in the game's speed unit).</summary>
        public double SpeedoValue, SpeedoMax;
        /// <summary>Flight keys display: 0 = off, 1 = bottom left, 2 = bottom right; and which are held (bits: Q W E A S D C Space).</summary>
        public int KeysCorner, KeysDown;
        /// <summary>Ship analyser: the stats panel (null = hidden).</summary>
        public AnalyserPanel Analyser;
        /// <summary>Roll indicator: degrees the ship is rolled from level (+ = right wing down). Null = not shown.</summary>
        public double? Roll;
        /// <summary>Pitch indicator: degrees the nose is above the horizon (- = below). Null = not shown.</summary>
        public double? Pitch;
        /// <summary>How fast it's rolling, degrees a second (+ = to the right).</summary>
        public double RollRate;

        public sealed class AnalyserPanel
        {
            public string TitleA, TitleB, Hint;
            public string Heading;   // the panel's title (null = SHIP ANALYSER)
            public bool PinnedA;
            public readonly List<(string Label, string A, string B, HudColor ColourB, HudColor ColourA)> Rows = new List<(string, string, string, HudColor, HudColor)>();
        }

        /// <summary>Speed vector marker: where the ship is heading, on screen (0..1). Null = not shown.</summary>
        public double? VectorX, VectorY;

        public double Scale = 1.0;
        public byte BackgroundOpacity = 150;

        public bool IsEmpty => Timer == null && Laps == null && RaceMessage == null && CentreMessage == null
                               && Splits == null && Results == null && Countdown == null
                               && Markers.Count == 0 && Arrow == null && Analyser == null;

        public enum DotState { Pending, Next, Hit, Missed }

        public sealed class ScreenMarker
        {
            public double X, Y;          // 0..1 screen position of the checkpoint
            public string Text;          // "CP 3"
            public string Sub;           // distance, may be null
            public HudColor Colour = HudColor.Orange;
            public bool Emphasis;        // the next checkpoint: bigger, brighter
            public Vec3D? World;         // where it is in the world, so the HUD can re-place it every rendered frame
        }

        public sealed class EdgeArrow
        {
            public double X, Y;          // 0..1 position on the screen edge
            public double AngleDegrees;  // 0 = pointing right, 90 = down
            public string Text;          // "CP 3  1.2 km"
            public HudColor Colour = HudColor.Orange;
            public Vec3D? World;         // the target, so the HUD can re-aim it every rendered frame
        }

        public sealed class TimerPanel
        {
            public string Label;
            public string Big;
            public HudColor BigColour = HudColor.White;
            public string Footer;        // may be null
            public string Delta;         // gap to best lap, e.g. "-1.204"; may be null
            public HudColor DeltaColour = HudColor.White;
            public DotState[] Dots;      // one per checkpoint; null/empty = none
            public HudColor?[] DotColours; // sector colour for flown checkpoints (null = plain green)
            public string BestLap;       // left of the timer: best single lap on this course; may be null
            public string BestLapNote;   // under it: "POSSIBLE 01:27.902" (best sectors added up); may be null
            public string BestRace;      // right of the timer: best whole-race time; may be null
            public string BestRaceLabel; // e.g. "3 LAPS"
            public string BestRaceNote;  // under it: "POSSIBLE 04:28.006" (possible lap x laps); may be null
            public string Hint;          // under the panel (how to start, or "GO!"); may be null
            public HudColor HintColour = HudColor.White;
        }

        public sealed class LapsPanel
        {
            public string Header = "LAPS";
            public readonly List<LapRow> Rows = new List<LapRow>();
        }

        public sealed class LapRow
        {
            public string Name;          // "L1"
            public string Note;          // "+1.234" or "+5s: 1 crash"; may be null
            public string Time;          // "01:32.384"
            public HudColor Colour = HudColor.White;
            public HudColor NoteColour = HudColor.Red;
            /// <summary>Penalty icons after the note: one ring per missed checkpoint, one cross per crash.</summary>
            public int MissedIcons, CrashIcons;
        }

        /// <summary>
        /// Splits table. During a race: columns GAP and TIME for the current lap.
        /// After the finish: one column per lap (L1, L2, ...), plus a LAP row with each lap's time.
        /// </summary>
        public sealed class SplitsPanel
        {
            public string Title = "SPLITS";
            /// <summary>Column headings after the name column (may be empty strings).</summary>
            public readonly List<string> Headers = new List<string>();
            public readonly List<SplitRow> Rows = new List<SplitRow>();
        }

        public sealed class SplitRow
        {
            public string Name;          // "CP 3" or "LAP"
            public HudColor NameColour = HudColor.White;
            /// <summary>One cell per header.</summary>
            public readonly List<(string Text, HudColor Colour)> Cells = new List<(string, HudColor)>();
        }

        public sealed class ResultsPanel
        {
            public string Title = "RESULTS";
            public string Headline;      // "NEW RECORD!" or "+3.204 vs record"; may be null
            public HudColor HeadlineColour = HudColor.Yellow;
            public string Footer;        // "Ctrl+B: this ship's best"; may be null
            public string Subtitle;      // ship name under the title; may be null
            public readonly List<(string Label, string Value, HudColor Colour)> Lines = new List<(string, string, HudColor)>();

            /// <summary>Speed graph: lines of (share of the lap 0..1, speed m/s). Empty = no graph.</summary>
            public readonly List<GraphLine> Graph = new List<GraphLine>();
            public double GraphMaxSpeed;
            /// <summary>Time gap graph: (share of the lap, seconds behind (+) or ahead (-) of <see cref="GapAgainst"/>). Empty = none.</summary>
            public readonly List<(double X, double Gap)> Gap = new List<(double, double)>();
            public string GapAgainst;
            /// <summary>Checkpoints (1 = CP 1) missed on the graphed lap: drawn red.</summary>
            public readonly List<int> MissedCheckpoints = new List<int>();
            /// <summary>Which graph is showing: 0 = speed, 1 = time gap (they take turns).</summary>
            public int GraphPage;
            /// <summary>Changes when the graphs are redrawn for another lap; and which lap they show (null with one lap).</summary>
            public int GraphVersion;
            public string GraphLap;
        }

        public sealed class GraphLine
        {
            public string Label;      // "Best this race"
            public HudColor Colour;
            public readonly List<(double X, double Speed)> Points = new List<(double, double)>();
            /// <summary>Where each checkpoint was hit on this lap: share of the lap, speed, checkpoint number (1 = CP 1).</summary>
            public readonly List<(double X, double Speed, int Number)> Checkpoints = new List<(double, double, int)>();
        }

        public sealed class ControlsPanel
        {
            public string Title = "RACING DISPLAY";
            public readonly List<ControlsRow> Rows = new List<ControlsRow>();
            public string Note;   // last change / clash warning; may be null
            public string Help;   // key help at the bottom

            /// <summary>Records tab: this course's best races. Rebuilt when RecordsVersion changes.</summary>
            public string RecordsTitle;
            public readonly List<HistoryRow> Records = new List<HistoryRow>();
            /// <summary>Records tab: best times per ship on this course, fastest first.</summary>
            public readonly List<ShipRow> Ships = new List<ShipRow>();
            public string ShipsRaceHeader = "BEST RACE";
            public string CurrentShip;
            /// <summary>Courses tab: the world's save folder (null = not found) and whether it has a course file.</summary>
            public string WorldFolder;
            public bool EditMode;
            public bool AnalyserOpen;
            /// <summary>Race tab > Race spotlight: exported race files, the one being shown (its path), and whether a race can be exported.</summary>
            public readonly List<(string Name, string Info, string Path)> Spotlights = new List<(string, string, string)>();
            public string SpotlightOpen, SpotlightFolder, LastRaceLine;
            public bool CanExportRace;
            public string EditModeKey, EditWheelKey;
            public bool WorldHasCourses;
            /// <summary>Courses tab: where the reset race teleport goes ("Behind the start gate" or a description).</summary>
            public string StartSpot;
            /// <summary>Records tab: ghost files in the ghosts folder.</summary>
            public readonly List<GhostRow> Ghosts = new List<GhostRow>();
            public string GhostFolder, RacingAgainst, PilotName;
            public bool CourseHasBest;
            /// <summary>Race tab: the possible lap (best sectors joined) has a line, and it's being raced against.</summary>
            public string PossibleLine;
            public bool RacingPossible;
            public int RecordsVersion;
            /// <summary>Records tab: the latest race on this course as text to copy (null = none yet).</summary>

            /// <summary>Courses tab. Rebuilt when CoursesVersion changes.</summary>
            public string CourseName;     // current course name, or null if not saved
            public string CourseSummary;  // "Start/finish gate + 9 checkpoints"
            public readonly List<string> CourseCheckpoints = new List<string>();   // "CP 1   420 m from the gate"
            public readonly List<(string Name, string Info, bool Current)> SavedCourses = new List<(string, string, bool)>();
            /// <summary>Courses tab: the route's points by name (for the sector pickers) and the named sectors.</summary>
            public readonly List<string> RoutePoints = new List<string>();
            public readonly List<(string Name, string Range)> Sectors = new List<(string, string)>();
            public int CoursesVersion;

            /// <summary>Settings tab. The menu only rebuilds it when SettingsVersion changes (not while you drag a slider).</summary>
            public readonly List<SettingRow> Settings = new List<SettingRow>();
            /// <summary>Settings tab: saved presets (names, sorted) and the one last loaded or saved.</summary>
            public readonly List<string> Presets = new List<string>();
            public string CurrentPreset;
            /// <summary>Preset files in the presets folder (exported here or copied in from someone else).</summary>
            public readonly List<(string Name, string Path)> PresetFiles = new List<(string, string)>();
            public string PresetFolder;
            public int SettingsVersion;

            /// <summary>RPI: the classes, the one in use (null = none) with its values, and class files that can be imported.</summary>
            public readonly List<(string Name, double Budget, double MinWeight, int Costed)> RpiClasses = new List<(string, double, double, int)>();
            public string RpiCurrent;
            public Dictionary<string, double> RpiValues = new Dictionary<string, double>();
            public double RpiBudget, RpiMinWeight;
            public Dictionary<string, int> RpiLimits = new Dictionary<string, int>();
            public readonly List<(double Weight, double Budget)> RpiSteps = new List<(double, double)>();
            /// <summary>The ship last looked at with the analyser (null = none yet): its block counts and mass.</summary>
            public string RpiShipName;
            public double RpiShipMass;
            public Dictionary<string, int> RpiShipCounts;
            public readonly List<(string Name, string Path)> RpiFiles = new List<(string, string)>();
            public string RpiFolder;
            public int RpiVersion;
        }

        public sealed class GhostRow
        {
            public string Path, Label, Detail;
            public bool Fits, Active, Second;
        }

        public sealed class ShipRow
        {
            public string Name, BestLap, Gap, BestRace, RaceGap, TopSpeed, TurnG, Races;
            public bool Fastest, FastestRace, Current, HasReplay, Second, Racing;
            /// <summary>The last lap flown in this ship: its time (null = none kept yet) and whether it's being raced against.</summary>
            public string LastLap;
            public bool RacingLast;
        }

        public sealed class HistoryRow
        {
            public string Group;      // "3 LAPS"
            public int Rank;
            public string Total, Date, Penalty, BestLap, TopSpeed, Ship;
            public bool IsRecord;     // the best one in its group
        }

        public sealed class SettingRow
        {
            public int Index; // into SettingDefs.All
            public string Section, Name, Description, Format;
            public string[] Choices;
            public bool Toggle;
            public double Min, Max, Step, Value;
            /// <summary>False when the setting it depends on is off (it does nothing then).</summary>
            public bool Enabled = true;
            public string DependsOn;
            public bool DependsOnOff;
            /// <summary>Not at its default value (Default).</summary>
            public bool Changed;
            public double Default;
        }

        public sealed class ControlsRow
        {
            public string Name;
            public string Section;   // group heading in the Controls tab
            public string Binding;
            public bool Clash, Changed;
            public bool Capturing; // waiting for the new key combination
        }

        public sealed class Message
        {
            public string Text;
            public HudColor Colour = HudColor.White;
        }

        /// <summary>A string that changes whenever anything visible changes (lets the UI skip identical updates).</summary>
        public string Signature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Scale).Append('|').Append(BackgroundOpacity).Append('|');
            if (Timer != null) sb.Append(Timer.Label).Append('|').Append(Timer.Big).Append('|').Append(Timer.BigColour.R).Append('|')
                                 .Append(Timer.Footer).Append('|').Append(Timer.BestLap).Append('|').Append(Timer.BestRace).Append(Timer.BestRaceLabel).Append('|').Append(Timer.Hint).Append(Timer.HintColour.G).Append('|');
            if (Laps != null)
            {
                sb.Append(Laps.Header).Append('|');
                foreach (var r in Laps.Rows) sb.Append(r.Name).Append(r.Note).Append(r.Time).Append(r.Colour.G).Append(r.MissedIcons).Append(r.CrashIcons).Append('|');
            }
            if (Timer != null)
            {
                sb.Append(Timer.Delta).Append(Timer.DeltaColour.R).Append('|');
                if (Timer.Dots != null) foreach (var d in Timer.Dots) sb.Append((int)d);
                if (Timer.DotColours != null) foreach (var c in Timer.DotColours) sb.Append(c?.R ?? 0).Append(c?.G ?? 0);
                sb.Append('|');
            }
            sb.Append(RaceStatsTitle).Append(RaceStatsOneColumn);
            if (Minimap != null)
            {
                sb.Append("MM").Append(Minimap.StaticSig).Append(Minimap.Size).Append('|').Append(Minimap.NextIndex)
                  .Append((int)(Minimap.ViewX * 1000)).Append(',').Append((int)(Minimap.ViewY * 1000)).Append(',').Append((int)(Minimap.ViewSpan * 1000)).Append('|');
                foreach (var p in Minimap.Points) sb.Append(p.Colour.R).Append(p.Colour.G).Append(',');
                if (Minimap.PlayerX.HasValue) sb.Append((int)(Minimap.PlayerX * 400)).Append(',').Append((int)(Minimap.PlayerY * 400)).Append(',').Append((int)(Minimap.PlayerAngle / 3));
                foreach (var g in Minimap.Ghosts) sb.Append('g').Append((int)(g.X * 400)).Append(',').Append((int)(g.Y * 400));
            }
            if (RaceStats != null) foreach (var st in RaceStats) sb.Append("RS:").Append(st.Label).Append(st.Value).Append(st.Colour.R).Append(';');
            if (Splits != null)
            {
                sb.Append(Splits.Title).Append(string.Join(",", Splits.Headers)).Append('|');
                foreach (var r in Splits.Rows)
                {
                    sb.Append(r.Name).Append(r.NameColour.G);
                    foreach (var c in r.Cells) sb.Append(c.Text).Append(c.Colour.G).Append(c.Colour.R);
                    sb.Append('|');
                }
            }
            if (Results != null)
            {
                sb.Append("RES:").Append(Results.Title).Append(Results.Subtitle).Append(Results.Footer).Append(Results.Headline).Append(Results.HeadlineColour.R).Append(Results.Graph.Count).Append(Results.GraphPage).Append(Results.GraphVersion).Append('|');
                foreach (var l in Results.Lines) sb.Append(l.Label).Append(l.Value).Append(l.Colour.G).Append('|');
            }
            sb.Append("CD:").Append(Countdown).Append(CountdownColour.R).Append('|');
            sb.Append("SW:").Append(SwapSides).Append(TimerInCorner).Append(HideGameHud).Append(KeepGyro).Append(GyroTargetBased).Append('|');
            if (Pitch.HasValue) sb.Append("PT:").Append(System.Math.Round(Pitch.Value));
            if (Roll.HasValue) sb.Append("RL:").Append(System.Math.Round(Roll.Value)).Append('/').Append(System.Math.Round(RollRate));
            if (Analyser != null)
            {
                sb.Append("AN:").Append(Analyser.Heading).Append(Analyser.TitleA).Append(Analyser.TitleB).Append(Analyser.PinnedA).Append(Analyser.Hint);
                foreach (var r in Analyser.Rows) sb.Append(r.Label).Append(r.A).Append(r.B).Append(r.ColourB.G).Append(r.ColourA.G);
                sb.Append('|');
            }
            sb.Append("VV:").Append(VectorX.HasValue ? (int)(VectorX.Value * 2000) : -1).Append(',').Append(VectorY.HasValue ? (int)(VectorY.Value * 2000) : -1).Append('|');
            sb.Append("SP:").Append(Speedo).Append(SpeedoUnit).Append(KeysCorner).Append(',').Append(KeysDown).Append('|');
            if (Timer != null) sb.Append(Timer.BestLapNote).Append(Timer.BestRaceNote).Append('|');
            // When the HUD places markers itself each frame, their screen position isn't part of the signature.
            bool pos = !MarkersPlacedByUi;
            foreach (var mk in Markers)
            {
                sb.Append(mk.Text).Append(mk.Sub).Append(mk.Colour.A).Append(mk.Emphasis);
                if (pos || !mk.World.HasValue) sb.Append(mk.X.ToString("0.000")).Append(mk.Y.ToString("0.000"));
                sb.Append('|');
            }
            if (Arrow != null)
            {
                sb.Append("A:").Append(Arrow.Text).Append(Arrow.Colour.R);
                if (pos || !Arrow.World.HasValue) sb.Append(Arrow.X.ToString("0.000")).Append(Arrow.Y.ToString("0.000")).Append(Arrow.AngleDegrees.ToString("0"));
                sb.Append('|');
            }
            if (Flash.HasValue) sb.Append("F:").Append(Flash.Value.R).Append(Flash.Value.G).Append('|');
            if (RaceMessage != null) sb.Append("R:").Append(RaceMessage.Text).Append(RaceMessage.Colour.R).Append('|');
            if (CentreMessage != null) sb.Append("C:").Append(CentreMessage.Text).Append(CentreMessage.Colour.R).Append('|');
            return sb.ToString();
        }
    }
}
