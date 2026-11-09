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

        /// <summary>A switch above the map: lit when on, dim when off; click to change.</summary>
        private Control MakeSwitch(string name, Func<bool> get, Action<bool> set)
        {
            var text = new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(12, 5, 12, 6),
                Margin = new Thickness(4),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            void Look()
            {
                bool on = get();
                chip.Background = new SolidColorBrush(on ? Color.FromArgb(255, 20, 60, 72) : Color.FromArgb(255, 18, 22, 26));
                chip.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(255, 90, 210, 240) : Color.FromArgb(160, 120, 128, 135));
                text.Foreground = new SolidColorBrush(on ? Colors.White : Color.FromArgb(200, 150, 156, 162));
            }
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
            canvas.Children.Add(lines);
            canvas.Children.Add(live);
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
            var switches = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (name, get, set) in MapOptions.All)
                switches.Children.Add(MakeSwitch(name, get, set));
            Grid.SetRow(switches, 0);
            Children.Add(switches);
            mapArea.Children.Add(frame);
            mapArea.Children.Add(empty);
            Grid.SetRow(mapArea, 1);
            Children.Add(mapArea);

            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, __) => Tick());
            AttachedToVisualTree += (_, __) => { timer.Start(); Tick(); };
            DetachedFromVisualTree += (_, __) => { timer.Stop(); shown = false; };
        }

        private readonly List<(string Name, HudColor Colour, Point Mid, Vector Normal)> sectorInfo = new List<(string, HudColor, Point, Vector)>();

        private static IBrush Brush(HudColor c) => UiBrushes.Of(c);
        private string liveSig;   // what the dots / labels / ghosts / you were last drawn for

        private void Tick()
        {
            try
            {
                bool visible = IsEffectivelyVisible && this.GetVisualRootSafe() != null;
                shown = visible;
                if (!visible) return;
                var mm = latest;
                empty.IsVisible = mm == null;
                frame.IsVisible = mm != null;
                if (mm == null) return;

                // The map is square, as big as fits, in the middle of the page.
                double side = Math.Max(100, Math.Min(mapArea.Bounds.Width, mapArea.Bounds.Height) - 20);
                var opt = MapOptions.Current;
                frame.Width = side; frame.Height = side;
                frame.HorizontalAlignment = HorizontalAlignment.Center;
                frame.VerticalAlignment = VerticalAlignment.Center;
                double sc = side / 700.0;   // text and line sizes grow with the map
                Point P(double x, double y) => new Point(x * side, y * side);

                // Route and lines: when they or the size change.
                var size = new Size(side, side);
                if (mm.StaticSig != drawnSig || size != drawnSize)
                {
                    drawnSig = mm.StaticSig; drawnSize = size;
                    sectorInfo.Clear();
                    lines.Children.Clear();
                    // Named sectors underneath: a wide see-through band along the route, and the name halfway along.
                    foreach (var sec in opt.Sectors ? mm.Sectors : new List<(List<(double X, double Y)> Points, string Name, HudColor Colour)>())
                    {
                        if (sec.Points.Count < 2) continue;
                        lines.Children.Add(new Polyline
                        {
                            Points = new AvaloniaList<Point>(sec.Points.ConvertAll(q => P(q.X, q.Y))),
                            Stroke = Brush(sec.Colour.WithAlpha(90)),
                            StrokeThickness = 18 * sc,
                            StrokeLineCap = PenLineCap.Round,
                            StrokeJoin = PenLineJoin.Round,
                        });
                        var pts = sec.Points.ConvertAll(q => P(q.X, q.Y));
                        var (mid, normal) = LabelPlacer.Halfway(pts, new Point(side / 2, side / 2));
                        sectorInfo.Add((sec.Name, sec.Colour, mid, normal));
                    }
                    if (opt.Route && mm.Route.Count > 1)
                        lines.Children.Add(new Polyline
                        {
                            Points = new AvaloniaList<Point>(mm.Route.ConvertAll(p => P(p.X, p.Y))),
                            Stroke = UiBrushes.Get(150, 235, 235, 245),
                            StrokeThickness = 2 * sc,
                            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
                        });
                    if (!opt.RacingLine) { }
                    else if (mm.LineColours.Count > 0)
                    {
                        // Segments grouped by colour (rounded to a few shades): one path per colour, not one line each.
                        var groups = new Dictionary<uint, StreamGeometry>();
                        var open = new Dictionary<uint, (StreamGeometryContext Ctx, int Last)>();
                        for (int i = 0; i + 1 < mm.Line.Count && i < mm.LineColours.Count; i++)
                        {
                            var c = mm.LineColours[i];
                            uint key = (uint)(c.R & 0xF0) << 16 | (uint)(c.G & 0xF0) << 8 | (uint)(c.B & 0xF0);
                            if (!open.TryGetValue(key, out var o))
                            {
                                var g = new StreamGeometry();
                                groups[key] = g;
                                o = (g.Open(), -2);
                            }
                            if (o.Last != i - 1)
                            {
                                if (o.Last >= 0) o.Ctx.EndFigure(false);
                                o.Ctx.BeginFigure(P(mm.Line[i].X, mm.Line[i].Y), false);
                            }
                            o.Ctx.LineTo(P(mm.Line[i + 1].X, mm.Line[i + 1].Y));
                            open[key] = (o.Ctx, i);
                        }
                        foreach (var kv in open) { kv.Value.Ctx.EndFigure(false); kv.Value.Ctx.Dispose(); }
                        foreach (var kv in groups)
                            lines.Children.Add(new Path
                            {
                                Data = kv.Value,
                                Stroke = UiBrushes.Get(255, (byte)(kv.Key >> 16 | 0x08), (byte)((kv.Key >> 8 & 0xFF) | 0x08), (byte)((kv.Key & 0xFF) | 0x08)),
                                StrokeThickness = 4 * sc,
                                StrokeLineCap = PenLineCap.Round,
                                StrokeJoin = PenLineJoin.Round,
                            });
                    }
                    else if (mm.Line.Count > 1)
                        lines.Children.Add(new Polyline
                        {
                            Points = new AvaloniaList<Point>(mm.Line.ConvertAll(p => P(p.X, p.Y))),
                            Stroke = Brush(mm.LineColour.WithAlpha(230)),
                            StrokeThickness = 4 * sc,
                            StrokeJoin = PenLineJoin.Round,
                        });
                    if (opt.RacingLine && mm.Line2.Count > 1)
                        lines.Children.Add(new Polyline
                        {
                            Points = new AvaloniaList<Point>(mm.Line2.ConvertAll(p => P(p.X, p.Y))),
                            Stroke = Brush(HudColor.Orange),
                            StrokeThickness = 4 * sc,
                            StrokeJoin = PenLineJoin.Round,
                        });
                }

                // Gates, checkpoints and their labels, the next one ringed, then the ghosts and you. Only redrawn
                // when something on it has moved (by a pixel) or changed colour.
                var ls = new System.Text.StringBuilder(drawnSig).Append(side.ToString("0")).Append(opt.Checkpoints).Append(opt.Labels).Append(opt.Ghosts).Append(opt.You).Append(opt.Sectors).Append(mm.NextIndex).Append('|');
                foreach (var pt in mm.Points) ls.Append(pt.Colour.R).Append(pt.Colour.G).Append(pt.Colour.B).Append(',');
                foreach (var g in mm.Ghosts) ls.Append('g').Append((int)(g.X * side)).Append(',').Append((int)(g.Y * side));
                if (mm.PlayerX.HasValue) ls.Append('p').Append((int)(mm.PlayerX.Value * side)).Append(',').Append((int)(mm.PlayerY.Value * side)).Append(',').Append((int)mm.PlayerAngle);
                string sigNow = ls.ToString();
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
