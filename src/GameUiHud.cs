using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Collections;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// The HUD drawn with the game's real UI system (Avalonia), the same system as the SPD/ALT boxes:
    /// proper dark panels with bright text on top, in the game's font.
    ///
    /// Our overlay is a Grid added to the ScreenManager's root panel (at index 0, so the game's own screens
    /// and menus draw over it). It never takes mouse or keyboard input. Updates arrive from the game thread
    /// and are applied on the UI thread via Dispatcher.UIThread.Post; identical frames are skipped.
    /// </summary>
    internal sealed class GameUiHud
    {
        public GameUiHud()
        {
            // A Race Map switch changed: redraw the minimap on the next update.
            MapOptions.Changed += () => Dispatcher.UIThread.Post(() => { lastSignature = null; mmDrawn = null; });
        }

        private Grid root;
        private Panel attachedTo;

        private Border statsBox;
        private TextBlock resultsFooter, resultsSubtitle;
        // Last contents of the splits table and stats box: they're only rebuilt when these change,
        // not every time something else on the HUD (a distance, the timer) changes.
        private string splitsDrawn, statsDrawn;
        private TextBlock statsTitle;
        private Grid statsGrid;
        private Border timerBox, infoBox, splitsBox, resultsBox, bestLapBox, bestRaceBox;
        private TextBlock bestLapValue, bestLapNote, bestRaceValue, bestRaceNote;
        private StackPanel leftColumn;
        private Viewbox leftFit;
        // Best race and best lap: one card, two rows.
        private Border bestCard, bestDivider;
        private Grid bigRow;   // the timer's big time with the gap beside it
        private StackPanel tLabelRow;
        private Grid edgeGlow;            // a soft glow round the edge of the screen
        private HudColor? edgeGlowFor;    // the colour its frames are in now
        private TextBlock tLabelRight, tFooterRight;
        // Checkpoint flash: "CP 6  +7.619" for a moment, top centre.
        private Border cpFlashBox;
        private TextBlock cpFlashName, cpFlashGap;
        private TranslateTransform cpFlashSlide;
        // Attitude: an artificial horizon (the ground turns and slides behind a fixed ship mark).
        private TextBlock rollRateText;
        private Border rollRateBox;
        private readonly List<Shape> horizonShip = new List<Shape>();   // the ship mark: tinted when the angle is steep
        private Path horizonGround;
        private Line horizonLine;
        private const double HorizonSize = 62;   // the round horizon picture (before HUD scale)
        // Where the HUD's panels are on screen: checkpoint labels behind them are hidden.
        private readonly List<Rect> hudRects = new List<Rect>();

        /// <summary>Shows or hides a panel; shown, it fades in (the panels have an opacity transition).</summary>
        private static void Appear(Control c, bool show)
        {
            if (c.IsVisible != show) c.IsVisible = show;
            double want = show ? 1 : 0;
            if (c.Opacity != want) c.Opacity = want;
        }
        // Timer in the top corner: the timer, best lap, best race, splits and stats move into this column.
        private Grid topRow;
        private StackPanel timerColumn, cornerColumn;
        private Viewbox cornerFit;
        private StackPanel mapRow, bestStack;   // timer in the corner: minimap with best race / best lap beside it
        private StackPanel splitsRow;           // timer in the corner: splits with the results beside them (towards the middle)
        private string placedKey;
        private StackPanel speedBlock, gapBlock;
        private Border graphsBox;
        private Canvas gapCanvas;
        private TextBlock gapTitle;
        private WrapPanel gapLegend;
        private const double GraphW = 400, GraphH = 120, GraphOneH = 165;   // (GraphOneH: speed and time gap on one graph)
        private WrapPanel graphLegend;
        private Canvas graphCanvas;
        private TextBlock graphTitle;
        private HudModel.ResultsPanel graphFor;
        private int graphPage, graphVersion;
        private Canvas vectorMark;
        private readonly List<TextBlock> mmCrashes = new List<TextBlock>();
        private Canvas markerLayer;
        private readonly System.Collections.Generic.List<(Border Box, TextBlock Text, TextBlock Sub)> markerPool =
            new System.Collections.Generic.List<(Border, TextBlock, TextBlock)>();
        private Path arrowShape;
        private Border arrowLabelBox;
        private TextBlock arrowLabel;
        private TextBlock tDelta, countdownText, resultsTitle, resultsHeadline, splitsHeader;
        private StackPanel dots, splitRows, resultLines;
        private TextBlock tLabel, tBig, tFooter, tHint, tSub;
        private Border tStartFill;
        private Grid tStartBar;
        private TextBlock tTagText;
        private Border tTag;
        private Grid tKeys;
        private string tKeysDrawn, courseDrawn;
        private double tBigSize = 44;
        private Border lapsBox;
        // Minimap
        private Border minimapBox;
        private Canvas minimapCanvas;
        private Path mmRoute, mmLine, mmLine2;
        private Polygon mmPlayer;
        private readonly List<Control> mmPoints = new List<Control>();
        private readonly List<Border> mmLabels = new List<Border>();
        private Canvas mmSectors;   // named sectors: wide see-through bands
        private readonly List<(string Name, HudColor Colour, Point Mid, Vector Normal)> mmSectorInfo = new List<(string, HudColor, Point, Vector)>();
        private Canvas mmSegs;   // the racing line as coloured segments (speed / braking colours)
        private Canvas mmContent; // route + lines, scaled / moved for zoom and follow
        private Ellipse mmBack;   // the round background
        private Line mmToNext;
        private Ellipse mmRing;
        private Point mmRingAt;
        private double mmRingBase = 12;
        private DispatcherTimer mmPulseTimer;
        private readonly List<Ellipse> mmGhosts = new List<Ellipse>();
        private string mmDrawn;
        private double mmClipSize = -1;
        private long mmUpdatedAt;
        private ScaleTransform mmRingScale;   // made on the UI thread when first needed
        private string mmLabelsSig;   // what the labels were last placed for
        private long mmLinesAt;                 // when the lines were last redrawn (they're redrawn at most ~30 times a second)
        private RotateTransform mmPlayerTurn;   // made on the UI thread when first needed (Avalonia objects belong to it)
        private readonly List<Path> mmSectorBands = new List<Path>();
        private readonly List<Border> mmSectorTagPool = new List<Border>();
        private Path mmLineUnder;               // a dark edge under the racing line, so it stands clear of the map
        private readonly List<Control> mmFlags = new List<Control>();   // a chequered square at each gate (null for checkpoints)
        private Ellipse mmRim, mmRimLine;       // the frame: a dark rim with a thin light line inside it, like the speedometer's
        private readonly List<Ellipse> mmFadeRings = new List<Ellipse>();   // zoomed in: the track fades towards the rim
        private Polygon mmTick;                 // a mark at the top of the rim
        private Border mmSectorTag;             // under the map: the named sector you're in
        private TextBlock mmSectorName;
        private bool mmResultsUp;               // results on screen: the map draws as little as it can
        private TextBlock lapsHeader;
        private StackPanel lapRows;
        private Border centreBox;
        private Panel raceDrop;                  // race messages: a tab that drops down under the timer
        private Avalonia.Controls.Shapes.Path raceFill;
        private Border raceBack;
        private bool timerSquare;   // the timer box's bottom corners are square (the message tab is out)
        private double raceDropHeight, raceScale = 1;
        private Grid raceTab;
        private TextBlock raceText, centreText;

        /// <summary>Projects a world point to 0..1 screen coordinates (the bridge's camera projection).</summary>
        public delegate bool ProjectFn(Vec3D world, out double x, out double y, out bool behind);
        public ProjectFn Projector;

        // Markers currently shown, re-placed every rendered frame from their world position so they move
        // smoothly with the camera instead of stepping with the game's update.
        private sealed class Placed
        {
            public Border Box;
            public string Key;
            public Vec3D? World;
            public double X, Y;       // fallback 0..1 position from the game thread
            public double W, H;       // box size
            public bool Target;       // the checkpoint you're heading for: never hidden behind a panel
        }
        private readonly List<Placed> placed = new List<Placed>();
        private readonly Dictionary<string, (double x, double y)> smooth = new Dictionary<string, (double, double)>();
        private HudModel.EdgeArrow placedArrow;
        private (double x, double y, double a)? smoothArrow;
        private bool frameLoop, projectBroken;
        private double markerScale = 1;

        private HudModel pending;
        private int postQueued;
        private string lastSignature;
        private volatile bool failed;
        private double builtScale = -1;

        /// <summary>True once something went wrong; the plugin then falls back to debug draw.</summary>
        public bool Failed => failed;

        /// <summary>Called from the game thread every frame. model = null hides the overlay.</summary>
        public bool Show(Panel rootPanel, HudModel model)
        {
            if (failed || rootPanel == null) return false;

            Volatile.Write(ref pending, model);
            Volatile.Write(ref attachTarget, rootPanel);
            if (Interlocked.Exchange(ref postQueued, 1) == 0)
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
            return true;
        }

        private Panel attachTarget;
        private int failures;

        private void Apply()
        {
            Interlocked.Exchange(ref postQueued, 0);
            if (failed) return;
            try
            {
                var model = Volatile.Read(ref pending);
                var target = Volatile.Read(ref attachTarget);

                double scale = model?.Scale ?? (builtScale > 0 ? builtScale : 1.0);
                if (root == null || Math.Abs(scale - builtScale) > 1e-6)
                    Build(scale);

                Attach(target);

                if (model == null)
                {
                    SetHideGameHud(false);   // the game's HUD comes back whenever ours isn't showing
                    root.IsVisible = false;
                    lastSignature = null;
            splitsDrawn = statsDrawn = null; tKeysDrawn = courseDrawn = null; tBigSize = 44;
                    return;
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                string sig = model.Signature();
                root.IsVisible = true;
                if (sig == lastSignature) { PluginLog.Time("hud check", (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); return; }
                lastSignature = sig;
                hudLapAt = System.Diagnostics.Stopwatch.GetTimestamp();
                Update(model);
                HudLap("hud: messages and the rest");
                PluginLog.Time("hud update", (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            catch (Exception e)
            {
                // First tries: build the HUD again from scratch. Only after repeated failures fall back to the
                // debug-draw panels for the rest of the session.
                failures++;
                if (failures <= 3)
                {
                    PluginLog.Error($"Game UI HUD failed (try {failures} of 3); rebuilding it", e);
                    builtScale = -1;
                    lastSignature = null;
                    return;
                }
                failed = true;
                PluginLog.Error("Game UI HUD failed again; falling back to debug-draw panels", e);
                try { if (root != null) root.IsVisible = false; } catch { /* ignore */ }
            }
        }

        private void Attach(Panel target)
        {
            if (target == null) return;
            if (ReferenceEquals(attachedTo, target))
            {
                // The game rebuilt its screens and dropped our overlay: put it back underneath them.
                if (root != null && !target.Children.Contains(root)) { target.Children.Insert(0, root); PluginLog.Info("Game UI HUD re-attached"); }
                return;
            }
            if (attachedTo != null) attachedTo.Children.Remove(root);
            target.Children.Insert(0, root); // first child = drawn underneath the game's own screens
            attachedTo = target;
            PluginLog.Info($"Game UI HUD attached to {target.GetType().Name}");
        }

        // ---------------------------------------------------------------- look

        // One brush per colour, reused: handing a control a new (if identical) brush every frame makes it redraw.
        private static IBrush Brush(HudColor c) => UiBrushes.Of(c);
        private static IBrush Brush(byte a, byte r, byte g, byte b) => UiBrushes.Get(a, r, g, b);

        // Matches the game's SPD/ALT boxes: near-black panel, thin bright border, small rounded corners.
        private static readonly IBrush BorderBrush = new SolidColorBrush(Color.FromArgb(130, 235, 238, 240));   // (a quiet edge: the panels read as one HUD, not a row of frames)
        private static readonly IBrush White = new SolidColorBrush(Colors.White);
        private static readonly IBrush Grey = new SolidColorBrush(Color.FromArgb(230, 185, 190, 195));

        /// <summary>Every panel the same: thin border, the same corners and the same inner space.</summary>
        private Border Box(double s) => new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(PanelCorner * s),
            Padding = PanelPadding(s),
        };
        private const double PanelCorner = 6;
        private static Thickness PanelPadding(double s) => new Thickness(14 * s, 8 * s, 14 * s, 10 * s);

        private static TextBlock Label(double size, IBrush brush, FontWeight weight = FontWeight.Normal) => new TextBlock
        {
            FontSize = size,
            Foreground = brush,
            FontWeight = weight,
            TextWrapping = TextWrapping.NoWrap,
        };

        /// <summary>Opacity changes on this control fade instead of snapping.</summary>
        private static void AddFade(Visual v, double ms)
        {
            v.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(ms) },
            };
            v.Opacity = 0;
        }

        // ---------------------------------------------------------------- build (once, or when HudScale changes)

        private void Build(double s)
        {
            builtScale = s;
            graphHeads.Clear();
            powerProp = dampProp = parkProp = null; cockpitHudRef = null; antennaLooked = false; antennaRead = null;
            var oldParent = attachedTo;
            if (oldParent != null && root != null) { oldParent.Children.Remove(root); attachedTo = null; }

            // Rows: 0-25% (race message sits at the bottom), 25-30%, 30-100% (centre message at the top).
            root = new Grid
            {
                IsHitTestVisible = false,
                Focusable = false,
            };
            root.RowDefinitions.Add(new RowDefinition(new GridLength(25, GridUnitType.Star)));
            root.RowDefinitions.Add(new RowDefinition(new GridLength(5, GridUnitType.Star)));
            root.RowDefinitions.Add(new RowDefinition(new GridLength(70, GridUnitType.Star)));

            // ---- timer panel, top centre, with best lap + hint under it
            tLabel = Label(17 * s, White);
            tLabel.HorizontalAlignment = HorizontalAlignment.Center;
            tBig = Label(44 * s, White, FontWeight.Bold);
            tBig.HorizontalAlignment = HorizontalAlignment.Center;
            tFooter = Label(16 * s, White);
            tFooter.HorizontalAlignment = HorizontalAlignment.Center;
            // (Racing: the total at the left end of the line, any penalty in red at the right.)
            tFooterRight = Label(16 * s, Brush(255, 232, 150, 142));   // (a calmer red than the message under it)
            tFooterRight.HorizontalAlignment = HorizontalAlignment.Right;
            tFooterRight.IsVisible = false;
            var tFooterLine = new Grid { Children = { tFooter, tFooterRight } };

            timerBox = Box(s);
            timerBox.MinWidth = 300 * s;
            timerBox.HorizontalAlignment = HorizontalAlignment.Center;
            // (The gap sits beside the time, not on a line of its own: the time stays centred, the gap to its right.)
            tDelta = Label(17 * s, White, FontWeight.Bold);
            tDelta.HorizontalAlignment = HorizontalAlignment.Left;
            tDelta.VerticalAlignment = VerticalAlignment.Bottom;
            tDelta.Margin = new Thickness(8 * s, 0, 0, 9 * s);
            dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6 * s,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2 * s, 0, 2 * s),
            };
            // (Course edit mode: a coloured tag after the label for the placement mode, and the keys as rows.)
            tTagText = Label(12 * s, White, FontWeight.Bold);
            tTag = new Border
            {
                Child = tTagText, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3 * s), IsVisible = false,
                Padding = new Thickness(6 * s, 0, 6 * s, 1 * s), Margin = new Thickness(8 * s, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            tLabelRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { tLabel, tTag } };
            // (Racing: the lap count at the left end of this line and the checkpoint count at the right.)
            tLabelRight = Label(17 * s, White);
            tLabelRight.HorizontalAlignment = HorizontalAlignment.Right;
            tLabelRight.IsVisible = false;
            var tLabelLine = new Grid { Children = { tLabelRow, tLabelRight } };
            tKeys = new Grid { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4 * s, 0, 2 * s) };
            tKeys.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            tKeys.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            bigRow = new Grid();
            bigRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            bigRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            bigRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            Grid.SetColumn(tBig, 1);
            Grid.SetColumn(tDelta, 2);
            bigRow.Children.Add(tBig);
            bigRow.Children.Add(tDelta);
            // Waiting at the start: how to start in small grey words, and a thin bar that fills as you close on the gate.
            tSub = Label(13 * s, Brush(255, 170, 180, 190));
            tSub.HorizontalAlignment = HorizontalAlignment.Center;
            tSub.TextAlignment = TextAlignment.Center;
            tSub.IsVisible = false;
            tStartFill = new Border { Height = 3 * s, Width = 0, Background = Brush(HudColor.Green), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5 * s) };
            tStartBar = new Grid
            {
                Height = 3 * s, Margin = new Thickness(0, 3 * s, 0, 2 * s), IsVisible = false,
                Children = { new Border { Background = Brush(255, 44, 52, 60), CornerRadius = new CornerRadius(1.5 * s) }, tStartFill },
            };
            timerBox.Child = new StackPanel { Spacing = 2 * s, Children = { tLabelLine, bigRow, dots, tFooterLine, tSub, tStartBar, tKeys } };
            AddFade(timerBox, 220);

            // Side panels: best lap (left of the timer) and best race (right).
            bestLapValue = Label(20 * s, Brush(HudColor.Yellow), FontWeight.Bold);
            bestLapBox = SideBox(s, "BEST LAP", null, bestLapValue, HorizontalAlignment.Right);
            bestLapNote = Label(11 * s, Brush(255, 205, 170, 255));
            ((StackPanel)bestLapBox.Child).Children.Add(bestLapNote);
            bestRaceValue = Label(20 * s, Brush(HudColor.Yellow), FontWeight.Bold);
            bestRaceBox = SideBox(s, "BEST RACE", null, bestRaceValue, HorizontalAlignment.Left);   // no "3 LAPS" line
            bestRaceNote = Label(11 * s, Brush(255, 205, 170, 255));
            ((StackPanel)bestRaceBox.Child).Children.Add(bestRaceNote);

            // Three columns keep the timer exactly centred whatever the side panels' widths.
            topRow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            topRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            topRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            topRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            // One card for both: best race over best lap, a thin line between (the two boxes are plain rows in it).
            foreach (var plainRow in new[] { bestLapBox, bestRaceBox })
            {
                plainRow.BorderThickness = new Thickness(0);
                plainRow.Padding = new Thickness(0);
                plainRow.Margin = new Thickness(0);
                plainRow.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            bestDivider = new Border { Height = 1, Background = Brush(255, 60, 72, 84), Margin = new Thickness(0, 4 * s, 0, 3 * s) };
            bestCard = Box(s);
            bestCard.Padding = new Thickness(12 * s, 6 * s, 12 * s, 7 * s);
            bestCard.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { bestRaceBox, bestDivider, bestLapBox } };
            bestCard.Margin = new Thickness(10 * s, 0, 0, 0);
            bestCard.HorizontalAlignment = HorizontalAlignment.Left;
            bestCard.VerticalAlignment = VerticalAlignment.Top;
            AddFade(bestCard, 220);
            // Race messages (GO!, lap times, penalties, missed checkpoints) drop down from under the timer in a tab
            // that narrows towards the bottom, in the same dark panel and thin border as the rest.
            raceText = Label(16 * s, White, FontWeight.Bold);
            raceText.HorizontalAlignment = HorizontalAlignment.Center;
            raceText.VerticalAlignment = VerticalAlignment.Center;
            raceDropHeight = 28 * s;
            timerSquare = false;
            raceFill = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 100,0 L 92,100 L 8,100 Z"),
                Stretch = Stretch.Fill,
            };
            var raceEdge = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 8,100 L 92,100 L 100,0"),
                Stretch = Stretch.Fill,
                Stroke = BorderBrush,
                StrokeThickness = 1,
            };
            var raceTab = new Grid
            {
                Height = raceDropHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            // A long message wraps onto a second line and the tab gets taller; it's never wider than the timer.
            raceText.TextWrapping = TextWrapping.Wrap;
            raceText.TextAlignment = TextAlignment.Center;
            raceScale = s;
            raceText.Margin = new Thickness(0, 2 * s, 0, 4 * s);
            // The tab is a straight continuation of the timer box: the same width, fill and edge, square where it
            // joins the box and rounded at the bottom like the box's own corners (which go square while it's out).
            raceBack = new Border
            {
                BorderBrush = BorderBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, PanelCorner * s, PanelCorner * s),
            };
            raceTab.Children.Add(raceBack);
            raceTab.Children.Add(raceText);
            this.raceTab = raceTab;
            // The tab slides down out of the timer box: its holder grows from 0 to the tab's height, and only the
            // part inside the holder shows.
            raceDrop = new Panel
            {
                Height = 0,
                ClipToBounds = true,
                Margin = new Thickness(0, -1, 0, 0),
                Children = { raceTab },
            };
            raceDrop.Transitions = new Transitions
            {
                new DoubleTransition { Property = Layoutable.HeightProperty, Duration = TimeSpan.FromMilliseconds(220) },
            };
            timerColumn = new StackPanel { Children = { timerBox, raceDrop } };
            // The tab is exactly as wide as the timer box (less its insets): it never makes the timer column wider.
            raceDrop.Width = 0;
            timerBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty)
                    raceDrop.Width = Math.Max(0, timerBox.Bounds.Width);
            };
            Grid.SetColumn(timerColumn, 1);
            Grid.SetColumn(bestCard, 2);
            topRow.Children.Add(timerColumn);
            topRow.Children.Add(bestCard);

            // "How to start" in its own panel under the timer.
            tHint = Label(16 * s, White);
            tHint.HorizontalAlignment = HorizontalAlignment.Center;
            infoBox = Box(s);
            infoBox.HorizontalAlignment = HorizontalAlignment.Center;
            infoBox.Padding = new Thickness(14 * s, 4 * s, 14 * s, 6 * s);   // (one line of text: a little slimmer)
            infoBox.Child = tHint;
            AddFade(infoBox, 300);

            var timerArea = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 30 * s, 0, 0),
                Spacing = 6 * s,
                Children = { topRow, infoBox },
            };
            Grid.SetRowSpan(timerArea, 3);


            // ---- lap list, left, top at 25%
            lapsHeader = Label(17 * s, White);
            lapsHeader.HorizontalAlignment = HorizontalAlignment.Center;   // (every panel's heading centred)
            lapRows = new StackPanel { Spacing = 3 * s };
            lapsBox = Box(s);
            // Only as wide as it needs to be: grows when a lap has penalty notes.
            lapsBox.MinWidth = 210 * s;
            lapsBox.Child = new StackPanel { Spacing = 6 * s, Children = { lapsHeader, lapRows } };
            AddFade(lapsBox, 220);

            // ---- centre message, top of row 2 (30%)
            centreText = Label(17 * s, White);
            centreBox = Box(s);
            centreBox.Padding = new Thickness(14 * s, 6 * s, 14 * s, 8 * s);
            centreBox.Child = centreText;
            centreBox.HorizontalAlignment = HorizontalAlignment.Center;
            centreBox.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(centreBox, 2);

            // ---- splits, right side, top at 25% (mirrors the lap list)
            splitsHeader = Label(17 * s, White);
            splitsHeader.HorizontalAlignment = HorizontalAlignment.Center;
            splitRows = new StackPanel { Spacing = 1 * s };
            splitsBox = Box(s);
            splitsBox.MinWidth = 220 * s;
            splitsBox.HorizontalAlignment = HorizontalAlignment.Right;
            splitsBox.VerticalAlignment = VerticalAlignment.Top;
            splitsBox.Margin = new Thickness(0, 0, 30 * s, 0);
            splitsBox.Child = new StackPanel { Spacing = 6 * s, Children = { splitsHeader, splitRows } };
            AddFade(splitsBox, 220);
            Grid.SetRow(splitsBox, 1);
            Grid.SetRowSpan(splitsBox, 2);

            // ---- race stats after the finish: own box in the top corner, two columns of label / value
            statsTitle = Label(17 * s, White);
            statsTitle.Text = "RACE STATS";
            statsTitle.HorizontalAlignment = HorizontalAlignment.Center;
            statsGrid = new Grid();
            statsBox = Box(s);
            statsBox.HorizontalAlignment = HorizontalAlignment.Right;
            statsBox.VerticalAlignment = VerticalAlignment.Top;
            statsBox.Margin = new Thickness(0, 40 * s, 30 * s, 0);
            statsBox.Child = new StackPanel { Spacing = 6 * s, Children = { statsTitle, statsGrid } };
            statsBox.IsVisible = false;
            Grid.SetRow(statsBox, 0);
            Grid.SetRowSpan(statsBox, 3);   // grows down as far as it needs (the course list in edit mode can be long)

            // ---- results, centre, under the centre message
            resultsTitle = Label(17 * s, White);
            resultsTitle.HorizontalAlignment = HorizontalAlignment.Center;
            resultsHeadline = Label(26 * s, Brush(HudColor.Yellow), FontWeight.Bold);
            resultsHeadline.HorizontalAlignment = HorizontalAlignment.Center;
            // A long headline goes onto a second line rather than being cut off with "...".
            resultsHeadline.TextTrimming = TextTrimming.None;
            resultsHeadline.TextWrapping = TextWrapping.Wrap;
            resultsHeadline.TextAlignment = TextAlignment.Center;
            resultLines = new StackPanel { Spacing = 4 * s };
            resultsBox = Box(s);
            // Results sit under the lap list (out of the way of the crosshair and the ship).
            resultsBox.Width = 400 * s;
            resultsBox.Padding = new Thickness(18 * s, 10 * s, 18 * s, 14 * s);
            // Speed graph: speed round the lap (left = start, right = finish) for the best lap and the record.
            // The two graphs (speed, and the time gap) sit one above the other in their own box on the right.
            graphTitle = Label(13 * s, Grey);
            graphTitle.TextWrapping = TextWrapping.Wrap;
            graphCanvas = new Canvas { Width = GraphW * s, Height = GraphH * s, ClipToBounds = true };
            graphLegend = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = GraphW * s };
            speedBlock = new StackPanel { Spacing = 4 * s, Children = { GraphHead(graphTitle, s), graphCanvas, graphLegend } };
            gapTitle = Label(13 * s, Grey);
            gapTitle.TextWrapping = TextWrapping.Wrap;
            gapCanvas = new Canvas { Width = GraphW * s, Height = GraphH * s, ClipToBounds = true };
            gapLegend = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = GraphW * s };
            gapBlock = new StackPanel { Spacing = 4 * s, Children = { GraphHead(gapTitle, s), gapCanvas, gapLegend } };
            graphsBox = Box(s);
            graphsBox.Padding = new Thickness(16 * s, 10 * s, 16 * s, 12 * s);
            graphsBox.Child = new StackPanel { Spacing = 14 * s, Children = { speedBlock, gapBlock } };
            graphsBox.HorizontalAlignment = HorizontalAlignment.Right;
            graphsBox.VerticalAlignment = VerticalAlignment.Top;
            graphsBox.IsVisible = false;   // (placed under the race stats by PlaceTimer)
            AddFade(graphsBox, 400);
            resultsSubtitle = Label(15 * s, Brush(HudColor.Cyan));
            resultsSubtitle.HorizontalAlignment = HorizontalAlignment.Center;
            resultsSubtitle.TextAlignment = TextAlignment.Center;
            resultsSubtitle.TextWrapping = TextWrapping.Wrap;
            resultsSubtitle.Margin = new Thickness(0, -6 * s, 0, 0);
            resultsTitle.TextWrapping = TextWrapping.Wrap;
            resultsTitle.TextAlignment = TextAlignment.Center;
            resultsFooter = Label(13 * s, Grey);
            resultsFooter.HorizontalAlignment = HorizontalAlignment.Center;
            resultsBox.Child = new StackPanel { Spacing = 8 * s, Children = { resultsTitle, resultsSubtitle, resultsHeadline, resultLines, resultsFooter } };
            AddFade(resultsBox, 400);

            // ---- left column (right with SwapSides), from the top corner: minimap, race message,
            // lap list, then results. If it's taller than the screen (results with a long lap list),
            // the whole column is scaled down to fit instead of running off the bottom.
            leftColumn = new StackPanel
            {
                Spacing = 10 * s,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Children = { MakeMinimap(s), lapsBox, resultsBox },
            };
            leftFit = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(30 * s, 30 * s, 30 * s, 30 * s),
                Child = leftColumn,
            };
            Grid.SetRow(leftFit, 0);
            Grid.SetRowSpan(leftFit, 3);

            // ---- checkpoint labels and the off-screen arrow (whole screen)
            markerLayer = new Canvas { IsHitTestVisible = false };
            Grid.SetRowSpan(markerLayer, 3);
            arrowShape = new Path
            {
                Data = Geometry.Parse("M 0,0 L 7,0 L 18,15 L 7,30 L 0,30 L 11,15 Z"),   // (a chevron)
                Width = 17 * s,
                Height = 28 * s,
                Stretch = Stretch.Fill,
                Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                StrokeThickness = 1,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                IsVisible = false,
            };
            arrowLabel = Label(15 * s, White, FontWeight.Bold);
            arrowLabel.TextAlignment = TextAlignment.Center;   // (two lines: the gate and its distance, then which way to turn)
            arrowLabelBox = Box(s);
            arrowLabelBox.Padding = new Thickness(11 * s, 2 * s, 11 * s, 3 * s);
            arrowLabelBox.CornerRadius = new CornerRadius(12 * s);   // (a pill)
            arrowLabelBox.Child = arrowLabel;
            arrowLabelBox.IsVisible = false;
            AddFade(arrowShape, 200);
            AddFade(arrowLabelBox, 200);
            markerLayer.Children.Add(arrowShape);
            markerLayer.Children.Add(arrowLabelBox);

            // ---- countdown, big, middle of the screen
            countdownText = Label(120 * s, White, FontWeight.Bold);
            countdownText.HorizontalAlignment = HorizontalAlignment.Center;
            countdownText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRowSpan(countdownText, 3);
            AddFade(countdownText, 150);

            // Messages fade in and out.
            AddFade(centreBox, 250);

            // ---- checkpoint flash: "CP 6  +7.619" drops in at the top centre as you pass a gate, then fades
            cpFlashName = Label(20 * s, White, FontWeight.Bold);
            cpFlashGap = Label(20 * s, White, FontWeight.Bold);
            cpFlashBox = Box(s);
            cpFlashBox.Padding = new Thickness(18 * s, 5 * s, 18 * s, 7 * s);
            cpFlashBox.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 * s, Children = { cpFlashName, cpFlashGap } };
            cpFlashBox.HorizontalAlignment = HorizontalAlignment.Center;
            cpFlashBox.VerticalAlignment = VerticalAlignment.Bottom;   // (the bottom of the top quarter: under the timer, over the view ahead)
            cpFlashSlide = new TranslateTransform { Y = -18 * s };
            cpFlashSlide.Transitions = new Transitions { new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(180) } };
            cpFlashBox.RenderTransform = cpFlashSlide;
            Grid.SetRow(cpFlashBox, 0);
            AddFade(cpFlashBox, 180);

            // ---- a soft glow round the edge of the screen (a checkpoint passed, a penalty): three frames, one inside
            // the other, each fainter, so it falls away towards the middle. Fades in and out.
            edgeGlow = new Grid { IsHitTestVisible = false };
            // (Six fine bands, 30 units deep in all: the same as the panels' margin from the screen's edge, so the glow
            // stops where the panels start instead of washing over them.)
            for (int frame = 0; frame < 6; frame++)
                edgeGlow.Children.Add(new Border { BorderThickness = new Thickness(5 * s), Margin = new Thickness(frame * 5 * s), IsHitTestVisible = false });
            Grid.SetRowSpan(edgeGlow, 3);
            AddFade(edgeGlow, 350);
            edgeGlowFor = null;
            root.Children.Add(edgeGlow);
            root.Children.Add(markerLayer); // under the panels
            root.Children.Add(timerArea);
            root.Children.Add(leftFit);
            root.Children.Add(splitsBox);
            root.Children.Add(statsBox);
            cornerColumn = new StackPanel { Spacing = 10 * s, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            cornerFit = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(30 * s, 30 * s, 30 * s, 30 * s),
                Child = cornerColumn,
            };
            Grid.SetRowSpan(cornerFit, 3);
            root.Children.Add(cornerFit);
            placedKey = null; mapRow = null; bestStack = null; splitsRow = null; statsStack = null;
            // ---- speedometer: a round dial in the bottom left corner (arc that fills with speed, big number in the middle)
            dialSize = 240 * s;
            dialStatic = new Canvas { Width = dialSize, Height = dialSize };
            dialArc = new Path { StrokeThickness = 11 * s, StrokeLineCap = PenLineCap.Flat };
            dialTip = new Line { StrokeThickness = 3 * s, Stroke = White, StrokeLineCap = PenLineCap.Round };
            speedoValue = Label(52 * s, White, FontWeight.Bold);
            speedoValue.HorizontalAlignment = HorizontalAlignment.Center;
            speedoUnit = Label(14 * s, Grey);
            speedoUnit.HorizontalAlignment = HorizontalAlignment.Center;
            // Under the unit: the speed in the other unit (km/h when the game shows m/s), its number over its unit
            // like the main one (so it can't be read as one of the dial's own numbers beside it).
            // (Quieter than the main number: smaller and grey, so the speed itself stands out.)
            speedoOther = Label(16 * s, Brush(255, 160, 170, 178), FontWeight.SemiBold);
            speedoOther.HorizontalAlignment = HorizontalAlignment.Center;
            speedoOther.Margin = new Thickness(0, 2 * s, 0, 0);
            speedoOtherUnit = Label(10 * s, Grey);
            speedoOtherUnit.HorizontalAlignment = HorizontalAlignment.Center;
            // Height above the ground: at the bottom, in the dial's open gap.
            speedoGround = Label(15 * s, Brush(255, 160, 170, 178), FontWeight.SemiBold);
            speedoGround.HorizontalAlignment = HorizontalAlignment.Center;
            groundLabel = Label(10 * s, Grey);
            groundLabel.Text = "ABOVE GROUND";
            groundLabel.HorizontalAlignment = HorizontalAlignment.Center;
            speedoGroundBox = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 18 * s), IsVisible = false, Children = { speedoGround, groundLabel },
            };
            // Safe speed: a small gauge to the left of the unit (M/S), inside the dial's numbers, so it never sits on
            // the scale (whose numbers move with the dial's top speed) or the speed itself.
            safeSpeedIcon = new Border
            {
                Width = 16 * s, Height = 16 * s,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false, IsHitTestVisible = false,
                Child = new Path { Data = Geometry.Parse(IconSafe), Fill = Brush(255, 120, 220, 140), StrokeThickness = 0, Stretch = Stretch.Uniform },
            };
            // Ship lights: a small lamp on the other side of the unit, while the ship's lights are on.
            lightIcon = new Border
            {
                Width = 16 * s, Height = 16 * s,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false, IsHitTestVisible = false,
                Child = new Path { Data = Geometry.Parse(IconLamp), Fill = Brush(255, 255, 214, 110), StrokeThickness = 0, Stretch = Stretch.Uniform },
            };
            var unitRow = new Grid { Width = 100 * s, HorizontalAlignment = HorizontalAlignment.Center, Children = { speedoUnit, safeSpeedIcon, lightIcon } };
            var dialText = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -6 * s, 0, 0),   // (a little high, clear of the scale numbers at the bottom, not into the top ones)
                Children = { speedoValue, unitRow, speedoOther, speedoOtherUnit },
            };
            var dial = new Grid { Width = dialSize, Height = dialSize };
            dial.Children.Add(dialStatic);
            dial.Children.Add(new Canvas { Width = dialSize, Height = dialSize, Children = { dialArc, dialTip } });
            dial.Children.Add(dialText);
            dial.Children.Add(speedoGroundBox);
            // Three short bars round the top of the dial, outside it: hydrogen, stored power, power use.
            shipBars = new Canvas { Width = dialSize, Height = dialSize, IsHitTestVisible = false, IsVisible = false };
            dial.Children.Add(shipBars);
            shipBarsFor = -1;
            // Two small tabs off the dial's right side: the ship's power and its inertia dampeners (the game's own icons).
            // A pod on the dial's right side (tucked under its edge, same fill and border, so it's part of the dial)
            // holding two of the game's own toggle tiles: power and inertia dampeners.
            powerTab = ShipTab(s, out powerIcon);
            parkTab = ShipTab(s, out parkIcon);
            antennaTab = ShipTab(s, out antennaIcon);
            dampTab = ShipTab(s, out dampIcon);
            // Two by two, in the game's order: power, parking brake / antenna, dampeners.
            shipTabs = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            shipTabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            shipTabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            shipTabs.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            shipTabs.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (tab, row, col) in new[] { (powerTab, 0, 0), (parkTab, 0, 1), (antennaTab, 1, 0), (dampTab, 1, 1) })
            {
                Grid.SetRow(tab, row); Grid.SetColumn(tab, col);
                tab.Margin = new Thickness(col == 1 ? 6 * s : 0, row == 1 ? 6 * s : 0, 0, 0);
                shipTabs.Children.Add(tab);
            }
            // (No fill or border of its own: one outline round the dial and the pod together draws both, so the pod
            // grows out of the dial instead of sitting beside it.)
            // (Held to the dial's height: the round part reaches out past the rings, above and below the dial, and if that
            // counted for layout the speedometer would grow taller and the pod, centred in it, would slip down off its outline.)
            speedoOutline = new Path { StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false, Height = dialSize };
            shipLegend = new StackPanel { Spacing = 1 * s, IsVisible = false, Margin = new Thickness(0, 2 * s, 0, 0) };
            shipPod = new Border
            {
                Child = new StackPanel { Spacing = 6 * s, Children = { shipTabs, shipLegend } },
                Padding = new Thickness(26 * s, 7 * s, 7 * s, 7 * s),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(dialSize - 26 * s, 0, 0, 0), IsVisible = false,
            };
            // (The outline and the rings are drawn to the pod's size. That size only settles after the layout that
            // follows a change to what's in it, a row hidden, the names appearing, so they're redrawn whenever it
            // changes; sitting still, nothing else would redraw them and the names hung out below the outline.)
            shipPod.PropertyChanged += (_, podChange) =>
            {
                if (podChange.Property != Visual.BoundsProperty || speedoBox == null || !speedoBox.IsVisible) return;
                try { UpdateShipBars(builtScale); } catch { }
            };
            dial.HorizontalAlignment = HorizontalAlignment.Left;   // (the pod makes the cell wider: the dial stays at the left)
            dial.VerticalAlignment = VerticalAlignment.Top;
            speedoBox = new Border { Child = new Grid { Children = { speedoOutline, shipPod, dial } } };
            shipTabsIcons = false;
            speedoBox.HorizontalAlignment = HorizontalAlignment.Left;
            speedoBox.VerticalAlignment = VerticalAlignment.Bottom;
            speedoBox.Margin = new Thickness(40 * s, 0, 0, 40 * s);
            speedoBox.IsVisible = false;
            AddFade(speedoBox, 220);
            Grid.SetRowSpan(speedoBox, 3);
            dialFor = -1;

            // ---- flight keys, bottom corner: Q W E / A S D / C Space
            keyCells.Clear();
            var keyGrid = new Grid();
            for (int c = 0; c < 3; c++) keyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(KeyCell * s, GridUnitType.Pixel)));
            for (int r = 0; r < 3; r++) keyGrid.RowDefinitions.Add(new RowDefinition(new GridLength(KeyCell * s, GridUnitType.Pixel)));
            string[] keyNames = { "Q", "W", "E", "A", "S", "D", "C", "SPACE" };
            (int Row, int Col, int Span)[] keyPlaces = { (0, 0, 1), (0, 1, 1), (0, 2, 1), (1, 0, 1), (1, 1, 1), (1, 2, 1), (2, 0, 1), (2, 1, 2) };
            for (int k = 0; k < keyNames.Length; k++)
            {
                var cell = new Border
                {
                    BorderBrush = BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4 * s),
                    Margin = new Thickness(2 * s),
                    Child = new TextBlock { Text = keyNames[k], FontSize = (k == 7 ? 9 : 12) * s, FontWeight = FontWeight.Bold, Foreground = White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                Grid.SetRow(cell, keyPlaces[k].Row); Grid.SetColumn(cell, keyPlaces[k].Col); Grid.SetColumnSpan(cell, keyPlaces[k].Span);
                keyGrid.Children.Add(cell);
                keyCells.Add(cell);
            }
            // In a panel like the attitude box beside it: same border, corners, background and height.
            keysBox = Box(s);
            keysBox.Padding = new Thickness(6 * s);
            keysBox.Height = BottomPanelHeight * s;
            keyGrid.VerticalAlignment = VerticalAlignment.Center;
            keyGrid.HorizontalAlignment = HorizontalAlignment.Center;
            keysBox.Child = keyGrid;
            keysBox.VerticalAlignment = VerticalAlignment.Bottom;
            keysBox.Margin = new Thickness(30 * s, 0, 30 * s, 40 * s);
            keysBox.IsVisible = false;
            AddFade(keysBox, 220);
            Grid.SetRowSpan(keysBox, 3);
            // Attitude: a small artificial horizon. The ground (brown) under the sky (blue) turns with the ship's roll and
            // slides with its pitch, behind a fixed mark for the ship; the numbers sit small underneath.
            // (A circle: the sky is a disc, the ground the part of it below the horizon line, worked out as a shape of
            // its own rather than a big turned rectangle cut off by a clip, which the game's UI doesn't always apply.
            // Quiet colours from the rest of the HUD: slate sky, near-black ground, a pale line between.)
            var rollCanvas = new Canvas { Width = HorizonSize * s, Height = HorizonSize * s, HorizontalAlignment = HorizontalAlignment.Center };
            rollCanvas.Children.Add(new Ellipse { Width = HorizonSize * s, Height = HorizonSize * s, Fill = Brush(255, 44, 62, 80) });
            horizonGround = new Path { Fill = Brush(255, 16, 20, 25), IsHitTestVisible = false };
            horizonLine = new Line { Stroke = Brush(255, 200, 208, 216), StrokeThickness = 1.5 * s, IsHitTestVisible = false };
            rollCanvas.Children.Add(horizonGround);
            rollCanvas.Children.Add(horizonLine);
            rollCanvas.Children.Add(new Ellipse { Width = HorizonSize * s, Height = HorizonSize * s, Stroke = BorderBrush, StrokeThickness = 1.5 * s });
            // The ship: two wings and a dot, always level in the middle.
            double shipMid = HorizonSize / 2 * s;
            horizonShip.Clear();
            horizonShip.Add(new Line { StartPoint = new Point(shipMid - 20 * s, shipMid), EndPoint = new Point(shipMid - 7 * s, shipMid), Stroke = Brush(HudColor.Orange), StrokeThickness = 2.5 * s });
            horizonShip.Add(new Line { StartPoint = new Point(shipMid + 7 * s, shipMid), EndPoint = new Point(shipMid + 20 * s, shipMid), Stroke = Brush(HudColor.Orange), StrokeThickness = 2.5 * s });
            foreach (var wing in horizonShip) rollCanvas.Children.Add(wing);
            var shipDot = new Ellipse { Width = 5 * s, Height = 5 * s, Fill = Brush(HudColor.Orange) };
            Canvas.SetLeft(shipDot, shipMid - 2.5 * s); Canvas.SetTop(shipDot, shipMid - 2.5 * s);
            rollCanvas.Children.Add(shipDot);
            horizonShip.Add(shipDot);
            // Roll speed: small, inside the bottom of the disc.
            rollRateText = Label(9 * s, Brush(255, 225, 230, 235), FontWeight.Bold);
            rollRateBox = new Border
            {
                Child = rollRateText, Background = Brush(255, 10, 12, 16), CornerRadius = new CornerRadius(3 * s),
                Padding = new Thickness(4 * s, 0, 4 * s, 1 * s), IsVisible = false, IsHitTestVisible = false,
            };
            Canvas.SetTop(rollRateBox, (HorizonSize - 11) * s);   // (on the bottom of the rim)
            rollCanvas.Children.Add(rollRateBox);
            rollText = Label(12 * s, White, FontWeight.Bold);
            rollText.HorizontalAlignment = HorizontalAlignment.Center;
            rollBox = Box(s);
            rollBox.Padding = new Thickness(6 * s, 6 * s, 6 * s, 4 * s);
            rollBox.Height = BottomPanelHeight * s;   // (the same height as the keys panel beside it)
            rollText.TextAlignment = TextAlignment.Center;
            pitchText = Label(12 * s, White, FontWeight.Bold);
            pitchText.HorizontalAlignment = HorizontalAlignment.Center;
            // (Room for the widest reading each can show, "R 180° R" and "P 90° DN": the panel keeps one size as
            // the ship rolls, so it and the panels joined to it don't shuffle about.)
            rollText.Width = 64 * s;
            pitchText.Width = 68 * s;
            pitchText.TextAlignment = TextAlignment.Center;
            rollCanvasBox = rollCanvas;
            pitchShown = double.NaN;
            horizonRollShown = horizonPitchShown = double.NaN;
            rollBox.Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, Spacing = 2 * s,
                Children = { rollCanvas, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 * s, HorizontalAlignment = HorizontalAlignment.Center, Children = { rollText, pitchText } } },
            };
            rollBox.HorizontalAlignment = HorizontalAlignment.Right;
            rollBox.VerticalAlignment = VerticalAlignment.Bottom;
            rollBox.IsVisible = false;
            AddFade(rollBox, 220);
            rollShown = double.NaN;
            Grid.SetRowSpan(rollBox, 3);

            // ---- gravity: a dial with an arrow (which way it pulls, seen from behind the ship: straight down when
            // you're level over a planet), and its strength beside it
            double gravDial = 46 * s;
            var gravCanvas = new Canvas { Width = gravDial, Height = gravDial, VerticalAlignment = VerticalAlignment.Center };
            gravCanvas.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = gravDial, Height = gravDial, Fill = Brush(255, 16, 22, 28), Stroke = Brush(255, 90, 104, 118), StrokeThickness = 1.5 * s,
            });
            // Three arrows from the dial's middle: natural (green) and artificial (orange) gravity, thin, and the
            // two together (white, thicker, on top). The thin ones only show when there's both kinds.
            Avalonia.Controls.Shapes.Path GravArrow(string shape, IBrush ink)
            {
                var arrow = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(shape), Fill = ink, IsVisible = false,
                    RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute),
                };
                Canvas.SetLeft(arrow, gravDial / 2); Canvas.SetTop(arrow, gravDial / 2);
                return arrow;
            }
            const string gravThin = "M -1.3,0 L 1.3,0 L 1.3,14 L 4.6,14 L 0,21 L -4.6,14 L -1.3,14 Z";
            gravArrowN = GravArrow(gravThin, Brush(255, 120, 220, 140));
            gravArrowA = GravArrow(gravThin, Brush(255, 255, 170, 70));
            gravArrow = GravArrow("M -2.2,0 L 2.2,0 L 2.2,13 L 6.5,13 L 0,21 L -6.5,13 L -2.2,13 Z", White);
            gravArrowKeyN = gravArrowKeyA = null;
            gravDot = new Avalonia.Controls.Shapes.Ellipse { Width = 7 * s, Height = 7 * s, Fill = White, IsVisible = false };
            Canvas.SetLeft(gravDot, gravDial / 2 - 3.5 * s); Canvas.SetTop(gravDot, gravDial / 2 - 3.5 * s);
            gravCanvas.Children.Add(gravArrowN);
            gravCanvas.Children.Add(gravArrowA);
            gravCanvas.Children.Add(gravArrow);
            gravCanvas.Children.Add(gravDot);
            gravArrowFor = null;
            var gravTitle = Label(9 * s, Brush(255, 160, 170, 180), FontWeight.Bold);
            gravTitle.Text = "GRAVITY";
            gravValue = Label(18 * s, White, FontWeight.Bold);
            gravSplit = Label(10 * s, Brush(255, 185, 193, 201), FontWeight.Bold);
            // (With both kinds: each one's strength in its arrow's colour.)
            gravTextN = Label(10 * s, Brush(255, 120, 220, 140), FontWeight.Bold);
            gravTextA = Label(10 * s, Brush(255, 255, 170, 70), FontWeight.Bold);
            gravPair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 * s, IsVisible = false, Children = { gravTextN, gravTextA } };
            gravBox = Box(s);
            gravBox.Padding = new Thickness(8 * s, 6 * s, 10 * s, 6 * s);
            gravBox.Height = BottomPanelHeight * s;   // (the same height as the attitude and keys panels it sits beside)
            gravBox.Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8 * s,
                Children = { gravCanvas, new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 62 * s, Children = { gravTitle, gravValue, gravSplit, gravPair } } },
            };
            gravBox.HorizontalAlignment = HorizontalAlignment.Right;
            gravBox.VerticalAlignment = VerticalAlignment.Bottom;
            gravBox.IsVisible = false;
            AddFade(gravBox, 220);
            Grid.SetRowSpan(gravBox, 3);

            // ---- ship analyser: a panel on the right, middle of the screen
            analyserGrid = new Grid();
            analyserMapCanvas = new Canvas { Width = AnalyserMapW * s, Height = AnalyserMapH * s, ClipToBounds = true };
            analyserMapKey = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = AnalyserMapW * s, Margin = new Thickness(8 * s, 0, 8 * s, 6 * s), IsVisible = false };
            analyserMapBox = new Border
            {
                Child = new StackPanel { Children = { analyserMapCanvas, analyserMapKey } }, Background = Brush(140, 5, 14, 22), CornerRadius = new CornerRadius(4 * s),
                Margin = new Thickness(0, 4 * s, 0, 0), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left,
            };
            analyserMapFor = null;
            // Race graphs page: the two results graphs, one above the other.
            anaSpeedTitle = Label(13 * s, Grey); anaSpeedTitle.TextWrapping = TextWrapping.Wrap;
            anaSpeedCanvas = new Canvas { Width = GraphW * s, Height = GraphH * s, ClipToBounds = true };
            anaSpeedLegend = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = GraphW * s };
            anaGapTitle = Label(13 * s, Grey); anaGapTitle.TextWrapping = TextWrapping.Wrap;
            anaGapCanvas = new Canvas { Width = GraphW * s, Height = GraphH * s, ClipToBounds = true };
            anaGapLegend = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = GraphW * s };
            anaGapBlock = new StackPanel { Spacing = 4 * s, Children = { GraphHead(anaGapTitle, s), anaGapCanvas, anaGapLegend } };
            analyserGraphs = new StackPanel
            {
                Spacing = 14 * s, Margin = new Thickness(0, 4 * s, 0, 0), IsVisible = false,
                Children = { new StackPanel { Spacing = 4 * s, Children = { GraphHead(anaSpeedTitle, s), anaSpeedCanvas, anaSpeedLegend } }, anaGapBlock },
            };
            analyserGraphsFor = null;
            // Ship card tiles and lap-by-lap bars.
            analyserTiles = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 560 * s, IsVisible = false };
            analyserBars = new StackPanel { Spacing = 6 * s, IsVisible = false, Margin = new Thickness(0, 2 * s, 0, 4 * s) };
            analyserTilesFor = analyserBarsFor = null;
            analyserTitle = Label(17 * s, White, FontWeight.Bold);
            analyserHint = Label(13 * s, Grey);
            analyserHint.TextWrapping = TextWrapping.Wrap;
            analyserBox = Box(s);
            // (Shrinks to fit the screen when a ship has a lot of rows.)
            analyserBox.Child = new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                Child = new StackPanel { Spacing = 6 * s, Children = { analyserTitle, analyserTiles, analyserBars, analyserGrid, analyserGraphs, analyserMapBox, analyserHint } },
            };
            analyserBox.HorizontalAlignment = HorizontalAlignment.Right;
            analyserBox.VerticalAlignment = VerticalAlignment.Center;
            analyserBox.Margin = new Thickness(0, 20 * s, 30 * s, 20 * s);
            analyserBox.MaxWidth = 640 * s;
            analyserBox.IsVisible = false;
            Grid.SetRowSpan(analyserBox, 3);
            analyserFor = null;
            root.Children.Add(analyserBox);

            // ---- race spotlight intro / outro: a big title card in the middle
            coverKicker = Label(15 * s, Brush(HudColor.Cyan), FontWeight.Bold);
            coverKicker.LetterSpacing = 3 * s;
            coverTitle = Label(60 * s, White, FontWeight.Bold);
            coverSubtitle = Label(24 * s, White);
            coverLines = new StackPanel { Spacing = 4 * s, Margin = new Thickness(0, 10 * s, 0, 0) };
            foreach (var t in new[] { coverKicker, coverTitle, coverSubtitle }) t.HorizontalAlignment = HorizontalAlignment.Center;
            coverBox = Box(s);
            coverBox.Padding = new Thickness(60 * s, 28 * s, 60 * s, 30 * s);
            coverBox.Child = new StackPanel
            {
                Spacing = 2 * s,
                Children =
                {
                    coverKicker, coverTitle,
                    new Border { Height = 2 * s, Width = 240 * s, Background = Brush(HudColor.Cyan), Margin = new Thickness(0, 6 * s, 0, 8 * s), HorizontalAlignment = HorizontalAlignment.Center },
                    coverSubtitle, coverLines,
                },
            };
            coverBox.HorizontalAlignment = HorizontalAlignment.Center;
            // At the top of the screen, so the ship being shown stays clear in the middle.
            coverBox.VerticalAlignment = VerticalAlignment.Top;
            coverBox.Margin = new Thickness(0, 90 * s, 0, 0);
            coverBox.MaxWidth = 1400 * s;
            coverBox.IsVisible = false;
            Grid.SetRowSpan(coverBox, 3);
            coverFor = null;
            root.Children.Add(coverBox);

            // ---- live timing strip: lap, race clock and speed along the bottom while a cinematic films the race
            filmLap = Label(16 * s, Brush(HudColor.Cyan), FontWeight.Bold);
            filmLap.LetterSpacing = 2 * s;
            filmClock = Label(30 * s, White, FontWeight.Bold);
            filmSpeed = Label(22 * s, White, FontWeight.Bold);
            foreach (var t in new[] { filmLap, filmClock, filmSpeed }) t.VerticalAlignment = VerticalAlignment.Center;
            filmBox = Box(s);
            filmBox.Padding = new Thickness(26 * s, 8 * s, 26 * s, 10 * s);
            filmBox.Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 30 * s,
                Children = { filmLap, filmClock, filmSpeed },
            };
            filmBox.HorizontalAlignment = HorizontalAlignment.Center;
            filmBox.VerticalAlignment = VerticalAlignment.Bottom;
            filmBox.Margin = new Thickness(0, 0, 0, 70 * s);
            filmBox.IsVisible = false;
            filmBox.ZIndex = 60;
            Grid.SetRowSpan(filmBox, 3);
            root.Children.Add(filmBox);

            // ---- cinematic debug: the timeline along the bottom of the screen
            cineTitle = Label(15 * s, White, FontWeight.Bold);
            cineNow = Label(14 * s, Grey);
            cineShots = new Grid { Height = 34 * s, Width = CineDebugWidth * s };
            cineEffects = new Canvas { Height = 20 * s, Width = CineDebugWidth * s, Margin = new Thickness(0, 3 * s, 0, 0), ClipToBounds = true };
            cineHead = new Border { Width = 2 * s, Background = White, HorizontalAlignment = HorizontalAlignment.Left };
            cineBox = Box(s);
            cineBox.Child = new StackPanel
            {
                Spacing = 5 * s,
                Children =
                {
                    cineTitle,
                    new Grid { Width = CineDebugWidth * s, Children = { new StackPanel { Children = { cineShots, cineEffects } }, cineHead } },
                    cineNow,
                },
            };
            cineBox.HorizontalAlignment = HorizontalAlignment.Center;
            cineBox.VerticalAlignment = VerticalAlignment.Bottom;
            cineBox.Margin = new Thickness(0, 0, 0, 40 * s);
            cineBox.IsVisible = false;
            Grid.SetRowSpan(cineBox, 3);
            cineDebugFor = null;
            root.Children.Add(cineBox);
            root.Children.Add(speedoBox);
            root.Children.Add(keysBox);
            root.Children.Add(rollBox);
            root.Children.Add(gravBox);
            root.Children.Add(centreBox);
            root.Children.Add(cpFlashBox);
            root.Children.Add(countdownText);

            // ---- cinematics: letterbox bars and fade to black, over everything else of ours
            barsGrid = new Grid { IsHitTestVisible = false, IsVisible = false };
            barsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(0, GridUnitType.Star)));
            barsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            barsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(0, GridUnitType.Star)));
            var barTop = new Border { Background = Brushes.Black };
            var barBottom = new Border { Background = Brushes.Black };
            Grid.SetRow(barBottom, 2);
            barsGrid.Children.Add(barTop);
            barsGrid.Children.Add(barBottom);
            Grid.SetRowSpan(barsGrid, 3);
            fadeRect = new Border { Background = Brushes.Black, IsHitTestVisible = false, IsVisible = false, Opacity = 0 };
            Grid.SetRowSpan(fadeRect, 3);
            root.Children.Add(barsGrid);
            root.Children.Add(fadeRect);
            markerPool.Clear();
            vectorMark = null;
            graphFor = null;

            lastSignature = null;
            if (oldParent != null) Attach(oldParent);
        }

        /// <summary>
        /// Moves the panels between their usual places (timer top centre with the best times beside it, minimap and
        /// lap list on the left, splits and stats on the right) and the corner layout: timer with the lap list under
        /// it in one top corner; minimap with best race and best lap stacked beside it, and the splits under, in the other.
        /// </summary>
        private const double CornerWidth = 380;   // the corner column, before HUD scale
        private const double KeyCell = 27;              // one flight key (before HUD scale)
        private const double BottomPanelHeight = 97;   // the attitude and keys panels, bottom right (before HUD scale)
        private const double KeysPanelWidth = 95;      // the keys panel: three keys, its padding and border
        private bool placedSwap;
        private StackPanel statsStack;   // (side layout) the race stats with the results graphs under them

        private void PlaceTimer(bool corner, bool swap, double s)
        {
            string key = corner + "|" + swap;
            placedSwap = swap;
            if (placedKey == key) return;
            placedKey = key;
            var minimap = (Control)minimapBox;
            foreach (Control c in new Control[] { timerColumn, bestCard, splitsBox, statsBox, graphsBox, lapsBox, minimap, resultsBox })
                (c.Parent as Panel)?.Children.Remove(c);
            // The holders too: they're put back in their new places below (a control can only have one parent).
            foreach (Panel holder in new Panel[] { mapRow, bestStack, splitsRow, statsStack })
            {
                if (holder == null) continue;
                (holder.Parent as Panel)?.Children.Remove(holder);
                holder.Children.Clear();
            }

            if (corner)
            {
                // Timer corner: the timer with the lap list under it. The other corner: the minimap with best race
                // and best lap stacked beside it (towards the middle), and the splits under both.
                var edge = swap ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                // (The results graphs go under the race stats, so they never cover each other.)
                foreach (Control c in new Control[] { timerColumn, lapsBox, statsBox, graphsBox })
                {
                    c.HorizontalAlignment = edge;
                    c.VerticalAlignment = VerticalAlignment.Top;
                    c.Margin = new Thickness(0);
                    cornerColumn.Children.Add(c);
                }
                timerBox.HorizontalAlignment = HorizontalAlignment.Stretch;
                timerColumn.Width = CornerWidth * s;
                statsBox.MinWidth = CornerWidth * s;

                // (The best times card stands beside the minimap, as tall as it: see Update.)
                foreach (var box in new[] { bestRaceBox, bestLapBox })
                    if (box.Child is StackPanel st)
                    {
                        st.Spacing = 0;
                        foreach (var tb in st.Children) tb.HorizontalAlignment = HorizontalAlignment.Center;
                    }
                bestCard.Margin = new Thickness(0);
                bestCard.HorizontalAlignment = HorizontalAlignment.Stretch;
                bestCard.VerticalAlignment = VerticalAlignment.Center;   // (level with the middle of the minimap beside it)
                bestLapValue.FontSize = bestRaceValue.FontSize = 20 * s;

                mapRow ??= new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                mapRow.Spacing = 5 * s;
                mapRow.HorizontalAlignment = swap ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                if (swap) { mapRow.Children.Add(bestCard); mapRow.Children.Add(minimap); }
                else { mapRow.Children.Add(minimap); mapRow.Children.Add(bestCard); }

                splitsBox.HorizontalAlignment = HorizontalAlignment.Stretch;
                splitsBox.VerticalAlignment = VerticalAlignment.Top;
                splitsBox.Margin = new Thickness(0);
                splitsBox.MinWidth = 0;
                // The results go beside the splits (towards the middle), not under them, so the column stays clear of
                // the game's own panels in the bottom corners.
                splitsRow ??= new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                splitsRow.Spacing = 10 * s;
                splitsRow.HorizontalAlignment = swap ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                resultsBox.VerticalAlignment = VerticalAlignment.Top;
                if (swap) { splitsRow.Children.Add(resultsBox); splitsRow.Children.Add(splitsBox); }
                else { splitsRow.Children.Add(splitsBox); splitsRow.Children.Add(resultsBox); }
                splitsBox.HorizontalAlignment = HorizontalAlignment.Left;
                leftColumn.Children.Insert(0, mapRow);
                leftColumn.Children.Insert(1, splitsRow);
                // Room left at the bottom for the game's own panels (toolbar, ship status): if the columns are taller
                // than what's left, they're scaled down to fit.
                leftFit.Margin = cornerFit.Margin = new Thickness(30 * s, 30 * s, 30 * s, 260 * s);
            }
            else
            {
                timerBox.HorizontalAlignment = HorizontalAlignment.Center;
                timerColumn.HorizontalAlignment = HorizontalAlignment.Stretch;
                timerColumn.Width = double.NaN;
                splitsBox.MinWidth = 220 * s;
                statsBox.MinWidth = 0;
                bestLapValue.FontSize = bestRaceValue.FontSize = 20 * s;
                foreach (var box in new[] { bestLapBox, bestRaceBox })
                    if (box.Child is StackPanel st)
                    {
                        st.Spacing = 2 * s;
                        foreach (var tb in st.Children) tb.HorizontalAlignment = HorizontalAlignment.Left;
                    }
                // The best times card to the right of the timer.
                bestCard.Height = double.NaN;
                bestCard.Margin = new Thickness(10 * s, 0, 0, 0);
                bestCard.HorizontalAlignment = HorizontalAlignment.Left;
                bestCard.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(timerColumn, 1);
                Grid.SetColumn(bestCard, 2);
                topRow.Children.Add(timerColumn);
                topRow.Children.Add(bestCard);
                lapsBox.HorizontalAlignment = swap ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                lapsBox.Margin = new Thickness(0);
                leftColumn.Children.Insert(0, minimap);
                leftColumn.Children.Insert(1, lapsBox);
                leftColumn.Children.Add(resultsBox);
                resultsBox.IsVisible = true;
                leftFit.Margin = cornerFit.Margin = new Thickness(30 * s, 30 * s, 30 * s, 30 * s);
                splitsBox.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(splitsBox, 1);
                Grid.SetRowSpan(splitsBox, 2);
                // The race stats with the results graphs under them, one stack (so they never cover each other).
                statsStack ??= new StackPanel();
                statsStack.Spacing = 10 * s;
                statsStack.VerticalAlignment = VerticalAlignment.Top;
                statsBox.VerticalAlignment = graphsBox.VerticalAlignment = VerticalAlignment.Top;
                statsBox.Margin = graphsBox.Margin = new Thickness(0);
                statsStack.Children.Add(statsBox);
                statsStack.Children.Add(graphsBox);
                Grid.SetRow(statsStack, 0);
                Grid.SetRowSpan(statsStack, 3);
                int at = root.Children.IndexOf(leftFit) + 1;
                root.Children.Insert(at, splitsBox);
                root.Children.Insert(at + 1, statsStack);
            }
        }

        // ---------------------------------------------------------------- minimap

        private Control MakeMinimap(double s)
        {
            minimapCanvas = new Canvas { ClipToBounds = true };
            // Route and lines live in their own layer that's scaled and moved for zoom / follow; dots, labels,
            // you and the ghosts are placed on top at their transformed positions (so they keep their size).
            mmContent = new Canvas();
            // (A plain line, not dashed: every dash is a separate piece for the game's UI drawing buffer, which is small.)
            mmRoute = new Path { Stroke = new SolidColorBrush(Color.FromArgb(150, 235, 235, 245)), StrokeThickness = 1.5 * s };
            mmLine = new Path { StrokeThickness = 2.5 * s, StrokeJoin = PenLineJoin.Round };
            mmLine2 = new Path { StrokeThickness = 2.5 * s, StrokeJoin = PenLineJoin.Round, Stroke = Brush(HudColor.Orange) };
            mmSegs = new Canvas();
            mmSectors = new Canvas();
            mmLineUnder = new Path { StrokeThickness = 4.5 * s, StrokeJoin = PenLineJoin.Bevel, Stroke = Brush(255, 8, 10, 14), IsVisible = false };
            mmContent.Children.Add(mmSectors);
            mmContent.Children.Add(mmRoute);
            mmContent.Children.Add(mmLineUnder);
            mmContent.Children.Add(mmSegs);
            mmContent.Children.Add(mmLine);
            mmContent.Children.Add(mmLine2);
            minimapCanvas.Children.Add(mmContent);
            // A faint line from you to the next checkpoint, and a pulsing ring round it.
            mmToNext = new Line { Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 160, 40)), StrokeThickness = 1.5 * s, StrokeDashArray = new AvaloniaList<double> { 3, 3 }, ZIndex = 30, IsVisible = false };
            mmRing = new Ellipse { Stroke = Brush(HudColor.Orange), StrokeThickness = 2 * s, ZIndex = 35, IsVisible = false,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
            minimapCanvas.Children.Add(mmToNext);
            minimapCanvas.Children.Add(mmRing);
            // Arrow pointing up, centred in a 16 x 16 box so it turns about its middle.
            mmPlayer = new Polygon
            {
                Points = new AvaloniaList<Point> { new Point(10 * s, 0.5 * s), new Point(17.5 * s, 19 * s), new Point(10 * s, 14 * s), new Point(2.5 * s, 19 * s) },
                Width = 20 * s, Height = 20 * s,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                // (Bigger, bright, with a dark edge: easy to find on the line.)
                Fill = Brush(255, 110, 220, 255), Stroke = Brush(255, 8, 10, 14), StrokeThickness = 1.5 * s,
                ZIndex = 100,
            };
            minimapCanvas.Children.Add(mmPlayer);
            mmPoints.Clear();
            mmLabels.Clear();
            mmGhosts.Clear();
            mmCrashes.Clear();
            mmSectorBands.Clear();
            mmSectorTagPool.Clear();
            mmFlags.Clear();
            mmFadeRings.Clear();
            mmDrawn = mmLabelsSig = null;
            mmClipSize = -1;
            mmRingScale = null;
            // The round background is an ellipse drawn in the canvas itself (a rounded border only makes a
            // rounded square), and the canvas is clipped to the same circle.
            mmBack = new Ellipse { ZIndex = -10 };
            minimapCanvas.Children.Add(mmBack);
            // The frame, over everything: a dark rim, a thin light line just inside it, and a tick at the top (the way
            // the map's up points: ahead of you with heading up on).
            mmRim = new Ellipse { Stroke = Brush(255, 10, 12, 16), StrokeThickness = 5 * s, ZIndex = 200, IsHitTestVisible = false };
            mmRimLine = new Ellipse { Stroke = BorderBrush, StrokeThickness = 1 * s, ZIndex = 201, IsHitTestVisible = false };
            mmTick = new Polygon { Fill = Brush(255, 235, 238, 240), ZIndex = 202, IsHitTestVisible = false };
            minimapCanvas.Children.Add(mmRim);
            minimapCanvas.Children.Add(mmRimLine);
            minimapCanvas.Children.Add(mmTick);
            // Zoomed in, the track fades out towards the rim: three rings of the map's own background, each darker.
            for (int ring = 0; ring < 3; ring++)
            {
                var fadeRing = new Ellipse { ZIndex = 15, IsHitTestVisible = false, IsVisible = false };
                mmFadeRings.Add(fadeRing);
                minimapCanvas.Children.Add(fadeRing);
            }
            // Under the map: the name of the sector you're in (the names aren't on the map itself any more).
            mmSectorName = new TextBlock { FontSize = 12 * s, FontWeight = FontWeight.Bold };
            mmSectorTag = new Border
            {
                Child = mmSectorName, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3 * s),
                Padding = new Thickness(8 * s, 1 * s, 8 * s, 2 * s), HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false,
                Margin = new Thickness(0, -11 * s, 0, 0),   // (sits on the bottom of the rim: part of the map, not a label under it)
            };
            minimapBox = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false,
                Child = new StackPanel { Children = { minimapCanvas, mmSectorTag } },
            };
            AddFade(minimapBox, 220);

            // The ring pulses on its own clock (the HUD only redraws when something changes).
            mmPulseTimer?.Stop();
            mmPulseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, __) =>
            {
                if (mmRing == null || !mmRing.IsVisible) return;
                // Grows by scaling (no layout pass), placed by the minimap update.
                double phase = (Environment.TickCount64 % 1200) / 1200.0;
                if (mmRingScale == null) mmRing.RenderTransform = mmRingScale = new ScaleTransform();
                mmRingScale.ScaleX = mmRingScale.ScaleY = 1 + 0.9 * phase;
                mmRing.Opacity = 1 - phase;
            });
            mmPulseTimer.Start();
            return minimapBox;
        }

        private void UpdateMinimap(HudModel.MinimapPanel mm, double s, IBrush fill)
        {
            Appear(minimapBox, mm != null);
            if (mm == null) { mmRing.IsVisible = false; mmUpdatedAt = 0; return; }
            // At most ~30 updates a second (plenty for a small map); a skipped one is caught up on the next frame.
            long nowMs = Environment.TickCount64;
            if (mmDrawn != null && mmDrawn.StartsWith(mm.StaticSig + "|") && nowMs - mmUpdatedAt < 33) { lastSignature = null; return; }
            mmUpdatedAt = nowMs;
            double size = mm.Size * s;
            minimapBox.Width = size;
            minimapCanvas.Width = size;
            minimapCanvas.Height = size;
            mmContent.Width = size;
            mmContent.Height = size;
            // Round: a filled circle behind everything, and the contents cut to that circle.
            mmBack.Width = size; mmBack.Height = size;
            mmBack.Fill = Brush(240, 12, 15, 18);   // (nearly solid whatever the panels' darkness: the track has to read over bright ground)
            Canvas.SetLeft(mmBack, 0); Canvas.SetTop(mmBack, 0);
            if (size != mmClipSize)
            {
                mmClipSize = size;
                minimapCanvas.Clip = new EllipseGeometry(new Rect(0, 0, size, size));
                // The frame: the rim along the edge, the light line just inside it, the tick at the top.
                mmRim.Width = mmRim.Height = size;
                Canvas.SetLeft(mmRim, 0); Canvas.SetTop(mmRim, 0);
                mmRimLine.Width = mmRimLine.Height = size - 10 * s;
                Canvas.SetLeft(mmRimLine, 5 * s); Canvas.SetTop(mmRimLine, 5 * s);
                mmTick.Points = new AvaloniaList<Point> { new Point(size / 2 - 5 * s, 0), new Point(size / 2 + 5 * s, 0), new Point(size / 2, 8 * s) };
                for (int ring = 0; ring < mmFadeRings.Count; ring++)
                {
                    // (From just inside the light line inwards: the outermost ring is the darkest.)
                    double ringWide = 5 * s, ringInset = 5.5 * s + ring * ringWide;
                    mmFadeRings[ring].StrokeThickness = ringWide + 0.5;
                    mmFadeRings[ring].Width = mmFadeRings[ring].Height = Math.Max(1, size - 2 * ringInset);
                    Canvas.SetLeft(mmFadeRings[ring], ringInset); Canvas.SetTop(mmFadeRings[ring], ringInset);
                }
            }
            // Everything drawn on the map stays inside its circle (the clip alone isn't always applied), clear of the rim.
            double radius = size / 2 - 6 * s;
            bool InCircle(double x, double y, double margin)
            {
                double ddx = x - size / 2, ddy = y - size / 2;
                return ddx * ddx + ddy * ddy <= (radius - margin) * (radius - margin);
            }
            bool BoxInCircle(double cx, double cy, double w, double h) =>
                InCircle(cx - w / 2, cy - h / 2, 0) && InCircle(cx + w / 2, cy - h / 2, 0)
                && InCircle(cx - w / 2, cy + h / 2, 0) && InCircle(cx + w / 2, cy + h / 2, 0);

            // View: the whole course, or zoomed in round you while you're moving.
            double span = Math.Max(0.02, mm.ViewSpan);
            // Heading up (Settings > Minimap): the whole map turns about its middle so your ship always points up
            // (in 2 degree steps, so the lines aren't redrawn for every wobble).
            double mapTurnDeg = mm.HeadingUp && mm.PlayerX.HasValue ? -Math.Round(mm.PlayerAngle / 2) * 2 : 0;
            double mapTurnCos = Math.Cos(mapTurnDeg * Math.PI / 180), mapTurnSin = Math.Sin(mapTurnDeg * Math.PI / 180);
            Point T(double x, double y)
            {
                double mapX = (x - mm.ViewX) / span * size, mapY = (y - mm.ViewY) / span * size;
                return new Point(size / 2 + mapX * mapTurnCos - mapY * mapTurnSin, size / 2 + mapX * mapTurnSin + mapY * mapTurnCos);
            }
            // Route and lines are drawn in screen coordinates and cut to the circle here (the game's renderer doesn't
            // apply clips to them reliably). Only redone when the view or the lines change.
            // The Race Map's switches (sectors, checkpoints, labels, route, racing line, ghosts, you) apply here too.
            var opt = MapOptions.Current;
            // The view to the nearest pixel (and half a percent of zoom): smaller moves don't redraw the lines.
            // Racing: the part of the racing line already flown this lap is dimmed, so the bright part is what's ahead.
            int lineDoneTo = 0;
            if (mm.NextIndex >= 0 && mm.PlayerX.HasValue && mm.Line.Count > 8 && mm.LineColours.Count > 0)
            {
                int nearAt = 0; double nearBest = double.MaxValue;
                for (int li = 0; li < mm.Line.Count; li++)
                {
                    double ldx = mm.Line[li].X - mm.PlayerX.Value, ldy = mm.Line[li].Y - mm.PlayerY.Value, ld = ldx * ldx + ldy * ldy;
                    if (ld < nearBest) { nearBest = ld; nearAt = li; }
                }
                lineDoneTo = (nearAt * 40 / mm.Line.Count) * mm.Line.Count / 40;
            }
            string viewSig = $"{mm.StaticSig}|{Math.Round(mm.ViewX / span * size)}|{Math.Round(mm.ViewY / span * size)}|{Math.Round(Math.Log(span) * 200)}|{size:0}|{opt.Sectors}{opt.Route}{opt.RacingLine}|{mapTurnDeg:0}|{lineDoneTo}|{mmResultsUp}";
            bool linesChanged = mmDrawn == null || !mmDrawn.StartsWith(mm.StaticSig + "|");
            long nowTicks = Environment.TickCount64;
            bool viewChanged = viewSig != mmDrawn && (linesChanged || nowTicks - mmLinesAt >= 33);
            if (viewSig != mmDrawn && !viewChanged) lastSignature = null;   // skipped for now: redraw on the next frame
            if (viewChanged)
            {
                mmLinesAt = nowTicks;
                mmDrawn = viewSig;
                double cx0 = size / 2, cy0 = size / 2, rad = size / 2 - 6 * s;

                // The part of the segment a-b inside the circle (false if none).
                bool Clip(Point a, Point b, out Point ca, out Point cb)
                {
                    ca = a; cb = b;
                    // Both ends off the same side of the map: nothing to draw (most of the course when zoomed in).
                    if ((a.X < cx0 - rad && b.X < cx0 - rad) || (a.X > cx0 + rad && b.X > cx0 + rad)
                        || (a.Y < cy0 - rad && b.Y < cy0 - rad) || (a.Y > cy0 + rad && b.Y > cy0 + rad)) return false;
                    double dx = b.X - a.X, dy = b.Y - a.Y, fx = a.X - cx0, fy = a.Y - cy0;
                    double qa = dx * dx + dy * dy, qb = 2 * (fx * dx + fy * dy), qc = fx * fx + fy * fy - rad * rad;
                    if (qa < 1e-9) return qc <= 0;
                    double disc = qb * qb - 4 * qa * qc;
                    if (disc <= 0) return false;
                    double sq = Math.Sqrt(disc), t1 = (-qb - sq) / (2 * qa), t2 = (-qb + sq) / (2 * qa);
                    double lo = Math.Max(0, t1), hi = Math.Min(1, t2);
                    if (lo >= hi) return false;
                    ca = new Point(a.X + dx * lo, a.Y + dy * lo);
                    cb = new Point(a.X + dx * hi, a.Y + dy * hi);
                    return true;
                }

                // A polyline cut to the circle, as one geometry with a figure per piece inside.
                Geometry Cut(List<(double X, double Y)> pts, bool closed)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        bool inFigure = false;
                        int n = pts.Count, segs = closed && n > 2 ? n : n - 1;
                        for (int i = 0; i < segs; i++)
                        {
                            var a = T(pts[i].X, pts[i].Y);
                            var b = T(pts[(i + 1) % n].X, pts[(i + 1) % n].Y);
                            if (!Drawable(a) || !Drawable(b) || !Clip(a, b, out var ca, out var cb)) { if (inFigure) { ctx.EndFigure(false); inFigure = false; } continue; }
                            bool startsInside = Math.Abs(ca.X - a.X) < 1e-6 && Math.Abs(ca.Y - a.Y) < 1e-6;
                            if (!inFigure || !startsInside)
                            {
                                if (inFigure) ctx.EndFigure(false);
                                ctx.BeginFigure(ca, false);
                                inFigure = true;
                            }
                            ctx.LineTo(cb);
                            bool endsInside = Math.Abs(cb.X - b.X) < 1e-6 && Math.Abs(cb.Y - b.Y) < 1e-6;
                            if (!endsInside) { ctx.EndFigure(false); inFigure = false; }
                        }
                        if (inFigure) ctx.EndFigure(false);
                    }
                    return g;
                }

                // Named sectors under everything else: a wide see-through band along the route, and the name.
                mmSectorInfo.Clear();
                var secs = opt.Sectors ? mm.Sectors : new List<(List<(double X, double Y)> Points, string Name, HudColor Colour)>();
                while (mmSectorBands.Count < secs.Count)
                {
                    var band = new Path { StrokeThickness = 6 * s, StrokeJoin = PenLineJoin.Bevel };   // (a thin, faint band: the racing line stays the clear thing)
                    mmSectorBands.Add(band);
                    mmSectors.Children.Add(band);
                }
                for (int k = 0; k < mmSectorBands.Count; k++) mmSectorBands[k].IsVisible = k < secs.Count;
                for (int k = 0; k < secs.Count; k++)
                {
                    var sec = secs[k];
                    mmSectorBands[k].Data = Cut(sec.Points, false);
                    mmSectorBands[k].Stroke = Brush(sec.Colour.WithAlpha(55));
                    // Where its name goes is worked out with the other labels (below), clear of the checkpoints.
                    var pts = sec.Points.ConvertAll(q => T(q.X, q.Y));
                    var (mid, normal) = LabelPlacer.Halfway(pts, new Point(size / 2, size / 2));
                    mmSectorInfo.Add((sec.Name, sec.Colour, mid, normal));
                }

                // (With no racing line to show, e.g. a course just reversed, the straight route joins the gates so the map isn't only dots.)
                // (Never beside the racing line: the two together read as a mess of lines.)
                bool lineShown = opt.RacingLine && mm.Line != null && mm.Line.Count >= 2;
                mmRoute.Data = Cut(!lineShown && (opt.Route || mm.Line == null || mm.Line.Count < 2) ? mm.Route : new List<(double X, double Y)>(), false);
                mmLine.Data = Cut(mm.Line, false);
                mmLineUnder.Data = mmLine.Data;   // (the same shape, drawn wider and dark underneath)
                mmLine2.Data = Cut(mm.Line2, false);

                // Coloured racing line: segments grouped by colour (rounded to a few shades), one path per
                // colour, cut to the circle. A handful of shapes instead of hundreds of separate lines.
                var groups = new Dictionary<uint, List<(Point A, Point B)>>();
                for (int i = 0; i + 1 < mm.Line.Count && i < mm.LineColours.Count; i++)
                {
                    var pa = T(mm.Line[i].X, mm.Line[i].Y); var pb = T(mm.Line[i + 1].X, mm.Line[i + 1].Y);
                    if (!Drawable(pa) || !Drawable(pb) || !Clip(pa, pb, out var a, out var b)) continue;
                    var c = mm.LineColours[i];
                    if (i < lineDoneTo) c = new HudColor((byte)(c.R * 0.62), (byte)(c.G * 0.62), (byte)(c.B * 0.62));   // (already flown this lap)
                    uint key = (uint)(c.R & 0xF0) << 16 | (uint)(c.G & 0xF0) << 8 | (uint)(c.B & 0xF0);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(Point, Point)>();
                    list.Add((a, b));
                }
                int used = 0;
                foreach (var kv in groups)
                {
                    if (used >= mmSegs.Children.Count)
                        mmSegs.Children.Add(new Path { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, StrokeThickness = 2.5 * s });
                    var path = (Path)mmSegs.Children[used++];
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        Point? last = null;
                        foreach (var (a, b) in kv.Value)
                        {
                            if (last == null || Math.Abs(last.Value.X - a.X) > 1e-6 || Math.Abs(last.Value.Y - a.Y) > 1e-6)
                            {
                                if (last != null) ctx.EndFigure(false);
                                ctx.BeginFigure(a, false);
                            }
                            ctx.LineTo(b);
                            last = b;
                        }
                        if (last != null) ctx.EndFigure(false);
                    }
                    path.Data = g;
                    path.Stroke = Brush(255, (byte)(kv.Key >> 16 | 0x08), (byte)((kv.Key >> 8 & 0xFF) | 0x08), (byte)((kv.Key & 0xFF) | 0x08));
                    path.IsVisible = true;
                }
                for (int i = used; i < mmSegs.Children.Count; i++) ((Path)mmSegs.Children[i]).IsVisible = false;
            }
            mmLine.IsVisible = opt.RacingLine && mm.LineColours.Count == 0;
            // (Not while the results are up: the game's UI can only draw so much shape in a frame.)
            mmLineUnder.IsVisible = opt.RacingLine && mm.Line.Count > 1 && !mmResultsUp;
            // Zoomed in: the track fades out towards the rim.
            bool mapZoomed = span < 0.8;
            byte[] fadeAlphas = { 190, 125, 60 };
            for (int ring = 0; ring < mmFadeRings.Count; ring++)
            {
                mmFadeRings[ring].IsVisible = mapZoomed;
                if (mapZoomed) mmFadeRings[ring].Stroke = Brush(fadeAlphas[ring], 12, 15, 18);
            }
            mmSegs.IsVisible = opt.RacingLine;
            mmLine.Stroke = Brush(mm.LineColour.WithAlpha(230));
            mmLine2.IsVisible = opt.RacingLine && mm.Line2.Count > 1;

            // Gates and checkpoints, coloured by state (missed = red).
            while (mmPoints.Count < mm.Points.Count)
            {
                var dot = new Ellipse { Width = 7 * s, Height = 7 * s, ZIndex = 20 };
                mmPoints.Add(dot);
                minimapCanvas.Children.Add(dot);
            }
            for (int i = 0; i < mmPoints.Count; i++)
            {
                var c = (Ellipse)mmPoints[i];
                c.IsVisible = i < mm.Points.Count && opt.Checkpoints;
                if (i >= mm.Points.Count) continue;
                var pt = mm.Points[i];
                double d = (pt.Gate ? 11 : (i == mm.NextIndex ? 10 : 7)) * s;
                c.Width = d; c.Height = d;
                c.Fill = Brush(pt.Colour.WithAlpha(255));
                c.Stroke = pt.Gate || i == mm.NextIndex ? White : null;
                c.StrokeThickness = 1.2 * s;
                var at = T(pt.X, pt.Y);
                // A gate (start / finish): a small chequered square instead of a dot.
                while (mmFlags.Count <= i) mmFlags.Add(null);
                if (pt.Gate && mmFlags[i] == null)
                {
                    var flag = new Grid { Width = 11 * s, Height = 11 * s, ZIndex = 21, IsHitTestVisible = false };
                    flag.Children.Add(new Rectangle { Fill = Brush(255, 10, 12, 16), Stroke = Brush(255, 235, 238, 240), StrokeThickness = 1 * s });
                    flag.Children.Add(new Path
                    {
                        Data = Geometry.Parse("M 0,0 h 1 v 1 h -1 Z M 2,0 h 1 v 1 h -1 Z M 1,1 h 1 v 1 h -1 Z M 0,2 h 1 v 1 h -1 Z M 2,2 h 1 v 1 h -1 Z"),
                        Fill = Brush(255, 235, 238, 240), Stretch = Stretch.Fill, Margin = new Thickness(1.5 * s),
                    });
                    mmFlags[i] = flag;
                    minimapCanvas.Children.Add(flag);
                }
                bool flagHere = pt.Gate && mmFlags[i] != null && c.IsVisible && InCircle(at.X, at.Y, d / 2);
                if (mmFlags[i] != null)
                {
                    mmFlags[i].IsVisible = flagHere;
                    if (flagHere) { Canvas.SetLeft(mmFlags[i], at.X - 5.5 * s); Canvas.SetTop(mmFlags[i], at.Y - 5.5 * s); }
                }
                if (flagHere) { c.IsVisible = false; continue; }
                if (!InCircle(at.X, at.Y, d / 2)) { c.IsVisible = false; continue; }
                Canvas.SetLeft(c, at.X - d / 2);
                Canvas.SetTop(c, at.Y - d / 2);
            }
            for (int fi = mm.Points.Count; fi < mmFlags.Count; fi++) if (mmFlags[fi] != null) mmFlags[fi].IsVisible = false;

            // Labels and sector names are only placed again when the drawn view (or what's shown) changes;
            // otherwise just their colours are kept up to date.
            string labelsSig = $"{mmDrawn}|{opt.Checkpoints}{opt.Labels}{opt.Sectors}|{mm.Points.Count}|{mm.NextIndex}|{mm.LabelsAhead}";
            if (labelsSig == mmLabelsSig)
            {
                for (int i = 0; i < mmLabels.Count && i < mm.Points.Count; i++)
                    if (mmLabels[i].IsVisible) ((TextBlock)mmLabels[i].Child).Foreground = Brush(mm.Points[i].Colour.WithAlpha(255));
            }
            else
            {
            mmLabelsSig = labelsSig;
            // Everything placed so far (the dots), so labels keep clear of it.
            var taken = new List<Rect>();
            foreach (var pt in opt.Checkpoints ? mm.Points : new List<(double X, double Y, HudColor Colour, bool Gate, string Label)>())
            {
                var at = T(pt.X, pt.Y);
                double pad = 10 * s;
                taken.Add(new Rect(at.X - pad / 2, at.Y - pad / 2, pad, pad));
            }

            // A small label by each gate and checkpoint, on the side away from the middle of the map so it
            // doesn't sit on the route.
            while (mmLabels.Count < mm.Points.Count)
            {
                var t = new TextBlock { FontSize = 11 * s, FontWeight = FontWeight.Bold };
                var b = new Border { Child = t, Background = Brush(170, 10, 12, 16), Padding = new Thickness(3 * s, 0, 3 * s, 0), CornerRadius = new CornerRadius(3 * s), ZIndex = 40 };
                mmLabels.Add(b);
                minimapCanvas.Children.Add(b);
            }
            for (int i = 0; i < mmLabels.Count; i++)
            {
                var b = mmLabels[i];
                b.IsVisible = i < mm.Points.Count && opt.Checkpoints && opt.Labels && !string.IsNullOrEmpty(mm.Points[i].Label)
                              && !(mm.Points[i].Gate && mm.Points[i].Label == "S/F");   // (the chequered square says start / finish)
                // (Racing: only the next few are numbered; the rest are just their dots, so numbers don't crowd.)
                if (b.IsVisible && mm.LabelsAhead > 0 && mm.NextIndex >= 0 && mm.Points.Count > mm.LabelsAhead + 2)
                {
                    int stepsOn = ((i - mm.NextIndex) % mm.Points.Count + mm.Points.Count) % mm.Points.Count;
                    if (stepsOn >= mm.LabelsAhead) b.IsVisible = false;
                }
                if (!b.IsVisible) continue;
                var pt = mm.Points[i];
                var t = (TextBlock)b.Child;
                t.Text = pt.Label;
                t.Foreground = Brush(pt.Colour.WithAlpha(255));
                var at = T(pt.X, pt.Y);
                double dx = at.X - size / 2, dy = at.Y - size / 2, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-3) { dx = 1; dy = 0; len = 1; }
                dx /= len; dy /= len;
                double w = (pt.Label.Length * 7 + 6) * s, h = 15 * s;
                // Off the map (zoomed in): no label.
                if (!InCircle(at.X, at.Y, 0)) { b.IsVisible = false; continue; }
                // Outside the point if there's room in the circle, otherwise wherever is free nearby.
                var spot = LabelPlacer.Place(at, new Vector(dx, dy), w, h, taken, r => BoxInCircle(r.Center.X, r.Center.Y, r.Width, r.Height), 3 * s);
                Canvas.SetLeft(b, spot.X);
                Canvas.SetTop(b, spot.Y);
            }

            // Sector names: clear of the checkpoints, their labels and each other; not shown if there's no room.
            int tagsUsed = 0;
            // (Not on the map any more: the sector you're in is named under it.)
            foreach (var info in mmSectorInfo.Take(0))
            {
                double lw = (info.Name.Length * 6.2 + 10) * s, lh = 15 * s;
                if (!InCircle(info.Mid.X, info.Mid.Y, 0)) continue;
                var spot = LabelPlacer.Place(info.Mid, info.Normal, lw, lh, taken, r => BoxInCircle(r.Center.X, r.Center.Y, r.Width, r.Height), 3 * s);
                if (!BoxInCircle(spot.Center.X, spot.Center.Y, spot.Width, spot.Height)) continue;
                taken.Add(spot);
                if (tagsUsed >= mmSectorTagPool.Count)
                {
                    var nt = new Border
                    {
                        Background = Brush(225, 10, 12, 16),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3 * s),
                        Padding = new Thickness(3 * s, 0, 3 * s, 1 * s),
                        ZIndex = 38,
                        Child = new TextBlock { FontSize = 10 * s, FontWeight = FontWeight.Bold },
                    };
                    mmSectorTagPool.Add(nt);
                    minimapCanvas.Children.Add(nt);
                }
                var tag = mmSectorTagPool[tagsUsed++];
                tag.IsVisible = true;
                tag.BorderBrush = Brush(info.Colour.WithAlpha(200));
                var tt = (TextBlock)tag.Child;
                tt.Text = info.Name;
                tt.Foreground = Brush(info.Colour.WithAlpha(255));
                Canvas.SetLeft(tag, spot.X); Canvas.SetTop(tag, spot.Y);
            }
            for (int k = tagsUsed; k < mmSectorTagPool.Count; k++) mmSectorTagPool[k].IsVisible = false;
            }

            // Ghosts, then you on top.
            while (mmGhosts.Count < mm.Ghosts.Count)
            {
                var e = new Ellipse { Width = 10 * s, Height = 10 * s, Stroke = White, StrokeThickness = 1 * s, ZIndex = 50 };
                mmGhosts.Add(e);
                minimapCanvas.Children.Add(e);
            }
            for (int i = 0; i < mmGhosts.Count; i++)
            {
                var e = mmGhosts[i];
                e.IsVisible = i < mm.Ghosts.Count && opt.Ghosts;
                if (i >= mm.Ghosts.Count) continue;
                e.Fill = Brush(mm.Ghosts[i].Colour.WithAlpha(255));
                var at = T(mm.Ghosts[i].X, mm.Ghosts[i].Y);
                if (!InCircle(at.X, at.Y, 5 * s)) { e.IsVisible = false; continue; }
                Canvas.SetLeft(e, at.X - 5 * s);
                Canvas.SetTop(e, at.Y - 5 * s);
            }
            // After the race: red X where you crashed on the lap shown.
            while (mmCrashes.Count < mm.Crashes.Count)
            {
                var x = new TextBlock { Text = "X", FontSize = 13 * s, FontWeight = FontWeight.Bold, Foreground = Brush(HudColor.Red), ZIndex = 49 };
                mmCrashes.Add(x);
                minimapCanvas.Children.Add(x);
            }
            for (int i = 0; i < mmCrashes.Count; i++)
            {
                var x = mmCrashes[i];
                x.IsVisible = i < mm.Crashes.Count;
                if (!x.IsVisible) continue;
                var at = T(mm.Crashes[i].X, mm.Crashes[i].Y);
                if (!InCircle(at.X, at.Y, 5 * s)) { x.IsVisible = false; continue; }
                Canvas.SetLeft(x, at.X - 4.5 * s);
                Canvas.SetTop(x, at.Y - 9 * s);
            }
            mmPlayer.IsVisible = mm.PlayerX.HasValue && opt.You;
            Point? me = null;
            if (mm.PlayerX.HasValue)
            {
                var at = T(mm.PlayerX.Value, mm.PlayerY.Value);
                me = at;
                Canvas.SetLeft(mmPlayer, at.X - 10 * s);
                Canvas.SetTop(mmPlayer, at.Y - 10 * s);
                mmPlayerTurn ??= new RotateTransform();
                if (!ReferenceEquals(mmPlayer.RenderTransform, mmPlayerTurn)) mmPlayer.RenderTransform = mmPlayerTurn;
                mmPlayerTurn.Angle = mm.PlayerAngle + mapTurnDeg;
            }

            // The next checkpoint: a pulsing ring, and a faint line to it from you.
            bool hasNext = mm.NextIndex >= 0 && mm.NextIndex < mm.Points.Count;
            mmRing.IsVisible = hasNext && opt.Checkpoints;
            mmToNext.IsVisible = hasNext && me.HasValue && opt.You && opt.Checkpoints;
            if (hasNext)
            {
                var np = mm.Points[mm.NextIndex];
                mmRingAt = T(np.X, np.Y);
                mmRingBase = 12 * s;
                mmRing.Width = mmRing.Height = mmRingBase;
                Canvas.SetLeft(mmRing, mmRingAt.X - mmRingBase / 2);
                Canvas.SetTop(mmRing, mmRingAt.Y - mmRingBase / 2);
                mmRing.Stroke = Brush(np.Colour.WithAlpha(255));
                // Off the map (zoomed in): no ring, and the line to it stops at the map's edge.
                bool onMap = InCircle(mmRingAt.X, mmRingAt.Y, 12 * s);
                mmRing.IsVisible = onMap && opt.Checkpoints;
                var end = mmRingAt;
                if (!InCircle(end.X, end.Y, 0))
                {
                    double ex = end.X - size / 2, ey = end.Y - size / 2, el = Math.Sqrt(ex * ex + ey * ey);
                    end = new Point(size / 2 + ex / el * radius, size / 2 + ey / el * radius);
                }
                if (me.HasValue) { mmToNext.StartPoint = me.Value; mmToNext.EndPoint = end; }
                // (Only when the checkpoint is off the map: on it, the pulsing ring already shows where to go.)
                if (onMap) mmToNext.IsVisible = false;
            }

            // The named sector you're in (the one whose stretch of the route is nearest), under the map.
            string sectorHere = null;
            HudColor sectorHereColour = HudColor.White;
            if (opt.Sectors && mm.PlayerX.HasValue)
            {
                double nearSector = double.MaxValue;
                foreach (var nearSec in mm.Sectors)
                    foreach (var nearPt in nearSec.Points)
                    {
                        double sdx = nearPt.X - mm.PlayerX.Value, sdy = nearPt.Y - mm.PlayerY.Value, sd = sdx * sdx + sdy * sdy;
                        if (sd < nearSector) { nearSector = sd; sectorHere = nearSec.Name; sectorHereColour = nearSec.Colour; }
                    }
            }
            mmSectorTag.IsVisible = !string.IsNullOrEmpty(sectorHere);
            if (mmSectorTag.IsVisible)
            {
                if (mmSectorName.Text != sectorHere) mmSectorName.Text = sectorHere;
                mmSectorName.Foreground = White;   // (white on the dark tag reads at this size; the sector's colour is its edge)
                mmSectorTag.BorderBrush = Brush(sectorHereColour.WithAlpha(255));
                mmSectorTag.Background = Brush(255, 12, 15, 18);
            }
        }

        // ---------------------------------------------------------------- update

        // ---- race HUD only: hide the game's own HUD screens (Keen.Game2.Client.UI.HUD.*) while it's on

        private bool hideGameHud, keepGyro, gyroTargetBased;
        private Border speedoBox, keysBox, analyserBox, rollBox;
        // Gravity: a small panel of its own beside the attitude panel: an arrow showing which way it pulls on the
        // ship, how strong it is, and the natural / artificial split when gravity generators add theirs.
        private Border gravBox;
        private Avalonia.Controls.Shapes.Path gravArrow;
        private Avalonia.Controls.Shapes.Ellipse gravDot;
        private TextBlock gravValue, gravSplit;
        private string gravArrowFor;
        private Avalonia.Controls.Shapes.Path gravArrowN, gravArrowA;
        private string gravArrowKeyN, gravArrowKeyA;
        private TextBlock gravTextN, gravTextA;
        private StackPanel gravPair;

        /// <summary>
        /// One of the gravity dial's arrows: turned to the pull's direction across the ship (x right, y up, each
        /// -1..1) and as long as its strength against the strongest of the three. Hidden when it pulls mostly along
        /// the ship's nose or tail (nothing to point at). True if it's showing.
        /// </summary>
        private static bool AimGravArrow(Avalonia.Controls.Shapes.Path arrow, ref string key, bool on, double x, double y, double strength, double strongest, double s)
        {
            double across = Math.Sqrt(x * x + y * y);
            on = on && strength >= 0.005 && across >= 0.2;
            if (arrow.IsVisible != on) arrow.IsVisible = on;
            if (!on) return false;
            // (Drawn pointing down the screen from the dial's middle.)
            double turn = Math.Round(Math.Atan2(-x, -y) * 180 / Math.PI / 3) * 3;
            double share = strongest > 0.005 ? Math.Min(1, strength * Math.Min(1, across) / strongest) : 1;
            double length = Math.Round((0.4 + 0.6 * share) * 20) / 20;
            string now = $"{turn:0}|{length:0.00}|{s:0.###}";
            if (now == key) return true;
            key = now;
            var shape = new TransformGroup();
            shape.Children.Add(new ScaleTransform(s, s * length));
            shape.Children.Add(new RotateTransform(turn));
            arrow.RenderTransform = shape;
            return true;
        }

        // The game's HUD measures natural and artificial gravity with a probe each (hidden entities that follow the
        // ship, each feeling one kind): found on its view model here, read by the bridge for the dial's arrows.
        internal static volatile object[] GravityProbes;
        internal static volatile string[] GravityProbeNames;
        internal static volatile System.Reflection.FieldInfo[] GravityProbeFields;
        internal static volatile object GravityProbeVm;
        internal static double GravityNaturalG, GravityArtificialG;
        private long gravityProbesAt;
        private WeakReference<object> gravityProbesVm;
        private string gravityProbesLogged;

        private void FindGravityProbes(object vm)
        {
            long nowMs = Environment.TickCount64;
            // (Looked for again every two seconds, and straight away in a new seat: its HUD has new probes.)
            bool newHud = gravityProbesVm == null || !gravityProbesVm.TryGetTarget(out object lastVm) || !ReferenceEquals(lastVm, vm);
            if (!newHud && nowMs - gravityProbesAt < 2000) return;
            if (newHud) { gravityProbesVm = new WeakReference<object>(vm); GravityProbes = null; }
            gravityProbesAt = nowMs;
            try
            {
                var found = new List<object>();
                var names = new List<string>();
                var plain = new List<System.Reflection.FieldInfo>();
                var entityType = typeof(Keen.VRage.DCS.Components.Entity);
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                for (Type t = vm.GetType(); t != null && t != typeof(object); t = t.BaseType)
                    foreach (var f in t.GetFields(all))
                    {
                        if (f.Name.IndexOf("gravity", StringComparison.OrdinalIgnoreCase) < 0 && f.Name.IndexOf("probe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object v;
                        try { v = f.GetValue(vm); } catch { continue; }
                        if (v == null || v is string) continue;
                        if (entityType.IsInstanceOfType(v)) { found.Add(v); names.Add(f.Name); plain.Add(f); }
                        else if (v is System.Collections.IEnumerable many)
                        {
                            int n = 0;
                            foreach (object item in many)
                                if (item != null && entityType.IsInstanceOfType(item) && n < 4) { found.Add(item); names.Add(f.Name + "[" + n++ + "]"); }
                        }
                    }
                // (When each probe is a plain field, the bridge reads the fields itself every frame: the game swaps
                // the probes for new ones when you change seat, and an old one can't be read.)
                GravityProbeFields = plain.Count == found.Count ? plain.ToArray() : null;
                GravityProbeVm = vm;
                GravityProbes = found.ToArray();
                GravityProbeNames = names.ToArray();
                string seen = found.Count + ": " + string.Join(", ", names);
                if (seen != gravityProbesLogged) { gravityProbesLogged = seen; PluginLog.Info("Gravity probes on the game's movement HUD: " + seen); }
            }
            catch (Exception e) { if (gravityProbesLogged != "failed") { gravityProbesLogged = "failed"; PluginLog.Info("Gravity probes not found: " + e.Message); } }
        }
        private double gravityNatural, gravityArtificial;   // in g, as last read from the game's HUD
        private TextBlock speedoGround, speedoOther, speedoOtherUnit;
        private StackPanel speedoGroundBox;
        private double horizonRollShown = double.NaN, horizonPitchShown = double.NaN;
        private TextBlock rollText, pitchText;
        private Canvas rollCanvasBox;
        private double pitchShown = double.NaN;
        private double rollShown = double.NaN, rollRateShown = double.NaN;
        private Grid analyserGrid;
        private TextBlock analyserTitle, analyserHint;
        private string analyserFor;
        private TextBlock speedoValue, speedoUnit;
        private Canvas dialStatic;
        private Path dialArc;
        private Line dialTip;
        private double dialSize, dialFor = -1;
        private IBrush dialFill;

        private const double DialStart = 135, DialSweep = 270;   // degrees, clockwise from pointing right (y is down)

        private Point DialPoint(double deg, double r)
        {
            double a = deg * Math.PI / 180, c = dialSize / 2;
            return new Point(c + r * Math.Cos(a), c + r * Math.Sin(a));
        }

        private StreamGeometry DialArc(double fromDeg, double toDeg, double r)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(DialPoint(fromDeg, r), false);
                ctx.ArcTo(DialPoint(toDeg, r), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            return g;
        }

        /// <summary>The dial's fixed parts for a top speed: backing disc, track, red zone, ticks and numbers.</summary>
        private void BuildDial(double max, double s, IBrush fill)
        {
            dialFor = max;
            dialFill = fill;
            dialStatic.Children.Clear();
            double c = dialSize / 2, r = c - 14 * s;
            dialDisc = new Ellipse { Width = dialSize, Height = dialSize, Fill = fill, Stroke = BorderBrush, StrokeThickness = 1 };
            dialStatic.Children.Add(dialDisc);
            outlineFor = null;   // (the outline round the dial and its pod is drawn again)
            dialStatic.Children.Add(new Path { Data = DialArc(DialStart, DialStart + DialSweep, r), Stroke = Brush(40, 255, 255, 255), StrokeThickness = 11 * s });
            dialStatic.Children.Add(new Path { Data = DialArc(DialStart + DialSweep * 0.9, DialStart + DialSweep, r), Stroke = Brush(110, 230, 50, 40), StrokeThickness = 11 * s });
            // Numbered ticks every round step (4 to 8 of them), small ticks between.
            double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, max))));
            double step = mag;
            foreach (double m in new[] { 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5 }) if (max / (m * mag) <= 8) { step = m * mag; break; }
            int minors = 4;
            for (int i = 0; i * step / minors <= max + 1e-6; i++)
            {
                double v = i * step / minors;
                double deg = DialStart + DialSweep * v / max;
                bool major = i % minors == 0;
                dialStatic.Children.Add(new Line
                {
                    StartPoint = DialPoint(deg, r - (major ? 22 : 15) * s), EndPoint = DialPoint(deg, r - 8 * s),
                    Stroke = major ? White : Brush(150, 220, 225, 230), StrokeThickness = (major ? 2.5 : 1.2) * s,
                });
                if (!major) continue;
                var t = Label(12 * s, Grey, FontWeight.Bold);
                t.Text = v >= 1000 && v % 1000 == 0 ? $"{v / 1000:0}k" : v.ToString("0.#");
                t.Measure(Size.Infinity);
                var at = DialPoint(deg, r - 36 * s);
                Canvas.SetLeft(t, at.X - t.DesiredSize.Width / 2);
                Canvas.SetTop(t, at.Y - t.DesiredSize.Height / 2);
                dialStatic.Children.Add(t);
            }
        }
        private readonly List<Border> keyCells = new List<Border>();
        private int keysShown = -1;
        private bool loggedGyro;
        private DispatcherTimer hideTimer;
        private readonly List<(WeakReference<Visual> Visual, IDisposable Undo)> hidden = new List<(WeakReference<Visual>, IDisposable)>();

        private void SetHideGameHud(bool on)
        {
            if (on == hideGameHud) return;
            hideGameHud = on;
            if (on)
            {
                if (hideTimer == null) hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, __) => HideGameHudNow());
                hideTimer.Start();
                HideGameHudNow();
            }
            else
            {
                hideTimer?.Stop();
                ClearMiniToolbar();
                foreach (var h in hidden) { try { h.Undo?.Dispose(); } catch { } }
                hidden.Clear();
            }
        }

        /// <summary>Hides the game's HUD screens under the UI root (again, if the game made new ones), not our own.</summary>
        // ---- Race HUD only: the toolbar slot just pressed, shown for a moment. The game's toolbar is still there,
        // only not drawn (an empty clip): the clip is opened up round that one slot, so it's the game's own tile, icon
        // and all, where it always is. It slides up into place inside a frame of ours (edged like the HUD's panels,
        // with the slot's number over it), and slides back down when its time is up.
        private IDisposable peekUndo, peekMoveUndo;
        private int peekShown = -1;
        private bool loggedPeek;
        private Border peekFrame;
        private Canvas peekLayer;
        private TextBlock peekCaption;
        // (Made when first needed, on the UI thread. NOT here: this class is created with the plugin, before the game's
        // UI exists, and making a UI object that early left the game on a black screen.)
        private TranslateTransform peekShift;
        private DispatcherTimer peekTimer;
        private readonly System.Diagnostics.Stopwatch peekClock = new System.Diagnostics.Stopwatch();
        private int peekPhase;      // 1 sliding up, 2 sliding away
        private double peekTravel;

        // ---- Race HUD only: the toolbar bar. Not the game's toolbar (that stays hidden, and still pops a slot up when
        // you press its key): a slim bar of our own where the toolbar's top edge would be, with each slot's icon,
        // small, over the middle of where that slot is. The icons are the pictures the game's own slots are showing.
        private long miniCheckedAt;
        private string miniLogged;
        private Canvas miniLayer;
        private Border miniHot;
        private Path miniShape;
        private string miniShapeFor;
        private TextBlock miniPage, miniStoreText, miniGravityText;
        private double? miniGravity;
        private Border miniSelected;
        private Line miniEndLine;
        private readonly List<Border> miniTicks = new List<Border>();   // quarter marks on the cargo gauge
        private IBrush hudFill;
        private TextBlock miniPageLabel, miniStoreLabel;
        private Border miniStoreTag, miniStoreGroove;
        private readonly List<bool> miniFilled = new List<bool>();
        private long gravityReadAt;
        private string gravityShown, gravityLogged;
        private System.Reflection.PropertyInfo naturalProp, artificialProp;
        private Type gravityVmType;

        /// <summary>A gravity reading from the game's HUD as g: a plain number is taken as g already, a vector as m/s².</summary>
        private static double? GravityAsG(object raw)
        {
            if (raw == null) return null;
            switch (raw)
            {
                case float gf: return float.IsNaN(gf) ? (double?)null : Math.Abs(gf);
                case double gd: return double.IsNaN(gd) ? (double?)null : Math.Abs(gd);
            }
            try
            {
                var vt = raw.GetType();
                object part(string name) => vt.GetField(name)?.GetValue(raw) ?? vt.GetProperty(name)?.GetValue(raw);
                object vx = part("X"), vy = part("Y"), vz = part("Z");
                if (vx != null && vy != null && vz != null)
                {
                    double ax = Convert.ToDouble(vx), ay = Convert.ToDouble(vy), az = Convert.ToDouble(vz);
                    return Math.Sqrt(ax * ax + ay * ay + az * az) / 9.81;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The gravity text for the toolbar bar's right corner: natural and artificial apart when the game's
        /// own HUD says them, else the total.</summary>
        private string GravityText()
        {
            long gravityNowMs = Environment.TickCount64;
            if (gravityShown != null && gravityNowMs - gravityReadAt < 200) return gravityShown;
            gravityReadAt = gravityNowMs;
            double? natural = null, artificial = null;
            try
            {
                object moveVm = MovementHud();
                if (moveVm == null) GravityProbes = null;   // (out of the seat: its probes go with it)
                if (moveVm != null)
                {
                    if (!ReferenceEquals(gravityVmType, moveVm.GetType()))
                    {
                        gravityVmType = moveVm.GetType();
                        naturalProp = gravityVmType.GetProperty("NaturalGravity") ?? gravityVmType.GetProperty("LocalNaturalGravity");
                        artificialProp = gravityVmType.GetProperty("ArtificialGravity") ?? gravityVmType.GetProperty("LocalArtificialGravity");
                    }
                    FindGravityProbes(moveVm);
                    object rawNatural = naturalProp?.GetValue(moveVm), rawArtificial = artificialProp?.GetValue(moveVm);
                    natural = GravityAsG(rawNatural);
                    artificial = GravityAsG(rawArtificial);
                    string gravitySeen = $"natural {rawNatural?.GetType().Name ?? "none"}, artificial {rawArtificial?.GetType().Name ?? "none"}";
                    if (gravitySeen != gravityLogged) { gravityLogged = gravitySeen; PluginLog.Info($"Toolbar bar gravity: {gravitySeen} (now {rawNatural} / {rawArtificial})"); }
                }
            }
            catch { }
            // (One number when there's only a planet's pull, which is nearly always; both, on two lines, when
            // gravity generators add theirs.)
            double pullNatural = natural ?? 0, pullArtificial = artificial ?? 0;
            gravityNatural = pullNatural; gravityArtificial = pullArtificial;
            GravityNaturalG = pullNatural; GravityArtificialG = pullArtificial;
            gravityShown = natural.HasValue || artificial.HasValue
                ? (pullArtificial < 0.005 ? $"{pullNatural:0.00} g" : pullNatural < 0.005 ? $"A {pullArtificial:0.00} g" : $"N {pullNatural:0.00} g\nA {pullArtificial:0.00} g")
                : miniGravity.HasValue ? $"{miniGravity.Value:0.00} g" : "";
            return gravityShown;
        }

        // Gravity in the bar's right wing: one bigger number, or two small lines, centred between the wing's top
        // and the screen's edge.
        private double gravWingLeft, gravWingTop, gravWingTall;
        private void ShowGravity()
        {
            if (miniGravityText == null) return;
            string gravityNow = GravityText();
            bool gravTwoLines = gravityNow.IndexOf('\n') >= 0;
            double gravScale = builtScale;
            if (miniGravityText.Text != gravityNow) miniGravityText.Text = gravityNow;
            double gravSize = (gravTwoLines ? 9 : 13) * gravScale;
            if (Math.Abs(miniGravityText.FontSize - gravSize) > 0.01)
            {
                miniGravityText.FontSize = gravSize;
                miniGravityText.LineHeight = (gravTwoLines ? 11 : 17) * gravScale;
            }
            double gravTall = (gravTwoLines ? 22 : 17) * gravScale;
            Canvas.SetLeft(miniGravityText, gravWingLeft);
            Canvas.SetTop(miniGravityText, gravWingTop + Math.Max(0, (gravWingTall - gravTall) / 2) - (gravTwoLines ? 1 * gravScale : 0));
        }
        private Border miniStoreTrack, miniStoreFill;
        private string storageLogged;

        private Func<int> toolbarSelected;   // the selected slot (0-based, -1 = none), straight from the game's toolbar
        private Func<string> toolbarRead;   // the toolbar's number, straight from the game's toolbar (set once it's been found)
        private string toolbarVmLogged;
        private int toolbarVmLogs;

        /// <summary>Which toolbar is up (1, 2, 3...), from the game's own toolbar screen; null if it can't be told.</summary>
        private string ToolbarNumber(Visual anySlot)
        {
            try
            {
                object barVm = null;
                foreach (var above in anySlot.GetVisualAncestors())
                    if (above is StyledElement styled && styled.DataContext != null && styled.DataContext.GetType().Name == "ToolbarScreenViewModel") { barVm = styled.DataContext; break; }
                if (barVm == null) return null;
                const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                // Which slot is selected: the toolbar's selection (the game's own list selection).
                var selectionProp = barVm.GetType().GetProperty("Selection", any);
                toolbarSelected = selectionProp == null ? null : (Func<int>)(() =>
                {
                    object selection = selectionProp.GetValue(barVm);
                    object selectedAt = selection?.GetType().GetProperty("SelectedIndex")?.GetValue(selection);
                    return selectedAt is int selectedInt ? selectedInt : -1;
                });
                // Every whole number the toolbar keeps (written to the log when one changes, so the right one can be told).
                var numbers = new List<(string Name, long Value, Func<long?> Read)>();
                long? WholeNumber(object o)
                {
                    switch (o)
                    {
                        case int wi: return wi;
                        case byte wb: return wb;
                        case short ws: return ws;
                        case uint wu: return wu;
                        case long wl: return wl;
                        default: return null;
                    }
                }
                for (var barType = barVm.GetType(); barType != null && barType != typeof(object); barType = barType.BaseType)
                {
                    foreach (var barProp in barType.GetProperties(any | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (barProp.GetIndexParameters().Length > 0 || !barProp.CanRead) continue;
                        object barValue = null;
                        try { barValue = barProp.GetValue(barVm); } catch { }
                        long? asNumber = WholeNumber(barValue);
                        // (A selection or a toolbar object: its own index.)
                        if (!asNumber.HasValue && barValue != null && barProp.Name.IndexOf("Toolbar", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { asNumber = WholeNumber((barValue.GetType().GetProperty("SelectedIndex") ?? barValue.GetType().GetProperty("Index"))?.GetValue(barValue)); } catch { }
                            if (asNumber.HasValue)
                            {
                                var outerProp = barProp;
                                numbers.Add((barProp.Name + ".Index", asNumber.Value, () =>
                                {
                                    object inner = outerProp.GetValue(barVm);
                                    return inner == null ? null : WholeNumber((inner.GetType().GetProperty("SelectedIndex") ?? inner.GetType().GetProperty("Index"))?.GetValue(inner));
                                }));
                                continue;
                            }
                        }
                        if (asNumber.HasValue) { var plainProp = barProp; numbers.Add((barProp.Name, asNumber.Value, () => WholeNumber(plainProp.GetValue(barVm)))); }
                    }
                    foreach (var barField in barType.GetFields(any | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        object fieldValue = null;
                        try { fieldValue = barField.GetValue(barVm); } catch { }
                        long? fieldNumber = WholeNumber(fieldValue);
                        if (fieldNumber.HasValue) { var plainField = barField; numbers.Add((barField.Name, fieldNumber.Value, () => WholeNumber(plainField.GetValue(barVm)))); }
                    }
                }
                string numbersSeen = string.Join(", ", numbers.Select(x => x.Name + "=" + x.Value));
                if (numbersSeen != toolbarVmLogged && toolbarVmLogs < 25) { toolbarVmLogs++; toolbarVmLogged = numbersSeen; PluginLog.Info("Toolbar bar: the game's toolbar keeps " + numbersSeen); }
                // The one that says which toolbar: by name.
                bool Named(string name, params string[] words) => words.All(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var pick in new Func<string, bool>[]
                {
                    n => Named(n, "Toolbar", "Index") && !Named(n, "Tile"),
                    n => Named(n, "Selected", "Toolbar") && !Named(n, "Tile"),
                    n => Named(n, "Current", "Toolbar") && !Named(n, "Tile"),
                    n => Named(n, "Toolbar", "Page"),
                    n => Named(n, "Page"),
                })
                    foreach (var (numberName, numberValue, numberRead) in numbers)
                        if (pick(numberName) && numberValue >= 0 && numberValue < 50)
                        {
                            // (Kept, so the number can be read again every frame: switching toolbars shows at once.)
                            bool fromZero = numberName.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0;
                            toolbarRead = () => { long? live = numberRead(); return live.HasValue ? (fromZero ? live.Value + 1 : Math.Max(1, live.Value)).ToString() : null; };
                            return toolbarRead();
                        }
                toolbarRead = null;
                return null;
            }
            catch { return null; }
        }

        /// <summary>How full the ship's storage is (0..1), from the game's cockpit HUD; null if it doesn't say.</summary>
        private double? StorageLevel()
        {
            try
            {
                object hudVm = CockpitHud();
                if (hudVm == null) return null;
                object rawStore = null;
                string from = null;
                foreach (string storeProp in new[] { "InventoryPercentage", "InventoryUsage", "InventoryFullness", "Inventory", "CargoFullness", "Cargo" })
                {
                    rawStore = hudVm.GetType().GetProperty(storeProp)?.GetValue(hudVm);
                    if (rawStore != null) { from = storeProp; break; }
                }
                double? storeLevel = Level(rawStore);
                // (The game's percentages run 0 to 100, like its battery and hydrogen readings: a small one isn't a share already.)
                if (from == "InventoryPercentage" && (rawStore is float || rawStore is double)) storeLevel = Convert.ToDouble(rawStore) / 100;
                if (!storeLevel.HasValue && rawStore != null)
                {
                    // (Not under one of the usual names: any number on it that reads like a fill level.)
                    foreach (var storeMember in rawStore.GetType().GetProperties())
                    {
                        string memberName = storeMember.Name;
                        if (memberName.IndexOf("Full", StringComparison.OrdinalIgnoreCase) < 0 && memberName.IndexOf("Percent", StringComparison.OrdinalIgnoreCase) < 0
                            && memberName.IndexOf("Ratio", StringComparison.OrdinalIgnoreCase) < 0 && memberName.IndexOf("Fill", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object memberValue = null;
                        try { memberValue = storeMember.GetValue(rawStore); } catch { }
                        storeLevel = Level(memberValue);
                        if (storeLevel.HasValue) { from += "." + memberName; break; }
                    }
                }
                string seen = rawStore == null
                    ? "the cockpit HUD has no storage reading (its properties: " + string.Join(", ", hudVm.GetType().GetProperties().Select(x => x.Name).Take(40)) + ")"
                    : storeLevel.HasValue ? $"read from {from}"
                    : $"{from} is a {rawStore.GetType().Name} with no fill level (its properties: {string.Join(", ", rawStore.GetType().GetProperties().Select(x => x.Name).Take(30))})";
                if (seen != storageLogged) { storageLogged = seen; PluginLog.Info("Toolbar bar storage: " + seen); }
                return storeLevel.HasValue ? Math.Clamp(storeLevel.Value, 0, 1) : (double?)null;
            }
            catch { return null; }
        }
        private readonly List<Image> miniIcons = new List<Image>();
        private readonly List<TextBlock> miniNumbers = new List<TextBlock>();
        private readonly List<Line> miniLines = new List<Line>();
        private readonly List<(double Left, double Wide)> miniCells = new List<(double, double)>();
        private double miniBarTop, miniBarTall;

        /// <summary>Lights the bar's cell for the slot whose key was just pressed (the game's own slot rises out of it).</summary>
        private void MiniHighlight(int slot)
        {
            if (miniHot == null) return;
            bool lit = miniLayer != null && miniLayer.IsVisible && slot >= 0 && slot < miniCells.Count;
            miniHot.IsVisible = lit;
            // (The bar's own small icon and number for that slot step aside while the game's slot is up in its place;
            // they're put back on the bar's next look at the slots, asked for straight away.)
            // (First every cell gets its number and icon back: going straight from one slot to another, the one just
            // left must not stay blank.)
            for (int cell = 0; cell < miniCells.Count; cell++)
            {
                if (cell < miniNumbers.Count) miniNumbers[cell].IsVisible = true;
                if (cell < miniIcons.Count) miniIcons[cell].IsVisible = cell < miniFilled.Count && miniFilled[cell];
            }
            if (lit && slot < miniIcons.Count) miniIcons[slot].IsVisible = false;
            if (lit && slot < miniNumbers.Count) miniNumbers[slot].IsVisible = false;
            if (!lit) { miniCheckedAt = 0; return; }
            miniHot.Width = miniCells[slot].Wide;
            miniHot.Height = miniBarTall;
            Canvas.SetLeft(miniHot, miniCells[slot].Left);
            Canvas.SetTop(miniHot, miniBarTop);
        }

        private void ClearMiniToolbar()
        {
            if (miniLayer != null) miniLayer.IsVisible = false;
            if (miniGravityText != null) miniGravityText.IsVisible = false;
            toolbarRead = null;
            toolbarSelected = null;
        }

        private void MiniLog(string what)
        {
            if (what == miniLogged) return;
            miniLogged = what;
            PluginLog.Info("Toolbar bar: " + what);
        }

        /// <summary>
        /// The picture a toolbar slot is showing now (the biggest one in it that's actually shown), or null for an
        /// empty slot. A slot keeps views for other kinds of item switched off inside it: those don't count.
        /// </summary>
        private static IImage SlotIcon(Visual slot)
        {
            IImage found = null;
            double foundArea = 0;
            foreach (var part in slot.GetVisualDescendants())
            {
                IImage picture = null;
                if (part is Image shownImage) picture = shownImage.Source;
                else if (part is Border edged && edged.Background is ImageBrush edgedBrush) picture = edgedBrush.Source as IImage;
                else if (part is Panel held && held.Background is ImageBrush heldBrush) picture = heldBrush.Source as IImage;
                else
                {
                    // (One of the game's own picture controls: whatever it keeps under "Source".)
                    try { picture = part.GetType().GetProperty("Source")?.GetValue(part) as IImage; } catch { }
                }
                if (picture == null || part.Bounds.Width < 2 || part.Bounds.Height < 2) continue;
                // Shown all the way up to the slot (not inside a part of it that's switched off or faded out).
                bool shown = true;
                for (Visual up = part; up != null && !ReferenceEquals(up, slot); up = up.GetVisualParent())
                    if (!up.IsVisible || up.Opacity < 0.05) { shown = false; break; }
                if (!shown) continue;
                double area = part.Bounds.Width * part.Bounds.Height;
                if (area > foundArea) { foundArea = area; found = picture; }
            }
            return found;
        }

        private List<Visual> miniTilesFound;        // the game's toolbar slots, as last found on screen
        private List<TextBlock> miniNumbersFound;   // ... and the short numbers on screen then
        private long miniTilesFoundAt;

        private void UpdateMiniToolbar(bool want, double iconShare)
        {
            // (Not with the terminal open: the whole toolbar shows then.)
            if (!want || !hideGameHud || terminalOpen) { ClearMiniToolbar(); return; }
            long miniNow = Environment.TickCount64;
            if (miniNow - miniCheckedAt < 500) return;   // (looked at again twice a second: what's on the slots changes, e.g. getting into a ship)
            miniCheckedAt = miniNow;
            try
            {
                Visual top = attachedTo == null ? null : (Visual)TopLevel.GetTopLevel(attachedTo) ?? attachedTo;
                if (top == null) return;
                var slotTiles = new List<(Visual Tile, Point At)>();
                var numberTexts = new List<TextBlock>();   // (short numbers on screen: one of them is the toolbar's own number, beside its slots)
                // Finding the toolbar's slots means walking everything on screen, which took tens of milliseconds:
                // twice a second that was a steady stutter. The slots found are kept, and looked for again only when
                // one has gone from the screen, or every eight seconds in case more have appeared.
                bool lookAgain = miniTilesFound == null || miniTilesFound.Count < 2 || miniNow - miniTilesFoundAt > 8000;
                if (!lookAgain)
                    foreach (var kept in miniTilesFound)
                        if (kept.GetVisualRoot() == null || kept.Bounds.Width < 2) { lookAgain = true; break; }
                if (lookAgain)
                {
                    miniTilesFoundAt = miniNow;
                    miniTilesFound = new List<Visual>();
                    miniNumbersFound = new List<TextBlock>();
                    foreach (var v in top.GetVisualDescendants())
                    {
                        if (v is TextBlock shortText && shortText.Text != null && shortText.Text.Length <= 2 && int.TryParse(shortText.Text, out _)) { miniNumbersFound.Add(shortText); continue; }
                        if (v.GetType().Name != "ToolbarTileControl" || v.Bounds.Width < 2) continue;
                        miniTilesFound.Add(v);
                    }
                }
                foreach (var v in miniTilesFound)
                {
                    var tileAt = v.TranslatePoint(new Point(0, 0), root);
                    if (tileAt.HasValue) slotTiles.Add((v, tileAt.Value));
                }
                numberTexts.AddRange(miniNumbersFound);
                // The toolbar's row: the line with the most tiles on it, left to right.
                var slotRow = slotTiles.GroupBy(t => Math.Round(t.At.Y / 8)).OrderByDescending(g => g.Count()).FirstOrDefault()?.OrderBy(t => t.At.X).ToList();
                if (slotRow == null || slotRow.Count < 2) { MiniLog($"no toolbar row found ({slotTiles.Count} slot(s) on screen)"); ClearMiniToolbar(); return; }
                double s = builtScale;
                Visual firstTile = slotRow[0].Tile, lastTile = slotRow[slotRow.Count - 1].Tile;
                double slotSide = Math.Min(firstTile.Bounds.Width, firstTile.Bounds.Height);
                double iconSide = Math.Max(12 * s, slotSide * Math.Clamp(iconShare, 0.3, 0.9));
                // The toolbar's number: the game writes it just left of the first slot, level with the row.
                string pageNumber = null;
                double pageNearest = double.MaxValue;
                foreach (var numberText in numberTexts)
                {
                    var numberAt = numberText.TranslatePoint(new Point(0, 0), root);
                    if (!numberAt.HasValue) continue;
                    double leftOfRow = slotRow[0].At.X - numberAt.Value.X;
                    if (leftOfRow <= 0 || leftOfRow > slotSide * 1.6) continue;
                    if (numberAt.Value.Y < slotRow[0].At.Y - slotSide * 0.5 || numberAt.Value.Y > slotRow[0].At.Y + slotSide * 1.2) continue;
                    if (numberText.GetVisualAncestors().Contains(root)) continue;   // (not one of ours)
                    if (leftOfRow < pageNearest) { pageNearest = leftOfRow; pageNumber = numberText.Text; }
                }
                // (Better: the number the game's toolbar keeps itself, which follows the toolbar being switched.)
                pageNumber = ToolbarNumber(firstTile) ?? pageNumber;

                if (miniLayer == null || !ReferenceEquals(miniLayer.Parent, root))
                {
                    miniLayer = new Canvas { IsHitTestVisible = false };
                    Grid.SetRowSpan(miniLayer, 3);
                    root.Children.Add(miniLayer);
                    // The bar's shape: its sides slope out to the screen's bottom edge (wider at the foot).
                    miniShape = new Path { Stroke = BorderBrush, StrokeThickness = 1, IsHitTestVisible = false };
                    miniShapeFor = null;
                    miniHot = new Border { Background = Brush(255, 70, 52, 22), IsHitTestVisible = false, IsVisible = false };
                    // Which of the ship's toolbars is up: its number, in the bar's left-hand corner.
                    miniPage = Label(15 * s, Brush(HudColor.Orange), FontWeight.Bold);
                    miniPage.IsVisible = false;
                    // The ship's storage: a thin bar along the top of the toolbar bar that fills as the cargo does, and
                    // how full it is in the right-hand corner.
                    miniStoreTrack = new Border { Background = Brush(255, 48, 56, 64), IsHitTestVisible = false, IsVisible = false };
                    miniStoreGroove = new Border { Background = Brush(255, 40, 48, 56), CornerRadius = new CornerRadius(2.5 * s), IsHitTestVisible = false, IsVisible = false };
                    miniStoreFill = new Border { CornerRadius = new CornerRadius(2.5 * s), IsHitTestVisible = false, IsVisible = false };
                    miniStoreText = Label(10 * s, White, FontWeight.Bold);
                    miniLayer.Children.Add(miniShape);
                    miniLayer.Children.Add(miniHot);
                    miniLayer.Children.Add(miniPage);
                    miniLayer.Children.Add(miniStoreTrack);
                    miniLayer.Children.Add(miniStoreGroove);
                    miniLayer.Children.Add(miniStoreFill);
                    miniPageLabel = Label(9 * s, Brush(255, 160, 170, 180), FontWeight.Bold);
                    miniPageLabel.Text = "BAR";
                    miniStoreLabel = Label(9 * s, Brush(255, 185, 193, 201), FontWeight.Bold);
                    miniSelected = new Border { Background = Brush(HudColor.Orange), IsHitTestVisible = false, IsVisible = false };
                    miniStoreLabel.Text = "CARGO";
                    miniStoreLabel.VerticalAlignment = miniStoreText.VerticalAlignment = VerticalAlignment.Center;
                    // (On a dark tag of its own, standing on the left end of the storage bar: readable over anything.)
                    miniStoreTag = new Border
                    {
                        Padding = new Thickness(8 * s, 0, 6 * s, 0),
                        IsHitTestVisible = false, IsVisible = false,
                        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s, Children = { miniStoreLabel, miniStoreText } },
                    };
                    miniLayer.Children.Add(miniPageLabel);
                    miniLayer.Children.Add(miniStoreTag);
                    miniLayer.Children.Add(miniSelected);
                    // Gravity, in the bar's right-hand corner (the storage's percentage sits over the right end of its bar).
                    // (Two lines: N, natural (a planet's or moon's); A, artificial (gravity generators).)
                    miniGravityText = Label(9 * s, Brush(255, 215, 221, 226), FontWeight.Bold);
                    miniGravityText.IsVisible = false;
                    miniGravityText.LineHeight = 12 * s;
                    miniLayer.Children.Add(miniGravityText);
                    // (And a line parting the last slot from the bar's right corner.)
                    miniEndLine = new Line { Stroke = Brush(255, 70, 80, 90), StrokeThickness = 1, IsHitTestVisible = false };
                    miniLayer.Children.Add(miniEndLine);
                    miniTicks.Clear();
                    for (int tick = 0; tick < 3; tick++)
                    {
                        var quarter = new Border { Width = 1, Background = Brush(255, 12, 15, 18), IsHitTestVisible = false, IsVisible = false };
                        miniTicks.Add(quarter);
                        miniLayer.Children.Add(quarter);
                    }
                    miniIcons.Clear(); miniNumbers.Clear(); miniLines.Clear();
                }
                miniLayer.IsVisible = true;
                // The bar: along the bottom edge of the screen, as wide as the row of slots, a cell under each slot
                // (its number small in the corner, its icon in the middle, a thin line between cells).
                double barPad = 4 * s, screenBottom = root.Bounds.Height;
                double cellGap = slotRow.Count > 1 ? Math.Max(0, slotRow[1].At.X - slotRow[0].At.X - firstTile.Bounds.Width) : 0;
                double barLeft = slotRow[0].At.X - cellGap / 2, barRight = slotRow[slotRow.Count - 1].At.X + lastTile.Bounds.Width + cellGap / 2;
                double barTall = iconSide + 2 * barPad;
                double barTop = screenBottom - barTall + 1;
                // Each side slopes out a little way from the top, then drops straight down to the screen's bottom edge.
                // Each side: a short slope down from the bar's top to a lower wing that runs out level, its outer corner
                // cut off, then straight down to the screen's bottom edge. The wings hold the toolbar's number (left)
                // and gravity (right).
                // (With the ship's storage showing, the cargo band is the top of the same shape: the slopes run on up to
                // its top, and only a faint line parts it from the slots.)
                double? stored = StorageLevel();
                double storeTall = 15 * s;
                double bodyTop = stored.HasValue ? barTop - storeTall : barTop;
                double barSlope = 88 * s;                              // how far each wing runs out
                double slopeTall = Math.Min(13 * s, barTall * 0.34);   // how far the wing's top is below the bar's
                double wingCut = 7 * s;                                // the cut-off outer corner
                double slopeRun = barTop + slopeTall - bodyTop;        // the slope's reach sideways (it climbs at 45 degrees)
                string shapeKey = $"{barLeft:0}|{barRight:0}|{barTop:0}|{screenBottom:0}|{barSlope:0}|{slopeTall:0}|{bodyTop:0}";
                if (shapeKey != miniShapeFor)
                {
                    miniShapeFor = shapeKey;
                    var barShape = new StreamGeometry();
                    using (var barCtx = barShape.Open())
                    {
                        // (Its bottom edge is just off the screen, so only the two sloping sides and the top show an edge.)
                        barCtx.BeginFigure(new Point(barLeft - barSlope, screenBottom + 2), true);
                        barCtx.LineTo(new Point(barLeft - barSlope, barTop + slopeTall + wingCut));
                        barCtx.LineTo(new Point(barLeft - barSlope + wingCut, barTop + slopeTall));
                        barCtx.LineTo(new Point(barLeft - slopeRun, barTop + slopeTall));
                        barCtx.LineTo(new Point(barLeft, bodyTop));
                        barCtx.LineTo(new Point(barRight, bodyTop));
                        barCtx.LineTo(new Point(barRight + slopeRun, barTop + slopeTall));
                        barCtx.LineTo(new Point(barRight + barSlope - wingCut, barTop + slopeTall));
                        barCtx.LineTo(new Point(barRight + barSlope, barTop + slopeTall + wingCut));
                        barCtx.LineTo(new Point(barRight + barSlope, screenBottom + 2));
                        barCtx.EndFigure(true);
                    }
                    miniShape.Data = barShape;
                }
                miniShape.Fill = hudFill ?? Brush(232, 12, 15, 18);   // (as dark as the other panels)
                // Storage: the thin bar along the top, and the percentage in the right corner under the slope.
                miniStoreTrack.IsVisible = miniStoreFill.IsVisible = miniStoreTag.IsVisible = stored.HasValue;
                if (stored.HasValue)
                {
                    // One band along the top of the bar, edged like it: "CARGO 4%" at its left end, and the rest of it a
                    // gauge that fills (a slim bar on a darker groove), so the words and the bar are one thing.
                    double storeWide = Math.Max(1, barRight - barLeft);
                    double bandTop = bodyTop;
                    // (No frame of its own: just a faint line between the band and the slots under it.)
                    miniStoreTrack.Width = storeWide; miniStoreTrack.Height = 1;
                    Canvas.SetLeft(miniStoreTrack, barLeft); Canvas.SetTop(miniStoreTrack, barTop);
                    string storeText = $"{stored.Value * 100:0}%";
                    if (miniStoreText.Text != storeText) miniStoreText.Text = storeText;
                    // (Cyan with room to spare, orange getting full, red when there's next to none left.)
                    var storeColour = stored.Value >= 0.95 ? Brush(HudColor.Red) : stored.Value >= 0.8 ? Brush(HudColor.Orange) : Brush(255, 90, 210, 255);
                    miniStoreText.Foreground = stored.Value >= 0.8 ? storeColour : White;
                    miniStoreTag.Height = storeTall;
                    miniStoreTag.Measure(Size.Infinity);
                    Canvas.SetLeft(miniStoreTag, barLeft);
                    Canvas.SetTop(miniStoreTag, bandTop);
                    // The gauge: from just after the words to the band's right end.
                    double gaugeLeft = barLeft + Math.Max(74 * s, miniStoreTag.DesiredSize.Width) + 2 * s, gaugeTall = 5 * s;
                    double gaugeWide = Math.Max(1, barRight - 8 * s - gaugeLeft), gaugeTop = bandTop + (storeTall - gaugeTall) / 2;
                    miniStoreGroove.Width = gaugeWide; miniStoreGroove.Height = gaugeTall;
                    Canvas.SetLeft(miniStoreGroove, gaugeLeft); Canvas.SetTop(miniStoreGroove, gaugeTop);
                    miniStoreFill.Width = Math.Max(stored.Value > 0 ? 2 * s : 0, gaugeWide * stored.Value); miniStoreFill.Height = gaugeTall;
                    Canvas.SetLeft(miniStoreFill, gaugeLeft); Canvas.SetTop(miniStoreFill, gaugeTop);
                    miniStoreFill.Background = storeColour;
                }
                miniStoreGroove.IsVisible = stored.HasValue;
                miniGravityText.IsVisible = false;   // (gravity has a panel of its own now: the wing had no room for two lines)
                // (The lines between slots lean the way the bar's sides slope, and run the bar's whole height.)
                double lineLean = 4 * s;
                // (Each starts exactly on its cell's top corner, so the lines, the selected slot's orange line and the
                // bar's own corners all meet; the lean is all below that.)
                miniEndLine.StartPoint = new Point(barRight, barTop);
                miniEndLine.EndPoint = new Point(barRight - 2 * lineLean, screenBottom);
                // Quarter marks on the cargo gauge.
                for (int tick = 0; tick < miniTicks.Count; tick++)
                {
                    miniTicks[tick].IsVisible = stored.HasValue;
                    if (!stored.HasValue) continue;
                    miniTicks[tick].Height = miniStoreGroove.Height;
                    Canvas.SetLeft(miniTicks[tick], Canvas.GetLeft(miniStoreGroove) + miniStoreGroove.Width * (tick + 1) / 4.0);
                    Canvas.SetTop(miniTicks[tick], Canvas.GetTop(miniStoreGroove));
                }
                if (miniGravity.HasValue)
                {
                    // (In the right wing, centred between its top and the screen's edge.)
                    gravWingTall = barTall - slopeTall;
                    gravWingLeft = barRight + slopeRun + 8 * s;
                    gravWingTop = barTop + slopeTall;
                    ShowGravity();
                }
                // The toolbar's number (read from the game's own toolbar), in the left corner under the slope.
                miniPage.IsVisible = pageNumber != null;
                if (pageNumber != null)
                {
                    if (miniPage.Text != pageNumber) miniPage.Text = pageNumber;
                    // (In the left wing: "BAR" and the number side by side, centred between its top and the screen's edge.)
                    double pageWingTall = barTall - slopeTall;
                    double pageTop = barTop + slopeTall + Math.Max(0, (pageWingTall - 18 * s) / 2);
                    Canvas.SetLeft(miniPageLabel, barLeft - barSlope + 14 * s);
                    Canvas.SetTop(miniPageLabel, pageTop + 4 * s);
                    Canvas.SetLeft(miniPage, barLeft - barSlope + 14 * s + 26 * s);
                    Canvas.SetTop(miniPage, pageTop);
                }
                miniPageLabel.IsVisible = pageNumber != null;
                miniBarTop = barTop; miniBarTall = barTall;
                miniCells.Clear();

                int withIcon = 0;
                for (int i = 0; i < slotRow.Count; i++)
                {
                    while (miniIcons.Count <= i)
                    {
                        var made = new Image { IsHitTestVisible = false, Stretch = Stretch.Uniform };
                        miniIcons.Add(made);
                        miniLayer.Children.Add(made);
                        var number = Label(9 * s, Brush(255, 150, 160, 170), FontWeight.Bold);
                        number.Text = miniIcons.Count == 10 ? "0" : miniIcons.Count.ToString();
                        miniNumbers.Add(number);
                        miniLayer.Children.Add(number);
                        var line = new Line { Stroke = Brush(255, 70, 80, 90), StrokeThickness = 1, IsHitTestVisible = false };
                        miniLines.Add(line);
                        miniLayer.Children.Add(line);
                    }
                    var icon = miniIcons[i];
                    Visual slot = slotRow[i].Tile;
                    double cellLeft = slotRow[i].At.X - cellGap / 2, cellWide = slot.Bounds.Width + cellGap;
                    miniCells.Add((cellLeft, cellWide));
                    miniNumbers[i].IsVisible = i != peekShown;
                    // (A line at the left of every cell: the first one's parts slot 1 from the bar's left corner.)
                    miniLines[i].IsVisible = true;
                    miniLines[i].StartPoint = new Point(cellLeft, barTop);
                    miniLines[i].EndPoint = new Point(cellLeft - 2 * lineLean, screenBottom);
                    IImage picture = SlotIcon(slot);
                    while (miniFilled.Count <= i) miniFilled.Add(false);
                    miniFilled[i] = picture != null;
                    // A filled slot: its number small in the corner over its icon. An empty one: the number alone,
                    // bigger and dim, in the middle of the cell, so the cell isn't just blank.
                    bool slotFilled = picture != null;
                    miniNumbers[i].Foreground = slotFilled ? Brush(255, 205, 211, 217) : Brush(255, 54, 62, 70);
                    double numberSize = (slotFilled ? 9 : 11) * s;
                    if (Math.Abs(miniNumbers[i].FontSize - numberSize) > 0.01) miniNumbers[i].FontSize = numberSize;
                    if (slotFilled)
                    {
                        Canvas.SetLeft(miniNumbers[i], cellLeft + 6 * s);
                        Canvas.SetTop(miniNumbers[i], barTop + 3 * s);
                    }
                    else
                    {
                        Canvas.SetLeft(miniNumbers[i], cellLeft - lineLean + cellWide / 2 - 3 * s);
                        Canvas.SetTop(miniNumbers[i], barTop + Math.Max(0, (barTall - 15 * s) / 2));
                    }
                    icon.IsVisible = picture != null && i != peekShown;   // (not under the game's own slot while that's popped up)
                    if (picture != null) withIcon++;
                    if (!icon.IsVisible) continue;
                    if (!ReferenceEquals(icon.Source, picture)) icon.Source = picture;
                    icon.Width = icon.Height = iconSide;
                    Canvas.SetLeft(icon, cellLeft - lineLean + cellWide / 2 - iconSide / 2);   // (the middle of the leaning cell)
                    Canvas.SetTop(icon, barTop + barPad);
                }
                for (int i = slotRow.Count; i < miniIcons.Count; i++) { miniIcons[i].IsVisible = false; miniNumbers[i].IsVisible = false; miniLines[i].IsVisible = false; }
                MiniHighlight(peekShown);
                MiniLog($"{slotRow.Count} slot(s), {withIcon} with an icon, toolbar number {pageNumber ?? "not found"}");
            }
            catch (Exception e) { ClearMiniToolbar(); MiniLog("couldn't show it: " + e.GetType().Name + " " + e.Message); }
        }

        // The toolbar bar keeps itself up to date on a clock of its own (the HUD is only redrawn when something on it
        // changes, which sitting still on the start line is hardly ever: switching toolbars then showed late).
        private bool miniWanted;
        private double miniIconShare = 0.5;
        private DispatcherTimer miniTimer;

        private void MiniTick()
        {
            if (root == null || !root.IsVisible) return;
            try
            {
            UpdateMiniToolbar(miniWanted, miniIconShare);
            // (The toolbar's number is read every frame: on a switch it changes at once, and the icons are looked at again straight away.)
            if (toolbarRead != null && miniPage != null && miniPage.IsVisible)
            {
                string pageLive = null;
                try { pageLive = toolbarRead(); } catch { toolbarRead = null; }
                if (pageLive != null && pageLive != miniPage.Text) { miniPage.Text = pageLive; miniCheckedAt = 0; UpdateMiniToolbar(miniWanted, miniIconShare); }
            }
            // The selected slot: a thin orange line along the top of its cell.
            if (miniSelected != null)
            {
                int selectedNow = -1;
                if (toolbarSelected != null && miniLayer != null && miniLayer.IsVisible)
                {
                    try { selectedNow = toolbarSelected(); } catch { toolbarSelected = null; }
                }
                bool selectedShown = selectedNow >= 0 && selectedNow < miniCells.Count;
                if (miniSelected.IsVisible != selectedShown) miniSelected.IsVisible = selectedShown;
                if (selectedShown)
                {
                    miniSelected.Width = miniCells[selectedNow].Wide;
                    miniSelected.Height = 2 * builtScale;
                    Canvas.SetLeft(miniSelected, miniCells[selectedNow].Left);
                    Canvas.SetTop(miniSelected, miniBarTop);
                }
            }
            // (Gravity changes as you fly: its number is kept up between the bar's own looks at the toolbar.)
            if (miniGravityText != null && miniGravityText.IsVisible && miniGravity.HasValue)
            {
                ShowGravity();
            }
            }
            catch (Exception e) { MiniLog("couldn't keep it up to date: " + e.GetType().Name + " " + e.Message); }
        }

        private void ClearToolbarPeek()
        {
            peekTimer?.Stop();
            peekPhase = 0;
            try { peekUndo?.Dispose(); } catch { }
            try { peekMoveUndo?.Dispose(); } catch { }
            peekUndo = peekMoveUndo = null;
            if (peekFrame != null) peekFrame.IsVisible = false;
        }

        private void PeekTick()
        {
            try
            {
                double t = Math.Min(1, peekClock.Elapsed.TotalSeconds / (peekPhase == 1 ? 0.22 : 0.16));
                double eased = peekPhase == 1 ? 1 - (1 - t) * (1 - t) * (1 - t) : t * t;   // quick then settling; away: speeding up
                double shown = peekPhase == 1 ? eased : 1 - eased;
                if (peekShift != null) peekShift.Y = (1 - shown) * peekTravel;
                if (peekFrame != null) peekFrame.Opacity = shown;
                if (t < 1) return;
                if (peekPhase == 2) ClearToolbarPeek();
                else { peekTimer?.Stop(); peekPhase = 0; }
            }
            catch { ClearToolbarPeek(); }
        }

        private void StartPeekSlide(int phase)
        {
            peekPhase = phase;
            peekClock.Restart();
            if (peekTimer == null)
            {
                peekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                peekTimer.Tick += (_, __) => PeekTick();
            }
            peekTimer.Start();
        }

        private void UpdateToolbarPeek(int slot)
        {
            if (slot == peekShown) return;
            peekShown = slot;
            MiniHighlight(slot);
            if (slot < 0)
            {
                // Time's up: slide it away, then cover the toolbar again.
                if (peekUndo != null) StartPeekSlide(2); else ClearToolbarPeek();
                return;
            }
            ClearToolbarPeek();
            try
            {
                Visual top = attachedTo == null ? null : (Visual)TopLevel.GetTopLevel(attachedTo) ?? attachedTo;
                if (top == null) return;
                var tiles = new List<(Visual Tile, Point At)>();
                foreach (var v in top.GetVisualDescendants())
                {
                    if (v.GetType().Name != "ToolbarTileControl" || v.Bounds.Width < 2) continue;
                    var at = v.TranslatePoint(new Point(0, 0), top);
                    if (at.HasValue) tiles.Add((v, at.Value));
                }
                // The toolbar's row: the line with the most tiles on it, left to right.
                var row = tiles.GroupBy(t => Math.Round(t.At.Y / 8)).OrderByDescending(g => g.Count()).FirstOrDefault()?.OrderBy(t => t.At.X).ToList();
                if (!loggedPeek) { loggedPeek = true; PluginLog.Info($"Toolbar slot: {tiles.Count} toolbar tile(s) found, {row?.Count ?? 0} in the toolbar's row"); }
                if (row == null || slot >= row.Count) return;
                Visual tile = row[slot].Tile;
                double s = builtScale;
                foreach (var h in hidden)
                {
                    if (!h.Visual.TryGetTarget(out var holder) || !tile.GetVisualAncestors().Contains(holder)) continue;
                    var p = tile.TranslatePoint(new Point(0, 0), holder);
                    if (!p.HasValue) return;
                    // (Just the square the slot draws, with a hair's room for its edge.)
                    double pad = 2 * s, shownSide = Math.Min(tile.Bounds.Width, tile.Bounds.Height);
                    var open = new Rect(p.Value.X - pad, p.Value.Y - pad, shownSide + 2 * pad, shownSide + 2 * pad);
                    // (Where the slot is on our HUD, measured before the toolbar is moved for the slide.)
                    var onHud = tile.TranslatePoint(new Point(0, 0), root);
                    peekUndo = holder.SetValue(Visual.ClipProperty, new RectangleGeometry(open), Avalonia.Data.BindingPriority.Animation);
                    // (The whole toolbar moves with the slide; only the open slot shows.)
                    peekTravel = tile.Bounds.Height + 60 * s;
                    peekShift ??= new TranslateTransform();
                    peekShift.Y = peekTravel;
                    peekMoveUndo = holder.SetValue(Visual.RenderTransformProperty, peekShift, Avalonia.Data.BindingPriority.Animation);

                    // Our frame round it, and the slot's number on a tab over it.
                    if (onHud.HasValue)
                        {
                        if (peekLayer == null || !ReferenceEquals(peekLayer.Parent, root))
                        {
                            // (On a canvas of their own: placed by position, so they can never push the HUD's panels about.)
                            peekLayer = new Canvas { IsHitTestVisible = false };
                            Grid.SetRowSpan(peekLayer, 3);
                            root.Children.Add(peekLayer);
                            // One panel like the HUD's others, behind the slot (the game draws its toolbar over our
                            // HUD): the slot's number along its top, the slot under it.
                            peekCaption = Label(11 * s, Brush(255, 200, 206, 212), FontWeight.Bold);
                            peekCaption.LetterSpacing = 1.5 * s;
                            peekCaption.HorizontalAlignment = HorizontalAlignment.Center;
                            peekCaption.VerticalAlignment = VerticalAlignment.Top;
                            peekCaption.Margin = new Thickness(0, 4 * s, 0, 0);
                            peekFrame = Box(s);
                            peekFrame.Padding = new Thickness(0);
                            peekCaption.IsVisible = false;   // (no label: just the panel behind the slot)
                            peekFrame.Child = peekCaption;
                            peekFrame.IsHitTestVisible = false;
                            peekFrame.RenderTransform = peekShift;
                            peekLayer.Children.Add(peekFrame);
                        }
                        // (The slot's control is a little taller than the square it draws: go by the square.)
                        double side = Math.Min(tile.Bounds.Width, tile.Bounds.Height);
                        double gap = 9 * s, capH = 0;
                        peekFrame.Background = Brush(225, 12, 15, 18);
                        peekFrame.Width = side + 2 * gap;
                        peekFrame.Height = side + 2 * gap + capH;
                        Canvas.SetLeft(peekFrame, onHud.Value.X + side / 2 - peekFrame.Width / 2);
                        Canvas.SetTop(peekFrame, onHud.Value.Y - gap - capH);
                        // (With the toolbar bar showing: the panel behind the slot runs on down into the bar, as wide as
                        // the bar's cell, so the slot stands up out of the bar rather than floating over it.)
                        if (miniLayer != null && miniLayer.IsVisible && slot < miniCells.Count)
                        {
                            double frameTop = onHud.Value.Y - gap - capH;
                            peekFrame.Width = miniCells[slot].Wide;
                            Canvas.SetLeft(peekFrame, miniCells[slot].Left);
                            peekFrame.Height = Math.Max(peekFrame.Height, root.Bounds.Height - frameTop + 2);
                        }
                        peekCaption.Text = "TOOLBAR " + (slot == 9 ? 0 : slot + 1);
                        peekFrame.Opacity = 0;
                        peekFrame.IsVisible = true;
                    }
                    StartPeekSlide(1);
                    return;
                }
            }
            catch (Exception e) { ClearToolbarPeek(); if (!loggedPeek) { loggedPeek = true; PluginLog.Error("Toolbar slot: couldn't show it", e); } }
        }

        private void HideGameHudNow()
        {
            long hideStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // From the window's root: the game keeps some HUD screens (character and ship status, toggles) in
                // other layers than the one our HUD is attached to.
                Visual top = attachedTo == null ? null : (Visual)TopLevel.GetTopLevel(attachedTo) ?? attachedTo;
                if (top == null || !hideGameHud) return;
                hidden.RemoveAll(h => !h.Visual.TryGetTarget(out _));
                var keptScreens = new HashSet<string>();
                bool logScreens = !loggedKeptScreens;
                bool sawTerminal = false;
                var stack = new Stack<(Visual V, int Depth)>();
                stack.Push((top, 0));
                while (stack.Count > 0)
                {
                    var (v, depth) = stack.Pop();
                    if (ReferenceEquals(v, root)) continue;
                    string ns = v.GetType().Namespace ?? "";
                    if (depth > 0 && ns.StartsWith("Keen.Game2.Client.UI.HUD") && keepGyro)
                    {
                        // Keep the gyro: leave it (and whatever holds it) showing, hide the rest around it.
                        if (v.GetType().Name == "CockpitGyroScreen") { SetGyroMode(v); continue; }
                        if (HoldsGyro(v))
                        {
                            foreach (var c in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(v)) stack.Push((c, depth + 1));
                            continue;
                        }
                    }
                    if (ns.StartsWith("Keen.Game2.Client.UI.TerminalScreen") && v.IsEffectivelyVisible) sawTerminal = true;
                    // (The toolbar's action menu, where an action is picked for a slot: the terminal steps aside
                    // while it's up, so it counts the same.)
                    if (depth > 0 && ns.StartsWith("Keen.Game2.Client.UI.HUD") && !sawTerminal && ActionMenuShowing(v, ns)) sawTerminal = true;
                    if (depth > 0 && ns.StartsWith("Keen.Game2.Client.UI.HUD") && terminalOpen && IsToolbarPart(v, ns))
                    {
                        // The terminal is open (the control panel puts actions on the toolbar): the toolbar and its
                        // action menu stay showing, and are put back if they were hidden.
                        int at = hidden.FindIndex(h => h.Visual.TryGetTarget(out var t) && ReferenceEquals(t, v));
                        if (at >= 0)
                        {
                            try { hidden[at].Undo?.Dispose(); } catch { }
                            hidden.RemoveAt(at);
                        }
                        continue;
                    }
                    if (depth > 0 && ns.StartsWith("Keen.Game2.Client.UI.HUD"))
                    {
                        if (!hidden.Exists(h => h.Visual.TryGetTarget(out var t) && ReferenceEquals(t, v)))
                        {
                            // Above the game's own value (and its bindings), and taken off again when this is turned off.
                            // Not drawn rather than hidden: hidden, the game stops updating its HUD (the ship's power,
                            // hydrogen and battery readings the speedometer's bars use).
                            // (An empty clip: the game animates its HUD's opacity as it fades in, which overrode a
                            // see-through setting on loading, showing both HUDs at once.)
                            var undo = v.SetValue(Visual.ClipProperty, new RectangleGeometry(new Rect(0, 0, 0, 0)), Avalonia.Data.BindingPriority.Animation);
                            hidden.Add((new WeakReference<Visual>(v), undo));
                        }
                        continue;
                    }
                    if (logScreens && IsScreen(v) && v.IsVisible) keptScreens.Add(v.GetType().FullName);
                    if (depth >= 16) continue;
                    foreach (var c in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(v)) stack.Push((c, depth + 1));
                }
                // (Used on the next pass, a quarter of a second on.)
                // (Kept for a second after it was last seen: the terminal closes for a moment as the action menu opens.)
                long nowMs = Environment.TickCount64;
                if (sawTerminal) terminalSeenMs = nowMs;
                bool openNow = sawTerminal || nowMs - terminalSeenMs < 1000;
                if (openNow != terminalOpen)
                {
                    terminalOpen = openNow;
                    if (terminalOpen) UpdateToolbarPeek(-1);
                    PluginLog.Info("Race HUD only: terminal " + (terminalOpen ? "open: the game's toolbar shows while it is" : "closed: the toolbar is hidden again"));
                }
                if (logScreens)
                {
                    loggedKeptScreens = true;
                    PluginLog.Info($"Race HUD only: hid {hidden.Count} game HUD part(s); other screens still showing: {string.Join(", ", keptScreens)}");
                }
            }
            catch (Exception e) { PluginLog.Error("Race HUD only: couldn't hide the game HUD", e); }
            PluginLog.Time("hud: hide game hud pass", (System.Diagnostics.Stopwatch.GetTimestamp() - hideStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private bool loggedKeptScreens;
        private bool terminalOpen;   // the game's terminal (G) is on screen: the toolbar isn't hidden while it is
        private long terminalSeenMs = -100000;

        /// <summary>The toolbar's action menu (BlockActionDetails) is up, in or under this part of the game's HUD.</summary>
        private static bool ActionMenuShowing(Visual v, string ns)
        {
            if (ns.Contains(".BlockActionDetails")) return v.IsEffectivelyVisible && v.Bounds.Width > 1;
            foreach (var d in v.GetVisualDescendants())
                if ((d.GetType().Namespace ?? "").Contains(".BlockActionDetails") && d.IsEffectivelyVisible && d.Bounds.Width > 1) return true;
            return false;
        }

        /// <summary>A part of the game's HUD that is the toolbar or belongs to it (its action menu), or holds the toolbar.</summary>
        private static bool IsToolbarPart(Visual v, string ns)
        {
            if (ns.Contains(".HUD.Toolbar")) return true;
            foreach (var d in v.GetVisualDescendants())
                if ((d.GetType().Namespace ?? "").Contains(".HUD.Toolbar")) return true;
            return false;
        }

        // Which of the game's HUD views hold the gyro (worked out once per view: the tree is walked 4 times a second).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Visual, object> holdsGyro = new System.Runtime.CompilerServices.ConditionalWeakTable<Visual, object>();
        private static readonly object Yes = new object();

        private static bool HoldsGyro(Visual v)
        {
            if (holdsGyro.TryGetValue(v, out var known)) return ReferenceEquals(known, Yes);
            bool has = HasGyro(v, 0);
            if (has) holdsGyro.AddOrUpdate(v, Yes);   // a "no" isn't kept: the gyro may be added to it later
            return has;
        }

        /// <summary>Ship analyser panel: a row per stat, one column per grid (the second one coloured against the first).</summary>
        // Race spotlight: when the panel moves on to its next page, the old one slides out to the side as it fades, then
        // the new one slides in as it fades up.
        private DispatcherTimer analyserSlide;
        private readonly System.Diagnostics.Stopwatch analyserSlideClock = new System.Diagnostics.Stopwatch();
        private int analyserSlidePhase;            // 0 = still, 1 = going out, 2 = coming in
        private HudModel.AnalyserPanel analyserNext;
        private double analyserNextScale;
        private IBrush analyserNextFill;
        private string analyserHeadingShown;
        // Made on first use, on the UI thread. Making an Avalonia object while the plugin is still being created
        // starts Avalonia's dispatcher on the wrong thread, and the game's whole UI then stays black.
        private TranslateTransform analyserShift;
        private double analyserSlideSeconds = 0.35;   // from the settings, with each page

        // ---- cinematic fade and letterbox
        private Grid barsGrid;
        private Border fadeRect;

        private void UpdateCineOverlay(double fade, double bars)
        {
            if (barsGrid == null || fadeRect == null) return;
            bars = Math.Clamp(bars, 0, 0.45);
            barsGrid.IsVisible = bars > 0.001;
            if (barsGrid.IsVisible)
            {
                barsGrid.RowDefinitions[0].Height = new GridLength(bars, GridUnitType.Star);
                barsGrid.RowDefinitions[1].Height = new GridLength(1 - 2 * bars, GridUnitType.Star);
                barsGrid.RowDefinitions[2].Height = new GridLength(bars, GridUnitType.Star);
            }
            // The title card and the timing strip sit inside the picture, clear of the bars (not cut off by, or
            // sitting on, them), and in front of them.
            double sc = builtScale > 0 ? builtScale : 1;
            double barPx = barsGrid.IsVisible && root != null ? bars * root.Bounds.Height : 0;
            if (coverBox != null)
            {
                var top = new Thickness(0, Math.Max(90 * sc, barPx + 24 * sc), 0, 0);
                if (coverBox.Margin != top) coverBox.Margin = top;
                coverBox.ZIndex = 60;
            }
            if (filmBox != null)
            {
                var bottom = new Thickness(0, 0, 0, Math.Max(70 * sc, barPx + 24 * sc));
                if (filmBox.Margin != bottom) filmBox.Margin = bottom;
            }
            fade = Math.Clamp(fade, 0, 1);
            fadeRect.IsVisible = fade > 0.001;
            fadeRect.Opacity = fade;
        }

        // ---- cinematic debug timeline
        private const double CineDebugWidth = 900;
        private Border cineBox, cineHead;
        private TextBlock cineTitle, cineNow;
        private Grid cineShots;
        private Canvas cineEffects;
        private string cineDebugFor;

        private void UpdateCineDebug(HudModel.CineDebugPanel c, double s, IBrush fill)
        {
            if (cineBox == null) return;
            cineBox.IsVisible = c != null;
            if (c == null) return;
            cineBox.Background = fill;
            double w = CineDebugWidth * s;
            // The blocks are only made again when the shots, the effects or which of them is on changes.
            var key = new System.Text.StringBuilder();
            foreach (var shot in c.Shots) key.Append(shot.Name).Append(shot.Seconds).Append(shot.Playing).Append(';');
            key.Append(c.Lanes).Append('|');
            foreach (var fx in c.Effects) key.Append(fx.Name).Append(fx.Start).Append(fx.Seconds).Append(fx.Active).Append(fx.Lane).Append(';');
            if (key.ToString() != cineDebugFor)
            {
                cineDebugFor = key.ToString();
                cineShots.ColumnDefinitions.Clear();
                cineShots.Children.Clear();
                cineEffects.Children.Clear();
                for (int i = 0; i < c.Shots.Count; i++)
                {
                    var shot = c.Shots[i];
                    cineShots.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.1, shot.Seconds), GridUnitType.Star)));
                    var name = Label(12 * s, White, FontWeight.Bold);
                    name.Text = $"{shot.Name}  {shot.Seconds:0.#}s";
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    name.VerticalAlignment = VerticalAlignment.Center;
                    name.Margin = new Thickness(5 * s, 0, 3 * s, 0);
                    var block = new Border
                    {
                        Background = Brush(shot.Playing ? (byte)255 : (byte)150, shot.Colour.R, shot.Colour.G, shot.Colour.B),
                        BorderBrush = shot.Playing ? White : Brush(255, 9, 20, 30),
                        BorderThickness = new Thickness((shot.Playing ? 2 : 1) * s),
                        Child = name, ClipToBounds = true,
                    };
                    Grid.SetColumn(block, i);
                    cineShots.Children.Add(block);
                }
                cineEffects.Height = Math.Max(1, c.Lanes) * 20 * s;
                if (c.Total > 0)
                    foreach (var fx in c.Effects)
                    {
                        var name = Label(11 * s, White);
                        name.Text = fx.Name;
                        name.TextTrimming = TextTrimming.CharacterEllipsis;
                        name.Margin = new Thickness(4 * s, 0, 2 * s, 0);
                        var block = new Border
                        {
                            Height = 18 * s, Width = Math.Max(4 * s, w * fx.Seconds / c.Total),
                            Background = Brush(fx.Active ? (byte)255 : (byte)150, fx.Colour.R, fx.Colour.G, fx.Colour.B),
                            BorderBrush = fx.Active ? White : Brush(255, 9, 20, 30), BorderThickness = new Thickness(1 * s),
                            Child = name, ClipToBounds = true,
                        };
                        Canvas.SetLeft(block, w * fx.Start / c.Total);
                        Canvas.SetTop(block, (fx.Lane * 20 + 1) * s);
                        cineEffects.Children.Add(block);
                    }
            }
            cineTitle.Text = c.Title;
            cineNow.Text = c.Now;
            cineHead.Margin = new Thickness(c.Total > 0 ? Math.Clamp(w * c.Time / c.Total, 0, Math.Max(0, w - 2 * s)) : 0, 0, 0, 0);
        }

        private void ShowAnalyser(HudModel.AnalyserPanel a, double s, IBrush fill)
        {
            bool newPage = a?.Heading != null && analyserHeadingShown != null && a.Heading != analyserHeadingShown && analyserBox.IsVisible;
            if (a != null) analyserSlideSeconds = a.SlideSeconds;
            if (a != null && analyserSlideSeconds > 0.01 && (analyserSlidePhase == 1 || newPage))
            {
                // Keep showing the old page while it goes; the newest contents are put in once it's out of sight.
                analyserNext = a; analyserNextScale = s; analyserNextFill = fill;
                if (analyserSlidePhase != 1) StartAnalyserSlide(1);
                return;
            }
            if (a == null) StopAnalyserSlide();
            UpdateAnalyser(a, s, fill);
            analyserHeadingShown = a?.Heading;
        }

        private void StartAnalyserSlide(int phase)
        {
            analyserSlidePhase = phase;
            analyserSlideClock.Restart();
            if (analyserShift == null) analyserShift = new TranslateTransform();
            analyserBox.RenderTransform = analyserShift;
            if (analyserSlide == null)
            {
                analyserSlide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                analyserSlide.Tick += (_, __) => AnalyserSlideTick();
            }
            analyserSlide.Start();
        }

        private void StopAnalyserSlide()
        {
            analyserSlidePhase = 0;
            analyserNext = null;
            analyserSlide?.Stop();
            if (analyserShift != null) analyserShift.X = 0;
            if (analyserBox != null) analyserBox.Opacity = 1;
        }

        private void AnalyserSlideTick()
        {
            try
            {
                double t = Math.Min(1, analyserSlideClock.Elapsed.TotalSeconds / Math.Max(0.01, analyserSlideSeconds));
                double eased = t * t * (3 - 2 * t);
                double far = 90 * Math.Max(0.5, analyserNextScale);   // how far to the side it travels
                if (analyserSlidePhase == 1)
                {
                    analyserBox.Opacity = 1 - eased;
                    analyserShift.X = eased * far;
                    if (t < 1) return;
                    var next = analyserNext;
                    analyserNext = null;
                    if (next == null) { StopAnalyserSlide(); return; }
                    UpdateAnalyser(next, analyserNextScale, analyserNextFill);
                    analyserHeadingShown = next.Heading;
                    analyserBox.Opacity = 0;
                    StartAnalyserSlide(2);
                }
                else if (analyserSlidePhase == 2)
                {
                    analyserBox.Opacity = eased;
                    analyserShift.X = (1 - eased) * far;
                    if (t >= 1) StopAnalyserSlide();
                }
                else StopAnalyserSlide();
            }
            catch (Exception) { StopAnalyserSlide(); }
        }

        // ---- lap replay map (under the analyser's rows)
        private const double AnalyserMapW = 420, AnalyserMapH = 300;
        private Border analyserMapBox;
        private Canvas analyserMapCanvas;
        private WrapPanel analyserMapKey;   // under the map: what each sign on it means
        private string analyserMapFor;
        private readonly List<Control> analyserMapDone = new List<Control>();
        private Ellipse analyserMapDot;

        /// <summary>
        /// Filled four-sided shapes (graph shading), at most 30 to a Path: one big shape overflows the game's UI
        /// drawing buffer and crashes the game.
        /// </summary>
        private static void AddQuads(Canvas canvas, List<Point[]> quads, IBrush fill)
        {
            for (int start = 0; start < quads.Count; start += 30)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                    for (int i = start; i < Math.Min(start + 30, quads.Count); i++)
                    {
                        var q = quads[i];
                        if (!Array.TrueForAll(q, Drawable)) continue;
                        ctx.BeginFigure(q[0], true);
                        for (int k = 1; k < q.Length; k++) ctx.LineTo(q[k]);
                        ctx.EndFigure(true);
                    }
                canvas.Children.Add(new Path { Data = g, Fill = fill, IsHitTestVisible = false });
            }
        }

        /// <summary>
        /// A point the game's UI can draw: a NaN, an infinity or a point miles off (bad lap data) makes a shape the
        /// UI renderer can't fit, and that crashes the game.
        /// </summary>
        private static bool Drawable(Point p) =>
            double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) < 20000 && Math.Abs(p.Y) < 20000;

        /// <summary>Every nth point so there are at most max (the first and last are kept).</summary>
        private static List<Point> Thin(List<Point> pts, int max)
        {
            if (pts.Count <= max) return pts;
            var outPts = new List<Point>();
            double step = (pts.Count - 1) / (double)(max - 1);
            for (int i = 0; i < max; i++) outPts.Add(pts[(int)Math.Round(i * step)]);
            return outPts;
        }

        /// <summary>A line through the points as polylines of up to 40 segments each (longer strokes don't draw reliably).</summary>
        private static List<Polyline> Strokes(List<Point> pts, IBrush brush, double width)
        {
            pts = pts.FindAll(Drawable);
            var list = new List<Polyline>();
            for (int i = 0; i + 1 < pts.Count; i += 40)
            {
                var chunk = new AvaloniaList<Point>();
                for (int k = i; k <= Math.Min(i + 40, pts.Count - 1); k++) chunk.Add(pts[k]);
                list.Add(new Polyline
                {
                    Points = chunk, Stroke = brush, StrokeThickness = width, IsHitTestVisible = false,
                    StrokeLineCap = PenLineCap.Flat, StrokeJoin = PenLineJoin.Bevel,   // (round ends and corners are many more triangles)
                });
            }
            return list;
        }

        /// <summary>
        /// The key under the map: the ship, start, finish and checkpoints, and whichever of the crash, missed
        /// checkpoint and top speed signs are on this lap's map. Drawn the same way as on the map.
        /// </summary>
        private void BuildMapKey(HudModel.AnalyserMap m, double s, IBrush shipColour)
        {
            if (analyserMapKey == null) return;
            analyserMapKey.Children.Clear();
            analyserMapKey.IsVisible = m.ShowKey;
            if (!m.ShowKey) return;
            Control Dot(double d, IBrush fill, IBrush stroke, double thick) => new Ellipse { Width = d, Height = d, Fill = fill, Stroke = stroke, StrokeThickness = thick };
            Control Sign(int kind)
            {
                double r = 7 * s;
                var c = new Canvas { Width = 2 * r + 4 * s, Height = 2 * r + 4 * s };
                Control shape;
                if (kind == 0)
                {
                    var cross = new StreamGeometry();
                    using (var ctx = cross.Open())
                    {
                        ctx.BeginFigure(new Point(2 * s, 2 * s), false); ctx.LineTo(new Point(2 * r + 2 * s, 2 * r + 2 * s)); ctx.EndFigure(false);
                        ctx.BeginFigure(new Point(2 * s, 2 * r + 2 * s), false); ctx.LineTo(new Point(2 * r + 2 * s, 2 * s)); ctx.EndFigure(false);
                    }
                    shape = new Path { Data = cross, Stroke = Brush(255, 255, 90, 80), StrokeThickness = 3 * s, StrokeLineCap = PenLineCap.Round };
                    c.Children.Add(shape);
                    return c;
                }
                var outer = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = kind == 1 ? Brush(255, 255, 170, 60) : White, StrokeThickness = 2.5 * s };
                Canvas.SetLeft(outer, 2 * s); Canvas.SetTop(outer, 2 * s);
                c.Children.Add(outer);
                if (kind == 2)
                {
                    var inner = new Ellipse { Width = r, Height = r, Fill = White };
                    Canvas.SetLeft(inner, 2 * s + r / 2); Canvas.SetTop(inner, 2 * s + r / 2);
                    c.Children.Add(inner);
                }
                return c;
            }
            void Item(Control icon, string text)
            {
                icon.VerticalAlignment = VerticalAlignment.Center;
                icon.IsHitTestVisible = false;
                var label = Label(12 * s, Grey);
                label.Text = text;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(5 * s, 0, 0, 0);
                analyserMapKey.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3 * s, 14 * s, 0),
                    Children = { new Border { Width = 20 * s, Child = icon, HorizontalAlignment = HorizontalAlignment.Center }, label },
                });
            }
            var bevel = Brush(255, 9, 20, 30);
            Item(Dot(12 * s, White, shipColour, 3 * s), "The ship");
            Item(Dot(11 * s, Brush(HudColor.Cyan), bevel, 1 * s), m.Gates.Any(g => g.Kind == 1) ? "Start" : "Start / finish");
            if (m.Gates.Any(g => g.Kind == 1)) Item(Dot(11 * s, Brush(HudColor.Purple), bevel, 1 * s), "Finish");
            if (m.Gates.Any(g => g.Kind == 2)) Item(Dot(7 * s, Brush(230, 185, 190, 195), bevel, 1 * s), "Checkpoint");
            if (m.Marks.Any(k => k.Kind == 0)) Item(Sign(0), "Crash");
            if (m.Marks.Any(k => k.Kind == 1)) Item(Sign(1), "Missed checkpoint");
            if (m.Marks.Any(k => k.Kind == 2)) Item(Sign(2), "Top speed");
        }

        private void UpdateAnalyserMap(HudModel.AnalyserMap m, double s)
        {
            if (analyserMapBox == null) return;
            analyserMapBox.IsVisible = m != null;
            if (m == null) { analyserMapFor = null; return; }
            double w = AnalyserMapW * s, h = AnalyserMapH * s, size = Math.Min(w, h) * 0.92;
            Point T(double x, double y) => new Point(w / 2 + (x - 0.5) * size, h / 2 + (y - 0.5) * size);
            var colour = Brush(m.Colour.WithAlpha(255));
            if (m.Key != analyserMapFor)
            {
                // The course and the whole lap (faintly), the gates, and the dot: made again only when the lap changes.
                analyserMapFor = m.Key;
                analyserMapCanvas.Children.Clear();
                analyserMapDone.Clear();
                if (m.Faint.Count >= 2)
                    foreach (var line in Strokes(m.Faint.Select(pt => T(pt.X, pt.Y)).ToList(), Brush(110, 200, 120, 255), 2 * s)) analyserMapCanvas.Children.Add(line);
                foreach (var line in Strokes(m.Line.Select(pt => T(pt.X, pt.Y)).ToList(), Brush(m.Colour.WithAlpha(70)), 3 * s)) analyserMapCanvas.Children.Add(line);
                foreach (var g in m.Gates)
                {
                    double d = (g.Kind == 2 ? 7 : 11) * s;
                    var mark = new Ellipse
                    {
                        Width = d, Height = d, IsHitTestVisible = false,
                        Fill = g.Kind == 0 ? Brush(HudColor.Cyan) : g.Kind == 1 ? Brush(HudColor.Purple) : Brush(230, 185, 190, 195),
                        Stroke = Brush(255, 9, 20, 30), StrokeThickness = 1 * s,
                    };
                    var at = T(g.X, g.Y);
                    Canvas.SetLeft(mark, at.X - d / 2);
                    Canvas.SetTop(mark, at.Y - d / 2);
                    analyserMapCanvas.Children.Add(mark);
                }
                BuildMapKey(m, s, colour);
                analyserMapDot = new Ellipse { Width = 12 * s, Height = 12 * s, Fill = White, Stroke = colour, StrokeThickness = 3 * s, IsHitTestVisible = false, ZIndex = 10 };
                analyserMapCanvas.Children.Add(analyserMapDot);
                // Marks: a crash (a cross), a missed checkpoint (a ring), the top speed (a ring with a dot).
                foreach (var mk in m.Marks)
                {
                    var at = T(mk.X, mk.Y);
                    double r = 9 * s;
                    Control mark;
                    if (mk.Kind == 0)
                    {
                        var cross = new StreamGeometry();
                        using (var ctx = cross.Open())
                        {
                            ctx.BeginFigure(new Point(-r, -r), false); ctx.LineTo(new Point(r, r)); ctx.EndFigure(false);
                            ctx.BeginFigure(new Point(-r, r), false); ctx.LineTo(new Point(r, -r)); ctx.EndFigure(false);
                        }
                        mark = new Path { Data = cross, Stroke = Brush(255, 255, 90, 80), StrokeThickness = 3.5 * s, StrokeLineCap = PenLineCap.Round };
                    }
                    else
                    {
                        var ring = new Canvas();
                        var outer = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = mk.Kind == 1 ? Brush(255, 255, 170, 60) : White, StrokeThickness = 3 * s };
                        Canvas.SetLeft(outer, -r); Canvas.SetTop(outer, -r);
                        ring.Children.Add(outer);
                        if (mk.Kind == 2)
                        {
                            var inner = new Ellipse { Width = r, Height = r, Fill = White };
                            Canvas.SetLeft(inner, -r / 2); Canvas.SetTop(inner, -r / 2);
                            ring.Children.Add(inner);
                        }
                        mark = ring;
                    }
                    mark.IsHitTestVisible = false;
                    mark.ZIndex = 12;
                    Canvas.SetLeft(mark, at.X);
                    Canvas.SetTop(mark, at.Y);
                    analyserMapCanvas.Children.Add(mark);
                }
            }
            if (analyserMapDot != null) analyserMapDot.IsVisible = !m.HideDot;
            // The lap so far, bright, up to the dot. The pieces are kept and their points changed, rather than
            // made again every frame; only the last piece (where the dot is) changes most frames.
            var dot = T(m.DotX, m.DotY);
            int flownCount = Math.Min(m.Done, m.Line.Count) + 1;   // + the dot
            int pieces = flownCount >= 2 ? (flownCount - 2) / 40 + 1 : 0;
            while (analyserMapDone.Count < pieces)
            {
                var piece = new Polyline { Stroke = colour, StrokeThickness = 3.5 * s, IsHitTestVisible = false, ZIndex = 5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
                analyserMapDone.Add(piece);
                analyserMapCanvas.Children.Add(piece);
            }
            Point Flown(int i) => i < flownCount - 1 ? T(m.Line[i].X, m.Line[i].Y) : dot;
            for (int k = 0; k < analyserMapDone.Count; k++)
            {
                var piece = (Polyline)analyserMapDone[k];
                piece.IsVisible = k < pieces;
                if (k >= pieces) continue;
                int from = k * 40, to = Math.Min(from + 40, flownCount - 1);
                bool last = k == pieces - 1;
                // Earlier pieces are full and don't change: only redo them when they aren't yet (a new lap, or the start).
                if (!last && piece.Tag is int done && done == to) continue;
                var pts = new AvaloniaList<Point>();
                for (int i = from; i <= to; i++) pts.Add(Flown(i));
                piece.Points = pts;
                piece.Tag = last ? -1 : to;
            }
            if (analyserMapDot != null)
            {
                Canvas.SetLeft(analyserMapDot, dot.X - 6 * s);
                Canvas.SetTop(analyserMapDot, dot.Y - 6 * s);
            }
        }

        private StackPanel analyserGraphs, anaGapBlock;
        private TextBlock anaSpeedTitle, anaGapTitle;
        private Canvas anaSpeedCanvas, anaGapCanvas;
        private WrapPanel anaSpeedLegend, anaGapLegend;
        private HudModel.ResultsPanel analyserGraphsFor;

        private void UpdateAnalyserGraphs(HudModel.ResultsPanel g, double s)
        {
            if (analyserGraphs == null) return;
            if (g == null) { analyserGraphs.IsVisible = false; analyserGraphsFor = null; return; }
            if (ReferenceEquals(g, analyserGraphsFor)) { analyserGraphs.IsVisible = true; return; }
            analyserGraphsFor = g;
            bool speed = DrawGraph(g, s, false, anaSpeedCanvas, anaSpeedTitle, anaSpeedLegend);
            anaGapBlock.IsVisible = speed && DrawGraph(g, s, true, anaGapCanvas, anaGapTitle, anaGapLegend);
            if (anaGapBlock.IsVisible) OneKey(anaSpeedLegend, anaGapLegend);
            analyserGraphs.IsVisible = speed;
        }

        // ---- ship card tiles, lap-by-lap bars, intro / outro card, pictures
        private WrapPanel analyserTiles;
        private StackPanel analyserBars, coverLines;
        private string analyserTilesFor, analyserBarsFor, coverFor, lastPicture;
        private Border coverBox;
        private Border filmBox;
        private TextBlock filmLap, filmClock, filmSpeed;

        private void UpdateFilmStrip(HudModel.FilmStripPanel f, IBrush fill)
        {
            if (filmBox == null) return;
            filmBox.IsVisible = f != null;
            if (f == null) return;
            filmBox.Background = fill;
            filmLap.Text = f.Lap ?? ""; filmLap.IsVisible = !string.IsNullOrEmpty(f.Lap);
            filmClock.Text = f.Clock ?? ""; filmClock.IsVisible = !string.IsNullOrEmpty(f.Clock);
            filmSpeed.Text = f.Speed ?? "";
        }
        private TextBlock coverKicker, coverTitle, coverSubtitle;

        private void UpdateTiles(List<(string Label, string Value, HudColor Colour)> tiles, double s, bool small = false)
        {
            if (analyserTiles == null) return;
            analyserTiles.IsVisible = tiles != null && tiles.Count > 0;
            if (!analyserTiles.IsVisible) { analyserTilesFor = null; return; }
            string key = small + string.Join("|", tiles.Select(t => t.Label + "=" + t.Value + t.Colour.G));
            if (key == analyserTilesFor) return;
            analyserTilesFor = key;
            analyserTiles.Children.Clear();
            foreach (var t in tiles)
            {
                var label = Label(12 * s, Grey);
                label.Text = t.Label;
                label.LetterSpacing = 1.5 * s;
                var value = Label((small ? (t.Value.Length > 10 ? 15 : 20) : t.Value.Length > 16 ? 18 : 24) * s, Brush(t.Colour.WithAlpha(255)), FontWeight.Bold);
                value.Text = t.Value;
                value.TextTrimming = TextTrimming.CharacterEllipsis;
                analyserTiles.Children.Add(new Border
                {
                    Width = (small ? 128 : 268) * s, Margin = new Thickness(0, 0, (small ? 6 : 10) * s, (small ? 4 : 10) * s),
                    Padding = small ? new Thickness(9 * s, 5 * s, 9 * s, 6 * s) : new Thickness(12 * s, 8 * s, 12 * s, 10 * s),
                    Background = Brush(150, 12, 32, 48), BorderBrush = Brush(255, 30, 70, 100), BorderThickness = new Thickness(1 * s, 1 * s, 1 * s, 1 * s),
                    CornerRadius = new CornerRadius(5 * s),
                    Child = new StackPanel { Spacing = 2 * s, Children = { label, value } },
                });
            }
        }

        private void UpdateBars(List<(string Label, string Value, double Fraction, HudColor Colour)> bars, double s)
        {
            if (analyserBars == null) return;
            analyserBars.IsVisible = bars != null && bars.Count > 0;
            if (!analyserBars.IsVisible) { analyserBarsFor = null; return; }
            string key = string.Join("|", bars.Select(b => b.Label + b.Value + (int)(b.Fraction * 1000) + b.Colour.G));
            if (key == analyserBarsFor) return;
            analyserBarsFor = key;
            analyserBars.Children.Clear();
            const double full = 360;
            foreach (var b in bars)
            {
                var label = Label(14 * s, Grey);
                label.Text = b.Label;
                label.Width = 70 * s;
                label.VerticalAlignment = VerticalAlignment.Center;
                var bar = new Border
                {
                    Width = Math.Max(6, full * Math.Clamp(b.Fraction, 0, 1)) * s, Height = 18 * s, CornerRadius = new CornerRadius(3 * s),
                    Background = Brush(b.Colour.WithAlpha(255)), VerticalAlignment = VerticalAlignment.Center,
                };
                var track = new Border { Width = full * s, Height = 18 * s, CornerRadius = new CornerRadius(3 * s), Background = Brush(90, 255, 255, 255), Child = bar, VerticalAlignment = VerticalAlignment.Center };
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                var value = Label(14 * s, White);
                value.Text = b.Value;
                value.Margin = new Thickness(10 * s, 0, 0, 0);
                value.VerticalAlignment = VerticalAlignment.Center;
                analyserBars.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { label, track, value } });
            }
        }

        private void UpdateCover(HudModel.TitleCard c, double s, IBrush fill)
        {
            if (coverBox == null) return;
            coverBox.IsVisible = c != null;
            if (c == null) { coverFor = null; return; }
            coverBox.Background = fill;
            string key = c.Kicker + "|" + c.Title + "|" + c.Subtitle + "|" + string.Join("|", c.Lines) + c.TitleColour.G;
            if (key == coverFor) return;
            coverFor = key;
            coverKicker.Text = c.Kicker ?? "";
            coverTitle.Text = c.Title ?? "";
            coverTitle.Foreground = Brush(c.TitleColour.WithAlpha(255));
            coverSubtitle.Text = c.Subtitle ?? "";
            coverLines.Children.Clear();
            foreach (string line in c.Lines)
            {
                var t = Label(18 * s, Grey);
                t.Text = line;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                coverLines.Children.Add(t);
            }
        }

        /// <summary>Saves what a control shows as a PNG (twice the size it's laid out at, for a sharp picture).</summary>
        private static void SavePicture(Control target, string path)
        {
            try
            {
                double w = target.Bounds.Width, h = target.Bounds.Height;
                if (w < 2 || h < 2) { PluginLog.Info("Spotlight picture skipped (nothing drawn): " + path); return; }
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w * 2), (int)Math.Ceiling(h * 2)), new Vector(192, 192));
                bmp.Render(target);
                bmp.Save(path);
                PluginLog.Info("Spotlight picture saved: " + path);
            }
            catch (Exception e) { PluginLog.Info("Spotlight picture not saved: " + e.Message); }
        }

        private void UpdateAnalyser(HudModel.AnalyserPanel a, double s, IBrush fill)
        {
            // Intro / outro: the title card instead of the panel.
            bool cover = a?.Cover != null;
            UpdateCover(cover ? a.Cover : null, s, fill);
            if (a?.ExportPath != null && a.ExportPath != lastPicture)
            {
                lastPicture = a.ExportPath;
                Control target = cover ? coverBox : analyserBox;
                string path = a.ExportPath;
                // After the page has been laid out and drawn.
                Dispatcher.UIThread.Post(() => SavePicture(target, path), DispatcherPriority.Background);
            }
            if (cover)
            {
                analyserBox.IsVisible = false;
                UpdateAnalyserMap(null, s);
                UpdateAnalyserGraphs(null, s);
                analyserFor = null;
                return;
            }
            analyserBox.IsVisible = a != null;
            UpdateAnalyserMap(a?.Map, s);
            UpdateAnalyserGraphs(a?.Graphs, s);
            UpdateTiles(a?.Card, s, a != null && a.CardSmall);
            UpdateBars(a?.Bars, s);
            if (a == null) { analyserFor = null; return; }
            analyserBox.Background = fill;
            // With the game's own HUD showing, its toolbar runs along the bottom: stop above it (the panel shrinks to fit).
            double analyserBottom = (hideGameHud ? 20 : 112) * s;
            if (Math.Abs(analyserBox.Margin.Bottom - analyserBottom) > 0.5) analyserBox.Margin = new Thickness(0, 20 * s, 30 * s, analyserBottom);
            var key = new System.Text.StringBuilder(a.Heading).Append(a.TitleA).Append(a.TitleB).Append(a.Hint);
            foreach (var r in a.Rows) key.Append(r.Label).Append(r.A).Append(r.B).Append(r.ColourB.G).Append(r.ColourA.G);
            foreach (var rb in a.RowBars) key.Append(rb.Key).Append(':').Append((int)(rb.Value.Fraction * 200)).Append(rb.Value.Colour.G);
            if (a.ThrustG != null) foreach (double tg in a.ThrustG) key.Append(tg.ToString("0.00"));
            string k = key.ToString();
            if (k == analyserFor) return;
            analyserFor = k;
            analyserTitle.Text = a.Heading ?? "SHIP ANALYSER";
            SetKeyText(analyserHint, a.Hint ?? "");
            analyserHint.IsVisible = !string.IsNullOrEmpty(a.Hint);
            // Leaderboard rows: where each one was (its row, by name), so a row that changes place slides there.
            boardWasRow.Clear();
            double boardStep = 0;
            foreach (var child in analyserGrid.Children)
                if (child is Border was && was.Tag is string wasTag && wasTag.StartsWith("board:"))
                {
                    boardWasRow[wasTag] = Grid.GetRow(was);
                    if (was.Bounds.Height > 1) boardStep = was.Bounds.Height + was.Margin.Top + was.Margin.Bottom;
                }
            boardMoves.Clear();
            boardSlideSeconds = a.RowSlideSeconds;
            analyserGrid.Children.Clear();
            analyserGrid.RowDefinitions.Clear();
            analyserGrid.ColumnDefinitions.Clear();
            bool two = a.TitleB != null;
            analyserGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            analyserGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            if (two) analyserGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            void Cell(string text, int row, int col, IBrush brush, bool bold = false, double size = 14)
            {
                var t = Label(size * s, brush, bold ? FontWeight.Bold : FontWeight.Normal);
                t.Text = text ?? "";
                t.Margin = new Thickness(col == 0 ? 0 : 16 * s, 1 * s, 0, 1 * s);
                if (col > 0) t.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(t, row); Grid.SetColumn(t, col);
                analyserGrid.Children.Add(t);
            }
            int rowNo = 0;
            if (a.Rows.Count > 0)
            {
                analyserGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Cell(a.TitleA, rowNo, 1, Brush(new HudColor(255, 210, 60, 255)), true, 15);
                if (two) Cell(a.TitleB, rowNo, 2, Brush(new HudColor(80, 200, 255, 255)), true, 15);
                rowNo++;
            }
            else
            {
                analyserGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Cell(a.TitleA, rowNo++, 0, White, true, 15);
            }
            int span = two ? 3 : 2, modelRow = -1;
            foreach (var r in a.Rows)
            {
                modelRow++;
                analyserGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (r.Label != null && r.Label.StartsWith("#"))
                {
                    // A section: its name over a thin line, with a short summary on the right.
                    var name = Label(12 * s, Brush(255, 120, 200, 235), FontWeight.Bold);
                    name.Text = r.Label.Substring(1);
                    name.LetterSpacing = 1.5 * s;
                    var sumText = Label(12 * s, Brush(r.ColourA.WithAlpha(255)), FontWeight.Bold);
                    sumText.Text = r.A ?? "";
                    sumText.HorizontalAlignment = HorizontalAlignment.Right;
                    var headRow = new Grid { Children = { name, sumText } };
                    var head = new Border
                    {
                        Child = headRow, BorderBrush = Brush(255, 52, 70, 86), BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 9 * s, 0, 3 * s), Padding = new Thickness(0, 0, 0, 2 * s),
                    };
                    Grid.SetRow(head, rowNo); Grid.SetColumnSpan(head, span);
                    analyserGrid.Children.Add(head);
                    rowNo++;
                    continue;
                }
                if (r.Label != null && r.Label.StartsWith("!"))
                {
                    // A tip: a boxed callout across the panel.
                    var tag = Label(11 * s, Brush(255, 20, 16, 4), FontWeight.Bold);
                    tag.Text = r.Label.Substring(1);
                    var tagBox = new Border { Child = tag, Background = Brush(HudColor.Yellow), CornerRadius = new CornerRadius(2 * s), Padding = new Thickness(5 * s, 0, 5 * s, 1 * s), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2 * s, 8 * s, 0) };
                    var tipText = Label(13 * s, Brush(255, 245, 225, 150));
                    tipText.Text = r.A ?? "";
                    tipText.TextWrapping = TextWrapping.Wrap;
                    tipText.MaxWidth = 430 * s;
                    var tipRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    Grid.SetColumn(tipText, 1);
                    tipRow.Children.Add(tagBox); tipRow.Children.Add(tipText);
                    var tip = new Border
                    {
                        Child = tipRow, Background = Brush(255, 44, 38, 14), BorderBrush = Brush(255, 150, 124, 40), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3 * s), Padding = new Thickness(8 * s, 5 * s, 8 * s, 6 * s), Margin = new Thickness(0, 5 * s, 0, 1 * s),
                    };
                    Grid.SetRow(tip, rowNo); Grid.SetColumnSpan(tip, span);
                    analyserGrid.Children.Add(tip);
                    rowNo++;
                    continue;
                }
                if (r.Label != null && r.Label.StartsWith("%"))
                {
                    // A leaderboard row: place badge, who, the gap, the time; a thin pace bar under it; this race's lit.
                    int bar = r.Label.IndexOf('|');
                    int.TryParse(bar > 1 ? r.Label.Substring(1, bar - 1) : "", out int place);
                    bool own = r.ColourA.B > 200 && r.ColourA.R < 150;   // (cyan: the race being shown)
                    IBrush medal = place == 1 ? Brush(255, 255, 205, 70) : place == 2 ? Brush(255, 200, 208, 216) : place == 3 ? Brush(255, 215, 140, 80) : Brush(255, 58, 70, 82);
                    var num = Label(13 * s, place <= 3 ? Brush(255, 16, 14, 8) : Brush(255, 205, 212, 218), FontWeight.Bold);
                    num.Text = place.ToString();
                    num.HorizontalAlignment = HorizontalAlignment.Center;
                    num.VerticalAlignment = VerticalAlignment.Center;
                    var badge = new Border { Child = num, Background = medal, CornerRadius = new CornerRadius(3 * s), Width = 26 * s, Height = 22 * s, Margin = new Thickness(0, 0, 10 * s, 0), VerticalAlignment = VerticalAlignment.Center };
                    var who = Label((place == 1 ? 16 : 14) * s, own ? Brush(HudColor.Cyan) : White, place <= 3 || own ? FontWeight.Bold : FontWeight.Normal);
                    who.Text = bar >= 0 ? r.Label.Substring(bar + 1) : r.Label.Substring(1);
                    who.VerticalAlignment = VerticalAlignment.Center;
                    who.TextTrimming = TextTrimming.CharacterEllipsis;
                    var gapText = Label(12 * s, place == 1 ? Brush(255, 255, 205, 70) : Grey);
                    gapText.Text = (r.B ?? "").ToUpperInvariant();
                    gapText.VerticalAlignment = VerticalAlignment.Center;
                    gapText.Margin = new Thickness(12 * s, 0, 12 * s, 0);
                    var time = Label((place == 1 ? 17 : 15) * s, own ? Brush(HudColor.Cyan) : White, FontWeight.Bold);
                    time.Text = r.A ?? "";
                    time.VerticalAlignment = VerticalAlignment.Center;
                    var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
                    Grid.SetColumn(who, 1); Grid.SetColumn(gapText, 2); Grid.SetColumn(time, 3);
                    line.Children.Add(badge); line.Children.Add(who); line.Children.Add(gapText); line.Children.Add(time);
                    double share = a.RowBars.TryGetValue(modelRow, out var pace) ? Math.Clamp(pace.Fraction, 0, 1) : 0;
                    // (The bar's left end stands for 80% of the fastest pace, so close times still look different.)
                    double shown = Math.Clamp((share - 0.8) / 0.2, 0.02, 1);
                    var paceGrid = new Grid { Height = 3 * s, Margin = new Thickness(0, 4 * s, 0, 0), ColumnDefinitions = new ColumnDefinitions($"{shown:0.###}*,{1 - shown:0.###}*") };
                    paceGrid.Children.Add(new Border { Background = Brush(255, 34, 42, 50), CornerRadius = new CornerRadius(1.5 * s), [Grid.ColumnSpanProperty] = 2 });
                    paceGrid.Children.Add(new Border { Background = own ? Brush(HudColor.Cyan) : medal, CornerRadius = new CornerRadius(1.5 * s) });
                    var card = new Border
                    {
                        Child = new StackPanel { Children = { line, paceGrid } },
                        Background = own ? Brush(255, 20, 46, 58) : Brush(255, 20, 25, 30), CornerRadius = new CornerRadius(3 * s),
                        BorderBrush = own ? Brush(HudColor.Cyan) : Brush(255, 40, 50, 60), BorderThickness = new Thickness(1),
                        Padding = new Thickness(8 * s, 5 * s, 10 * s, 5 * s), Margin = new Thickness(0, 3 * s, 0, 0), MinWidth = 500 * s,
                    };
                    Grid.SetRow(card, rowNo); Grid.SetColumnSpan(card, span);
                    card.Tag = "board:" + who.Text;
                    if (boardStep > 0 && boardWasRow.TryGetValue((string)card.Tag, out int wasRow) && wasRow != rowNo)
                    {
                        // It was in another row a moment ago: drawn there, then slid to its new one.
                        var slide = new TranslateTransform { Y = (wasRow - rowNo) * boardStep };
                        card.RenderTransform = slide;
                        if (own) card.ZIndex = 1;   // (the one climbing passes over the others)
                        boardMoves.Add((slide, slide.Y));
                    }
                    analyserGrid.Children.Add(card);
                    rowNo++;
                    continue;
                }
                if (r.Label == "@THRUST" && a.ThrustG != null)
                {
                    var diagram = ThrustDiagram(a.ThrustG, s);
                    Grid.SetRow(diagram, rowNo); Grid.SetColumnSpan(diagram, span);
                    analyserGrid.Children.Add(diagram);
                    rowNo++;
                    continue;
                }
                if (a.RowBars.TryGetValue(modelRow, out var rowBar) && !two)
                {
                    // The value with a bar before it (a share of the bar's full width).
                    Cell(r.Label, rowNo, 0, Grey);
                    var valueText = Label(14 * s, Brush(r.ColourA.WithAlpha(255)), FontWeight.Bold);
                    valueText.Text = r.A ?? "";
                    valueText.VerticalAlignment = VerticalAlignment.Center;
                    double full = 110 * s;
                    var barTrack = new Border { Width = full, Height = 5 * s, Background = Brush(255, 36, 44, 52), CornerRadius = new CornerRadius(2 * s), VerticalAlignment = VerticalAlignment.Center };
                    var barFill = new Border
                    {
                        Width = Math.Max(2 * s, full * Math.Clamp(rowBar.Fraction, 0, 1)), Height = 5 * s, HorizontalAlignment = HorizontalAlignment.Left,
                        Background = Brush(rowBar.Colour.WithAlpha(255)), CornerRadius = new CornerRadius(2 * s), VerticalAlignment = VerticalAlignment.Center,
                    };
                    var barCell = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16 * s, 1 * s, 0, 1 * s),
                        Children = { new Grid { Margin = new Thickness(0, 0, 10 * s, 0), Children = { barTrack, barFill } }, valueText },
                    };
                    Grid.SetRow(barCell, rowNo); Grid.SetColumn(barCell, 1);
                    analyserGrid.Children.Add(barCell);
                    rowNo++;
                    continue;
                }
                Cell(r.Label, rowNo, 0, Grey);
                Cell(r.A, rowNo, 1, Brush(r.ColourA.WithAlpha(255)), true);
                if (two) Cell(r.B, rowNo, 2, Brush(r.ColourB.WithAlpha(255)), true);
                rowNo++;
            }
            StartBoardSlide();
        }

        /// <summary>
        /// Thrust as a picture: two crosses, one seen from above (forward up the page, left and right) and one from the
        /// side (up and down), each arm a bar as long as that way's acceleration against the strongest, with its g.
        /// </summary>
        // ---- Leaderboard rows sliding to a new place.
        private readonly Dictionary<string, int> boardWasRow = new Dictionary<string, int>();
        private readonly List<(TranslateTransform Slide, double From)> boardMoves = new List<(TranslateTransform, double)>();
        private readonly System.Diagnostics.Stopwatch boardClock = new System.Diagnostics.Stopwatch();
        private DispatcherTimer boardTimer;
        private double boardSlideSeconds = 0.45;

        private void StartBoardSlide()
        {
            if (boardMoves.Count == 0) return;
            boardClock.Restart();
            if (boardTimer == null)
            {
                boardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                boardTimer.Tick += (_, __) =>
                {
                    double t = Math.Min(1, boardClock.Elapsed.TotalSeconds / Math.Max(0.1, boardSlideSeconds));
                    double eased = t * t * (3 - 2 * t);   // gently away, gently into place
                    double left = 1 - eased;
                    foreach (var move in boardMoves) move.Slide.Y = move.From * left;
                    if (t >= 1) { boardTimer.Stop(); boardMoves.Clear(); }
                };
            }
            boardTimer.Start();
        }

        private Control ThrustDiagram(double[] g, double s)
        {
            double most = 0;
            foreach (double v in g) most = Math.Max(most, v);
            IBrush[] cols = { Brush(255, 80, 230, 120), Brush(255, 255, 90, 80), Brush(255, 90, 170, 255), Brush(255, 90, 170, 255), Brush(255, 255, 210, 60), Brush(255, 255, 210, 60) };
            string[] names = { "FWD", "BACK", "LEFT", "RIGHT", "UP", "DOWN" };
            double arm = 62 * s, thick = 9 * s, hub = 7 * s;
            Canvas Cross(int[] slots, (double X, double Y)[] ways, string caption)
            {
                double w = 2 * arm + 150 * s, h = 2 * arm + 62 * s, cx = w / 2, cy = h / 2 + 4 * s;
                var c = new Canvas { Width = w, Height = h };
                var cap = Label(10 * s, Grey);
                cap.Text = caption;
                cap.Measure(Size.Infinity);
                Canvas.SetLeft(cap, cx - cap.DesiredSize.Width / 2); Canvas.SetTop(cap, 0);
                c.Children.Add(cap);
                for (int i = 0; i < slots.Length; i++)
                {
                    int k = slots[i];
                    var (dx, dy) = ways[i];
                    double len = most > 0 ? arm * g[k] / most : 0;
                    // The arm's full length, faint; then as much of it as this way has.
                    foreach (var (l, brush) in new[] { (arm, (IBrush)Brush(255, 36, 44, 52)), (len, cols[k]) })
                    {
                        if (l <= 0.5) continue;
                        var bar = new Border { Background = brush, CornerRadius = new CornerRadius(2 * s), Width = dx != 0 ? l : thick, Height = dy != 0 ? l : thick };
                        Canvas.SetLeft(bar, dx > 0 ? cx + hub : dx < 0 ? cx - hub - l : cx - thick / 2);
                        Canvas.SetTop(bar, dy > 0 ? cy + hub : dy < 0 ? cy - hub - l : cy - thick / 2);
                        c.Children.Add(bar);
                    }
                    var t = Label(11 * s, g[k] > 0 ? White : Grey, FontWeight.Bold);
                    t.Text = g[k] > 0 ? $"{names[k]} {g[k]:0.00} g" : $"{names[k]} none";
                    t.Measure(Size.Infinity);
                    double tw = t.DesiredSize.Width, th = t.DesiredSize.Height;
                    Canvas.SetLeft(t, dx > 0 ? cx + hub + arm + 5 * s : dx < 0 ? cx - hub - arm - 5 * s - tw : cx - tw / 2);
                    Canvas.SetTop(t, dy > 0 ? cy + hub + arm + 2 * s : dy < 0 ? cy - hub - arm - th - 2 * s : cy - th / 2);
                    c.Children.Add(t);
                }
                var dot = new Border { Width = hub * 1.4, Height = hub * 1.4, CornerRadius = new CornerRadius(hub), Background = Brush(255, 200, 206, 212) };
                Canvas.SetLeft(dot, cx - hub * 0.7); Canvas.SetTop(dot, cy - hub * 0.7);
                c.Children.Add(dot);
                return c;
            }
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2 * s, 0, 4 * s),
                Children =
                {
                    Cross(new[] { 0, 1, 2, 3 }, new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) }, "FROM ABOVE"),
                    Cross(new[] { 4, 5 }, new[] { (0.0, -1.0), (0.0, 1.0) }, "UP / DOWN"),
                },
            };
        }

        private static bool HasGyro(Visual v, int depth)
        {
            if (depth > 14) return false;
            foreach (var c in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(v))
                if (c.GetType().Name == "CockpitGyroScreen" || HasGyro(c, depth + 1)) return true;
            return false;
        }

        private static readonly Dictionary<Type, System.Reflection.FieldInfo> gyroFields = new Dictionary<Type, System.Reflection.FieldInfo>();

        /// <summary>Switches the game's gyro to target based (when that's turned on).</summary>
        private void SetGyroMode(Visual gyro)
        {
            if (!gyroTargetBased) return;
            try
            {
                foreach (object target in new object[] { gyro, (gyro as StyledElement)?.DataContext })
                {
                    if (target == null) continue;
                    // An Avalonia property on the view, or a plain property on its view model (looked up once per type).
                    var type = target.GetType();
                    if (!gyroFields.TryGetValue(type, out var field))
                        gyroFields[type] = field = type.GetField("TargetBasedGyroProperty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                    if (field?.GetValue(null) is AvaloniaProperty ap && target is AvaloniaObject ao)
                    {
                        if (!Equals(ao.GetValue(ap), true)) ao.SetValue(ap, true);
                        return;
                    }
                    var prop = target.GetType().GetProperty("TargetBasedGyro");
                    if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                    {
                        if (!(bool)prop.GetValue(target)) prop.SetValue(target, true);
                        return;
                    }
                }
                if (!loggedGyro) { loggedGyro = true; PluginLog.Info("Gyro: couldn't find its target based switch"); }
            }
            catch (Exception e)
            {
                if (!loggedGyro) { loggedGyro = true; PluginLog.Error("Gyro: couldn't switch to target based", e); }
            }
        }

        private static bool IsScreen(Visual v)
        {
            for (var t = v.GetType(); t != null; t = t.BaseType) if (t.Name == "ScreenView") return true;
            return false;
        }

        // Where the HUD update's time goes, part by part (in the log's one-a-second summary while tracing).
        private long hudLapAt;
        private void HudLap(string part)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            PluginLog.Time(part, (now - hudLapAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            hudLapAt = now;
        }

        private void Update(HudModel m)
        {
            if (m.KeepGyro != keepGyro || m.GyroTargetBased != gyroTargetBased)
            {
                keepGyro = m.KeepGyro;
                gyroTargetBased = m.GyroTargetBased;
                if (hideGameHud) SetHideGameHud(false);   // put everything back, then hide again with the new choice
            }
            SetHideGameHud(m.HideGameHud);
            UpdateToolbarPeek(m.HideGameHud ? m.ToolbarPeek : -1);
            miniGravity = m.GravityG;
            miniWanted = m.HideGameHud && m.MiniToolbar;
            miniIconShare = m.MiniToolbarScale;
            if (miniWanted)
            {
                if (miniTimer == null) miniTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, __) => MiniTick());
                if (!miniTimer.IsEnabled) miniTimer.Start();
            }
            else miniTimer?.Stop();
            MiniTick();
            double s = builtScale;
            IBrush fill = Brush(m.BackgroundOpacity, 12, 15, 18);
            hudFill = fill;
            timerBox.Background = fill;
            infoBox.Background = fill;
            bestCard.Background = fill;
            lapsBox.Background = fill;
            raceBack.Background = fill;
            // (Not while a cinematic's black bars or fade are up: those are part of this overlay and must stay solid.)
            double hudOpacity = m.CineFade > 0.001 || m.CineBars > 0.001 ? 1 : Math.Clamp(m.Opacity, 0.2, 1);
            if (Math.Abs(root.Opacity - hudOpacity) > 0.004) root.Opacity = hudOpacity;
            centreBox.Background = fill;
            splitsBox.Background = fill;
            statsBox.Background = fill;
            resultsBox.Background = fill;
            PlaceTimer(m.TimerInCorner, m.SwapSides, s);
            if (!m.TimerInCorner && statsStack != null)
            {
                statsStack.HorizontalAlignment = statsBox.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                statsStack.Margin = m.SwapSides ? new Thickness(30 * s, 30 * s, 0, 0) : new Thickness(0, 30 * s, 30 * s, 0);
            }
            cornerFit.HorizontalAlignment = cornerColumn.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Left : HorizontalAlignment.Right;

            // Sides: lap list + results left and splits right, or swapped.
            leftColumn.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            leftFit.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            if (!m.TimerInCorner)
            {
                splitsBox.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                splitsBox.Margin = m.SwapSides ? new Thickness(30 * s, 0, 0, 0) : new Thickness(0, 0, 30 * s, 0);
            }
            minimapBox.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Right : HorizontalAlignment.Left;

            HudLap("hud: toolbar bar");
            UpdateMarkers(m, s);
            HudLap("hud: markers");

            // Flash: timer border turns green (best lap) or red (penalty) briefly.
            // (The same thin edge, coloured, on the timer and its message tab: no thick frame.)
            timerBox.BorderBrush = raceBack.BorderBrush = m.Flash.HasValue ? Brush(m.Flash.Value) : BorderBrush;

            // Timer
            var t = m.Timer;
            Appear(timerBox, t != null);
            // The hint box fades out instead of vanishing; its last text stays while it fades.
            infoBox.Opacity = t?.Hint != null ? 1 : 0;
            bestLapBox.IsVisible = t?.BestLap != null;
            bestRaceBox.IsVisible = t?.BestRace != null;
            bestDivider.IsVisible = bestLapBox.IsVisible && bestRaceBox.IsVisible;
            Appear(bestCard, bestLapBox.IsVisible || bestRaceBox.IsVisible);
            // (The card is only as tall as its two rows; beside the minimap it sits at the top.)
            if (t != null)
            {
                string labelLeft = t.Label, labelRight = null;
                int cpSplit = (t.Label ?? "").IndexOf("   CP ", StringComparison.Ordinal);
                if (cpSplit > 0) { labelLeft = t.Label.Substring(0, cpSplit); labelRight = t.Label.Substring(cpSplit + 3); }
                tLabel.Text = labelLeft;
                tLabelRight.Text = labelRight ?? "";
                tLabelRight.IsVisible = labelRight != null;
                tLabelRow.HorizontalAlignment = labelRight != null ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                tBig.Text = t.Big;
                tBig.Foreground = Brush(t.BigColour);
                double bigSize = t.Compact ? 30 : 44;
                if (bigSize != tBigSize) { tBigSize = bigSize; tBig.FontSize = bigSize * s; }
                tTag.IsVisible = t.Tag != null;
                if (t.Tag != null)
                {
                    tTagText.Text = t.Tag;
                    tTagText.Foreground = tTag.BorderBrush = Brush(t.TagColour);
                }
                // The keys as rows: kKey boxes down the left (right-aligned to each other), kWhat each does beside it.
                tKeys.IsVisible = t.Keys != null;
                string keysSig = t.Keys == null ? null : string.Join("|", t.Keys.Select(k => k.Key + ":" + k.Words + (k.Dim ? "0" : "1")));
                if (keysSig != tKeysDrawn)
                {
                    tKeysDrawn = keysSig;
                    tKeys.Children.Clear();
                    tKeys.RowDefinitions.Clear();
                    if (t.Keys != null)
                        for (int kr = 0; kr < t.Keys.Count; kr++)
                        {
                            var (kKey, kWords, kDim) = t.Keys[kr];
                            tKeys.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                            IBrush kInk = kDim ? Brush(255, 110, 120, 130) : White;
                            var kCaps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2 * s, 8 * s, 2 * s) };
                            foreach (string kOne in kKey.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var kCap = KeyCap(kOne.Trim(), 16 * s, kInk);
                                if (kCap is Border capBox && kDim) capBox.BorderBrush = Brush(255, 80, 90, 100);
                                kCaps.Children.Add(kCap);
                            }
                            var kWhat = Label(15 * s, kDim ? Brush(255, 110, 120, 130) : Brush(255, 215, 221, 226));
                            kWhat.Text = kWords;
                            kWhat.VerticalAlignment = VerticalAlignment.Center;
                            Grid.SetRow(kCaps, kr);
                            Grid.SetRow(kWhat, kr); Grid.SetColumn(kWhat, 1);
                            tKeys.Children.Add(kCaps);
                            tKeys.Children.Add(kWhat);
                        }
                }
                // Corner layout (narrow timer): one hint per line, so none is cut off; a short footer (total and
                // penalty) stays on one line.
                bool narrow = placedKey != null && placedKey.StartsWith("True");
                var footerParts = (t.Footer ?? "").Split(new[] { "   " }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                bool footerFits = footerParts.Sum(x => x.Length) + 5 * Math.Max(0, footerParts.Count - 1) <= 34;
                bool totalLine = footerParts.Count >= 1 && footerParts.Count <= 2 && footerParts[0].StartsWith("TOTAL", StringComparison.Ordinal)
                                 && (footerParts.Count == 1 || footerParts[1].StartsWith("PENALTY", StringComparison.Ordinal));
                if (totalLine) SetKeyText(tFooter, footerParts[0]);
                else SetKeyText(tFooter, narrow && !footerFits ? string.Join("\n", footerParts) : narrow ? string.Join("     ", footerParts) : t.Footer ?? "");
                tFooter.TextAlignment = TextAlignment.Center;
                tFooter.HorizontalAlignment = totalLine ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                tFooter.IsVisible = t.Footer != null;
                tFooterRight.IsVisible = totalLine && footerParts.Count == 2;
                if (tFooterRight.IsVisible) tFooterRight.Text = footerParts[1];
                tSub.IsVisible = t.Sub != null;
                if (t.Sub != null && tSub.Text != t.Sub) tSub.Text = t.Sub;
                tStartBar.IsVisible = t.StartBar.HasValue;
                if (t.StartBar.HasValue)
                {
                    double startWide = Math.Max(0, tStartBar.Bounds.Width) * Math.Clamp(t.StartBar.Value, 0, 1);
                    if (Math.Abs(tStartFill.Width - startWide) > 0.5) tStartFill.Width = startWide;
                }
                if (t.Hint != null)
                {
                    SetKeyText(tHint, t.Hint);
                    tHint.Foreground = Brush(t.HintColour);
                    tHint.FontWeight = t.Hint.StartsWith("GO!") ? FontWeight.Bold : FontWeight.Normal;
                }
                bestLapValue.Text = t.BestLap ?? "";
                bestLapNote.Text = t.BestLapNote ?? "";
                bestLapNote.IsVisible = t.BestLapNote != null;
                bestRaceNote.Text = t.BestRaceNote ?? "";
                bestRaceNote.IsVisible = t.BestRaceNote != null;
                bestRaceValue.Text = t.BestRace ?? "";

                tDelta.IsVisible = t.Delta != null;
                tDelta.Text = t.Delta ?? "";
                tDelta.Foreground = Brush(t.DeltaColour);
                // (Room for the gap on the right, and the same on the left so the time stays in the middle.)
                double deltaRoom = t.Delta != null ? 72 * s : 0;
                if (bigRow.ColumnDefinitions[0].MinWidth != deltaRoom)
                    bigRow.ColumnDefinitions[0].MinWidth = bigRow.ColumnDefinitions[2].MinWidth = deltaRoom;

                int n = t.Dots?.Length ?? 0;
                dots.IsVisible = n > 0;
                while (dots.Children.Count < n) dots.Children.Add(new Ellipse { Width = 10 * s, Height = 10 * s, StrokeThickness = 1.5 * s });
                while (dots.Children.Count > n) dots.Children.RemoveAt(dots.Children.Count - 1);
                for (int i = 0; i < n; i++)
                {
                    var e = (Ellipse)dots.Children[i];
                    switch (t.Dots[i])
                    {
                        case HudModel.DotState.Hit:
                            var dc = t.DotColours != null && i < t.DotColours.Length && t.DotColours[i].HasValue ? t.DotColours[i].Value : HudColor.Green;
                            e.Fill = Brush(dc); e.Stroke = Brush(dc); break;
                        case HudModel.DotState.Next: e.Fill = Brush(HudColor.Orange); e.Stroke = White; break;
                        case HudModel.DotState.Missed: e.Fill = Brushes.Transparent; e.Stroke = Brush(HudColor.Red); break;
                        default: e.Fill = Brushes.Transparent; e.Stroke = Grey; break;
                    }
                }
            }

            HudLap("hud: timer");
            // Splits
            var sp = m.Splits;
            Appear(splitsBox, sp != null);
            string spSig = null;
            if (sp != null)
            {
                var sb = new System.Text.StringBuilder(sp.Title).Append(string.Join(",", sp.Headers)).Append('|');
                foreach (var r in sp.Rows)
                {
                    sb.Append(r.Name).Append(r.NameColour.R).Append(r.NameColour.G);
                    foreach (var c in r.Cells) sb.Append(c.Text).Append(c.Colour.R).Append(c.Colour.G).Append(c.Colour.B).Append(',');
                    sb.Append('|');
                }
                spSig = sb.ToString();
            }
            if (sp != null && spSig != splitsDrawn)
            {
                splitsDrawn = spSig;
                // Rebuilt only when the table changes: heading row, then one row per checkpoint.
                splitsHeader.Text = sp.Title;
                splitRows.Children.Clear();
                // Column widths fit what's in them (with room for the longest time the column can get), so there's
                // no big gap between the checkpoint names and the first column.
                int cols = sp.Headers.Count;
                foreach (var r in sp.Rows) cols = Math.Max(cols, r.Cells.Count);
                var widths = new double[cols + 1];
                int nameChars = 4;
                foreach (var r in sp.Rows) nameChars = Math.Max(nameChars, (r.Name ?? "").Length);
                widths[0] = nameChars * 8.6 * s + 12 * s;
                for (int c = 0; c < cols; c++)
                {
                    string h = c < sp.Headers.Count ? sp.Headers[c] ?? "" : "";
                    int chars = h == "GAP" || h == "VS BEST" ? 7 : h == "SECTOR" ? 6 : 9;   // "+12.345", "12.345", "01:32.384"
                    chars = Math.Max(chars, (int)Math.Ceiling(h.Length * 0.95));
                    foreach (var r in sp.Rows) if (c < r.Cells.Count) chars = Math.Max(chars, (r.Cells[c].Text ?? "").Length);
                    widths[c + 1] = chars * 8.6 * s + 12 * s;
                }
                if (sp.Headers.Count > 0)
                {
                    var head = SplitGrid(widths);
                    for (int c = 0; c < sp.Headers.Count; c++)
                        AddCell(head, c + 1, sp.Headers[c], Grey, 14 * s);
                    splitRows.Children.Add(head);
                }
                foreach (var r in sp.Rows)
                {
                    var g = SplitGrid(widths);
                    // During the race: the checkpoint you're heading for has a lit row with a bar at its left; the
                    // ones still to come are dimmed right down.
                    bool rowNext = r.NameColour.R == HudColor.Orange.R && r.NameColour.G == HudColor.Orange.G && r.NameColour.B == HudColor.Orange.B;
                    bool rowLater = r.NameColour.R == HudColor.Grey.R && r.NameColour.G == HudColor.Grey.G && r.NameColour.B == HudColor.Grey.B && r.NameColour.A == HudColor.Grey.A;
                    if (rowNext)
                    {
                        var rowLit = new Border { Background = Brush(255, 52, 44, 30), CornerRadius = new CornerRadius(2 * s), Margin = new Thickness(-8 * s, 0, -6 * s, 0) };
                        Grid.SetColumnSpan(rowLit, widths.Length);
                        g.Children.Add(rowLit);
                        var rowBar = new Border { Width = 3 * s, Background = Brush(HudColor.Orange), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8 * s, 1 * s, 0, 1 * s) };
                        g.Children.Add(rowBar);
                    }
                    bool rowMissed = r.NameColour.R == HudColor.Red.R && r.NameColour.G == HudColor.Red.G && r.NameColour.B == HudColor.Red.B;
                    if (rowMissed)
                    {
                        var rowRed = new Border { Background = Brush(255, 56, 24, 24), CornerRadius = new CornerRadius(2 * s), Margin = new Thickness(-8 * s, 0, -6 * s, 0) };
                        Grid.SetColumnSpan(rowRed, widths.Length);
                        g.Children.Add(rowRed);
                    }
                    if (rowLater) g.Opacity = 0.45;
                    if (r.Name == "LAP" && sp.Headers.Count > 0 && sp.Headers[0] == "GAP")
                    {
                        // (The lap's running gap: parted from the checkpoints by a thin line.)
                        var lapLine = new Border { Height = 1, Background = Brush(255, 60, 72, 84), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4 * s, -1 * s, -4 * s, 0) };
                        Grid.SetColumnSpan(lapLine, widths.Length);
                        g.Children.Add(lapLine);
                        g.Margin = new Thickness(0, 4 * s, 0, 0);
                    }
                    var name = Label(15 * s, Brush(r.NameColour), rowNext ? FontWeight.Bold : FontWeight.Normal);
                    name.Text = r.Name;
                    name.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(name, 0);
                    g.Children.Add(name);
                    for (int c = 0; c < r.Cells.Count; c++)
                        AddCell(g, c + 1, r.Cells[c].Text, Brush(r.Cells[c].Colour), 15 * s);
                    splitRows.Children.Add(g);
                }
            }

            // Race stats box: two columns of label / value.
            var rs = m.RaceStats;
            var course = m.Course;
            statsBox.IsVisible = rs != null || course != null;
            if (course == null) courseDrawn = null;
            else
            {
                rs = null;
                statsDrawn = null;
                string cSig = course.Summary + course.Summary2 + course.More + string.Join("|", course.Legs.Select(l => l.Name + l.Distance + l.Aimed))
                    + string.Join("|", course.Warnings.Select(x => x.Label + x.Text));
                if (cSig != courseDrawn)
                {
                    courseDrawn = cSig;
                    statsTitle.Text = "COURSE";
                    statsGrid.Children.Clear();
                    statsGrid.ColumnDefinitions.Clear();
                    statsGrid.RowDefinitions.Clear();
                    // Number / legName, a legBar for the leg's length against the longest, the length.
                    foreach (double cw in new double[] { 46, 190, 84 }) statsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cw * s, GridUnitType.Pixel)));
                    int cRow = 0;
                    void Whole(Control c) { statsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Grid.SetRow(c, cRow++); Grid.SetColumnSpan(c, 3); statsGrid.Children.Add(c); }
                    var cSum = Label(13 * s, White, FontWeight.Bold);
                    cSum.Text = course.Summary;
                    cSum.HorizontalAlignment = HorizontalAlignment.Center;
                    Whole(cSum);
                    if (course.Summary2 != null)
                    {
                        var sum2 = Label(12 * s, Brush(HudColor.Yellow));
                        sum2.Text = course.Summary2;
                        sum2.HorizontalAlignment = HorizontalAlignment.Center;
                        Whole(sum2);
                    }
                    if (course.Legs.Count > 0) Whole(new Border { Height = 1, Background = Brush(255, 60, 72, 84), Margin = new Thickness(0, 5 * s, 0, 4 * s) });
                    foreach (var leg in course.Legs)
                    {
                        statsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                        if (leg.Aimed)
                        {
                            // (The gate being aimed at: its cRow legLit.)
                            var legLit = new Border { Background = Brush(255, 44, 60, 74), CornerRadius = new CornerRadius(2 * s), Margin = new Thickness(-4 * s, 0, -4 * s, 0) };
                            Grid.SetRow(legLit, cRow); Grid.SetColumnSpan(legLit, 3);
                            statsGrid.Children.Add(legLit);
                        }
                        var legName = Label(12 * s, Brush(leg.Colour), FontWeight.Bold);
                        legName.Text = leg.Name.StartsWith("CP ") ? leg.Name.Substring(3) : leg.Name.ToUpperInvariant();
                        legName.VerticalAlignment = VerticalAlignment.Center;
                        legName.Margin = new Thickness(0, 2 * s, 0, 2 * s);
                        var legTrack = new Border { Height = 4 * s, Background = Brush(255, 36, 44, 52), CornerRadius = new CornerRadius(2 * s), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10 * s, 0) };
                        var legBar = new Border
                        {
                            Height = 4 * s, Width = Math.Max(3 * s, 180 * s * Math.Clamp(leg.Share, 0, 1)), HorizontalAlignment = HorizontalAlignment.Left,
                            Background = leg.Aimed ? White : Brush(255, 150, 162, 172), CornerRadius = new CornerRadius(2 * s), VerticalAlignment = VerticalAlignment.Center,
                        };
                        var legDist = Label(13 * s, leg.Aimed ? White : Brush(255, 200, 206, 212));
                        legDist.Text = leg.Distance;
                        legDist.HorizontalAlignment = HorizontalAlignment.Right;
                        legDist.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetRow(legName, cRow);
                        Grid.SetRow(legTrack, cRow); Grid.SetColumn(legTrack, 1);
                        Grid.SetRow(legBar, cRow); Grid.SetColumn(legBar, 1);
                        Grid.SetRow(legDist, cRow); Grid.SetColumn(legDist, 2);
                        statsGrid.Children.Add(legName); statsGrid.Children.Add(legTrack); statsGrid.Children.Add(legBar); statsGrid.Children.Add(legDist);
                        cRow++;
                    }
                    if (course.More != null) { var cMore = Label(12 * s, Grey); cMore.Text = course.More; Whole(cMore); }
                    foreach (var (warnLabel, warnText) in course.Warnings)
                    {
                        var wt = Label(12 * s, Brush(HudColor.Orange));
                        wt.Text = warnLabel.ToUpperInvariant() + "  " + warnText;
                        wt.TextWrapping = TextWrapping.Wrap;
                        wt.Margin = new Thickness(0, 3 * s, 0, 0);
                        Whole(wt);
                    }
                }
            }
            string rsSig = rs == null ? null : m.RaceStatsTitle + m.RaceStatsOneColumn + string.Join("|", rs.ConvertAll(x => x.Label + x.Value + x.Colour.R + x.Colour.G));
            if (rs != null && rsSig != statsDrawn)
            {
                statsDrawn = rsSig;
                statsTitle.Text = m.RaceStatsTitle ?? "RACE STATS";
                statsGrid.Children.Clear();
                statsGrid.ColumnDefinitions.Clear();
                statsGrid.RowDefinitions.Clear();
                double[] widths = m.RaceStatsOneColumn ? new double[] { 150, 210 } : new double[] { 108, 150, 14, 108, 150 };
                foreach (var w in widths) statsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w * s, GridUnitType.Pixel)));
                int rows = m.RaceStatsOneColumn ? rs.Count : (rs.Count + 1) / 2;
                for (int r = 0; r < rows; r++) statsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int k = 0; k < rs.Count; k++)
                {
                    int row = k % rows, col = k < rows ? 0 : 3;
                    var l = Label(12 * s, Grey);
                    l.Text = rs[k].Label;
                    l.VerticalAlignment = VerticalAlignment.Center;
                    var v = Label(14 * s, Brush(rs[k].Colour));
                    v.Text = rs[k].Value;
                    // Long values (a ship's name) wrap onto a second line instead of being cut off.
                    v.TextWrapping = TextWrapping.Wrap;
                    Grid.SetRow(l, row); Grid.SetColumn(l, col);
                    Grid.SetRow(v, row); Grid.SetColumn(v, col + 1);
                    statsGrid.Children.Add(l);
                    statsGrid.Children.Add(v);
                }
            }

            // Results (fades in at the finish)
            var res = m.Results;
            if (res != null)
            {
                resultsTitle.Text = res.Title;
                resultsSubtitle.Text = res.Subtitle ?? "";
                resultsSubtitle.IsVisible = !string.IsNullOrEmpty(res.Subtitle);
                SetKeyText(resultsFooter, res.Footer ?? "");
                resultsFooter.IsVisible = res.Footer != null;
                resultsHeadline.Text = res.Headline ?? "";
                resultsHeadline.IsVisible = res.Headline != null;
                resultsHeadline.Foreground = Brush(res.HeadlineColour);
                while (resultLines.Children.Count < res.Lines.Count) resultLines.Children.Add(NewResultLine(s));
                while (resultLines.Children.Count > res.Lines.Count) resultLines.Children.RemoveAt(resultLines.Children.Count - 1);
                for (int i = 0; i < res.Lines.Count; i++)
                {
                    var (label, value, colour) = res.Lines[i];
                    var g = (Grid)resultLines.Children[i];
                    ((TextBlock)g.Children[0]).Text = label;
                    var v = (TextBlock)g.Children[1];
                    v.Text = value;
                    v.Foreground = Brush(colour);
                }
            }
            UpdateGraph(res, s);
            if (graphsBox != null)
            {
                graphsBox.Background = fill;
                graphsBox.Opacity = res != null ? 1 : 0;
                // On the side away from the minimap and lap list.
                graphsBox.HorizontalAlignment = placedSwap ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            }
            resultsBox.Opacity = res != null ? 1 : 0;
            // Corner layout: beside the splits, so it mustn't take up room while there are no results.
            if (placedKey != null && placedKey.StartsWith("True")) resultsBox.IsVisible = res != null;

            mmResultsUp = m.Results != null;
            HudLap("hud: splits, stats, results");
            UpdateMinimap(m.Minimap, s, fill);
            HudLap("hud: minimap");

            ShowAnalyser(m.Analyser, s, m.Analyser != null && m.Analyser.Exporting ? Brush(255, 8, 18, 28) : fill);
            UpdateCineDebug(m.CineDebug, s, fill);
            UpdateFilmStrip(m.FilmStrip, fill);
            UpdateCineOverlay(m.CineFade, m.CineBars);

            // Speedometer and flight keys
            Appear(speedoBox, m.Speedo != null);
            // (While the results show, the speedometer's bars and indicators are left out: with the graphs they were more
            // shape than the game's UI can draw in one frame, which crashed the game.)
            if (m.Speedo != null) { speedoParts = m.Results != null ? 0 : m.SpeedoParts; UpdateShipTabs(s); UpdateShipBars(s); UpdateSafeSpeed(m.SpeedMs); if (lightIcon != null && lightIcon.IsVisible != m.LightsOn) lightIcon.IsVisible = m.LightsOn; }
            // The panels on the left side (lap list, results...) stop above the dial instead of running under it:
            // their bottom margin grows to clear it, and they're scaled down to fit if needed.
            {
                // With the game's own HUD showing, its status panels sit in both bottom corners: stop above them too.
                double baseBottom = (m.TimerInCorner || !m.HideGameHud ? 260 : 30) * s;
                // (The resource rings reach out past the dial's top: clear those too.)
                double ringsOver = Math.Max(0, outlineRadius - dialSize / 2);
                // (The dial sits the same 40 in from the corner as the other panels, measured from its outer ring.)
                double speedoIn = 40 * s + ringsOver;
                if (Math.Abs(speedoBox.Margin.Left - speedoIn) > 0.5 || Math.Abs(speedoBox.Margin.Bottom - speedoIn) > 0.5)
                    speedoBox.Margin = new Thickness(speedoIn, 0, 0, speedoIn);
                double clear = m.Speedo != null ? speedoIn + dialSize + ringsOver + 16 * s : 0;
                bool leftOnLeft = !m.SwapSides;                       // leftFit is on the left unless swapped
                bool cornerOnLeft = m.TimerInCorner && m.SwapSides;   // cornerFit is on the left when swapped
                double lb = leftOnLeft ? Math.Max(baseBottom, clear) : baseBottom;
                double cb = cornerOnLeft ? Math.Max(baseBottom, clear) : baseBottom;
                if (Math.Abs(leftFit.Margin.Bottom - lb) > 0.5) leftFit.Margin = new Thickness(30 * s, 30 * s, 30 * s, lb);
                if (Math.Abs(cornerFit.Margin.Bottom - cb) > 0.5) cornerFit.Margin = new Thickness(30 * s, 30 * s, 30 * s, cb);
            }
            if (m.Speedo != null)
            {
                double max = Math.Max(1, m.SpeedoMax);
                if (max != dialFor || !ReferenceEquals(fill, dialFill)) BuildDial(max, s, fill);
                double frac = Math.Clamp(m.SpeedoValue / max, 0, 1);
                double r = dialSize / 2 - 14 * s;
                double deg = DialStart + DialSweep * Math.Max(frac, 0.002);
                dialArc.Data = DialArc(DialStart, deg, r);
                // White, then orange near the top, red in the last tenth.
                dialArc.Stroke = frac >= 0.9 ? Brush(HudColor.Red) : frac >= 0.75 ? Brush(255, 255, 160, 40) : Brush(255, 90, 210, 255);
                dialTip.StartPoint = DialPoint(deg, r - 26 * s);
                dialTip.EndPoint = DialPoint(deg, r + 7 * s);
                speedoValue.Text = m.Speedo;
                // Smaller for three digits and more, so it can't run into the scale numbers beside and above it. The
                // room it frees is padding above (mostly) and below, so the unit and the rest don't jump about.
                int digits = (m.Speedo ?? "").Count(char.IsDigit);
                double size = digits <= 2 ? 52 : digits == 3 ? 42 : 34;
                if (Math.Abs(speedoValue.FontSize - size * s) > 0.01)
                {
                    speedoValue.FontSize = size * s;
                    double room = (52 - size) * s * 1.2;
                    speedoValue.Padding = new Thickness(0, room * 0.65, 0, room * 0.35);
                }
                speedoUnit.Text = (m.SpeedoUnit ?? "").ToUpperInvariant();
                speedoGroundBox.IsVisible = m.GroundHeight != null;
                speedoOther.IsVisible = speedoOtherUnit.IsVisible = m.SpeedoOther != null;
                if (m.SpeedoOther != null && speedoOther.Text != m.SpeedoOther) speedoOther.Text = m.SpeedoOther;
                if (m.SpeedoOtherUnit != null && speedoOtherUnit.Text != m.SpeedoOtherUnit) speedoOtherUnit.Text = m.SpeedoOtherUnit;
                // Height above the ground, and beside it above sea level: "179 m / 1.2 km" over "GROUND / SEA LEVEL".
                // (Sea level: the game's own altimeter number when its flight HUD has one, so the two always agree.)
                string seaText = m.SeaHeight == null ? null : GameAltitude() ?? m.SeaHeight;
                string heights = m.GroundHeight == null ? null : seaText != null ? m.GroundHeight + " / " + seaText : m.GroundHeight;
                if (heights != null && speedoGround.Text != heights) speedoGround.Text = heights;
                string heightsLabel = m.SeaHeight != null ? "GROUND / SEA LEVEL" : "ABOVE GROUND";
                if (groundLabel != null && groundLabel.Text != heightsLabel) groundLabel.Text = heightsLabel;
            }
            bool attitude = m.Roll.HasValue || m.Pitch.HasValue;
            HudLap("hud: speedometer");
            Appear(rollBox, attitude);
            // Gravity: left of the attitude panel (or in its place), joined to it.
            {
                bool gravOn = m.GravityG.HasValue && m.Speedo != null;
                Appear(gravBox, gravOn);
                if (gravOn)
                {
                    gravBox.Background = fill;
                    double gravRight = (m.KeysCorner == 2 ? 30 + KeysPanelWidth - 1 : 30) * s + (attitude && rollBox.Bounds.Width > 1 ? rollBox.Bounds.Width - 1 : 0);
                    var gravMargin = new Thickness(0, 0, gravRight, !m.HideGameHud ? 260 * s : 40 * s);
                    if (gravBox.Margin != gravMargin) gravBox.Margin = gravMargin;
                    double gravNow = m.GravityG.Value;
                    string gravTotal = $"{gravNow:0.00} g";
                    if (gravValue.Text != gravTotal) gravValue.Text = gravTotal;
                    GravityText();   // (reads the natural / artificial split from the game's HUD, a few times a second)
                    bool gravBoth = gravityNatural > 0.005 && gravityArtificial > 0.005;
                    string gravParts = gravNow < 0.005 ? "none" : gravBoth ? "" : gravityArtificial > 0.005 ? "artificial" : "natural";
                    if (gravSplit.Text != gravParts) gravSplit.Text = gravParts;
                    if (gravSplit.IsVisible != !gravBoth) gravSplit.IsVisible = !gravBoth;
                    if (gravPair.IsVisible != gravBoth) gravPair.IsVisible = gravBoth;
                    if (gravBoth)
                    {
                        string textN = $"N {gravityNatural:0.00}", textA = $"A {gravityArtificial:0.00}";
                        if (gravTextN.Text != textN) gravTextN.Text = textN;
                        if (gravTextA.Text != textA) gravTextA.Text = textA;
                    }
                    // The arrows: each pull across the ship (right / up), seen from behind, as long as it's strong.
                    // The overall one is white; natural (green) and artificial (orange) show when there's both.
                    bool gravPartsOn = m.GravityParts && m.GravityNatG > 0.005 && m.GravityArtG > 0.005;
                    double gravStrongest = Math.Max(gravNow, gravPartsOn ? Math.Max(m.GravityNatG, m.GravityArtG) : 0);
                    AimGravArrow(gravArrowN, ref gravArrowKeyN, gravPartsOn, m.GravityNatX, m.GravityNatY, m.GravityNatG, gravStrongest, s);
                    AimGravArrow(gravArrowA, ref gravArrowKeyA, gravPartsOn, m.GravityArtX, m.GravityArtY, m.GravityArtG, gravStrongest, s);
                    bool gravArrowOn = AimGravArrow(gravArrow, ref gravArrowFor, true, m.GravityX, m.GravityY, gravNow, gravStrongest, s);
                    // (Pulling mostly along the ship's nose or tail there's little to point at: a dot instead.)
                    bool gravDotOn = gravNow >= 0.005 && !gravArrowOn;
                    if (gravDot.IsVisible != gravDotOn) gravDot.IsVisible = gravDotOn;
                }
            }
            if (attitude)
            {
                // The horizon: turned against the roll, and down when the nose is up (2 degrees to the pixel, held in the picture).
                double horizonRoll = Math.Round(m.Roll ?? 0), horizonPitch = Math.Round(m.Pitch ?? 0);
                if (horizonRoll != horizonRollShown || horizonPitch != horizonPitchShown)
                {
                    horizonRollShown = horizonRoll; horizonPitchShown = horizonPitch;
                    // (Steep: the ship mark turns yellow, then red; the numbers under it stay white.)
                    double steep = Math.Max(Math.Abs(horizonRoll), Math.Abs(horizonPitch));
                    var shipTint = steep > 60 ? Brush(HudColor.Red) : steep > 30 ? Brush(HudColor.Yellow) : Brush(HudColor.Orange);
                    foreach (var shipPart in horizonShip) { if (shipPart is Line) shipPart.Stroke = shipTint; else shipPart.Fill = shipTint; }
                    // The ground's side of the horizon: "down" turned against the roll; the line slid along it by the pitch.
                    double tiltRad = -horizonRoll * Math.PI / 180;
                    double downX = -Math.Sin(tiltRad), downY = Math.Cos(tiltRad);
                    double discR = HorizonSize / 2 * s, slide = Math.Clamp(horizonPitch * 0.5, -(HorizonSize / 2 - 5), HorizonSize / 2 - 5) * s;
                    // The line's ends on the circle (a chord), then round the circle's edge on the ground's side back to the start.
                    double chordHalf = Math.Sqrt(Math.Max(0, discR * discR - slide * slide));
                    double midX = discR + downX * slide, midY = discR + downY * slide;
                    var chordA = new Point(midX + downY * chordHalf, midY - downX * chordHalf);
                    var chordB = new Point(midX - downY * chordHalf, midY + downX * chordHalf);
                    var groundShape = new StreamGeometry();
                    using (var groundCtx = groundShape.Open())
                    {
                        groundCtx.BeginFigure(chordA, true);
                        groundCtx.LineTo(chordB);
                        groundCtx.ArcTo(chordA, new Size(discR, discR), 0, slide < 0, SweepDirection.CounterClockwise);
                        groundCtx.EndFigure(true);
                    }
                    horizonGround.Data = groundShape;
                    horizonLine.StartPoint = chordA;
                    horizonLine.EndPoint = chordB;
                }
                rollCanvasBox.IsVisible = true;
                rollText.IsVisible = m.Roll.HasValue;
                pitchText.IsVisible = m.Pitch.HasValue;
                rollBox.Background = fill;
                rollBox.Margin = new Thickness(0, 0, (m.KeysCorner == 2 ? 30 + KeysPanelWidth - 1 : 30) * s, !m.HideGameHud ? 260 * s : 40 * s);
            }
            if (m.Pitch.HasValue)
            {
                double pitchDeg = Math.Round(m.Pitch.Value);
                if (pitchDeg != pitchShown)
                {
                    pitchShown = pitchDeg;
                    pitchText.Text = Math.Abs(pitchDeg) < 0.5 ? "P 0°" : $"P {Math.Abs(pitchDeg):0}° {(pitchDeg > 0 ? "UP" : "DN")}";

                }
            }
            if (m.Roll.HasValue)
            {
                rollBox.Background = fill;
                // Left of the flight keys when they're showing (both sit in the bottom right corner).
                rollBox.Margin = new Thickness(0, 0, (m.KeysCorner == 2 ? 30 + KeysPanelWidth - 1 : 30) * s, !m.HideGameHud ? 260 * s : 40 * s);
                double rollDeg = Math.Round(m.Roll.Value);
                double rollRate = Math.Round(m.RollRate);
                if (rollDeg != rollShown || rollRate != rollRateShown)
                {
                    rollShown = rollDeg;
                    rollRateShown = rollRate;
                    double rollAbs = Math.Abs(rollDeg);
                    rollText.Text = rollAbs < 0.5 ? "R 0°" : $"R {rollAbs:0}° {(rollDeg > 0 ? "R" : "L")}";
                    rollRateBox.IsVisible = Math.Abs(rollRate) >= 0.5;
                    if (rollRateBox.IsVisible)
                    {
                        rollRateText.Text = $"{Math.Abs(rollRate):0}°/s";
                        rollRateBox.Measure(Size.Infinity);
                        Canvas.SetLeft(rollRateBox, (HorizonSize * s - rollRateBox.DesiredSize.Width) / 2);
                    }
                    rollText.TextAlignment = TextAlignment.Center;
                }
            }
            Appear(keysBox, m.KeysCorner > 0);
            if (m.KeysCorner > 0)
            {
                keysBox.Background = fill;
                keysBox.HorizontalAlignment = m.KeysCorner == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                // Bottom left with the speedometer there too: the keys sit just above the dial.
                keysBox.Margin = new Thickness(30 * s, 0, 30 * s, m.KeysCorner == 1 && m.Speedo != null ? 40 * s + dialSize + 10 * s
                    : !m.HideGameHud ? 260 * s : 40 * s);   // above the game's ship status panel when that's showing
                if (m.KeysDown != keysShown)
                {
                    keysShown = m.KeysDown;
                    var lit = Brush(HudColor.Orange.WithAlpha(230));
                    for (int k = 0; k < keyCells.Count; k++)
                    {
                        bool down = (m.KeysDown & (1 << k)) != 0;
                        keyCells[k].Background = down ? lit : Brushes.Transparent;
                        ((TextBlock)keyCells[k].Child).Foreground = down ? Brushes.Black : White;
                    }
                }
            }
            else keysShown = -1;
            // Both showing: one panel, the keys' box straight on to the attitude's (square where they meet).
            {
                bool joined = attitude && m.KeysCorner == 2;
                double joinCorner = PanelCorner * s;
                rollBox.CornerRadius = joined ? new CornerRadius(joinCorner, 0, 0, joinCorner) : new CornerRadius(joinCorner);
                keysBox.CornerRadius = joined ? new CornerRadius(0, joinCorner, joinCorner, 0) : new CornerRadius(joinCorner);
            }

            HudLap("hud: attitude, gravity, keys");
            // Countdown
            if (m.Countdown != null) { countdownText.Text = m.Countdown; countdownText.Foreground = Brush(m.CountdownColour); }
            countdownText.Opacity = m.Countdown != null ? 1 : 0;

            // Laps
            var laps = m.Laps;
            Appear(lapsBox, laps != null);
            if (laps != null)
            {
                lapsHeader.Text = laps.Header;
                // Reuse row controls; add or remove to match the count.
                while (lapRows.Children.Count < laps.Rows.Count) lapRows.Children.Add(NewLapRow(s));
                while (lapRows.Children.Count > laps.Rows.Count) lapRows.Children.RemoveAt(lapRows.Children.Count - 1);
                for (int i = 0; i < laps.Rows.Count; i++)
                {
                    var r = laps.Rows[i];
                    var g = (Grid)lapRows.Children[i];
                    var name = (TextBlock)g.Children[0];
                    var notePanel = (StackPanel)g.Children[1];
                    var time = (TextBlock)g.Children[2];
                    IBrush colour = Brush(r.Colour);
                    name.Text = r.Name; name.Foreground = colour;
                    time.Text = r.Time; time.Foreground = colour;
                    bool lapNow = r.Colour.R == HudColor.Grey.R && r.Colour.G == HudColor.Grey.G && r.Colour.A == HudColor.Grey.A;
                    bool lapBest = r.Colour.R == HudColor.Green.R && r.Colour.G == HudColor.Green.G && r.Colour.B == HudColor.Green.B;
                    var lapLitNow = (Border)g.Children[3];
                    var lapBarNow = (Border)g.Children[4];
                    lapLitNow.IsVisible = lapNow;
                    lapBarNow.IsVisible = lapNow || lapBest;
                    if (lapBarNow.IsVisible) lapBarNow.Background = lapNow ? Brush(HudColor.Orange) : Brush(HudColor.Purple);
                    if (lapNow) name.Foreground = time.Foreground = White;
                    var note = (TextBlock)notePanel.Children[0];
                    note.Text = r.Note ?? ""; note.Foreground = Brush(r.NoteColour);
                    // Penalty icons: a red ring per missed checkpoint, a red cross per crash.
                    while (notePanel.Children.Count > 1) notePanel.Children.RemoveAt(notePanel.Children.Count - 1);
                    // (A small dark red chip each for missed checkpoints and crashes: its icon and how many.)
                    void PenaltyChip(Control chipIcon, int howMany)
                    {
                        var countText = Label(12 * s, White, FontWeight.Bold);
                        countText.Text = howMany.ToString();
                        countText.VerticalAlignment = VerticalAlignment.Center;
                        notePanel.Children.Add(new Border
                        {
                            Background = Brush(255, 62, 24, 24), BorderBrush = Brush(255, 140, 58, 54), BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(3 * s), Padding = new Thickness(5 * s, 1 * s, 5 * s, 1 * s), VerticalAlignment = VerticalAlignment.Center,
                            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 * s, Children = { chipIcon, countText } },
                        });
                    }
                    if (r.MissedIcons > 0)
                        PenaltyChip(new Ellipse { Width = 9 * s, Height = 9 * s, Stroke = Brush(HudColor.Red), StrokeThickness = 1.6 * s, VerticalAlignment = VerticalAlignment.Center }, r.MissedIcons);
                    if (r.CrashIcons > 0)
                        PenaltyChip(new Path
                        {
                            Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                            Width = 9 * s, Height = 9 * s, Stretch = Stretch.Fill,
                            Stroke = Brush(HudColor.Red), StrokeThickness = 1.8 * s,
                            VerticalAlignment = VerticalAlignment.Center,
                        }, r.CrashIcons);
                }
            }

            HudLap("hud: laps");
            // Messages: fade in/out (text is kept while fading out)
            if (m.RaceMessage != null) { raceText.Text = m.RaceMessage.Text; raceText.Foreground = Brush(m.RaceMessage.Colour); }
            // Tab height: one line, or more when the message wraps in the tab's width.
            double tabH = raceDropHeight;
            if (raceDrop.Width > 0)
            {
                // The text gets a fixed width that clears the tab's slanted sides (8% of the width each side at the
                // bottom), so a long message wraps onto another line instead of running past the edge and being cut off.
                double textW = Math.Max(40 * raceScale, raceDrop.Width - 28 * raceScale);
                if (double.IsNaN(raceText.Width) || Math.Abs(raceText.Width - textW) > 0.5) raceText.Width = textW;
                raceText.InvalidateMeasure();
                // (The text's margins are part of its measured size.)
                raceText.Measure(new Size(raceDrop.Width, double.PositiveInfinity));
                tabH = Math.Max(raceDropHeight, raceText.DesiredSize.Height);
            }
            raceTab.Height = tabH;
            raceDrop.Height = m.RaceMessage != null ? tabH : 0;
            bool tabOut = m.RaceMessage != null;
            if (tabOut != timerSquare)
            {
                timerSquare = tabOut;
                double boxCorner = PanelCorner * s;
                timerBox.CornerRadius = tabOut ? new CornerRadius(boxCorner, boxCorner, 0, 0) : new CornerRadius(boxCorner);
            }
            // Edge glow (its last colour stays while it fades out).
            if (m.EdgeGlow.HasValue && (!edgeGlowFor.HasValue || edgeGlowFor.Value.R != m.EdgeGlow.Value.R || edgeGlowFor.Value.G != m.EdgeGlow.Value.G || edgeGlowFor.Value.B != m.EdgeGlow.Value.B))
            {
                edgeGlowFor = m.EdgeGlow;
                byte[] glowAlphas = { 84, 62, 44, 30, 18, 9 };
                for (int frame = 0; frame < edgeGlow.Children.Count && frame < glowAlphas.Length; frame++)
                    ((Border)edgeGlow.Children[frame]).BorderBrush = Brush(m.EdgeGlow.Value.WithAlpha(glowAlphas[frame]));
            }
            edgeGlow.Opacity = m.EdgeGlow.HasValue ? 1 : 0;
            // Checkpoint flash (its last text stays while it fades out).
            if (m.CpFlash != null)
            {
                string[] flashParts = (m.CpFlash.Text ?? "").Split('|');
                cpFlashName.Text = flashParts[0];
                cpFlashGap.Text = flashParts.Length > 1 ? flashParts[1] : "";
                cpFlashGap.IsVisible = flashParts.Length > 1 && flashParts[1].Length > 0;
                cpFlashGap.Foreground = Brush(m.CpFlash.Colour);
                cpFlashBox.Background = Brush((byte)Math.Max(235, (int)m.BackgroundOpacity), 12, 15, 18);
            }
            cpFlashBox.Opacity = m.CpFlash != null ? 1 : 0;
            double flashY = m.CpFlash != null ? 0 : -18 * s;
            if (cpFlashSlide.Y != flashY) cpFlashSlide.Y = flashY;
            if (m.CentreMessage != null) { SetKeyText(centreText, m.CentreMessage.Text); centreText.Foreground = Brush(m.CentreMessage.Colour); }
            centreBox.Opacity = m.CentreMessage != null ? 1 : 0;
        }


        /// <summary>
        /// Results graphs, in their own box on the right: speed round the lap on top and the time gap round
        /// the lap under it. Only drawn again when the results (or which graphs to show) change.
        /// </summary>
        private void UpdateGraph(HudModel.ResultsPanel res, double s)
        {
            if (res == null) { graphsBox.IsVisible = false; graphFor = null; return; }
            if (ReferenceEquals(res, graphFor) && res.GraphVersion == graphVersion && res.GraphsShown == graphPage) return; // already drawn
            graphFor = res;
            graphVersion = res.GraphVersion;
            graphPage = res.GraphsShown;
            // Two graphs (speed, then the time gap), but the words only once: which lap and the key go under the
            // last one, not repeated on each.
            bool speed = res.GraphsShown >= 1 && DrawGraph(res, s, false, graphCanvas, graphTitle, graphLegend);
            bool gap = speed && res.GraphsShown >= 2 && DrawGraph(res, s, true, gapCanvas, gapTitle, gapLegend);
            if (gap) OneKey(graphLegend, gapLegend);
            speedBlock.IsVisible = speed;
            gapBlock.IsVisible = gap;
            graphsBox.IsVisible = speed;
        }

        /// <summary>
        /// One results graph into the canvas given: speed round the lap (this race against the best, shaded green
        /// where you were faster and red where slower), or the time gap round the lap. False if there's nothing to draw.
        /// </summary>
        // ---- Graph headings, laid out in parts: which lap (a tag, then the keys to change it as key caps), then the
        // graph's name on the left and its number on the right, each in its own size and colour.
        private readonly Dictionary<TextBlock, (TextBlock Value, WrapPanel Lap)> graphHeads = new Dictionary<TextBlock, (TextBlock, WrapPanel)>();

        private Control GraphHead(TextBlock title, double s)
        {
            title.FontSize = 12 * s;
            title.FontWeight = FontWeight.Bold;
            title.Foreground = Brush(255, 200, 206, 212);
            title.TextWrapping = TextWrapping.NoWrap;
            title.VerticalAlignment = VerticalAlignment.Center;
            var value = Label(14 * s, White, FontWeight.Bold);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(value, 1);
            row.Children.Add(title);
            row.Children.Add(value);
            var lap = new WrapPanel { Orientation = Orientation.Horizontal, IsVisible = false, Margin = new Thickness(0, 0, 0, 6 * s) };
            graphHeads[title] = (value, lap);
            return new StackPanel { Children = { lap, row } };
        }

        private void SetHead(TextBlock title, string name, string value, IBrush valueBrush, string lap, double s)
        {
            title.Text = name;
            if (!graphHeads.TryGetValue(title, out var head)) { title.Text = name + "   " + value; return; }
            head.Value.Text = value;
            head.Value.Foreground = valueBrush;
            head.Lap.Children.Clear();
            head.Lap.IsVisible = !string.IsNullOrEmpty(lap);
            if (!head.Lap.IsVisible) return;
            // "best lap (L2)   Shift+[ / Shift+]: other laps" -> a tag, then key caps, then the words.
            string which = lap, keys = null, words = null;
            int split = lap.IndexOf("   ", StringComparison.Ordinal);
            if (split > 0) { which = lap.Substring(0, split); keys = lap.Substring(split).Trim(); }
            if (keys != null)
            {
                int colon = keys.LastIndexOf(':');
                if (colon > 0) { words = keys.Substring(colon + 1).Trim(); keys = keys.Substring(0, colon).Trim(); }
            }
            var tagText = Label(12 * s, White, FontWeight.Bold);
            tagText.Text = which.ToUpperInvariant();
            head.Lap.Children.Add(new Border
            {
                Child = tagText, Background = Brush(255, 22, 64, 78), BorderBrush = Brush(200, 90, 210, 240), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3 * s), Padding = new Thickness(7 * s, 1 * s, 7 * s, 2 * s), Margin = new Thickness(0, 0, 10 * s, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (keys != null)
                foreach (string key in keys.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var k = Label(11 * s, White, FontWeight.Bold);
                    k.Text = key.Trim();
                    head.Lap.Children.Add(new Border
                    {
                        Child = k, BorderBrush = Brush(180, 200, 206, 212), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3 * s),
                        Padding = new Thickness(5 * s, 0, 5 * s, 1 * s), Margin = new Thickness(0, 0, 4 * s, 0), VerticalAlignment = VerticalAlignment.Center,
                    });
                }
            if (words != null)
            {
                var w = Label(12 * s, Grey);
                w.Text = words;
                w.VerticalAlignment = VerticalAlignment.Center;
                w.Margin = new Thickness(2 * s, 0, 0, 0);
                head.Lap.Children.Add(w);
            }
        }

        // ---- Key hints with the keys drawn as key caps: "Ctrl+B: this ship's best" shows Ctrl+B in a small outlined
        // box, then the words. Hints are split at wide gaps ("   "); a part only gets key caps when everything before
        // its colon looks like keys ("Ctrl+B", "Shift+[ / Shift+]", "Esc", "Left click"), else it stays plain text.
        private static readonly System.Text.RegularExpressions.Regex KeyLike = new System.Text.RegularExpressions.Regex(
            // (With Ctrl / Shift / Alt: any key; on its own: one character, an F key or a named key, so words like
            // "Best: 1:15" aren't taken for keys.)
            @"^(((Ctrl|Shift|Alt)\+)+\S{1,12}|\S|F\d{1,2}|Esc|Enter|Space|Tab|Home|End|Insert|Delete|Backspace|Left click|Right click|Middle click|Mouse wheel|Wheel|Page Up|Page Down)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private void SetKeyText(TextBlock tb, string text)
        {
            text ??= "";
            if (tb.Tag is string last && last == text) return;
            tb.Tag = text;
            tb.Inlines?.Clear();
            if (text.Length == 0) { tb.Text = ""; return; }
            bool any = false;
            var inlines = new Avalonia.Controls.Documents.InlineCollection();
            string[] lines = text.Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) inlines.Add(new Avalonia.Controls.Documents.LineBreak());
                string[] parts = lines[li].Split(new[] { "   " }, StringSplitOptions.None);
                for (int pi = 0; pi < parts.Length; pi++)
                {
                    string part = parts[pi];
                    if (pi > 0) inlines.Add(new Avalonia.Controls.Documents.Run("   "));
                    int colon = part.IndexOf(": ", StringComparison.Ordinal);
                    string before = colon > 0 ? part.Substring(0, colon).Trim() : null;
                    string[] keys = before?.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries);
                    if (keys == null || keys.Length == 0 || keys.Length > 4 || !keys.All(k => KeyLike.IsMatch(k.Trim())))
                    {
                        inlines.Add(new Avalonia.Controls.Documents.Run(part));
                        continue;
                    }
                    any = true;
                    // (Centred on the line, not sat on the baseline and pushed down: that ran below the line and its bottom was cut off.)
                    foreach (string key in keys)
                        inlines.Add(new Avalonia.Controls.Documents.InlineUIContainer(KeyCap(key.Trim(), tb.FontSize, tb.Foreground)) { BaselineAlignment = BaselineAlignment.Center });
                    inlines.Add(new Avalonia.Controls.Documents.Run(" " + part.Substring(colon + 2)));
                }
            }
            if (!any) { tb.Inlines?.Clear(); tb.Text = text; return; }
            tb.Text = null;
            tb.Inlines = inlines;
        }

        /// <summary>A key in a small outlined box, sized to sit in a line of text of this size.</summary>
        private static Control KeyCap(string key, double fontSize, IBrush brush)
        {
            var t = new TextBlock { Text = key, FontSize = Math.Max(9, fontSize * 0.82), FontWeight = FontWeight.Bold, Foreground = brush ?? Brushes.White };
            return new Border
            {
                // (Outline only, no fill: the same as the keys over the results graphs.)
                Child = t, BorderBrush = UiBrushes.Get(180, 200, 206, 212), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 4, 0),
            };
        }

        // ---- Ship power and dampeners, in a pod on the dial's side. The state comes from the game's cockpit HUD
        // (its view model: GridPowerEnabled, DampenerEnabled); the icons from its own toggle tiles (power is the
        // first, dampeners the last), drawn the same way: orange tile with a dark icon when on, dark when off.
        private Border powerTab, dampTab, shipPod;
        private Border powerIcon, dampIcon;
        private int speedoParts = 63;   // which speedometer extras are on (HudModel.SpeedoParts)
        private bool PartOn(int bit) => (speedoParts & bit) != 0;
        private Path dampOnPath, dampOffPath;   // the dampener icon for each state
        private Grid shipTabs;
        private Border parkTab, antennaTab, parkIcon, antennaIcon;
        private System.Reflection.PropertyInfo parkProp;
        private Func<object, bool?> antennaRead;
        private bool antennaLooked;
        private bool shipTabsIcons;
        private WeakReference<StyledElement> cockpitHudRef;
        private long cockpitSearchMs;
        private System.Reflection.PropertyInfo powerProp, dampProp;
        private bool loggedShipTabs;
        private static readonly IBrush KeenOrange = UiBrushes.Get(255, 232, 140, 24);

        private Border ShipTab(double s, out Border iconHolder)
        {
            iconHolder = new Border { Width = 22 * s, Height = 22 * s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // The game's tile shape: a square with its corners cut off.
            var shape = new Path
            {
                Data = Geometry.Parse("M 6,0 L 28,0 L 34,6 L 34,28 L 28,34 L 6,34 L 0,28 L 0,6 Z"),
                Stretch = Stretch.Fill, StrokeThickness = 1.2 * s,
            };
            return new Border { Width = 34 * s, Height = 34 * s, Child = new Grid { Children = { shape, iconHolder } } };
        }

        private static Path TileShape(Border tab) => (Path)((Grid)tab.Child).Children[0];

        private object CockpitHud()
        {
            if (cockpitHudRef != null && cockpitHudRef.TryGetTarget(out var el) && el is Visual v && v.GetVisualRoot() != null
                && el.DataContext?.GetType().Name == "CockpitHUDScreenViewModel")
                return el.DataContext;
            cockpitHudRef = null;
            long now = Environment.TickCount64;
            if (now - cockpitSearchMs < 1000) return null;   // (looked for once a second at most)
            cockpitSearchMs = now;
            var top = root?.GetVisualRoot() as Visual;
            if (top == null) return null;
            foreach (var d in top.GetVisualDescendants())
                if (d is StyledElement se && se.DataContext != null && se.DataContext.GetType().Name == "CockpitHUDScreenViewModel")
                {
                    cockpitHudRef = new WeakReference<StyledElement>(se);
                    return se.DataContext;
                }
            return null;
        }

        private static bool? ReadFlag(System.Reflection.PropertyInfo p, object o)
        {
            if (p == null || o == null) return null;
            object v = p.GetValue(o);
            if (v is bool b) return b;
            var inner = v?.GetType().GetProperty("Value");
            return inner != null && inner.GetValue(v) is bool ib ? ib : (bool?)null;
        }

        private void UpdateShipTabs(double s)
        {
            object vm = null;
            try { vm = CockpitHud(); } catch { }
            if (vm == null) { shipPod.IsVisible = false; UpdateSpeedoOutline(s); return; }
            var t = vm.GetType();
            powerProp ??= t.GetProperty("GridPowerEnabled");
            dampProp ??= t.GetProperty("DampenerEnabled");
            parkProp ??= t.GetProperty("ParkingBrakeEnabled");
            if (!antennaLooked) { antennaLooked = true; antennaRead = FindFlag(vm, "AntennaEnabled"); }
            bool? power = null, damp = null, park = null, antenna = null;
            try { power = ReadFlag(powerProp, vm); damp = ReadFlag(dampProp, vm); park = ReadFlag(parkProp, vm); antenna = antennaRead?.Invoke(vm); } catch { }
            // (Ones turned off in Settings > HUD are left out; the pod stays for the bars' names if those are on.)
            if (!PartOn(2)) power = null;
            if (!PartOn(4)) damp = null;
            if (!PartOn(8)) park = null;
            if (!PartOn(16)) antenna = null;
            bool anyTile = power.HasValue || damp.HasValue || park.HasValue || antenna.HasValue;
            shipTabs.IsVisible = anyTile;
            shipPod.IsVisible = anyTile || PartOn(1);
            if (!shipPod.IsVisible) { UpdateSpeedoOutline(s); return; }
            UpdateSpeedoOutline(s);
            if (!shipTabsIcons) FindToggleIcons(s);
            void Look(Border tab, Border icon, bool? on)
            {
                tab.IsVisible = on.HasValue;
                if (!on.HasValue) return;
                var shape = TileShape(tab);
                shape.Fill = on.Value ? KeenOrange : Brush(230, 16, 22, 28);
                shape.Stroke = on.Value ? KeenOrange : Brush(200, 150, 160, 170);
                var ink = on.Value ? Brush(255, 12, 12, 12) : Brush(255, 185, 196, 204);
                icon.Background = Brushes.Transparent;
                InkIcon(icon.Child, ink);
            }
            Look(powerTab, powerIcon, power);
            if (damp.HasValue && dampOnPath != null)
            {
                var want = damp.Value ? dampOnPath : dampOffPath;
                if (!ReferenceEquals(dampIcon.Child, want)) dampIcon.Child = want;
            }
            Look(dampTab, dampIcon, damp);
            Look(parkTab, parkIcon, park);
            Look(antennaTab, antennaIcon, antenna);
            if (!loggedShipTabs) { loggedShipTabs = true; PluginLog.Info($"Speedometer tabs: power {power?.ToString() ?? "?"}, dampeners {damp?.ToString() ?? "?"}, parking brake {park?.ToString() ?? "?"}, antenna {antenna?.ToString() ?? "?"} (from the cockpit HUD)"); }
        }

        // ---- The indicator icons: traced from the game's own icon pictures into shapes (the game's renderer won't
        // load pictures from our DLL, so they're drawn as filled outlines on a 24 x 24 grid instead).
        private const string IconPower = "F0 M 12.1,13.9 L 11.2,13.6 L 11.0,12.8 L 11.0,1.0 L 11.7,-0.0 L 12.5,0.0 L 12.9,0.7 L 12.8,13.3 Z M 12.8,23.9 L 11.1,23.9 L 7.6,23.0 L 4.7,21.2 L 2.3,18.3 L 0.9,15.0 L 0.9,10.5 L 2.3,7.1 L 4.8,4.1 L 7.8,2.3 L 8.8,2.7 L 8.8,3.8 L 6.3,5.4 L 4.5,7.2 L 3.2,9.6 L 2.7,11.9 L 3.1,15.5 L 4.0,17.5 L 6.2,19.9 L 7.5,20.8 L 9.7,21.7 L 11.9,22.0 L 15.5,21.2 L 17.2,20.3 L 19.0,18.7 L 20.7,15.7 L 21.2,11.9 L 20.7,9.7 L 19.0,6.7 L 17.1,5.0 L 15.1,3.9 L 15.3,2.6 L 16.0,2.3 L 17.1,2.7 L 19.9,4.9 L 21.7,7.2 L 23.0,10.5 L 23.0,14.8 L 21.2,19.0 L 19.3,21.1 L 17.3,22.5 Z";
        private const string IconPark = "F0 M 3.7,20.9 L 2.8,20.6 L 1.4,18.3 L 0.7,16.4 L -0.0,12.6 L 0.0,10.6 L 1.1,6.1 L 2.5,3.5 L 3.1,2.8 L 3.5,2.8 L 4.0,2.8 L 4.1,3.8 L 2.1,7.8 L 1.5,12.2 L 2.1,15.9 L 4.1,19.8 L 4.1,20.6 Z M 20.3,20.9 L 19.8,20.7 L 19.8,19.7 L 21.1,17.3 L 21.8,15.2 L 22.2,10.8 L 21.4,7.1 L 19.8,3.9 L 19.8,2.8 L 20.0,2.8 L 20.8,2.8 L 22.2,5.0 L 23.2,7.7 L 23.7,10.7 L 23.6,14.3 L 22.9,17.0 L 21.1,20.4 Z M 13.1,19.6 L 10.0,19.4 L 7.2,18.0 L 5.3,15.8 L 4.6,14.3 L 4.2,12.6 L 4.6,9.3 L 6.0,6.7 L 7.1,5.6 L 8.8,4.6 L 11.7,3.9 L 14.3,4.2 L 17.0,5.6 L 19.0,8.0 L 19.7,9.7 L 20.0,11.2 L 19.7,13.9 L 18.3,16.6 L 16.0,18.7 Z M 13.1,18.1 L 15.4,17.3 L 17.3,15.5 L 18.3,13.3 L 18.5,11.4 L 18.0,9.3 L 16.6,7.2 L 14.7,6.0 L 12.1,5.3 L 9.4,5.9 L 7.4,7.4 L 6.0,9.9 L 5.7,12.3 L 6.6,15.1 L 8.3,16.9 L 10.4,18.0 Z M 11.5,14.1 L 11.2,14.0 L 11.3,7.8 L 11.5,7.4 L 12.6,6.6 L 12.8,13.0 Z M 12.6,16.2 L 11.4,16.2 L 11.3,15.2 L 11.5,14.7 L 12.6,14.1 L 12.8,15.8 Z";
        private const string IconAntenna = "F0 M 22.3,16.7 L 21.6,16.2 L 22.2,12.1 L 21.5,8.7 L 20.3,6.4 L 18.6,4.7 L 16.2,3.1 L 13.1,2.2 L 10.1,2.3 L 7.3,3.1 L 6.5,2.7 L 6.9,1.5 L 10.1,0.6 L 13.9,0.6 L 17.5,1.8 L 20.3,3.8 L 22.7,7.1 L 23.9,11.5 L 23.4,15.5 L 23.0,16.4 Z M 18.6,15.7 L 17.8,15.0 L 18.2,12.7 L 17.9,10.5 L 16.3,7.9 L 14.7,6.6 L 12.4,5.9 L 10.7,5.9 L 8.5,6.4 L 7.6,6.0 L 8.0,5.0 L 10.1,4.2 L 13.1,4.2 L 15.5,5.0 L 17.5,6.6 L 19.1,8.9 L 19.9,11.9 L 19.5,14.8 Z M 13.1,23.7 L 9.8,23.7 L 7.6,22.9 L 4.9,21.3 L 2.8,19.2 L 1.3,16.8 L 0.4,14.5 L 0.4,10.8 L 1.4,8.7 L 2.2,8.3 L 3.5,8.3 L 5.5,9.1 L 7.5,10.3 L 7.9,11.6 L 6.7,11.8 L 3.9,10.2 L 2.9,10.0 L 2.5,10.4 L 4.4,14.0 L 6.9,16.9 L 10.7,20.1 L 13.7,21.4 L 14.0,21.2 L 13.9,20.4 L 11.3,16.3 L 11.4,15.3 L 11.7,15.1 L 12.6,15.3 L 13.5,16.3 L 15.1,18.9 L 15.8,21.4 L 15.0,22.9 Z M 8.1,16.1 L 7.3,15.9 L 7.3,14.7 L 11.7,10.3 L 12.8,10.3 L 12.8,11.7 Z";
        private const string IconDampOn = "F0 M 12.9,6.3 L 10.9,6.2 L 10.8,3.8 L 8.4,3.6 L 11.9,0.0 L 15.5,3.6 L 15.4,3.8 L 13.4,3.9 L 13.3,6.1 Z M 16.2,15.8 L 7.8,15.8 L 7.1,15.4 L 7.1,14.5 L 11.4,7.8 L 12.0,7.6 L 12.6,7.8 L 16.9,14.4 L 16.9,15.4 Z M 3.7,16.2 L 0.0,12.4 L 3.6,8.4 L 3.9,10.9 L 6.1,11.0 L 6.3,11.4 L 6.2,13.6 L 3.9,13.7 Z M 20.5,16.1 L 20.2,16.0 L 20.1,13.8 L 17.9,13.7 L 17.7,13.2 L 17.8,11.1 L 20.2,10.8 L 20.4,8.5 L 24.0,12.3 Z M 12.2,12.9 L 12.5,12.5 L 12.7,10.0 L 12.5,9.7 L 11.5,9.7 L 11.3,10.2 L 11.6,12.6 L 11.8,13.0 Z M 12.2,14.8 L 12.6,14.5 L 12.6,13.9 L 11.8,13.7 L 11.4,14.4 L 11.8,14.8 Z M 12.2,23.9 L 11.9,23.9 L 8.5,20.4 L 10.7,20.1 L 10.8,18.0 L 11.1,17.7 L 13.1,17.8 L 13.3,20.1 L 15.5,20.3 Z";
        private const string IconDampOff = "F0 M 12.1,9.6 L 6.6,4.1 L 6.8,3.9 L 10.1,3.8 L 10.2,-0.0 L 13.7,0.0 L 13.8,3.8 L 17.3,4.1 Z M 19.7,17.4 L 19.2,17.2 L 14.2,11.9 L 19.6,6.3 L 19.8,9.9 L 23.4,10.0 L 23.4,10.3 L 23.4,13.7 L 19.8,13.9 Z M 4.5,17.4 L 4.2,17.3 L 4.1,14.0 L 0.5,13.9 L 0.5,13.4 L 0.5,10.1 L 4.1,10.0 L 4.4,6.4 L 9.8,11.9 Z M 13.7,23.9 L 10.3,23.9 L 10.2,20.1 L 6.6,19.9 L 11.9,14.3 L 17.4,19.8 L 17.2,20.0 L 13.8,20.1 Z";
        private const string IconSafe = "F0 M 20.5,22.4 L 20.0,22.4 L 17.4,19.3 L 18.2,18.8 L 19.1,19.6 L 19.6,19.5 L 20.9,17.6 L 21.6,15.3 L 19.9,13.4 L 20.2,13.0 L 21.8,12.8 L 21.4,10.5 L 20.3,8.5 L 19.0,9.2 L 17.4,7.9 L 18.1,6.8 L 17.8,6.2 L 14.1,5.0 L 13.5,5.2 L 13.1,6.8 L 11.0,5.1 L 8.2,6.0 L 7.4,6.8 L 11.2,10.4 L 13.4,11.0 L 15.2,12.8 L 15.8,14.3 L 15.4,15.8 L 14.2,16.9 L 12.8,17.1 L 11.6,16.8 L 10.0,15.3 L 9.2,13.6 L 9.4,11.4 L 7.3,7.1 L 6.7,7.0 L 7.4,8.8 L 6.8,9.3 L 5.8,8.4 L 5.2,8.6 L 4.0,10.3 L 3.3,12.5 L 4.1,13.9 L 4.9,14.1 L 4.9,14.8 L 3.3,15.1 L 3.1,15.5 L 3.6,17.6 L 4.6,19.5 L 5.9,18.8 L 7.5,20.4 L 5.8,22.4 L 5.4,22.4 L 4.2,21.2 L 2.0,18.3 L 0.9,14.3 L 1.5,9.8 L 3.6,6.2 L 2.0,6.0 L 1.8,5.2 L 0.8,4.9 L 1.4,3.8 L 0.0,2.4 L 0.5,1.5 L 0.9,1.5 L 2.2,3.0 L 2.9,2.5 L 3.5,2.6 L 3.7,3.4 L 4.6,3.8 L 4.7,5.3 L 9.2,3.1 L 14.2,3.0 L 17.3,4.1 L 20.9,7.0 L 22.8,9.7 L 23.9,12.3 L 23.9,16.0 L 22.9,19.2 Z";

        private static Path TracedIcon(string data, double s, double size = 22) => new Path
        {
            Data = Geometry.Parse(data), StrokeThickness = 0, Stretch = Stretch.Uniform, Width = size * s, Height = size * s,
        };

        /// <summary>Colours a drawn icon: lines get the ink as their stroke, shapes as their fill (a group, each part).</summary>
        private static void InkIcon(Control icon, IBrush ink)
        {
            if (icon is Path drawn) { if (drawn.StrokeThickness > 0) drawn.Stroke = ink; else drawn.Fill = ink; }
            else if (icon is Panel group) foreach (var part in group.Children) InkIcon(part, ink);
            else if (icon is Viewbox box && box.Child is Control inner) InkIcon(inner, ink);
        }

        // ---- Safe speed: a small gauge icon in the dial's bottom gap while the game says the ship is at a safe
        // speed (its movement HUD's ShowSafeSpeedIndicator).
        private Border safeSpeedIcon;
        // (A headlamp: a rounded lamp with three beams to its right.)
        private Border lightIcon;
        private const string IconLamp = "M 11,4 C 5.5,4 2,7.6 2,12 C 2,16.4 5.5,20 11,20 Z M 13.5,5.6 L 22,3.6 L 22,5.4 L 13.5,7.4 Z M 13.5,11.1 L 22,11.1 L 22,12.9 L 13.5,12.9 Z M 13.5,16.6 L 22,18.6 L 22,20.4 L 13.5,18.4 Z";
        private TextBlock groundLabel;
        private WeakReference<StyledElement> movementHudRef;
        private long movementSearchMs;
        private System.Reflection.PropertyInfo safeSpeedProp;

        // ---- The game's altimeter (its flight HUD's view model, FlightHUDViewModel.Altitude), read so our sea level
        // height is the same number the game shows.
        private WeakReference<StyledElement> flightHudRef;
        private long flightSearchMs;
        private System.Reflection.PropertyInfo altitudeProp;
        private bool loggedAltitude;

        private string GameAltitude()
        {
            try
            {
                object vm = null;
                if (flightHudRef != null && flightHudRef.TryGetTarget(out var el) && el is Visual v && v.GetVisualRoot() != null
                    && el.DataContext?.GetType().Name == "FlightHUDViewModel")
                    vm = el.DataContext;
                else
                {
                    flightHudRef = null;
                    long now = Environment.TickCount64;
                    if (now - flightSearchMs < 1000) return null;   // (looked for once a second at most)
                    flightSearchMs = now;
                    var top = root?.GetVisualRoot() as Visual;
                    if (top == null) return null;
                    foreach (var d in top.GetVisualDescendants())
                        if (d is StyledElement se && se.DataContext != null && se.DataContext.GetType().Name == "FlightHUDViewModel")
                        {
                            flightHudRef = new WeakReference<StyledElement>(se);
                            vm = se.DataContext;
                            break;
                        }
                }
                if (vm == null) return null;
                altitudeProp ??= vm.GetType().GetProperty("Altitude");
                object raw = altitudeProp?.GetValue(vm);
                if (!loggedAltitude) { loggedAltitude = true; PluginLog.Info("Sea level height: the game's altimeter gives " + (raw == null ? "nothing" : raw.GetType().Name + " " + raw)); }
                if (raw == null) return null;
                double a = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsFinite(a)) return null;
                return Math.Abs(a) >= 10000 ? $"{a / 1000:0.0} km" : $"{a:0} m";
            }
            catch { return null; }
        }

        private object MovementHud()
        {
            if (movementHudRef != null && movementHudRef.TryGetTarget(out var el) && el is Visual v && v.GetVisualRoot() != null
                && el.DataContext?.GetType().Name == "MovementHUDScreenViewModel")
                return el.DataContext;
            movementHudRef = null;
            long now = Environment.TickCount64;
            if (now - movementSearchMs < 1000) return null;   // (looked for once a second at most)
            movementSearchMs = now;
            var top = root?.GetVisualRoot() as Visual;
            if (top == null) return null;
            foreach (var d in top.GetVisualDescendants())
                if (d is StyledElement se && se.DataContext != null && se.DataContext.GetType().Name == "MovementHUDScreenViewModel")
                {
                    movementHudRef = new WeakReference<StyledElement>(se);
                    return se.DataContext;
                }
            return null;
        }

        private double? safeSpeedLimit;   // m/s, from the game's movement HUD settings (its SafeSpeed plus tolerance)
        private bool safeSpeedLooked;

        /// <summary>
        /// The game's safe speed (the speed its speedometer stays in its safe colour up to) plus its tolerance, from
        /// the movement HUD's settings (MovementHUDConfiguration), looked for on the view model and one level in.
        /// </summary>
        private static double? FindSafeSpeed(object vm)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            double? Read(object cfg)
            {
                if (cfg == null) return null;
                var t = cfg.GetType();
                object sp = t.GetProperty("SafeSpeed", all)?.GetValue(cfg) ?? t.GetField("SafeSpeed", all)?.GetValue(cfg);
                object tol = t.GetProperty("SafeSpeedTolerance", all)?.GetValue(cfg) ?? t.GetField("SafeSpeedTolerance", all)?.GetValue(cfg);
                if (sp == null) return null;
                double v = Convert.ToDouble(sp), x = tol == null ? 0 : Convert.ToDouble(tol);
                return v > 0 && double.IsFinite(v) ? v + (double.IsFinite(x) ? x : 0) : (double?)null;
            }
            IEnumerable<object> Members(object o)
            {
                var t = o.GetType();
                foreach (var f in t.GetFields(all)) { object v = null; try { v = f.IsStatic ? f.GetValue(null) : f.GetValue(o); } catch { } if (v != null) yield return v; }
                foreach (var p in t.GetProperties(all))
                {
                    if (p.GetIndexParameters().Length > 0 || p.GetMethod == null) continue;
                    object v = null; try { v = p.GetMethod.IsStatic ? p.GetValue(null) : p.GetValue(o); } catch { }
                    if (v != null) yield return v;
                }
            }
            foreach (var m in Members(vm))
            {
                if (m.GetType().Name.StartsWith("MovementHUDConfiguration")) { var r = Read(m); if (r.HasValue) return r; }
            }
            foreach (var m in Members(vm))
            {
                if (m is string || m.GetType().IsPrimitive || m is System.Collections.IEnumerable) continue;
                foreach (var inner in Members(m))
                    if (inner.GetType().Name.StartsWith("MovementHUDConfiguration")) { var r = Read(inner); if (r.HasValue) return r; }
            }
            return null;
        }

        /// <summary>The safe speed icon: shown while the ship is at or under the game's safe speed.</summary>
        private void UpdateSafeSpeed(double speedMs)
        {
            if (safeSpeedIcon == null) return;
            if (!PartOn(32)) { safeSpeedIcon.IsVisible = false; return; }
            bool under = false;
            try
            {
                if (!safeSpeedLooked)
                {
                    object vm = MovementHud();
                    if (vm != null)
                    {
                        safeSpeedLooked = true;
                        try { safeSpeedLimit = FindSafeSpeed(vm); } catch { }
                        safeSpeedProp = vm.GetType().GetProperty("ShowSafeSpeedIndicator");
                        PluginLog.Info("Safe speed: " + (safeSpeedLimit.HasValue ? $"{safeSpeedLimit.Value:0.##} m/s (with tolerance), from the movement HUD settings"
                            : "no safe speed found in the movement HUD settings; going by its ShowSafeSpeedIndicator"));
                    }
                }
                if (safeSpeedLimit.HasValue) under = speedMs <= safeSpeedLimit.Value;
                else if (safeSpeedProp != null)
                {
                    // (No number to go by: the game's own flag, on while under the safe speed.)
                    object vm = MovementHud();
                    under = vm != null && ReadFlag(safeSpeedProp, vm) == true;
                }
            }
            catch { }
            safeSpeedIcon.IsVisible = under;
        }

        private Path speedoOutline;
        private Ellipse dialDisc;
        private string outlineFor;

        /// <summary>
        /// One shape for the dial and its pod together (circle joined to a box with rounded outer corners), filled and
        /// edged like the dial, so the pod looks part of it. Without the pod the dial keeps its own round edge.
        /// </summary>
        private void UpdateSpeedoOutline(double s)
        {
            if (dialDisc == null || speedoOutline == null) return;
            bool pod = shipPod.IsVisible;
            double d = dialSize;
            double h = shipPod.Bounds.Height > 1 ? shipPod.Bounds.Height : (34 * 2 + 6 + 14) * s;
            double w = shipPod.Bounds.Width > 1 ? shipPod.Bounds.Width : (26 + 34 + 7) * s;
            double x = d - 26 * s, y = (d - h) / 2, r = 10 * s;
            double R = Math.Max(d / 2, outlineRadius);   // (out past the resource rings when they show)
            string key = $"{pod}|{d:0.#}|{h:0.#}|{w:0.#}|{R:0.#}|{dialFill?.GetHashCode()}";
            if (key == outlineFor) return;
            outlineFor = key;
            speedoOutline.IsVisible = pod;
            // (The joined shape draws the fill and the edge instead: the disc's own would show twice, darker.)
            dialDisc.Stroke = pod ? null : BorderBrush;
            dialDisc.Fill = pod ? Brushes.Transparent : (dialFill ?? dialDisc.Fill);
            if (!pod) return;
            // (Joined by hand, one path: the game's UI can't combine shapes, that crashed it.) From where the box's top
            // edge meets the circle, along the box and round its corners, back to the circle at its bottom edge,
            // then round the circle the long way back to the start.
            double c = d / 2;
            double yTop = Math.Max(c - R + 1, y), yBot = Math.Min(c + R - 1, y + h);
            double xTop = c + Math.Sqrt(Math.Max(0, R * R - (yTop - c) * (yTop - c)));
            double xBot = c + Math.Sqrt(Math.Max(0, R * R - (yBot - c) * (yBot - c)));
            string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            // (The pod flares into the dial: its top and bottom edges leave the circle a little above and below the
            // box and run in at a slant, so it grows out of the dial instead of butting against it.)
            double flare = 12 * s, flareRun = 18 * s;
            double yTopFlare = Math.Max(c - R + 1, yTop - flare), yBotFlare = Math.Min(c + R - 1, yBot + flare);
            double xTopFlare = c + Math.Sqrt(Math.Max(0, R * R - (yTopFlare - c) * (yTopFlare - c)));
            double xBotFlare = c + Math.Sqrt(Math.Max(0, R * R - (yBotFlare - c) * (yBotFlare - c)));
            double xTopJoin = Math.Min(x + w - r, xTop + flareRun), xBotJoin = Math.Min(x + w - r, xBot + flareRun);
            speedoOutline.Data = Geometry.Parse(
                $"M {F(xTopFlare)},{F(yTopFlare)} L {F(xTopJoin)},{F(yTop)} L {F(x + w - r)},{F(yTop)} A {F(r)},{F(r)} 0 0 1 {F(x + w)},{F(yTop + r)} " +
                $"L {F(x + w)},{F(yBot - r)} A {F(r)},{F(r)} 0 0 1 {F(x + w - r)},{F(yBot)} L {F(xBotJoin)},{F(yBot)} L {F(xBotFlare)},{F(yBotFlare)} " +
                $"A {F(R)},{F(R)} 0 1 1 {F(xTopFlare)},{F(yTopFlare)} Z");
            speedoOutline.Fill = dialFill ?? Brush(200, 10, 14, 18);
            speedoOutline.Stroke = BorderBrush;
        }

        /// <summary>
        /// The icons, drawn like the game's toggle icons (its own pictures don't draw in our HUD): power, a ring
        /// with a bar; dampeners, four arrows pointing in.
        /// </summary>
        /// <summary>A yes / no the cockpit HUD keeps by this name: on it, or on one of its parts (one level in); null if none.</summary>
        private static Func<object, bool?> FindFlag(object vm, string name)
        {
            var direct = vm.GetType().GetProperty(name);
            if (direct != null) return o => ReadFlag(direct, o);
            foreach (var p in vm.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsPrimitive || p.PropertyType == typeof(string)) continue;
                var inner = p.PropertyType.GetProperty(name);
                if (inner == null) continue;
                var outer = p;
                PluginLog.Info($"Speedometer tabs: {name} found on the cockpit HUD's {p.Name}");
                return o => { object part = outer.GetValue(o); return part == null ? null : ReadFlag(inner, part); };
            }
            PluginLog.Info($"Speedometer tabs: no {name} on the cockpit HUD (that tile stays hidden)");
            return null;
        }

        private void FindToggleIcons(double s)
        {
            shipTabsIcons = true;
            powerIcon.Child = TracedIcon(IconPower, s);
            parkIcon.Child = TracedIcon(IconPark, s);
            antennaIcon.Child = TracedIcon(IconAntenna, s);
            dampOnPath = TracedIcon(IconDampOn, s);
            dampOffPath = TracedIcon(IconDampOff, s);
            dampIcon.Child = dampOffPath;
        }

        private static Control DrawnIcon(string data, double s) => new Path
        {
            Data = Geometry.Parse(data), Stroke = Brushes.White, StrokeThickness = 2.2 * s, StrokeLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform, Width = 20 * s, Height = 20 * s,
        };

        // ---- Hydrogen, stored power and power use: three rings round the outside of the dial, one outside the
        // other, each running the whole way round from just under the power / dampener pod to just over it. Their
        // names and levels are listed in the pod, under its tiles. Read from the game's cockpit HUD.
        private Canvas shipBars;
        private double shipBarsFor;
        private readonly Path[] barTrack = new Path[3], barFill = new Path[3];
        private double outlineRadius;   // the outline's round part: the dial's edge, or out past the rings when they show
        private readonly int[] ringSlot = { 0, 1, 2 };   // which ring each resource is drawn in (only ones the ship has, packed in)
        private int ringCount = 3;
        private readonly TextBlock[] barText = new TextBlock[3];
        private readonly Border[] barSwatch = new Border[3];
        private readonly TextBlock[] barPct = new TextBlock[3];
        private readonly StackPanel[] barRow = new StackPanel[3];
        private const double LegendBar = 26;   // a legend bar's length (before HUD scale)
        private StackPanel shipLegend;
        private int shipBarsLogs;
        private string lastShipBarsLog;
        private string ringsFor;
        private static readonly (string Prop, string Label)[] ShipBarDefs = { ("Hydrogen", "H2"), ("BatteryCharge", "BAT"), ("PowerUsage", "USE") };

        // Ring sizes: thin rings, a small gap between, in a band just outside the dial's edge.
        private const double RingWidth = 4, RingGap = 3, BandInset = 3;
        /// <summary>Radius of ring slot k (0 = innermost).</summary>
        private double RingRadius(int k, double s) => dialSize / 2 + (BandInset + RingGap + RingWidth / 2 + k * (RingWidth + RingGap)) * s;
        private double BandOuter(int count, double s) => dialSize / 2 + (BandInset + RingGap + count * (RingWidth + RingGap)) * s;

        /// <summary>Where ring i starts (under the pod) and ends (over it), clockwise round the bottom, left and top.</summary>
        private (double From, double To) RingSpan(int k, double s) => SpanAt(RingRadius(k, s), s, (RingGap + RingWidth / 2) * s);

        /// <summary>The angles at radius R level with the pod's top and bottom edges, pushed out by margin.</summary>
        private (double From, double To) SpanAt(double R, double s, double margin = 0)
        {
            double half = (shipPod.IsVisible && shipPod.Bounds.Height > 1 ? shipPod.Bounds.Height : 150 * s) / 2 + margin;
            double a = Math.Asin(Math.Min(0.95, half / R)) * 180 / Math.PI;
            return (a, 360 - a);
        }

        private static IBrush BarColour(int i, double v) =>
            i == 0 ? UiBrushes.Get(255, 110, 180, 255)
          : i == 1 ? (v < 0.2 ? UiBrushes.Of(HudColor.Red) : UiBrushes.Get(255, 90, 220, 120))
          : (v > 0.9 ? UiBrushes.Of(HudColor.Red) : UiBrushes.Get(255, 255, 160, 40));

        private void UpdateShipBars(double s)
        {
            object vm = null;
            try { vm = CockpitHud(); } catch { }
            if (vm == null || !PartOn(1))
            {
                shipBars.IsVisible = false; if (shipLegend != null) shipLegend.IsVisible = false;
                if (outlineRadius != 0) { outlineRadius = 0; UpdateSpeedoOutline(s); }
                return;
            }
            if (shipBarsFor != s)
            {
                shipBarsFor = s;
                ringsFor = null;
                shipBars.Children.Clear();
                // (No housing of their own: the speedometer's one outline grows out past them, see UpdateSpeedoOutline.)
                for (int i = 0; i < 3; i++)
                {
                    barTrack[i] = new Path { Stroke = Brush(38, 255, 255, 255), StrokeThickness = RingWidth * s, StrokeLineCap = PenLineCap.Round };
                    barFill[i] = new Path { StrokeThickness = RingWidth * s, StrokeLineCap = PenLineCap.Round };
                    shipBars.Children.Add(barTrack[i]);
                    shipBars.Children.Add(barFill[i]);
                }
                // The names in the pod: a colour swatch and "BAT 30%", one under another (outer ring first).
                shipLegend.Children.Clear();
                for (int i = 2; i >= 0; i--)
                {
                    // A row each: the name, a thin bar that fills with the level, the number.
                    barText[i] = Label(10 * s, White, FontWeight.Bold);
                    barText[i].VerticalAlignment = VerticalAlignment.Center;
                    barText[i].Width = 24 * s;
                    barSwatch[i] = new Border { Width = 0, Height = 4 * s, CornerRadius = new CornerRadius(2 * s), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                    var legendTrack = new Border { Width = LegendBar * s, Height = 4 * s, CornerRadius = new CornerRadius(2 * s), Background = Brush(255, 44, 52, 60), VerticalAlignment = VerticalAlignment.Center };
                    barPct[i] = Label(10 * s, Grey);
                    barPct[i].VerticalAlignment = VerticalAlignment.Center;
                    barPct[i].Margin = new Thickness(4 * s, 0, 0, 0);
                    barRow[i] = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children = { barText[i], new Grid { Width = LegendBar * s, Children = { legendTrack, barSwatch[i] } }, barPct[i] },
                    };
                    shipLegend.Children.Add(barRow[i]);
                }
            }
            // Read the three first: only the ones the ship has get a ring (packed in from the dial outwards).
            var levels = new double?[3];
            var seen = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var (prop, _) = ShipBarDefs[i];
                object raw = null;
                try { raw = vm.GetType().GetProperty(prop)?.GetValue(vm); } catch { }
                // The game's resource row: Percentage, and AnyNodes (false with no tanks / batteries).
                object pct = null, anyNodes = null;
                try
                {
                    anyNodes = raw?.GetType().GetProperty("AnyNodes")?.GetValue(raw);
                    pct = raw?.GetType().GetProperty("Percentage")?.GetValue(raw);
                }
                catch { }
                double? level = Level(raw);
                if (anyNodes is bool hasAny && !hasAny && (!level.HasValue || level.Value <= 0)) level = null;
                seen.Add($"{prop}: Percentage {pct?.GetType().Name ?? "null"} {pct}, AnyNodes {anyNodes} -> {level?.ToString("0.###") ?? "none"}");
                levels[i] = level;
            }
            int count = 0;
            for (int i = 0; i < 3; i++) ringSlot[i] = levels[i].HasValue ? count++ : -1;
            // Geometry: redrawn when the pod's height or the set of rings changes.
            string spanKey = $"{shipPod.Bounds.Height:0}|{shipPod.IsVisible}|{ringSlot[0]},{ringSlot[1]},{ringSlot[2]}|{dialFill?.GetHashCode()}";
            if (spanKey != ringsFor)
            {
                ringsFor = spanKey;
                ringCount = count;
                for (int i = 0; i < 3; i++)
                {
                    if (ringSlot[i] < 0) continue;
                    var (from, to) = RingSpan(ringSlot[i], s);
                    double cap = RingWidth * s / 2 / RingRadius(ringSlot[i], s) * 180 / Math.PI;
                    barTrack[i].Data = DialArc(from + cap, to - cap, RingRadius(ringSlot[i], s));
                }
            }
            // The outline round the dial and pod takes the rings in: one edge round the lot.
            outlineRadius = count > 0 ? BandOuter(count, s) : 0;
            bool any = count > 0;
            for (int i = 0; i < 3; i++)
            {
                var (_, label) = ShipBarDefs[i];
                double? level = levels[i];
                double v = Math.Clamp(level ?? 0, 0, 1);
                barTrack[i].IsVisible = barFill[i].IsVisible = level.HasValue;
                // (All three rows always show: one the ship has none of is greyed, with a dash.)
                if (barRow[i] != null && !barRow[i].IsVisible) barRow[i].IsVisible = true;
                barText[i].Text = label;
                barText[i].Foreground = level.HasValue ? White : Grey;
                barPct[i].Text = level.HasValue ? $"{v * 100:0}%" : "\u2013";
                double legendFill = level.HasValue ? Math.Max(2 * s, LegendBar * s * v) : 0;
                if (Math.Abs(barSwatch[i].Width - legendFill) > 0.5) barSwatch[i].Width = legendFill;
                barSwatch[i].Background = level.HasValue ? BarColour(i, v) : Grey;
                if (!level.HasValue) continue;
                var (from, to) = RingSpan(ringSlot[i], s);
                // (Round ends: kept inside the span so an empty or full ring doesn't poke past the pod.)
                double capDeg = RingWidth * s / 2 / RingRadius(ringSlot[i], s) * 180 / Math.PI;
                double end = from + capDeg + Math.Max(0.01, (to - from - 2 * capDeg) * v);
                barFill[i].Data = DialArc(from + capDeg, end, RingRadius(ringSlot[i], s));
                barFill[i].Stroke = BarColour(i, v);
            }
            // (Logged when the values change, the first few times, to check what the game gives.)
            string seenText = string.Join("; ", seen);
            if (shipBarsLogs < 4 && seenText != lastShipBarsLog) { shipBarsLogs++; lastShipBarsLog = seenText; PluginLog.Info("Speedometer bars: " + seenText); }
            shipBars.IsVisible = any;
            shipLegend.IsVisible = true;
            UpdateSpeedoOutline(s);
        }

        /// <summary>A level 0..1 from whatever the cockpit HUD gives (a number, a percentage, a value, or current / max).</summary>
        private static double? Level(object raw)
        {
            if (raw == null) return null;
            double? Num(object o)
            {
                switch (o)
                {
                    case double d: return double.IsNaN(d) ? (double?)null : d;
                    case float f: return float.IsNaN(f) ? (double?)null : f;
                    case int n: return n;
                    case long l: return l;
                    case decimal m: return (double)m;
                    default: return null;
                }
            }
            double? v = Num(raw);
            if (!v.HasValue)
            {
                var t = raw.GetType();
                object cur = (t.GetProperty("Current") ?? t.GetProperty("Amount") ?? t.GetProperty("Stored"))?.GetValue(raw);
                object max = (t.GetProperty("Max") ?? t.GetProperty("Capacity") ?? t.GetProperty("Maximum"))?.GetValue(raw);
                if (Num(cur) is double a && Num(max) is double b && b > 0) return a / b;
                foreach (string name in new[] { "Percentage", "Value", "Ratio", "Fraction", "Percent", "Fill", "Progress" })
                    if (t.GetProperty(name)?.GetValue(raw) is object inner)
                    {
                        // (Possibly a wrapper round the number: its Value.)
                        double? x = Num(inner) ?? Num(inner.GetType().GetProperty("Value")?.GetValue(inner));
                        if (x.HasValue) { v = x; break; }
                    }
            }
            if (!v.HasValue) return null;
            return v.Value > 1.0001 ? v.Value / 100 : v.Value;   // (a percentage)
        }

        /// <summary>The speed graph's key moved under the gap graph (one key for both), green / red said once.</summary>
        private static void OneKey(WrapPanel speedKey, WrapPanel gapKey)
        {
            var items = speedKey.Children.ToList();
            speedKey.Children.Clear();
            gapKey.Children.Clear();
            foreach (var item in items) gapKey.Children.Add(item);
            speedKey.IsVisible = false;
            gapKey.IsVisible = true;
        }

        private bool DrawGraph(HudModel.ResultsPanel res, double s, bool gapPage, Canvas graphCanvas, TextBlock graphTitle, WrapPanel graphLegend, bool withGap = false)
        {
            string lapNote = res.GraphLap != null ? "\n" + res.GraphLap.ToUpperInvariant() : "";
            if (res.Graph.Count == 0 || res.GraphMaxSpeed <= 0) return false;
            if (gapPage && res.Gap.Count < 2) return false;
            double w = graphCanvas.Width, h = graphCanvas.Height;
            double top = 14 * s;            // room for the checkpoint labels
                        graphCanvas.Children.Clear();
            graphLegend.Children.Clear();
            graphCanvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = UiBrushes.Get(170, 0, 0, 0) });

            // Checkpoints of the graphed lap: a thin line down the graph, labelled CP n (red if missed), FIN at the end.
            var main = res.Graph[0];
            int cps = main.Checkpoints.Count + res.MissedCheckpoints.Count;
            var cpX = new System.Collections.Generic.List<(double X, int N, bool Missed)>();
            foreach (var (x, _, n) in main.Checkpoints) cpX.Add((x, n, false));
            // A missed checkpoint has no place on this lap: take it from the other line, else halfway between its neighbours.
            foreach (int n in res.MissedCheckpoints)
            {
                double? x = null;
                for (int k = 1; k < res.Graph.Count && !x.HasValue; k++)
                    foreach (var c in res.Graph[k].Checkpoints) if (c.Number == n) { x = c.X; break; }
                if (!x.HasValue)
                {
                    double lo = 0, hi = 1;
                    foreach (var c in main.Checkpoints) { if (c.Number < n) lo = Math.Max(lo, c.X); else hi = Math.Min(hi, c.X); }
                    x = (lo + hi) / 2;
                }
                cpX.Add((x.Value, n, true));
            }
            bool shortNames = cps > 7;   // "CP 3" when there's room, else just "3"
            double lastLabelRight = -1;
            cpX.Sort((p, q) => p.X.CompareTo(q.X));
            void Marker(double x, string text, IBrush brush, bool dashed)
            {
                double gx = Math.Max(0, Math.Min(1, x)) * w;
                var ln = new Line { StartPoint = new Point(gx, top), EndPoint = new Point(gx, h), Stroke = brush, StrokeThickness = 1 };
                // (No dashes: every dash is a shape of its own to the game's UI, and too many crash it.)
                graphCanvas.Children.Add(ln);
                var lbl = Label(10 * s, brush, FontWeight.Bold);
                lbl.Text = text;
                lbl.Measure(Size.Infinity);
                double left = Math.Max(0, Math.Min(w - lbl.DesiredSize.Width, gx - lbl.DesiredSize.Width / 2));
                if (left < lastLabelRight + 2 * s) return;   // would overlap the one before: line only
                lastLabelRight = left + lbl.DesiredSize.Width;
                Canvas.SetLeft(lbl, left);
                Canvas.SetTop(lbl, 0);
                graphCanvas.Children.Add(lbl);
            }
            var cpBrush = UiBrushes.Get(120, 255, 170, 60);
            foreach (var (x, n, missed) in cpX)
                Marker(x, shortNames ? n.ToString() : $"CP {n}", missed ? Brush(HudColor.Red) : cpBrush, !missed);
            Marker(1, "FIN", UiBrushes.Get(160, 255, 255, 255), false);

            double plotH = h - top;
            if (!gapPage)
            {
                double max = res.GraphMaxSpeed * 1.05;
                double Y(double v) => h - Math.Min(1, v / max) * plotH;
                for (int q = 1; q < 4; q++)
                {
                    double y = h - plotH * q / 4.0;
                    graphCanvas.Children.Add(new Line { StartPoint = new Point(0, y), EndPoint = new Point(w, y), Stroke = UiBrushes.Get(35, 255, 255, 255), StrokeThickness = 1 });
                }
                SetHead(graphTitle, "SPEED", $"top {RaceDisplay.Core.GameOptions.Speed(res.GraphMaxSpeed)}", Brush(HudColor.Cyan), res.GraphLap, s);

                // Shading between this race and the line it's compared with: green = faster there, red = slower.
                if (res.Graph.Count > 1)
                {
                    var a = res.Graph[0].Points; var b = res.Graph[1].Points;
                    var greenQuads = new List<Point[]>(); var redQuads = new List<Point[]>();
                    const int n = 60;
                    double pa = SpeedAt(a, 0), pb = SpeedAt(b, 0);
                    for (int i = 1; i <= n; i++)
                    {
                        double x0 = (double)(i - 1) / n, x1 = (double)i / n;
                        double va = SpeedAt(a, x1), vb = SpeedAt(b, x1);
                        ((pa + va) >= (pb + vb) ? greenQuads : redQuads).Add(new[]
                            { new Point(x0 * w, Y(pa)), new Point(x1 * w, Y(va)), new Point(x1 * w, Y(vb)), new Point(x0 * w, Y(pb)) });
                        pa = va; pb = vb;
                    }
                    AddQuads(graphCanvas, greenQuads, UiBrushes.Get(70, 60, 220, 90));
                    AddQuads(graphCanvas, redQuads, UiBrushes.Get(70, 230, 60, 60));
                }

                // Lines: the compared ones first (underneath), this race on top.
                for (int k = res.Graph.Count - 1; k >= 0; k--)
                {
                    var line = res.Graph[k];
                    var pts = new System.Collections.Generic.List<Point>();
                    foreach (var (x, v) in line.Points) pts.Add(new Point(Math.Max(0, Math.Min(1, x)) * w, Y(v)));
                    foreach (var piece in Strokes(Thin(pts, 110), Brush(line.Colour), (k == 0 ? 2.6 : 2) * s)) graphCanvas.Children.Add(piece);
                }
                foreach (var line in res.Graph) graphLegend.Children.Add(LegendKey(s, Brush(line.Colour), line.Label));
                if (res.Graph.Count > 1)
                {
                    graphLegend.Children.Add(LegendKey(s, UiBrushes.Get(150, 60, 220, 90), "faster / gaining", true));
                    graphLegend.Children.Add(LegendKey(s, UiBrushes.Get(150, 230, 60, 60), "slower / losing", true));
                }
                graphLegend.IsVisible = true;

                // The time gap on the same graph: a yellow line on its own scale (the middle = level, up = ahead,
                // down = behind), labelled on the right.
                if (withGap && res.Gap.Count >= 2)
                {
                    double span = 0.2;
                    foreach (var (_, g) in res.Gap) span = Math.Max(span, Math.Abs(g));
                    span = NiceSpan(span * 1.1);
                    double mid = top + plotH / 2;
                    double GY(double gap) => mid + Math.Max(-1, Math.Min(1, gap / span)) * plotH / 2;
                    var level = new Line { StartPoint = new Point(0, mid), EndPoint = new Point(w, mid), Stroke = UiBrushes.Get(90, 255, 220, 80), StrokeThickness = 1 };
                    graphCanvas.Children.Add(level);
                    var gpts = new List<Point>();
                    int stepGap = Math.Max(1, (int)Math.Ceiling(res.Gap.Count / 80.0));
                    for (int i = 0; i < res.Gap.Count; i += stepGap) gpts.Add(new Point(res.Gap[i].X * w, GY(res.Gap[i].Gap)));
                    gpts.Add(new Point(res.Gap[res.Gap.Count - 1].X * w, GY(res.Gap[res.Gap.Count - 1].Gap)));
                    foreach (var piece in Strokes(gpts, Brush(HudColor.Yellow), 2 * s)) graphCanvas.Children.Add(piece);
                    void RightScale(string text, double y, IBrush brush)
                    {
                        var t = Label(10 * s, brush);
                        t.Text = text;
                        t.Measure(Size.Infinity);
                        Canvas.SetLeft(t, w - t.DesiredSize.Width - 3 * s);
                        Canvas.SetTop(t, Math.Max(top, Math.Min(h - t.DesiredSize.Height, y - t.DesiredSize.Height / 2)));
                        graphCanvas.Children.Add(t);
                    }
                    RightScale($"{span:0.0#}s ahead", top + 6 * s, Brush(HudColor.Green));
                    RightScale($"{span:0.0#}s behind", h - 6 * s, Brush(HudColor.Red));
                    double end = res.Gap[res.Gap.Count - 1].Gap;
                    graphTitle.Text = $"SPEED AND TIME GAP ROUND THE LAP   top {RaceDisplay.Core.GameOptions.Speed(res.GraphMaxSpeed)}   "
                        + $"finish {(end <= 0 ? $"{-end:0.000}s ahead" : $"{end:0.000}s behind")}" + (res.GapAgainst != null ? $" of {res.GapAgainst}" : "") + lapNote;
                    graphLegend.Children.Add(LegendKey(s, Brush(HudColor.Yellow), "time gap (right scale)"));
                }
            }
            else
            {
                // Time gap: above the middle line = ahead of the other lap, below = behind.
                double span = 0.2;
                foreach (var (_, g) in res.Gap) span = Math.Max(span, Math.Abs(g));
                span = NiceSpan(span * 1.1);
                double mid = top + plotH / 2;
                double Y(double gap) => mid + Math.Max(-1, Math.Min(1, gap / span)) * plotH / 2;   // + (behind) goes down
                graphCanvas.Children.Add(new Line { StartPoint = new Point(0, mid), EndPoint = new Point(w, mid), Stroke = UiBrushes.Get(140, 255, 255, 255), StrokeThickness = 1 });
                foreach (double q in new[] { -0.5, 0.5 })
                {
                    double y = mid + q * plotH;
                    graphCanvas.Children.Add(new Line { StartPoint = new Point(0, y), EndPoint = new Point(w, y), Stroke = UiBrushes.Get(35, 255, 255, 255), StrokeThickness = 1 });
                }
                // (Thinned to at most 80 points: the game's UI can only draw so much shape in one go.)
                var gap = new List<(double X, double G)>();
                int stepGap = Math.Max(1, (int)Math.Ceiling(res.Gap.Count / 80.0));
                for (int i = 0; i < res.Gap.Count; i += stepGap) gap.Add(res.Gap[i]);
                if (res.Gap.Count > 0 && gap[gap.Count - 1] != res.Gap[res.Gap.Count - 1]) gap.Add(res.Gap[res.Gap.Count - 1]);
                var aheadQuads = new List<Point[]>(); var behindQuads = new List<Point[]>();
                for (int i = 1; i < gap.Count; i++)
                {
                    var (x0, g0) = gap[i - 1]; var (x1, g1) = gap[i];
                    (g0 + g1 <= 0 ? aheadQuads : behindQuads).Add(new[]
                        { new Point(x0 * w, mid), new Point(x0 * w, Y(g0)), new Point(x1 * w, Y(g1)), new Point(x1 * w, mid) });
                }
                AddQuads(graphCanvas, aheadQuads, UiBrushes.Get(90, 60, 220, 90));
                AddQuads(graphCanvas, behindQuads, UiBrushes.Get(90, 230, 60, 60));
                var pts = new System.Collections.Generic.List<Point>();
                foreach (var (x, g) in gap) pts.Add(new Point(x * w, Y(g)));
                foreach (var piece in Strokes(pts, Brush(HudColor.White), 2 * s)) graphCanvas.Children.Add(piece);

                // Scale at the left: how many seconds the top and bottom lines are.
                void Scale(string text, double y, IBrush brush)
                {
                    var t = Label(10 * s, brush);
                    t.Text = text;
                    t.Measure(Size.Infinity);
                    Canvas.SetLeft(t, 3 * s);
                    Canvas.SetTop(t, Math.Max(top, Math.Min(h - t.DesiredSize.Height, y - t.DesiredSize.Height / 2)));
                    graphCanvas.Children.Add(t);
                }
                Scale($"{span:0.0#}s ahead", top + 6 * s, Brush(HudColor.Green));
                Scale($"{span:0.0#}s behind", h - 6 * s, Brush(HudColor.Red));
                double end = res.Gap[res.Gap.Count - 1].Gap;
                // (Short: which lap is said on the speed graph above, and the key is shared under this one.)
                SetHead(graphTitle, "TIME GAP", end <= 0 ? $"{-end:0.000}s ahead at the finish" : $"{end:0.000}s behind at the finish",
                    end <= 0 ? Brush(HudColor.Green) : Brush(HudColor.Red), null, s);
                graphLegend.IsVisible = true;
            }
            return true;
        }

        private static double NiceSpan(double v)
        {
            foreach (double n in new[] { 0.25, 0.5, 1, 2, 3, 5, 10, 15, 20, 30, 60, 120 }) if (v <= n) return n;
            return Math.Ceiling(v / 60) * 60;
        }

        /// <summary>Speed at a share of the lap, from a line's points (in order of share).</summary>
        private static double SpeedAt(System.Collections.Generic.List<(double X, double Speed)> pts, double x)
        {
            if (pts.Count == 0) return 0;
            if (x <= pts[0].X) return pts[0].Speed;
            int lo = 0, hi = pts.Count - 1;
            if (x >= pts[hi].X) return pts[hi].Speed;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (pts[m].X <= x) lo = m; else hi = m; }
            double span = pts[hi].X - pts[lo].X;
            return span > 1e-9 ? pts[lo].Speed + (pts[hi].Speed - pts[lo].Speed) * (x - pts[lo].X) / span : pts[hi].Speed;
        }

        private static StackPanel LegendKey(double s, IBrush brush, string text, bool box = false)
        {
            var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 * s, Margin = new Thickness(0, 0, 12 * s, 2 * s) };
            key.Children.Add(new Rectangle { Width = (box ? 10 : 14) * s, Height = (box ? 10 : 3) * s, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
            var label = Label(12 * s, box ? Grey : brush);
            label.Text = text;
            key.Children.Add(label);
            return key;
        }

        /// <summary>Checkpoint labels at their place on screen, and the edge arrow to the next checkpoint.</summary>
        private readonly System.Diagnostics.Stopwatch vectorClock = System.Diagnostics.Stopwatch.StartNew();
        private double vectorClockAt, vectorFade;   // the speed vector marker's fade (0 hidden .. 1 shown)

        private void UpdateMarkers(HudModel m, double s)
        {
            double w = root.Bounds.Width, h = root.Bounds.Height;
            if (w <= 0 || h <= 0) { markerLayer.IsVisible = false; return; }
            markerLayer.IsVisible = true;
            markerScale = s;
            placed.Clear();
            // Speed vector: a ring with wings and a fin where the ship is heading, outlined so it shows on any background.
            if (vectorMark == null)
            {
                var geo = StreamGeometry.Parse("M -11,0 A 11,11 0 1 1 11,0 A 11,11 0 1 1 -11,0 Z M -11,0 L -26,0 M 11,0 L 26,0 M 0,-11 L 0,-21");
                vectorMark = new Canvas { IsHitTestVisible = false, IsVisible = false, ZIndex = 50 };   // (in front of the checkpoint labels and the arrow)
                vectorMark.Children.Add(new Path { Data = geo, Stroke = Brush(170, 0, 0, 0), StrokeThickness = 6, StrokeLineCap = PenLineCap.Round });
                vectorMark.Children.Add(new Path { Data = geo, Stroke = Brush(255, 255, 255, 255), StrokeThickness = 3, StrokeLineCap = PenLineCap.Round });
                markerLayer.Children.Add(vectorMark);
            }
            // Fades in over about half a second when it appears (and out a little quicker, where it last was).
            double vecNow = vectorClock.Elapsed.TotalSeconds;
            double vecDt = Math.Clamp(vecNow - vectorClockAt, 0, 0.25);
            vectorClockAt = vecNow;
            vectorFade = m.VectorX.HasValue ? Math.Min(1, vectorFade + vecDt / 0.5) : Math.Max(0, vectorFade - vecDt / 0.3);
            vectorMark.IsVisible = vectorFade > 0.01;
            vectorMark.Opacity = vectorFade * vectorFade * (3 - 2 * vectorFade);   // (eased)
            if (m.VectorX.HasValue)
            {
                vectorMark.RenderTransform = new ScaleTransform(s * 1.2, s * 1.2);
                vectorMark.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
                Canvas.SetLeft(vectorMark, m.VectorX.Value * w);
                Canvas.SetTop(vectorMark, m.VectorY.Value * h);
            }
            IBrush fill = Brush((byte)Math.Min(200, (int)m.BackgroundOpacity), 12, 15, 18);

            // Where our panels are now: a checkpoint label that would sit behind one is hidden (see PlaceMarkers).
            hudRects.Clear();
            foreach (Control panel in new Control[] { timerBox, raceDrop, infoBox, lapsBox, splitsBox, bestCard, minimapBox, statsBox, resultsBox, speedoBox, rollBox, gravBox, keysBox, analyserBox, cpFlashBox })
            {
                if (panel == null || !panel.IsEffectivelyVisible || panel.Opacity < 0.05 || panel.Bounds.Width < 1 || panel.Bounds.Height < 1) continue;
                var cornerA = panel.TranslatePoint(new Point(0, 0), root);
                var cornerB = panel.TranslatePoint(new Point(panel.Bounds.Width, panel.Bounds.Height), root);
                if (cornerA.HasValue && cornerB.HasValue) hudRects.Add(new Rect(cornerA.Value, cornerB.Value).Inflate(3 * s));
            }

            while (markerPool.Count < m.Markers.Count)
            {
                var text = Label(15 * s, White, FontWeight.Bold);
                text.HorizontalAlignment = HorizontalAlignment.Center;
                var sub = Label(13 * s, White);
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                var box = Box(s);
                box.Padding = new Thickness(7 * s, 2 * s, 7 * s, 3 * s);
                box.Child = new StackPanel { Children = { text, sub } };
                markerLayer.Children.Add(box);
                markerPool.Add((box, text, sub));
            }
            for (int i = 0; i < markerPool.Count; i++)
            {
                var (box, text, sub) = markerPool[i];
                if (i >= m.Markers.Count) { box.IsVisible = false; continue; }
                var mk = m.Markers[i];
                box.IsVisible = true;
                // (One shape for every tag: the same thin edge in the tag's colour. The one to fly to is a little
                // bigger and on a slightly lighter panel, not a heavier box.)
                box.Background = mk.Emphasis ? Brush((byte)Math.Min(225, m.BackgroundOpacity + 45), 20, 28, 38) : fill;
                var c = Brush(mk.Colour);
                box.BorderBrush = c;
                box.BorderThickness = new Thickness(1);
                if (mk.Quiet) box.BorderBrush = Brush(255, 70, 82, 94);   // (a plain small tag: quiet edge, the text in its colour)
                text.Text = mk.Text; text.Foreground = c;
                text.FontSize = (mk.Emphasis ? 15 : mk.Quiet ? 12 : 13) * s;
                sub.Text = mk.Sub ?? ""; sub.IsVisible = mk.Sub != null; sub.Foreground = mk.SubColour.HasValue ? Brush(mk.SubColour.Value) : c;
                // (A slim tag: the second part beside the first, not under it.)
                var markerStack = (StackPanel)box.Child;
                markerStack.Orientation = mk.SubInline ? Orientation.Horizontal : Orientation.Vertical;
                markerStack.Spacing = mk.SubInline ? 7 * s : 0;
                box.Measure(Size.Infinity);
                var size = box.DesiredSize;
                placed.Add(new Placed { Box = box, Key = mk.Text, World = mk.World, X = mk.X, Y = mk.Y, W = size.Width, H = size.Height, Target = mk.Emphasis });
            }

            var a = m.Arrow;
            // (Fades out where it last was once the gate comes into view, rather than blinking off.)
            if (a != null && !arrowShape.IsVisible) arrowShape.IsVisible = arrowLabelBox.IsVisible = true;
            arrowShape.Opacity = arrowLabelBox.Opacity = a != null ? 1 : 0;
            placedArrow = a;
            if (a == null) smoothArrow = null;
            if (a != null)
            {
                arrowShape.Fill = Brush(a.Colour);
                arrowLabel.Text = a.Text;
                arrowLabel.Foreground = Brush(a.Colour);
                arrowLabelBox.BorderBrush = Brush(a.Colour);
                arrowLabelBox.Background = fill;
                arrowLabelBox.Measure(Size.Infinity);
            }

            // Forget smoothing for markers that have gone.
            if (smooth.Count > 0)
            {
                var gone = new List<string>();
                foreach (var k in smooth.Keys) if (!placed.Exists(p => p.Key == k)) gone.Add(k);
                foreach (var k in gone) smooth.Remove(k);
            }

            PlaceMarkers();
            StartFrameLoop();
        }

        private void StartFrameLoop()
        {
            if (frameLoop || projectBroken) return;
            var top = TopLevel.GetTopLevel(root);
            if (top == null) return;
            frameLoop = true;
            HudModel.MarkersPlacedByUi = true;
            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan _)
        {
            frameLoop = false;
            if (failed || root == null || !root.IsVisible || (placed.Count == 0 && placedArrow == null)) { HudModel.MarkersPlacedByUi = false; return; }
            try { PlaceMarkers(); }
            catch (Exception e)
            {
                projectBroken = true;
                HudModel.MarkersPlacedByUi = false;
                PluginLog.Info("HUD markers: per-frame placement off (" + e.Message + ")");
                return;
            }
            StartFrameLoop();
        }

        /// <summary>
        /// Moves a value toward its target: small changes (a pixel or two of shake) are eased, big ones
        /// (turning quickly) are followed straight away so the labels don't lag behind.
        /// </summary>
        private static double Ease(double from, double to, double pixels)
        {
            double d = Math.Abs(to - from) * pixels;
            double k = Math.Min(1, 0.3 + d / 50);
            return from + (to - from) * k;
        }

        /// <summary>Places the checkpoint labels and the edge arrow, re-projecting them from the camera now.</summary>
        private void PlaceMarkers()
        {
            double w = root.Bounds.Width, h = root.Bounds.Height;
            if (w <= 0 || h <= 0) return;
            double s = markerScale;
            var project = projectBroken ? null : Projector;

            foreach (var p in placed)
            {
                double x = p.X, y = p.Y;
                bool show = true;
                if (project != null && p.World.HasValue)
                {
                    if (project(p.World.Value, out double px, out double py, out bool behind))
                    {
                        x = px; y = py;
                        show = !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98;
                    }
                }
                if (smooth.TryGetValue(p.Key, out var last) && show)
                {
                    x = Ease(last.x, x, w);
                    y = Ease(last.y, y, h);
                }
                if (show) smooth[p.Key] = (x, y); else smooth.Remove(p.Key);
                // Centred just below the checkpoint's position, kept on screen.
                double boxLeft = Math.Max(0, Math.Min(w - p.W, x * w - p.W / 2)), boxTop = Math.Max(0, Math.Min(h - p.H, y * h + 18 * s));
                // (The gate you're heading for, dead ahead: its label drops below the crosshair instead of sitting on it.)
                if (show && p.Target)
                {
                    double aimX = w / 2, aimY = h / 2, aimClear = 26 * s;
                    if (aimX > boxLeft - aimClear && aimX < boxLeft + p.W + aimClear && aimY > boxTop - aimClear && aimY < boxTop + p.H + aimClear)
                        boxTop = Math.Min(h - p.H, aimY + aimClear + 8 * s);
                }
                // (Behind one of our panels: not shown, so it can't poke out from under a message or a table.)
                if (show && !p.Target && hudRects.Count > 0)
                {
                    var boxRect = new Rect(boxLeft, boxTop, p.W, p.H);
                    foreach (var panelRect in hudRects) if (panelRect.Intersects(boxRect)) { show = false; break; }
                }
                p.Box.IsVisible = show;
                Canvas.SetLeft(p.Box, boxLeft);
                Canvas.SetTop(p.Box, boxTop);
            }

            var a = placedArrow;
            if (a == null) return;
            double ax = a.X, ay = a.Y, angle = a.AngleDegrees;
            if (project != null && a.World.HasValue && project(a.World.Value, out double tx, out double ty, out _))
            {
                // Same rule as the game thread: from the screen centre toward the target, out to the edge.
                double dx = (tx - 0.5) * w, dy = (ty - 0.5) * h;
                if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) dy = 1;
                double k = Math.Min(0.44 * w / Math.Max(1e-6, Math.Abs(dx)), 0.40 * h / Math.Max(1e-6, Math.Abs(dy)));
                ax = 0.5 + dx * k / w;
                ay = 0.5 + dy * k / h;
                angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            }
            if (smoothArrow.HasValue)
            {
                var l = smoothArrow.Value;
                ax = Ease(l.x, ax, w);
                ay = Ease(l.y, ay, h);
                double da = angle - l.a;
                while (da > 180) da -= 360;
                while (da < -180) da += 360;
                angle = l.a + da * Math.Min(1, 0.3 + Math.Abs(da) / 30);
            }
            smoothArrow = (ax, ay, angle);

            double cx = ax * w, cy = ay * h;
            arrowShape.RenderTransform = new RotateTransform(angle);
            Canvas.SetLeft(arrowShape, cx - arrowShape.Width / 2);
            Canvas.SetTop(arrowShape, cy - arrowShape.Height / 2);

            var ls = arrowLabelBox.DesiredSize;
            // Label just inside the screen from the arrow.
            double rad = angle * Math.PI / 180;
            double lx = cx - Math.Cos(rad) * (40 * s + ls.Width / 2) - ls.Width / 2;
            double ly = cy - Math.Sin(rad) * (30 * s + ls.Height / 2) - ls.Height / 2;
            Canvas.SetLeft(arrowLabelBox, Math.Max(0, Math.Min(w - ls.Width, lx)));
            Canvas.SetTop(arrowLabelBox, Math.Max(0, Math.Min(h - ls.Height, ly)));
        }

        /// <summary>Small panel beside the timer: title, optional sub-title, big value.</summary>
        private Border SideBox(double s, string title, TextBlock subTitle, TextBlock value, HorizontalAlignment align)
        {
            var titleText = Label(12 * s, Grey);
            titleText.Text = title;
            titleText.HorizontalAlignment = HorizontalAlignment.Center;
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var stack = new StackPanel { Spacing = 2 * s, Children = { titleText } };
            if (subTitle != null) stack.Children.Add(subTitle);
            stack.Children.Add(value);

            var box = Box(s);
            box.HorizontalAlignment = align;
            box.VerticalAlignment = VerticalAlignment.Top;
            box.Child = stack;
            return box;
        }

        /// <summary>Name column, then <paramref name="cells"/> right-aligned columns of equal minimum width.</summary>
        private static Grid SplitGrid(double[] widths)
        {
            // Same widths on every row so the columns line up.
            var g = new Grid();
            foreach (double w in widths) g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w, GridUnitType.Pixel)));
            return g;
        }

        private static void AddCell(Grid g, int column, string text, IBrush brush, double size)
        {
            var t = Label(size, brush);
            t.Text = text;
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, column);
            g.Children.Add(t);
        }

        private Grid NewResultLine(double s)
        {
            // Label on the left, value on the right; a long value (penalties) wraps onto more lines instead of
            // running off the panel.
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            var label = Label(17 * s, Grey);
            label.VerticalAlignment = VerticalAlignment.Top;
            var value = Label(17 * s, White, FontWeight.Bold);
            value.TextWrapping = TextWrapping.Wrap;
            value.TextAlignment = TextAlignment.Right;
            value.HorizontalAlignment = HorizontalAlignment.Stretch;
            value.Margin = new Thickness(12 * s, 0, 0, 0);
            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            g.Children.Add(label);
            g.Children.Add(value);
            return g;
        }

        private Grid NewLapRow(double s)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(46 * s, GridUnitType.Pixel)));
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var name = Label(17 * s, White);
            var note = Label(14 * s, Brush(HudColor.Red), FontWeight.Bold);
            note.VerticalAlignment = VerticalAlignment.Center;
            note.Margin = new Thickness(0, 0, 2 * s, 0);
            var notePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 * s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14 * s, 0), Children = { note } };
            var time = Label(17 * s, White);
            // Columns: lap, its time, then any penalties (so the times line up under each other).
            g.ColumnDefinitions[1].Width = GridLength.Auto;
            g.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            notePanel.Margin = new Thickness(14 * s, 0, 0, 0);
            notePanel.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(name, 0);
            Grid.SetColumn(time, 1);
            Grid.SetColumn(notePanel, 2);
            g.Children.Add(name);
            g.Children.Add(notePanel);
            g.Children.Add(time);
            // (Behind the row: lit for the lap being flown; a bar at the left for it, and for the best lap.)
            var lapLit = new Border { Background = Brush(255, 52, 44, 30), CornerRadius = new CornerRadius(2 * s), Margin = new Thickness(-9 * s, -1 * s, -9 * s, -1 * s), ZIndex = -1, IsVisible = false };
            Grid.SetColumnSpan(lapLit, 3);
            var lapBar = new Border { Width = 3 * s, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-9 * s, -1 * s, 0, -1 * s), IsVisible = false };
            g.Children.Add(lapLit);
            g.Children.Add(lapBar);
            return g;
        }
    }
}
