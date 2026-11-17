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
            public bool HeadingUp;              // the map turns so your ship always points up
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
            public int LabelsAhead;             // only this many points from NextIndex on are numbered (0 = all of them)
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
            public bool ProfileAboveGround;   // the profile's heights are above the ground below (as the speedometer shows), not above the start
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
        public string GroundHeight;
        /// <summary>Beside the height above ground: the height above sea level (null = not shown).</summary>
        public string SeaHeight;
        public string SpeedoOther, SpeedoOtherUnit;    // the speed in the other unit (km/h when the game shows m/s, else m/s), under the unit   // under the speedometer: how high above the ground below (null = not shown)
        /// <summary>Speedometer dial: the speed and the dial's top end (both in the game's speed unit).</summary>
        public double SpeedoValue, SpeedoMax;
        /// <summary>Which extras the speedometer shows (bits): 1 resource rings, 2 power, 4 dampeners, 8 parking brake, 16 antenna, 32 safe speed.</summary>
        public int SpeedoParts;
        /// <summary>Race HUD only: the game toolbar slot to show for a moment (0 = the first, key 1), or -1.</summary>
        public int ToolbarPeek = -1;
        /// <summary>Race HUD only: the game's toolbar shown as a small strip, and its size (0.3 to 0.9 of the toolbar's own).</summary>
        public bool MiniToolbar;
        public double MiniToolbarScale = 0.5;
        /// <summary>Gravity where the ship is, in g (for the gravity panel); null = not shown.</summary>
        public double? GravityG;
        /// <summary>Which way gravity pulls across the ship: its share along the ship's right (X) and up (Y), each -1..1 (level over a planet: 0, -1).</summary>
        public double GravityX, GravityY;
        /// <summary>Natural and artificial gravity apart (when both could be read): each one's strength in g and which way it pulls across the ship, like GravityX / GravityY.</summary>
        public bool GravityParts;
        public double GravityNatG, GravityNatX, GravityNatY, GravityArtG, GravityArtX, GravityArtY;
        /// <summary>The ship's speed in m/s (for the safe speed warning).</summary>
        public double SpeedMs;
        /// <summary>The ship's lights are on (a small lamp on the speedometer).</summary>
        public bool LightsOn;
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

        /// <summary>Cinematics: fade to black (0..1) and letterbox bars (0..0.45 of the screen's height, each).</summary>
        public double CineFade, CineBars;

        /// <summary>Cinematic debug: the timeline on screen while a cinematic plays (null = not shown).</summary>
        public CineDebugPanel CineDebug;

        /// <summary>Live timing strip while a cinematic films a race (null = not shown).</summary>
        public FilmStripPanel FilmStrip;

        public sealed class CineDebugPanel
        {
            public string Title, Now;
            public double Total, Time;
            public int Lanes = 1;
            public readonly List<(string Name, double Seconds, HudColor Colour, bool Playing)> Shots = new List<(string, double, HudColor, bool)>();
            public readonly List<(string Name, double Start, double Seconds, HudColor Colour, bool Active, int Lane)> Effects = new List<(string, double, double, HudColor, bool, int)>();
        }

        /// <summary>One shot of a cinematic, as the Cinematics tab lists it.</summary>
        public sealed class CineShotRow
        {
            public string Type, Target;
            public double Seconds, Rotate;
            public bool Ready, ReadyEnd, Track, Relative, HasShipView;
            public int Keys;
            public double Blend;
        }

        public sealed class AnalyserPanel
        {
            public string TitleA, TitleB, Hint;
            public string Heading;   // the panel's title (null = SHIP ANALYSER)
            public bool PinnedA;
            public double SlideSeconds = 0.35;   // how long a page takes to slide out (and the next one in); 0 = swap at once
            public readonly List<(string Label, string A, string B, HudColor ColourB, HudColor ColourA)> Rows = new List<(string, string, string, HudColor, HudColor)>();
            /// <summary>Extras for rows, by row number: a bar after the value (0..1 of its full width).</summary>
            public readonly Dictionary<int, (double Fraction, HudColor Colour)> RowBars = new Dictionary<int, (double, HudColor)>();
            /// <summary>The row labelled "@THRUST" is drawn as a diagram of these: acceleration (g) forward, backward, left, right, up, down.</summary>
            public double[] ThrustG;
            /// <summary>The tiles (Card) are small ones, four to a row.</summary>
            public bool CardSmall;
            /// <summary>How long a leaderboard row takes to slide to a new place (seconds).</summary>
            public double RowSlideSeconds = 0.45;
            /// <summary>A course map drawn under the rows (race spotlight's lap replay); null = none.</summary>
            public AnalyserMap Map;
            /// <summary>The results graphs drawn under the rows (race spotlight's race graphs page); null = none.</summary>
            public ResultsPanel Graphs;
            /// <summary>Ship card: big stat tiles (race spotlight); null = none.</summary>
            public List<(string Label, string Value, HudColor Colour)> Card;
            /// <summary>Lap by lap: a bar each (Fraction 0..1 of the full width); null = none.</summary>
            public List<(string Label, string Value, double Fraction, HudColor Colour)> Bars;
            /// <summary>Intro / outro: a big title card in the middle of the screen instead of the panel; null = none.</summary>
            public TitleCard Cover;
            /// <summary>Export: save the panel (or title card) as a picture here once it's drawn; Exporting = draw it on a solid background.</summary>
            public string ExportPath;
            public bool Exporting;
        }

        public sealed class TitleCard
        {
            public string Kicker, Title, Subtitle;
            public HudColor TitleColour = HudColor.White;
            public readonly List<string> Lines = new List<string>();
        }

        /// <summary>
        /// Lap replay map, in 0..1 map coordinates (y down). Key changes when the line or gates change (they're
        /// drawn again then); Done and the dot move every frame.
        /// </summary>
        public sealed class AnalyserMap
        {
            public string Key;
            public List<(double X, double Y)> Line = new List<(double, double)>();
            public List<(double X, double Y)> Faint = new List<(double, double)>();   // another lap (the best), drawn faintly underneath
            public readonly List<(double X, double Y, int Kind)> Gates = new List<(double, double, int)>();   // 0 start, 1 finish, 2 checkpoint
            public int Done;                     // points of Line flown so far
            public double DotX, DotY;
            public HudColor Colour = HudColor.Cyan;
            public bool HideDot;                 // a still map (course card, speed trap, trouble spots): no moving dot
            /// <summary>Marks on the map: Kind 0 crash, 1 missed checkpoint, 2 top speed.</summary>
            public readonly List<(double X, double Y, int Kind)> Marks = new List<(double, double, int)>();
            public bool ShowKey;                 // a key under the map saying what each sign is (race spotlight's lap replay)
        }

        /// <summary>Live timing strip along the bottom while a cinematic films the autopilot: lap, clock, speed.</summary>
        public sealed class FilmStripPanel
        {
            public string Lap, Clock, Speed;
        }

        /// <summary>One finished race on the History tab.</summary>
        public sealed class RaceHistoryRow
        {
            public System.DateTime Date;
            public string Course, Ship;
            public int Laps;
            public double Total, BestLap, Penalty, TopSpeed;
            public bool Best;              // a personal best at the time
            public string File;            // its race file (to watch as a spotlight or race again), or null
            public bool CanSave;           // the race just flown, not saved to a file yet (Save makes it watchable)
            public double Flown => System.Math.Max(0, Total - Penalty);
        }

        /// <summary>Speed vector marker: where the ship is heading, on screen (0..1). Null = not shown.</summary>
        public double? VectorX, VectorY;

        public double Scale = 1.0;
        public byte BackgroundOpacity = 150;
        /// <summary>The whole HUD's opacity (0.2 to 1).</summary>
        public double Opacity = 1.0;
        /// <summary>A soft glow round the edge of the screen in this colour (a checkpoint passed, a penalty); null = none.</summary>
        public HudColor? EdgeGlow;
        /// <summary>Checkpoint flash at the top centre: "CP 6|+7.619" (the part after the bar in Colour); null = none.</summary>
        public Message CpFlash;

        public bool IsEmpty => Timer == null && Laps == null && RaceMessage == null && CentreMessage == null
                               && Splits == null && Results == null && Countdown == null
                               && Markers.Count == 0 && Arrow == null && Analyser == null && CineDebug == null
                               && CineFade <= 0.001 && CineBars <= 0.001 && FilmStrip == null && !CleanPicture;

        /// <summary>A cinematic with Hide the HUD: nothing of ours to show, but the game's HUD is kept hidden.</summary>
        public bool CleanPicture;

        public enum DotState { Pending, Next, Hit, Missed }

        /// <summary>Course edit mode: the course as a summary line, its legs (with a bar for each one's length) and warnings.</summary>
        public sealed class CoursePanel
        {
            public string Summary;       // "CIRCUIT · 9 CP · 26.40 km"
            public string Summary2;      // "2 laps 52.79 km · best 01:15.699"; may be null
            public readonly List<(string Name, string Distance, double Share, bool Aimed, HudColor Colour)> Legs = new List<(string, string, double, bool, HudColor)>();
            public string More;          // "... 3 more legs"; may be null
            public readonly List<(string Label, string Text)> Warnings = new List<(string, string)>();
        }
        public CoursePanel Course;

        public sealed class ScreenMarker
        {
            public double X, Y;          // 0..1 screen position of the checkpoint
            public string Text;          // "CP 3"
            public string Sub;           // distance, may be null
            public HudColor Colour = HudColor.Orange;
            public bool Emphasis;        // the next checkpoint: bigger, brighter
            public bool Quiet;           // a small plain tag (leg lengths in course edit mode)
            public HudColor? SubColour;  // the second part in a colour of its own (null = the marker's)
            public bool SubInline;       // the second part beside the first, on one line
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
            /// <summary>Course edit mode: the keys as rows (key, what it does, greyed out) instead of the footer; a smaller
            /// big line; and a coloured tag after the label (the placement mode).</summary>
            public List<(string Key, string Words, bool Dim)> Keys;
            public bool Compact;
            public string Tag;
            public HudColor TagColour = HudColor.White;
            public string Sub;           // small grey words under the footer (how to start); may be null
            public double? StartBar;     // waiting at the start: a thin bar, 0..1, that fills as you close on the gate; null = none
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
            /// <summary>Changes when the graphs are redrawn for another lap; and which lap they show (null with one lap).</summary>
            public int GraphVersion;
            /// <summary>Which graphs to show: 0 none, 1 speed, 2 speed and time gap.</summary>
            public int GraphsShown = 2;
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
            public string ShipsRaceHeader = "BEST RACE", ShipsAverageHeader = "AVERAGE";
            public string CurrentShip;
            /// <summary>Courses tab: the world's save folder (null = not found) and whether it has a course file.</summary>
            public string WorldFolder;
            public bool EditMode;
            public bool AnalyserOpen;
            public bool OrbitOpen, AutopilotOn, AutopilotReady, SpotExporting;
            public bool Replaying;   // a replay is playing (or waiting for the menu to close)
            /// <summary>Race spotlight pages, in order: id, name, on, seconds (0 = usual).</summary>
            public readonly List<(int Page, string Name, bool On, double Seconds)> SpotPages = new List<(int, string, bool, double)>();
            /// <summary>Race spotlight leaderboard: every ship with a race file (its name, how many races, hidden from the board), and one row a ship.</summary>
            public readonly List<(string Ship, int Races, bool Hidden)> SpotBoardShips = new List<(string, int, bool)>();
            public bool SpotBoardBestPerShip, SpotBoardReveal;
            public bool SpotTourOn;
            /// <summary>The cinematic that films every ship on the tour (null / "" = each race's own).</summary>
            public string SpotTourCine;
            public string AutopilotKey;
            public string OrbitKey, OrbitLockKey;
            /// <summary>Race tab > Race spotlight: exported race files, the one being shown (its path), and whether a race can be exported.</summary>
            public readonly List<(string Name, string Info, string Path)> Spotlights = new List<(string, string, string)>();
            public string SpotlightOpen, SpotlightFolder, LastRaceLine, SpotlightRacing;
            /// <summary>The race file the spotlight is compared with (null = none).</summary>
            public string SpotCompare;
            /// <summary>Per race file (its path): auto race on, and the cinematic it starts ("" = none).</summary>
            public readonly Dictionary<string, (bool Race, string Cinematic)> SpotAuto = new Dictionary<string, (bool, string)>();
            public bool CanExportRace;
            public string EditModeKey, EditWheelKey;
            public bool WorldHasCourses;
            /// <summary>Courses tab: where the reset race teleport goes ("Behind the start gate" or a description).</summary>
            public string StartSpot;
            /// <summary>This course: checkpoints and gates on grids get the glowing frame too (the course designer's choice).</summary>
            public bool CourseGridGlow;
            /// <summary>Records tab: ghost files in the ghosts folder.</summary>
            public readonly List<GhostRow> Ghosts = new List<GhostRow>();
            public string GhostFolder, RacingAgainst, PilotName;
            public bool CourseHasBest;
            /// <summary>Race tab: the possible lap (best sectors joined) has a line, and it's being raced against.</summary>
            public string PossibleLine;
            public bool RacingBestRace;
            public string BestRaceLine;   // the course record race that can be raced (laps and time), or null
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
            public readonly List<(string Name, string Range, int From, int To, HudColor Colour)> Sectors = new List<(string, string, int, int, HudColor)>();
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
            /// <summary>Cinematics tab: the saved cinematics, the one being edited and its shots.</summary>
            public int CineVersion;
            public readonly List<string> Cinematics = new List<string>();
            public string CineCurrent, CineFolder;
            /// <summary>History tab: finished races, oldest first (already filtered).</summary>
            public int HistoryVersion, HistoryCount;
            public bool HistoryThisCourse, HistorySessionOnly;
            public string HistoryCourse;
            public readonly List<RaceHistoryRow> History = new List<RaceHistoryRow>();
            public bool CineAutoOn;
            public readonly List<(string Label, int Gate, string Cinematic, double Offset)> CineGates = new List<(string, int, string, double)>();
            public bool CineLoop, CineFollow, CineSpotlight, CinePlaying, CineDebugOn, CinePreviewOn;
            public int CineUndos, CineRedos;
            public double CineLength;   // the timeline's set length (0 = as long as its shots)   // edits that can be undone / redone
            public int CineShotPlaying = -1;
            public double CineTime = -1;   // seconds into the timeline while it plays (-1 = not playing)
            public readonly List<CineShotRow> CineShots = new List<CineShotRow>();
            /// <summary>The effects layer: (type, start, seconds, from, to), and the game's own field of view in degrees.</summary>
            public readonly List<(string Type, double Start, double Seconds, double From, double To, int Lane)> CineEffects = new List<(string, double, double, double, double, int)>();
            public int CineLanes = 1;   // how many effect layers
            public double CineDefaultFov;
        }

        public sealed class GhostRow
        {
            public string Path, Label, Detail;
            public bool Fits, Active, Second;
        }

        public sealed class ShipRow
        {
            public string Name, BestLap, Gap, BestRace, RaceGap, TopSpeed, TurnG, Races;
            /// <summary>Its average race time on this course (from the race history), and how many races that's over.</summary>
            public string Average, AverageNote;
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
            sb.Append(Scale).Append('|').Append(BackgroundOpacity).Append('|').Append((int)(Opacity * 100)).Append('|');
            if (EdgeGlow.HasValue) sb.Append("EG:").Append(EdgeGlow.Value.R).Append(EdgeGlow.Value.G).Append('|');
            if (CpFlash != null) sb.Append("CPF:").Append(CpFlash.Text).Append(CpFlash.Colour.R).Append('|');
            if (Timer != null) sb.Append(Timer.Label).Append('|').Append(Timer.Big).Append('|').Append(Timer.BigColour.R).Append('|')
                                 .Append(Timer.Footer).Append('|').Append(Timer.BestLap).Append('|').Append(Timer.BestRace).Append(Timer.BestRaceLabel).Append('|').Append(Timer.Hint).Append(Timer.HintColour.G).Append('|').Append(Timer.Sub).Append(Timer.StartBar.HasValue ? (int)(Timer.StartBar.Value * 60) : -1).Append('|');
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
                sb.Append("MM").Append(Minimap.StaticSig).Append(Minimap.Size).Append(Minimap.HeadingUp).Append('|').Append(Minimap.LabelsAhead).Append(Minimap.NextIndex)
                  .Append((int)(Minimap.ViewX * 1000)).Append(',').Append((int)(Minimap.ViewY * 1000)).Append(',').Append((int)(Minimap.ViewSpan * 1000)).Append('|');
                foreach (var p in Minimap.Points) sb.Append(p.Colour.R).Append(p.Colour.G).Append(',');
                if (Minimap.PlayerX.HasValue) sb.Append((int)(Minimap.PlayerX * 400)).Append(',').Append((int)(Minimap.PlayerY * 400)).Append(',').Append((int)(Minimap.PlayerAngle / 3));
                foreach (var g in Minimap.Ghosts) sb.Append('g').Append((int)(g.X * 400)).Append(',').Append((int)(g.Y * 400));
            }
            sb.Append("CF:").Append((int)(CineFade * 200)).Append(',').Append((int)(CineBars * 400)).Append('|');
            if (FilmStrip != null) sb.Append("FS:").Append(FilmStrip.Lap).Append(FilmStrip.Clock).Append(FilmStrip.Speed).Append('|');
            if (CineDebug != null)
            {
                sb.Append("CD:").Append(CineDebug.Title).Append(CineDebug.Now).Append((int)(CineDebug.Time * 30)).Append('|');
                foreach (var cs in CineDebug.Shots) sb.Append(cs.Name).Append(cs.Seconds).Append(cs.Playing);
                foreach (var ce in CineDebug.Effects) sb.Append(ce.Name).Append(ce.Start).Append(ce.Seconds).Append(ce.Active).Append(ce.Lane);
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
                sb.Append("RES:").Append(Results.Title).Append(Results.Subtitle).Append(Results.Footer).Append(Results.Headline).Append(Results.HeadlineColour.R).Append(Results.Graph.Count).Append(Results.GraphsShown).Append(Results.GraphVersion).Append('|');
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
                if (Analyser.Card != null) foreach (var c in Analyser.Card) sb.Append(c.Label).Append(c.Value).Append(c.Colour.G);
                if (Analyser.Bars != null) foreach (var c in Analyser.Bars) sb.Append(c.Label).Append(c.Value).Append((int)(c.Fraction * 100));
                if (Analyser.Cover != null) sb.Append("TC:").Append(Analyser.Cover.Kicker).Append(Analyser.Cover.Title).Append(Analyser.Cover.Subtitle).Append(string.Join("|", Analyser.Cover.Lines));
                sb.Append(Analyser.ExportPath).Append(Analyser.Exporting);
                if (Analyser.Graphs != null) sb.Append("GR:").Append(Analyser.Graphs.Graph.Count).Append(Analyser.Graphs.Gap.Count);
                if (Analyser.Map != null)
                    sb.Append("MAP:").Append(Analyser.Map.Key).Append(Analyser.Map.Done).Append(',')
                      .Append((int)(Analyser.Map.DotX * 1000)).Append(',').Append((int)(Analyser.Map.DotY * 1000));
                sb.Append('|');
            }
            sb.Append("VV:").Append(VectorX.HasValue ? (int)(VectorX.Value * 2000) : -1).Append(',').Append(VectorY.HasValue ? (int)(VectorY.Value * 2000) : -1).Append('|');
            if (Course != null)
            {
                sb.Append("CO:").Append(Course.Summary).Append(Course.Summary2).Append(Course.More);
                foreach (var l in Course.Legs) sb.Append(l.Name).Append(l.Distance).Append(l.Aimed ? '!' : ',');
                foreach (var w in Course.Warnings) sb.Append(w.Label).Append(w.Text);
                sb.Append('|');
            }
            if (Timer?.Keys != null) { sb.Append("TK:").Append(Timer.Tag); foreach (var k in Timer.Keys) sb.Append(k.Key).Append(k.Words).Append(k.Dim ? '0' : '1'); sb.Append('|'); }
            sb.Append("TP:").Append(ToolbarPeek).Append(MiniToolbar).Append((int)(MiniToolbarScale * 100)).Append(GravityG.HasValue ? (int)(GravityG.Value * 100) : -1).Append((int)(GravityX * 18)).Append(',').Append((int)(GravityY * 18)).Append(GravityParts ? $"/{(int)(GravityNatG * 50)},{(int)(GravityNatX * 18)},{(int)(GravityNatY * 18)},{(int)(GravityArtG * 50)},{(int)(GravityArtX * 18)},{(int)(GravityArtY * 18)}" : "").Append('|');
            sb.Append("SP:").Append(Speedo).Append(SpeedoUnit).Append(KeysCorner).Append(',').Append(KeysDown).Append(LightsOn ? 'L' : '-').Append('|');
            if (Timer != null) sb.Append(Timer.BestLapNote).Append(Timer.BestRaceNote).Append('|');
            // When the HUD places markers itself each frame, their screen position isn't part of the signature.
            bool pos = !MarkersPlacedByUi;
            foreach (var mk in Markers)
            {
                sb.Append(mk.Text).Append(mk.Sub).Append(mk.Colour.A).Append(mk.Emphasis).Append(mk.Quiet).Append(mk.SubInline).Append(mk.SubColour?.G ?? 0);
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
