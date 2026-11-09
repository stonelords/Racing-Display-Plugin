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
        // Timer in the top corner: the timer, best lap, best race, splits and stats move into this column.
        private Grid topRow;
        private StackPanel timerColumn, cornerColumn;
        private Viewbox cornerFit;
        private StackPanel mapRow, bestStack;   // timer in the corner: minimap with best race / best lap beside it
        private StackPanel splitsRow;           // timer in the corner: splits with the results beside them (towards the middle)
        private string placedKey;
        private StackPanel graphBox;
        private WrapPanel graphLegend;
        private Canvas graphCanvas;
        private TextBlock graphTitle;
        private HudModel.ResultsPanel graphFor;
        private int graphPage;
        private Canvas markerLayer;
        private readonly System.Collections.Generic.List<(Border Box, TextBlock Text, TextBlock Sub)> markerPool =
            new System.Collections.Generic.List<(Border, TextBlock, TextBlock)>();
        private Path arrowShape;
        private Border arrowLabelBox;
        private TextBlock arrowLabel;
        private TextBlock tDelta, countdownText, resultsTitle, resultsHeadline, splitsHeader;
        private StackPanel dots, splitRows, resultLines;
        private TextBlock tLabel, tBig, tFooter, tHint;
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
        private TextBlock lapsHeader;
        private StackPanel lapRows;
        private Border centreBox;
        private Panel raceDrop;                  // race messages: a tab that drops down under the timer
        private Avalonia.Controls.Shapes.Path raceFill;
        private double raceDropHeight, raceTextInset, raceScale = 1;
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
            splitsDrawn = statsDrawn = null;
                    return;
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                string sig = model.Signature();
                root.IsVisible = true;
                if (sig == lastSignature) { PluginLog.Time("hud check", (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); return; }
                lastSignature = sig;
                Update(model);
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
        private static readonly IBrush BorderBrush = new SolidColorBrush(Color.FromArgb(230, 235, 238, 240));
        private static readonly IBrush White = new SolidColorBrush(Colors.White);
        private static readonly IBrush Grey = new SolidColorBrush(Color.FromArgb(230, 185, 190, 195));

        private Border Box(double s) => new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3 * s),
            Padding = new Thickness(14 * s, 8 * s, 14 * s, 10 * s),
        };

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

            timerBox = Box(s);
            timerBox.MinWidth = 300 * s;
            timerBox.HorizontalAlignment = HorizontalAlignment.Center;
            tDelta = Label(20 * s, White, FontWeight.Bold);
            tDelta.HorizontalAlignment = HorizontalAlignment.Center;
            dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6 * s,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2 * s, 0, 2 * s),
            };
            timerBox.Child = new StackPanel { Spacing = 2 * s, Children = { tLabel, tBig, tDelta, dots, tFooter } };

            // Side panels: best lap (left of the timer) and best race (right).
            bestLapValue = Label(24 * s, Brush(HudColor.Yellow), FontWeight.Bold);
            bestLapBox = SideBox(s, "BEST LAP", null, bestLapValue, HorizontalAlignment.Right);
            bestLapNote = Label(13 * s, Brush(HudColor.Purple));
            ((StackPanel)bestLapBox.Child).Children.Add(bestLapNote);
            bestRaceValue = Label(24 * s, Brush(HudColor.Yellow), FontWeight.Bold);
            bestRaceBox = SideBox(s, "BEST RACE", null, bestRaceValue, HorizontalAlignment.Left);   // no "3 LAPS" line
            bestRaceNote = Label(13 * s, Brush(HudColor.Purple));
            ((StackPanel)bestRaceBox.Child).Children.Add(bestRaceNote);

            // Three columns keep the timer exactly centred whatever the side panels' widths.
            topRow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            topRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            topRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            topRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            bestLapBox.Margin = new Thickness(0, 0, 10 * s, 0);
            bestRaceBox.Margin = new Thickness(10 * s, 0, 0, 0);
            Grid.SetColumn(bestLapBox, 0);
            // Race messages (GO!, lap times, penalties, missed checkpoints) drop down from under the timer in a tab
            // that narrows towards the bottom, in the same dark panel and thin border as the rest.
            raceText = Label(18 * s, White, FontWeight.Bold);
            raceText.HorizontalAlignment = HorizontalAlignment.Center;
            raceText.VerticalAlignment = VerticalAlignment.Center;
            raceDropHeight = 36 * s;
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
            raceTextInset = raceDropHeight * 0.9;
            raceScale = s;
            raceText.Margin = new Thickness(0, 4 * s, 0, 6 * s);
            raceTab.Children.Add(raceFill);
            raceTab.Children.Add(raceEdge);
            raceTab.Children.Add(raceText);
            this.raceTab = raceTab;
            // The tab slides down out of the timer box: its holder grows from 0 to the tab's height, and only the
            // part inside the holder shows.
            raceDrop = new Panel
            {
                Height = 0,
                ClipToBounds = true,
                Margin = new Thickness(6 * s, -1, 6 * s, 0),
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
                    raceDrop.Width = Math.Max(0, timerBox.Bounds.Width - 12 * s);
            };
            bestLapBox.VerticalAlignment = VerticalAlignment.Top;
            bestRaceBox.VerticalAlignment = VerticalAlignment.Top;

            Grid.SetColumn(timerColumn, 1);
            Grid.SetColumn(bestRaceBox, 2);
            topRow.Children.Add(bestLapBox);
            topRow.Children.Add(timerColumn);
            topRow.Children.Add(bestRaceBox);

            // "How to start" in its own panel under the timer.
            tHint = Label(20 * s, White);
            tHint.HorizontalAlignment = HorizontalAlignment.Center;
            infoBox = Box(s);
            infoBox.HorizontalAlignment = HorizontalAlignment.Center;
            infoBox.Padding = new Thickness(14 * s, 6 * s, 14 * s, 8 * s);
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
            lapsHeader.HorizontalAlignment = HorizontalAlignment.Right;
            lapRows = new StackPanel { Spacing = 3 * s };
            lapsBox = Box(s);
            // Only as wide as it needs to be: grows when a lap has penalty notes.
            lapsBox.MinWidth = 210 * s;
            lapsBox.Child = new StackPanel { Spacing = 6 * s, Children = { lapsHeader, lapRows } };

            // ---- centre message, top of row 2 (30%)
            centreText = Label(17 * s, White);
            centreBox = Box(s);
            centreBox.Padding = new Thickness(12 * s, 6 * s, 12 * s, 6 * s);
            centreBox.Child = centreText;
            centreBox.HorizontalAlignment = HorizontalAlignment.Center;
            centreBox.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(centreBox, 2);

            // ---- splits, right side, top at 25% (mirrors the lap list)
            splitsHeader = Label(17 * s, White);
            splitsHeader.HorizontalAlignment = HorizontalAlignment.Right;
            splitRows = new StackPanel { Spacing = 1 * s };
            splitsBox = Box(s);
            splitsBox.MinWidth = 220 * s;
            splitsBox.HorizontalAlignment = HorizontalAlignment.Right;
            splitsBox.VerticalAlignment = VerticalAlignment.Top;
            splitsBox.Margin = new Thickness(0, 0, 30 * s, 0);
            splitsBox.Child = new StackPanel { Spacing = 6 * s, Children = { splitsHeader, splitRows } };
            Grid.SetRow(splitsBox, 1);
            Grid.SetRowSpan(splitsBox, 2);

            // ---- race stats after the finish: own box in the top corner, two columns of label / value
            statsTitle = Label(17 * s, White);
            statsTitle.Text = "RACE STATS";
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
            resultLines = new StackPanel { Spacing = 4 * s };
            resultsBox = Box(s);
            // Results sit under the lap list (out of the way of the crosshair and the ship).
            resultsBox.Width = 400 * s;
            resultsBox.Padding = new Thickness(18 * s, 10 * s, 18 * s, 14 * s);
            // Speed graph: speed round the lap (left = start, right = finish) for the best lap and the record.
            graphTitle = Label(13 * s, Grey);
            graphTitle.TextWrapping = TextWrapping.Wrap;
            graphCanvas = new Canvas { Width = 364 * s, Height = 110 * s, ClipToBounds = true };
            graphLegend = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 364 * s };
            graphBox = new StackPanel { Spacing = 4 * s, Margin = new Thickness(0, 6 * s, 0, 0), Children = { graphTitle, graphCanvas, graphLegend } };
            resultsSubtitle = Label(15 * s, Brush(HudColor.Cyan));
            resultsSubtitle.HorizontalAlignment = HorizontalAlignment.Center;
            resultsSubtitle.TextAlignment = TextAlignment.Center;
            resultsSubtitle.TextWrapping = TextWrapping.Wrap;
            resultsSubtitle.Margin = new Thickness(0, -6 * s, 0, 0);
            resultsTitle.TextWrapping = TextWrapping.Wrap;
            resultsTitle.TextAlignment = TextAlignment.Center;
            resultsFooter = Label(13 * s, Grey);
            resultsFooter.HorizontalAlignment = HorizontalAlignment.Center;
            resultsBox.Child = new StackPanel { Spacing = 8 * s, Children = { resultsTitle, resultsSubtitle, resultsHeadline, resultLines, graphBox, resultsFooter } };
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
                Data = Geometry.Parse("M 0,0 L 30,15 L 0,30 L 7,15 Z"),
                Width = 30 * s,
                Height = 30 * s,
                Stretch = Stretch.Fill,
                Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                StrokeThickness = 1,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                IsVisible = false,
            };
            arrowLabel = Label(15 * s, White, FontWeight.Bold);
            arrowLabelBox = Box(s);
            arrowLabelBox.Padding = new Thickness(8 * s, 3 * s, 8 * s, 4 * s);
            arrowLabelBox.Child = arrowLabel;
            arrowLabelBox.IsVisible = false;
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
            placedKey = null; mapRow = null; bestStack = null; splitsRow = null;
            root.Children.Add(centreBox);
            root.Children.Add(countdownText);
            markerPool.Clear();
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

        private void PlaceTimer(bool corner, bool swap, double s)
        {
            string key = corner + "|" + swap;
            if (placedKey == key) return;
            placedKey = key;
            var minimap = (Control)minimapBox;
            foreach (Control c in new Control[] { timerColumn, bestLapBox, bestRaceBox, splitsBox, statsBox, lapsBox, minimap, resultsBox })
                (c.Parent as Panel)?.Children.Remove(c);
            // The holders too: they're put back in their new places below (a control can only have one parent).
            foreach (Panel holder in new Panel[] { mapRow, bestStack, splitsRow })
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
                foreach (Control c in new Control[] { timerColumn, lapsBox, statsBox })
                {
                    c.HorizontalAlignment = edge;
                    c.VerticalAlignment = VerticalAlignment.Top;
                    c.Margin = new Thickness(0);
                    cornerColumn.Children.Add(c);
                }
                timerBox.HorizontalAlignment = HorizontalAlignment.Stretch;
                timerColumn.Width = CornerWidth * s;
                statsBox.MinWidth = CornerWidth * s;

                bestStack ??= new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                bestStack.Spacing = 8 * s;
                foreach (var box in new[] { bestRaceBox, bestLapBox })
                {
                    box.HorizontalAlignment = HorizontalAlignment.Stretch;
                    box.VerticalAlignment = VerticalAlignment.Top;
                    box.Margin = new Thickness(0);
                    box.Padding = new Thickness(12 * s, 4 * s, 16 * s, 6 * s);
                    Grid.SetColumnSpan(box, 1);
                    if (box.Child is StackPanel st)
                    {
                        st.Spacing = 0;
                        foreach (var tb in st.Children) tb.HorizontalAlignment = HorizontalAlignment.Left;
                    }
                    bestStack.Children.Add(box);
                }
                bestLapValue.FontSize = bestRaceValue.FontSize = 26 * s;

                mapRow ??= new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                mapRow.Spacing = 14 * s;
                mapRow.HorizontalAlignment = swap ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                if (swap) { mapRow.Children.Add(bestStack); mapRow.Children.Add(minimap); }
                else { mapRow.Children.Add(minimap); mapRow.Children.Add(bestStack); }

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
                bestLapBox.HorizontalAlignment = HorizontalAlignment.Right;
                bestRaceBox.HorizontalAlignment = HorizontalAlignment.Left;
                bestLapBox.Margin = new Thickness(0, 0, 10 * s, 0);
                bestRaceBox.Margin = new Thickness(10 * s, 0, 0, 0);
                bestLapValue.FontSize = bestRaceValue.FontSize = 24 * s;
                foreach (var box in new[] { bestLapBox, bestRaceBox })
                {
                    box.Padding = new Thickness(14 * s, 8 * s, 14 * s, 10 * s);
                    box.VerticalAlignment = VerticalAlignment.Top;
                    Grid.SetColumnSpan(box, 1);
                    if (box.Child is StackPanel st)
                    {
                        st.Spacing = 2 * s;
                        foreach (var tb in st.Children) tb.HorizontalAlignment = HorizontalAlignment.Left;
                    }
                }
                Grid.SetColumn(bestLapBox, 0);
                Grid.SetColumn(timerColumn, 1);
                Grid.SetColumn(bestRaceBox, 2);
                topRow.Children.Add(bestLapBox);
                topRow.Children.Add(timerColumn);
                topRow.Children.Add(bestRaceBox);
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
                statsBox.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(statsBox, 0);
                Grid.SetRowSpan(statsBox, 3);
                int at = root.Children.IndexOf(leftFit) + 1;
                root.Children.Insert(at, splitsBox);
                root.Children.Insert(at + 1, statsBox);
            }
        }

        // ---------------------------------------------------------------- minimap

        private Control MakeMinimap(double s)
        {
            minimapCanvas = new Canvas { ClipToBounds = true };
            // Route and lines live in their own layer that's scaled and moved for zoom / follow; dots, labels,
            // you and the ghosts are placed on top at their transformed positions (so they keep their size).
            mmContent = new Canvas();
            mmRoute = new Path { Stroke = new SolidColorBrush(Color.FromArgb(150, 235, 235, 245)), StrokeThickness = 1.5 * s, StrokeDashArray = new AvaloniaList<double> { 4, 3 } };
            mmLine = new Path { StrokeThickness = 2.5 * s, StrokeJoin = PenLineJoin.Round };
            mmLine2 = new Path { StrokeThickness = 2.5 * s, StrokeJoin = PenLineJoin.Round, Stroke = Brush(HudColor.Orange) };
            mmSegs = new Canvas();
            mmSectors = new Canvas();
            mmContent.Children.Add(mmSectors);
            mmContent.Children.Add(mmRoute);
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
                Points = new AvaloniaList<Point> { new Point(8 * s, 0), new Point(14 * s, 15 * s), new Point(8 * s, 11 * s), new Point(2 * s, 15 * s) },
                Width = 16 * s, Height = 16 * s,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                Fill = Brush(HudColor.Cyan), Stroke = White, StrokeThickness = 1 * s,
                ZIndex = 100,
            };
            minimapCanvas.Children.Add(mmPlayer);
            mmPoints.Clear();
            mmLabels.Clear();
            mmGhosts.Clear();
            mmSectorBands.Clear();
            mmSectorTagPool.Clear();
            mmDrawn = mmLabelsSig = null;
            mmClipSize = -1;
            mmRingScale = null;
            // The round background is an ellipse drawn in the canvas itself (a rounded border only makes a
            // rounded square), and the canvas is clipped to the same circle.
            mmBack = new Ellipse { Stroke = BorderBrush, StrokeThickness = 1.5 * s, ZIndex = -10 };
            minimapCanvas.Children.Add(mmBack);
            minimapBox = new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = minimapCanvas, IsVisible = false };

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
            minimapBox.IsVisible = mm != null;
            if (mm == null) { mmRing.IsVisible = false; mmUpdatedAt = 0; return; }
            // At most ~30 updates a second (plenty for a small map); a skipped one is caught up on the next frame.
            long nowMs = Environment.TickCount64;
            if (mmDrawn != null && mmDrawn.StartsWith(mm.StaticSig + "|") && nowMs - mmUpdatedAt < 33) { lastSignature = null; return; }
            mmUpdatedAt = nowMs;
            double size = mm.Size * s;
            minimapBox.Width = size;
            minimapBox.Height = size;
            minimapCanvas.Width = size;
            minimapCanvas.Height = size;
            mmContent.Width = size;
            mmContent.Height = size;
            // Round: a filled circle behind everything, and the contents cut to that circle.
            mmBack.Width = size; mmBack.Height = size;
            mmBack.Fill = fill;
            Canvas.SetLeft(mmBack, 0); Canvas.SetTop(mmBack, 0);
            if (size != mmClipSize)
            {
                mmClipSize = size;
                minimapCanvas.Clip = new EllipseGeometry(new Rect(0, 0, size, size));
                minimapBox.Clip = new EllipseGeometry(new Rect(0, 0, size, size));
            }
            // Everything drawn on the map stays inside its circle (the clip alone isn't always applied).
            double radius = size / 2 - 2 * s;
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
            Point T(double x, double y) => new Point(((x - mm.ViewX) / span + 0.5) * size, ((y - mm.ViewY) / span + 0.5) * size);
            // Route and lines are drawn in screen coordinates and cut to the circle here (the game's renderer doesn't
            // apply clips to them reliably). Only redone when the view or the lines change.
            // The Race Map's switches (sectors, checkpoints, labels, route, racing line, ghosts, you) apply here too.
            var opt = MapOptions.Current;
            // The view to the nearest pixel (and half a percent of zoom): smaller moves don't redraw the lines.
            string viewSig = $"{mm.StaticSig}|{Math.Round(mm.ViewX / span * size)}|{Math.Round(mm.ViewY / span * size)}|{Math.Round(Math.Log(span) * 200)}|{size:0}|{opt.Sectors}{opt.Route}{opt.RacingLine}";
            bool linesChanged = mmDrawn == null || !mmDrawn.StartsWith(mm.StaticSig + "|");
            long nowTicks = Environment.TickCount64;
            bool viewChanged = viewSig != mmDrawn && (linesChanged || nowTicks - mmLinesAt >= 33);
            if (viewSig != mmDrawn && !viewChanged) lastSignature = null;   // skipped for now: redraw on the next frame
            if (viewChanged)
            {
                mmLinesAt = nowTicks;
                mmDrawn = viewSig;
                double cx0 = size / 2, cy0 = size / 2, rad = size / 2 - 2 * s;

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
                            if (!Clip(a, b, out var ca, out var cb)) { if (inFigure) { ctx.EndFigure(false); inFigure = false; } continue; }
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
                    var band = new Path { StrokeThickness = 9 * s, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
                    mmSectorBands.Add(band);
                    mmSectors.Children.Add(band);
                }
                for (int k = 0; k < mmSectorBands.Count; k++) mmSectorBands[k].IsVisible = k < secs.Count;
                for (int k = 0; k < secs.Count; k++)
                {
                    var sec = secs[k];
                    mmSectorBands[k].Data = Cut(sec.Points, false);
                    mmSectorBands[k].Stroke = Brush(sec.Colour.WithAlpha(90));
                    // Where its name goes is worked out with the other labels (below), clear of the checkpoints.
                    var pts = sec.Points.ConvertAll(q => T(q.X, q.Y));
                    var (mid, normal) = LabelPlacer.Halfway(pts, new Point(size / 2, size / 2));
                    mmSectorInfo.Add((sec.Name, sec.Colour, mid, normal));
                }

                mmRoute.Data = Cut(opt.Route ? mm.Route : new List<(double X, double Y)>(), false);
                mmLine.Data = Cut(mm.Line, false);
                mmLine2.Data = Cut(mm.Line2, false);

                // Coloured racing line: segments grouped by colour (rounded to a few shades), one path per
                // colour, cut to the circle. A handful of shapes instead of hundreds of separate lines.
                var groups = new Dictionary<uint, List<(Point A, Point B)>>();
                for (int i = 0; i + 1 < mm.Line.Count && i < mm.LineColours.Count; i++)
                {
                    if (!Clip(T(mm.Line[i].X, mm.Line[i].Y), T(mm.Line[i + 1].X, mm.Line[i + 1].Y), out var a, out var b)) continue;
                    var c = mm.LineColours[i];
                    uint key = (uint)(c.R & 0xF0) << 16 | (uint)(c.G & 0xF0) << 8 | (uint)(c.B & 0xF0);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(Point, Point)>();
                    list.Add((a, b));
                }
                int used = 0;
                foreach (var kv in groups)
                {
                    if (used >= mmSegs.Children.Count)
                        mmSegs.Children.Add(new Path { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, StrokeThickness = 3 * s });
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
                if (!InCircle(at.X, at.Y, d / 2)) { c.IsVisible = false; continue; }
                Canvas.SetLeft(c, at.X - d / 2);
                Canvas.SetTop(c, at.Y - d / 2);
            }

            // Labels and sector names are only placed again when the drawn view (or what's shown) changes;
            // otherwise just their colours are kept up to date.
            string labelsSig = $"{mmDrawn}|{opt.Checkpoints}{opt.Labels}{opt.Sectors}|{mm.Points.Count}";
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
                b.IsVisible = i < mm.Points.Count && opt.Checkpoints && opt.Labels && !string.IsNullOrEmpty(mm.Points[i].Label);
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
            foreach (var info in mmSectorInfo)
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
            mmPlayer.IsVisible = mm.PlayerX.HasValue && opt.You;
            Point? me = null;
            if (mm.PlayerX.HasValue)
            {
                var at = T(mm.PlayerX.Value, mm.PlayerY.Value);
                me = at;
                Canvas.SetLeft(mmPlayer, at.X - 8 * s);
                Canvas.SetTop(mmPlayer, at.Y - 8 * s);
                mmPlayerTurn ??= new RotateTransform();
                if (!ReferenceEquals(mmPlayer.RenderTransform, mmPlayerTurn)) mmPlayer.RenderTransform = mmPlayerTurn;
                mmPlayerTurn.Angle = mm.PlayerAngle;
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
            }
        }

        // ---------------------------------------------------------------- update

        // ---- race HUD only: hide the game's own HUD screens (Keen.Game2.Client.UI.HUD.*) while it's on

        private bool hideGameHud;
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
                foreach (var h in hidden) { try { h.Undo?.Dispose(); } catch { } }
                hidden.Clear();
            }
        }

        /// <summary>Hides the game's HUD screens under the UI root (again, if the game made new ones), not our own.</summary>
        private void HideGameHudNow()
        {
            try
            {
                // From the window's root: the game keeps some HUD screens (character and ship status, toggles) in
                // other layers than the one our HUD is attached to.
                Visual top = attachedTo == null ? null : (Visual)TopLevel.GetTopLevel(attachedTo) ?? attachedTo;
                if (top == null || !hideGameHud) return;
                hidden.RemoveAll(h => !h.Visual.TryGetTarget(out _));
                var keptScreens = new HashSet<string>();
                bool logScreens = !loggedKeptScreens;
                var stack = new Stack<(Visual V, int Depth)>();
                stack.Push((top, 0));
                while (stack.Count > 0)
                {
                    var (v, depth) = stack.Pop();
                    if (ReferenceEquals(v, root)) continue;
                    string ns = v.GetType().Namespace ?? "";
                    if (depth > 0 && ns.StartsWith("Keen.Game2.Client.UI.HUD"))
                    {
                        if (!hidden.Exists(h => h.Visual.TryGetTarget(out var t) && ReferenceEquals(t, v)))
                        {
                            // Above the game's own value (and its bindings), and taken off again when this is turned off.
                            var undo = v.SetValue(Visual.IsVisibleProperty, false, Avalonia.Data.BindingPriority.Animation);
                            hidden.Add((new WeakReference<Visual>(v), undo));
                        }
                        continue;
                    }
                    if (logScreens && IsScreen(v) && v.IsVisible) keptScreens.Add(v.GetType().FullName);
                    if (depth >= 16) continue;
                    foreach (var c in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(v)) stack.Push((c, depth + 1));
                }
                if (logScreens)
                {
                    loggedKeptScreens = true;
                    PluginLog.Info($"Race HUD only: hid {hidden.Count} game HUD part(s); other screens still showing: {string.Join(", ", keptScreens)}");
                }
            }
            catch (Exception e) { PluginLog.Error("Race HUD only: couldn't hide the game HUD", e); }
        }

        private bool loggedKeptScreens;

        private static bool IsScreen(Visual v)
        {
            for (var t = v.GetType(); t != null; t = t.BaseType) if (t.Name == "ScreenView") return true;
            return false;
        }

        private void Update(HudModel m)
        {
            SetHideGameHud(m.HideGameHud);
            double s = builtScale;
            IBrush fill = Brush(m.BackgroundOpacity, 12, 15, 18);
            timerBox.Background = fill;
            infoBox.Background = fill;
            bestLapBox.Background = fill;
            bestRaceBox.Background = fill;
            lapsBox.Background = fill;
            raceFill.Fill = fill;
            centreBox.Background = fill;
            splitsBox.Background = fill;
            statsBox.Background = fill;
            resultsBox.Background = fill;
            PlaceTimer(m.TimerInCorner, m.SwapSides, s);
            if (!m.TimerInCorner)
            {
                statsBox.HorizontalAlignment = m.SwapSides ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                statsBox.Margin = m.SwapSides ? new Thickness(30 * s, 30 * s, 0, 0) : new Thickness(0, 30 * s, 30 * s, 0);
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

            UpdateMarkers(m, s);

            // Flash: timer border turns green (best lap) or red (penalty) briefly.
            timerBox.BorderBrush = m.Flash.HasValue ? Brush(m.Flash.Value) : BorderBrush;
            timerBox.BorderThickness = new Thickness(m.Flash.HasValue ? 3 : 1);

            // Timer
            var t = m.Timer;
            timerBox.IsVisible = t != null;
            // The hint box fades out instead of vanishing; its last text stays while it fades.
            infoBox.Opacity = t?.Hint != null ? 1 : 0;
            bestLapBox.IsVisible = t?.BestLap != null;
            bestRaceBox.IsVisible = t?.BestRace != null;
            if (t != null)
            {
                tLabel.Text = t.Label;
                tBig.Text = t.Big;
                tBig.Foreground = Brush(t.BigColour);
                // Corner layout (narrow timer): one hint per line, so none is cut off.
                bool narrow = placedKey != null && placedKey.StartsWith("True");
                tFooter.Text = narrow ? string.Join("\n", (t.Footer ?? "").Split(new[] { "   " }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                                      : t.Footer ?? "";
                tFooter.TextAlignment = TextAlignment.Center;
                tFooter.IsVisible = t.Footer != null;
                if (t.Hint != null)
                {
                    tHint.Text = t.Hint;
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

            // Splits
            var sp = m.Splits;
            splitsBox.IsVisible = sp != null;
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
                    var name = Label(15 * s, Brush(r.NameColour));
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
            statsBox.IsVisible = rs != null;
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
                resultsFooter.Text = res.Footer ?? "";
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
            if (res != null) UpdateGraph(res, s);
            resultsBox.Opacity = res != null ? 1 : 0;
            // Corner layout: beside the splits, so it mustn't take up room while there are no results.
            if (placedKey != null && placedKey.StartsWith("True")) resultsBox.IsVisible = res != null;

            UpdateMinimap(m.Minimap, s, fill);

            // Countdown
            if (m.Countdown != null) { countdownText.Text = m.Countdown; countdownText.Foreground = Brush(m.CountdownColour); }
            countdownText.Opacity = m.Countdown != null ? 1 : 0;

            // Laps
            var laps = m.Laps;
            lapsBox.IsVisible = laps != null;
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
                    var note = (TextBlock)notePanel.Children[0];
                    note.Text = r.Note ?? ""; note.Foreground = Brush(r.NoteColour);
                    // Penalty icons: a red ring per missed checkpoint, a red cross per crash.
                    while (notePanel.Children.Count > 1) notePanel.Children.RemoveAt(notePanel.Children.Count - 1);
                    for (int k = 0; k < Math.Min(r.MissedIcons, 6); k++)
                        notePanel.Children.Add(new Ellipse { Width = 10 * s, Height = 10 * s, Stroke = Brush(HudColor.Red), StrokeThickness = 1.6 * s, VerticalAlignment = VerticalAlignment.Center });
                    for (int k = 0; k < Math.Min(r.CrashIcons, 6); k++)
                        notePanel.Children.Add(new Path
                        {
                            Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                            Width = 10 * s, Height = 10 * s, Stretch = Stretch.Fill,
                            Stroke = Brush(HudColor.Red), StrokeThickness = 1.8 * s,
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                }
            }

            // Messages: fade in/out (text is kept while fading out)
            if (m.RaceMessage != null) { raceText.Text = m.RaceMessage.Text; raceText.Foreground = Brush(m.RaceMessage.Colour); }
            // Tab height: one line, or more when the message wraps in the tab's width.
            double tabH = raceDropHeight;
            if (raceDrop.Width > 0)
            {
                // The text gets a fixed width that clears the tab's slanted sides (8% of the width each side at the
                // bottom), so a long message wraps onto another line instead of running past the edge and being cut off.
                double textW = Math.Max(40 * raceScale, raceDrop.Width * 0.84 - 16 * raceScale);
                if (double.IsNaN(raceText.Width) || Math.Abs(raceText.Width - textW) > 0.5) raceText.Width = textW;
                raceText.InvalidateMeasure();
                // (The text's margins are part of its measured size.)
                raceText.Measure(new Size(raceDrop.Width, double.PositiveInfinity));
                tabH = Math.Max(raceDropHeight, raceText.DesiredSize.Height);
            }
            raceTab.Height = tabH;
            raceDrop.Height = m.RaceMessage != null ? tabH : 0;
            if (m.CentreMessage != null) { centreText.Text = m.CentreMessage.Text; centreText.Foreground = Brush(m.CentreMessage.Colour); }
            centreBox.Opacity = m.CentreMessage != null ? 1 : 0;
        }


        /// <summary>
        /// Results graph. Two pages that take turns (every 5 s): speed round the lap (this race against the
        /// best, shaded green where you were faster and red where slower) and the time gap round the lap.
        /// </summary>
        private void UpdateGraph(HudModel.ResultsPanel res, double s)
        {
            if (ReferenceEquals(res, graphFor) && res.GraphPage == graphPage) return; // already drawn
            graphFor = res;
            graphPage = res.GraphPage;
            graphBox.IsVisible = res.Graph.Count > 0 && res.GraphMaxSpeed > 0;
            if (!graphBox.IsVisible) return;
            double w = graphCanvas.Width, h = graphCanvas.Height;
            double top = 14 * s;            // room for the checkpoint labels
            bool gapPage = res.GraphPage == 1 && res.Gap.Count > 1;
            bool pages = res.Gap.Count > 1;
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
                if (dashed) ln.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 };
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
                graphTitle.Text = $"SPEED ROUND THE LAP   top {RaceDisplay.Core.GameOptions.Speed(res.GraphMaxSpeed)}" + (pages ? "   (1/2)" : "");

                // Shading between this race and the line it's compared with: green = faster there, red = slower.
                if (res.Graph.Count > 1)
                {
                    var a = res.Graph[0].Points; var b = res.Graph[1].Points;
                    var green = new StreamGeometry(); var red = new StreamGeometry();
                    using (var gc = green.Open())
                    using (var rc = red.Open())
                    {
                        const int n = 120;
                        double pa = SpeedAt(a, 0), pb = SpeedAt(b, 0);
                        for (int i = 1; i <= n; i++)
                        {
                            double x0 = (double)(i - 1) / n, x1 = (double)i / n;
                            double va = SpeedAt(a, x1), vb = SpeedAt(b, x1);
                            var ctx = (pa + va) >= (pb + vb) ? gc : rc;
                            ctx.BeginFigure(new Point(x0 * w, Y(pa)), true);
                            ctx.LineTo(new Point(x1 * w, Y(va)));
                            ctx.LineTo(new Point(x1 * w, Y(vb)));
                            ctx.LineTo(new Point(x0 * w, Y(pb)));
                            ctx.EndFigure(true);
                            pa = va; pb = vb;
                        }
                    }
                    graphCanvas.Children.Add(new Path { Data = green, Fill = UiBrushes.Get(70, 60, 220, 90) });
                    graphCanvas.Children.Add(new Path { Data = red, Fill = UiBrushes.Get(70, 230, 60, 60) });
                }

                // Lines: the compared ones first (underneath), this race on top.
                for (int k = res.Graph.Count - 1; k >= 0; k--)
                {
                    var line = res.Graph[k];
                    var pts = new System.Collections.Generic.List<Point>();
                    foreach (var (x, v) in line.Points) pts.Add(new Point(Math.Max(0, Math.Min(1, x)) * w, Y(v)));
                    graphCanvas.Children.Add(new Polyline { Points = pts, Stroke = Brush(line.Colour), StrokeThickness = (k == 0 ? 2.6 : 2) * s });
                }
                foreach (var line in res.Graph) graphLegend.Children.Add(LegendKey(s, Brush(line.Colour), line.Label));
                if (res.Graph.Count > 1)
                {
                    graphLegend.Children.Add(LegendKey(s, UiBrushes.Get(150, 60, 220, 90), "faster", true));
                    graphLegend.Children.Add(LegendKey(s, UiBrushes.Get(150, 230, 60, 60), "slower", true));
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
                var ahead = new StreamGeometry(); var behind = new StreamGeometry();
                using (var ac = ahead.Open())
                using (var bc = behind.Open())
                {
                    for (int i = 1; i < res.Gap.Count; i++)
                    {
                        var (x0, g0) = res.Gap[i - 1]; var (x1, g1) = res.Gap[i];
                        var ctx = g0 + g1 <= 0 ? ac : bc;
                        ctx.BeginFigure(new Point(x0 * w, mid), true);
                        ctx.LineTo(new Point(x0 * w, Y(g0)));
                        ctx.LineTo(new Point(x1 * w, Y(g1)));
                        ctx.LineTo(new Point(x1 * w, mid));
                        ctx.EndFigure(true);
                    }
                }
                graphCanvas.Children.Add(new Path { Data = ahead, Fill = UiBrushes.Get(90, 60, 220, 90) });
                graphCanvas.Children.Add(new Path { Data = behind, Fill = UiBrushes.Get(90, 230, 60, 60) });
                var pts = new System.Collections.Generic.List<Point>();
                foreach (var (x, g) in res.Gap) pts.Add(new Point(x * w, Y(g)));
                graphCanvas.Children.Add(new Polyline { Points = pts, Stroke = Brush(HudColor.White), StrokeThickness = 2 * s });

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
                graphTitle.Text = $"TIME GAP ROUND THE LAP   vs {res.GapAgainst}   finish {(end <= 0 ? $"{-end:0.000}s ahead" : $"{end:0.000}s behind")}   (2/2)";
                graphLegend.Children.Add(LegendKey(s, UiBrushes.Get(150, 60, 220, 90), "ahead (gaining time)", true));
                graphLegend.Children.Add(LegendKey(s, UiBrushes.Get(150, 230, 60, 60), "behind", true));
            }
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
        private void UpdateMarkers(HudModel m, double s)
        {
            double w = root.Bounds.Width, h = root.Bounds.Height;
            if (w <= 0 || h <= 0) { markerLayer.IsVisible = false; return; }
            markerLayer.IsVisible = true;
            markerScale = s;
            placed.Clear();
            IBrush fill = Brush((byte)Math.Min(200, (int)m.BackgroundOpacity), 12, 15, 18);

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
                box.Background = fill;
                var c = Brush(mk.Colour);
                box.BorderBrush = c;
                box.BorderThickness = new Thickness(mk.Emphasis ? 2 : 1);
                text.Text = mk.Text; text.Foreground = c;
                text.FontSize = (mk.Emphasis ? 17 : 13) * s;
                sub.Text = mk.Sub ?? ""; sub.IsVisible = mk.Sub != null; sub.Foreground = c;
                box.Measure(Size.Infinity);
                var size = box.DesiredSize;
                placed.Add(new Placed { Box = box, Key = mk.Text, World = mk.World, X = mk.X, Y = mk.Y, W = size.Width, H = size.Height });
            }

            var a = m.Arrow;
            arrowShape.IsVisible = a != null;
            arrowLabelBox.IsVisible = a != null;
            placedArrow = a;
            if (a == null) smoothArrow = null;
            if (a != null)
            {
                arrowShape.Fill = Brush(a.Colour);
                arrowLabel.Text = a.Text;
                arrowLabel.Foreground = Brush(a.Colour);
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
                p.Box.IsVisible = show;
                // Centred just below the checkpoint's position, kept on screen.
                Canvas.SetLeft(p.Box, Math.Max(0, Math.Min(w - p.W, x * w - p.W / 2)));
                Canvas.SetTop(p.Box, Math.Max(0, Math.Min(h - p.H, y * h + 18 * s)));
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
            var titleText = Label(15 * s, Grey);
            titleText.Text = title;
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
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var label = Label(17 * s, Grey);
            var value = Label(17 * s, White, FontWeight.Bold);
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
            var note = Label(14 * s, Brush(HudColor.Red));
            note.VerticalAlignment = VerticalAlignment.Center;
            var notePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 * s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14 * s, 0), Children = { note } };
            var time = Label(17 * s, White);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(notePanel, 1);
            Grid.SetColumn(time, 2);
            g.Children.Add(name);
            g.Children.Add(notePanel);
            g.Children.Add(time);
            return g;
        }
    }
}
