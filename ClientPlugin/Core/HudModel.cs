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
        /// <summary>Hide the game's own HUD screens and show only the race HUD.</summary>
        public bool HideGameHud;

        public double Scale = 1.0;
        public byte BackgroundOpacity = 150;

        public bool IsEmpty => Timer == null && Laps == null && RaceMessage == null && CentreMessage == null
                               && Splits == null && Results == null && Countdown == null
                               && Markers.Count == 0 && Arrow == null;

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
            /// <summary>Race stats shown under the table after the finish (label, value, colour). May be empty.</summary>
            public readonly List<(string Label, string Value, HudColor Colour)> Stats = new List<(string, string, HudColor)>();
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
            public string EditModeKey, EditWheelKey;
            public bool WorldHasCourses;
            /// <summary>Records tab: ghost files in the ghosts folder.</summary>
            public readonly List<GhostRow> Ghosts = new List<GhostRow>();
            public string GhostFolder, RacingAgainst, PilotName;
            public bool CourseHasBest;
            public int RecordsVersion;

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
            public int SettingsVersion;
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
        }

        public sealed class ControlsRow
        {
            public string Name;
            public string Section;   // group heading in the Controls tab
            public string Binding;
            public bool Selected, Clash, Changed;
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
                foreach (var st in Splits.Stats) sb.Append(st.Label).Append(st.Value).Append(st.Colour.R).Append(';');
                foreach (var r in Splits.Rows)
                {
                    sb.Append(r.Name).Append(r.NameColour.G);
                    foreach (var c in r.Cells) sb.Append(c.Text).Append(c.Colour.G).Append(c.Colour.R);
                    sb.Append('|');
                }
            }
            if (Results != null)
            {
                sb.Append("RES:").Append(Results.Title).Append(Results.Subtitle).Append(Results.Footer).Append(Results.Headline).Append(Results.HeadlineColour.R).Append(Results.Graph.Count).Append('|');
                foreach (var l in Results.Lines) sb.Append(l.Label).Append(l.Value).Append(l.Colour.G).Append('|');
            }
            sb.Append("CD:").Append(Countdown).Append(CountdownColour.R).Append('|');
            sb.Append("SW:").Append(SwapSides).Append(HideGameHud).Append('|');
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
