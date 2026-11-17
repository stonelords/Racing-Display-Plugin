using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// Adds RPI lines to three of the game's own panels, without touching the game's own (bound) text:
    ///  - the block placer, next to the block's PCU ("Placing: Atmospheric Thruster 1 m ... 150 PCU");
    ///  - the build menu's block details, next to "PCU: 150";
    ///  - the grid info panel in the terminal (under "Owner: Player"), as the grid's RPI total.
    /// The game's text is found by walking the UI tree (on the UI thread, twice a second); the RPI numbers are worked out
    /// on the game thread from the names found there.
    /// </summary>
    internal static class GameUiExtras
    {
        // UI thread -> game thread: what's showing.
        private static volatile string placingName, menuName, gridName;
        private static volatile bool wantNames;
        // Game thread -> UI thread: what to show (null = nothing), and every block's shown name ("Atmospheric Thruster 1 m").
        private static volatile string placingText, menuText, gridText;
        private static volatile HashSet<string> knownNames;

        private static double lastPost;
        private static int posted;
        private static readonly Dictionary<string, List<string>> keysByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static bool loggedPlacer, loggedMenu, loggedGrid, loggedNoMatch, loggedNoClass;

        /// <summary>Game thread, every frame.</summary>
        public static void Frame(RaceGateController controller, double now)
        {
            // (Several times a second: cheap now the names are looked up in a ready-made index.)
            if (now - lastPost < 0.15 && now >= lastPost) return;
            lastPost = now;
            // The game's block list and the name index, read once on a background thread a little after the game
            // starts, so the first time a panel shows RPI there's nothing left to read (that took half a second).
            if (knownNames == null && (prewarm == null || prewarm.IsCompleted) && now - prewarmAt > 20)
            {
                prewarmAt = now;
                prewarm = System.Threading.Tasks.Task.Run(BuildIndex);
            }
            try
            {
                if (wantNames && knownNames == null && (prewarm == null || prewarm.IsCompleted)) BuildIndex();
                placingText = TextFor(controller, placingName);
                menuText = TextFor(controller, menuName);
                string g = gridName;
                gridText = g != null ? controller.GridRpiText(g) : null;
            }
            catch (Exception e) { PluginLog.Info("Game panel RPI: " + e.Message); }
            if (System.Threading.Interlocked.Exchange(ref posted, 1) == 0)
                Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
        }

        private static System.Threading.Tasks.Task prewarm;
        private static double prewarmAt = -100;
        private static volatile Dictionary<string, List<string>> nameIndex;

        /// <summary>Every block's shown names ("Atmospheric Thruster 1 m", and "Atmospheric Thruster" for all its sizes) to its keys.</summary>
        private static void BuildIndex()
        {
            try
            {
                var blocks = Se2Bridge.AllBlocks();
                if (blocks.Count == 0) return;
                var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                void Add(string name, string key)
                {
                    name = Norm(name);
                    if (string.IsNullOrEmpty(name)) return;
                    if (!index.TryGetValue(name, out var keys)) index[name] = keys = new List<string>();
                    if (!keys.Contains(key)) keys.Add(key);
                }
                foreach (var b in blocks) Add(Sized(b), b.Key);
                foreach (var b in blocks) Add(b.Name, b.Key);
                nameIndex = index;
                knownNames = new HashSet<string>(index.Keys, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) { PluginLog.Info("Game panel RPI: block names not read: " + e.Message); }
        }

        private static string TextFor(RaceGateController controller, string shown)
        {
            if (shown == null) return null;
            var keys = KeysFor(shown);
            if (keys.Count == 0) return null;
            string text = controller.RpiTextFor(keys);
            if (text == null && !loggedNoClass)
            {
                loggedNoClass = true;
                PluginLog.Info($"Game panel RPI: '{shown}' is {string.Join(", ", keys)}, but no RPI class is picked (menu > RPI tab), so there's nothing to show");
            }
            return text;
        }

        private static string Sized(Se2Bridge.BlockEntry b) => $"{b.Name} {b.Size.ToString("0.##", CultureInfo.InvariantCulture)} m";

        /// <summary>Spaces of any kind (the game uses a no-break space before "m") as one plain space.</summary>
        private static string Norm(string s)
        {
            if (s == null) return null;
            var sb = new System.Text.StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s.Trim())
            {
                if (char.IsWhiteSpace(c) || c == ' ' || c == ' ' || c == ' ')
                {
                    if (!space) sb.Append(' ');
                    space = true;
                }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString();
        }

        /// <summary>"Atmospheric Thruster 1 m" -> the block keys with that name (and size).</summary>
        private static List<string> KeysFor(string shown)
        {
            shown = Norm(shown);
            if (keysByName.TryGetValue(shown, out var keys)) return keys;
            var index = nameIndex;
            if (index != null)
            {
                keys = index.TryGetValue(shown, out var found) ? found : new List<string>();
                if (keys.Count > 0) { keysByName[shown] = keys; return keys; }
            }
            keys = new List<string>();
            var blocks = Se2Bridge.AllBlocks();
            foreach (var b in blocks)
                if (string.Equals(Norm(Sized(b)), shown, StringComparison.OrdinalIgnoreCase)) keys.Add(b.Key);
            if (keys.Count == 0)
                foreach (var b in blocks)
                    if (string.Equals(Norm(b.Name), shown, StringComparison.OrdinalIgnoreCase)) keys.Add(b.Key);
            if (keys.Count == 0 && !loggedNoMatch && blocks.Count > 0)
            {
                loggedNoMatch = true;
                PluginLog.Info($"Game panel RPI: '{shown}' ({string.Join(" ", shown.Select(c => ((int)c).ToString("X")))}) matched no block. Names look like: "
                    + string.Join("; ", blocks.Take(6).Select(Sized)));
            }
            keysByName[shown] = keys;
            return keys;
        }

        // ---- UI thread

        private static WeakReference<TextBlock> pcuRef, menuPcuRef, ownerRef, placingRef, gridRef;
        private static TextBlock placerLine, menuLine, gridLine;
        private static long lastSearchMs;

        private static void Tick()
        {
            System.Threading.Interlocked.Exchange(ref posted, 0);
            try
            {
                var top = RacingMenu.Instance?.RootPanel?.GetVisualRoot() as Visual;
                if (top == null) return;

                TextBlock pcu = Live(pcuRef), menuPcu = Live(menuPcuRef), owner = Live(ownerRef), placing = Live(placingRef), grid = Live(gridRef);
                // (A full look through the UI about twice a second at most: it's only needed when a panel opens.)
                long nowMs = Environment.TickCount64;
                if ((pcu == null || menuPcu == null || owner == null || placing == null || grid == null) && nowMs - lastSearchMs >= 450)
                {
                    lastSearchMs = nowMs;
                    Search(top, ref pcu, ref menuPcu, ref owner, ref placing, ref grid);
                }

                // Block placer: "Placing: Atmospheric Thruster 1 m" ... "150 PCU".
                string name = placing != null ? After(placing, "Placing:") : null;
                placingName = string.IsNullOrWhiteSpace(name) ? null : Norm(name);
                if (pcu != null && placingName != null)
                {
                    wantNames = true;
                    if (!loggedPlacer) { loggedPlacer = true; PluginLog.Info($"Block placer RPI: found '{Text(placing)}' and '{Text(pcu)}' in {pcu.Parent?.GetType().Name}"); }
                    placerLine = Beside(placerLine, pcu, ShortRpi(placingText));
                }
                else Hide(placerLine);

                // Build menu: the block's name somewhere above "PCU: 150" in the detail panel.
                string mName = menuPcu != null ? NameNear(menuPcu) : null;
                menuName = mName;
                if (menuPcu != null)
                {
                    wantNames = true;
                    if (!loggedMenu && mName != null) { loggedMenu = true; PluginLog.Info($"Build menu RPI: found '{mName}' and '{Text(menuPcu)}' in {menuPcu.Parent?.GetType().Name}"); }
                    menuLine = Beside(menuLine, menuPcu, mName != null ? ShortRpi(menuText) : null);
                }
                else Hide(menuLine);

                // Grid info panel: a line under "Owner: Player".
                string gn = grid != null ? After(grid, "Grid:") : null;
                gridName = string.IsNullOrWhiteSpace(gn) ? null : gn.Trim();
                if (owner != null && gridName != null)
                {
                    if (!loggedGrid) { loggedGrid = true; PluginLog.Info($"Grid info RPI: found '{Text(grid)}' and '{Text(owner)}' in {owner.Parent?.GetType().Name}"); }
                    gridLine = Below(gridLine, owner);
                    Show(gridLine, gridText);
                }
                else Hide(gridLine);

                SyncShadow(placerLine, pcu); SyncShadow(menuLine, menuPcu);
                pcuRef = Ref(pcu); menuPcuRef = Ref(menuPcu); ownerRef = Ref(owner); placingRef = Ref(placing); gridRef = Ref(grid);
            }
            catch (Exception e) { PluginLog.Info("Game panel RPI (UI): " + e.Message); }
        }

        /// <summary>"RPI: 12  (Class)  at most 4" -> "RPI 12  at most 4" for the short line by the PCU.</summary>
        private static string ShortRpi(string full)
        {
            if (string.IsNullOrEmpty(full)) return null;
            string s = full.StartsWith("RPI: ") ? "RPI " + full.Substring(5) : full;
            int cls = s.IndexOf("  (", StringComparison.Ordinal);
            if (cls > 0)
            {
                int close = s.IndexOf(')', cls);
                s = s.Substring(0, cls) + (close > 0 && close + 1 < s.Length ? "  " + s.Substring(close + 1).Trim() : "");
            }
            return s;
        }

        private static TextBlock Live(WeakReference<TextBlock> r) =>
            r != null && r.TryGetTarget(out var t) && t.GetVisualRoot() != null && t.IsEffectivelyVisible ? t : null;

        private static WeakReference<TextBlock> Ref(TextBlock t) => t != null ? new WeakReference<TextBlock>(t) : null;

        private static string Text(TextBlock t)
        {
            if (t == null) return null;
            string s = t.Text;
            if (string.IsNullOrEmpty(s) && t.Inlines != null && t.Inlines.Count > 0)
                s = string.Concat(t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
            return s;
        }

        /// <summary>The text after a "Label:" (or the next text along, when the label is its own TextBlock).</summary>
        private static string After(TextBlock t, string label)
        {
            string s = Text(t)?.Trim();
            if (s == null || !s.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = s.Substring(label.Length).Trim();
            if (rest.Length > 0) return rest;
            if (t.Parent is Panel p)
            {
                int i = p.Children.IndexOf(t);
                for (int j = i + 1; j < p.Children.Count; j++)
                    if (p.Children[j] is TextBlock next && !string.IsNullOrWhiteSpace(Text(next))) return Text(next);
            }
            return null;
        }

        /// <summary>The placer's "150 PCU".</summary>
        private static bool IsPlacerPcu(string s) =>
            s != null && s.EndsWith("PCU", StringComparison.Ordinal) && s.Length <= 14 && s.Any(char.IsDigit) && !s.Contains(':');

        /// <summary>The build menu's "PCU: 150" (not "PCU Usage: 31%").</summary>
        private static bool IsMenuPcu(string s) =>
            s != null && s.StartsWith("PCU:", StringComparison.Ordinal) && s.Length <= 14 && s.Any(char.IsDigit);

        private static void Search(Visual top, ref TextBlock pcu, ref TextBlock menuPcu, ref TextBlock owner, ref TextBlock placing, ref TextBlock grid)
        {
            var stack = new Stack<(Visual V, int Depth)>();
            stack.Push((top, 0));
            int seen = 0;
            while (stack.Count > 0 && seen < 20000)
            {
                var (v, depth) = stack.Pop();
                seen++;
                if (!v.IsVisible) continue;
                if (ReferenceEquals(v, placerLine) || ReferenceEquals(v, gridLine) || ReferenceEquals(v, menuLine)) continue;
                if (v is TextBlock tb)
                {
                    string s = Norm(Text(tb));
                    if (!string.IsNullOrEmpty(s))
                    {
                        if (pcu == null && IsPlacerPcu(s)) pcu = tb;
                        else if (menuPcu == null && IsMenuPcu(s)) menuPcu = tb;
                        else if (owner == null && s.StartsWith("Owner:", StringComparison.OrdinalIgnoreCase)) owner = tb;
                        else if (placing == null && s.StartsWith("Placing:", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("Placing on", StringComparison.OrdinalIgnoreCase)) placing = tb;
                        else if (grid == null && s.StartsWith("Grid:", StringComparison.OrdinalIgnoreCase)) grid = tb;
                    }
                    continue;
                }
                if (depth >= 60) continue;
                foreach (var c in v.GetVisualChildren()) stack.Push((c, depth + 1));
            }
        }

        /// <summary>A block name ("Atmospheric Thruster 1 m") in the panel around the build menu's PCU text.</summary>
        private static string NameNear(TextBlock pcu)
        {
            var names = knownNames;
            if (names == null) return null;
            // Where the name was found last time first (the same panel, showing another block now).
            if (nameArea != null && nameArea.TryGetTarget(out var last) && last.GetVisualRoot() != null && last.GetVisualDescendants().Contains(pcu))
            {
                string again = NameIn(last, names);
                if (again != null) return again;
            }
            Visual area = pcu;
            for (int up = 0; up < 8 && area != null; up++)
            {
                area = area.GetVisualParent();
                if (area == null) break;
                string found = NameIn(area, names);
                if (found != null) { nameArea = new WeakReference<Visual>(area); return found; }
            }
            return null;
        }

        private static WeakReference<Visual> nameArea;

        private static string NameIn(Visual area, HashSet<string> names)
        {
            string plain = null;
            foreach (var tb in area.GetVisualDescendants().OfType<TextBlock>())
            {
                if (ReferenceEquals(tb, menuLine) || !tb.IsEffectivelyVisible) continue;
                string s = Norm(Text(tb));
                if (string.IsNullOrEmpty(s) || s.Length >= 60 || !names.Contains(s)) continue;
                if (s.EndsWith(" m", StringComparison.Ordinal)) return s;   // with its size: just the one block
                plain ??= s;
            }
            return plain;
        }

        private static void Hide(TextBlock line) { if (line != null && line.IsVisible) line.IsVisible = false; }

        private static TextBlock NewLine(TextBlock anchor, TextBlock line)
        {
            if (line == null)
            {
                // The game's own kind of text when it's its own control (that's where its shadow comes from), with
                // the same style classes; else plain text with a shadow drawn under it.
                Type kind = anchor.GetType();
                try { if (kind != typeof(TextBlock) && kind.GetConstructor(Type.EmptyTypes) != null) line = (TextBlock)Activator.CreateInstance(kind); }
                catch { line = null; }
                line ??= new TextBlock();
                line.IsHitTestVisible = false;
                try { foreach (string c in anchor.Classes) if (!c.StartsWith(":", StringComparison.Ordinal)) line.Classes.Add(c); } catch { }
                PluginLog.Info($"Game panel RPI: the game's text is a {kind.FullName} (classes: {string.Join(" ", anchor.Classes)}, effect: {anchor.Effect?.GetType().Name ?? "none"}); ours is a {line.GetType().Name}");
            }
            // Look like the game's own text next to it.
            line.FontSize = anchor.FontSize;
            line.FontFamily = anchor.FontFamily;
            line.FontWeight = anchor.FontWeight;
            line.Foreground = anchor.Foreground;
            line.FontStyle = anchor.FontStyle;
            line.FontStretch = anchor.FontStretch;
            if (anchor.Effect != null && line.Effect == null) line.Effect = anchor.Effect;
            return line;
        }

        /// <summary>Width of some text in the anchor's font.</summary>
        private static double TextWidth(TextBlock anchor, string text)
        {
            var probe = new TextBlock { Text = text, FontSize = anchor.FontSize, FontFamily = anchor.FontFamily, FontWeight = anchor.FontWeight };
            probe.Measure(Size.Infinity);
            return probe.DesiredSize.Width;
        }

        /// <summary>
        /// Our text to the left of the game's PCU (and its icon), at the same height: in the PCU's row when it's a
        /// StackPanel, else by position in the nearest panel with room, so the game's own layout isn't moved.
        /// </summary>
        private static TextBlock Beside(TextBlock line, TextBlock anchor, string text)
        {
            line = NewLine(anchor, line);
            if (string.IsNullOrEmpty(text)) { Hide(line); return line; }
            if (line.Text != text) line.Text = text;

            // Where the PCU's text starts, in the PCU TextBlock's own coordinates (it may be right-aligned in a wide box).
            double textW = TextWidth(anchor, Text(anchor) ?? "");
            double inner = anchor.Bounds.Width - anchor.Padding.Left - anchor.Padding.Right;
            double left = anchor.Padding.Left;
            if (anchor.TextAlignment == TextAlignment.Right || anchor.TextAlignment == TextAlignment.End) left += Math.Max(0, inner - textW);
            else if (anchor.TextAlignment == TextAlignment.Center) left += Math.Max(0, (inner - textW) / 2);
            double h = anchor.Bounds.Height;

            // Any icon just left of it (the PCU symbol), anywhere near it in the UI: an image, a path, or a text glyph.
            double textStart = left;
            Visual near = anchor.GetVisualParent();
            for (int up = 0; up < 4 && near?.GetVisualParent() != null; up++) near = near.GetVisualParent();
            var seen = logIcons ? new List<string>() : null;
            for (int pass = 0; pass < 3 && near != null; pass++)   // again from the new left edge: an icon may be in parts
            {
                double was = left;
                foreach (var v in near.GetVisualDescendants())
                {
                    if (ReferenceEquals(v, anchor) || ReferenceEquals(v, line) || !v.IsEffectivelyVisible) continue;
                    if (v is TextBlock t && (Text(t) ?? "").Any(char.IsLetterOrDigit)) continue;   // other words, not an icon
                    if (v.GetVisualDescendants().Contains(anchor)) continue;                      // a box holding the PCU text
                    var tl = v.TranslatePoint(new Point(0, 0), anchor);
                    if (tl == null) continue;
                    Rect r = new Rect(tl.Value, v.Bounds.Size);
                    if (r.Width <= 0 || r.Height <= 0 || r.Width > anchor.FontSize * 4 || r.Height > h * 3) continue;
                    bool sameRow = r.Bottom > h * 0.15 && r.Top < h * 0.85;
                    if (seen != null && pass == 0 && sameRow && r.Right <= left + 2 && r.Right >= left - anchor.FontSize * 6)
                        seen.Add($"{v.GetType().Name} {r.Left:0}..{r.Right:0}");
                    if (sameRow && r.Right <= left + 2 && r.Right >= left - anchor.FontSize * 3) left = Math.Min(left, r.Left);
                }
                if (left >= was) break;
            }
            // Not found (drawn in a way that can't be seen from here): both of the game's PCU texts have their icon
            // just left of them, about as wide as the text is tall, so leave room for it anyway.
            if (left >= textStart - 0.5) left = textStart - anchor.FontSize * 1.4;
            if (seen != null)
            {
                logIcons = false;
                PluginLog.Info($"Game panel RPI: things left of '{Text(anchor)}': " + (seen.Count > 0 ? string.Join(", ", seen) : "none") + $"; text placed left of {left:0}");
            }

            double gap = anchor.FontSize * 0.8;
            double w = TextWidth(anchor, text);
            double fontH = anchor.FontSize * 1.35;

            // A row (horizontal StackPanel) right round the PCU: just go in before it (and its icon).
            if (anchor.Parent is StackPanel row && row.Orientation == Orientation.Horizontal)
            {
                var kids = row.Children.Where(k => !ReferenceEquals(k, line)).ToList();
                int index = kids.IndexOf(anchor);
                if (index > 0 && !(kids[index - 1] is TextBlock)) index--;
                line.Margin = new Thickness(0, 0, gap, 0);
                line.VerticalAlignment = VerticalAlignment.Center;
                line.HorizontalAlignment = HorizontalAlignment.Left;
                Put(line, row, index);
                if (!line.IsVisible) line.IsVisible = true;
                return line;
            }

            // Otherwise placed by position, in the first panel up from it with room to the left (the PCU often sits in
            // a small box only as wide as itself), so the game's own layout isn't moved.
            Control holder = anchor;
            for (int up = 0; up < 8 && holder.Parent is Control parent; up++)
            {
                holder = parent;
                if (!(holder is Panel panel) || panel is StackPanel) continue;
                var at = anchor.TranslatePoint(new Point(left, (h - fontH) / 2), panel);
                if (at == null) continue;
                double x = at.Value.X - w - gap;
                if (x < 0) continue;   // (hanging out of a small box got it hidden)
                line.HorizontalAlignment = HorizontalAlignment.Left;
                line.VerticalAlignment = VerticalAlignment.Top;
                var m = new Thickness(Math.Round(x), Math.Round(at.Value.Y), 0, 0);
                if (line.Margin != m) line.Margin = m;
                if (panel is Grid g)
                {
                    Grid.SetColumn(line, 0);
                    Grid.SetRow(line, 0);
                    Grid.SetColumnSpan(line, Math.Max(1, g.ColumnDefinitions.Count));
                    Grid.SetRowSpan(line, Math.Max(1, g.RowDefinitions.Count));
                }
                if (!ReferenceEquals(line.Parent, panel)) Put(line, panel, panel.Children.Count);
                if (!loggedBeside.Contains(anchor.GetHashCode()))
                {
                    loggedBeside.Add(anchor.GetHashCode());
                    var effects = new List<string>();
                    for (Visual a = anchor; a != null; a = a.GetVisualParent())
                        if (a.Effect != null || a.OpacityMask != null) effects.Add($"{a.GetType().Name}: {a.Effect?.GetType().Name ?? "mask"}");
                    PluginLog.Info("Game panel RPI: effects above the PCU text: " + (effects.Count > 0 ? string.Join(", ", effects) : "none"));
                    PluginLog.Info($"Game panel RPI: '{text}' put in a {panel.GetType().Name} {up + 1} level(s) up, at x {x:0}, y {at.Value.Y:0} (text starts {left:0} into a {anchor.Bounds.Width:0} wide PCU box)");
                }
                if (!line.IsVisible) line.IsVisible = true;
                return line;
            }
            Hide(line);
            return line;
        }

        private static readonly HashSet<int> loggedBeside = new HashSet<int>();
        private static bool logIcons = true;

        /// <summary>Our line under the game's "Owner: Player" (after its row, when it's a label + value row).</summary>
        private static TextBlock Below(TextBlock line, TextBlock anchor)
        {
            line = NewLine(anchor, line);
            Control at = anchor;
            Panel panel = anchor.Parent as Panel;
            while (panel is StackPanel sp && sp.Orientation == Orientation.Horizontal && sp.Parent is Panel up) { at = sp; panel = up; }
            if (panel == null) return line;
            line.HorizontalAlignment = anchor.HorizontalAlignment;
            line.Margin = (at as Layoutable)?.Margin ?? new Thickness(0);
            var kids = panel.Children.Where(c => !ReferenceEquals(c, line)).ToList();
            int index = kids.IndexOf(at);
            if (index < 0) return line;
            if (panel is Grid)
            {
                Grid.SetColumn(line, Grid.GetColumn(at));
                Grid.SetRow(line, Grid.GetRow(at) + 1);
            }
            Put(line, panel, index + 1);
            return line;
        }


        // ---- Shadow: the HUD's text is the game's own ShadowedTextBlock (a TextBlock with a shadow copy under it, made
        // by its template). Ours shows through one of those too, made like the PCU's, sitting exactly where our line
        // was placed (the line itself stays, see-through, to do the placing). Without one: a black copy under ours.

        private static readonly Dictionary<TextBlock, Control> twins = new Dictionary<TextBlock, Control>();
        private static bool loggedChain, loggedTwin, loggedTwinColour;

        private static void SyncShadow(TextBlock line, TextBlock anchor)
        {
            if (line == null) return;
            twins.TryGetValue(line, out var twin);
            var panel = line.Parent as Panel;
            // Only for a line placed by position (not one in a row or list, where a copy would take up room).
            bool on = line.IsVisible && panel != null && !(panel is StackPanel) && !string.IsNullOrEmpty(line.Text) && anchor != null;
            if (!on)
            {
                if (twin != null && twin.IsVisible) twin.IsVisible = false;
                return;
            }
            var game = anchor.TemplatedParent as Avalonia.Controls.Primitives.TemplatedControl;
            if (game != null && game.GetType().Name != "ShadowedTextBlock") game = null;
            if (twin == null)
            {
                try
                {
                    if (game != null) twin = (Control)Activator.CreateInstance(game.GetType());
                }
                catch (Exception e) { PluginLog.Info("Game panel RPI: couldn't make the game's shadowed text: " + e.Message); }
                twin ??= new TextBlock { Foreground = Brushes.Black };
                twin.IsHitTestVisible = false;
                twins[line] = twin;
                if (!loggedTwin) { loggedTwin = true; PluginLog.Info($"Game panel RPI: shown through a {twin.GetType().Name}"); }
            }

            double d = 0;
            if (twin is TextBlock copy)
            {
                // The black copy, a little down and right.
                if (copy.Text != line.Text) copy.Text = line.Text;
                copy.FontSize = line.FontSize; copy.FontFamily = line.FontFamily; copy.FontWeight = line.FontWeight; copy.FontStyle = line.FontStyle;
                d = Math.Max(1, Math.Round(line.FontSize * 0.08));
                if (line.Opacity != 1) line.Opacity = 1;
            }
            else if (twin is Avalonia.Controls.Primitives.TemplatedControl tc && game != null)
            {
                var type = tc.GetType();
                var textProp = type.GetProperty("Text");
                if (textProp != null && !Equals(textProp.GetValue(tc), line.Text)) textProp.SetValue(tc, line.Text);
                // (From the text the game actually draws: the control's own Foreground isn't what colours it, and
                // copying that made ours black.)
                tc.FontSize = anchor.FontSize; tc.FontFamily = anchor.FontFamily; tc.FontWeight = anchor.FontWeight; tc.FontStyle = anchor.FontStyle;
                if (!ReferenceEquals(tc.Foreground, anchor.Foreground)) tc.Foreground = anchor.Foreground;
                // Everything the game's own kind of text adds (its shadow colour, offset, margin, spacing, any colour of its own).
                foreach (var pi in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (pi.Name == "Text" || pi.GetIndexParameters().Length > 0) continue;
                    if (!pi.CanWrite || !pi.CanRead) continue;
                    try
                    {
                        object v = pi.GetValue(game);
                        if (!Equals(pi.GetValue(tc), v)) pi.SetValue(tc, v);
                    }
                    catch { /* not one that can be copied */ }
                }
                if (!ReferenceEquals(tc.Foreground, anchor.Foreground)) tc.Foreground = anchor.Foreground;
                // The game's styles colour its text by the control's style classes: the same ones on ours.
                foreach (string cls in game.Classes)
                    if (!cls.StartsWith(":", StringComparison.Ordinal) && !tc.Classes.Contains(cls)) tc.Classes.Add(cls);
                // And the inner text pieces made by its template, matched one for one (colour, and whether shown).
                try
                {
                    var theirs = game.GetVisualDescendants().OfType<TextBlock>().ToList();
                    var ours = tc.GetVisualDescendants().OfType<TextBlock>().ToList();
                    for (int k = 0; k < theirs.Count && k < ours.Count; k++)
                    {
                        if (!ReferenceEquals(ours[k].Foreground, theirs[k].Foreground)) ours[k].Foreground = theirs[k].Foreground;
                        if (ours[k].Opacity != theirs[k].Opacity) ours[k].Opacity = theirs[k].Opacity;
                        if (ours[k].Margin != theirs[k].Margin) ours[k].Margin = theirs[k].Margin;
                    }
                    if (!loggedTwinColour && theirs.Count > 0)
                        PluginLog.Info("Game panel RPI: the PCU's layers: " + string.Join(", ", theirs.Select(t => $"{t.Foreground} {t.Opacity:0.##} {t.Margin}"))
                            + $" (classes: {string.Join(" ", game.Classes)}); ours has {ours.Count}");
                }
                catch { }
                if (!loggedTwinColour)
                {
                    loggedTwinColour = true;
                    PluginLog.Info($"Game panel RPI: the PCU's colour {anchor.Foreground}, its control's {game.Foreground}; ours set to {tc.Foreground}");
                }
                if (line.Opacity != 0) line.Opacity = 0;   // ours does the placing; the game's kind of text shows it
            }
            twin.HorizontalAlignment = line.HorizontalAlignment;
            twin.VerticalAlignment = line.VerticalAlignment;
            var m = new Thickness(line.Margin.Left + d, line.Margin.Top + d, line.Margin.Right - d, line.Margin.Bottom - d);
            if (twin.Margin != m) twin.Margin = m;
            Grid.SetColumn(twin, Grid.GetColumn(line)); Grid.SetRow(twin, Grid.GetRow(line));
            Grid.SetColumnSpan(twin, Grid.GetColumnSpan(line)); Grid.SetRowSpan(twin, Grid.GetRowSpan(line));
            // Just before the line, so a black copy is drawn under it.
            int li = panel.Children.IndexOf(line);
            if (!ReferenceEquals(twin.Parent, panel) || panel.Children.IndexOf(twin) != li - 1)
            {
                (twin.Parent as Panel)?.Children.Remove(twin);
                panel.Children.Insert(Math.Max(0, panel.Children.IndexOf(line)), twin);
            }
            if (!twin.IsVisible) twin.IsVisible = true;
            if (!loggedChain)
            {
                loggedChain = true;
                var chain = new List<string>();
                for (Visual a2 = anchor; a2 != null && chain.Count < 8; a2 = a2.GetVisualParent()) chain.Add(a2.GetType().Name);
                PluginLog.Info("Game panel RPI: the PCU text sits in " + string.Join(" < ", chain) + $" (made by {anchor.TemplatedParent?.GetType().Name ?? "nothing"})");
            }
        }

        /// <summary>Puts the line at this place in the panel (counted without it), leaving it alone if it's there already.</summary>
        private static void Put(TextBlock line, Panel panel, int index)
        {
            if (ReferenceEquals(line.Parent, panel))
            {
                if (panel.Children.IndexOf(line) == index) return;
                panel.Children.Remove(line);
            }
            else (line.Parent as Panel)?.Children.Remove(line);
            panel.Children.Insert(Math.Max(0, Math.Min(index, panel.Children.Count)), line);
        }

        private static void Show(TextBlock line, string text)
        {
            if (line == null) return;
            bool on = !string.IsNullOrEmpty(text);
            if (on && line.Text != text) line.Text = text;
            if (line.IsVisible != on) line.IsVisible = on;
        }
    }
}
