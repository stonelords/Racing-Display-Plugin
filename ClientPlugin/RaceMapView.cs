using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// The Race Map page in the terminal (G > Map > Race Map): the minimap's view of the whole course, as big as the page.
    /// The game thread hands it the latest map data (only while it's showing); it redraws a few times a second.
    /// </summary>
    internal sealed class RaceMapView : Grid
    {
        private static volatile HudModel.MinimapPanel latest;
        private static volatile bool shown;

        /// <summary>True while a Race Map page is on screen (the game thread then builds the map data).</summary>
        public static bool Shown => shown;

        /// <summary>From the game thread: the newest map data.</summary>
        public static void Push(HudModel.MinimapPanel map) => latest = map;

        private readonly Canvas canvas = new Canvas { ClipToBounds = true };
        private readonly Border frame;
        private readonly TextBlock empty;
        private readonly DispatcherTimer timer;
        private string drawnSig;
        private Size drawnSize;
        private readonly Canvas lines = new Canvas();   // route and racing lines (redrawn when they or the size change)
        private readonly Canvas live = new Canvas();    // points, labels, you and the ghosts (every tick)
        private readonly Grid mapArea = new Grid();
        // Zoom and drag: lines and live sit in 'content', which is scaled and moved; the scale bar, north arrow
        // and the hover box sit in 'overlay' on top, not zoomed.
        private readonly Canvas content = new Canvas();
        private readonly Canvas overlay = new Canvas { IsHitTestVisible = false };
        private double zoom = 1;
        private Vector pan;
        private Point? dragFrom;
        private double side = 100;
        private HudModel.MinimapPanel drawnMap;
        // Under the map: the height profile and the key.
        private readonly Canvas profile = new Canvas { Height = 110 };
        private readonly Border profileFrame;
        private Border replayBar, replayPrev, replayNext;
        private TextBlock replayLap;
        private readonly WrapPanel legend = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        private readonly Line profileCursor = new Line { Stroke = Brushes.White, StrokeThickness = 1.5, IsVisible = false };
        private int hoverIndex = -1;

        /// <summary>
        /// A switch above the map: a small picture of what it shows and its name. On: lit, with a cyan edge; off:
        /// dimmed and see-through. Click to change.
        /// </summary>
        private Control MakeSwitch(string name, Func<bool> get, Action<bool> set)
        {
            var text = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var icon = new Canvas { Width = 22, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            var tick = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, text, tick } },
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(3),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            bool hover = false;
            void Look()
            {
                bool on = get();
                chip.Background = new SolidColorBrush(on ? Color.FromArgb(255, 22, 64, 78) : Color.FromArgb(hover ? (byte)90 : (byte)0, 60, 70, 80));
                chip.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(255, 90, 210, 240) : Color.FromArgb(hover ? (byte)160 : (byte)90, 120, 128, 135));
                text.Foreground = new SolidColorBrush(on ? Colors.White : Color.FromArgb(170, 160, 166, 172));
                tick.Fill = new SolidColorBrush(on ? Color.FromArgb(255, 90, 230, 140) : Color.FromArgb(90, 160, 166, 172));
                icon.Opacity = on ? 1 : 0.35;
            }
            DrawIcon(icon, name);
            chip.PointerEntered += (_, __) => { hover = true; Look(); };
            chip.PointerExited += (_, __) => { hover = false; Look(); };
            chip.PointerPressed += (_, e) =>
            {
                set(!get());
                MapOptions.Save();
                Look();
                drawnSig = null;   // redraw the lines too
                Tick();
                e.Handled = true;
            };
            Look();
            return chip;
        }

        /// <summary>A tiny picture of what each switch shows.</summary>
        private static void DrawIcon(Canvas c, string name)
        {
            IBrush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
            switch (name)
            {
                case "Sectors":
                    c.Children.Add(new Line { StartPoint = new Point(1, 12), EndPoint = new Point(8, 6), Stroke = B(120, 200, 255), StrokeThickness = 5, StrokeLineCap = PenLineCap.Round });
                    c.Children.Add(new Line { StartPoint = new Point(8, 6), EndPoint = new Point(14, 10), Stroke = B(255, 150, 220), StrokeThickness = 5, StrokeLineCap = PenLineCap.Round });
                    c.Children.Add(new Line { StartPoint = new Point(14, 10), EndPoint = new Point(21, 4), Stroke = B(180, 255, 140), StrokeThickness = 5, StrokeLineCap = PenLineCap.Round });
                    break;
                case "Checkpoints":
                    foreach (double x in new[] { 3.0, 11.0, 19.0 })
                    {
                        var e = new Ellipse { Width = 6, Height = 6, Fill = x == 11 ? B(255, 160, 40) : B(80, 230, 120) };
                        Canvas.SetLeft(e, x - 3); Canvas.SetTop(e, 5);
                        c.Children.Add(e);
                    }
                    break;
                case "Labels":
                    var tag = new Border { Width = 20, Height = 13, BorderBrush = B(255, 210, 60), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(2),
                        Child = new TextBlock { Text = "A", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = B(255, 210, 60), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    Canvas.SetTop(tag, 1.5);
                    c.Children.Add(tag);
                    break;
                case "Route":
                    c.Children.Add(new Line { StartPoint = new Point(1, 8), EndPoint = new Point(21, 8), Stroke = B(235, 235, 245), StrokeThickness = 2, StrokeDashArray = new AvaloniaList<double> { 2, 1.5 } });
                    break;
                case "Racing line":
                    c.Children.Add(new Path { Data = StreamGeometry.Parse("M 1,12 C 6,0 14,16 21,4"), Stroke = B(255, 70, 255), StrokeThickness = 3, StrokeLineCap = PenLineCap.Round });
                    break;
                case "Ghosts":
                    var g = new Ellipse { Width = 11, Height = 11, Fill = B(200, 120, 255), Stroke = Brushes.White, StrokeThickness = 1.5 };
                    Canvas.SetLeft(g, 5.5); Canvas.SetTop(g, 2.5);
                    c.Children.Add(g);
                    break;
                case "You":
                    c.Children.Add(new Polygon
                    {
                        Points = new AvaloniaList<Point> { new Point(11, 1), new Point(18, 15), new Point(11, 11), new Point(4, 15) },
                        Fill = B(70, 200, 255), Stroke = Brushes.White, StrokeThickness = 1,
                    });
                    break;
            }
        }

        // ---- the game's own map screen bits (Fast Travel, "Map Data Unavailable", its side panels) hidden while the
        // Race Map shows, and put back as soon as it doesn't.
        private readonly List<IDisposable> hiddenOverlays = new List<IDisposable>();
        private readonly HashSet<Visual> hiddenVisuals = new HashSet<Visual>();
        private long overlaysCheckedAt;

        private void HideMapOverlays()
        {
            // Looked for when the page shows, then every couple of seconds (the game may add them a moment later).
            long now = Environment.TickCount64;
            if (now - overlaysCheckedAt < 2000) return;
            overlaysCheckedAt = now;
            try
            {
                if (!(this.GetVisualRootSafe() is Visual root)) return;
                void Hide(Visual v)
                {
                    if (!hiddenVisuals.Add(v)) return;
                    hiddenOverlays.Add(v.SetValue(Visual.IsVisibleProperty, false, Avalonia.Data.BindingPriority.Animation));
                }
                void Walk(Visual v, int depth)
                {
                    if (v == null || depth > 40 || ReferenceEquals(v, this)) return;   // never ours
                    bool holdsUs = Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(v, this);
                    string type = v.GetType().FullName ?? "";
                    if (!holdsUs)
                    {
                        // The map screen's own views (overlay, you-are-here, sector progress, GPS panels...).
                        if (type.StartsWith("Keen.Game2.Client.UI.MapScreen.", StringComparison.Ordinal)) { Hide(v); return; }
                        if (v is TextBlock t && t.Text != null && (t.Text.Contains("Map Data Unavailable") || t.Text == "Fast Travel"))
                        {
                            // The text, or the button it's on.
                            Visual target = t;
                            foreach (var a in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(t))
                            {
                                if (a is Button) { target = a; break; }
                                if (Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(a, this)) break;
                            }
                            Hide(target);
                            return;
                        }
                    }
                    foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(v)) Walk(child, depth + 1);
                }
                Walk(root, 0);
            }
            catch (Exception e) { PluginLog.Error("Race Map: couldn't hide the map screen's own panels", e); }
        }

        private void ShowMapOverlays()
        {
            foreach (var undo in hiddenOverlays) { try { undo?.Dispose(); } catch { } }
            hiddenOverlays.Clear();
            hiddenVisuals.Clear();
            overlaysCheckedAt = 0;
        }

        public RaceMapView()
        {
            Margin = new Thickness(0, 10, 0, 10);
            // Solid behind the map, so the game's own map screen underneath ("Map Data Unavailable") doesn't show through.
            Background = new SolidColorBrush(Color.FromArgb(245, 8, 11, 14));
            frame = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 14, 18, 22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(230, 235, 238, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = canvas,
            };
            content.Children.Add(lines);
            content.Children.Add(live);
            content.Children.Add(anim);
            content.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
            canvas.Children.Add(content);
            canvas.Children.Add(overlay);
            canvas.Background = Brushes.Transparent;   // so the mouse works anywhere on the map
            canvas.PointerWheelChanged += (_, e) =>
            {
                var at = e.GetPosition(canvas);
                double z = Math.Clamp(zoom * Math.Pow(1.25, e.Delta.Y), 1, 12);
                // Keep the point under the mouse where it is.
                pan = (Vector)at - ((Vector)at - pan) * (z / zoom);
                zoom = z;
                ApplyView();
                e.Handled = true;
            };
            canvas.PointerPressed += (_, e) =>
            {
                if (e.ClickCount == 2) { zoom = 1; pan = default; ApplyView(); e.Handled = true; return; }
                if (e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) { dragFrom = e.GetPosition(canvas); e.Handled = true; }
            };
            canvas.PointerReleased += (_, e) => dragFrom = null;
            canvas.PointerExited += (_, e) => { dragFrom = null; ShowHover(-1); };
            canvas.PointerMoved += (_, e) =>
            {
                var at = e.GetPosition(canvas);
                if (dragFrom.HasValue)
                {
                    pan += at - dragFrom.Value;
                    dragFrom = at;
                    ApplyView();
                    return;
                }
                ShowHover(NearestHover(at));
            };
            profileFrame = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 14, 18, 22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(230, 235, 238, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 8, 0, 0),
                Child = profile,
                ClipToBounds = true,
            };
            empty = new TextBlock
            {
                Text = "No race course here yet. Set one up in course edit mode (Ctrl+F) and it shows here.",
                Foreground = new SolidColorBrush(Color.FromArgb(230, 185, 190, 195)),
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
            // A row of switches above the map (what to show), then the map.
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            // The switches sit together on one dark rounded bar, with a "SHOW" title.
            var switches = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            switches.Children.Add(new TextBlock
            {
                Text = "SHOW", FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(8, 0, 6, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(170, 160, 166, 172)), VerticalAlignment = VerticalAlignment.Center,
            });
            foreach (var (name, get, set) in MapOptions.All)
                switches.Children.Add(MakeSwitch(name, get, set));
            var bar = new Border
            {
                Child = switches,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(235, 14, 18, 22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 235, 238, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 0, 8),
            };
            // Beside them, after a race: the lap being shown (arrows step through the laps) and Replay.
            Border Pill(string text, Action click)
            {
                var pill = new Border
                {
                    Child = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                    Background = new SolidColorBrush(Color.FromArgb(255, 22, 64, 78)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 210, 240)),
                    BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 3, 12, 4), Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                pill.PointerPressed += (_, e) => { e.Handled = true; click(); };
                return pill;
            }
            replayPrev = Pill("<", () => RacingMenu.Instance.Send(new MenuCommand(MenuAction.ResultsLapPrev)));
            replayNext = Pill(">", () => RacingMenu.Instance.Send(new MenuCommand(MenuAction.ResultsLapNext)));
            replayLap = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            var replayRow = new StackPanel { Orientation = Orientation.Horizontal };
            replayRow.Children.Add(new TextBlock
            {
                Text = "LAP", FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(8, 0, 6, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(170, 160, 166, 172)), VerticalAlignment = VerticalAlignment.Center,
            });
            replayRow.Children.Add(replayPrev);
            replayRow.Children.Add(replayLap);
            replayRow.Children.Add(replayNext);
            replayRow.Children.Add(Pill("Replay", () => { animFor = null; Tick(); }));
            replayBar = new Border
            {
                Child = replayRow,
                Background = new SolidColorBrush(Color.FromArgb(235, 14, 18, 22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 235, 238, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(12, 0, 0, 8),
                IsVisible = false,
            };
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            var bars = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { bar, replayBar } };
            Grid.SetRow(bars, 0);
            Children.Add(bars);
            mapArea.Children.Add(new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { frame, profileFrame, legend },
            });
            mapArea.Children.Add(empty);
            Grid.SetRow(mapArea, 1);
            Children.Add(mapArea);

            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, __) => Tick());
            AttachedToVisualTree += (_, __) => { timer.Start(); Tick(); };
            DetachedFromVisualTree += (_, __) => { timer.Stop(); shown = false; ShowMapOverlays(); };
        }

        private readonly List<(string Name, HudColor Colour, Point Mid, Vector Normal)> sectorInfo = new List<(string, HudColor, Point, Vector)>();

        private static IBrush Brush(HudColor c) => UiBrushes.Of(c);
        private string liveSig;   // what the dots / labels / ghosts / you were last drawn for

        private void ApplyView()
        {
            // Never dragged so far that the map leaves the frame.
            double max = side * (zoom - 1);
            pan = new Vector(Math.Clamp(pan.X, -max, 0), Math.Clamp(pan.Y, -max, 0));
            content.RenderTransform = new MatrixTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(pan));
            if (drawnMap != null) UpdateOverlay(drawnMap);
            ShowHover(-1);
        }

        /// <summary>
        /// A line in many colours: segments grouped by colour (a few shades) into paths, each kept short (the game's
        /// UI renderer can't take one very long stroke: it crashed drawing them).
        /// </summary>
        private void AddColouredLine(List<(double X, double Y)> pts, List<HudColor> colours, double width, Func<double, double, Point> P, Canvas target = null)
        {
            target ??= lines;
            const int MaxSegments = 40;
            var open = new Dictionary<uint, (StreamGeometry Geo, StreamGeometryContext Ctx, int Last, int Count)>();
            void Flush(uint key, (StreamGeometry Geo, StreamGeometryContext Ctx, int Last, int Count) o)
            {
                o.Ctx.EndFigure(false);
                o.Ctx.Dispose();
                target.Children.Add(new Path
                {
                    Data = o.Geo,
                    Stroke = UiBrushes.Get(255, (byte)(key >> 16 | 0x08), (byte)((key >> 8 & 0xFF) | 0x08), (byte)((key & 0xFF) | 0x08)),
                    StrokeThickness = width,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                });
            }
            for (int i = 0; i + 1 < pts.Count && i < colours.Count; i++)
            {
                var c = colours[i];
                uint key = (uint)(c.R & 0xF0) << 16 | (uint)(c.G & 0xF0) << 8 | (uint)(c.B & 0xF0);
                if (open.TryGetValue(key, out var o) && o.Count >= MaxSegments) { Flush(key, o); open.Remove(key); }
                if (!open.TryGetValue(key, out o))
                {
                    var g = new StreamGeometry();
                    o = (g, g.Open(), -2, 0);
                }
                if (o.Last != i - 1)
                {
                    if (o.Last >= 0) o.Ctx.EndFigure(false);
                    o.Ctx.BeginFigure(P(pts[i].X, pts[i].Y), false);
                }
                o.Ctx.LineTo(P(pts[i + 1].X, pts[i + 1].Y));
                open[key] = (o.Geo, o.Ctx, i, o.Count + 1);
            }
            foreach (var kv in open) Flush(kv.Key, kv.Value);
        }

        /// <summary>A long polyline as several short ones (see AddColouredLine).</summary>
        private static void AddChunked(Avalonia.Controls.Controls target, Polyline pl, int max = 40)
        {
            var pts = pl.Points;
            if (pts == null || pts.Count <= max) { target.Add(pl); return; }
            for (int start = 0; start < pts.Count - 1; start += max - 1)
            {
                var part = new AvaloniaList<Point>();
                for (int i = start; i < Math.Min(pts.Count, start + max); i++) part.Add(pts[i]);
                target.Add(new Polyline
                {
                    Points = part, Stroke = pl.Stroke, StrokeThickness = pl.StrokeThickness, StrokeDashArray = pl.StrokeDashArray,
                    StrokeLineCap = pl.StrokeLineCap, StrokeJoin = pl.StrokeJoin, Opacity = pl.Opacity,
                });
            }
        }

        /// <summary>Scale bar and north arrow in the bottom corners (they follow the zoom).</summary>
        private string overlayKey;

        private void UpdateOverlay(HudModel.MinimapPanel mm)
        {
            // Only when the map, its size or the zoom changed (it's called every tick).
            string key = $"{mm.MetresAcross:0.#}|{mm.NorthX:0.###}|{mm.NorthY:0.###}|{mm.HasNorth}|{side:0}|{zoom:0.###}";
            if (key == overlayKey) return;
            overlayKey = key;
            var keep = hoverBox;
            overlay.Children.Clear();
            if (keep != null) overlay.Children.Add(keep);
            double sc = side / 700.0;
            var dim = UiBrushes.Get(220, 235, 238, 240);
            // Scale: a round length about a sixth of the map's width.
            if (mm.MetresAcross > 1)
            {
                double mPerPx = mm.MetresAcross / (side * zoom);
                double want = mPerPx * side / 6;
                double mag = Math.Pow(10, Math.Floor(Math.Log10(want)));
                double len = mag;
                foreach (double m in new[] { 1, 2, 5, 10 }) if (m * mag <= want) len = m * mag;
                double px = len / mPerPx;
                double x0 = 16 * sc, y0 = side - 18 * sc;
                overlay.Children.Add(new Line { StartPoint = new Point(x0, y0), EndPoint = new Point(x0 + px, y0), Stroke = dim, StrokeThickness = 3 * sc });
                overlay.Children.Add(new Line { StartPoint = new Point(x0, y0 - 6 * sc), EndPoint = new Point(x0, y0 + 2 * sc), Stroke = dim, StrokeThickness = 2 * sc });
                overlay.Children.Add(new Line { StartPoint = new Point(x0 + px, y0 - 6 * sc), EndPoint = new Point(x0 + px, y0 + 2 * sc), Stroke = dim, StrokeThickness = 2 * sc });
                var t = new TextBlock { Text = len >= 1000 ? $"{len / 1000:0.#} km" : $"{len:0} m", FontSize = 14 * sc, FontWeight = FontWeight.Bold, Foreground = dim };
                Canvas.SetLeft(t, x0); Canvas.SetTop(t, y0 - 24 * sc);
                overlay.Children.Add(t);
            }
            // North arrow.
            if (mm.HasNorth)
            {
                var c = new Point(side - 34 * sc, side - 38 * sc);
                double r = 16 * sc;
                var tip = c + new Vector(mm.NorthX, mm.NorthY) * r;
                var tail = c - new Vector(mm.NorthX, mm.NorthY) * r;
                var side1 = c + new Vector(-mm.NorthY, mm.NorthX) * (r * 0.45);
                var side2 = c - new Vector(-mm.NorthY, mm.NorthX) * (r * 0.45);
                var disc = new Ellipse { Width = r * 2.6, Height = r * 2.6, Fill = UiBrushes.Get(160, 10, 12, 16) };
                Canvas.SetLeft(disc, c.X - r * 1.3); Canvas.SetTop(disc, c.Y - r * 1.3);
                overlay.Children.Add(disc);
                overlay.Children.Add(new Polygon { Points = new AvaloniaList<Point> { tip, side1, side2 }, Fill = Brush(HudColor.Red) });
                overlay.Children.Add(new Polygon { Points = new AvaloniaList<Point> { tail, side1, side2 }, Fill = dim });
                var n = new TextBlock { Text = "N", FontSize = 13 * sc, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
                var lp = c + new Vector(mm.NorthX, mm.NorthY) * (r * 1.75);
                Canvas.SetLeft(n, lp.X - 5 * sc); Canvas.SetTop(n, lp.Y - 9 * sc);
                overlay.Children.Add(n);
            }
        }

        // ---- Pointing at the line: a box with the speed, lap time and gap there, and a mark on the height profile.
        private Border hoverBox;

        private int NearestHover(Point at)
        {
            var mm = drawnMap;
            if (mm == null || mm.HoverPoints.Count == 0) return -1;
            double best = 18, bi = -1;
            for (int i = 0; i < mm.HoverPoints.Count; i++)
            {
                var h = mm.HoverPoints[i];
                var p = new Point(h.X * side * zoom + pan.X, h.Y * side * zoom + pan.Y);
                double d = Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));
                if (d < best) { best = d; bi = i; }
            }
            return (int)bi;
        }

        private void ShowHover(int i)
        {
            var mm = drawnMap;
            if (i == hoverIndex && (i < 0 || hoverBox != null)) return;
            hoverIndex = i;
            if (hoverBox != null) { overlay.Children.Remove(hoverBox); hoverBox = null; }
            profileCursor.IsVisible = false;
            if (mm == null || i < 0 || i >= mm.HoverPoints.Count) return;
            var h = mm.HoverPoints[i];
            double sc = side / 700.0;
            var text = new TextBlock { FontSize = 15 * sc, Foreground = Brushes.White, LineHeight = 20 * sc };
            text.Inlines = new Avalonia.Controls.Documents.InlineCollection();
            text.Inlines.Add(new Avalonia.Controls.Documents.Run(GameOptions.Speed(h.Speed)) { FontWeight = FontWeight.Bold });
            if (h.Time >= 0) text.Inlines.Add(new Avalonia.Controls.Documents.Run("\n" + LapTimer.Format(h.Time)));
            if (h.Gap.HasValue)
                text.Inlines.Add(new Avalonia.Controls.Documents.Run("\n" + (h.Gap.Value <= 0 ? $"{-h.Gap.Value:0.000}s ahead" : $"{h.Gap.Value:0.000}s behind"))
                    { Foreground = Brush(h.Gap.Value <= 0 ? HudColor.Green : HudColor.Red) });
            hoverBox = new Border
            {
                Background = UiBrushes.Get(235, 10, 12, 16),
                BorderBrush = UiBrushes.Get(230, 235, 238, 240),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4 * sc),
                Padding = new Thickness(8 * sc, 4 * sc, 8 * sc, 6 * sc),
                Child = text,
            };
            var p = new Point(h.X * side * zoom + pan.X, h.Y * side * zoom + pan.Y);
            // A dot on the line, and the box beside it (kept inside the map).
            var dot = new Ellipse { Width = 10 * sc, Height = 10 * sc, Fill = Brushes.White };
            hoverBox.Measure(Size.Infinity);
            double bx = p.X + 14 * sc, by = p.Y - hoverBox.DesiredSize.Height - 8 * sc;
            if (bx + hoverBox.DesiredSize.Width > side - 4) bx = p.X - hoverBox.DesiredSize.Width - 14 * sc;
            if (by < 4) by = p.Y + 12 * sc;
            var holder = new Canvas();
            Canvas.SetLeft(dot, p.X - 5 * sc); Canvas.SetTop(dot, p.Y - 5 * sc);
            Canvas.SetLeft(hoverBox, bx); Canvas.SetTop(hoverBox, by);
            holder.Children.Add(dot);
            holder.Children.Add(hoverBox);
            hoverBox = new Border { Child = holder };
            overlay.Children.Add(hoverBox);
            if (mm.Profile.Count == mm.HoverPoints.Count && profile.Width > 0)
            {
                double x = mm.Profile[i].Share * profile.Width;
                profileCursor.StartPoint = new Point(x, 0);
                profileCursor.EndPoint = new Point(x, profile.Height);
                profileCursor.IsVisible = true;
            }
        }

        /// <summary>Height round the lap: filled under the line, gates marked (missed in red), highest and lowest labelled.</summary>
        private void BuildProfile(HudModel.MinimapPanel mm)
        {
            profile.Children.Clear();
            if (mm.Profile.Count < 2) return;
            double w = side, h = profile.Height, top = 20, bottom = h - 6;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var (_, z) in mm.Profile) { lo = Math.Min(lo, z); hi = Math.Max(hi, z); }
            if (hi - lo < 10) { double mid = (hi + lo) / 2; lo = mid - 5; hi = mid + 5; }
            double Y(double z) => bottom - (z - lo) / (hi - lo) * (bottom - top);
            var line = new AvaloniaList<Point>();
            foreach (var (share, z) in mm.Profile) line.Add(new Point(share * w, Y(z)));
            // The filled area in short pieces (the game's UI renderer can't take one very long shape).
            for (int start = 0; start < line.Count - 1; start += 39)
            {
                int end = Math.Min(line.Count - 1, start + 39);
                var piece = new AvaloniaList<Point> { new Point(line[start].X, bottom) };
                for (int i = start; i <= end; i++) piece.Add(line[i]);
                piece.Add(new Point(line[end].X, bottom));
                profile.Children.Add(new Polygon { Points = piece, Fill = UiBrushes.Get(70, 90, 210, 255) });
            }
            AddChunked(profile.Children, new Polyline { Points = line, Stroke = Brush(new HudColor(90, 210, 255, 255)), StrokeThickness = 2 });
            foreach (var (share, name, missed) in mm.ProfileMarks)
            {
                double x = share * w;
                var col = missed ? Brush(HudColor.Red) : UiBrushes.Get(140, 255, 170, 60);
                profile.Children.Add(new Line { StartPoint = new Point(x, 16), EndPoint = new Point(x, h), Stroke = col, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 3, 3 } });
                var t = new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = missed ? Brush(HudColor.Red) : UiBrushes.Get(220, 255, 170, 60) };
                Canvas.SetLeft(t, Math.Min(w - 22, Math.Max(0, x - 6))); Canvas.SetTop(t, 1);
                profile.Children.Add(t);
            }
            var hiT = new TextBlock { Text = $"{hi:0} m", FontSize = 11, Foreground = UiBrushes.Get(200, 235, 238, 240) };
            Canvas.SetLeft(hiT, 4); Canvas.SetTop(hiT, top - 2);
            var loT = new TextBlock { Text = $"{lo:0} m", FontSize = 11, Foreground = UiBrushes.Get(200, 235, 238, 240) };
            Canvas.SetLeft(loT, 4); Canvas.SetTop(loT, bottom - 16);
            var title = new TextBlock { Text = "HEIGHT ROUND THE LAP (above the start)", FontSize = 11, Foreground = UiBrushes.Get(170, 235, 238, 240) };
            Canvas.SetLeft(title, w - 250); Canvas.SetTop(title, h - 18);
            profile.Children.Add(hiT);
            profile.Children.Add(loT);
            profile.Children.Add(title);
            profile.Children.Add(profileCursor);
        }

        /// <summary>The key under the map: a swatch and a name for each thing on it.</summary>
        private void BuildLegend(HudModel.MinimapPanel mm, MapOptions opt)
        {
            legend.Children.Clear();
            void Key(Control swatch, string text)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 2, 8, 2) };
                swatch.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(swatch);
                row.Children.Add(new TextBlock { Text = text, FontSize = 14, Foreground = UiBrushes.Get(230, 235, 238, 240), VerticalAlignment = VerticalAlignment.Center });
                legend.Children.Add(row);
            }
            Control Bar(IBrush b, double h = 4) => new Border { Width = 22, Height = h, Background = b, CornerRadius = new CornerRadius(2) };
            if (mm.LegendMine != null)
            {
                if (mm.Line2Colours.Count > 0)
                    Key(new StackPanel { Orientation = Orientation.Horizontal, Children = { Bar(Brush(HudColor.Green), 5), Bar(Brush(HudColor.Red), 5) } },
                        mm.LegendMine + "  (green: gaining, red: losing)");
                else Key(Bar(Brush(HudColor.Orange), 5), mm.LegendMine);
            }
            else if (mm.Line.Count > 1 && mm.LineColours.Count > 0)
                Key(new Border { Width = 34, Height = 4, CornerRadius = new CornerRadius(2), Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromRgb(255, 50, 40), 0), new GradientStop(Color.FromRgb(255, 215, 0), 0.5), new GradientStop(Color.FromRgb(40, 230, 80), 1) },
                } }, "Racing line (slow to fast)");
            else if (mm.Line.Count > 1) Key(Bar(Brush(mm.LineColour.WithAlpha(255))), "Racing line");
            if (mm.LegendRef != null) Key(Bar(Brush(mm.LineColour.WithAlpha(200)), 3), mm.LegendRef);
            if (mm.Diverge.Count > 0) Key(Bar(UiBrushes.Get(120, 255, 70, 60), 10), "Well off the record's line");
            if (opt.Checkpoints)
            {
                Key(new Border { Width = 22, Height = 6, Background = new LinearGradientBrush
                {
                    SpreadMethod = GradientSpreadMethod.Repeat, StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(6, 0, RelativeUnit.Absolute),
                    GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 0.5), new GradientStop(Colors.Black, 0.5), new GradientStop(Colors.Black, 1) },
                } }, "Start / finish");
            }
            if (mm.LegendMine != null)
            {
                Key(new Ellipse { Width = 14, Height = 14, Stroke = Brush(HudColor.Red), StrokeThickness = 2 }, "Missed checkpoint");
                Key(new TextBlock { Text = "X", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Brush(HudColor.Red) }, "Crash");
            }
            // The replay bar above the map: which lap, and play it again.
            bool canReplay = mm.AnimLapTime > 0 && mm.HoverPoints.Count > 1;
            replayBar.IsVisible = canReplay;
            if (canReplay)
            {
                replayLap.Text = mm.LegendMine ?? "Lap";
                replayPrev.IsVisible = replayNext.IsVisible = mm.Laps > 1;
            }
            legend.Children.Add(new TextBlock { Text = "Wheel: zoom   Drag: move   Double-click: reset   Point at the line for details",
                FontSize = 12, Foreground = UiBrushes.Get(160, 235, 238, 240), Margin = new Thickness(8, 4, 8, 0) });
        }

        // ---- After the race: the lap played back on the map: the line drawing itself in, checkpoints turning green
        // (or red when missed) and crashes appearing as they happened. Plays when the results reach the map, and
        // again with Replay lap.
        private readonly Canvas anim = new Canvas { IsHitTestVisible = false };
        private readonly List<Control> line2Shapes = new List<Control>();
        private string animFor;
        private DateTime animStart;
        private bool animating;

        private void StopAnimation()
        {
            if (!animating && anim.Children.Count == 0) return;
            animating = false;
            anim.Children.Clear();
            foreach (var c in line2Shapes) c.Opacity = 1;
            timer.Interval = TimeSpan.FromMilliseconds(100);
        }

        private bool UpdateAnimation(HudModel.MinimapPanel mm, MapOptions opt, double sc, Func<double, double, Point> P)
        {
            var pts = mm.HoverPoints;
            double lapEnd = pts.Count > 1 && pts[pts.Count - 1].Time > 0 ? pts[pts.Count - 1].Time : mm.AnimLapTime;
            if (mm.AnimLapTime <= 0 || pts.Count < 2 || lapEnd <= 0 || !opt.RacingLine) { StopAnimation(); return false; }
            if (animFor != mm.StaticSig) { animFor = mm.StaticSig; animStart = DateTime.Now; animating = true; }
            if (!animating) return false;
            double duration = Math.Clamp(lapEnd / 8, 8, 15);   // seconds the playback takes
            double p = (DateTime.Now - animStart).TotalSeconds / duration;
            if (p >= 1) { StopAnimation(); return false; }
            timer.Interval = TimeSpan.FromMilliseconds(33);
            double t = p * lapEnd;
            foreach (var c in line2Shapes) c.Opacity = 0.2;   // the whole line faintly, the part flown so far bright
            anim.Children.Clear();

            // The line so far, in its gain / loss colours, up to an in-between point for a smooth head.
            int last = 0;
            while (last + 1 < pts.Count && pts[last + 1].Time >= 0 && pts[last + 1].Time <= t) last++;
            var drawn = new List<(double X, double Y)>();
            for (int j = 0; j <= last; j++) drawn.Add((pts[j].X, pts[j].Y));
            var head = (pts[last].X, pts[last].Y);
            if (last + 1 < pts.Count)
            {
                double t0 = pts[last].Time, t1 = pts[last + 1].Time;
                double u = t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0, 1) : 0;
                head = (pts[last].X + (pts[last + 1].X - pts[last].X) * u, pts[last].Y + (pts[last + 1].Y - pts[last].Y) * u);
                drawn.Add(head);
            }
            var cols = new List<HudColor>();
            for (int j = 0; j + 1 < drawn.Count; j++) cols.Add(j < mm.Line2Colours.Count ? mm.Line2Colours[j] : HudColor.Orange.WithAlpha(255));
            if (drawn.Count > 1) AddColouredLine(drawn, cols, 6 * sc, P, anim);

            // Checkpoints: grey until flown (green), red from when they counted as missed.
            foreach (var (point, hit, missedAt) in mm.AnimCheckpoints)
            {
                if (point < 0 || point >= mm.Points.Count) continue;
                var at = P(mm.Points[point].X, mm.Points[point].Y);
                bool flown = hit.HasValue && hit.Value <= t, missed = missedAt.HasValue && missedAt.Value <= t;
                var colour = flown ? HudColor.Green : missed ? HudColor.Red : HudColor.Grey;
                double d = (flown || missed ? 14 : 11) * sc;
                var dot = new Ellipse { Width = d, Height = d, Fill = Brush(colour.WithAlpha(255)), Stroke = Brushes.Black, StrokeThickness = 1 };
                Canvas.SetLeft(dot, at.X - d / 2); Canvas.SetTop(dot, at.Y - d / 2);
                anim.Children.Add(dot);
                if (missed)
                {
                    double r = 26 * sc;
                    var ring = new Ellipse { Width = r, Height = r, Stroke = Brush(HudColor.Red), StrokeThickness = 2.5 * sc };
                    Canvas.SetLeft(ring, at.X - r / 2); Canvas.SetTop(ring, at.Y - r / 2);
                    anim.Children.Add(ring);
                }
            }
            // Crashes, as they happen (with a ring that fades out for the first second).
            for (int i = 0; i < mm.Crashes.Count && i < mm.CrashTimes.Count; i++)
            {
                if (mm.CrashTimes[i] > t) continue;
                var at = P(mm.Crashes[i].X, mm.Crashes[i].Y);
                double r = 8 * sc;
                anim.Children.Add(new Line { StartPoint = new Point(at.X - r, at.Y - r), EndPoint = new Point(at.X + r, at.Y + r), Stroke = Brush(HudColor.Red), StrokeThickness = 3 * sc });
                anim.Children.Add(new Line { StartPoint = new Point(at.X - r, at.Y + r), EndPoint = new Point(at.X + r, at.Y - r), Stroke = Brush(HudColor.Red), StrokeThickness = 3 * sc });
                double age = (t - mm.CrashTimes[i]) / lapEnd * duration;   // real seconds since it appeared
                if (age < 1)
                {
                    double rr = (20 + 40 * age) * sc;
                    var flash = new Ellipse { Width = rr, Height = rr, Stroke = Brush(HudColor.Red), StrokeThickness = 3 * sc, Opacity = 1 - age };
                    Canvas.SetLeft(flash, at.X - rr / 2); Canvas.SetTop(flash, at.Y - rr / 2);
                    anim.Children.Add(flash);
                }
            }
            // The head, with the lap time there.
            var hp = P(head.Item1, head.Item2);
            double hd = 14 * sc;
            var dotHead = new Ellipse { Width = hd, Height = hd, Fill = Brushes.White, Stroke = Brush(HudColor.Cyan), StrokeThickness = 3 * sc };
            Canvas.SetLeft(dotHead, hp.X - hd / 2); Canvas.SetTop(dotHead, hp.Y - hd / 2);
            anim.Children.Add(dotHead);
            var time = new Border
            {
                Background = UiBrushes.Get(225, 10, 12, 16), CornerRadius = new CornerRadius(3 * sc),
                Padding = new Thickness(5 * sc, 1 * sc, 5 * sc, 2 * sc),
                Child = new TextBlock { Text = LapTimer.Format(t), FontSize = 15 * sc, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
            };
            Canvas.SetLeft(time, hp.X + 12 * sc); Canvas.SetTop(time, hp.Y - 30 * sc);
            anim.Children.Add(time);
            return true;
        }

        private void Tick()
        {
            try
            {
                bool visible = IsEffectivelyVisible && this.GetVisualRootSafe() != null;
                shown = visible;
                if (!visible) { ShowMapOverlays(); return; }
                HideMapOverlays();
                var mm = latest;
                empty.IsVisible = mm == null;
                frame.IsVisible = mm != null;
                profileFrame.IsVisible = mm != null && mm.Profile.Count > 1;
                legend.IsVisible = mm != null;
                if (mm == null) return;
                drawnMap = mm;

                // The map is square, as big as fits, in the middle of the page, with the height profile and key under it.
                double below = (mm.Profile.Count > 1 ? 118 : 0) + 44;
                this.side = Math.Max(100, Math.Min(mapArea.Bounds.Width - 20, mapArea.Bounds.Height - below - 20));
                double side = this.side;
                var opt = MapOptions.Current;
                frame.Width = side; frame.Height = side;
                profile.Width = side;
                legend.MaxWidth = side;
                frame.HorizontalAlignment = HorizontalAlignment.Center;
                UpdateOverlay(mm);
                double sc = side / 700.0;   // text and line sizes grow with the map
                Point P(double x, double y) => new Point(x * side, y * side);

                // Route and lines: when they or the size change.
                var size = new Size(side, side);
                if (mm.StaticSig != drawnSig || size != drawnSize)
                {
                    drawnSig = mm.StaticSig; drawnSize = size;
                    sectorInfo.Clear();
                    lines.Children.Clear();
                    // Drawn bottom to top: sector glow, stretches off the record's line, the route, gate bars,
                    // the record's line (thin), then the main line on top.
                    bool hasLine = opt.RacingLine && (mm.Line.Count > 1 || mm.Line2.Count > 1);
                    foreach (var sec in opt.Sectors ? mm.Sectors : new List<(List<(double X, double Y)> Points, string Name, HudColor Colour)>())
                    {
                        if (sec.Points.Count < 2) continue;
                        AddChunked(lines.Children, new Polyline
                        {
                            Points = new AvaloniaList<Point>(sec.Points.ConvertAll(q => P(q.X, q.Y))),
                            Stroke = Brush(sec.Colour.WithAlpha(70)),
                            StrokeThickness = 16 * sc,
                            StrokeLineCap = PenLineCap.Round,
                            StrokeJoin = PenLineJoin.Round,
                        });
                        var pts = sec.Points.ConvertAll(q => P(q.X, q.Y));
                        var (mid, normal) = LabelPlacer.Halfway(pts, new Point(side / 2, side / 2));
                        sectorInfo.Add((sec.Name, sec.Colour, mid, normal));
                    }
                    if (opt.RacingLine)
                        foreach (var run in mm.Diverge)
                            AddChunked(lines.Children, new Polyline
                            {
                                Points = new AvaloniaList<Point>(run.ConvertAll(q => P(q.X, q.Y))),
                                Stroke = UiBrushes.Get(80, 255, 70, 60),
                                StrokeThickness = 11 * sc,
                                StrokeLineCap = PenLineCap.Round,
                                StrokeJoin = PenLineJoin.Round,
                            });
                    if (opt.Route && mm.Route.Count > 1)
                        AddChunked(lines.Children, new Polyline
                        {
                            Points = new AvaloniaList<Point>(mm.Route.ConvertAll(p => P(p.X, p.Y))),
                            Stroke = hasLine ? UiBrushes.Get(60, 235, 235, 245) : UiBrushes.Get(150, 235, 235, 245),   // faint under a racing line
                            StrokeThickness = (hasLine ? 1.5 : 2) * sc,
                            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
                        });
                    // Gates: a bar across the course as wide as the gate; start / finish chequered black and white.
                    if (opt.Checkpoints)
                        foreach (var gb in mm.GateBars)
                        {
                            var a1 = P(gb.X1, gb.Y1); var a2 = P(gb.X2, gb.Y2);
                            Vector d = a2 - a1;
                            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                            if (len < 10 * sc)   // tiny on this map: give it a readable size
                            {
                                var c = new Point((a1.X + a2.X) / 2, (a1.Y + a2.Y) / 2);
                                var u = len > 1e-6 ? d / len : new Vector(1, 0);
                                a1 = c - u * 5 * sc; a2 = c + u * 5 * sc;
                            }
                            if (gb.StartFinish)
                            {
                                lines.Children.Add(new Line { StartPoint = a1, EndPoint = a2, Stroke = Brushes.White, StrokeThickness = 6 * sc });
                                lines.Children.Add(new Line { StartPoint = a1, EndPoint = a2, Stroke = Brushes.Black, StrokeThickness = 6 * sc, StrokeDashArray = new AvaloniaList<double> { 1, 1 } });
                            }
                            else lines.Children.Add(new Line { StartPoint = a1, EndPoint = a2, Stroke = UiBrushes.Get(200, 235, 238, 240), StrokeThickness = 3 * sc, StrokeLineCap = PenLineCap.Round });
                        }
                    bool two = mm.Line2.Count > 1;
                    if (!opt.RacingLine) { }
                    else if (mm.LineColours.Count > 0)
                        AddColouredLine(mm.Line, mm.LineColours, 4 * sc, P);
                    else if (mm.Line.Count > 1)
                        AddChunked(lines.Children, new Polyline
                        {
                            Points = new AvaloniaList<Point>(mm.Line.ConvertAll(p => P(p.X, p.Y))),
                            Stroke = Brush(mm.LineColour.WithAlpha((byte)(two ? 190 : 230))),
                            StrokeThickness = (two ? 2.5 : 4) * sc,
                            StrokeJoin = PenLineJoin.Round,
                        });
                    int line2From = lines.Children.Count;
                    if (opt.RacingLine && two)
                    {
                        // Your lap on top: coloured by where it gained (green) or lost (red) time on the record when known.
                        if (mm.Line2Colours.Count > 0) AddColouredLine(mm.Line2, mm.Line2Colours, 5 * sc, P);
                        else
                            AddChunked(lines.Children, new Polyline
                            {
                                Points = new AvaloniaList<Point>(mm.Line2.ConvertAll(p => P(p.X, p.Y))),
                                Stroke = Brush(HudColor.Orange),
                                StrokeThickness = 5 * sc,
                                StrokeJoin = PenLineJoin.Round,
                            });
                    }
                    line2Shapes.Clear();
                    for (int k = line2From; k < lines.Children.Count; k++) line2Shapes.Add(lines.Children[k]);
                    BuildProfile(mm);
                    BuildLegend(mm, opt);
                }

                // Gates, checkpoints and their labels, the next one ringed, then the ghosts and you. Only redrawn
                // when something on it has moved (by a pixel) or changed colour.
                var ls = new System.Text.StringBuilder(drawnSig).Append(side.ToString("0")).Append(opt.Checkpoints).Append(opt.Labels).Append(opt.Ghosts).Append(opt.You).Append(opt.Sectors).Append(mm.NextIndex).Append('|');
                foreach (var pt in mm.Points) ls.Append(pt.Colour.R).Append(pt.Colour.G).Append(pt.Colour.B).Append(',');
                foreach (var g in mm.Ghosts) ls.Append('g').Append((int)(g.X * side)).Append(',').Append((int)(g.Y * side));
                foreach (var c in mm.Crashes) ls.Append('x').Append((int)(c.X * side)).Append(',').Append((int)(c.Y * side));
                if (mm.PlayerX.HasValue) ls.Append('p').Append((int)(mm.PlayerX.Value * side)).Append(',').Append((int)(mm.PlayerY.Value * side)).Append(',').Append((int)mm.PlayerAngle);
                string sigNow = ls.ToString();
                bool animNow = UpdateAnimation(mm, opt, sc, P);
                if (animNow) sigNow += "A";
                if (sigNow == liveSig) return;
                liveSig = sigNow;
                live.Children.Clear();
                // Everything placed so far, so labels can keep clear of it.
                var taken = new List<Rect>();
                var frameRect = new Rect(4, 4, side - 8, side - 8);
                for (int i = 0; i < (opt.Checkpoints ? mm.Points.Count : 0); i++)
                {
                    var pt = mm.Points[i];
                    var at = P(pt.X, pt.Y);
                    bool next = i == mm.NextIndex;
                    double d = (pt.Gate ? 16 : next ? 15 : 11) * sc;
                    if (next)
                    {
                        double r = 30 * sc;
                        var ring = new Ellipse { Width = r, Height = r, Stroke = Brush(pt.Colour.WithAlpha(255)), StrokeThickness = 3 * sc };
                        Canvas.SetLeft(ring, at.X - r / 2); Canvas.SetTop(ring, at.Y - r / 2);
                        live.Children.Add(ring);
                    }
                    var dot = new Ellipse
                    {
                        Width = d, Height = d,
                        Fill = Brush(pt.Colour.WithAlpha(255)),
                        Stroke = pt.Gate || next ? Brushes.White : null,
                        StrokeThickness = 1.5 * sc,
                    };
                    Canvas.SetLeft(dot, at.X - d / 2); Canvas.SetTop(dot, at.Y - d / 2);
                    live.Children.Add(dot);
                    if (!pt.Gate && pt.Colour.R == HudColor.Red.R && pt.Colour.G == HudColor.Red.G && pt.Colour.B == HudColor.Red.B)
                    {
                        // Missed: a red ring with a cross, so it can't be mistaken for one flown.
                        double r = 26 * sc;
                        var ring = new Ellipse { Width = r, Height = r, Stroke = Brush(HudColor.Red), StrokeThickness = 2.5 * sc };
                        Canvas.SetLeft(ring, at.X - r / 2); Canvas.SetTop(ring, at.Y - r / 2);
                        live.Children.Add(ring);
                        double x = 7 * sc;
                        live.Children.Add(new Line { StartPoint = new Point(at.X - x, at.Y - x), EndPoint = new Point(at.X + x, at.Y + x), Stroke = Brushes.White, StrokeThickness = 2 * sc });
                        live.Children.Add(new Line { StartPoint = new Point(at.X - x, at.Y + x), EndPoint = new Point(at.X + x, at.Y - x), Stroke = Brushes.White, StrokeThickness = 2 * sc });
                    }
                    double pad = (next ? 30 : d + 6) * sc;
                    taken.Add(new Rect(at.X - pad / 2, at.Y - pad / 2, pad, pad));
                }
                for (int i = 0; i < (opt.Checkpoints ? mm.Points.Count : 0); i++)
                {
                    var pt = mm.Points[i];
                    var at = P(pt.X, pt.Y);
                    if (string.IsNullOrEmpty(pt.Label) || !opt.Labels) continue;
                    var label = new Border
                    {
                        Background = UiBrushes.Get(225, 10, 12, 16),
                        CornerRadius = new CornerRadius(3 * sc),
                        Padding = new Thickness(5 * sc, 1 * sc, 5 * sc, 2 * sc),
                        Child = new TextBlock { Text = pt.Label, FontSize = 18 * sc, FontWeight = FontWeight.Bold, Foreground = Brush(pt.Colour.WithAlpha(255)) },
                    };
                    // Away from the middle of the map, so it doesn't sit on the route.
                    double dx = at.X - side / 2, dy = at.Y - side / 2, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-3) { dx = 1; dy = 0; len = 1; }
                    double w = (pt.Label.Length * 11 + 12) * sc, h = 26 * sc;
                    var spot = LabelPlacer.Place(at, new Vector(dx / len, dy / len), w, h, taken, r => frameRect.Contains(r), 6 * sc);
                    Canvas.SetLeft(label, spot.X); Canvas.SetTop(label, spot.Y);
                    live.Children.Add(label);
                }

                // Sector names: clear of the checkpoints, their labels and each other.
                foreach (var info in sectorInfo)
                {
                    double lw = (info.Name.Length * 10.5 + 18) * sc, lh = 30 * sc;
                    var spot = LabelPlacer.Place(info.Mid, info.Normal, lw, lh, taken, r => frameRect.Contains(r), 8 * sc);
                    var tag = new Border
                    {
                        Background = UiBrushes.Get(230, 10, 12, 16),
                        BorderBrush = Brush(info.Colour.WithAlpha(220)),
                        BorderThickness = new Thickness(1.5),
                        CornerRadius = new CornerRadius(4 * sc),
                        Padding = new Thickness(7 * sc, 2 * sc, 7 * sc, 3 * sc),
                        Child = new TextBlock { Text = info.Name, FontSize = 18 * sc, FontWeight = FontWeight.Bold, Foreground = Brush(info.Colour.WithAlpha(255)) },
                    };
                    Canvas.SetLeft(tag, spot.X); Canvas.SetTop(tag, spot.Y);
                    // A thin leader from the label to its stretch of route when it had to move away.
                    var c = spot.Center;
                    if (LabelPlacer.Sub(c, info.Mid).Length > 30 * sc)
                        live.Children.Add(new Line { StartPoint = info.Mid, EndPoint = c, Stroke = Brush(info.Colour.WithAlpha(160)), StrokeThickness = 1.5 * sc });
                    live.Children.Add(tag);
                }
                // After the race: a red X where you crashed on the lap shown (the replay shows them as they happen).
                foreach (var c in animNow ? new List<(double X, double Y)>() : mm.Crashes)
                {
                    var at = P(c.X, c.Y);
                    double r = 8 * sc;
                    live.Children.Add(new Line { StartPoint = new Point(at.X - r, at.Y - r), EndPoint = new Point(at.X + r, at.Y + r), Stroke = Brush(HudColor.Red), StrokeThickness = 3 * sc });
                    live.Children.Add(new Line { StartPoint = new Point(at.X - r, at.Y + r), EndPoint = new Point(at.X + r, at.Y - r), Stroke = Brush(HudColor.Red), StrokeThickness = 3 * sc });
                }
                foreach (var g in opt.Ghosts ? mm.Ghosts : new List<(double X, double Y, HudColor Colour)>())
                {
                    var at = P(g.X, g.Y);
                    double d = 14 * sc;
                    var e = new Ellipse { Width = d, Height = d, Fill = Brush(g.Colour.WithAlpha(255)), Stroke = Brushes.White, StrokeThickness = 1.5 * sc };
                    Canvas.SetLeft(e, at.X - d / 2); Canvas.SetTop(e, at.Y - d / 2);
                    live.Children.Add(e);
                }
                if (opt.You && mm.PlayerX.HasValue && mm.PlayerY.HasValue)
                {
                    var at = P(mm.PlayerX.Value, mm.PlayerY.Value);
                    double a = 24 * sc;
                    var arrow = new Polygon
                    {
                        Points = new AvaloniaList<Point> { new Point(a / 2, 0), new Point(a * 0.875, a * 0.94), new Point(a / 2, a * 0.69), new Point(a * 0.125, a * 0.94) },
                        Width = a, Height = a,
                        Fill = Brush(HudColor.Cyan), Stroke = Brushes.White, StrokeThickness = 1.5 * sc,
                        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                        RenderTransform = new RotateTransform(mm.PlayerAngle),
                    };
                    Canvas.SetLeft(arrow, at.X - a / 2); Canvas.SetTop(arrow, at.Y - a / 2);
                    live.Children.Add(arrow);
                }
            }
            catch (Exception e)
            {
                timer.Stop();
                PluginLog.Error("Race Map: drawing failed", e);
            }
        }
    }

    /// <summary>What the Race Map and the minimap show (switches above the Race Map), remembered in RaceMap.json next to the settings.</summary>
    internal sealed class MapOptions
    {
        public bool Sectors { get; set; } = true;
        public bool Checkpoints { get; set; } = true;
        public bool Labels { get; set; } = true;
        public bool Route { get; set; } = true;
        public bool RacingLine { get; set; } = true;
        public bool Ghosts { get; set; } = true;
        public bool You { get; set; } = true;

        public static MapOptions Current = Load();
        private static string FilePath => System.IO.Path.Combine(PluginPaths.Folder, "RaceMap.json");

        private static MapOptions Load()
        {
            try { if (System.IO.File.Exists(FilePath)) return System.Text.Json.JsonSerializer.Deserialize<MapOptions>(System.IO.File.ReadAllText(FilePath)) ?? new MapOptions(); }
            catch { }
            return new MapOptions();
        }
        /// <summary>Raised when a switch changes (the minimap redraws).</summary>
        public static event Action Changed;

        public static void Save()
        {
            Changed?.Invoke();
            try
            {
                System.IO.Directory.CreateDirectory(PluginPaths.Folder);
                System.IO.File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(Current));
            }
            catch { }
        }

        public static readonly (string Name, Func<bool> Get, Action<bool> Set)[] All =
        {
            ("Sectors", () => Current.Sectors, v => Current.Sectors = v),
            ("Checkpoints", () => Current.Checkpoints, v => Current.Checkpoints = v),
            ("Labels", () => Current.Labels, v => Current.Labels = v),
            ("Route", () => Current.Route, v => Current.Route = v),
            ("Racing line", () => Current.RacingLine, v => Current.RacingLine = v),
            ("Ghosts", () => Current.Ghosts, v => Current.Ghosts = v),
            ("You", () => Current.You, v => Current.You = v),
        };
    }


    internal static class VisualRootHelper
    {
        public static object GetVisualRootSafe(this Visual v)
        {
            try { return Avalonia.VisualTree.VisualExtensions.GetVisualRoot(v); } catch { return null; }
        }
    }
}
