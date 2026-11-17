using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HarmonyLib;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// Adds a "Racing Display" button to the game's pause (Esc) menu, just above "Exit to Main Menu".
    /// Called from a Harmony postfix on GameMenu.UpdateButtons, which rebuilds the menu's buttons.
    /// The button uses the game's own "Menu" button style, so it looks like the others.
    /// </summary>
    internal static class PauseMenuButton
    {
        private const string ButtonName = "RaceDisplay_RacingDisplay";
        private static FieldInfo buttonsPanelField;

        public static void Add(Control menu)
        {
            // The same control is used for the main menu; only add the button in a world.
            if (menu?.DataContext == null || menu.DataContext.GetType().Name != "InGameMenuScreenViewModel") return;
            // The menu lives in the terminal screen (G > Racing Display) when that worked; then not in the pause menu.
            if (TerminalTab.Added) return;

            if (buttonsPanelField == null) buttonsPanelField = AccessTools.Field(menu.GetType(), "_buttonsPanel");
            if (buttonsPanelField == null)
            {
                PluginLog.Error("Pause menu: GameMenu._buttonsPanel not found, can't add the Racing Display button");
                return;
            }
            // Null the first time (the game calls this before the menu's template exists); it's called again once it does.
            if (!(buttonsPanelField.GetValue(menu) is Panel panel)) return;
            if (panel.Children.OfType<Button>().Any(b => b.Name == ButtonName)) { OpenRaceTabIfAsked(menu); return; }

            // Match the other buttons' text case (the game may give them in capitals).
            var texts = panel.Children.OfType<Button>().Select(b => b.Content as string).Where(t => !string.IsNullOrEmpty(t)).ToList();
            bool capitals = texts.Count > 0 && texts.All(t => t == t.ToUpperInvariant());

            // Like the game's own menu buttons: the "Menu" style plus a Command (the style greys out and
            // ignores buttons that only have a Click handler).
            var button = new Button
            {
                Name = ButtonName,
                Content = capitals ? "RACING DISPLAY" : "Racing Display",
                Command = MakeCommand(() =>
                {
                    PluginLog.Info("Racing Display button pressed");
                    RacingMenu.Instance.Open(menu);
                }),
                IsEnabled = true,
            };
            button.Classes.Add("Menu");

            // Above the gap before "Exit to Main Menu" / "Exit", i.e. straight after Settings and Help.
            int index = -1;
            for (int i = panel.Children.Count - 1; i >= 0; i--)
                if (panel.Children[i] is Separator) { index = i; break; }
            if (index < 0) index = panel.Children.Count;
            panel.Children.Insert(index, button);
            OpenRaceTabIfAsked(menu);
        }

        /// <summary>The race setup key opened the pause menu: go straight on to the Racing Display menu's Race tab.</summary>
        private static void OpenRaceTabIfAsked(Control menu)
        {
            if (!RacingMenu.Instance.OpenRaceTabNext) return;
            RacingMenu.Instance.OpenRaceTabNext = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PluginLog.Info("Race setup: opening the Racing Display menu on the Race tab");
                RacingMenu.Instance.Open(menu);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private static MethodInfo simpleCommandCreate;
        private static bool lookedForSimpleCommand;

        /// <summary>The game's own SimpleCommand.Create(Action) if it's there, otherwise our ActionCommand.</summary>
        private static ICommand MakeCommand(Action action)
        {
            var safe = new ActionCommand(action);
            if (!lookedForSimpleCommand)
            {
                lookedForSimpleCommand = true;
                var t = AccessTools.TypeByName("Keen.VRage.UI.Shared.Helpers.SimpleCommand");
                simpleCommandCreate = t == null ? null : AccessTools.Method(t, "Create", new[] { typeof(Action) });
                if (simpleCommandCreate == null) PluginLog.Info("SimpleCommand.Create not found; using our own command");
            }
            try
            {
                if (simpleCommandCreate?.Invoke(null, new object[] { (Action)(() => safe.Execute(null)) }) is ICommand game)
                    return game;
            }
            catch (Exception e) { PluginLog.Error("SimpleCommand.Create failed; using our own command", e); }
            return safe;
        }
    }

    /// <summary>Always-enabled command for a button.</summary>
    /// <summary>Text to and from the Windows clipboard (for sharing RPI classes).</summary>
    internal static class ClipboardText
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr handle);
        private const uint UnicodeText = 13, Moveable = 0x0002;

        public static bool Set(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || !OpenClipboard(IntPtr.Zero)) return false;
                try
                {
                    EmptyClipboard();
                    IntPtr handle = GlobalAlloc(Moveable, (UIntPtr)(uint)((text.Length + 1) * 2));
                    if (handle == IntPtr.Zero) return false;
                    IntPtr at = GlobalLock(handle);
                    if (at == IntPtr.Zero) return false;
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, at, text.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt16(at, text.Length * 2, 0);
                    GlobalUnlock(handle);
                    return SetClipboardData(UnicodeText, handle) != IntPtr.Zero;
                }
                finally { CloseClipboard(); }
            }
            catch (Exception e) { PluginLog.Info("Clipboard copy failed: " + e.Message); return false; }
        }

        public static string Get()
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return null;
                try
                {
                    IntPtr handle = GetClipboardData(UnicodeText);
                    if (handle == IntPtr.Zero) return null;
                    IntPtr at = GlobalLock(handle);
                    if (at == IntPtr.Zero) return null;
                    try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(at); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            catch (Exception e) { PluginLog.Info("Clipboard paste failed: " + e.Message); return null; }
        }
    }

    internal sealed class ActionCommand : ICommand
    {
        private readonly Action action;
        public ActionCommand(Action action) { this.action = action; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter)
        {
            try { action(); }
            catch (Exception e) { PluginLog.Error("Racing Display button failed", e); }
        }
    }

    /// <summary>
    /// The Racing Display menu, opened from the pause menu. It's built like the game's Settings screen
    /// (same background, title, bevelled panel, tabs, sliders, switches and menu buttons, using the game's
    /// own controls and style classes, with plain fallbacks if a game control can't be made) and has two tabs:
    ///  - Controls: click a shortcut, then press the new keys.
    ///  - Settings: penalties, countdown distance, markers and so on.
    /// It runs on the UI thread and only passes clicks on (a queue the controller reads each frame); the
    /// controller captures keys, saves, and sends back what to show. Closes with Back, or with the pause menu.
    /// </summary>
    internal sealed class RacingMenu
    {
        public static readonly RacingMenu Instance = new RacingMenu();

        /// <summary>The game's UI root panel, set each frame by Se2Bridge when it finds it.</summary>
        public volatile Panel RootPanel;

        private readonly ConcurrentQueue<MenuCommand> commands = new ConcurrentQueue<MenuCommand>();
        private volatile bool open;

        // UI-thread state
        private Grid overlay;
        private Control contentPanel;            // the bevelled box with the page and the description column
        private Grid terminalHost;               // the menu inside the terminal screen (G > Racing Display)
        private bool terminalMode;
        private volatile string wantedTerminalPage = "Race";   // set from the terminal's selection (any thread)
        private string shownTerminalPage;
        private DispatcherTimer terminalPoll;
        private Panel host;
        private Control pauseMenu;
        private TabControl tabs;
        private ContentControl page;
        private ScrollViewer controlsPage, settingsPage, recordsPage, infoPage, coursesPage;
        private StackPanel courseRows;
        private TextBox courseNameBox, pilotNameBox, sectorNameBox, presetNameBox;
        private StackPanel presetRows;
        private string pickedPreset;
        private int sectorFrom, sectorTo = 1;
        private int builtCoursesVersion = -1;
        private StackPanel controlRows, settingRows, recordRows;
        private Grid controlsHolder, settingsHolder;   // the Controls / Settings pages: a search box that stays put, the list scrolling under it
        private Border settingsLinkHost;               // "Collapse all", beside the settings search box
        private TextBox controlsSearch;
        private HudModel.ControlsPanel lastControlsPanel;
        private Button resetButton;
        private int builtRecordsVersion = -1, builtRpiVersion = -1;
        private StackPanel rpiRows;
        private ScrollViewer rpiPage;
        private TabItem rpiTab, cineTab, historyTab;
        private StackPanel historyRows;
        private ScrollViewer historyPage;
        private int builtHistoryVersion = -1;
        private bool historyClearArmed;
        private bool OnHistoryTab => tabs != null && tabs.SelectedItem == historyTab;
        private StackPanel cineRows;
        private ScrollViewer cinePage;
        private TextBox cineNameBox;
        private int builtCineVersion = -1;
        private bool cineDeleteArmed;
        private bool OnCineTab => tabs != null && tabs.SelectedItem == cineTab;
        private Control designerView;
        private Border pageDivider;
        private ScrollViewer descScroll;
        private HudModel.ControlsPanel lastRpiPanel;
        private bool OnRpiTab => tabs != null && tabs.SelectedItem == rpiTab;
        private TextBlock note, description;
        private WrapPanel statusStrip;   // across the top of the menu: the course, the ship, and what's running (with Stop)
        private string statusKey;        // what the strip was last built from (rebuilt only when that changes)
        private double scale = 1;
        private string lastControlsSignature;
        private int builtSettingsVersion = -1;
        private string defaultDescription = "";

        // game thread -> UI thread
        private HudModel.ControlsPanel pending;
        private int postQueued;

        public bool IsOpen => open;

        /// <summary>Set by the race setup key: the next pause menu opens this menu (on the Race tab, its first).</summary>
        public volatile bool OpenRaceTabNext;

        public bool TryTake(out MenuCommand command) => commands.TryDequeue(out command);
        /// <summary>For the Race Map's own buttons.</summary>
        internal void Send(MenuCommand command) => commands.Enqueue(command);

        private TabItem raceTab, infoTab, settingsTab, controlsTab, recordsTab, coursesTab;
        private StackPanel raceRows;
        private ScrollViewer racePage;
        private bool OnRaceTab => tabs != null && tabs.SelectedItem == raceTab;
        private bool OnInfoTab => tabs != null && infoTab != null && tabs.SelectedItem == infoTab;
        private bool OnSettingsTab => tabs != null && tabs.SelectedItem == settingsTab;
        private bool OnRecordsTab => tabs != null && tabs.SelectedItem == recordsTab;
        private bool OnCoursesTab => tabs != null && tabs.SelectedItem == coursesTab;

        // ---------------------------------------------------------------- open / close (UI thread)

        public void Open(Control fromMenu)
        {
            try
            {
                if (open) return;
                var root = RootPanel ?? fromMenu.GetVisualAncestors().OfType<Panel>().LastOrDefault();
                if (root == null) { PluginLog.Error("Racing Display menu: no UI root panel to show it in"); return; }

                double h = root.Bounds.Height;
                scale = h > 0 ? Math.Max(0.75, Math.Min(2.0, h / 1080.0)) : 1.0;
                Build(scale);

                host = root;
                host.Children.Add(overlay); // last child = drawn on top of the pause menu
                pauseMenu = fromMenu;
                pauseMenu.DetachedFromVisualTree += OnPauseMenuGone;
                pauseMenu.IsVisible = false; // like the game's own Settings screen, which replaces the menu
                lastControlsSignature = null;
                builtSettingsVersion = -1;
                builtRecordsVersion = -1;
                builtRpiVersion = -1;
                builtCineVersion = -1;
                builtHistoryVersion = -1;
                builtCoursesVersion = -1;
                open = true;
                commands.Enqueue(new MenuCommand(MenuAction.Opened));

                // Take keyboard focus off the pause menu, so keys pressed while choosing shortcuts don't press its buttons.
                overlay.Focus();
                PluginLog.Info("Racing Display menu opened");
            }
            catch (Exception e)
            {
                PluginLog.Error("Racing Display menu failed to open", e);
                CloseNow();
            }
        }

        private void OnPauseMenuGone(object sender, VisualTreeAttachmentEventArgs e) => CloseNow();

        /// <summary>From the game thread.</summary>
        public void RequestClose() => Dispatcher.UIThread.Post(() => { if (!terminalMode) CloseNow(); });

        // ---------------------------------------------------------------- in the terminal screen (G)

        /// <summary>The menu's content for the terminal screen's Racing Display tab (one control for all its sub-tabs).</summary>
        public Control CreateTerminalHost()
        {
            try
            {
                if (open && !terminalMode) CloseNow();
                double h = RootPanel?.Bounds.Height ?? 0;
                scale = h > 0 ? Math.Max(0.75, Math.Min(2.0, h / 1080.0)) : 1.0;
                double s = scale;
                Build(s);
                terminalMode = true;

                // Take the box and the Reset button out of the full-screen layout and put them in our own.
                if (contentPanel.Parent is Panel p1) p1.Children.Remove(contentPanel);
                if (resetButton.Parent is Panel p2) p2.Children.Remove(resetButton);
                var host = new Grid { Margin = new Thickness(0, 10 * s, 0, 10 * s), DataContext = null };
                host.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
                host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(contentPanel, 0); host.Children.Add(contentPanel);
                resetButton.Margin = new Thickness(0, 12 * s, 0, 0);
                Grid.SetRow(resetButton, 1); host.Children.Add(resetButton);

                // Shown or not: checked a few times a second while it's in the terminal (the terminal shows and
                // hides sub-screen content in ways that don't always raise an event we see, e.g. when opened with
                // the race setup key straight onto this tab).
                terminalPoll?.Stop();
                terminalPoll = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, __) => PollTerminal());
                host.AttachedToVisualTree += (_, __) => { terminalPoll.Start(); Dispatcher.UIThread.Post(PollTerminal, DispatcherPriority.Background); };
                host.DetachedFromVisualTree += (_, __) => { terminalPoll.Stop(); SetTerminalOpen(false); };
                host.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Dispatcher.UIThread.Post(PollTerminal, DispatcherPriority.Background); };
                terminalHost = host;
                shownTerminalPage = null;
                SelectPage(wantedTerminalPage);
                PluginLog.Info("Racing Display menu made for the terminal screen");
                return host;
            }
            catch (Exception e)
            {
                PluginLog.Error("Racing Display menu: couldn't make it for the terminal screen", e);
                return new TextBlock { Text = "Racing Display couldn't be shown here (see RaceDisplay.log)." };
            }
        }

        /// <summary>One of our sub-tabs was picked in the terminal: Race, Records, Courses, Settings or Controls.</summary>
        public void ShowTerminalPage(string name)
        {
            wantedTerminalPage = name;
            Dispatcher.UIThread.Post(PollTerminal, DispatcherPriority.Background);
        }

        private void PollTerminal()
        {
            var host = terminalHost;
            if (host == null || !terminalMode) return;
            bool shown = host.GetVisualRoot() != null && host.IsEffectivelyVisible;
            if (shown && wantedTerminalPage != shownTerminalPage) SelectPage(wantedTerminalPage);
            SetTerminalOpen(shown);
            if (shown) HideTerminalCineTab(host);
            if (shown && open) Apply();   // fill the page if its contents arrived before it was shown
        }

        // The terminal's own Cinematics tab header (the game makes it from our sub-screen): hidden while
        // Settings > Menu > Filming is off, straight away rather than the next time the terminal opens.
        private WeakReference<Control> terminalCineHeader;
        private long terminalCineLookedAt;

        private void HideTerminalCineTab(Control host)
        {
            try
            {
                bool want = GateSettings.FilmingOn;
                Control header = null;
                if (terminalCineHeader != null && (!terminalCineHeader.TryGetTarget(out header) || header.GetVisualRoot() == null)) { header = null; terminalCineHeader = null; }
                if (header == null)
                {
                    if (want) return;   // (nothing was hidden)
                    long nowMs = Environment.TickCount64;
                    if (nowMs - terminalCineLookedAt < 150) return;
                    terminalCineLookedAt = nowMs;
                    if (!(host.GetVisualRoot() is Visual root)) return;
                    foreach (var v in root.GetVisualDescendants())
                    {
                        if (!(v is Control c) || !TerminalTab.IsCinematicsPage(c.DataContext)) continue;
                        // (The outermost control made for that page: its parent belongs to the tab strip, not the page.)
                        if (c.GetVisualParent() is Control up && TerminalTab.IsCinematicsPage(up.DataContext)) continue;
                        header = c;
                        break;
                    }
                    if (header == null) return;
                    terminalCineHeader = new WeakReference<Control>(header);
                    PluginLog.Info("Racing Display menu: the terminal's Cinematics tab found (" + header.GetType().Name + "), hidden while Filming is off");
                }
                if (header.IsVisible != want) header.IsVisible = want;
                if (!want && OnCineTab) TerminalTab.OpenPage("Race");
            }
            catch (Exception e)
            {
                if (tabErrorsLogged.Add("cine header|" + e.Message)) PluginLog.Error("Racing Display menu: couldn't hide the terminal's Cinematics tab", e);
            }
        }

        private void SelectPage(string name)
        {
            if (tabs == null) return;
            shownTerminalPage = name;
            var tab = name == "Records" ? recordsTab : name == "RPI" ? rpiTab : name == "Cinematics" ? cineTab : name == "History" ? historyTab : name == "Courses" ? coursesTab : name == "Settings" ? settingsTab : name == "Controls" ? controlsTab : raceTab;
            if (tabs.SelectedItem != tab) tabs.SelectedItem = tab; else ShowPage();
        }

        private void SetTerminalOpen(bool on)
        {
            if (on == open) return;
            open = on;
            if (on)
            {
                lastControlsSignature = null;
                builtSettingsVersion = -1;
                builtRecordsVersion = -1;
                builtRpiVersion = -1;
                builtCineVersion = -1;
                builtHistoryVersion = -1;
                builtCoursesVersion = -1;
                commands.Enqueue(new MenuCommand(MenuAction.Opened));
                PluginLog.Info("Racing Display menu shown in the terminal");
            }
        }

        private void CloseNow()
        {
            try
            {
                if (pauseMenu != null)
                {
                    pauseMenu.DetachedFromVisualTree -= OnPauseMenuGone;
                    pauseMenu.IsVisible = true;
                }
                if (host != null && overlay != null) host.Children.Remove(overlay);
                if (open && pauseMenu != null && pauseMenu.GetVisualRoot() != null)
                {
                    // Give focus back to the pause menu's first button.
                    var first = pauseMenu.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible);
                    first?.Focus();
                }
            }
            catch (Exception e) { PluginLog.Error("Racing Display menu failed to close cleanly", e); }
            pauseMenu = null;
            host = null;
            open = false;
        }

        // ---------------------------------------------------------------- contents (game thread -> UI thread)

        public void Show(HudModel.ControlsPanel panel)
        {
            if (!open || panel == null) return;
            Volatile.Write(ref pending, panel);
            if (Interlocked.Exchange(ref postQueued, 1) == 0)
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
        }

        private void Apply()
        {
            Interlocked.Exchange(ref postQueued, 0);
            if (!open || overlay == null) return;
            var p = Volatile.Read(ref pending);
            if (p == null) return;
            try
            {
                string sig = ControlsSignature(p);
                if (sig != lastControlsSignature)
                {
                    lastControlsSignature = sig;
                    Safe("Controls", () => FillControls(p));
                }
                bool raceChanged = false;
                if (p.SettingsVersion != builtSettingsVersion)
                {
                    builtSettingsVersion = p.SettingsVersion;
                    Safe("Settings", () => FillSettings(p));
                    raceChanged = true;
                }
                if (p.RecordsVersion != builtRecordsVersion)
                {
                    builtRecordsVersion = p.RecordsVersion;
                    Safe("Records", () => FillRecords(p));
                    raceChanged = true;
                }
                if (raceChanged) Safe("Race", () => FillRace(p));
                if (p.RpiVersion != builtRpiVersion)
                {
                    builtRpiVersion = p.RpiVersion;
                    Safe("Ship analyser", () => FillRpi(p));
                }
                if (p.CoursesVersion != builtCoursesVersion)
                {
                    builtCoursesVersion = p.CoursesVersion;
                    Safe("Courses", () => FillCourses(p));
                }
                if (p.HistoryVersion != builtHistoryVersion)
                {
                    builtHistoryVersion = p.HistoryVersion;
                    Safe("History", () => FillHistory(p));
                }
                if (p.CineVersion != builtCineVersion)
                {
                    builtCineVersion = p.CineVersion;
                    Safe("Cinematics", () => FillCine(p));
                }
                if (builtFilming != GateSettings.FilmingOn)
                {
                    // Settings > Menu > Filming changed: the Cinematics tab, the Race spotlight section and their settings come or go.
                    builtFilming = GateSettings.FilmingOn;
                    if (cineTab != null) cineTab.IsVisible = builtFilming;
                    if (!builtFilming && OnCineTab && tabs != null) tabs.SelectedItem = raceTab;
                    Safe("Race", () => FillRace(p));
                    Safe("Settings", () => FillSettings(p));
                }
                MoveCinePlayhead(p);
                UpdateStatusStrip(p);
            }
            catch (Exception e)
            {
                PluginLog.Error("Racing Display menu update failed", e);
                CloseNow();
            }
        }

        // One tab failing to build only breaks that tab (logged once per tab), not the whole menu.
        private readonly HashSet<string> tabErrorsLogged = new HashSet<string>();
        private void Safe(string tab, Action fill)
        {
            try { fill(); }
            catch (Exception e)
            {
                if (tabErrorsLogged.Add(tab + "|" + e.Message)) PluginLog.Error($"Racing Display menu: the {tab} tab failed to build", e);
            }
        }

        private static string ControlsSignature(HudModel.ControlsPanel p)
        {
            var sb = new StringBuilder();
            sb.Append(p.Note).Append('|').Append(p.Help).Append('|');
            foreach (var r in p.Rows)
                sb.Append(r.Name).Append(r.Binding).Append(r.Capturing).Append(r.Clash).Append(r.Changed).Append('|');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- game controls (by reflection, so a game update can't break the build)

        private static Control NewGameControl(string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                return t == null ? null : Activator.CreateInstance(t) as Control;
            }
            catch (Exception e)
            {
                PluginLog.Error($"Racing Display menu: couldn't create {typeName}", e);
                return null;
            }
        }

        private static bool TrySet(object target, string property, object value)
        {
            try
            {
                var p = AccessTools.Property(target.GetType(), property);
                if (p == null || !p.CanWrite) return false;
                var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                p.SetValue(target, value == null || type.IsInstanceOfType(value) ? value : Convert.ChangeType(value, type));
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- look

        private static IBrush Argb(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        private static IBrush Brush(HudColor c) => Argb(c.A, c.R, c.G, c.B);

        private static readonly IBrush White = Brushes.White;
        private static readonly IBrush Grey = Argb(230, 185, 190, 195);
        private static readonly IBrush SectionLine = Argb(90, 200, 210, 220);
        private static readonly IBrush CaptureBack = Argb(110, 255, 190, 40);

        private static TextBlock Text(string text, IBrush brush = null, params string[] classes)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            if (brush != null) t.Foreground = brush;
            foreach (var c in classes) t.Classes.Add(c);
            return t;
        }

        /// <summary>Font size that the game's own style classes can still override.</summary>
        /// <summary>
        /// A tooltip in small wrapped text. (A plain string as a tooltip takes the game's own tooltip style, which is
        /// huge and covered the rows it was about.)
        /// </summary>
        private static void SmallTip(Control target, object tip)
        {
            if (tip is string words)
                tip = string.IsNullOrEmpty(words) ? null : new TextBlock { Text = TidyPaths(words), FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            ToolTip.SetTip(target, tip);
        }

        /// <summary>Folder paths shown in the menu, with the Windows user folder written as %AppData% (shorter, and no user name on screen).</summary>
        private static string TidyPaths(string words)
        {
            if (string.IsNullOrEmpty(words) || words.IndexOf(":\\", StringComparison.Ordinal) < 0) return words;
            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(roaming)) words = words.Replace(roaming, "%AppData%", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(profile)) words = words.Replace(profile, "%UserProfile%", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return words;
        }

        private static void SoftFontSize(TextBlock t, double size)
        {
            if (t.Classes.Count == 0) { t.FontSize = size; return; } // no game style to defer to
            try { t.SetValue(TextBlock.FontSizeProperty, size, BindingPriority.Style); }
            catch { /* leave it to the game's style */ }
        }

        private Button MenuButton(string text, Action click)
        {
            var b = new Button
            {
                Content = text,
                Command = new ActionCommand(click),
                Focusable = false, // keys are for choosing shortcuts, not pressing these
                MinWidth = 220 * scale,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            b.Classes.Add("Menu");
            return b;
        }

        /// <summary>A plain button in the game's default button look (like the key buttons in its Controls settings).</summary>
        private Button ValueButton(string text, IBrush textBrush, double width, Action click)
        {
            var label = Text(text, textBrush);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.FontWeight = FontWeight.Bold;
            SoftFontSize(label, 16 * scale);
            var b = new Button
            {
                Content = label,
                Command = new ActionCommand(click),
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 38 * scale,
            };
            if (width > 0) b.Width = width * scale;
            return b;
        }

        private const double SlimScale = 0.8;

        /// <summary>
        /// One of the game's buttons, a size smaller, for the compact rows. The game's button doesn't take a shorter
        /// height (forced, its frame is cut off top and bottom), so it's left its own size and the whole button is
        /// scaled down: frame, corners and words together, coming out as tall as the text boxes and sliders.
        /// </summary>
        private Control SlimButton(string words, IBrush brush, Action click)
        {
            var inner = ValueButton(words, brush, 0, click);
            inner.MinWidth = 88 * scale / SlimScale;
            inner.HorizontalAlignment = HorizontalAlignment.Stretch;
            return new LayoutTransformControl
            {
                Child = inner, LayoutTransform = new ScaleTransform(SlimScale, SlimScale),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>
        /// One setting, laid out like the game's Settings rows: name right-aligned, a gap, then the value
        /// control. Pointing at it shows its description in the panel on the right.
        /// </summary>
        private Grid OptionRow(string name, string about, Control value)
        {
            double s = scale;
            // (Right margin: clear of the scroll bar, lined up with the section lines.)
            var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 4 * s, 24 * s, 4 * s), MinHeight = 50 * s };
            // Name on the left, then the control; the description panel sits to the right of the list.
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(470 * s, GridUnitType.Pixel)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(24 * s, GridUnitType.Pixel)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var label = Text(name, null, "OptionSubTitle");
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextWrapping = TextWrapping.Wrap;
            label.Margin = new Thickness(16 * s, 0, 0, 0);
            SoftFontSize(label, 20 * s);
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            if (value != null)
            {
                value.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
            }

            row.PointerEntered += (_, __) => description.Text = about;
            return row;
        }


        // ---- "?" beside section titles: what the section is for, shown in the description column.
        private static readonly Dictionary<string, string> SectionHelp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Laps"] = "Your laps in this session: the time of each, its splits and any penalties. Pick a lap to see it on the race map or race its ghost.",
            ["Ship analyser"] = "Look at a ship to see what it's made of: mass, thrust, top speed estimates and (with an RPI class in use) how much of the budget it uses.",
            ["Autopilot"] = "Flies your ship round the course along a recorded racing line, so you can film it or test a course. Needs a course with a racing line.",
            ["Race spotlight"] = "A showcase of one race for people watching: the ship flown round the course while panels show the results, stats, graphs and replays. Pick which pages show, in what order and for how long. Auto race flies the race again with the autopilot; the drop-down picks a cinematic to film it with.",
            ["Ghost and racing line"] = "Race against a ghost of your best lap (or a ghost file), and draw the line it took.",
            ["Session history"] = "Every race finished in this game session, newest first. Clear history keeps the old file beside the new one.",
            ["Presets"] = "Saved sets of settings. Save the settings you have now as a preset, then load it in one click later.",
            ["RPI designer"] = "Race Performance Index: make classes with a budget of points that blocks cost, so ships in a class are evenly matched.",
            ["Class in use"] = "The RPI class the ship analyser checks ships against.",
            ["Orbit camera"] = "A spectator camera that circles a ship. Mouse swings round it, W / S zoom, left click picks another ship, Esc goes back.",
            ["Cinematics"] = "Camera timelines: shots (orbit, chase, fixed, moving, path) played one after another, with effects laid over them. Each cinematic is a file you can share.",
            ["Auto switch at gates"] = "Start a different cinematic each time the filmed ship flies through a gate of this course. Set a cinematic per gate; they're kept per course.",
            ["Timeline"] = "The picked cinematic's shots, left to right, as wide as they are long, with the effect layers under them. Drag shot types and effects on; click one to edit it in the inspector below. Ctrl + Z undoes.",
            ["This course"] = "The course loaded in this world: its name, laps and whether it has a finish separate from the start.",
            ["Sectors"] = "Split the lap into sectors between checkpoints, to compare your times part by part.",
            ["Course edit mode"] = "Place and move the start gate and checkpoints in the world.",
            ["World save"] = "Keep the course with the world save, so it's there the next time the world loads (and for anyone else who loads it).",
            ["Build"] = "Make a course quickly: from named grids or antennas in the world (Auto-build), or by flying it.",
            ["Checkpoints"] = "The course's checkpoints in order. Delete one, or go to it.",
            ["Saved courses in this world"] = "Courses saved in this world. Load one to race it.",
            ["Ships on this course"] = "Best times by ship on this course. Delete forgets a ship's times.",
            ["Ghost files"] = "Saved ghost laps for this course. Race one, share the file, or delete it.",
            ["Reset"] = "Clear best times and race history, for this course or every course. Can't be undone.",
            ["HUD"] = "What the race HUD shows while you race and where.",
            ["Panels"] = "The extra boxes on screen: splits, race stats, graphs and more.",
            ["Markers"] = "The markers drawn on the gates and checkpoints in the world.",
            ["Minimap"] = "The small course map in the corner.",
            ["Penalties"] = "Time added for missed checkpoints and crashes, and how hard a hit counts as a crash.",
            ["Race"] = "How races start and finish: laps, countdown, false starts.",
            ["Replay"] = "Watching laps back from the spectator camera.",
            ["Sound"] = "Sounds for checkpoints, penalties and the countdown.",
            ["Course editing"] = "How gates and checkpoints are placed and drawn while you edit a course.",
            ["Advanced"] = "Rarely needed settings and fixes for problems.",
        };

        /// <summary>Where each menu section is explained in the guide: its page and the section on that page.</summary>
        private static readonly Dictionary<string, (string Page, string Section)> SectionGuide = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Laps"] = ("Results and records", "After the race"),
            ["Ship analyser"] = ("Ship analyser", "Ship analyser"),
            ["Autopilot"] = ("Autopilot", "Autopilot"),
            ["Race spotlight"] = ("Showcase and video", "Race spotlight"),
            ["Ghost and racing line"] = ("Ghosts and replays", "Ghosts"),
            ["Session history"] = ("Results and records", "History"),
            ["Presets"] = ("Menu and settings", "Racing Display menu"),
            ["RPI designer"] = ("Ship analyser", "Ship analyser"),
            ["Class in use"] = ("Ship analyser", "Ship analyser"),
            ["Orbit camera"] = ("Cameras and cinematics", "Orbit camera"),
            ["Cinematics"] = ("Cameras and cinematics", "Cinematics"),
            ["Auto switch at gates"] = ("Cameras and cinematics", "Cinematics"),
            ["Timeline"] = ("Cameras and cinematics", "Cinematics"),
            ["This course"] = ("Building courses", "Saving and sharing"),
            ["Sectors"] = ("Minimap and Race Map", "Maps"),
            ["Course edit mode"] = ("Building courses", "Course edit mode"),
            ["World save"] = ("Building courses", "Saving and sharing"),
            ["Build"] = ("Building courses", "Auto-build from names"),
            ["Checkpoints"] = ("Building courses", "Course edit mode"),
            ["Saved courses in this world"] = ("Building courses", "Saving and sharing"),
            ["Ships on this course"] = ("Results and records", "Records"),
            ["Ghost files"] = ("Ghosts and replays", "Ghosts"),
            ["Reset"] = ("Results and records", "Records"),
            ["HUD"] = ("HUD and markers", "Messages and HUD modes"),
            ["Panels"] = ("HUD and markers", "Panels"),
            ["Markers"] = ("HUD and markers", "Markers"),
            ["Minimap"] = ("Minimap and Race Map", "Maps"),
            ["Penalties"] = ("Penalties", "Penalties"),
            ["Race"] = ("Getting started", "Getting started"),
            ["Replay"] = ("Ghosts and replays", "Replays"),
            ["Sound"] = ("Penalties", "Penalties"),
            ["Course editing"] = ("Building courses", "Course edit mode"),
            ["Advanced"] = ("Menu and settings", "Racing Display menu"),
            ["Ghost"] = ("Ghosts and replays", "Ghosts"),
        };

        /// <summary>
        /// The guide over the menu, opened at a page and scrolled to a section (which is lit up): what a "?" opens.
        /// The same guide is in the game's Help screen (F1) under Racing Display.
        /// </summary>
        private void ShowGuideAt(string pageName, string sectionName)
        {
            double s = scale;
            CloseConfirm();
            StackPanel guide;
            try { guide = BuildGuide(s, pageName); }
            catch (Exception e) { PluginLog.Error("Couldn't build the guide page", e); return; }

            var title = Text(pageName, White, "Header");
            SoftFontSize(title, 30 * s);
            var where = Text("Racing Display guide (also in the game's Help screen, F1)", Grey);
            SoftFontSize(where, 15 * s);
            var close = MenuButton("Close", CloseConfirm);
            close.VerticalAlignment = VerticalAlignment.Center;
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, where } };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24 * s, 18 * s, 24 * s, 8 * s) };
            Grid.SetColumn(close, 1);
            head.Children.Add(titles);
            head.Children.Add(close);

            var scroller = new ScrollViewer { Content = guide, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(scroller, 1);
            body.Children.Add(head);
            body.Children.Add(scroller);
            var box = new Border
            {
                Background = Argb(255, 10, 22, 32), BorderBrush = Argb(255, 80, 110, 135), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6 * s), Child = body,
                Width = 1180 * s, Height = 780 * s, MaxHeight = 900 * s,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            confirmLayer = new Grid { Background = Argb(170, 0, 0, 0), Children = { box } };
            confirmLayer.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, confirmLayer)) { e.Handled = true; CloseConfirm(); } };   // a click outside closes it
            confirmLayer.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; CloseConfirm(); } };
            Panel holder = overlay != null && Avalonia.VisualTree.VisualExtensions.GetVisualRoot(overlay) != null ? overlay : (Panel)terminalHost ?? overlay;
            if (holder == null) { confirmLayer = null; return; }
            if (holder is Grid g && g.RowDefinitions.Count > 1) Grid.SetRowSpan(confirmLayer, g.RowDefinitions.Count);
            holder.Children.Add(confirmLayer);

            // Scroll to the section's heading and light it up.
            string wanted = (sectionName ?? "").ToUpperInvariant();
            var target = guide.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text == wanted);
            if (target != null)
            {
                target.Foreground = Brush(HudColor.Yellow);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var at = target.TranslatePoint(new Point(0, 0), guide);
                        if (at.HasValue) scroller.Offset = new Vector(0, Math.Max(0, at.Value.Y - 8 * s));
                    }
                    catch { /* the menu closed */ }
                }, DispatcherPriority.Background);
            }
        }

        /// <summary>A small round "?": pointing at it or clicking it puts the help in the description column.</summary>
        private Control HelpDot(string help, string section = null)
        {
            double s = scale;
            var q = Text("?", White);
            q.FontWeight = FontWeight.Bold;
            SoftFontSize(q, 15 * s);
            q.HorizontalAlignment = HorizontalAlignment.Center;
            q.VerticalAlignment = VerticalAlignment.Center;
            var dot = new Border
            {
                Width = 24 * s, Height = 24 * s, CornerRadius = new CornerRadius(12 * s), Child = q,
                Background = Argb(255, 30, 60, 84), BorderBrush = Argb(255, 80, 110, 135), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10 * s, 0, 0, 8 * s),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Help),
            };
            bool inGuide = section != null && SectionGuide.ContainsKey(section);
            void Show() { if (description != null) description.Text = help + (inGuide ? "\n\nClick the ? to read more about it in the guide." : ""); }
            dot.PointerEntered += (_, __) => { dot.Background = Argb(255, 50, 95, 130); Show(); };
            dot.PointerExited += (_, __) => dot.Background = Argb(255, 30, 60, 84);
            dot.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (section != null && SectionGuide.TryGetValue(section, out var place)) ShowGuideAt(place.Page, place.Section);
                else Show();
            };
            return dot;
        }

        /// <summary>A section title, with a "?" beside it when there's help for it.</summary>
        private Control Section(string text, string help = null, string guideKey = null)
        {
            var head = SectionHeader(text);
            if (help == null && (text == null || !SectionHelp.TryGetValue(text, out help))) return head;
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { head, HelpDot(help, guideKey ?? text) } };
        }

        private TextBlock SectionHeader(string text) => SectionHeader(text, scale);

        private static TextBlock SectionHeader(string text, double s)
        {
            // Bigger and brighter than the rows under it, so the groups stand out.
            var t = Text(text.ToUpperInvariant(), White);
            t.FontWeight = FontWeight.Bold;
            t.FontSize = 26 * s;
            t.Margin = new Thickness(16 * s, 26 * s, 0, 6 * s);
            return t;
        }

        // ---------------------------------------------------------------- build (each time the menu opens)

        private void Build(double s)
        {
            confirmLayer = null;
            // Title, top left like the Settings screen.
            var title = Text("Racing Display", White, "Header");
            SoftFontSize(title, 40 * s);
            title.HorizontalAlignment = HorizontalAlignment.Left;
            title.Margin = new Thickness(0, 0, 0, 20 * s);

            // Tabs (headers only; the page below swaps), styled like the Settings screen's tabs.
            tabs = new TabControl { Focusable = false, HorizontalAlignment = HorizontalAlignment.Left };
            tabs.Classes.Add("TabOnly");
            tabs.Classes.Add("SettingsTabs");
            // The guide lives in the game's Help screen (F1 > Racing Display). The Info tab is only
            // shown if adding it there failed.
            infoTab = HelpScreenPage.Added ? null : new TabItem { Header = "Info", Focusable = false };
            raceTab = new TabItem { Header = "Race", Focusable = false };
            settingsTab = new TabItem { Header = "Settings", Focusable = false };
            controlsTab = new TabItem { Header = "Controls", Focusable = false };
            recordsTab = new TabItem { Header = "Records", Focusable = false };
            coursesTab = new TabItem { Header = "Courses", Focusable = false };
            rpiTab = new TabItem { Header = "RPI", Focusable = false };
            cineTab = new TabItem { Header = "Cinematics", Focusable = false };
            historyTab = new TabItem { Header = "History", Focusable = false };
            tabs.Items.Add(raceTab);
            tabs.Items.Add(recordsTab);
            tabs.Items.Add(historyTab);
            tabs.Items.Add(coursesTab);
            tabs.Items.Add(rpiTab);
            tabs.Items.Add(cineTab);
            tabs.Items.Add(settingsTab);
            tabs.Items.Add(controlsTab);
            if (infoTab != null) tabs.Items.Add(infoTab);
            tabs.SelectedIndex = 0;
            tabs.SelectionChanged += (_, __) => ShowPage();

            controlRows = new StackPanel();
            settingRows = new StackPanel();
            recordRows = new StackPanel();
            raceRows = new StackPanel();
            rpiRows = new StackPanel();
            cineRows = new StackPanel();
            historyRows = new StackPanel();
            historyPage = new ScrollViewer { Content = historyRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            cinePage = new ScrollViewer { Content = cineRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            cineNameBox = new TextBox { Watermark = "Cinematic name", Width = 260 * s, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8 * s, 0) };
            // (The designer made for the last menu still belongs to that menu: it can't be shown in this one.)
            // It's made again (if it was open) when this menu gets its first contents.
            designerView = null;
            rpiList = null; rpiDetail = null; rpiSummary = null; rpiCats = null; rpiCatDrop = null; rpiCatOpen = false; rpiCheck = null; rpiDetailBox = null;
            rpiRowsByKey.Clear();
            rpiPage = new ScrollViewer { Content = rpiRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            racePage = new ScrollViewer { Content = raceRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            recordsPage = new ScrollViewer { Content = recordRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            infoPage = infoTab == null ? null : new ScrollViewer { Content = BuildInfo(s, null, null), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            courseRows = new StackPanel();
            coursesPage = new ScrollViewer { Content = courseRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            courseNameBox = new TextBox { Watermark = "Course name", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            TidyBox(courseNameBox);
            pilotNameBox = new TextBox { Watermark = "Your name (optional)", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            sectorNameBox = new TextBox { Watermark = "Sector name", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            TidyBox(sectorNameBox);
            controlsPage = new ScrollViewer { Content = controlRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // Search box above the settings: type part of a setting's name (or words from its description).
            settingsSearch = new TextBox { Watermark = "Search settings", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(16 * s, 4 * s, 24 * s, 8 * s) };
            settingsSearch.TextChanged += (_, __) => { if (lastSettingsPanel != null) FillSettings(lastSettingsPanel); };
            presetNameBox = new TextBox { Watermark = "Preset name (e.g. Testing, Race day)", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            TidyBox(presetNameBox);   // (made short on the Settings tab: without this its scroll bar shows as a grey square at its end)
            presetRows = new StackPanel();
            settingsPage = new ScrollViewer
            {
                Content = new StackPanel { Children = { presetRows, settingRows } },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // The search boxes stay at the top of their pages while the lists scroll; Settings has "Collapse all" beside its box.
            Grid Pinned(Control header, Control list)
            {
                var pinned = new Grid();
                pinned.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                pinned.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
                Grid.SetRow(header, 0); pinned.Children.Add(header);
                Grid.SetRow(list, 1); pinned.Children.Add(list);
                return pinned;
            }
            settingsLinkHost = new Border { VerticalAlignment = VerticalAlignment.Center };
            var settingsTop = new Grid();
            settingsTop.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            settingsTop.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            settingsSearch.Margin = new Thickness(16 * s, 0, 12 * s, 8 * s);
            Grid.SetColumn(settingsSearch, 0); settingsTop.Children.Add(settingsSearch);
            Grid.SetColumn(settingsLinkHost, 1); settingsTop.Children.Add(settingsLinkHost);
            settingsHolder = Pinned(settingsTop, settingsPage);
            controlsSearch = new TextBox { Watermark = "Search controls (an action or a key)", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(16 * s, 0, 24 * s, 8 * s) };
            controlsSearch.TextChanged += (_, __) => { if (lastControlsPanel != null) FillControls(lastControlsPanel); };
            controlsHolder = Pinned(controlsSearch, controlsPage);
            page = new ContentControl();

            note = Text("", Brush(HudColor.Yellow));
            SoftFontSize(note, 17 * s);
            note.Margin = new Thickness(16 * s, 6 * s, 0, 0);
            note.IsVisible = false;

            description = Text("", null, "SubTitle", "Bright");
            description.TextWrapping = TextWrapping.Wrap;
            // (Its own size, set outright: the game's style made it far bigger than the rows it explains.)
            description.FontSize = 19 * s;
            var descriptionBox = description;
            descriptionBox.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBlock.TextProperty) return;
                string shownWords = descriptionBox.Text;
                string tidyWords = TidyPaths(shownWords);
                if (tidyWords != shownWords) descriptionBox.Text = tidyWords;
            };
            description.Margin = new Thickness(20 * s, 4 * s, 8 * s, 0);
            description.VerticalAlignment = VerticalAlignment.Top;

            // The list (settings, controls, records ...) on the left; the description of whatever you point at
            // in its own column on the right, so it never covers a row.
            var inner = new Grid { Margin = new Thickness(24 * s, 24 * s, 24 * s, 18 * s) };
            inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Pixel)));
            inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(440 * s, GridUnitType.Pixel)));
            inner.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // the status strip
            inner.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            inner.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            statusStrip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16 * s, 0, 8 * s, 16 * s) };
            // (A line under it: the page scrolls beneath, and rows cut off right under the words looked like part of them.)
            var statusLine = new Border { Height = 1, Background = SectionLine, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16 * s, 0, 8 * s, 6 * s) };
            Grid.SetRow(statusLine, 0); Grid.SetColumn(statusLine, 0); Grid.SetColumnSpan(statusLine, 3); inner.Children.Add(statusLine);
            statusKey = null;
            Grid.SetRow(statusStrip, 0); Grid.SetColumn(statusStrip, 0); Grid.SetColumnSpan(statusStrip, 3); inner.Children.Add(statusStrip);
            Grid.SetRow(page, 1); Grid.SetColumn(page, 0); inner.Children.Add(page);
            Grid.SetRow(note, 2); Grid.SetColumn(note, 0); inner.Children.Add(note);
            var divider = new Border { Background = SectionLine, Margin = new Thickness(0, 4 * s, 0, 4 * s) };
            pageDivider = divider;
            Grid.SetRow(divider, 1); Grid.SetColumn(divider, 1); Grid.SetRowSpan(divider, 2); inner.Children.Add(divider);
            descScroll = new ScrollViewer { Content = description, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(descScroll, 1); Grid.SetColumn(descScroll, 2); Grid.SetRowSpan(descScroll, 2); inner.Children.Add(descScroll);
            tabs.Margin = new Thickness(0, 0, 0, 24 * s);

            // The Settings screen's bevelled panel (plain dark box if it can't be made).
            Control panel;
            if (NewGameControl("Keen.VRage.UI.Shared.Controls.BeveledBorder.BeveledBorder") is Decorator bevel)
            {
                bevel.Classes.Add("PrimaryPanel");
                bevel.Child = inner;
                panel = bevel;
            }
            else
            {
                panel = new Border
                {
                    Background = Argb(235, 14, 20, 26),
                    BorderBrush = Argb(160, 200, 210, 220),
                    BorderThickness = new Thickness(1),
                    Child = inner,
                };
            }

            // Bottom buttons, like the Settings screen: Back on the left, Reset to default on the right.
            contentPanel = panel;
            var back = MenuButton("Back", () => commands.Enqueue(new MenuCommand(MenuAction.Close)));
            back.HorizontalAlignment = HorizontalAlignment.Left;
            var reset = MenuButton("Reset to default", () =>
            {
                bool onSettings = OnSettingsTab;
                ShowConfirm(onSettings ? "This puts every setting back to its default. Your courses, times, cinematics and presets stay."
                                       : "This puts the plugin's settings back to their defaults. Your courses, times, cinematics and presets stay.",
                    () => commands.Enqueue(new MenuCommand(onSettings ? MenuAction.ResetSettings : MenuAction.ResetAll)));
            });
            reset.HorizontalAlignment = HorizontalAlignment.Right;
            resetButton = reset;
            var bottom = new Grid { Margin = new Thickness(0, 16 * s, 0, 0) };
            bottom.Children.Add(back);
            bottom.Children.Add(reset);

            var layout = new Grid
            {
                Margin = new Thickness(50 * s, 20 * s, 50 * s, 30 * s),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            layout.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(title, 0); layout.Children.Add(title);
            Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
            Grid.SetRow(panel, 2); layout.Children.Add(panel);
            Grid.SetRow(bottom, 3); layout.Children.Add(bottom);

            // Full screen: the game's menu background over a dark layer. Blocks clicks to the pause menu
            // underneath and holds keyboard focus.
            overlay = new Grid { Background = Argb(170, 6, 10, 16), Focusable = true, IsHitTestVisible = true };
            // Don't inherit the root panel's data: parts of the game's control templates bind to it and
            // would show it as text (that was the "None" on the sliders).
            overlay.DataContext = null;
            var background = NewGameControl("Keen.Game2.Client.UI.Library.Controls.ScreenBackground");
            if (background != null) { background.IsHitTestVisible = false; overlay.Children.Add(background); }
            overlay.Children.Add(layout);

            ShowPage();
        }

        private void ShowPage()
        {
            if (page == null) return;
            // The RPI designer takes the whole panel (no description column) while it's open on the RPI tab.
            bool designer = designerOpen && OnRpiTab && designerView != null;
            Grid.SetColumnSpan(page, designer ? 3 : 1);
            if (pageDivider != null) pageDivider.IsVisible = !designer;
            if (descScroll != null) descScroll.IsVisible = !designer;
            page.Content = designer ? designerView : OnRpiTab ? rpiPage : OnCineTab ? cinePage : OnHistoryTab ? historyPage : OnRaceTab ? racePage : OnCoursesTab ? coursesPage : OnInfoTab ? infoPage : OnRecordsTab ? recordsPage : OnSettingsTab ? (Control)settingsHolder : (Control)controlsHolder;
            defaultDescription = OnHistoryTab
                ? "Every race you've finished: when, where, which ship, the time, the best lap and penalties. Purple marks a personal best at the time. The graph shows how your times have gone on this course."
                : OnCineTab
                ? "Cinematics: a timeline of camera shots (orbit a ship, chase it, a fixed camera, a camera that moves from one place to another). Fly the camera to a view, add a shot, set how long it lasts, then Play."
                : OnRpiTab
                ? "Race Performance Index: make classes in the RPI designer, pick the one in use here, then look at a ship in Ship analyser mode (Race tab) to see how much RPI it uses."
                : OnRaceTab
                ? "Set up the next race: the number of laps, practice laps, and whose lap the ghost and racing line follow."
                : OnCoursesTab
                ? "Save and load courses, change the checkpoint order, build a course from grid names, and switch antennas off."
                : OnInfoTab
                ? "How the Race Display works and what everything on it means."
                : OnRecordsTab
                ? "Your best 5 races on this course for each lap count. Purple is the course record."
                : OnSettingsTab
                ? "Point at a setting to see what it does. Changes are saved straight away. Help (F1) > Racing Display explains everything on the HUD."
                : "Click a shortcut, then press the new key combination. Shortcuts used by two actions show in red.";
            if (resetButton != null) resetButton.IsVisible = !OnRecordsTab && !OnInfoTab && !OnCoursesTab && !OnRaceTab && !OnRpiTab && !OnCineTab && !OnHistoryTab; // those tabs have their own buttons
            description.Text = defaultDescription;
            overlay?.Focus(); // never leave focus on a slider while keys are being chosen
        }

        // ---------------------------------------------------------------- Controls tab

        private static readonly IBrush KeyCapBack = Argb(255, 20, 46, 62);
        private static readonly IBrush KeyCapEdge = Argb(255, 96, 122, 138);
        private static readonly IBrush ChangedInk = Argb(255, 255, 208, 150);   // a setting's name while it's not at its default
        private static readonly IBrush ChildInk = Argb(255, 176, 192, 204);     // the name of a setting that belongs to the one above it
        private static readonly IBrush ChildLine = Argb(255, 84, 110, 128);     // the line leading to it

        /// <summary>A shortcut as key caps: "Ctrl+Shift+F" becomes three small keys with + between.</summary>
        private Control KeyCaps(string binding, IBrush ink, double s)
        {
            string shown = binding ?? "";
            TextBlock Plain(string words, double size, IBrush brush)
            {
                var t = Text(words, brush);
                t.FontSize = size * s;
                t.FontWeight = FontWeight.Bold;
                t.VerticalAlignment = VerticalAlignment.Center;
                return t;
            }
            // (A key that is + itself: "Ctrl++".)
            if (shown == "+" || shown.EndsWith("++", StringComparison.Ordinal)) shown = shown.Substring(0, shown.Length - 1) + "Plus";
            var keyParts = shown.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            // (Not a shortcut: "(none)", or the words shown while a new one is being pressed.)
            if (keyParts.Count == 0 || (keyParts.Count == 1 && shown.Length > 14)) return Plain(shown, 15, ink);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 * s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int k = 0; k < keyParts.Count; k++)
            {
                if (k > 0) line.Children.Add(Plain("+", 13, Grey));
                line.Children.Add(new Border
                {
                    Background = KeyCapBack, BorderBrush = KeyCapEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4 * s),
                    Padding = new Thickness(8 * s, 1 * s, 8 * s, 2 * s), VerticalAlignment = VerticalAlignment.Center,
                    Child = Plain(keyParts[k], 14, ink),
                });
            }
            return line;
        }

        /// <summary>
        /// The Controls tab: each section folds, its shortcuts two to a row on the game's own buttons (click one to change it), a
        /// Reset beside any that's been changed, and a warning under any that shares its keys with another.
        /// </summary>
        private void FillControls(HudModel.ControlsPanel p)
        {
            lastControlsPanel = p;
            controlRows.Children.Clear();
            double s = scale;
            var findWords = (controlsSearch?.Text ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool finding = findWords.Length > 0;
            string section = null, headedFor = null;   // the section the rows are in now, and the one the last heading was for
            Grid pairs = null;        // the section's shortcuts, two side by side
            int inSection = 0, matched = 0;
            bool sectionShut = false;
            for (int i = 0; i < p.Rows.Count; i++)
            {
                var r = p.Rows[i];
                int row = i;
                if (r.Section != null) section = r.Section;
                string rowSection = section ?? "Controls";
                string hay = (rowSection + " " + r.Name + " " + r.Binding).ToLowerInvariant();
                if (finding && !findWords.All(w => hay.Contains(w))) continue;
                matched++;
                if (pairs == null || rowSection != headedFor)
                {
                    headedFor = rowSection;
                    string foldKey = "Controls: " + rowSection;
                    sectionShut = !finding && CollapsedSections.Contains(foldKey);
                    controlRows.Children.Add(CollapsibleHeader(rowSection, finding, foldKey, () => { if (lastControlsPanel != null) FillControls(lastControlsPanel); }));
                    controlRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
                    pairs = new Grid { Margin = new Thickness(16 * s, 0, 24 * s, 4 * s) };
                    pairs.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                    pairs.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(36 * s, GridUnitType.Pixel)));
                    pairs.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                    inSection = 0;
                    if (!sectionShut) controlRows.Children.Add(pairs);
                }
                if (sectionShut) continue;

                IBrush keyInk = r.Capturing ? White : r.Clash ? Brush(HudColor.Red) : r.Changed ? Brush(HudColor.Yellow) : White;
                // (The game's own button look, the shortcut as plain bold words, as its Controls settings have them.)
                var bind = ValueButton(r.Binding, keyInk, 0, () => commands.Enqueue(new MenuCommand(MenuAction.Change, row)));
                bind.MinWidth = 190 * s;
                bind.VerticalAlignment = VerticalAlignment.Center;
                if (r.Capturing) bind.Background = CaptureBack;
                var reset = ValueButton("Reset", Brush(HudColor.Orange), 74, () => commands.Enqueue(new MenuCommand(MenuAction.Default, row)));
                reset.MinHeight = 30 * s;
                reset.Margin = new Thickness(6 * s, 0, 0, 0);
                reset.VerticalAlignment = VerticalAlignment.Center;
                reset.IsVisible = r.Changed;

                var actionName = Text(r.Name, null, "OptionSubTitle");
                actionName.FontSize = 17 * s;
                actionName.TextTrimming = TextTrimming.CharacterEllipsis;
                if (r.Changed) actionName.Foreground = ChangedInk;
                var actionWords = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10 * s, 0) };
                actionWords.Children.Add(actionName);
                var sharedWith = r.Clash ? p.Rows.Where(o => !ReferenceEquals(o, r) && o.Binding == r.Binding).Select(o => o.Name).ToList() : new List<string>();
                if (r.Clash)
                {
                    var clashWords = Text(sharedWith.Count > 0 ? "Also used by: " + string.Join(", ", sharedWith) : "Also used by another action", Brush(HudColor.Red));
                    clashWords.FontSize = 12 * s;
                    clashWords.TextTrimming = TextTrimming.CharacterEllipsis;
                    actionWords.Children.Add(clashWords);
                }
                var changedDot = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 7 * s, Height = 7 * s, Fill = Brush(HudColor.Orange), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7 * s, 0), IsVisible = r.Changed, IsHitTestVisible = false,
                };
                var item = new Grid { Background = Brushes.Transparent, MinHeight = 42 * s, Margin = new Thickness(0, 1 * s, 0, 1 * s) };
                item.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                item.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                item.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                item.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                Grid.SetColumn(changedDot, 0); item.Children.Add(changedDot);
                Grid.SetColumn(actionWords, 1); item.Children.Add(actionWords);
                Grid.SetColumn(bind, 2); item.Children.Add(bind);
                Grid.SetColumn(reset, 3); item.Children.Add(reset);
                string about = r.Capturing
                    ? "Press the new key combination (with Ctrl / Shift / Alt if you want them). Esc or click again to cancel."
                    : $"{r.Name}: {r.Binding}. Click it to change it." + (r.Changed ? " Reset puts back the default." : "")
                      + (r.Clash ? (sharedWith.Count > 0 ? $" This shortcut is also used by: {string.Join(", ", sharedWith)}." : " This shortcut is also used by another action.") : "");
                item.PointerEntered += (_, __) => description.Text = about;

                int pairRow = inSection / 2;
                while (pairs.RowDefinitions.Count <= pairRow) pairs.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(item, pairRow);
                Grid.SetColumn(item, inSection % 2 == 0 ? 0 : 2);
                pairs.Children.Add(item);
                inSection++;
            }
            if (finding && matched == 0)
            {
                var noneFound = Text($"No controls match \"{controlsSearch?.Text?.Trim()}\".", Grey);
                noneFound.Margin = new Thickness(24 * s, 8 * s, 24 * s, 8 * s);
                SoftFontSize(noneFound, 18 * s);
                controlRows.Children.Add(noneFound);
            }

            note.Text = p.Note ?? "";
            note.IsVisible = !string.IsNullOrEmpty(note.Text);
            if (p.Rows.Any(r => r.Capturing)) description.Text = "Press the new key combination (with Ctrl / Shift / Alt if you want them). Esc or click again to cancel.";
        }

        // ---------------------------------------------------------------- Settings tab

        private TextBox settingsSearch;
        private HudModel.ControlsPanel lastSettingsPanel;

        private bool builtFilming = true;
        // Settings sections that only matter for filming (hidden with Settings > Menu > Filming off).
        private static readonly HashSet<string> FilmingSections = new HashSet<string> { "Cinematics", "Orbit camera", "Race spotlight" };

        private void FillSettings(HudModel.ControlsPanel p)
        {
            if (!ReferenceEquals(lastSettingsPanel, p) || presetRows.Children.Count == 0) FillPresets(p);
            lastSettingsPanel = p;
            if (PendingSettingsSearch != null && settingsSearch != null)
            {
                // (From a link in the guide: the setting's name goes in the search box.)
                string wanted = PendingSettingsSearch;
                PendingSettingsSearch = null;
                if (settingsSearch.Text != wanted) settingsSearch.Text = wanted;
            }
            settingRows.Children.Clear();
            settingViews.Clear();
            resetButtons.Clear();
            double s = scale;
            // Grouped by section (in the order sections first appear), filtered by the search box. Laps and practice
            // laps live on the Race tab.
            var order = p.Settings.Select(r => r.Section).Distinct().ToList();
            string q = settingsSearch?.Text?.Trim() ?? "";
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool searching = words.Length > 0;
            bool Matches(HudModel.SettingRow r)
            {
                string hay = (r.Section + " " + r.Name + " " + r.Description).ToLowerInvariant();
                return words.All(w => hay.Contains(w.ToLowerInvariant()));
            }
            var rows = p.Settings
                .Where(r => !r.Name.StartsWith("Laps per race") && !r.Name.StartsWith("Practice laps"))
                .Where(r => GateSettings.FilmingOn || !FilmingSections.Contains(r.Section))
                .Where(Matches).OrderBy(r => order.IndexOf(r.Section)).ToList();
            if (rows.Count == 0)
            {
                var none = Text($"No settings match \"{q}\".", Grey);
                none.Margin = new Thickness(24 * s, 8 * s, 24 * s, 8 * s);
                SoftFontSize(none, 18 * s);
                settingRows.Children.Add(none);
            }
            // Collapse all / expand all (not while searching: every match shows then).
            if (settingsLinkHost != null) settingsLinkHost.Child = null;
            if (!searching && rows.Count > 0)
            {
                var sections = rows.Select(r => r.Section).Distinct().ToList();
                bool allShut = sections.All(x => CollapsedSections.Contains(x));
                var linkText = Text(allShut ? "Expand all" : "Collapse all", Argb(255, 150, 175, 195));
                SoftFontSize(linkText, 15 * s);
                var link = new Border
                {
                    Child = linkText, Padding = new Thickness(8 * s, 4 * s, 8 * s, 4 * s),   // (no background: see-through ones show grey)
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 24 * s, 0),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                link.PointerEntered += (_, __) => description.Text = allShut ? "Opens every settings section." : "Folds every settings section away, so only their titles show; click a title to open just that one.";
                link.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    if (allShut) CollapsedSections.Clear();
                    else foreach (var x in sections) CollapsedSections.Add(x);
                    SaveCollapsedSections();
                    FillSettings(p);
                };
                if (settingsLinkHost != null) { link.Margin = new Thickness(0, 0, 24 * s, 8 * s); settingsLinkHost.Child = link; }
                else settingRows.Children.Add(link);
            }
            var parentNames = new HashSet<string>(rows.Where(o => o.DependsOn != null).Select(o => o.DependsOn));
            string section = null;
            foreach (var r in rows)
            {
                if (r.Section != section)
                {
                    section = r.Section;
                    string headSection = section;
                    settingRows.Children.Add(CollapsibleHeader(section, searching, null, null,
                        rows.Count(o => o.Section == headSection), rows.Any(o => o.Section == headSection && o.Changed)));
                    settingRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
                }
                // A closed section shows just its header (searching opens every section).
                if (!searching && CollapsedSections.Contains(r.Section)) continue;

                // (A unit in the name, "... (m/s, 0 = stopped)", moves beside the value: "0 m/s".)
                string shownName = r.Name, valueUnit = null;
                if (!r.Toggle && r.Choices == null) SplitUnit(r.Name, out shownName, out valueUnit);
                var control = r.Toggle ? MakeSwitch(r) : MakeSlider(r, valueUnit, true);
                // (Compact: the game's own switch and our slider, both slimmer, so more settings fit on a screen.)
                // (The slider is ours, so it's simply made shorter. The game's switch doesn't take a new height: forced
                // shorter, its lit half spilled over its frame and its words stayed big. It's left its own size and the
                // whole control is scaled down instead, words, frame and all.)
                Control rowValue = control;
                if (r.Toggle)
                    rowValue = new LayoutTransformControl
                    {
                        Child = control, LayoutTransform = new ScaleTransform(0.74, 0.74),
                        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                    };
                else { control.MinHeight = 0; control.Height = 34 * s; }
                string about = r.Description;
                if (r.DependsOn != null)
                    about += $" (Only used with \"{r.DependsOn}\" {(r.DependsOnOff ? "off" : "on")}.)";
                var row = OptionRow(shownName, about, rowValue);
                row.MinHeight = 38 * s;
                row.Margin = new Thickness(0, 1 * s, 24 * s, 1 * s);
                // Not at its default: a small orange dot before the name.
                var dot = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 8 * s, Height = 8 * s, Fill = Brush(HudColor.Orange),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2 * s, 0, 0, 0), IsVisible = r.Changed, IsHitTestVisible = false,
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);
                // ...and a small Reset at the end of the name column, to put just this one back to its default.
                int index = r.Index;
                var resetWords = Text("Reset", Brush(HudColor.Orange));
                resetWords.FontSize = 12 * s;
                resetWords.FontWeight = FontWeight.Bold;
                var reset = new Border
                {
                    Child = resetWords, Background = KeyCapBack, BorderBrush = Brush(HudColor.Orange), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3 * s), Padding = new Thickness(8 * s, 2 * s, 8 * s, 3 * s),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8 * s, 0), IsVisible = r.Changed,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                reset.PointerPressed += (_, press) =>
                {
                    press.Handled = true;
                    if (!settingViews.TryGetValue(index, out var view)) return;
                    Send(index, view.Row.Default);
                    if (lastSettingsPanel != null) FillSettings(lastSettingsPanel);   // shows the default value
                };
                Grid.SetColumn(reset, 0);
                row.Children.Add(reset);
                // Keep the name clear of the Reset button.
                // (A setting that only matters with another one on sits indented under it; a changed one has its name tinted.)
                if (row.Children.Count > 0 && row.Children[0] is TextBlock nameText)
                {
                    nameText.Margin = new Thickness(nameText.Margin.Left + (r.DependsOn != null ? 30 * s : 0), 0, 70 * s, 0);
                    nameText.FontSize = (r.DependsOn != null ? 16 : 17) * s;
                    if (r.Changed) nameText.Foreground = ChangedInk;
                    else if (r.DependsOn != null) nameText.Foreground = ChildInk;
                }
                // A setting and the ones that belong to it are one group: no gaps between their rows, and one line running down from under the parent's name with a tick across
                // to each child (it stops at the last one).
                bool hasChildren = parentNames.Contains(r.Name), isChild = r.DependsOn != null;
                if (hasChildren || isChild)
                {
                    // (No panel behind the group: a box round some settings and not others looked out of place. The
                    // line and the rows sitting close together are what tie them.)
                    int rowAt = rows.IndexOf(r);
                    bool lastChild = isChild && !hasChildren && (rowAt + 1 >= rows.Count || rows[rowAt + 1].DependsOn != r.DependsOn);
                    row.MinHeight = 40 * s;
                    row.Margin = new Thickness(0, hasChildren && !isChild ? 3 * s : 0, 24 * s, lastChild ? 3 * s : 0);
                    double treeX = 25 * s, treeWide = 1.5 * s;
                    if (isChild)
                    {
                        // Down the row (only to its middle on the last child), and a tick across to the name.
                        var treeDown = new Border
                        {
                            Width = treeWide, Background = ChildLine, HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = lastChild ? VerticalAlignment.Top : VerticalAlignment.Stretch,
                            Margin = new Thickness(treeX, 0, 0, 0), IsHitTestVisible = false,
                        };
                        if (lastChild) treeDown.Height = 20 * s + treeWide / 2;
                        var treeTick = new Border
                        {
                            Height = treeWide, Width = 13 * s, Background = ChildLine, HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = lastChild ? VerticalAlignment.Top : VerticalAlignment.Center,
                            Margin = new Thickness(treeX, lastChild ? 20 * s - treeWide / 2 : 0, 0, 0), IsHitTestVisible = false,
                        };
                        Grid.SetColumn(treeDown, 0); row.Children.Add(treeDown);
                        Grid.SetColumn(treeTick, 0); row.Children.Add(treeTick);
                    }
                    else
                    {
                        // The parent: the line starts under its name.
                        var treeStart = new Border
                        {
                            Width = treeWide, Height = 7 * s, Background = ChildLine, HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(treeX, 0, 0, 0), IsHitTestVisible = false,
                        };
                        Grid.SetColumn(treeStart, 0); row.Children.Add(treeStart);
                    }
                }
                resetButtons[r.Index] = reset;
                settingViews[r.Index] = (r, row, control, dot);
                SetRowEnabled(row, control, r.Enabled);
                settingRows.Children.Add(row);
            }
        }

        // Settings tab rows as shown, by setting index: to grey out dependants and move the "changed" dot as values change.
        private readonly Dictionary<int, (HudModel.SettingRow Row, Grid View, Control Value, Control Dot)> settingViews =
            new Dictionary<int, (HudModel.SettingRow, Grid, Control, Control)>();

        private readonly Dictionary<int, Control> resetButtons = new Dictionary<int, Control>();

        private static void SetRowEnabled(Grid row, Control value, bool enabled)
        {
            value.IsEnabled = enabled;
            foreach (var child in row.Children)
                if (child != null && !(child is Avalonia.Controls.Shapes.Ellipse)) child.Opacity = enabled ? 1 : 0.4;
        }

        /// <summary>A value was changed: update its dot, and grey out / bring back the settings that depend on it.</summary>
        private void SettingChangedLocally(int index, double value)
        {
            if (!settingViews.TryGetValue(index, out var v)) return;
            v.Row.Value = value;
            double tolerance = Math.Max(1e-6, v.Row.Step / 2);
            v.Dot.IsVisible = Math.Abs(value - v.Row.Default) > tolerance;
            if (resetButtons.TryGetValue(index, out var reset)) reset.IsVisible = v.Dot.IsVisible;
            if (v.View.Children.Count > 0 && v.View.Children[0] is TextBlock changedName)
            {
                if (v.Dot.IsVisible) changedName.Foreground = ChangedInk;
                else if (v.Row.DependsOn != null) changedName.Foreground = ChildInk;
                else changedName.ClearValue(TextBlock.ForegroundProperty);
            }
            foreach (var other in settingViews.Values)
                if (other.Row.DependsOn == v.Row.Name)
                    SetRowEnabled(other.View, other.Value, (value >= 0.5) != other.Row.DependsOnOff);
        }

        // ---- Records tab: lap counts hidden from the race lists (remembered in RecordsHidden.json)
        private HudModel.ControlsPanel lastRecordsPanel;
        private static HashSet<string> hiddenRecordGroups;
        private static string HiddenGroupsFile => System.IO.Path.Combine(PluginPaths.Folder, "RecordsHidden.json");

        private static HashSet<string> HiddenRecordGroups
        {
            get
            {
                if (hiddenRecordGroups != null) return hiddenRecordGroups;
                try
                {
                    if (System.IO.File.Exists(HiddenGroupsFile))
                        hiddenRecordGroups = new HashSet<string>(System.Text.Json.JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(HiddenGroupsFile)) ?? new List<string>());
                }
                catch { }
                return hiddenRecordGroups ??= new HashSet<string>();
            }
        }

        private static void SaveHiddenGroups()
        {
            try
            {
                System.IO.Directory.CreateDirectory(PluginPaths.Folder);
                System.IO.File.WriteAllText(HiddenGroupsFile, System.Text.Json.JsonSerializer.Serialize(HiddenRecordGroups.ToList()));
            }
            catch { }
        }

        // ---- sections that can be folded away (remembered in SettingsSections.json; Advanced starts folded)
        private static HashSet<string> collapsedSections;
        private static string SectionsFile => System.IO.Path.Combine(PluginPaths.Folder, "SettingsSections.json");

        private static HashSet<string> CollapsedSections
        {
            get
            {
                if (collapsedSections != null) return collapsedSections;
                try
                {
                    if (System.IO.File.Exists(SectionsFile))
                        collapsedSections = new HashSet<string>(System.Text.Json.JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(SectionsFile)) ?? new List<string>());
                }
                catch { }
                return collapsedSections ??= new HashSet<string> { "Advanced" };
            }
        }

        private static void SaveCollapsedSections()
        {
            try
            {
                System.IO.Directory.CreateDirectory(PluginPaths.Folder);
                System.IO.File.WriteAllText(SectionsFile, System.Text.Json.JsonSerializer.Serialize(CollapsedSections.ToList()));
            }
            catch { }
        }

        private static readonly Dictionary<string, string> UnitWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "m", "m" }, { "metres", "m" }, { "m/s", "m/s" }, { "km/h", "km/h" }, { "s", "s" }, { "seconds", "s" }, { "ms", "ms" },
            { "%", "%" }, { "kg", "kg" }, { "km", "km" }, { "g", "g" }, { "degrees", "\u00B0" }, { "deg", "\u00B0" }, { "\u00B0", "\u00B0" },
        };

        /// <summary>"Lift height (m)" -> "Lift height" and "m"; "Exit speed (m/s, 0 = stopped)" -> "Exit speed  (0 = stopped)" and "m/s".</summary>
        private static void SplitUnit(string name, out string shown, out string unit)
        {
            shown = name; unit = null;
            if (string.IsNullOrEmpty(name) || !name.EndsWith(")", StringComparison.Ordinal)) return;
            int open = name.LastIndexOf('(');
            if (open <= 0) return;
            string inside = name.Substring(open + 1, name.Length - open - 2);
            int comma = inside.IndexOf(',');
            string first = (comma >= 0 ? inside.Substring(0, comma) : inside).Trim();
            if (!UnitWords.TryGetValue(first, out string found)) return;
            unit = found;
            string rest = comma >= 0 ? inside.Substring(comma + 1).Trim() : "";
            shown = name.Substring(0, open).TrimEnd() + (rest.Length > 0 ? "  (" + rest + ")" : "");
        }

        /// <summary>
        /// A section title that folds its rows away. foldKey: what it's remembered under (the title, unless another
        /// tab has a section of the same name). refill: what redraws the list (the Settings list if left out).
        /// count / anyChanged: shown beside the title while it's folded.
        /// </summary>
        private Control CollapsibleHeader(string section, bool searching, string foldKey = null, Action refill = null, int count = 0, bool anyChanged = false)
        {
            string fold = foldKey ?? section;
            bool closed = !searching && CollapsedSections.Contains(fold);
            // The arrow is drawn (the game's font has no arrow characters): pointing right when folded, down when open.
            double sc = scale;
            var head = SectionHeader(section);
            head.FontSize = 21 * sc;
            head.Margin = new Thickness(10 * sc, 14 * sc, 0, 4 * sc);
            var arrow = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(closed ? "M 0,0 L 9,6 L 0,12 Z" : "M 0,0 L 12,0 L 6,9 Z"),
                Fill = White,
                Width = 12 * sc, Height = 12 * sc,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18 * sc, head.Margin.Top - head.Margin.Bottom, 0, 0),
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { arrow, head } };
            if (SectionHelp.TryGetValue(section, out string sectionHelp)) line.Children.Add(HelpDot(sectionHelp, section));
            // Folded: how many settings are inside, and an orange dot if any of them isn't at its default.
            if (closed && count > 0)
            {
                var countWords = Text(count.ToString(), Grey);
                SoftFontSize(countWords, 15 * sc);
                countWords.VerticalAlignment = VerticalAlignment.Center;
                countWords.Margin = new Thickness(12 * sc, head.Margin.Top - head.Margin.Bottom, 0, 0);
                line.Children.Add(countWords);
            }
            if (closed && anyChanged)
                line.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 8 * sc, Height = 8 * sc, Fill = Brush(HudColor.Orange), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(9 * sc, head.Margin.Top - head.Margin.Bottom, 0, 0),
                });
            var holder = new Border { Background = Brushes.Transparent, Child = line, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            holder.PointerPressed += (_, e) =>
            {
                if (searching) return;
                if (!CollapsedSections.Remove(fold)) CollapsedSections.Add(fold);
                SaveCollapsedSections();
                e.Handled = true;
                if (refill != null) refill();
                else if (lastSettingsPanel != null) FillSettings(lastSettingsPanel);
            };
            holder.PointerEntered += (_, __) => description.Text = closed
                ? $"Click to open the {section} section."
                : $"Click to fold the {section} section away (it's remembered).";
            return holder;
        }

        /// <summary>
        /// Settings presets, above the search box: save every Settings-tab value (laps too) under a name, and switch
        /// between them (e.g. Testing and Race day).
        /// </summary>
        private void FillPresets(HudModel.ControlsPanel p)
        {
            double s = scale;
            presetRows.Children.Clear();
            // (A folding section like the others: the presets and their files no longer sit loose above the settings.)
            presetRows.Children.Add(CollapsibleHeader("Presets", false, "Presets", () => FillPresets(p), p.Presets.Count));
            presetRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            if (CollapsedSections.Contains("Presets")) return;

            // Compact rows like the settings under them: slimmer buttons and boxes, a smaller name, less height.
            Grid Compact(Grid optionRow)
            {
                optionRow.MinHeight = 40 * s;
                optionRow.Margin = new Thickness(0, 1 * s, 24 * s, 1 * s);
                if (optionRow.Children.Count > 0 && optionRow.Children[0] is TextBlock rowName) rowName.FontSize = 17 * s;
                return optionRow;
            }
            Control Slim(string words, IBrush brush, Action click)
            {
                var slim = SlimButton(words, brush, click);
                slim.Margin = new Thickness(8 * s, 0, 0, 0);
                return slim;
            }
            // (The folder button: beside the others, its own size, not a bar the width of the page.)
            Control FolderButton() => Slim("Open folder", White, () => commands.Enqueue(new MenuCommand(MenuAction.OpenPresetFolder)));
            string folderAbout = $" Open folder shows where preset files are kept ({p.PresetFolder}): Export writes one there to share, and a file copied in from someone else shows below with an Import button.";

            if (p.Presets.Count > 0)
            {
                if (pickedPreset == null || !p.Presets.Contains(pickedPreset))
                    pickedPreset = p.CurrentPreset != null && p.Presets.Contains(p.CurrentPreset) ? p.CurrentPreset : p.Presets[0];
                var pick = new ComboBox { ItemsSource = p.Presets.ToList(), SelectedItem = pickedPreset, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
                pick.SelectionChanged += (_, __) => { if (pick.SelectedItem is string n) pickedPreset = n; };
                // The picker takes the room that's left; Load, Delete, Export and Open folder follow it on the one line.
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                Grid.SetColumn(pick, 0); row.Children.Add(pick);
                var presetButtons = new[]
                {
                    Slim("Load", White, () => commands.Enqueue(new MenuCommand(MenuAction.LoadPreset, text: pickedPreset))),
                    Slim("Delete", White, () => { string gone = pickedPreset; ShowConfirm($"Delete the preset '{gone}'? It can't be undone.", () => commands.Enqueue(new MenuCommand(MenuAction.DeletePreset, text: gone))); }),
                    Slim("Export", White, () => commands.Enqueue(new MenuCommand(MenuAction.ExportPreset, text: pickedPreset))),
                    FolderButton(),
                };
                for (int pb = 0; pb < presetButtons.Length; pb++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    Grid.SetColumn(presetButtons[pb], pb + 1);
                    row.Children.Add(presetButtons[pb]);
                }
                presetRows.Children.Add(Compact(OptionRow("Preset" + (p.CurrentPreset != null ? $"  (now: {p.CurrentPreset})" : ""),
                    "Load puts every setting on this tab (and the laps on the Race tab) back to how they were when the preset was saved. Keys, records and courses aren't part of a preset." + folderAbout, row)));
            }

            if (presetNameBox.Parent is Panel oldParent) oldParent.Children.Remove(presetNameBox);
            presetNameBox.MinWidth = 0;
            presetNameBox.MinHeight = 0;
            presetNameBox.Height = 34 * s;
            presetNameBox.FontSize = 16 * s;
            presetNameBox.Padding = new Thickness(8 * s, 0, 8 * s, 0);
            presetNameBox.VerticalAlignment = VerticalAlignment.Center;
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var save = Slim("Save", White, () =>
            {
                commands.Enqueue(new MenuCommand(MenuAction.SavePreset, text: presetNameBox.Text));
                pickedPreset = presetNameBox.Text?.Trim();
                presetNameBox.Text = "";
            });
            Grid.SetColumn(presetNameBox, 0); nameRow.Children.Add(presetNameBox);
            Grid.SetColumn(save, 1); nameRow.Children.Add(save);
            // (With no presets yet there's no line above for the folder button: it goes here.)
            if (p.Presets.Count == 0) { var folderHere = FolderButton(); Grid.SetColumn(folderHere, 2); nameRow.Children.Add(folderHere); }
            presetRows.Children.Add(Compact(OptionRow("Save settings as",
                "Type a name (e.g. Testing or Race day) and press Save to keep all the current settings as a preset. Saving with an existing name updates it." + (p.Presets.Count == 0 ? folderAbout : ""), nameRow)));

            // Preset files in the folder (yours, or ones copied in from someone else): each with a small Import.
            foreach (var (name, path) in p.PresetFiles)
            {
                string file = path;
                var import = Slim("Import", White, () => commands.Enqueue(new MenuCommand(MenuAction.ImportPreset, text: file)));
                import.Margin = new Thickness(0);
                import.HorizontalAlignment = HorizontalAlignment.Left;
                presetRows.Children.Add(Compact(OptionRow("    " + name,
                    $"Preset file: {System.IO.Path.GetFileName(path)}. Import adds it to your presets (one with the same name as yours replaces yours).", import)));
            }
        }

        // ---------------------------------------------------------------- Race tab

        /// <summary>Race setup: laps and practice laps, and which lap the ghost and racing line follow.</summary>
        // ---------------------------------------------------------------- RPI tab

        private static bool TryNumber(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return true;   // empty = 0 (no cost / no limit)
            return double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
                   && value >= 0 && value < 1e9;
        }

        /// <summary>A small number box: what you type is sent as you type it (red while it isn't a number).</summary>
        private TextBox NumberBox(double value, string watermark, Action<double> changed)
        {
            double s = scale;
            var box = new TextBox
            {
                Text = value != 0 ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "",
                Watermark = watermark, Width = 150 * s, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            box.TextChanged += (_, __) =>
            {
                bool ok = TryNumber(box.Text, out double v);
                box.Foreground = ok ? White : Brush(HudColor.Red);
                if (ok) changed(v);
            };
            return box;
        }

        private void FillRpi(HudModel.ControlsPanel p)
        {
            double s = scale;
            lastRpiPanel = p;
            rpiValues = new Dictionary<string, double>(p.RpiValues);
            rpiLimits = new Dictionary<string, int>(p.RpiLimits);
            rpiSteps = new List<(double Weight, double Budget)>(p.RpiSteps);
            rpiBudget = p.RpiBudget;
            rpiMinWeight = p.RpiMinWeight;
            rpiClassName = p.RpiCurrent;
            rpiShipName = p.RpiShipName;
            rpiShipMass = p.RpiShipMass;
            rpiShipCounts = p.RpiShipCounts;
            rpiDeleteArmed = false;
            rpiRows.Children.Clear();

            rpiRows.Children.Add(Section("RPI designer"));
            rpiRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            rpiRows.Children.Add(OptionRow("RPI designer",
                "Make RPI classes: give any block in the game a cost, set the class budget and minimum weight, and export or import classes to share them.",
                Widen(ValueButton("Open", White, 0, OpenDesigner))));

            rpiRows.Children.Add(Section("Class in use"));
            rpiRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var about = Text(p.RpiClasses.Count == 0
                ? "No classes yet. Open the RPI designer to make one (or import one)."
                : "Pick the class the Ship analyser (Race tab) checks ships against: it shows a ship's total RPI against the class budget, the minimum weight and what the total is made of. The class's costs also show in the build menu, next to the PCU when placing a block, and as a grid's total in the terminal's grid info.", Grey);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
            SoftFontSize(about, 16 * s);
            rpiRows.Children.Add(about);
            foreach (var c in p.RpiClasses)
            {
                string name = c.Name;
                bool inUse = name == p.RpiCurrent;
                string info = (c.Budget > 0 ? $"budget {c.Budget:0.##} RPI" : "no budget")
                              + (c.MinWeight > 0 ? $", min weight {c.MinWeight:#,0} kg" : ", no min weight")
                              + $", {c.Costed} block type{(c.Costed == 1 ? "" : "s")} with a cost";
                rpiRows.Children.Add(OptionRow(name, $"{name}: {info}.",
                    Widen(ValueButton(inUse ? "In use" : "Use", inUse ? Brush(HudColor.Purple) : White, 0,
                        () => commands.Enqueue(new MenuCommand(MenuAction.RpiSelectClass, text: name))))));
            }
            if (p.RpiCurrent != null)
                rpiRows.Children.Add(OptionRow("None",
                    "Stop checking ships against an RPI class (the analyser's RPI rows go away).",
                    Widen(ValueButton("Use none", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.RpiSelectClass, text: null))))));

            if (designerOpen) BuildDesigner(p);
        }

        private static string SpotPageAbout(int page) => page switch
        {
            0 => "Race results: the course, pilot, race time, penalties and every lap's time.",
            1 => "Race stats: top and average speed, consistency, distance, G-forces (when the race has them).",
            2 => "The ship: the ship analyser's numbers for the ship in the spotlight.",
            4 => "Lap replay: the course from above with each lap flown again as a moving dot, plus the course (length, checkpoints, high to low), the top speed (a white ring where it was hit) and the lap's crashes (red crosses) and missed checkpoints (orange rings). Its time is how long all the laps take, unless you set one.",
            5 => "Race graphs: the speed graph and the time gap graph from the results.",
            6 => "Ship card: the ship's headline numbers as big tiles: race time, mass, thrust to weight, 0 to 100, RPI, top speed.",
            7 => "Lap by lap: a bar and time for each lap (the faster the lap, the longer the bar), with the average and the spread.",
            8 => "Intro: a big title card at the top of the screen with the ship, course and pilot.",
            9 => "Outro: a big end card with the final time (and any penalties).",
            12 => "Fun facts: distance flown, football pitches end to end, average and top speed, the hardest turn in g.",
            14 => "Leaderboard: every saved race on this course with the same number of laps, fastest first, this one highlighted (needs at least two).",
            15 => "Head to head: this race against the one picked with Compare (same course), lap by lap with the gap on each, then the totals, best laps, laps won and the winner.",
            _ => "",
        };

        // ---------------------------------------------------------------- History tab

        // History tab: what's picked on the page itself (kept while the game runs).
        private string historyShip;                      // only this ship's races (null = every ship)
        private bool historyCleanOnly;                   // only races with no penalty
        private string historySort = "When";             // the column the table is sorted by
        private bool historySortDown = true;             // biggest / newest first
        private static readonly HudColor[] ShipColours =
        {
            HudColor.Cyan, new HudColor(255, 255, 160, 60), HudColor.Green, new HudColor(255, 255, 110, 180),
            HudColor.Yellow, new HudColor(255, 150, 140, 255), new HudColor(255, 120, 220, 200), new HudColor(255, 230, 120, 90),
        };

        private void FillHistory(HudModel.ControlsPanel p)
        {
            double s = scale;
            if (historyRows == null) return;
            historyRows.Children.Clear();
            void Send(MenuAction action, string text = null) => commands.Enqueue(new MenuCommand(action, text: text));
            TextBlock Small(string text, IBrush brush, double size = 15)
            {
                var t = Text(text, brush);
                t.VerticalAlignment = VerticalAlignment.Center;
                SoftFontSize(t, size * s);
                return t;
            }
            Button Pill(string text, IBrush brush, Action click)
            {
                var b = ValueButton(text, brush, 0, click);
                b.Margin = new Thickness(0, 2 * s, 6 * s, 2 * s);
                b.Padding = new Thickness(12 * s, 2 * s, 12 * s, 2 * s);
                return b;
            }
            string Time(double t) => LapTimer.Format(t);

            historyRows.Children.Add(Section("Session history"));
            historyRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24 * s, 4 * s, 24 * s, 4 * s) };
            bar.Children.Add(Pill(p.HistoryThisCourse ? "This course" + (p.HistoryCourse != null ? ": " + p.HistoryCourse : "") : "All courses",
                p.HistoryThisCourse ? Brush(HudColor.Cyan) : White, () => Send(MenuAction.HistoryFilter, "Course")));
            bar.Children.Add(Pill(p.HistorySessionOnly ? "This session" : "All time", p.HistorySessionOnly ? Brush(HudColor.Cyan) : White, () => Send(MenuAction.HistoryFilter, "Session")));
            bar.Children.Add(Pill(historyCleanOnly ? "Clean races only" : "With penalties too", historyCleanOnly ? Brush(HudColor.Cyan) : White, () => { historyCleanOnly = !historyCleanOnly; FillHistory(p); }));
            bar.Children.Add(new Border { Width = 16 * s });
            bar.Children.Add(Pill("Open folder", White, () => Send(MenuAction.HistoryOpen)));
            bar.Children.Add(Pill(historyClearArmed ? "Click again to clear" : "Clear history", historyClearArmed ? Brush(HudColor.Red) : Grey, () =>
            {
                if (historyClearArmed) { historyClearArmed = false; Send(MenuAction.HistoryClear); }
                else { historyClearArmed = true; FillHistory(p); }
            }));
            foreach (var child in bar.Children) if (child is Layoutable placed) placed.VerticalAlignment = VerticalAlignment.Center;
            historyRows.Children.Add(bar);

            // Each ship its own colour (in the order first raced), on the graph, in the table and on its filter pill.
            var shipNames = p.History.Select(r => r.Ship ?? "").Distinct().ToList();
            IBrush ShipBrush(string ship)
            {
                int i = shipNames.IndexOf(ship ?? "");
                return Brush(i < 0 ? HudColor.White : ShipColours[i % ShipColours.Length]);
            }
            if (historyShip != null && !shipNames.Contains(historyShip)) historyShip = null;
            if (shipNames.Count > 1)
            {
                var ships = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24 * s, 0, 24 * s, 6 * s) };
                var shipsLabel = Small("Ships:", Grey, 14);
                shipsLabel.Margin = new Thickness(0, 0, 8 * s, 0);
                ships.Children.Add(shipsLabel);
                ships.Children.Add(Pill("All", historyShip == null ? Brush(HudColor.Yellow) : White, () => { historyShip = null; FillHistory(p); }));
                // With lots of ships: a pill each for the most raced (and the one picked), the rest in a list, so the
                // row stays one line. Each shows how many races it has.
                const int MaxPills = 6;
                var counts = p.History.GroupBy(r => r.Ship ?? "").ToDictionary(g => g.Key, g => g.Count());
                var byRaces = shipNames.OrderByDescending(n => counts.TryGetValue(n, out int c) ? c : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var pills = byRaces.Take(MaxPills).ToList();
                if (historyShip != null && !pills.Contains(historyShip)) pills.Add(historyShip);
                string Label(string ship) => (string.IsNullOrEmpty(ship) ? "(no name)" : ship) + (counts.TryGetValue(ship ?? "", out int n) ? $"  {n}" : "");
                foreach (var ship in pills)
                {
                    string pick = ship;
                    var pill = Pill(Label(ship), ShipBrush(ship), () => { historyShip = historyShip == pick ? null : pick; FillHistory(p); });
                    if (historyShip == pick) { pill.BorderBrush = Brush(HudColor.Yellow); pill.BorderThickness = new Thickness(2); }
                    ships.Children.Add(pill);
                }
                var rest = byRaces.Where(n => !pills.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (rest.Count > 0)
                {
                    var more = new ComboBox
                    {
                        PlaceholderText = $"{rest.Count} more ship{(rest.Count == 1 ? "" : "s")}",
                        ItemsSource = rest.Select(Label).ToList(),
                        MinWidth = 190 * s, Height = 34 * s, MinHeight = 0, FontSize = 15 * s,
                        Margin = new Thickness(4 * s, 0, 0, 0),
                    };
                    more.SelectionChanged += (_, __) =>
                    {
                        if (more.SelectedIndex < 0 || more.SelectedIndex >= rest.Count) return;
                        historyShip = rest[more.SelectedIndex];
                        FillHistory(p);
                    };
                    ships.Children.Add(more);
                }
                foreach (var child in ships.Children) if (child is Layoutable placed) placed.VerticalAlignment = VerticalAlignment.Center;
                historyRows.Children.Add(ships);
            }

            var rows = p.History.Where(r => (historyShip == null || (r.Ship ?? "") == historyShip) && (!historyCleanOnly || r.Penalty <= 0)).ToList();
            if (rows.Count == 0)
            {
                var none = Small(p.HistoryCount == 0 ? "No races finished yet. Every race you finish is added here."
                    : "No races match: try All courses, All time, every ship or With penalties too.", Grey, 16);
                none.Margin = new Thickness(24 * s, 8 * s, 24 * s, 8 * s);
                historyRows.Children.Add(none);
                return;
            }

            // Summary and trend, for the lap count raced most often in what's shown.
            int laps = rows.GroupBy(r => r.Laps).OrderByDescending(g => g.Count()).First().Key;
            var same = rows.Where(r => r.Laps == laps).OrderBy(r => r.Date).ToList();
            double best = same.Min(r => r.Total), avg = same.Average(r => r.Total), bestFlown = same.Min(r => r.Flown), bestLap = rows.Where(r => r.BestLap > 0).Select(r => r.BestLap).DefaultIfEmpty(0).Min();
            var summary = Small($"{rows.Count} race{(rows.Count == 1 ? "" : "s")}   ·   {laps}-lap races: best {Time(best)} (flown {Time(bestFlown)}), average {Time(avg)}   ·   best lap {Time(bestLap)}", White, 16);
            summary.Margin = new Thickness(24 * s, 2 * s, 24 * s, 6 * s);
            historyRows.Children.Add(summary);

            // Each race's row in the table and its point on the graph (filled in below): pointing at either lights up the other.
            var rowLights = new Dictionary<HudModel.RaceHistoryRow, Border>();
            Border litRow = null;
            Action<HudModel.RaceHistoryRow> ringOnGraph = null;
            void LightRow(HudModel.RaceHistoryRow race)
            {
                // (Shown and hidden rather than its colour cleared: the game's UI kept drawing a cleared background.)
                if (litRow != null) litRow.IsVisible = false;
                litRow = null;
                if (race != null && rowLights.TryGetValue(race, out var back)) { back.IsVisible = true; litRow = back; }
            }

            if (same.Count >= 2 && p.HistoryThisCourse)
            {
                // Race times one after another (oldest left): a faint line through them, a dot each in its ship's
                // colour, personal bests ringed in purple. The flown times (no penalties) as a dashed-looking grey line.
                double w = 860 * s, h = 150 * s, pad = 12 * s;
                var chart = new Canvas { Width = w, Height = h, ClipToBounds = true, Background = Argb(255, 9, 26, 38) };
                double lo = Math.Min(same.Min(r => r.Total), same.Min(r => r.Flown)), hi = same.Max(r => r.Total);
                if (hi - lo < 0.5) { lo -= 0.25; hi += 0.25; }
                Point At(int i, double t) => new Point(pad + (w - 2 * pad) * (same.Count == 1 ? 0.5 : (double)i / (same.Count - 1)), pad + (h - 2 * pad) * (t - lo) / (hi - lo));
                void Line(Func<HudModel.RaceHistoryRow, double> value, IBrush brush, double width)
                {
                    var all = new List<Point>();
                    for (int i = 0; i < same.Count; i++) all.Add(At(i, value(same[i])));
                    for (int i = 0; i + 1 < all.Count; i += 40)
                    {
                        var chunk = new Avalonia.Collections.AvaloniaList<Point>();
                        for (int k = i; k <= Math.Min(i + 40, all.Count - 1); k++) chunk.Add(all[k]);
                        chart.Children.Add(new Avalonia.Controls.Shapes.Polyline { Points = chunk, Stroke = brush, StrokeThickness = width, IsHitTestVisible = false });
                    }
                }
                bool anyPenalty = same.Any(r => r.Penalty > 0);
                if (anyPenalty) Line(r => r.Flown, Argb(255, 70, 90, 105), 1.5 * s);
                Line(r => r.Total, Argb(255, 80, 140, 175), 2 * s);
                var pts = new List<Point>();
                for (int i = 0; i < same.Count; i++)
                {
                    var pt = At(i, same[i].Total);
                    pts.Add(pt);
                    double d = 8 * s;
                    var dot = new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = d, Height = d, Fill = ShipBrush(same[i].Ship), IsHitTestVisible = false,
                        Stroke = same[i].Best ? Brush(HudColor.Purple) : null, StrokeThickness = 2.5 * s,
                    };
                    if (same[i].Best) { dot.Width = dot.Height = d = 11 * s; }
                    Canvas.SetLeft(dot, pt.X - d / 2);
                    Canvas.SetTop(dot, pt.Y - d / 2);
                    chart.Children.Add(dot);
                }
                var fastLabel = Small("faster  " + Time(lo), Grey, 12);
                Canvas.SetLeft(fastLabel, pad); Canvas.SetTop(fastLabel, 0);
                var slowLabel = Small("slower  " + Time(hi), Grey, 12);
                Canvas.SetLeft(slowLabel, pad); Canvas.SetTop(slowLabel, h - 16 * s);
                chart.Children.Add(fastLabel);
                chart.Children.Add(slowLabel);

                // Pointing along the graph: the nearest race gets a guide line, a ring and its numbers; its row lights up.
                var guide = new Border { Width = 1 * s, Height = h, Background = Argb(255, 70, 100, 125), IsVisible = false, IsHitTestVisible = false };
                var ring = new Avalonia.Controls.Shapes.Ellipse { Width = 18 * s, Height = 18 * s, Stroke = Brush(HudColor.Yellow), StrokeThickness = 2.5 * s, IsVisible = false, IsHitTestVisible = false };
                var readText = Small("", White, 13);
                var readout = new Border
                {
                    Background = Argb(255, 14, 34, 48), BorderBrush = Argb(255, 60, 82, 98), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3 * s),
                    Padding = new Thickness(7 * s, 2 * s, 7 * s, 3 * s), Child = readText, IsVisible = false, IsHitTestVisible = false,
                };
                chart.Children.Insert(0, guide);
                chart.Children.Add(ring);
                chart.Children.Add(readout);
                int hovered = -1;
                void Ring(int i)
                {
                    if (i == hovered) return;
                    hovered = i;
                    guide.IsVisible = ring.IsVisible = readout.IsVisible = i >= 0;
                    if (i < 0) return;
                    var race = same[i];
                    Canvas.SetLeft(guide, pts[i].X);
                    Canvas.SetLeft(ring, pts[i].X - 9 * s);
                    Canvas.SetTop(ring, pts[i].Y - 9 * s);
                    string day = race.Date.Date == DateTime.Today ? "today " + race.Date.ToString("HH:mm") : race.Date.ToString("d MMM HH:mm");
                    readText.Text = $"Race {i + 1}:  {Time(race.Total)}" + (race.Penalty > 0 ? $"  (flown {Time(race.Flown)})" : "") + $"   {day}"
                        + (string.IsNullOrEmpty(race.Ship) ? "" : "   " + race.Ship) + (race.Best ? "   (personal best)" : "");
                    readText.Foreground = race.Best ? Brush(HudColor.Purple) : White;
                    readout.Measure(Size.Infinity);
                    double rw = readout.DesiredSize.Width;
                    Canvas.SetLeft(readout, Math.Clamp(pts[i].X + 12 * s, 0, Math.Max(0, w - rw)));
                    Canvas.SetTop(readout, pts[i].Y < h / 2 ? pts[i].Y + 12 * s : pts[i].Y - 34 * s);
                }
                ringOnGraph = race => Ring(race == null ? -1 : same.IndexOf(race));
                chart.PointerMoved += (_, e) =>
                {
                    double x = e.GetPosition(chart).X;
                    int nearest = 0;
                    for (int i = 1; i < pts.Count; i++) if (Math.Abs(pts[i].X - x) < Math.Abs(pts[nearest].X - x)) nearest = i;
                    Ring(nearest);
                    LightRow(same[nearest]);
                };
                chart.PointerExited += (_, __) => { Ring(-1); LightRow(null); };

                // The ships' colours, under the graph.
                var key = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6 * s, 0, 0) };
                foreach (var ship in same.Select(r => r.Ship ?? "").Distinct())
                {
                    var swatch = new Avalonia.Controls.Shapes.Ellipse { Width = 9 * s, Height = 9 * s, Fill = ShipBrush(ship), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5 * s, 0) };
                    key.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16 * s, 0), Children = { swatch, Small(string.IsNullOrEmpty(ship) ? "(no name)" : ship, Grey, 12) } });
                }
                var pbRing = new Avalonia.Controls.Shapes.Ellipse { Width = 11 * s, Height = 11 * s, Stroke = Brush(HudColor.Purple), StrokeThickness = 2.5 * s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5 * s, 0) };
                key.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16 * s, 0), Children = { pbRing, Small("personal best", Grey, 12) } });
                if (anyPenalty)
                {
                    var flownLine = new Border { Width = 16 * s, Height = 2 * s, Background = Argb(255, 70, 90, 105), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5 * s, 0) };
                    key.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { flownLine, Small("flown time (no penalties)", Grey, 12) } });
                }
                historyRows.Children.Add(new Border
                {
                    Background = Argb(255, 9, 26, 38), BorderBrush = Argb(255, 30, 60, 84), BorderThickness = new Thickness(1 * s), CornerRadius = new CornerRadius(6 * s),
                    Padding = new Thickness(8 * s), Margin = new Thickness(24 * s, 0, 48 * s, 10 * s), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new StackPanel { Children = { chart, key } },
                });
            }

            // ---- the table: sortable by any heading; by date it's grouped by day.
            bool showCourse = !p.HistoryThisCourse;
            var cols = new List<(string Key, string Head, double Width)> { ("When", "WHEN", 90) };
            if (showCourse) cols.Add(("Course", "COURSE", 210));
            cols.AddRange(new (string Key, string Head, double Width)[] { ("Ship", "SHIP", 190), ("Laps", "LAPS", 60), ("Time", "TIME", 115), ("Flown", "FLOWN", 115), ("BestLap", "BEST LAP", 110), ("Penalty", "PENALTY", 95), ("Top", "TOP SPEED", 110), ("", "", 190) });
            Func<HudModel.RaceHistoryRow, IComparable> SortKey(string k) => k switch
            {
                "Course" => r => r.Course ?? "",
                "Ship" => r => r.Ship ?? "",
                "Laps" => r => r.Laps,
                "Time" => r => r.Total,
                "Flown" => r => r.Flown,
                "BestLap" => r => r.BestLap > 0 ? r.BestLap : double.MaxValue,
                "Penalty" => r => r.Penalty,
                "Top" => r => r.TopSpeed,
                _ => r => r.Date,
            };
            if (!cols.Any(c => c.Key == historySort)) historySort = "When";
            var sorted = historySortDown ? rows.OrderByDescending(SortKey(historySort)).ThenByDescending(r => r.Date).ToList()
                                         : rows.OrderBy(SortKey(historySort)).ThenByDescending(r => r.Date).ToList();
            bool byDay = historySort == "When";

            var table = new Grid();
            foreach (var c in cols) table.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(c.Width * s)));
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cols.Count; c++)
            {
                if (cols[c].Key == "") continue;
                string colKey = cols[c].Key;
                bool on = historySort == colKey;
                var hd = Small(cols[c].Head + (on ? (historySortDown ? "  v" : "  ^") : ""), on ? Brush(HudColor.Yellow) : Grey, 13);
                var head = new Border
                {
                    Child = hd, Background = Argb(255, 9, 26, 38), Padding = new Thickness(0, 2 * s, 8 * s, 4 * s),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                head.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    if (historySort == colKey) historySortDown = !historySortDown;
                    else { historySort = colKey; historySortDown = colKey == "When" || colKey == "Top" || colKey == "Laps"; }
                    FillHistory(p);
                };
                head.PointerEntered += (_, __) => description.Text = "Click a heading to sort the races by it; click it again to turn the order round. Sorted by WHEN, they're grouped by day.";
                Grid.SetColumn(head, c);
                table.Children.Add(head);
            }
            var rowOf = new List<HudModel.RaceHistoryRow> { null };   // table row -> race (null: a heading or a day line)
            DateTime? dayShown = null;
            int shown = 0;
            foreach (var r in sorted)
            {
                if (shown++ >= 80) break;
                if (byDay && dayShown != r.Date.Date)
                {
                    dayShown = r.Date.Date;
                    table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    rowOf.Add(null);
                    string dayName = r.Date.Date == DateTime.Today ? "TODAY" : r.Date.Date == DateTime.Today.AddDays(-1) ? "YESTERDAY" : r.Date.ToString("dddd d MMMM").ToUpperInvariant();
                    var dayText = Small(dayName, Brush(HudColor.Cyan), 13);
                    dayText.FontWeight = FontWeight.Bold;
                    var dayLine = new StackPanel { Margin = new Thickness(0, 10 * s, 0, 2 * s), Children = { dayText, new Border { Height = 1, Background = SectionLine, Margin = new Thickness(0, 2 * s, 0, 0) } } };
                    Grid.SetRow(dayLine, table.RowDefinitions.Count - 1);
                    Grid.SetColumnSpan(dayLine, cols.Count);
                    table.Children.Add(dayLine);
                }
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                int row = table.RowDefinitions.Count - 1;
                rowOf.Add(r);
                // (Behind the row's cells: lit up when the race is pointed at, here or on the graph.)
                var rowBack = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(3 * s), Margin = new Thickness(-6 * s, 0, 0, 0), Background = Argb(255, 24, 58, 80), IsVisible = false };
                Grid.SetRow(rowBack, row);
                Grid.SetColumnSpan(rowBack, cols.Count);
                table.Children.Add(rowBack);
                rowLights[r] = rowBack;
                string when = byDay ? r.Date.ToString("HH:mm") : r.Date.Date == DateTime.Today ? "today " + r.Date.ToString("HH:mm") : r.Date.ToString("d MMM HH:mm");
                var cells = new List<(string Text, IBrush Brush)> { (when, Grey) };
                if (showCourse) cells.Add((r.Course ?? "", White));
                cells.AddRange(new[]
                {
                    (r.Ship ?? "", ShipBrush(r.Ship)), (r.Laps.ToString(), White),
                    (Time(r.Total), r.Best ? Brush(HudColor.Purple) : White),
                    (r.Penalty > 0 ? Time(r.Flown) : "-", r.Penalty > 0 ? Argb(255, 170, 185, 200) : Grey),
                    (r.BestLap > 0 ? Time(r.BestLap) : "-", White),
                    (r.Penalty > 0 ? $"+{r.Penalty:0.#} s" : "clean", r.Penalty > 0 ? Brush(HudColor.Red) : Brush(HudColor.Green)),
                    (r.TopSpeed > 0 ? GameOptions.Speed(r.TopSpeed) : "-", White),
                });
                for (int c = 0; c < cells.Count; c++)
                {
                    var t = Small(cells[c].Text, cells[c].Brush, 15);
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    t.Margin = new Thickness(0, 3 * s, 8 * s, 3 * s);
                    Grid.SetColumn(t, c);
                    Grid.SetRow(t, row);
                    table.Children.Add(t);
                }
                // Watch it (race spotlight) or race it again, when its race file is there; the race just flown can be saved first.
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Button Act(string text, IBrush brush, Action click)
                {
                    var b = ValueButton(text, brush, 0, click);
                    b.Margin = new Thickness(0, 1 * s, 4 * s, 1 * s);
                    b.Padding = new Thickness(8 * s, 0, 8 * s, 1 * s);
                    b.MinHeight = 0;
                    b.FontSize = 13 * s;
                    return b;
                }
                if (r.File != null)
                {
                    string file = r.File;
                    acts.Children.Add(Act("Watch", Brush(HudColor.Purple), () => Send(MenuAction.SpotlightStart, file)));
                    acts.Children.Add(Act("Race", Brush(HudColor.Cyan), () => Send(MenuAction.HistoryRace, file)));
                }
                else if (r.CanSave) acts.Children.Add(Act("Save to watch", White, () => Send(MenuAction.SpotlightExport)));
                Grid.SetColumn(acts, cols.Count - 1);
                Grid.SetRow(acts, row);
                table.Children.Add(acts);
            }
            // Pointing at a row lights it and rings its point on the graph. (Worked out from where the pointer is,
            // since the row's own background can't catch the mouse: see-through backgrounds draw grey in the game.)
            var tableCard = new Border
            {
                Background = Argb(255, 9, 26, 38), BorderBrush = Argb(255, 30, 60, 84), BorderThickness = new Thickness(1 * s), CornerRadius = new CornerRadius(6 * s),
                Padding = new Thickness(12 * s, 6 * s, 12 * s, 8 * s), Margin = new Thickness(24 * s, 0, 48 * s, 12 * s), HorizontalAlignment = HorizontalAlignment.Left,
                Child = table,
            };
            tableCard.PointerMoved += (_, e) =>
            {
                double y = e.GetPosition(table).Y, top = 0;
                HudModel.RaceHistoryRow over = null;
                for (int i = 0; i < table.RowDefinitions.Count; i++)
                {
                    double rh = table.RowDefinitions[i].ActualHeight;
                    if (y >= top && y < top + rh) { over = i < rowOf.Count ? rowOf[i] : null; break; }
                    top += rh;
                }
                LightRow(over);
                ringOnGraph?.Invoke(over);
            };
            tableCard.PointerExited += (_, __) => { LightRow(null); ringOnGraph?.Invoke(null); };
            historyRows.Children.Add(tableCard);
            if (sorted.Count > 80)
            {
                var more = Small($"...and {sorted.Count - 80} more (all in History.jsonl in the plugin's folder)", Grey, 14);
                more.Margin = new Thickness(24 * s, 0, 24 * s, 12 * s);
                historyRows.Children.Add(more);
            }
            if (rows.Any(r => r.File == null && !r.CanSave))
            {
                var tip = Small("Watch and Race show for races that have a race file (Race tab > Race spotlight > Export, or Save to watch on the race you've just flown).", Grey, 13);
                tip.TextWrapping = TextWrapping.Wrap;
                tip.Margin = new Thickness(24 * s, 0, 48 * s, 12 * s);
                historyRows.Children.Add(tip);
            }
        }

        // ---------------------------------------------------------------- Cinematics tab

        private static IBrush CineColour(string type) =>
            type == "Fixed" ? Argb(255, 70, 110, 190) : type == "Move" ? Argb(255, 150, 90, 190) : type == "Chase" ? Argb(255, 60, 150, 110)
            : type == "Path" ? Argb(255, 200, 90, 130) : Argb(255, 190, 130, 50);
        private static string CineName(string type) =>
            type == "Fixed" ? "Fixed camera" : type == "Move" ? "Moving camera" : type == "Path" ? "Camera path" : type == "Chase" ? "Chase" : "Orbit";

        /// <summary>
        /// The race spotlight's page list (Race tab > Race spotlight > Change the pages): one framed list, a row per
        /// page in the order they show, each with a switch, its name, its own time (blank = its usual time) and
        /// drawn up / down arrows. Reset sits at the bottom.
        /// </summary>
        /// <summary>
        /// The race spotlight leaderboard's list (Race tab > Race spotlight > Change the board): a switch for one row a
        /// ship, then a row per ship with a switch (on = on the board).
        /// </summary>
        private Control SpotBoardList(HudModel.ControlsPanel p)
        {
            double s = scale;
            var list = new StackPanel();
            IBrush cardBack = Argb(255, 7, 20, 30), divider = Argb(255, 34, 52, 66);   // (solid: see-through ones are drawn grey)
            Control Switch(bool on) => new Border
            {
                Width = 36 * s, Height = 20 * s, CornerRadius = new CornerRadius(10 * s), Padding = new Thickness(3 * s, 0),
                Background = on ? Brush(HudColor.Green) : Argb(255, 40, 55, 68),
                Child = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 14 * s, Height = 14 * s, Fill = on ? White : Argb(255, 150, 160, 170),
                    HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Control Arrow(bool up, bool usable, Action click)
            {
                var hit = new Border
                {
                    Background = cardBack, Padding = new Thickness(6 * s), VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse(up ? "M 0,7 L 6,1 L 12,7" : "M 0,1 L 6,7 L 12,1"),
                        Stroke = usable ? White : Argb(255, 50, 66, 80), StrokeThickness = 2.2 * s,
                        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 14 * s, Height = 9 * s, Stretch = Stretch.Uniform,
                    },
                };
                hit.PointerPressed += (_, e) => { e.Handled = true; if (usable) click(); };
                SmallTip(hit, up ? "Ran earlier" : "Ran later");
                return hit;
            }
            void Row(bool on, string name, string note, string tip, Action click, bool top, Control up = null, Control down = null)
            {
                var sw = new Border
                {
                    Background = cardBack, Child = Switch(on), Padding = new Thickness(6 * s), VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                sw.PointerPressed += (_, e) => { e.Handled = true; click(); };
                SmallTip(sw, tip);
                var nameText = Text(name, on ? White : Argb(255, 110, 120, 130));
                nameText.VerticalAlignment = VerticalAlignment.Center;
                nameText.Margin = new Thickness(12 * s, 0, 12 * s, 0);
                SoftFontSize(nameText, 16 * s);
                var noteText = Text(note ?? "", Grey);
                noteText.VerticalAlignment = VerticalAlignment.Center;
                noteText.Margin = new Thickness(0, 0, 8 * s, 0);
                SoftFontSize(noteText, 14 * s);
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), MinHeight = 38 * s };
                Grid.SetColumn(nameText, 1); Grid.SetColumn(noteText, 2);
                grid.Children.Add(sw); grid.Children.Add(nameText); grid.Children.Add(noteText);
                if (up != null) { Grid.SetColumn(up, 3); grid.Children.Add(up); }
                if (down != null) { Grid.SetColumn(down, 4); grid.Children.Add(down); }
                list.Children.Add(new Border
                {
                    Background = cardBack, Child = grid, Padding = new Thickness(8 * s, 0, 8 * s, 0),
                    BorderBrush = divider, BorderThickness = new Thickness(0, top ? 0 : 1, 0, 0),
                });
            }
            Row(p.SpotBoardBestPerShip, "One row a ship (its fastest race)", p.SpotBoardBestPerShip ? "on" : "every race listed",
                "On: each ship is on the board once, at its fastest time, so beating its own time moves it up. Off: every race file has its own row.",
                () => commands.Enqueue(new MenuCommand(MenuAction.SpotBoardSet, 0, 0, "*best*")), true);
            Row(p.SpotBoardReveal, "Bring the ships on one at a time", p.SpotBoardReveal ? "in the order below" : "all at once",
                "On: the Leaderboard page starts with the first ship to run, then adds the next every couple of seconds, each going to its place on the board (a faster ship moves up past the ones before it). Off: the whole board shows at once.",
                () => commands.Enqueue(new MenuCommand(MenuAction.SpotBoardSet, 0, 0, "*reveal*")), false);
            var caption = Text(p.SpotBoardShips.Count == 0 ? "No race files yet: export a race first."
                : "The ships, in the order they ran: move them with the arrows. Switch one off to leave it off the board.", Grey);
            caption.TextWrapping = TextWrapping.Wrap;
            caption.Margin = new Thickness(14 * s, 8 * s, 14 * s, 6 * s);
            SoftFontSize(caption, 14 * s);
            list.Children.Add(new Border { BorderBrush = divider, BorderThickness = new Thickness(0, 1, 0, 0), Child = caption });
            for (int si = 0; si < p.SpotBoardShips.Count; si++)
            {
                var ship = p.SpotBoardShips[si];
                string shipName = ship.Ship;
                bool first = si == 0, last = si == p.SpotBoardShips.Count - 1;
                Row(!ship.Hidden, $"{si + 1}.  {shipName}", ship.Races == 1 ? "1 race" : $"{ship.Races} races",
                    ship.Hidden ? "Left off the leaderboard: click to put it back" : "On the leaderboard: click to leave it off",
                    () => commands.Enqueue(new MenuCommand(MenuAction.SpotBoardSet, 0, 0, shipName)), false,
                    Arrow(true, !first, () => commands.Enqueue(new MenuCommand(MenuAction.SpotBoardSet, 0, 7, "^" + shipName))),
                    Arrow(false, !last, () => commands.Enqueue(new MenuCommand(MenuAction.SpotBoardSet, 0, 7, "v" + shipName))));
            }
            return new Border
            {
                Background = cardBack, BorderBrush = Argb(255, 60, 82, 98), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6 * s), Margin = new Thickness(36 * s, 2 * s, 48 * s, 12 * s), MaxWidth = 620 * s,
                HorizontalAlignment = HorizontalAlignment.Left, Child = list, ClipToBounds = true,
            };
        }

        private Control SpotPageList(HudModel.ControlsPanel p)
        {
            double s = scale;
            var list = new StackPanel();
            // Every colour here is solid: see-through ones are drawn grey by the game's UI.
            IBrush cardBack = Argb(255, 7, 20, 30), rowBack = cardBack, divider = Argb(255, 34, 52, 66);

            // A plain clickable area (the game's buttons are too big and boxy for a list like this).
            Border Clickable(Control child, Action click, string tip)
            {
                var hit = new Border
                {
                    Background = rowBack, Child = child, Padding = new Thickness(6 * s),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
                };
                hit.PointerPressed += (_, e) => { e.Handled = true; click(); };
                if (tip != null) SmallTip(hit, tip);
                return hit;
            }
            Control Chevron(bool up, bool usable) => new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(up ? "M 0,7 L 6,1 L 12,7" : "M 0,1 L 6,7 L 12,1"),
                Stroke = usable ? White : Argb(255, 50, 66, 80), StrokeThickness = 2.2 * s,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                Width = 14 * s, Height = 9 * s, Stretch = Stretch.Uniform,
            };
            Control Switch(bool on)
            {
                var knob = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 14 * s, Height = 14 * s, Fill = on ? White : Argb(255, 150, 160, 170),
                    HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return new Border
                {
                    Width = 36 * s, Height = 20 * s, CornerRadius = new CornerRadius(10 * s), Padding = new Thickness(3 * s, 0),
                    Background = on ? Brush(HudColor.Green) : Argb(255, 40, 55, 68), Child = knob,
                };
            }

            var caption = Text("Shown in this order. Switch a page off to skip it; a blank time = its usual time.", Grey);
            caption.TextWrapping = TextWrapping.Wrap;
            caption.Margin = new Thickness(14 * s, 8 * s, 14 * s, 6 * s);
            SoftFontSize(caption, 14 * s);
            list.Children.Add(caption);

            for (int pi = 0; pi < p.SpotPages.Count; pi++)
            {
                var sp = p.SpotPages[pi];
                int pageId = sp.Page;
                bool first = pi == 0, last = pi == p.SpotPages.Count - 1;

                var number = Text((pi + 1).ToString(), Argb(255, 120, 135, 150));
                number.Width = 22 * s;
                number.TextAlignment = TextAlignment.Right;
                number.VerticalAlignment = VerticalAlignment.Center;
                SoftFontSize(number, 14 * s);
                var name = Text(sp.Name, sp.On ? White : Argb(255, 110, 120, 130));
                name.VerticalAlignment = VerticalAlignment.Center;
                name.Margin = new Thickness(12 * s, 0, 12 * s, 0);
                SoftFontSize(name, 16 * s);

                var secs = CommitBox(sp.Seconds, 54, v => commands.Enqueue(new MenuCommand(MenuAction.SpotPageSet, pageId, v, "Seconds")), "auto");
                secs.Height = 28 * s;
                secs.FontSize = 14 * s;
                secs.Margin = new Thickness(0);
                secs.HorizontalContentAlignment = HorizontalAlignment.Center;
                var secsUnit = Text("s", Grey);
                secsUnit.VerticalAlignment = VerticalAlignment.Center;
                secsUnit.Margin = new Thickness(5 * s, 0, 10 * s, 0);
                SoftFontSize(secsUnit, 14 * s);

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto"), MinHeight = 40 * s };
                var sw = Clickable(Switch(sp.On), () => commands.Enqueue(new MenuCommand(MenuAction.SpotPageSet, pageId, 0, "On")),
                    sp.On ? "Showing: click to skip it" : "Skipped: click to show it");
                var up = Clickable(Chevron(true, !first), () => { if (!first) commands.Enqueue(new MenuCommand(MenuAction.SpotPageSet, pageId, 0, "Up")); }, "Move up");
                var down = Clickable(Chevron(false, !last), () => { if (!last) commands.Enqueue(new MenuCommand(MenuAction.SpotPageSet, pageId, 0, "Down")); }, "Move down");
                Control[] cells = { sw, number, name, secs, secsUnit, up, down };
                for (int c = 0; c < cells.Length; c++) { Grid.SetColumn(cells[c], c); grid.Children.Add(cells[c]); }

                var row = new Border
                {
                    Background = rowBack, Child = grid, Padding = new Thickness(8 * s, 0, 8 * s, 0),
                    BorderBrush = divider, BorderThickness = new Thickness(0, 1, 0, 0),
                };
                // Pointing at a row shows what the page is. (No row highlight: the game's UI fades backgrounds, so rows flickered.)
                row.PointerEntered += (_, __) => { if (description != null) description.Text = SpotPageAbout(pageId); };
                list.Children.Add(row);
            }

            // Footer: Reset, as a quiet link on the right.
            var resetText = Text("Reset order and times", Argb(255, 150, 175, 195));
            SoftFontSize(resetText, 14 * s);
            var reset = Clickable(resetText, () => ShowConfirm("Put the spotlight pages back to the usual order, all on, with their usual times?",
                    () => commands.Enqueue(new MenuCommand(MenuAction.SpotPageSet, 0, 0, "Reset"))),
                "Puts the pages back to the usual order, all on, with their usual times");
            // Turn all on, or (once every page is on) Turn all off.
            bool allAreOn = p.SpotPages.Count > 0 && p.SpotPages.All(x => x.On);
            var allOnText = Text(allAreOn ? "Turn all off" : "Turn all on", Argb(255, 150, 175, 195));
            SoftFontSize(allOnText, 14 * s);
            var allOn = Clickable(allOnText, () => commands.Enqueue(new MenuCommand(MenuAction.SpotPageSet, 0, 0, allAreOn ? "AllOff" : "AllOn")),
                allAreOn ? "Switches every page off (the order and times stay as they are)" : "Switches every page on (the order and times stay as they are)");
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 * s,
                Margin = new Thickness(0, 2 * s, 8 * s, 2 * s), Children = { allOn, reset } };
            list.Children.Add(new Border { BorderBrush = divider, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer });

            return new Border
            {
                Background = cardBack, BorderBrush = Argb(255, 60, 82, 98), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6 * s), Margin = new Thickness(36 * s, 2 * s, 48 * s, 12 * s), MaxWidth = 620 * s,
                HorizontalAlignment = HorizontalAlignment.Left, Child = list, ClipToBounds = true,
            };
        }

        /// <summary>
        /// Puts a drawn drop-down arrow after a button's text (the game's font has no arrow characters, so a
        /// text arrow shows as a box): pointing down when the list is closed, up when it's open.
        /// </summary>
        private Button WithDropArrow(Button b, bool open, IBrush brush)
        {
            if (b.Content is not Control label) return b;
            b.Content = null;   // (let go of the label before it goes in the new row)
            label.VerticalAlignment = VerticalAlignment.Center;
            var arrow = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(open ? "M 0,9 L 6,0 L 12,9 Z" : "M 0,0 L 12,0 L 6,9 Z"),
                Fill = brush,
                Width = 10 * scale, Height = 10 * scale,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8 * scale, 1 * scale, 0, 0),
            };
            b.Content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { label, arrow } };
            return b;
        }

        /// <summary>A small drawn cross for delete buttons (the game's font has no ✕ character: it showed as a box).</summary>
        private Avalonia.Controls.Shapes.Path Cross(IBrush brush)
        {
            double s = scale;
            return new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                Stroke = brush, StrokeThickness = 2 * s, StrokeLineCap = PenLineCap.Round,
                Width = 10 * s, Height = 10 * s, Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private Button SmallButton(string text, IBrush brush, Action click)
        {
            var b = ValueButton(text, brush, 0, click);
            b.Margin = new Thickness(0, 2 * scale, 6 * scale, 2 * scale);
            b.Padding = new Thickness(10 * scale, 2 * scale, 10 * scale, 2 * scale);
            return b;
        }

        private int cineSelected = -1;          // the shot picked on the timeline
        private int? cineGateOpen;              // auto switch: the gate whose drop-down list is open
        private bool spotPagesOpen;             // race spotlight: the page list is open
        private bool spotBoardOpen;             // race spotlight: the leaderboard's ship list is open
        private string spotCineOpen;            // race spotlight: the race file whose cinematic drop-down is open
        private int cineFxSelected = -1;        // or the effect picked on the layer under it
        private int cineKeySelected = -1;       // the picked shot's picked keyframe (camera path)
        private int cineKeyFor = -1;            // (which shot that was for)
        private bool cineFocusFx;
        private static IBrush FxColour(string type) =>
            type == "Roll" ? Argb(255, 170, 80, 110) : type == "Shake" ? Argb(255, 125, 120, 60) : type == "Panel" ? Argb(255, 90, 100, 120)
            : type == "Fade" ? Argb(255, 60, 60, 70) : type == "Bars" ? Argb(255, 110, 85, 60) : Argb(255, 40, 140, 150);
        private static string FxName(string type) => type == "Roll" ? "Tilt" : type == "Shake" ? "Shake" : type == "Panel" ? "Panel" : type == "Fade" ? "Fade" : type == "Bars" ? "Letterbox" : "FOV";
        /// <summary>A small drawn sign for a shot type (the game's font has no symbols): orbit ring, chase arrow, camera, glide, curve.</summary>
        private Control ShotIcon(string type, double size)
        {
            double s = scale;
            string shape = type == "Orbit" ? "M 1,6 A 5,5 0 1 1 11,6 A 5,5 0 1 1 1,6 M 5,6 L 7,6"
                : type == "Chase" ? "M 1,6 L 11,6 M 7,2 L 11,6 L 7,10"
                : type == "Fixed" ? "M 1,3 L 7.5,3 L 7.5,9 L 1,9 Z M 7.5,5 L 11,3 L 11,9 L 7.5,7"
                : type == "Path" ? "M 1,10 C 3,0 7,12 11,2"
                : "M 1,10 L 11,2 M 7,2 L 11,2 L 11,6";
            return new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(shape), Stroke = White, StrokeThickness = 1.6 * s,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                Width = size * s, Height = size * s, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6 * s, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };
        }
        private bool cineFocusBar;              // give the timeline the keyboard after the next rebuild (so Delete works)
        private Grid cineBar;
        private Border cinePlayhead;
        private Control cinePlayheadSpan;       // what the playhead runs down through (ruler, shots and effects)
        private double cineTotal;
        private double cineCue;                 // where the playhead sits while nothing plays (Play starts from here)
        private double cineZoomLevel = 1, cineScrollX;   // the timeline's zoom (1 = the whole thing fits) and how far it's scrolled

        /// <summary>A number box that sends its value when Enter is pressed or it loses focus (not while typing).</summary>
        private TextBox CommitBox(double value, double width, Action<double> commit, string blankMeansZero = null)
        {
            double s = scale;
            var box = new TextBox
            {
                Text = blankMeansZero != null && value == 0 ? "" : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                Watermark = blankMeansZero,
                Width = width * s, MinHeight = 0, Height = 34 * s, FontSize = 15 * s,
                Padding = new Thickness(6 * s, 0, 6 * s, 0),
                VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2 * s, 6 * s, 2 * s),
            };
            TidyBox(box);
            bool sent = false;
            void Commit()
            {
                if (sent) return;
                double v = 0;
                bool ok = (blankMeansZero != null && string.IsNullOrWhiteSpace(box.Text)) || TryNumber(box.Text, out v);
                box.Foreground = ok ? White : Brush(HudColor.Red);
                if (ok && Math.Abs(v - value) > 1e-9) { sent = true; commit(v); }
            }
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            box.LostFocus += (_, __) => Commit();
            return box;
        }

        private static string CineTime(double seconds) =>
            seconds < 60 ? $"{seconds:0.#}s" : $"{(int)(seconds / 60)}:{seconds % 60:00.#}";

        /// <summary>
        /// The status strip at the top of the menu: the course and ship, then a chip for each thing running
        /// (cinematic, orbit camera, autopilot, spotlight, ship analyser, replay, course edit mode), each with Stop.
        /// </summary>
        private void UpdateStatusStrip(HudModel.ControlsPanel p)
        {
            if (statusStrip == null) return;
            var running = new List<(string Text, HudColor Colour, MenuAction Stop)>();
            if (p.CinePlaying) running.Add(("Cinematic playing", HudColor.Purple, MenuAction.CineStop));
            else if (p.OrbitOpen) running.Add(("Orbit camera", HudColor.Cyan, MenuAction.OrbitCamera));
            if (p.AutopilotOn) running.Add(("Autopilot", HudColor.Cyan, MenuAction.Autopilot));
            if (p.SpotlightOpen != null) running.Add(("Race spotlight", HudColor.Purple, MenuAction.SpotlightStop));
            else if (p.AnalyserOpen) running.Add(("Ship analyser", HudColor.Cyan, MenuAction.ShipAnalyser));
            if (p.Replaying) running.Add(("Replay", HudColor.Purple, MenuAction.StopReplay));
            if (p.EditMode) running.Add(("Course edit mode", HudColor.Yellow, MenuAction.ToggleEditMode));
            string key = (p.CourseName ?? "") + "|" + (p.CurrentShip ?? "") + "|" + string.Join(",", running.Select(r => r.Text));
            if (key == statusKey) return;
            statusKey = key;

            double s = scale;
            statusStrip.Children.Clear();
            Control Item(string label, string value, IBrush brush)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22 * s, 0) };
                var l = Text(label, Grey);
                SoftFontSize(l, 15 * s);
                l.Margin = new Thickness(0, 0, 6 * s, 0);
                l.VerticalAlignment = VerticalAlignment.Center;
                var v = Text(value, brush);
                v.FontWeight = FontWeight.Bold;
                v.VerticalAlignment = VerticalAlignment.Center;
                SoftFontSize(v, 15 * s);
                line.Children.Add(l);
                line.Children.Add(v);
                return line;
            }
            bool noCourse = string.IsNullOrEmpty(p.CourseName), noShip = string.IsNullOrEmpty(p.CurrentShip);
            statusStrip.Children.Add(Item("Course", noCourse ? "none saved" : p.CourseName, noCourse ? Grey : White));
            statusStrip.Children.Add(Item("Ship", noShip ? "not in a cockpit" : p.CurrentShip, noShip ? Grey : White));
            if (running.Count == 0)
            {
                statusStrip.Children.Add(Item("Running", "nothing", Grey));
                return;
            }
            foreach (var r in running)
            {
                var stopAction = r.Stop;
                var name = Text(r.Text, Brush(r.Colour));
                name.FontWeight = FontWeight.Bold;
                SoftFontSize(name, 14 * s);
                name.VerticalAlignment = VerticalAlignment.Center;
                var stopText = Text("Stop", White);
                SoftFontSize(stopText, 13 * s);
                var stop = new Border
                {
                    Child = stopText, Background = Argb(255, 120, 40, 40), CornerRadius = new CornerRadius(3 * s),
                    Padding = new Thickness(8 * s, 1 * s, 8 * s, 2 * s), Margin = new Thickness(8 * s, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                stop.PointerPressed += (_, e) => { e.Handled = true; commands.Enqueue(new MenuCommand(stopAction)); };
                var chip = new Border
                {
                    Background = Argb(255, 14, 34, 48), BorderBrush = Brush(r.Colour), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4 * s), Padding = new Thickness(10 * s, 3 * s, 4 * s, 3 * s), Margin = new Thickness(0, 2 * s, 8 * s, 2 * s),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, stop } },
                };
                string text = r.Text;
                chip.PointerEntered += (_, __) => { if (description != null) description.Text = $"{text} is running. Stop ends it from here, without going to its tab."; };
                statusStrip.Children.Add(chip);
            }
        }

        /// <summary>The playhead: a white line across the timeline at the time the cinematic has reached.</summary>
        private void MoveCinePlayhead(HudModel.ControlsPanel p)
        {
            if (cinePlayhead == null || cineBar == null) return;
            double w = cineBar.Bounds.Width;
            double at = p.CineTime >= 0 ? p.CineTime : Math.Min(cineCue, cineTotal);
            bool show = cineTotal > 0 && w > 1;
            cinePlayhead.IsVisible = show;
            if (!show) return;
            if (cinePlayheadSpan != null && Math.Abs(cinePlayhead.Height - cinePlayheadSpan.Bounds.Height) > 0.5) cinePlayhead.Height = cinePlayheadSpan.Bounds.Height;
            Canvas.SetLeft(cinePlayhead, Math.Clamp(w * at / cineTotal - cinePlayhead.Width / 2, 0, Math.Max(0, w - cinePlayhead.Width)));
            if (cinePlayhead.Child is Control headLine) headLine.Opacity = p.CineTime >= 0 ? 1 : 0.75;
        }

        private void FillCine(HudModel.ControlsPanel p)
        {
            double s = scale;
            if (cineRows == null) return;
            cineRows.Children.Clear();
            cineBar = null; cinePlayhead = null; cinePlayheadSpan = null;
            void Send(MenuAction action, int row = -1, double value = 0, string text = null) => commands.Enqueue(new MenuCommand(action, row, value, text));
            void Line() => cineRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            TextBlock Small(string text, IBrush brush, double size = 15)
            {
                var t = Text(text, brush);
                t.VerticalAlignment = VerticalAlignment.Center;
                SoftFontSize(t, size * s);
                return t;
            }
            void Note(string text)
            {
                var t = Small(text, Grey, 16);
                t.TextWrapping = TextWrapping.Wrap;
                t.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
                cineRows.Children.Add(t);
            }
            WrapPanel Buttons(params Control[] items)
            {
                var w = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var item in items) if (item != null) w.Children.Add(item);
                return w;
            }

            cineRows.Children.Add(Section("Orbit camera"));
            cineRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            cineRows.Children.Add(OptionRow("Spectator orbit camera" + (string.IsNullOrEmpty(p.OrbitKey) ? "" : "  (" + p.OrbitKey + ")"),
                "Closes the menu and switches to the spectator camera, circling the grid you were looking at (or your own ship). Mouse: swing round it. W / S: closer / further. Ctrl + mouse wheel: auto rotate faster / slower. Left click another grid to move to it; right click lets go so you can fly free and pick again. "
                + (string.IsNullOrEmpty(p.OrbitLockKey) ? "" : p.OrbitLockKey + ": the view keeps its angle / turns with the ship. ") + "Esc or the button again: back to what you were doing. Settings > Orbit camera has smooth follow and auto rotate.",
                Widen(ValueButton(p.OrbitOpen ? "Close" : "Open", p.OrbitOpen ? Brush(HudColor.Purple) : White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.OrbitCamera))))));

            cineRows.Children.Add(Section("Cinematics"));
            Line();
            if (cineNameBox.Parent is Panel oldHome) oldHome.Children.Remove(cineNameBox);
            // One slim toolbar (name, New, Rename, the Showcase example, the folder), then the cinematics as a row of
            // chips to pick from: the one being edited lit up.
            var cineToolbar = Buttons(cineNameBox,
                SmallButton("New", White, () => Send(MenuAction.CineNew, text: cineNameBox.Text)),
                p.CineCurrent != null ? SmallButton("Rename", White, () => Send(MenuAction.CineRename, text: cineNameBox.Text)) : null,
                SmallButton("+ Showcase example", Brush(HudColor.Cyan), () => Send(MenuAction.CineShowcase)),
                SmallButton("Open folder", Grey, () => Send(MenuAction.CineOpenFolder)));
            cineToolbar.Margin = new Thickness(24 * s, 4 * s, 24 * s, 6 * s);
            SmallTip(cineToolbar, "Type a name and press New (or Rename for the one being edited). + Showcase example makes a ready-made cinematic fitted to the ship you're in: an orbit reveal, a chase with a lens push, a wing camera, head-on, from above and a wide orbit, with fades and film bars. Each cinematic is a file in the folder: copy it to share.");
            cineRows.Children.Add(cineToolbar);
            if (p.Cinematics.Count > 0)
            {
                var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24 * s, 0, 24 * s, 8 * s) };
                var chipsLabel = Small("Edit:", Grey, 14);
                chipsLabel.VerticalAlignment = VerticalAlignment.Center;
                chipsLabel.Margin = new Thickness(0, 0, 8 * s, 0);
                chips.Children.Add(chipsLabel);
                foreach (string listed in p.Cinematics)
                {
                    string name = listed;
                    bool current = name == p.CineCurrent;
                    var chip = ValueButton(name, current ? Brush(HudColor.Purple) : White, 0, () => { if (current) return; cineDeleteArmed = false; cineSelected = -1; Send(MenuAction.CineSelect, text: name); });
                    chip.Margin = new Thickness(0, 2 * s, 6 * s, 2 * s);
                    chip.Padding = new Thickness(12 * s, 2 * s, 12 * s, 2 * s);
                    if (current) { chip.BorderBrush = Brush(HudColor.Purple); chip.BorderThickness = new Thickness(2); }
                    SmallTip(chip, current ? $"{name}: being edited below" : $"Edit {name}");
                    chip.VerticalAlignment = VerticalAlignment.Center;
                    chips.Children.Add(chip);
                }
                cineRows.Children.Add(chips);
            }
            if (p.CineCurrent == null)
            {
                Note("A cinematic is a timeline of camera shots played one after another: orbit a ship, chase it, a fixed camera, or a camera that glides from one place to another. Make one to start.");
                return;
            }

            int count = p.CineShots.Count;
            if (cineSelected >= count) cineSelected = count - 1;
            double[] secs = p.CineShots.Select(x => Math.Max(0.5, x.Seconds)).ToArray();
            double total = secs.Sum();
            // The timeline's own length (Timeline length on the toolbar; 0 = as long as the shots). The bar shows
            // whichever is longer; past a set length nothing plays.
            double lengthSet = Math.Max(0, p.CineLength);
            double Span() => Math.Max(secs.Sum(), lengthSet);
            cineTotal = Span();
            ColumnDefinition fillerColumn = null;

            cineRows.Children.Add(Section(p.CineCurrent, guideKey: "Cinematics", help: "The cinematic being edited: its options, then its timeline. Play, Loop, Undo and Redo are on the timeline's toolbar."));
            Line();
            cineRows.Children.Add(OptionRow("Options",
                "Play, Loop, Undo and Redo are on the timeline's toolbar below.",
                Buttons(
                    SmallButton("Any ship: " + (p.CineFollow ? "on" : "off"), p.CineFollow ? Brush(HudColor.Green) : Grey, () => Send(MenuAction.CineSet, text: "Follow")),
                    SmallButton("In race spotlight: " + (p.CineSpotlight ? "on" : "off"), p.CineSpotlight ? Brush(HudColor.Green) : Grey, () => Send(MenuAction.CineSet, text: "Spotlight")),
                    SmallButton("Debug timeline: " + (p.CineDebugOn ? "on" : "off"), p.CineDebugOn ? Brush(HudColor.Yellow) : Grey, () => Send(MenuAction.CineSet, text: "Debug")),
                    SmallButton(cineDeleteArmed ? "Click again to delete" : "Delete cinematic", cineDeleteArmed ? Brush(HudColor.Red) : Grey, () =>
                    {
                        if (cineDeleteArmed) { cineDeleteArmed = false; Send(MenuAction.CineDelete); }
                        else { cineDeleteArmed = true; FillCine(p); }
                    }))));
            var playRow = cineRows.Children[cineRows.Children.Count - 1] as Control;
            if (playRow != null)
                playRow.PointerEntered += (_, __) => description.Text =
                    "Any ship: every shot films the ship picked when it's played, so a cinematic made on one ship works on any other. Off: each shot looks for the ship it was made on, by name.\n\n"
                    + "In race spotlight: this cinematic plays on the ship you pick in race spotlight (Race tab) while the panels show.\n\n"
                    + "Debug timeline: while a cinematic plays, its timeline is drawn at the bottom of the screen (shots, effects, where it has got to, what is playing) and the race HUD is hidden. Turn it off to record.";

            // ---- auto switch: which cinematic starts when the filmed ship flies through each gate of this course
            // (Built here, then moved to below the timeline and the inspector.)
            int autoFrom = cineRows.Children.Count;
            cineRows.Children.Add(Section("Auto switch at gates"));
            Line();
            cineRows.Children.Add(OptionRow("Switch cinematics at the gates",
                "On: whenever the ship being filmed (by a playing cinematic, or the orbit camera) flies through a gate of this course, the cinematic you set for that gate starts on it. "
                + "Set them below: click a gate's drop-down and pick a cinematic (or next cinematic = whichever comes after the one playing). They're kept per course.",
                Widen(ValueButton(p.CineAutoOn ? "On" : "Off", p.CineAutoOn ? Brush(HudColor.Green) : Grey, 0, () => Send(MenuAction.CineSet, text: "Auto")))));
            if (p.CineAutoOn)
            {
                if (p.CineGates.Count == 0) Note("Load or make a course first: each of its gates can start a cinematic.");
                var choices = new List<string> { "", "*next*" };
                choices.AddRange(p.Cinematics);
                string Shown(string c) => string.IsNullOrEmpty(c) ? "nothing" : c == "*next*" ? "next cinematic" : c;
                // One framed list, a row per gate: its name, then a drop-down field. Clicking the field opens its list
                // of choices right under it (in the page, not a pop-up); picking one sets it and closes the list.
                // Every colour is solid: see-through ones are drawn grey by the game's UI.
                // (No highlight on the whole row under the mouse: the game's UI fades backgrounds, so rows flickered.)
                IBrush cardBack = Argb(255, 7, 20, 30), divider = Argb(255, 34, 52, 66);
                IBrush fieldBack = Argb(255, 14, 34, 48), fieldEdge = Argb(255, 60, 82, 98), optLit = Argb(255, 24, 58, 80);
                double fieldWidth = 240 * s;
                var gateList = new StackPanel();
                foreach (var g in p.CineGates)
                {
                    var gate = g;
                    bool open = cineGateOpen == gate.Gate;
                    bool set = !string.IsNullOrEmpty(gate.Cinematic);

                    // (Gates with nothing set are quieter, so the ones that do something stand out.)
                    var label = Text(gate.Label, set ? White : Argb(255, 150, 164, 176));
                    label.VerticalAlignment = VerticalAlignment.Center;
                    SoftFontSize(label, 14 * s);

                    var value = Text(Shown(gate.Cinematic), set ? Brush(HudColor.Purple) : Argb(255, 120, 135, 150));
                    value.VerticalAlignment = VerticalAlignment.Center;
                    value.TextTrimming = TextTrimming.CharacterEllipsis;
                    SoftFontSize(value, 13 * s);
                    var chevron = new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse(open ? "M 0,7 L 6,1 L 12,7" : "M 0,1 L 6,7 L 12,1"),
                        Stroke = open ? Brush(HudColor.Yellow) : Argb(255, 150, 164, 176), StrokeThickness = 1.6 * s,
                        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                        Width = 10 * s, Height = 6 * s, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                    };
                    var fieldGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    Grid.SetColumn(chevron, 1);
                    fieldGrid.Children.Add(value);
                    fieldGrid.Children.Add(chevron);
                    var field = new Border
                    {
                        Width = fieldWidth, Height = 26 * s, Padding = new Thickness(9 * s, 0, 9 * s, 0),
                        Background = fieldBack, BorderBrush = open ? Brush(HudColor.Yellow) : fieldEdge, BorderThickness = new Thickness(1),
                        CornerRadius = open ? new CornerRadius(3 * s, 3 * s, 0, 0) : new CornerRadius(3 * s),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                        Child = fieldGrid, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    field.PointerEntered += (_, __) => { if (!open) field.BorderBrush = White; };
                    field.PointerExited += (_, __) => { if (!open) field.BorderBrush = fieldEdge; };
                    field.PointerPressed += (_, e) =>
                    {
                        e.Handled = true;
                        cineGateOpen = open ? (int?)null : gate.Gate;
                        FillCine(p);
                    };

                    var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), MinHeight = 36 * s };
                    Grid.SetColumn(field, 2);
                    head.Children.Add(label);
                    head.Children.Add(field);
                    {
                        // When it starts: at the gate, a few seconds before (on the approach), or a few seconds after.
                        // (Shown for every gate, so it can be set before or after picking the cinematic.)
                        double off = gate.Offset;
                        // One slim switch, three parts: Before | At | After (the lit one is how it's set).
                        IBrush segOff = Argb(255, 14, 34, 48), segOn = Argb(255, 30, 78, 104), segLit = Argb(255, 20, 50, 68);
                        var segs = new StackPanel { Orientation = Orientation.Horizontal };
                        foreach (var (name, sign) in new[] { ("Before", -1), ("At", 0), ("After", 1) })
                        {
                            bool on = Math.Sign(off) == sign;
                            int pick = sign;
                            var t = Text(name, on ? White : Argb(255, 130, 146, 160));
                            SoftFontSize(t, 12 * s);
                            t.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
                            t.VerticalAlignment = VerticalAlignment.Center;
                            var seg = new Border
                            {
                                Child = t, Background = on ? segOn : segOff, Padding = new Thickness(9 * s, 0, 9 * s, 0), Height = 24 * s,
                                BorderBrush = fieldEdge, BorderThickness = new Thickness(sign == -1 ? 0 : 1, 0, 0, 0),
                                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                            };
                            if (!on)
                            {
                                seg.PointerEntered += (_, __) => seg.Background = segLit;
                                seg.PointerExited += (_, __) => seg.Background = segOff;
                                seg.PointerPressed += (_, e) =>
                                {
                                    e.Handled = true;
                                    // (Keeps the seconds already typed when switching between before and after.)
                                    double secs = off != 0 ? Math.Abs(off) : 2;
                                    Send(MenuAction.CineTriggerTime, gate.Gate, pick * secs);
                                };
                            }
                            segs.Children.Add(seg);
                        }
                        Control when = new Border
                        {
                            Child = segs, BorderBrush = fieldEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3 * s),
                            ClipToBounds = true, Background = segOff,
                        };
                        var whenRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8 * s, 0, 10 * s, 0) };
                        if (off != 0)
                        {
                            var whenSecs = CommitBox(Math.Abs(off), 52, v => Send(MenuAction.CineTriggerTime, gate.Gate, Math.Max(0.1, Math.Abs(v)) * (off < 0 ? -1 : 1)));
                            whenSecs.VerticalAlignment = VerticalAlignment.Center;
                            var whenUnit = Text("s", Grey);
                            SoftFontSize(whenUnit, 14 * s);
                            whenUnit.VerticalAlignment = VerticalAlignment.Center;
                            whenUnit.Margin = new Thickness(4 * s, 0, 6 * s, 0);
                            whenRow.Children.Add(whenSecs);
                            whenRow.Children.Add(whenUnit);
                        }
                        when.VerticalAlignment = VerticalAlignment.Center;
                        whenRow.Children.Add(when);
                        whenRow.PointerEntered += (_, __) => description.Text =
                            "When the cinematic starts. Click Before, At or After: at the gate (as the ship flies through it), before (that many seconds before the ship gets there, "
                            + "so the cut happens on the approach) or after (that many seconds after it's through, e.g. to follow it away from a checkpoint). Type the seconds and press Enter.";
                        Grid.SetColumn(whenRow, 1);
                        head.Children.Add(whenRow);
                    }
                    var inRow = new StackPanel { Children = { head } };

                    if (open)
                    {
                        // The choices, hanging straight off the bottom of the field.
                        var options = new StackPanel();
                        var optionRows = new List<Border>();
                        foreach (string choice in choices)
                        {
                            string pickValue = choice;
                            bool current = (gate.Cinematic ?? "") == pickValue;
                            var text = Text(Shown(pickValue), current ? Brush(HudColor.Yellow) : White);
                            text.TextTrimming = TextTrimming.CharacterEllipsis;
                            SoftFontSize(text, 13 * s);
                            var opt = new Border
                            {
                                Background = fieldBack, Padding = new Thickness(9 * s, 4 * s, 9 * s, 4 * s), Child = text,
                                BorderBrush = current ? Brush(HudColor.Yellow) : fieldBack, BorderThickness = new Thickness(3 * s, 0, 0, 0),
                                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                            };
                            optionRows.Add(opt);
                            // (Hover: only one lit at a time, since the game doesn't always say when the mouse leaves.)
                            opt.PointerEntered += (_, __) => { foreach (var o in optionRows) o.Background = fieldBack; opt.Background = optLit; };
                            opt.PointerExited += (_, __) => opt.Background = fieldBack;
                            opt.PointerPressed += (_, e) =>
                            {
                                e.Handled = true;
                                cineGateOpen = null;
                                Send(MenuAction.CineTriggerSet, gate.Gate, 0, pickValue);
                            };
                            options.Children.Add(opt);
                        }
                        inRow.Children.Add(new Border
                        {
                            Width = fieldWidth, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -5 * s, 0, 8 * s),
                            Background = fieldBack, BorderBrush = Brush(HudColor.Yellow), BorderThickness = new Thickness(1, 0, 1, 1),
                            CornerRadius = new CornerRadius(0, 0, 4 * s, 4 * s), Padding = new Thickness(0, 2 * s, 0, 4 * s),
                            Child = options, ClipToBounds = true,
                        });
                    }

                    var row = new Border
                    {
                        Background = cardBack, Child = inRow, Padding = new Thickness(11 * s, 0, 8 * s, 0),
                        BorderBrush = divider, BorderThickness = new Thickness(0, gateList.Children.Count == 0 ? 0 : 1, 0, 0),
                    };
                    // A purple bar down the left of the gates that start a cinematic.
                    var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    rowGrid.Children.Add(new Border { Width = 3 * s, Background = set ? Brush(HudColor.Purple) : cardBack });
                    Grid.SetColumn(row, 1);
                    rowGrid.Children.Add(row);
                    gateList.Children.Add(rowGrid);
                }
                if (p.CineGates.Count > 0)
                    cineRows.Children.Add(new Border
                    {
                        Background = cardBack, BorderBrush = fieldEdge, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4 * s), Margin = new Thickness(36 * s, 2 * s, 48 * s, 12 * s), MaxWidth = 640 * s,
                        HorizontalAlignment = HorizontalAlignment.Left, Child = gateList, ClipToBounds = true,
                    });
            }

            var autoParts = cineRows.Children.Skip(autoFrom).ToList();
            cineRows.Children.RemoveRange(autoFrom, autoParts.Count);

            // ---- the timeline
            cineRows.Children.Add(Section("Timeline"));
            Line();

            var overlayLayer = new Canvas { IsHitTestVisible = false };
            var marker = new Border { Width = 4 * s, Background = White, IsVisible = false, CornerRadius = new CornerRadius(2 * s) };
            overlayLayer.Children.Add(marker);
            var bar = new Grid { Height = 60 * s, Background = Argb(255, 9, 20, 30), Focusable = true };
            // The playhead: a white line with a tab on top, down through the ruler, the shots and the effects.
            var playhead = new Border
            {
                Width = 12 * s, IsVisible = false, IsHitTestVisible = false,
                Child = new Grid
                {
                    Children =
                    {
                        new Border { Width = 2 * s, Background = White, HorizontalAlignment = HorizontalAlignment.Center },
                        new Border { Width = 12 * s, Height = 12 * s, Background = White, CornerRadius = new CornerRadius(2 * s, 2 * s, 6 * s, 6 * s), VerticalAlignment = VerticalAlignment.Top },
                    },
                },
            };
            cineBar = bar; cinePlayhead = playhead;
            void SizeLines() { marker.Height = bar.Bounds.Height; }
            var blockLayouts = new List<Action>();   // (each shot block's blend wedge, laid out once the bar has a size)

            // Where on the bar a place in the list is, and which place a point on the bar is nearest to.
            double EdgeX(int index)
            {
                double before = 0;
                for (int k = 0; k < index && k < secs.Length; k++) before += secs[k];
                double sum = Span();
                return sum > 0 ? bar.Bounds.Width * before / sum : 0;
            }
            int PlaceAt(double x)
            {
                double sum = Span(), acc = 0, w = bar.Bounds.Width;
                for (int k = 0; k < secs.Length; k++)
                {
                    if (x < w * (acc + secs[k] / 2) / sum) return k;
                    acc += secs[k];
                }
                return secs.Length;
            }
            void ShowMarker(int index)
            {
                SizeLines();
                Canvas.SetLeft(marker, Math.Clamp(EdgeX(index) - 2 * s, 0, Math.Max(0, bar.Bounds.Width - 4 * s)));
                marker.IsVisible = true;
            }
            bool OverBar(Point at) => at.X >= -20 * s && at.X <= bar.Bounds.Width + 20 * s && at.Y >= -40 * s && at.Y <= bar.Bounds.Height + 40 * s;

            // Shot types to drag onto the timeline (a click adds one at the end).
            var palette = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6 * s) };
            var dragLabel = Small("Shots:", Grey, 13);
            dragLabel.Margin = new Thickness(0, 0, 8 * s, 0);
            palette.Children.Add(dragLabel);
            foreach (string kind in new[] { "Orbit", "Chase", "Fixed", "Move", "Path" })
            {
                string type = kind;
                var chipText = Small("+ " + CineName(type), White, 13);
                chipText.FontWeight = FontWeight.Bold;
                var chip = new Border
                {
                    Background = CineColour(type), CornerRadius = new CornerRadius(4 * s), Padding = new Thickness(9 * s, 3 * s, 9 * s, 3 * s),
                    Margin = new Thickness(0, 1 * s, 5 * s, 1 * s), Child = chipText,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                bool chipDown = false;
                chip.PointerPressed += (_, e) => { chipDown = true; e.Pointer.Capture(chip); e.Handled = true; };
                chip.PointerMoved += (_, e) =>
                {
                    if (!chipDown) return;
                    var at = e.GetPosition(bar);
                    if (OverBar(at)) ShowMarker(PlaceAt(at.X)); else marker.IsVisible = false;
                    chip.Opacity = 0.6;
                };
                chip.PointerReleased += (_, e) =>
                {
                    if (!chipDown) return;
                    chipDown = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                    marker.IsVisible = false;
                    chip.Opacity = 1;
                    var at = e.GetPosition(bar);
                    var onChip = e.GetPosition(chip);
                    int place;
                    if (OverBar(at)) place = PlaceAt(at.X);
                    else if (onChip.X >= 0 && onChip.Y >= 0 && onChip.X <= chip.Bounds.Width && onChip.Y <= chip.Bounds.Height) place = secs.Length;   // a plain click
                    else return;
                    cineSelected = place;
                    cineFxSelected = -1;
                    cineFocusBar = true;
                    Send(MenuAction.CineAddShot, place, 0, type);
                };
                chip.PointerEntered += (_, __) => description.Text =
                    (type == "Orbit" ? "Orbit: circles the ship from the camera's view, always aiming at its middle."
                    : type == "Chase" ? "Chase: the camera is fixed to the ship and travels, turns and rolls with it."
                    : type == "Fixed" ? "Fixed camera: stays where it is. It can turn to keep the ship in view."
                    : type == "Path" ? "Camera path: flies through as many keyframes as you like on a smooth curve. Add it, then close the menu, fly to the next view and press Add keyframe; repeat."
                    : "Moving camera: glides from a start view to an end view over the shot's time.")
                    + "\n\nDrag it onto the timeline where you want it (or click to add it at the end). It takes the view the camera has now"
                    + (type == "Orbit" || type == "Chase" ? ", and needs a ship: have the orbit camera on it, or have it in the crosshair." : ".");
                palette.Children.Add(chip);
            }

            // The shots: one block each, as wide as it is long.
            var blockLabels = new TextBlock[count];
            for (int i = 0; i < count; i++)
            {
                int index = i;
                var shot = p.CineShots[i];
                var column = new ColumnDefinition(new GridLength(secs[i], GridUnitType.Star));
                bar.ColumnDefinitions.Add(column);
                bool playing = i == p.CineShotPlaying;
                bool picked = i == cineSelected;
                bool ready = shot.Ready && (shot.Type != "Move" || shot.ReadyEnd);

                var title = Small($"{i + 1}  {CineName(shot.Type)}", White, 15);
                title.FontWeight = FontWeight.Bold;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                string LengthText(double blendShown) => CineTime(secs[index])
                    + (shot.Type == "Path" ? $"   {shot.Keys} key{(shot.Keys == 1 ? "" : "s")}" : "")
                    + (blendShown > 0 ? $"   blend {blendShown:0.#}s" : "")
                    + (ready ? "" : shot.Type == "Path" ? "   needs 2+" : "   view not set");
                var length = Small(LengthText(shot.Blend), ready ? White : Brush(HudColor.Yellow), 14);
                length.TextTrimming = TextTrimming.CharacterEllipsis;
                blockLabels[i] = length;
                var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
                titleLine.Children.Add(ShotIcon(shot.Type, 14));
                titleLine.Children.Add(title);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8 * s, 0, 10 * s, 0), IsHitTestVisible = false };
                words.Children.Add(titleLine);
                words.Children.Add(length);
                // Blend in, drawn on the block: the shaded corner is the time it takes to ease in from the shot before.
                // On the picked shot a dot on its top edge can be dragged to change it.
                double blendNow = Math.Min(shot.Blend, secs[i]), laidX = -1, laidH = -1;
                var wedge = new Avalonia.Controls.Shapes.Polygon { Fill = Argb(70, 255, 255, 255), IsHitTestVisible = false };
                var wedgeEdge = new Avalonia.Controls.Shapes.Polyline { Stroke = Argb(220, 255, 255, 255), StrokeThickness = 1.5 * s, IsHitTestVisible = false };
                var blendLayer = new Canvas { IsHitTestVisible = false };
                blendLayer.Children.Add(wedge);
                blendLayer.Children.Add(wedgeEdge);
                var handleLayer = new Canvas();
                Border blendHandle = null;
                if (picked)
                {
                    blendHandle = new Border
                    {
                        Width = 12 * s, Height = 12 * s, CornerRadius = new CornerRadius(6 * s), Background = White,
                        BorderBrush = Argb(255, 9, 20, 30), BorderThickness = new Thickness(1.5 * s),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                    };
                    Canvas.SetTop(blendHandle, 1 * s);
                    handleLayer.Children.Add(blendHandle);
                }
                Grid inside = null;
                void LayBlend()
                {
                    if (inside == null) return;
                    double bw = inside.Bounds.Width, bh = inside.Bounds.Height;
                    double x = secs[index] > 0 ? bw * Math.Min(blendNow, secs[index]) / secs[index] : 0;
                    if (Math.Abs(x - laidX) < 0.5 && Math.Abs(bh - laidH) < 0.5) return;
                    laidX = x; laidH = bh;
                    wedge.Points = new List<Point> { new Point(0, 0), new Point(x, 0), new Point(0, bh) };
                    wedgeEdge.Points = new List<Point> { new Point(0, bh), new Point(x, 0) };
                    wedge.IsVisible = wedgeEdge.IsVisible = x > 1;
                    if (blendHandle != null) Canvas.SetLeft(blendHandle, Math.Clamp(x - 6 * s, 0, Math.Max(0, bw - 12 * s)));
                }
                blockLayouts.Add(LayBlend);
                if (blendHandle != null)
                {
                    bool blendDown = false;
                    double blendStart = blendNow;
                    blendHandle.PointerPressed += (_, e) => { blendDown = true; blendStart = blendNow; e.Pointer.Capture(blendHandle); e.Handled = true; };
                    blendHandle.PointerMoved += (_, e) =>
                    {
                        if (!blendDown || inside == null || inside.Bounds.Width < 2) return;
                        double x = e.GetPosition(inside).X;
                        blendNow = Math.Clamp(Math.Round(x / inside.Bounds.Width * secs[index] * 10) / 10, 0, Math.Min(30, secs[index]));
                        LayBlend();
                        length.Text = LengthText(blendNow);
                    };
                    blendHandle.PointerReleased += (_, e) =>
                    {
                        if (!blendDown) return;
                        blendDown = false;
                        e.Pointer.Capture(null);
                        e.Handled = true;
                        cineFocusBar = true;
                        if (Math.Abs(blendNow - blendStart) > 1e-6) Send(MenuAction.CineShotSet, index, blendNow, "BlendTo");
                    };
                    blendHandle.PointerEntered += (_, __) => description.Text =
                        "Blend in: drag this dot along the top of the shot. The shaded corner is how long the camera takes to ease in from where the shot before left it (none = a hard cut).";
                }
                // The right edge: drag it to change the shot's length.
                var grip = new Border
                {
                    Width = 10 * s, HorizontalAlignment = HorizontalAlignment.Right, Background = Argb(90, 255, 255, 255),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                };
                inside = new Grid { ClipToBounds = true };
                inside.Children.Add(blendLayer);
                inside.Children.Add(words);
                inside.Children.Add(grip);
                inside.Children.Add(handleLayer);
                var block = new Border
                {
                    Background = ready ? CineColour(shot.Type) : Argb(255, 70, 74, 80),
                    BorderBrush = picked ? White : playing ? Brush(HudColor.Green) : Argb(255, 9, 20, 30),
                    BorderThickness = new Thickness((picked || playing ? 3 : 1) * s),
                    CornerRadius = new CornerRadius(4 * s),
                    Child = inside, ClipToBounds = true,
                };
                Grid.SetColumn(block, i);
                bar.Children.Add(block);

                bool down = false, dragging = false, resizing = false;
                double pressX = 0, startSecs = secs[i], newSecs = secs[i];
                block.PointerPressed += (_, e) =>
                {
                    down = true; dragging = false;
                    resizing = e.GetPosition(block).X > block.Bounds.Width - 12 * s;
                    pressX = e.GetPosition(bar).X;
                    startSecs = newSecs = secs[index];
                    e.Pointer.Capture(block);
                    e.Handled = true;
                };
                block.PointerMoved += (_, e) =>
                {
                    if (!down) return;
                    double x = e.GetPosition(bar).X, w = bar.Bounds.Width;
                    if (Math.Abs(x - pressX) > 6 * s) dragging = true;
                    if (!dragging || w < 10) return;
                    if (resizing)
                    {
                        // The length that puts this shot's right edge under the mouse.
                        double before = 0, after = 0;
                        for (int k = 0; k < secs.Length; k++) { if (k < index) before += secs[k]; else if (k > index) after += secs[k]; }
                        double others = before + after, wanted;
                        double inSet = lengthSet > 0 ? Math.Min(x, w) / w * lengthSet - before : 0;   // (a set length: the bar's scale stays put)
                        if (lengthSet > 0 && inSet > 0 && others + inSet <= lengthSet) wanted = inSet;
                        else if (others <= 0) wanted = startSecs * (1 + 2 * (x - pressX) / w);
                        else
                        {
                            double xx = Math.Min(x, w - 30 * s);
                            wanted = (xx * others - w * before) / (w - xx);
                        }
                        newSecs = Math.Clamp(Math.Round(wanted * 2) / 2, 0.5, 600);   // to the half second
                        secs[index] = newSecs;
                        column.Width = new GridLength(newSecs, GridUnitType.Star);
                        if (fillerColumn != null) fillerColumn.Width = new GridLength(Math.Max(0.001, lengthSet - secs.Sum()), GridUnitType.Star);
                        cineTotal = Span();
                        blockLabels[index].Text = CineTime(newSecs);
                        laidX = -1;   // (the blend corner keeps its seconds, so its share of the block changes)
                    }
                    else ShowMarker(PlaceAt(x));
                };
                block.PointerReleased += (_, e) =>
                {
                    if (!down) return;
                    down = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                    marker.IsVisible = false;
                    cineFocusBar = true;
                    cineFxSelected = -1;
                    if (!dragging)
                    {
                        // A click: pick this shot (Delete removes it; its settings show below; its views show in the world).
                        cineSelected = index;
                        Send(MenuAction.CineFocus, index, -1);
                        FillCine(p);
                        return;
                    }
                    if (resizing)
                    {
                        cineSelected = index;
                        if (Math.Abs(newSecs - startSecs) > 1e-6) Send(MenuAction.CineShotSet, index, newSecs, "SecondsTo"); else FillCine(p);
                        return;
                    }
                    int place = PlaceAt(e.GetPosition(bar).X);
                    int target = place > index ? place - 1 : place;
                    cineSelected = target;
                    if (target != index) Send(MenuAction.CineShotMove, index, target, "to"); else FillCine(p);
                };
                string blockAbout = $"{i + 1}. {CineName(shot.Type)}, {CineTime(secs[i])}. Click to pick it (its settings show below; the Delete key removes it). Drag it along the timeline to move it. Drag its right edge to change how long it lasts.";
                block.PointerEntered += (_, __) => description.Text = blockAbout;
                {
                    // Hover card: the shot at a glance.
                    double tipStart = 0;
                    for (int k = 0; k < i; k++) tipStart += secs[k];
                    string tipStatus = ready ? (shot.Type == "Path" ? $"{shot.Keys} keyframes" : "ready")
                        : shot.Type == "Path" ? $"{shot.Keys} keyframe{(shot.Keys == 1 ? "" : "s")}: needs 2 or more"
                        : !shot.Ready ? "view not set" : "end view not set";
                    SmallTip(block, $"Shot {i + 1}: {CineName(shot.Type)}\n{CineTime(tipStart)} to {CineTime(tipStart + secs[i])}  ({CineTime(secs[i])})"
                        + (shot.Blend > 0 ? $"\nBlends in over {shot.Blend:0.#}s" : "\nHard cut in")
                        + (shot.Type == "Orbit" ? $"\nRotates {shot.Rotate:0.#}°/s" : "")
                        + "\n" + tipStatus);
                }
            }
            if (lengthSet > 0)
            {
                // The rest of a set length that no shot fills yet: the last shot keeps going to the end.
                fillerColumn = new ColumnDefinition(new GridLength(Math.Max(0.001, lengthSet - total), GridUnitType.Star));
                bar.ColumnDefinitions.Add(fillerColumn);
                if (lengthSet > total + 0.05)
                {
                    var fillerText = Small(count == 0 ? "" : "the last shot holds to the end", Argb(255, 110, 125, 140), 13);
                    fillerText.Margin = new Thickness(8 * s, 0, 4 * s, 0);
                    fillerText.TextTrimming = TextTrimming.CharacterEllipsis;
                    var filler = new Border
                    {
                        Background = Argb(255, 14, 30, 42), BorderBrush = Argb(255, 40, 62, 80), BorderThickness = new Thickness(1 * s),
                        CornerRadius = new CornerRadius(4 * s), Child = fillerText, IsHitTestVisible = false,
                    };
                    Grid.SetColumn(filler, bar.ColumnDefinitions.Count - 1);
                    bar.Children.Add(filler);
                }
            }
            if (count == 0)
            {
                var empty = Small("Drag a shot type here (or click one) to add the first shot", Grey, 16);
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.IsHitTestVisible = false;
                bar.Children.Add(empty);
            }
            bar.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.D && (e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0)
                {
                    if (cineSelected < 0 || cineSelected >= secs.Length) return;
                    e.Handled = true;
                    int copied = cineSelected;
                    cineSelected = copied + 1;
                    cineFocusBar = true;
                    Send(MenuAction.CineShotDuplicate, copied);
                    return;
                }
                if (e.Key != Avalonia.Input.Key.Delete && e.Key != Avalonia.Input.Key.Back) return;
                if (cineSelected < 0 || cineSelected >= secs.Length) return;
                e.Handled = true;
                int gone = cineSelected;
                cineSelected = Math.Min(gone, secs.Length - 2);
                cineFocusBar = true;
                Send(MenuAction.CineShotDelete, gone);
            };

            // Above the bar: a time ruler, its marks spaced to suit the zoom. Click or drag on it to move the playhead
            // (Play starts from there).
            double rulerH = 24 * s;
            var ruler = new Canvas
            {
                Height = rulerH, Background = Argb(255, 9, 26, 38), ClipToBounds = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            var rulerHover = new Border { Width = 1 * s, Height = rulerH, Background = Argb(255, 150, 160, 170), IsVisible = false, IsHitTestVisible = false };
            double rulerLaid = -1;
            void LayRuler()
            {
                double w = bar.Bounds.Width;
                if (Math.Abs(w - rulerLaid) < 0.5 || w < 10) return;
                rulerLaid = w;
                ruler.Children.Clear();
                ruler.Children.Add(rulerHover);
                double total = Span();
                if (total <= 0) return;
                double pps = w / total;   // pixels a second
                double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
                bool Multiple(double big, double small) { double r = big / small; return Math.Abs(r - Math.Round(r)) < 1e-6; }
                double major = steps.FirstOrDefault(x => x * pps >= 64 * s);
                if (major <= 0) major = 600;
                double minor = steps.Where(x => x < major && Multiple(major, x) && x * pps >= 7 * s).DefaultIfEmpty(major).Min();
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                var minorPath = new StringBuilder();
                var majorPath = new StringBuilder();
                int minorN = 0, majorN = 0;
                void Flush(StringBuilder path, IBrush brush)
                {
                    if (path.Length == 0) return;
                    ruler.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(path.ToString()), Stroke = brush, StrokeThickness = 1 * s, IsHitTestVisible = false });
                    path.Clear();
                }
                for (int k = 0; k <= 4000; k++)
                {
                    double t = k * minor;
                    if (t > total + 1e-6) break;
                    double x = Math.Min(t * pps, w - 1);
                    if (Multiple(t, major))
                    {
                        majorPath.Append(string.Format(culture, "M {0:0.##},{1:0.##} L {0:0.##},{2:0.##} ", x, rulerH - 12 * s, rulerH));
                        if (++majorN % 30 == 0) Flush(majorPath, Argb(255, 150, 165, 180));
                        if (x < w - 34 * s)
                        {
                            var mark = Small(CineTime(t), Argb(255, 150, 165, 180), 12);
                            mark.IsHitTestVisible = false;
                            Canvas.SetLeft(mark, x + 3 * s);
                            Canvas.SetTop(mark, 0);
                            ruler.Children.Add(mark);
                        }
                    }
                    else
                    {
                        minorPath.Append(string.Format(culture, "M {0:0.##},{1:0.##} L {0:0.##},{2:0.##} ", x, rulerH - 5 * s, rulerH));
                        if (++minorN % 30 == 0) Flush(minorPath, Argb(255, 70, 90, 108));
                    }
                }
                Flush(minorPath, Argb(255, 70, 90, 108));
                Flush(majorPath, Argb(255, 150, 165, 180));
                // The whole length, at the right-hand end.
                var whole = Small(CineTime(total), White, 12);
                whole.FontWeight = FontWeight.Bold;
                whole.IsHitTestVisible = false;
                Canvas.SetRight(whole, 3 * s);
                Canvas.SetTop(whole, 0);
                ruler.Children.Add(whole);
            }
            double RulerTime(double x) => bar.Bounds.Width > 1 ? Math.Clamp(Math.Round(x / bar.Bounds.Width * Span() * 10) / 10, 0, Span()) : 0;
            bool rulerDown = false;
            ruler.PointerPressed += (_, e) =>
            {
                if (Span() <= 0) return;
                rulerDown = true;
                e.Pointer.Capture(ruler);
                e.Handled = true;
                cineCue = RulerTime(e.GetPosition(ruler).X);
                MoveCinePlayhead(p);
            };
            ruler.PointerMoved += (_, e) =>
            {
                double x = e.GetPosition(ruler).X;
                if (rulerDown) { cineCue = RulerTime(x); MoveCinePlayhead(p); }
                rulerHover.IsVisible = Span() > 0;
                Canvas.SetLeft(rulerHover, Math.Max(0, x));
                description.Text = $"{CineTime(RulerTime(x))}: click (or drag) to put the playhead here. Play then starts from the playhead.";
            };
            ruler.PointerReleased += (_, e) =>
            {
                if (!rulerDown) return;
                rulerDown = false;
                e.Pointer.Capture(null);
                FillCine(p);   // (the Play button says where it starts from)
            };
            ruler.PointerExited += (_, __) => rulerHover.IsVisible = false;

            var barHolder = new Grid();
            barHolder.Children.Add(bar);
            barHolder.Children.Add(overlayLayer);
            // ---- the effects layer: changes laid over the shots at their own times (field of view, tilt, shake)
            int fxCount = p.CineEffects.Count;
            if (cineFxSelected >= fxCount) cineFxSelected = fxCount - 1;
            // All the effect layers are one canvas, a stripe each, so a block can be dragged from one layer to another.
            int lanes = Math.Max(1, p.CineLanes);
            double laneH = 36 * s;
            var fxTrack = new Canvas { Height = lanes * laneH, Background = Argb(255, 14, 34, 50), ClipToBounds = true, Focusable = true, Margin = new Thickness(0, 6 * s, 0, 0) };
            var laneStripes = new Border[lanes];
            for (int l = 0; l < lanes; l++)
            {
                var stripe = new Border
                {
                    Height = laneH, Background = l % 2 == 0 ? Argb(255, 14, 34, 50) : Argb(255, 18, 42, 60),
                    BorderBrush = Argb(255, 30, 60, 84), BorderThickness = new Thickness(0, l == 0 ? 0 : 1 * s, 0, 0), IsHitTestVisible = false,
                };
                Canvas.SetTop(stripe, l * laneH);
                laneStripes[l] = stripe;
                fxTrack.Children.Add(stripe);
                if (lanes > 1)
                {
                    var laneName = Small($"Layer {l + 1}", Argb(120, 185, 190, 195), 12);
                    laneName.IsHitTestVisible = false;
                    Canvas.SetTop(laneName, l * laneH + 10 * s);
                    fxTrack.Children.Add(laneName);   // (placed at the right end in LayEffects)
                    laneStripes[l].Tag = laneName;
                }
            }
            int LaneAt(double y) => Math.Clamp((int)Math.Floor(y / laneH), 0, lanes - 1);
            double[] fxStart = p.CineEffects.Select(x => Math.Max(0, x.Start)).ToArray();
            double[] fxSecs = p.CineEffects.Select(x => Math.Max(0.5, x.Seconds)).ToArray();
            int[] fxLane = p.CineEffects.Select(x => Math.Clamp(x.Lane, 0, lanes - 1)).ToArray();
            var fxBlocks = new Border[fxCount];
            void LayEffects()
            {
                double barWidth = bar.Bounds.Width, sum = Span();
                foreach (var stripe in laneStripes)
                {
                    stripe.Width = Math.Max(1, barWidth);
                    if (stripe.Tag is TextBlock laneName) Canvas.SetLeft(laneName, Math.Max(0, barWidth - 60 * s));
                }
                if (barWidth < 1 || sum <= 0) return;
                for (int k = 0; k < fxCount; k++)
                {
                    Canvas.SetLeft(fxBlocks[k], barWidth * fxStart[k] / sum);
                    Canvas.SetTop(fxBlocks[k], fxLane[k] * laneH + 3 * s);
                    fxBlocks[k].Width = Math.Max(10 * s, barWidth * fxSecs[k] / sum);
                }
            }
            for (int i = 0; i < fxCount; i++)
            {
                int index = i;
                var fx = p.CineEffects[i];
                bool picked = i == cineFxSelected;
                string caption = fx.Type == "Shake" ? $"Shake {fx.To:0.##}°"
                    : fx.Type == "Fade" || fx.Type == "Bars" ? $"{FxName(fx.Type)} {fx.From:0}% > {fx.To:0}%"
                    : fx.Type == "Panel" ? RaceGateController.PanelNames[Math.Clamp((int)Math.Round(fx.To), 0, 15)]
                    : $"{FxName(fx.Type)} {fx.From:0}° > {fx.To:0}°";
                var words = Small(caption, White, 13);
                words.FontWeight = FontWeight.Bold;
                words.TextTrimming = TextTrimming.CharacterEllipsis;
                words.Margin = new Thickness(6 * s, 0, 10 * s, 0);
                words.IsHitTestVisible = false;
                var grip = new Border
                {
                    Width = 8 * s, HorizontalAlignment = HorizontalAlignment.Right, Background = Argb(90, 255, 255, 255),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                };
                var inside = new Grid { ClipToBounds = true };
                inside.Children.Add(words);
                inside.Children.Add(grip);
                var block = new Border
                {
                    Height = 30 * s, Background = FxColour(fx.Type), CornerRadius = new CornerRadius(4 * s), Child = inside, ClipToBounds = true,
                    BorderBrush = picked ? White : Argb(255, 9, 20, 30), BorderThickness = new Thickness((picked ? 3 : 1) * s),
                };
                Canvas.SetTop(block, fxLane[i] * laneH + 3 * s);
                block.ZIndex = picked ? 10 : 1;   // the picked one never hidden
                fxBlocks[i] = block;
                fxTrack.Children.Add(block);

                bool down = false, dragging = false, resizing = false;
                double pressX = 0, pressY = 0, startAt = fxStart[i], startLen = fxSecs[i];
                int startLane = fxLane[i];
                block.PointerPressed += (_, e) =>
                {
                    down = true; dragging = false;
                    resizing = e.GetPosition(block).X > block.Bounds.Width - 10 * s;
                    pressX = e.GetPosition(bar).X;
                    pressY = e.GetPosition(fxTrack).Y;
                    startAt = fxStart[index]; startLen = fxSecs[index]; startLane = fxLane[index];
                    e.Pointer.Capture(block);
                    e.Handled = true;
                };
                block.PointerMoved += (_, e) =>
                {
                    if (!down) return;
                    double x = e.GetPosition(bar).X, y = e.GetPosition(fxTrack).Y, barWidth = bar.Bounds.Width, sum = Span();
                    if (Math.Abs(x - pressX) > 5 * s || Math.Abs(y - pressY) > 8 * s) dragging = true;
                    if (!dragging || barWidth < 10 || sum <= 0) return;
                    double moved = Math.Round((x - pressX) / barWidth * sum * 2) / 2;   // to the half second
                    if (resizing) fxSecs[index] = Math.Clamp(startLen + moved, 0.5, 600);
                    else
                    {
                        fxStart[index] = Math.Max(0, startAt + moved);
                        fxLane[index] = LaneAt(y);   // up or down onto another layer
                    }
                    block.ZIndex = 20;   // over the others while it's being dragged
                    // Red edge while it sits on top of another effect on that layer (it'll be moved to a free layer when let go).
                    bool clash = false;
                    for (int k = 0; k < fxCount; k++)
                        if (k != index && fxLane[k] == fxLane[index] && fxStart[index] < fxStart[k] + fxSecs[k] - 1e-6 && fxStart[k] < fxStart[index] + fxSecs[index] - 1e-6) { clash = true; break; }
                    block.BorderBrush = clash ? Brush(HudColor.Red) : White;
                    block.BorderThickness = new Thickness(3 * s);
                    LayEffects();
                };
                block.PointerReleased += (_, e) =>
                {
                    if (!down) return;
                    down = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                    cineFxSelected = index;
                    cineSelected = -1;
                    cineFocusFx = true;
                    block.ZIndex = 0;
                    bool sent = false;
                    if (dragging && resizing && Math.Abs(fxSecs[index] - startLen) > 1e-6) { Send(MenuAction.CineFxSet, index, fxSecs[index], "Seconds"); sent = true; }
                    if (dragging && !resizing && (Math.Abs(fxStart[index] - startAt) > 1e-6 || fxLane[index] != startLane))
                    {
                        Send(MenuAction.CineFxSet, index, fxStart[index], "Move|" + fxLane[index]);
                        sent = true;
                    }
                    if (!sent) FillCine(p);
                };
                string fxAbout = (fx.Type == "Fov" ? "FOV: the field of view changes from the first value to the second over this time (smaller = zoomed in), then goes back to the game's own when it ends. Same value twice = held."
                    : fx.Type == "Roll" ? "Tilt: the picture leans over from the first angle to the second over this time, then levels out again when it ends."
                    : fx.Type == "Fade" ? "Fade: the picture goes from the first darkness to the second (0% clear, 100% black) over this time, then clears when it ends."
                    : fx.Type == "Bars" ? "Letterbox: cinema bars grow or shrink from the first size to the second (% of the screen each) over this time, then go when it ends."
                    : fx.Type == "Panel" ? "Panel: in race spotlight, shows this showcase page (race results, race stats, race graphs, lap replay, the ship, or none) for this time. Outside Panel effects the pages change by themselves."
                    : "Shake: a hand-held wobble of the camera for this time. The value is how strong, in degrees.")
                    + "\n\nClick to pick it (its values show below; the Delete key removes it). Drag it to move it, or up and down onto another layer; drag its right edge to change how long it lasts.";
                block.PointerEntered += (_, __) => description.Text = fxAbout;
                SmallTip(block, $"{FxName(fx.Type)} effect, layer {fxLane[i] + 1}\n{CineTime(fxStart[i])} to {CineTime(fxStart[i] + fxSecs[i])}  ({CineTime(fxSecs[i])})\n{caption}");
            }
            if (fxCount == 0)
            {
                var fxEmpty = Small("Effects layer: drag FOV, Tilt or Shake here", Grey, 14);   // (on the top layer)
                fxEmpty.IsHitTestVisible = false;
                Canvas.SetLeft(fxEmpty, 10 * s);
                Canvas.SetTop(fxEmpty, 8 * s);
                fxTrack.Children.Add(fxEmpty);
            }
            fxTrack.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Delete && e.Key != Avalonia.Input.Key.Back) return;
                if (cineFxSelected < 0 || cineFxSelected >= fxCount) return;
                e.Handled = true;
                int gone = cineFxSelected;
                cineFxSelected = -1;
                Send(MenuAction.CineFxDelete, gone);
            };

            // Effect types to drag onto the layer (a click puts one at the picked shot's start, or the very start).
            var fxLabel = Small("Effects:", Grey, 13);
            fxLabel.Margin = new Thickness(14 * s, 0, 8 * s, 0);
            palette.Children.Add(fxLabel);
            foreach (string fxKind in new[] { "Fov", "Roll", "Shake", "Fade", "Bars", "Panel" })
            {
                string type = fxKind;
                var chipText = Small("+ " + FxName(type), White, 13);
                chipText.FontWeight = FontWeight.Bold;
                var chip = new Border
                {
                    Background = FxColour(type), CornerRadius = new CornerRadius(4 * s), Padding = new Thickness(9 * s, 3 * s, 9 * s, 3 * s),
                    Margin = new Thickness(0, 1 * s, 5 * s, 1 * s), Child = chipText,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                bool chipDown = false;
                chip.PointerPressed += (_, e) => { chipDown = true; chip.Opacity = 0.6; e.Pointer.Capture(chip); e.Handled = true; };
                chip.PointerReleased += (_, e) =>
                {
                    if (!chipDown) return;
                    chipDown = false;
                    chip.Opacity = 1;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                    var onTrack = e.GetPosition(fxTrack);
                    var onChip = e.GetPosition(chip);
                    double barWidth = bar.Bounds.Width, sum = Span(), startsAt;
                    int lane;
                    if (onTrack.X >= -20 * s && onTrack.X <= barWidth + 20 * s && onTrack.Y >= -50 * s && onTrack.Y <= fxTrack.Bounds.Height + 40 * s)
                    {
                        startsAt = barWidth > 1 ? Math.Clamp(sum * onTrack.X / barWidth, 0, Math.Max(0, sum - 0.5)) : 0;
                        lane = LaneAt(onTrack.Y);
                    }
                    else if (onChip.X >= 0 && onChip.Y >= 0 && onChip.X <= chip.Bounds.Width && onChip.Y <= chip.Bounds.Height)
                    {
                        startsAt = 0;   // a plain click: at the picked shot's start, on the picked effect's layer (or the top one)
                        for (int k = 0; k < cineSelected && k < secs.Length; k++) startsAt += secs[k];
                        lane = cineFxSelected >= 0 && cineFxSelected < fxCount ? fxLane[cineFxSelected] : 0;
                    }
                    else return;
                    cineFxSelected = fxCount;
                    cineSelected = -1;
                    cineFocusFx = true;
                    Send(MenuAction.CineFxAdd, lane, startsAt, type);
                };
                chip.PointerEntered += (_, __) => description.Text =
                    (type == "Fov" ? "FOV: changes the camera's field of view over a stretch of the timeline, e.g. a slow zoom in. Smaller = zoomed in."
                    : type == "Roll" ? "Tilt: leans the picture over (a Dutch angle), from one angle to another."
                    : type == "Fade" ? "Fade: fades the picture to black (or back from black). 0% = clear, 100% = black. Fade in at the start: 100 > 0; fade out at the end: 0 > 100."
                    : type == "Bars" ? "Letterbox: black cinema bars at the top and bottom. The value is each bar's height as a % of the screen (12 is about a film's widescreen look). 0 > 12 slides them in."
                    : type == "Panel" ? "Panel: switches the race spotlight's showcase panel to a page (race results, race stats, the ship, or hidden) at this time. Only does anything when the cinematic plays in race spotlight."
                    : "Shake: a hand-held wobble of the camera.")
                    + "\n\nDrag it onto an effects layer under the timeline, at the time it should start (or click to put it at the picked shot's start). Effects run over the shots, whatever shot is playing. Add more layers with + Layer to run effects at the same time.";
                palette.Children.Add(chip);
            }

            TextBlock Label(string text, IBrush brush, double left = 14)
            {
                var t = Small(text, brush, 15);
                t.Margin = new Thickness(left * s, 0, 6 * s, 0);
                return t;
            }
            Button Tiny(string text, IBrush brush, Action click)
            {
                var b = SmallButton(text, brush, click);
                b.MinHeight = 34 * s;
                b.Margin = new Thickness(0, 2 * s, 4 * s, 2 * s);
                return b;
            }

            // Everything about the timeline sits on one dark panel: shot types, the bar, its times, and the picked shot.
            var timeline = new StackPanel();

            // The toolbar: play / stop (from the playhead), loop, the whole length, undo / redo and the zoom.
            bool fromCue = cineCue > 0.05 && cineCue < Span();
            var zoomLabel = Small($"Zoom {cineZoomLevel:0.#}x", Grey, 13);
            zoomLabel.Margin = new Thickness(0, 0, 10 * s, 0);   // (room before the Fit button)
            double playsFor = lengthSet > 0 ? lengthSet : total;
            var totalText = Small(count == 0 ? "No shots yet" : $"{count} shot{(count == 1 ? "" : "s")}, {CineTime(playsFor)}", White, 15);
            totalText.FontWeight = FontWeight.Bold;
            totalText.Margin = new Thickness(4 * s, 0, 4 * s, 0);
            var toolbar = Buttons(
                Tiny(p.CinePlaying ? "Stop" : fromCue ? $"Play from {CineTime(cineCue)}" : "Play",
                    p.CinePlaying ? Brush(HudColor.Purple) : count > 0 ? Brush(HudColor.Green) : Grey, () =>
                    {
                        if (p.CinePlaying) Send(MenuAction.CineStop);
                        else if (fromCue) Send(MenuAction.CinePlay, -1, cineCue, "From");
                        else Send(MenuAction.CinePlay);
                    }),
                fromCue && !p.CinePlaying ? Tiny("From start", White, () => { cineCue = 0; FillCine(p); }) : null,
                Tiny("Loop: " + (p.CineLoop ? "on" : "off"), p.CineLoop ? Brush(HudColor.Green) : Grey, () => Send(MenuAction.CineSet, text: "Loop")),
                new Border { Width = 12 * s },
                totalText,
                Small(p.CineLoop ? "then loops" : "plays once", p.CineLoop ? Brush(HudColor.Green) : Grey, 13),
                new Border { Width = 18 * s },
                Small("Timeline length", Grey, 13),
                new Border { Width = 6 * s },
                CommitBox(lengthSet, 70, v => Send(MenuAction.CineSet, -1, Math.Max(0, v), "Length"), "fit"),
                Small("s", Grey, 13),
                lengthSet > 0 && total > lengthSet + 0.05 ? Small($"   shots past {CineTime(lengthSet)} are cut", Brush(HudColor.Yellow), 13) : null,
                new Border { Width = 18 * s },
                Tiny(p.CineUndos > 0 ? $"Undo ({p.CineUndos})" : "Undo", p.CineUndos > 0 ? White : Grey, () => { if (p.CineUndos > 0) Send(MenuAction.CineUndo); }),
                Tiny(p.CineRedos > 0 ? $"Redo ({p.CineRedos})" : "Redo", p.CineRedos > 0 ? White : Grey, () => { if (p.CineRedos > 0) Send(MenuAction.CineRedo); }),
                new Border { Width = 18 * s },
                zoomLabel);
            toolbar.Margin = new Thickness(0, 0, 0, 6 * s);
            foreach (var child in toolbar.Children) if (child is Layoutable placedTool) placedTool.VerticalAlignment = VerticalAlignment.Center;
            toolbar.PointerEntered += (_, __) => description.Text =
                "Play: closes the menu and runs the shots on the ship you're looking at (or the orbit camera's, or your own), from the playhead. Esc stops it. Click the time ruler to move the playhead; From start puts it back at the beginning.\n\n"
                + "Loop: start again after the last shot.\n\nUndo / Redo: step back and forward through your edits to this cinematic (Ctrl + Z / Ctrl + Y with the timeline picked). Kept until the game closes.\n\n"
                + "Zoom: Ctrl + mouse wheel over the timeline zooms in on the mouse; Shift + wheel scrolls along. Fit shows it all again.\n\n"
                + "Timeline length: how long the whole cinematic runs, in seconds (blank = as long as its shots). The timeline is drawn that long, so there's room to lay out effects and shots. "
                + "If the shots end sooner the last one keeps going to the end (an orbit keeps circling, a fixed camera holds); anything past the end is cut, marked by the red line. Loop starts again at the end.";

            // Ruler, shots and effects zoom and scroll together; the playhead runs down through all three.
            var span = new StackPanel();
            span.Children.Add(ruler);
            span.Children.Add(barHolder);
            span.Children.Add(fxTrack);
            var headLayer = new Canvas { IsHitTestVisible = false };
            var endLine = new Border { Width = 2 * s, Background = Brush(HudColor.Red), IsVisible = false };
            headLayer.Children.Add(endLine);
            headLayer.Children.Add(playhead);
            void LayEnd()
            {
                // Only drawn when shots run past a set length (otherwise the end is simply the bar's end).
                bool show = lengthSet > 0 && secs.Sum() > lengthSet + 0.05 && bar.Bounds.Width > 1;
                endLine.IsVisible = show;
                if (!show) return;
                double h = cinePlayheadSpan?.Bounds.Height ?? 0;
                if (Math.Abs(endLine.Height - h) > 0.5) endLine.Height = h;
                Canvas.SetLeft(endLine, bar.Bounds.Width * lengthSet / Span() - 1 * s);
            }
            var zoomed = new Grid();
            zoomed.Children.Add(span);
            zoomed.Children.Add(headLayer);
            cinePlayheadSpan = span;
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = zoomed,
            };
            bool scrollRestored = cineZoomLevel <= 1;
            void ApplyZoom()
            {
                double vw = scroller.Viewport.Width > 10 ? scroller.Viewport.Width : scroller.Bounds.Width;
                if (vw < 10) return;
                double want = Math.Round(vw * Math.Max(1, cineZoomLevel));
                if (double.IsNaN(zoomed.Width) || Math.Abs(zoomed.Width - want) > 1) zoomed.Width = want;
                if (!scrollRestored)
                {
                    scrollRestored = true;
                    double back = cineScrollX;
                    Dispatcher.UIThread.Post(() => { try { scroller.Offset = new Vector(back, 0); } catch { /* the menu closed */ } }, DispatcherPriority.Background);
                }
            }
            void ZoomTo(double level, double anchorInZoomed, double anchorInView)
            {
                double oldW = zoomed.Bounds.Width;
                double part = oldW > 1 ? anchorInZoomed / oldW : 0;
                cineZoomLevel = Math.Clamp(level, 1, 40);
                if (cineZoomLevel < 1.05) cineZoomLevel = 1;
                ApplyZoom();
                zoomLabel.Text = $"Zoom {cineZoomLevel:0.#}x";
                double newW = double.IsNaN(zoomed.Width) ? oldW : zoomed.Width;
                Dispatcher.UIThread.Post(() =>
                {
                    try { scroller.Offset = new Vector(Math.Max(0, part * newW - anchorInView), 0); cineScrollX = scroller.Offset.X; }
                    catch { /* the menu closed */ }
                }, DispatcherPriority.Background);
            }
            toolbar.Children.Add(Tiny("Fit", cineZoomLevel > 1 ? White : Grey, () => ZoomTo(1, 0, 0)));
            scroller.LayoutUpdated += (_, __) => ApplyZoom();
            scroller.ScrollChanged += (_, __) => { if (scrollRestored) cineScrollX = scroller.Offset.X; };
            scroller.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, new EventHandler<Avalonia.Input.PointerWheelEventArgs>((_, e) =>
            {
                bool ctrl = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0, shift = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0;
                if (!ctrl && !shift) return;   // (a plain wheel scrolls the page as usual)
                e.Handled = true;
                double turn = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
                if (ctrl) ZoomTo(cineZoomLevel * (turn > 0 ? 1.25 : 0.8), e.GetPosition(zoomed).X, e.GetPosition(scroller).X);
                else
                {
                    scroller.Offset = new Vector(Math.Max(0, scroller.Offset.X - turn * 80 * s), 0);
                    cineScrollX = scroller.Offset.X;
                }
            }), Avalonia.Interactivity.RoutingStrategies.Tunnel);

            timeline.Children.Add(toolbar);
            timeline.Children.Add(palette);
            timeline.Children.Add(scroller);
            // Effect layers: add one, or take the bottom one away (its effects move up a layer).
            var laneButtons = Buttons(
                Small(lanes == 1 ? "1 effect layer" : $"{lanes} effect layers", Grey, 13),
                new Border { Width = 10 * s },
                Tiny("+ Layer", lanes < 8 ? White : Grey, () => Send(MenuAction.CineSet, text: "AddLane")),
                lanes > 1 ? Tiny("- Layer", White, () => Send(MenuAction.CineSet, text: "RemoveLane")) : null);
            laneButtons.Margin = new Thickness(0, 4 * s, 0, 0);
            foreach (var child in laneButtons.Children) if (child is Layoutable placed) placed.VerticalAlignment = VerticalAlignment.Center;
            laneButtons.PointerEntered += (_, __) => description.Text =
                "Effect layers: effects on different layers can run at the same time (a zoom while the picture tilts, say). + Layer adds a layer underneath; - Layer takes the bottom one away and moves its effects up onto the one above. "
                + "Drag an effect up or down to move it to another layer. Where two FOV effects overlap, the higher layer's wins; tilts and shakes add together.";
            timeline.Children.Add(laneButtons);
            var hint = Small(count == 0 ? "Close the menu and fly the camera to a view first (the orbit camera at the top of this tab is the easy way): a new shot takes the view the camera has."
                : "Click a shot or an effect to pick it: its settings show below (Delete removes it, Ctrl + D copies a shot). Drag it to move it; drag its right edge to change its length. Click the ruler to move the playhead. Ctrl + wheel zooms.", Grey, 14);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 8 * s, 0, 0);
            timeline.Children.Add(hint);
            var timelineCard = new Border
            {
                Background = Argb(255, 9, 26, 38), BorderBrush = Argb(255, 30, 60, 84), BorderThickness = new Thickness(1 * s),
                CornerRadius = new CornerRadius(6 * s), Padding = new Thickness(16 * s, 12 * s, 16 * s, 12 * s),
                Margin = new Thickness(24 * s, 4 * s, 48 * s, 8 * s), Child = timeline,
            };
            timelineCard.KeyDown += (_, e) =>
            {
                if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) == 0) return;
                bool shift = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0;
                if (e.Key == Avalonia.Input.Key.Z && !shift) { e.Handled = true; cineFocusBar = true; Send(MenuAction.CineUndo); }
                else if (e.Key == Avalonia.Input.Key.Y || (e.Key == Avalonia.Input.Key.Z && shift)) { e.Handled = true; cineFocusBar = true; Send(MenuAction.CineRedo); }
            };
            cineRows.Children.Add(timelineCard);

            // The inspector: everything about the picked shot or effect, on its own panel under the timeline.
            var inspector = new StackPanel();
            var inspectorCard = new Border
            {
                Background = Argb(255, 9, 26, 38), BorderBrush = Argb(255, 30, 60, 84), BorderThickness = new Thickness(1 * s),
                CornerRadius = new CornerRadius(6 * s), Padding = new Thickness(16 * s, 12 * s, 16 * s, 12 * s),
                Margin = new Thickness(24 * s, 0, 48 * s, 12 * s), Child = inspector, IsVisible = false,
            };
            cineRows.Children.Add(inspectorCard);
            cineRows.Children.AddRange(autoParts);
            Grid InspectorRow(string name, params Control[] items)
            {
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 3 * s, 0, 3 * s) };
                var rowName = Small(name, Grey, 14);
                rowName.Width = 100 * s;
                var rowItems = Buttons(items);
                foreach (var child in rowItems.Children) if (child is Layoutable placedItem) placedItem.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(rowItems, 1);
                rowGrid.Children.Add(rowName);
                rowGrid.Children.Add(rowItems);
                return rowGrid;
            }
            Grid InspectorHead(IBrush colour, Control icon, string name, string status, IBrush statusBrush, params Control[] actions)
            {
                var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 6 * s) };
                var swatch = new Border { Width = 6 * s, Height = 36 * s, Background = colour, CornerRadius = new CornerRadius(3 * s), Margin = new Thickness(0, 0, 10 * s, 0), VerticalAlignment = VerticalAlignment.Center };
                var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
                if (icon != null) nameLine.Children.Add(icon);
                var nameText = Small(name, White, 18);
                nameText.FontWeight = FontWeight.Bold;
                nameLine.Children.Add(nameText);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(nameLine);
                if (!string.IsNullOrEmpty(status)) words.Children.Add(Small(status, statusBrush, 14));
                var buttons = Buttons(actions);
                buttons.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(words, 1);
                Grid.SetColumn(buttons, 2);
                head.Children.Add(swatch);
                head.Children.Add(words);
                head.Children.Add(buttons);
                return head;
            }
            bar.LayoutUpdated += (_, __) => { SizeLines(); LayEffects(); LayRuler(); LayEnd(); foreach (var lay in blockLayouts) lay(); };
            if (cineFocusFx)
            {
                cineFocusFx = false;
                cineFocusBar = false;
                Dispatcher.UIThread.Post(() => { try { fxTrack.Focus(); } catch { /* the menu closed */ } }, DispatcherPriority.Background);
            }
            if (cineFocusBar)
            {
                cineFocusBar = false;
                Dispatcher.UIThread.Post(() => { try { bar.Focus(); } catch { /* the menu closed */ } }, DispatcherPriority.Background);
            }

            // ---- the picked effect, in the inspector
            if (cineFxSelected >= 0 && cineFxSelected < fxCount)
            {
                int row = cineFxSelected;
                var fx = p.CineEffects[row];
                bool shake = fx.Type == "Shake", panel = fx.Type == "Panel", percent = fx.Type == "Fade" || fx.Type == "Bars";
                string unit = percent ? "%" : "°";
                int shown = Math.Clamp((int)Math.Round(fx.To), 0, 15);
                inspector.Children.Add(InspectorHead(FxColour(fx.Type), null, FxName(fx.Type) + " effect",
                    $"Layer {fx.Lane + 1}, {CineTime(fx.Start)} to {CineTime(fx.Start + fx.Seconds)}", Grey,
                    Tiny("Delete", Brush(HudColor.Red), () => { cineFxSelected = -1; Send(MenuAction.CineFxDelete, row); })));
                inspector.Children.Add(InspectorRow("Timing",
                    Label("Starts at", Grey, 0),
                    CommitBox(fx.Start, 64, v => Send(MenuAction.CineFxSet, row, v, "Start")),
                    Label("s", Grey, 0),
                    Label("Length", Grey, 18),
                    CommitBox(fx.Seconds, 64, v => Send(MenuAction.CineFxSet, row, v, "Seconds")),
                    Label("s", Grey, 0)));
                if (panel)
                {
                    var pages = new List<Control> { };
                    foreach (var (pageName, pageId) in new[] { ("Race results", 0), ("Race stats", 1), ("Intro", 8), ("Ship card", 6), ("Lap by lap", 7), ("Outro", 9),
                                                               ("Race graphs", 5), ("Lap replay", 4), ("The ship", 2), ("Fun facts", 12), ("Leaderboard", 14), ("Head to head", 15), ("Hidden", 3) })
                    {
                        int id = pageId;
                        pages.Add(Tiny(pageName, shown == id ? Brush(HudColor.Green) : White, () => Send(MenuAction.CineFxSet, row, id, "To")));
                    }
                    inspector.Children.Add(InspectorRow("Show", pages.ToArray()));
                }
                else if (shake)
                    inspector.Children.Add(InspectorRow("Strength",
                        CommitBox(fx.To, 64, v => Send(MenuAction.CineFxSet, row, v, "To")),
                        Label("°   (0.3 gentle, 1 rough)", Grey, 0)));
                else
                    inspector.Children.Add(InspectorRow(fx.Type == "Fov" ? "Field of view" : fx.Type == "Roll" ? "Tilt" : fx.Type == "Fade" ? "Darkness" : "Bar size",
                        Label("From", Grey, 0),
                        CommitBox(fx.From, 64, v => Send(MenuAction.CineFxSet, row, v, "From")),
                        Label(unit, Grey, 0),
                        Label("to", Grey, 18),
                        CommitBox(fx.To, 64, v => Send(MenuAction.CineFxSet, row, v, "To")),
                        Label(unit, Grey, 0),
                        fx.Type == "Fov" && p.CineDefaultFov > 1 ? Label($"(the game's own is {p.CineDefaultFov:0}°)", Grey) : null));
                string fxAboutShort =
                    fx.Type == "Fov" ? "The field of view goes from the first value to the second over the effect (smaller = zoomed in), then back to the game's own. The same value twice holds it."
                    : fx.Type == "Roll" ? "The picture leans from the first angle to the second (below 0 = the other way), then levels out when it ends."
                    : fx.Type == "Fade" ? "0% = clear, 100% = black. To hold black, make both 100; the picture clears when it ends, so put a fade-in right after a fade-out."
                    : fx.Type == "Bars" ? "Each bar's height as a % of the screen (0 to 45); 12 is about a widescreen film. To keep them, make the next one start at the same size."
                    : panel ? "While this runs, race spotlight shows the page picked here. Lay several one after another to script the whole showcase. Only does anything in race spotlight."
                    : "A hand-held wobble of the camera.";
                var fxNote = Small(fxAboutShort + " Type a number and press Enter, or drag the block on the timeline.", Grey, 13);
                fxNote.TextWrapping = TextWrapping.Wrap;
                fxNote.Margin = new Thickness(0, 6 * s, 0, 0);
                inspector.Children.Add(fxNote);
                inspectorCard.IsVisible = true;
                return;
            }

            // ---- the picked shot, in the inspector
            if (cineSelected < 0 || cineSelected >= count) return;
            {
                int row = cineSelected;
                var shot = p.CineShots[row];
                bool move = shot.Type == "Move", path = shot.Type == "Path";
                bool needsShip = shot.Type == "Orbit" || shot.Type == "Chase";
                bool ready = shot.Ready && (!move || shot.ReadyEnd);
                string shipName = shot.Target == null ? "no ship" : shot.Target.StartsWith("Grid_") ? "unnamed ship" : shot.Target;
                double startsAt = 0;
                for (int k = 0; k < row; k++) startsAt += secs[k];
                string status = (ready ? (needsShip || shot.Track || shot.Relative ? shipName : "world view") + (path ? $", {shot.Keys} keyframes" : "")
                                 : path ? $"{shot.Keys} keyframe{(shot.Keys == 1 ? "" : "s")}: needs 2 or more" : !shot.Ready ? "view not set" : "end view not set")
                                + $"   |   {CineTime(startsAt)} to {CineTime(startsAt + secs[row])}";

                inspector.Children.Add(InspectorHead(CineColour(shot.Type), ShotIcon(shot.Type, 18), $"Shot {row + 1}: {CineName(shot.Type)}", status, ready ? Grey : Brush(HudColor.Yellow),
                    Tiny("Preview", ready ? White : Grey, () => Send(MenuAction.CinePlay, row)),
                    Tiny("Duplicate", White, () => { cineSelected = row + 1; Send(MenuAction.CineShotDuplicate, row); }),
                    Tiny("Delete", Brush(HudColor.Red), () => { cineSelected = Math.Min(row, count - 2); Send(MenuAction.CineShotDelete, row); })));

                var timingRow = InspectorRow("Timing",
                    Label("Length", Grey, 0),
                    CommitBox(shot.Seconds, 64, v => Send(MenuAction.CineShotSet, row, v, "SecondsTo")),
                    Label("s", Grey, 0),
                    Label("Blend in", Grey, 18),
                    CommitBox(shot.Blend, 56, v => Send(MenuAction.CineShotSet, row, v, "BlendTo")),
                    Label("s", Grey, 0),
                    Small(shot.Blend > 0 ? "(the shaded corner on the block; drag its dot)" : "(0 = a hard cut; or drag the dot on the block)", Grey, 13));
                timingRow.PointerEntered += (_, __) => description.Text =
                    "Length: how long the shot lasts, in seconds. Type it and press Enter, or drag the block's right edge.\n\n"
                    + "Blend in: a smooth cut. Over this many seconds the camera eases from where the shot before left it into this shot (0 = a hard cut). It's drawn as the shaded corner at the start of the block; drag the white dot on the block's top edge to change it.";
                inspector.Children.Add(timingRow);

                var viewRow = InspectorRow(path ? "Keyframes" : "View",
                    !path ? Tiny(move ? "Set start" : "Set view", shot.Ready ? White : Brush(HudColor.Yellow), () => Send(MenuAction.CineShotCapture, row, 0)) : null,
                    !path && shot.Ready ? Tiny(move ? "Go to start" : "Go to view", White, () => Send(MenuAction.CineShotSet, row, 0, "KeyGo")) : null,
                    move ? Tiny("Set end", shot.ReadyEnd ? White : Brush(HudColor.Yellow), () => Send(MenuAction.CineShotCapture, row, 1)) : null,
                    move && shot.ReadyEnd ? Tiny("Go to end", White, () => Send(MenuAction.CineShotSet, row, shot.Ready ? 1 : 0, "KeyGo")) : null,
                    path ? Tiny("Add keyframe", shot.Ready ? White : Brush(HudColor.Yellow), () => Send(MenuAction.CineShotCapture, row, 2)) : null,
                    path && shot.Keys > 0 ? Tiny("Clear", Grey, () => Send(MenuAction.CineShotSet, row, 0, "ClearKeys")) : null,
                    new Border { Width = 12 * s },
                    Tiny("Show in world: " + (p.CinePreviewOn ? "on" : "off"), p.CinePreviewOn ? Brush(HudColor.Green) : Grey, () => Send(MenuAction.CineSet, text: "Preview")));
                viewRow.PointerEntered += (_, __) => description.Text =
                    (path ? "Add keyframe takes the view the camera has now as the next point of the path. Close the menu, fly to a view, open it and press Add keyframe; repeat (2 or more). Clear starts again."
                    : move ? "Set start / Set end take the view the camera has now. Set start, close the menu, fly to the second view, then Set end."
                    : "Set view takes the view the camera has now.")
                    + "\n\nGo to puts the spectator camera at the view so you can fine-tune it: fly a little, open the menu and Set again. Show in world draws the picked shot's camera views (and a path's curve) in the world while you edit.";
                inspector.Children.Add(viewRow);

                if (shot.Type == "Orbit")
                {
                    var motionRow = InspectorRow("Rotate",
                        CommitBox(shot.Rotate, 64, v => Send(MenuAction.CineShotSet, row, v, "RotateTo")),
                        Label("° a second   (0 = holds still, below 0 = the other way)", Grey, 0));
                    motionRow.PointerEntered += (_, __) => description.Text = "Orbit: circles the ship from the view you set at this many degrees a second, always aiming at its middle.";
                    inspector.Children.Add(motionRow);
                }
                if (!needsShip)
                {
                    var aimRow = InspectorRow("Aim",
                        Tiny("Look at ship: " + (shot.Track ? "on" : "off"), shot.Track ? Brush(HudColor.Green) : Grey, () => Send(MenuAction.CineShotSet, row, 0, "Track")),
                        shot.HasShipView ? Tiny("Place: " + (shot.Relative ? "from the ship" : "in the world"), shot.Relative ? Brush(HudColor.Cyan) : White, () => Send(MenuAction.CineShotSet, row, 0, "Relative")) : null);
                    aimRow.PointerEntered += (_, __) => description.Text =
                        "Look at ship: the camera turns to keep the ship in view.\n\nPlace: 'in the world' is the exact spot you set (for this world); 'from the ship' puts it the same way off the ship, wherever the ship is when the shot starts, then leaves it there as the ship flies on.";
                    inspector.Children.Add(aimRow);
                }

                // A camera path's keyframes: pick one to go to it, move it to the current view, add one after it,
                // move it earlier or later, or delete it.
                if (path && shot.Keys > 0)
                {
                    if (cineKeyFor != row) { cineKeyFor = row; cineKeySelected = -1; }
                    if (cineKeySelected >= shot.Keys) cineKeySelected = shot.Keys - 1;
                    int key = cineKeySelected;
                    var keyItems = new List<Control>();
                    for (int k = 0; k < shot.Keys; k++)
                    {
                        int pickK = k;
                        keyItems.Add(Tiny((k + 1).ToString(), k == key ? Brush(HudColor.Yellow) : White, () =>
                        {
                            cineKeySelected = pickK == cineKeySelected ? -1 : pickK;
                            Send(MenuAction.CineFocus, row, cineKeySelected);
                            FillCine(p);
                        }));
                    }
                    if (key >= 0)
                    {
                        keyItems.Add(new Border { Width = 14 * s });
                        keyItems.Add(Label($"Keyframe {key + 1}:", Brush(HudColor.Yellow), 0));
                        keyItems.Add(Tiny("Go to", White, () => Send(MenuAction.CineShotSet, row, key, "KeyGo")));
                        keyItems.Add(Tiny("Set to current view", White, () => Send(MenuAction.CineShotCapture, row, 3, key.ToString())));
                        keyItems.Add(Tiny("Add after", White, () => { cineKeySelected = key + 1; Send(MenuAction.CineShotCapture, row, 2, key.ToString()); }));
                        keyItems.Add(Tiny("<", key > 0 ? White : Grey, () => { if (key > 0) { cineKeySelected = key - 1; Send(MenuAction.CineShotSet, row, key, "KeyLeft"); } }));
                        keyItems.Add(Tiny(">", key < shot.Keys - 1 ? White : Grey, () => { if (key < shot.Keys - 1) { cineKeySelected = key + 1; Send(MenuAction.CineShotSet, row, key, "KeyRight"); } }));
                        keyItems.Add(Tiny("Delete", Brush(HudColor.Red), () => { cineKeySelected = Math.Min(key, shot.Keys - 2); Send(MenuAction.CineShotSet, row, key, "KeyDelete"); }));
                    }
                    else keyItems.Add(Label("pick one to go to it, move it, or add one after it", Grey, 8));
                    var keys = InspectorRow("Points", keyItems.ToArray());
                    keys.PointerEntered += (_, __) => description.Text =
                        "Keyframes, in the order the camera flies through them. Pick one (it turns yellow, in the world too) and:\n\n"
                        + "Go to: puts the spectator camera there (the menu closes) so you can see it and adjust it.\n"
                        + "Set to current view: moves the keyframe to where the camera is now.\n"
                        + "Add after: a new keyframe at the current view, straight after this one.\n"
                        + "< and >: fly through it earlier or later.\nDelete: takes it out.\n\n"
                        + "With Show in world on, every keyframe is drawn as a camera shape with its number, and the curve between them as a pink line.";
                    inspector.Children.Add(keys);
                }

                string aboutShort =
                    shot.Type == "Orbit" ? "Circles the ship from the view you set, always aiming at its middle."
                    : shot.Type == "Chase" ? "Fixed to the ship at the view you set: travels, turns and rolls with it."
                    : shot.Type == "Fixed" ? "Stays where you set it; it can turn to keep the ship in view."
                    : path ? "Flies through its keyframes in order on a smooth curve, easing away from the first and into the last."
                    : "Glides from the start view to the end view over the shot's time, easing in and out.";
                var shotNote = Small(aboutShort + " Preview plays just this shot; Duplicate (Ctrl + D) puts a copy straight after it.", Grey, 13);
                shotNote.TextWrapping = TextWrapping.Wrap;
                shotNote.Margin = new Thickness(0, 6 * s, 0, 0);
                inspector.Children.Add(shotNote);
                inspectorCard.IsVisible = true;
            }
        }

        // ---------------------------------------------------------------- RPI designer

        private bool designerOpen, rpiCostedOnly, rpiShipOnly, rpiDeleteArmed, rpiCatOpen;
        private bool rpiManageOpen = true;
        private int rpiSort, rpiShowGroups = 50;   // sort: 0 name, 1 size, 2 category, 3 RPI
        private string rpiSelected;   // the block being edited
        private readonly HashSet<string> rpiCategories = new HashSet<string>();   // ticked categories (none = all)
        private Border rpiCatDrop;    // the category tick list (beside the blocks while open)
        // The class being edited, as last sent by the game plus what's been typed since.
        private Dictionary<string, double> rpiValues = new Dictionary<string, double>();
        private Dictionary<string, int> rpiLimits = new Dictionary<string, int>();
        private List<(double Weight, double Budget)> rpiSteps = new List<(double, double)>();
        private double rpiBudget, rpiMinWeight, rpiStepWeight, rpiStepBudget;
        private string rpiClassName, rpiShipName;
        private double rpiShipMass;
        private Dictionary<string, int> rpiShipCounts;
        private TextBox rpiSearch, rpiNewName, rpiDetailBox;
        private TextBlock rpiCheck;
        private StackPanel rpiList, rpiDetail, rpiSummary;
        private WrapPanel rpiCats;
        private readonly Dictionary<string, (Border Cell, TextBox Box)> rpiRowsByKey = new Dictionary<string, (Border, TextBox)>();
        private static MethodInfo iconConvert;
        private static bool iconConvertLooked, iconFailLogged;
        // (Solid dark blues like the game's own panels: a see-through row comes out grey in the game's UI.)
        private static readonly IBrush RowRest = Argb(240, 9, 26, 38);
        private static readonly IBrush RowHover = Argb(255, 20, 52, 74);
        private static readonly IBrush RowEdge = Argb(255, 28, 58, 76);
        private static readonly IBrush RowPicked = Argb(255, 112, 78, 22);
        private static readonly IBrush CellRest = Argb(255, 14, 38, 54);
        private static readonly IBrush RowAlt = Argb(240, 12, 33, 48);        // every other row of the block list
        private static readonly IBrush NameQuiet = Argb(255, 150, 168, 182);  // blocks the class doesn't cost

        private void OpenDesigner()
        {
            designerOpen = true;
            BuildDesigner(Volatile.Read(ref pending) ?? lastRpiPanel);
        }

        private void CloseDesigner()
        {
            designerOpen = false;
            var now = Volatile.Read(ref pending) ?? lastRpiPanel;
            if (now != null) FillRpi(now);   // (budget and weight typed in the designer show in the class list)
            ShowPage();
        }

        /// <summary>A block's icon as a picture (the game's own icon), or null.</summary>
        private static IImage IconOf(Se2Bridge.BlockEntry e)
        {
            if (e.ImageTried) return e.Image as IImage;
            e.ImageTried = true;
            if (e.Icon == null) return null;
            try
            {
                if (!iconConvertLooked)
                {
                    iconConvertLooked = true;
                    var type = AccessTools.TypeByName("Keen.VRage.UI.AvaloniaInterface.Bitmaps.MarkupImageConvert");
                    iconConvert = type == null ? null : AccessTools.Method(type, "Convert");
                    if (iconConvert == null) PluginLog.Info("RPI designer: the game's icon converter wasn't found (no block icons)");
                }
                e.Image = iconConvert?.Invoke(null, new[] { e.Icon });
            }
            catch (Exception ex)
            {
                if (!iconFailLogged) { iconFailLogged = true; PluginLog.Info($"RPI designer: icon for {e.Key} failed: {(ex.InnerException ?? ex).Message}"); }
            }
            return e.Image as IImage;
        }

        private Control IconBox(Se2Bridge.BlockEntry e, double size)
        {
            var box = new Border { Width = size, Height = size, Background = Argb(255, 5, 16, 24), VerticalAlignment = VerticalAlignment.Center };
            var image = IconOf(e);
            if (image != null) box.Child = new Image { Source = image, Stretch = Stretch.Uniform };
            return box;
        }

        private static string SizeLabel(Se2Bridge.BlockEntry e) => e.Size > 0 ? $"{e.Size:0.##} m" : "-";
        private static string BlockLabel(Se2Bridge.BlockEntry e) => e.Size > 0 ? $"{e.Name}  {e.Size:0.##} m" : e.Name;
        private static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>A number box small enough to sit in a row.</summary>
        private TextBox SmallBox(double value, string watermark, double width, Action<double> changed)
        {
            double s = scale;
            var box = NumberBox(value, watermark, changed);
            box.Width = width * s;
            box.MinHeight = 0;
            box.Height = 32 * s;
            box.Padding = new Thickness(6 * s, 0, 6 * s, 0);
            box.FontSize = 15 * s;
            TidyBox(box);
            return box;
        }

        /// <summary>A row you can click (left-aligned content, lit while pointed at).</summary>
        private Border ClickRow(Control content, Action click, double s)
        {
            var row = new Border
            {
                Child = content, Background = RowRest, Tag = RowRest,
                BorderBrush = RowEdge, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6 * s, 2 * s, 6 * s, 2 * s), HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // (Tag = its resting colour, so pointing away puts back the picked highlight.)
            row.PointerEntered += (_, __) => SetHover(row);
            row.PointerMoved += (_, __) => SetHover(row);
            row.PointerExited += (_, __) => { if (ReferenceEquals(rpiHover, row)) ClearHover(); };
            row.PointerPressed += (_, e) => { e.Handled = true; click(); };
            return row;
        }

        // Only one row is lit at a time, and the designer checks the mouse is still over it whenever it moves (the
        // game's UI doesn't always say when the mouse has left a row).
        private Border rpiHover;

        private void SetHover(Border row)
        {
            if (ReferenceEquals(rpiHover, row)) return;
            var was = rpiHover;
            rpiHover = row;
            row.Background = RowHover;
            row.InvalidateVisual();
            Unlight(was);
            Sweep(row.Parent as Panel);
        }

        private void ClearHover()
        {
            var was = rpiHover;
            rpiHover = null;
            Unlight(was);
            Sweep(was?.Parent as Panel);
        }

        /// <summary>Puts a row back to its resting colour and makes sure the game draws it again.</summary>
        private void Unlight(Border row)
        {
            if (row == null || ReferenceEquals(row, rpiHover)) return;
            row.Background = row.Tag as IBrush ?? RowRest;
            row.InvalidateVisual();
        }

        /// <summary>
        /// Every other row in the same list back to its resting colour, now and again a moment later: moving the
        /// mouse quickly down a list can leave rows lit that the game never said the mouse had left (or never redrew).
        /// </summary>
        private void Sweep(Panel list)
        {
            if (list == null) return;
            void Pass()
            {
                foreach (var child in list.Children)
                    if (child is Border row && row.Tag is IBrush rest && !ReferenceEquals(row, rpiHover) && !ReferenceEquals(row.Background, rest))
                    {
                        row.Background = rest;
                        row.InvalidateVisual();
                    }
                list.InvalidateVisual();
            }
            Pass();
            DispatcherTimer.RunOnce(Pass, TimeSpan.FromMilliseconds(60));
        }

        /// <summary>Takes the scroll bar and any buttons out of a small text box (they show as a grey square in it).</summary>
        private static void TidyBox(TextBox box)
        {
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Hidden);
            box.AcceptsReturn = false;
            void Hide()
            {
                try
                {
                    foreach (var v in box.GetVisualDescendants())
                    {
                        string name = v is Control named ? named.Name : null;
                        if (v is ScrollBar || v is Button || v is ToggleButton
                            || (name != null && (name.IndexOf("Clear", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Reveal", StringComparison.OrdinalIgnoreCase) >= 0)))
                            v.IsVisible = false;
                    }
                }
                catch { /* leave the box as it is */ }
            }
            box.TemplateApplied += (_, __) => Dispatcher.UIThread.Post(Hide);
            box.AttachedToVisualTree += (_, __) => Dispatcher.UIThread.Post(Hide);
        }

        private void BuildDesigner(HudModel.ControlsPanel p)
        {
            double s = scale;
            if (p == null) return;
            if (rpiSearch == null)
            {
                rpiSearch = new TextBox { Watermark = "Search blocks", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4 * s, 0, 12 * s, 0), Width = 360 * s, HorizontalAlignment = HorizontalAlignment.Left };
                rpiSearch.TextChanged += (_, __) => { rpiShowGroups = 50; RefreshBlockList(); };
                rpiNewName = new TextBox { Watermark = "Class name", Width = 240 * s, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8 * s, 4 * s) };
            }
            if (rpiSearch.Parent is Panel oldSearchParent) oldSearchParent.Children.Remove(rpiSearch);
            if (rpiNewName.Parent is Panel oldNameParent) oldNameParent.Children.Remove(rpiNewName);
            rpiDeleteArmed = false;

            // (The faintest background, so the designer hears the mouse moving anywhere on it.)
            var root = new Grid { Margin = new Thickness(8 * s, 0, 8 * s, 0), Background = Argb(1, 9, 26, 38) };
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            ClearHover();
            root.PointerMoved += (_, e) =>
            {
                if (rpiHover == null) return;
                var at = e.GetPosition(rpiHover);
                if (at.X < 0 || at.Y < 0 || at.X > rpiHover.Bounds.Width || at.Y > rpiHover.Bounds.Height) ClearHover();
            };
            root.PointerExited += (_, __) => ClearHover();
            var top = new StackPanel { Spacing = 2 * s, Margin = new Thickness(0, 0, 0, 6 * s) };
            Grid.SetRow(top, 0); root.Children.Add(top);

            TextBlock Label(string text, IBrush brush, double size, bool bold = false)
            {
                var t = Text(text, brush);
                SoftFontSize(t, size * s);
                if (bold) t.FontWeight = FontWeight.Bold;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Margin = new Thickness(0, 0, 10 * s, 4 * s);
                return t;
            }
            Button Small(string text, IBrush brush, Action click)
            {
                var b = ValueButton(text, brush, 0, click);
                b.Margin = new Thickness(0, 0, 8 * s, 4 * s);
                b.Padding = new Thickness(12 * s, 0, 12 * s, 0);
                b.MinHeight = 34 * s;
                return b;
            }

            // Title bar, with the classes: pick the one to edit (it's also the one in use).
            var bar = new WrapPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(Small("< Back", White, CloseDesigner));
            var heading = Label("RPI DESIGNER", White, 22, true);
            heading.Margin = new Thickness(4 * s, 0, 24 * s, 4 * s);
            bar.Children.Add(heading);
            bar.Children.Add(Label("Class", Grey, 16));
            foreach (var c in p.RpiClasses)
            {
                string name = c.Name;
                bool on = name == p.RpiCurrent;
                var chip = Small(name, on ? Brush(HudColor.Purple) : Grey, () => commands.Enqueue(new MenuCommand(MenuAction.RpiSelectClass, text: name)));
                chip.Opacity = on ? 1 : 0.7;
                bar.Children.Add(chip);
            }
            if (p.RpiClasses.Count == 0) bar.Children.Add(Label("none yet: make or import one below", Brush(HudColor.Yellow), 16));
            bool manageShown = rpiManageOpen || p.RpiCurrent == null;
            if (p.RpiCurrent != null)
                bar.Children.Add(Small(rpiManageOpen ? "Manage classes  ^" : "Manage classes  v", White, () =>
                {
                    rpiManageOpen = !rpiManageOpen;
                    BuildDesigner(Volatile.Read(ref pending) ?? lastRpiPanel);
                }));
            top.Children.Add(bar);

            if (manageShown)
            {
                var manage = new WrapPanel { Orientation = Orientation.Horizontal };
                manage.Children.Add(rpiNewName);
                manage.Children.Add(Small("New class", White, () => { commands.Enqueue(new MenuCommand(MenuAction.RpiNewClass, text: rpiNewName.Text)); rpiNewName.Text = ""; }));
                if (p.RpiCurrent != null)
                {
                    string current = p.RpiCurrent;
                    manage.Children.Add(Small("Duplicate", White, () => { commands.Enqueue(new MenuCommand(MenuAction.RpiDuplicateClass, text: rpiNewName.Text)); rpiNewName.Text = ""; }));
                    manage.Children.Add(Small("Rename to this", White, () => { commands.Enqueue(new MenuCommand(MenuAction.RpiRenameClass, text: rpiNewName.Text)); rpiNewName.Text = ""; }));
                    Button delete = null;
                    delete = Small("Delete class", Brush(HudColor.Red), () =>
                    {
                        if (!rpiDeleteArmed)
                        {
                            rpiDeleteArmed = true;
                            if (delete.Content is TextBlock label) label.Text = $"Click again to delete '{current}'";
                            return;
                        }
                        commands.Enqueue(new MenuCommand(MenuAction.RpiDeleteClass, text: current));
                    });
                    manage.Children.Add(delete);
                }
                // (Sharing apart from the class's own actions: a gap and a quiet word, then export / copy / paste.)
                var shareWord = Label("Share", Grey, 14);
                shareWord.Margin = new Thickness(22 * s, 0, 10 * s, 4 * s);
                manage.Children.Add(shareWord);
                if (p.RpiCurrent != null)
                {
                    manage.Children.Add(Small("Export", White, () => commands.Enqueue(new MenuCommand(MenuAction.RpiExport))));
                    Button copy = null;
                    copy = Small("Copy", White, () =>
                    {
                        bool ok = ClipboardText.Set(CurrentClassJson());
                        if (copy.Content is TextBlock label) label.Text = ok ? "Copied" : "Couldn't copy";
                    });
                    manage.Children.Add(copy);
                }
                manage.Children.Add(Small("Paste", White, () => commands.Enqueue(new MenuCommand(MenuAction.RpiImportText, text: ClipboardText.Get()))));
                manage.Children.Add(Small("Open folder", White, () => commands.Enqueue(new MenuCommand(MenuAction.RpiOpenFolder))));
                if (!p.RpiClasses.Any(c => string.Equals(c.Name, "Atmo Sprinter", StringComparison.OrdinalIgnoreCase)))
                    manage.Children.Add(Small("Add Atmo Sprinter class", White, AddAtmoSprinterClass));
                top.Children.Add(manage);

                // Budget and minimum weight, then the class files that can be imported.
                var limits = new WrapPanel { Orientation = Orientation.Horizontal };
                if (p.RpiCurrent != null)
                {
                    var budgetWord = Label("Budget (max RPI)", Grey, 16);
                    budgetWord.Width = 150 * s;
                    limits.Children.Add(budgetWord);
                    var budgetBox = SmallBox(rpiBudget, "no budget", 110, v => { rpiBudget = v; commands.Enqueue(new MenuCommand(MenuAction.SetRpiBudget, value: v)); RefreshCheck(); });
                    budgetBox.Margin = new Thickness(0, 0, 24 * s, 4 * s);
                    limits.Children.Add(budgetBox);
                    limits.Children.Add(Label("Min weight (kg)", Grey, 16));
                    var weightBox = SmallBox(rpiMinWeight, "no minimum", 110, v => { rpiMinWeight = v; commands.Enqueue(new MenuCommand(MenuAction.SetRpiMinWeight, value: v)); RefreshCheck(); });
                    weightBox.Margin = new Thickness(0, 0, 32 * s, 4 * s);
                    limits.Children.Add(weightBox);
                }
                if (p.RpiCurrent != null) top.Children.Add(limits);
                // (Class files to import: a line of their own, its label in the same column.)
                var imports = new WrapPanel { Orientation = Orientation.Horizontal };
                var importWord = Label("Import", Grey, 16);
                importWord.Width = 150 * s;
                imports.Children.Add(importWord);
                foreach (var f in p.RpiFiles)
                {
                    string path = f.Path;
                    imports.Children.Add(Small(f.Name, White, () => commands.Enqueue(new MenuCommand(MenuAction.RpiImport, text: path))));
                }
                if (p.RpiFiles.Count == 0) imports.Children.Add(Label("put a class file in the folder (Open folder) and it shows here, or Paste one", Grey, 14));

                // Lighter ships: weight steps that trade weight for budget.
                if (p.RpiCurrent != null)
                {
                    var steps = new WrapPanel { Orientation = Orientation.Horizontal };
                    var stepsWord = Label("Lighter ships", Grey, 16);
                    stepsWord.Width = 150 * s;
                    steps.Children.Add(stepsWord);
                    SmallTip(steps, "A ship under the minimum weight gets the budget of the heaviest step it reaches. Type a weight and its budget and press Add step; click a step to remove it.");
                    foreach (var step in rpiSteps.OrderByDescending(x => x.Weight))
                    {
                        double w = step.Weight;
                        var chip = Small($"{step.Weight / 1000:0.##} t: {Num(step.Budget)}   x", Brush(HudColor.Cyan), () => commands.Enqueue(new MenuCommand(MenuAction.RpiRemoveStep, value: w)));
                        steps.Children.Add(chip);
                    }
                    var stepWeight = SmallBox(0, "weight kg", 110, v => rpiStepWeight = v);
                    stepWeight.Margin = new Thickness(0, 0, 8 * s, 4 * s);
                    steps.Children.Add(stepWeight);
                    var stepBudget = SmallBox(0, "budget", 90, v => rpiStepBudget = v);
                    stepBudget.Margin = new Thickness(0, 0, 8 * s, 4 * s);
                    steps.Children.Add(stepBudget);
                    rpiStepWeight = rpiStepBudget = 0;
                    steps.Children.Add(Small("Add step", White, () => commands.Enqueue(new MenuCommand(MenuAction.RpiAddStep, value: rpiStepWeight, text: Num(rpiStepBudget)))));
                    top.Children.Add(steps);
                }
                top.Children.Add(imports);
            }

            rpiRowsByKey.Clear();
            rpiList = null; rpiDetail = null; rpiSummary = null; rpiCats = null; rpiCatDrop = null; rpiCatOpen = false; rpiCheck = null; rpiDetailBox = null;
            if (p.RpiCurrent == null)
            {
                designerView = root;
                ShowPage();
                return;
            }

            // The last ship looked at with the analyser, checked against the class as you edit.
            rpiCheck = Label("", White, 15);
            rpiCheck.TextWrapping = TextWrapping.Wrap;
            top.Children.Add(rpiCheck);
            top.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(0, 2 * s, 0, 0) });

            // Body: every block in the game on the left (filters + search), the picked block on the right.
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(16 * s, GridUnitType.Pixel)));
            body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(430 * s, GridUnitType.Pixel)));
            Grid.SetRow(body, 1); root.Children.Add(body);

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            left.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            var filters = new Grid { Margin = new Thickness(0, 4 * s, 0, 4 * s) };
            filters.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            filters.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            rpiCats = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rpiCats, 0); filters.Children.Add(rpiCats);
            Grid.SetColumn(rpiSearch, 1); filters.Children.Add(rpiSearch);
            Grid.SetRow(filters, 0); left.Children.Add(filters);
            // Under it: the category tick list (while open) beside the blocks, so neither covers the other.
            var lists = new Grid();
            lists.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            lists.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            rpiCatDrop = new Border
            {
                Background = RowRest, BorderBrush = RowEdge, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 0, 12 * s, 0),
                Padding = new Thickness(6 * s), IsVisible = false,
            };
            Grid.SetColumn(rpiCatDrop, 0); lists.Children.Add(rpiCatDrop);
            rpiList = new StackPanel();
            var listScroll = new ScrollViewer { Content = rpiList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(listScroll, 1); lists.Children.Add(listScroll);
            Grid.SetRow(lists, 1); left.Children.Add(lists);
            Grid.SetColumn(left, 0); body.Children.Add(left);

            rpiDetail = new StackPanel { Spacing = 4 * s };
            rpiSummary = new StackPanel { Spacing = 0, Margin = new Thickness(0, 10 * s, 0, 0) };
            var rightScroll = new ScrollViewer
            {
                Content = new StackPanel { Children = { rpiDetail, rpiSummary } },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetColumn(rightScroll, 2); body.Children.Add(rightScroll);

            designerView = root;
            RefreshCategories();
            RefreshBlockList();
            RefreshDetail();
            RefreshSummary();
            RefreshCheck();
            ShowPage();
        }

        /// <summary>The class being edited as the text of a class file (for copying to the clipboard).</summary>
        private string CurrentClassJson()
        {
            var rc = new GateSettings.RpiClass
            {
                Name = rpiClassName, Budget = rpiBudget, MinWeight = rpiMinWeight,
                Values = new Dictionary<string, double>(rpiValues), Limits = new Dictionary<string, int>(rpiLimits),
                Steps = rpiSteps.Select(x => new GateSettings.RpiStep { Weight = x.Weight, Budget = x.Budget }).ToList(),
            };
            return System.Text.Json.JsonSerializer.Serialize(rc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>The line above the lists: the last analysed ship against the class as it is now.</summary>
        private void RefreshCheck()
        {
            if (rpiCheck == null) return;
            if (rpiShipCounts == null || rpiShipCounts.Count == 0)
            {
                rpiCheck.Text = "No ship checked yet: open the Ship analyser (Race tab) on a ship, then come back.";
                rpiCheck.Foreground = Grey;
                return;
            }
            double total = 0;
            foreach (var kv in rpiShipCounts)
                if (rpiValues.TryGetValue(kv.Key, out double cost)) total += cost * kv.Value;
            double budget = GateSettings.RpiBudgetFor(rpiShipMass, rpiMinWeight, rpiBudget, rpiSteps);
            double lowest = GateSettings.RpiLowestWeight(rpiMinWeight, rpiSteps);
            bool over = budget > 0 && total > budget + 1e-9;
            bool light = lowest > 0 && Math.Round(rpiShipMass) < lowest;
            int broken = rpiLimits.Count(l => (rpiShipCounts.TryGetValue(l.Key, out int n) ? n : 0) > l.Value);
            string text = $"Ship check - {rpiShipName}:  {Num(total)}" + (budget > 0 ? $" / {Num(budget)} RPI  ({(over ? Num(total - budget) + " OVER" : Num(budget - total) + " left")})" : " RPI")
                + $"     {rpiShipMass:#,0} kg" + (lowest > 0 ? (light ? $"  (UNDER the {lowest:#,0} kg minimum)" : "  (weight OK)") : "")
                + (rpiLimits.Count > 0 ? (broken > 0 ? $"     {broken} block limit{(broken == 1 ? "" : "s")} BROKEN" : "     block limits OK") : "");
            rpiCheck.Text = text;
            rpiCheck.Foreground = over || light || broken > 0 ? Brush(HudColor.Red) : Brush(HudColor.Green);
        }

        private void RefreshCategories()
        {
            if (rpiCats == null) return;
            double s = scale;
            var all = Se2Bridge.AllBlocks().Select(x => x.Category).Distinct()
                .OrderBy(x => x == "Other" ? 1 : 0).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            rpiCategories.RemoveWhere(x => !all.Contains(x));

            // The bar: the drop-down's button (says what's ticked), the switches and the sort order.
            rpiCats.Children.Clear();
            void Bar(string text, bool on, bool dim, Action click)
            {
                // (Smaller than the buttons above and lit when on, so they read as filters on the list, not actions.)
                var b = ValueButton(text, on ? Brush(HudColor.Purple) : dim ? Grey : White, 0, click);
                b.Margin = new Thickness(0, 0, 6 * s, 0);
                b.Padding = new Thickness(10 * s, 0, 10 * s, 0);
                b.MinHeight = 28 * s;
                if (b.Content is TextBlock chipWords) chipWords.FontSize = 13 * s;
                if (on) { b.BorderBrush = Brush(HudColor.Purple); b.BorderThickness = new Thickness(1); }
                if (dim && !on) b.Opacity = 0.65;
                rpiCats.Children.Add(b);
            }
            var filterWord = Text("FILTER", Grey);
            SoftFontSize(filterWord, 12 * s);
            filterWord.VerticalAlignment = VerticalAlignment.Center;
            filterWord.Margin = new Thickness(2 * s, 0, 8 * s, 0);
            rpiCats.Children.Add(filterWord);
            string picked = rpiCategories.Count == 0 ? "All"
                : rpiCategories.Count <= 2 ? string.Join(", ", rpiCategories.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : rpiCategories.Count + " picked";
            Bar($"Category: {picked}   {(rpiCatOpen ? "^" : "v")}", rpiCategories.Count > 0, false, () => { rpiCatOpen = !rpiCatOpen; RefreshCategories(); });
            Bar("With a cost", rpiCostedOnly, true, () => { rpiCostedOnly = !rpiCostedOnly; rpiShowGroups = 50; RefreshCategories(); RefreshBlockList(); });
            if (rpiShipCounts != null && rpiShipCounts.Count > 0)
                Bar("On last ship", rpiShipOnly, true, () => { rpiShipOnly = !rpiShipOnly; rpiShowGroups = 50; RefreshCategories(); RefreshBlockList(); });
            else rpiShipOnly = false;
            string[] sorts = { "name", "size", "category", "RPI" };
            Bar("Sort: " + sorts[rpiSort % sorts.Length], false, false, () => { rpiSort = (rpiSort + 1) % sorts.Length; RefreshCategories(); RefreshBlockList(); });

            // The tick list: a box for each category.
            if (rpiCatDrop == null) return;
            rpiCatDrop.IsVisible = rpiCatOpen;
            if (!rpiCatOpen) { rpiCatDrop.Child = null; return; }
            var rows = new StackPanel { MinWidth = 240 * s };
            void Tick(string text, bool on, Action click)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 * s };
                line.Children.Add(new Border
                {
                    Width = 18 * s, Height = 18 * s, VerticalAlignment = VerticalAlignment.Center,
                    BorderBrush = on ? Brush(HudColor.Purple) : Grey, BorderThickness = new Thickness(2),
                    Child = on ? new Border { Background = Brush(HudColor.Purple), Margin = new Thickness(3 * s) } : null,
                });
                var label = Text(text, on ? White : Grey);
                SoftFontSize(label, 16 * s);
                label.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(label);
                var row = ClickRow(line, click, s);
                row.Padding = new Thickness(6 * s, 4 * s, 6 * s, 4 * s);
                rows.Children.Add(row);
            }
            Tick("All categories", rpiCategories.Count == 0, () => { rpiCategories.Clear(); rpiShowGroups = 50; RefreshCategories(); RefreshBlockList(); });
            foreach (string cat in all)
            {
                string c = cat;
                Tick(c, rpiCategories.Contains(c), () =>
                {
                    if (!rpiCategories.Remove(c)) rpiCategories.Add(c);
                    rpiShowGroups = 50;
                    RefreshCategories();
                    RefreshBlockList();
                });
            }
            var close = ValueButton("Done", White, 0, () => { rpiCatOpen = false; RefreshCategories(); });
            close.Margin = new Thickness(0, 6 * s, 0, 0);
            close.MinHeight = 32 * s;
            rows.Children.Add(close);
            rpiCatDrop.Child = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        /// <summary>
        /// The block list: one compact row per block (icon, name), with every size of it side by side, each size with
        /// its own RPI box to type straight into.
        /// </summary>
        private void RefreshBlockList()
        {
            if (rpiList == null) return;
            double s = scale;
            rpiList.Children.Clear();
            rpiRowsByKey.Clear();
            var all = Se2Bridge.AllBlocks();
            if (all.Count == 0)
            {
                var none = Text("The game's block list couldn't be read (see RaceDisplay.log, the lines starting 'RPI designer').", Grey);
                none.TextWrapping = TextWrapping.Wrap;
                rpiList.Children.Add(none);
                return;
            }
            string[] words = (rpiSearch?.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double ValueOf(Se2Bridge.BlockEntry e) => rpiValues.TryGetValue(e.Key, out double cost) ? cost : 0;
            int OnShip(Se2Bridge.BlockEntry e) => rpiShipCounts != null && rpiShipCounts.TryGetValue(e.Key, out int n) ? n : 0;
            bool Matches(Se2Bridge.BlockEntry e)
            {
                if (rpiCategories.Count > 0 && !rpiCategories.Contains(e.Category)) return false;
                if (rpiCostedOnly && ValueOf(e) == 0 && !rpiLimits.ContainsKey(e.Key)) return false;
                if (rpiShipOnly && OnShip(e) == 0) return false;
                foreach (string w in words)
                    if (e.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 && e.Key.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0
                        && (e.Group ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 && e.Category.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0
                        && !Num(e.Size).StartsWith(w.TrimEnd('m', 'M'), StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }
            // Blocks of the same name are one row, their sizes smallest first.
            var groups = all.Where(Matches).GroupBy(e => e.Name + "\n" + e.Category)
                .Select(g => g.OrderBy(e => e.Size).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList()).ToList();
            switch (rpiSort)
            {
                case 1: groups = groups.OrderBy(g => g[0].Size).ThenBy(g => g[0].Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                case 2: groups = groups.OrderBy(g => g[0].Category, StringComparer.OrdinalIgnoreCase).ThenBy(g => g[0].Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                case 3: groups = groups.OrderByDescending(g => g.Max(e => ValueOf(e))).ThenBy(g => g[0].Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                default: groups = groups.OrderBy(g => g[0].Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
            }
            int shown = 0;
            string lastHeadCategory = null;
            foreach (var group in groups)
            {
                if (shown >= rpiShowGroups) break;
                shown++;
                var first = group[0];
                // (Sorted by category: a heading where each category starts, and no category under every name.)
                if (rpiSort == 2 && first.Category != lastHeadCategory)
                {
                    lastHeadCategory = first.Category;
                    var categoryHead = Text((first.Category ?? "Other").ToUpperInvariant(), Brush(HudColor.Cyan));
                    SoftFontSize(categoryHead, 13 * s);
                    categoryHead.FontWeight = FontWeight.Bold;
                    categoryHead.Margin = new Thickness(6 * s, shown == 1 ? 2 * s : 12 * s, 0, 3 * s);
                    rpiList.Children.Add(categoryHead);
                }
                // (A block the class costs or limits: bright, with an orange bar down its left. The rest are quieter.)
                bool groupCosted = group.Any(x => ValueOf(x) != 0 || rpiLimits.ContainsKey(x.Key));
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(250 * s, GridUnitType.Pixel)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                var icon = IconBox(first, 34 * s);
                Grid.SetColumn(icon, 0); grid.Children.Add(icon);
                if (groupCosted)
                    grid.Children.Add(new Border { Width = 3 * s, Background = Brush(HudColor.Orange), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6 * s, 0, 0, 0), IsHitTestVisible = false });
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8 * s, 0, 8 * s, 0) };
                var name = Text(first.Name, groupCosted ? White : NameQuiet);
                SoftFontSize(name, 15 * s);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                names.Children.Add(name);
                var place = Text(first.Category, Grey);
                SoftFontSize(place, 11 * s);
                place.Opacity = 0.8;
                if (rpiSort != 2) names.Children.Add(place);
                Grid.SetColumn(names, 1); grid.Children.Add(names);

                // Its sizes: "1 m [box]  2 m [box] ..." (click a size for its details on the right).
                var sizes = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var e in group)
                {
                    var entry = e;
                    var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s };
                    int have = OnShip(entry);
                    var size = Text(SizeLabel(entry) + (have > 0 ? $"  x{have}" : ""), have > 0 ? Brush(HudColor.Cyan) : ValueOf(entry) != 0 ? White : NameQuiet);
                    SoftFontSize(size, 14 * s);
                    size.VerticalAlignment = VerticalAlignment.Center;
                    size.MinWidth = 58 * s;
                    size.TextAlignment = TextAlignment.Right;
                    cell.Children.Add(size);
                    var box = SmallBox(ValueOf(entry), "-", 72, v => SetBlockValue(entry.Key, v));
                    // (An empty box is dimmed until it's typed in, so the costs that are set stand out.)
                    box.Opacity = ValueOf(entry) != 0 ? 1 : 0.55;
                    box.GotFocus += (_, __) => { box.Opacity = 1; SelectBlock(entry.Key); };
                    cell.Children.Add(box);
                    // (Every cell the same width, so a block's first, second, third size line up down the list.)
                    var holder = new Border
                    {
                        Child = cell, Background = CellRest, Tag = CellRest, Padding = new Thickness(6 * s, 2 * s, 4 * s, 2 * s),
                        Margin = new Thickness(0, 1 * s, 6 * s, 1 * s), MinWidth = 152 * s,
                    };
                    holder.PointerPressed += (_, press) => { press.Handled = true; SelectBlock(entry.Key); };
                    rpiRowsByKey[entry.Key] = (holder, box);
                    sizes.Children.Add(holder);
                }
                Grid.SetColumn(sizes, 2); grid.Children.Add(sizes);
                var row = ClickRow(grid, () => SelectBlock(first.Key), s);
                row.Margin = new Thickness(0, 0, 12 * s, 0);
                if (shown % 2 == 0) { row.Background = RowAlt; row.Tag = RowAlt; }   // (striped, to follow a row across)
                rpiList.Children.Add(row);
            }
            if (groups.Count == 0)
            {
                var none = Text("No blocks match.", Grey);
                none.Margin = new Thickness(6 * s, 8 * s, 0, 8 * s);
                SoftFontSize(none, 15 * s);
                rpiList.Children.Add(none);
            }
            else if (groups.Count > shown)
            {
                var more = ValueButton($"Show more ({groups.Count - shown} more blocks)", White, 0, () => { rpiShowGroups += 50; RefreshBlockList(); });
                more.Margin = new Thickness(0, 8 * s, 12 * s, 8 * s);
                more.MinHeight = 34 * s;
                rpiList.Children.Add(more);
            }
            RefreshRowStates();
        }

        /// <summary>The list's highlight and RPI boxes (without rebuilding it, so it stays scrolled where it is).</summary>
        private void RefreshRowStates()
        {
            foreach (var kv in rpiRowsByKey)
            {
                kv.Value.Cell.Background = kv.Key == rpiSelected ? RowPicked : CellRest;
                SyncBox(kv.Value.Box, kv.Key);
                if (!kv.Value.Box.IsFocused) kv.Value.Box.Opacity = rpiValues.TryGetValue(kv.Key, out double keptCost) && keptCost != 0 ? 1 : 0.55;
            }
            if (rpiDetailBox != null && rpiSelected != null) SyncBox(rpiDetailBox, rpiSelected);
        }

        /// <summary>Makes a box show a block's value, unless it's the box being typed in.</summary>
        private void SyncBox(TextBox box, string key)
        {
            if (box.IsFocused) return;
            double value = rpiValues.TryGetValue(key, out double cost) ? cost : 0;
            if (TryNumber(box.Text, out double has) && has == value) return;
            box.Text = value != 0 ? Num(value) : "";
        }

        private void SelectBlock(string key)
        {
            if (rpiSelected == key) return;
            rpiSelected = key;
            if (rpiCatOpen) { rpiCatOpen = false; RefreshCategories(); }
            RefreshDetail();
            RefreshRowStates();
        }

        private void SetBlockValue(string key, double value)
        {
            double had = rpiValues.TryGetValue(key, out double cost) ? cost : 0;
            if (had == value) return;
            if (value == 0) rpiValues.Remove(key); else rpiValues[key] = value;
            commands.Enqueue(new MenuCommand(MenuAction.SetRpiValue, value: value, text: key));
            RefreshRowStates();
            RefreshSummary();
            RefreshCheck();
        }

        /// <summary>The most of a block a ship may have: 0 = banned, under 0 = no limit.</summary>
        private void SetBlockLimit(string key, int most)
        {
            if (most < 0) rpiLimits.Remove(key); else rpiLimits[key] = most;
            commands.Enqueue(new MenuCommand(MenuAction.SetRpiLimit, value: most, text: key));
            RefreshSummary();
            RefreshCheck();
        }

        private void RefreshDetail()
        {
            if (rpiDetail == null) return;
            double s = scale;
            rpiDetail.Children.Clear();
            rpiDetailBox = null;
            var e = rpiSelected == null ? null : Se2Bridge.AllBlocks().FirstOrDefault(x => x.Key == rpiSelected);
            if (e == null)
            {
                // (Nothing picked: no words here, so the class list sits at the top. The how-to is behind its "?".)
                return;
            }
            string key = e.Key;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 * s };
            head.Children.Add(IconBox(e, 72 * s));
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320 * s };
            var name = Text(BlockLabel(e), White);
            SoftFontSize(name, 19 * s);
            name.FontWeight = FontWeight.Bold;
            name.TextWrapping = TextWrapping.Wrap;
            titles.Children.Add(name);
            int have = rpiShipCounts != null && rpiShipCounts.TryGetValue(key, out int n) ? n : 0;
            var info = Text(e.Category + (e.Group != null && e.Group != e.Name ? " / " + e.Group : "") + (have > 0 ? $"     {have} on {rpiShipName}" : ""), Grey);
            SoftFontSize(info, 13 * s);
            info.TextWrapping = TextWrapping.Wrap;
            titles.Children.Add(info);
            head.Children.Add(titles);
            rpiDetail.Children.Add(head);

            TextBlock Label(string text, double width)
            {
                var t = Text(text, White);
                SoftFontSize(t, 15 * s);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Width = width * s;
                return t;
            }
            // RPI each.
            var edit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s, Margin = new Thickness(0, 8 * s, 0, 0) };
            edit.Children.Add(Label("RPI each", 110));
            var box = SmallBox(rpiValues.TryGetValue(key, out double now) ? now : 0, "free", 100, v => SetBlockValue(key, v));
            rpiDetailBox = box;
            edit.Children.Add(box);
            rpiDetail.Children.Add(edit);

            // Most per ship / banned.
            bool hasLimit = rpiLimits.TryGetValue(key, out int limit);
            bool banned = hasLimit && limit == 0;
            var cap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s };
            cap.Children.Add(Label("Max per ship", 110));
            var capBox = SmallBox(hasLimit && limit > 0 ? limit : 0, "no limit", 100, v =>
            {
                bool isBanned = rpiLimits.TryGetValue(key, out int l) && l == 0;
                if (v >= 1) SetBlockLimit(key, (int)Math.Round(v));
                else if (!isBanned) SetBlockLimit(key, -1);
            });
            capBox.IsEnabled = !banned;
            cap.Children.Add(capBox);
            var ban = ValueButton(banned ? "Banned" : "Ban", banned ? Brush(HudColor.Red) : Grey, 0, () =>
            {
                bool isBanned = rpiLimits.TryGetValue(key, out int l) && l == 0;
                SetBlockLimit(key, isBanned ? -1 : 0);
                RefreshDetail();
            });
            ban.MinHeight = 32 * s;
            ban.Padding = new Thickness(12 * s, 0, 12 * s, 0);
            cap.Children.Add(ban);
            rpiDetail.Children.Add(cap);

            // What the block does (for judging a fair cost).
            if (e.Stats.Count > 0)
            {
                var stats = new Grid { Margin = new Thickness(0, 6 * s, 12 * s, 0) };
                stats.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                stats.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                int rowNo = 0;
                void Stat(string label, string value)
                {
                    stats.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var l = Text(label, Grey);
                    SoftFontSize(l, 13 * s);
                    l.Margin = new Thickness(0, 0, 14 * s, 1 * s);
                    Grid.SetRow(l, rowNo); Grid.SetColumn(l, 0); stats.Children.Add(l);
                    var v = Text(value, White);
                    SoftFontSize(v, 13 * s);
                    Grid.SetRow(v, rowNo); Grid.SetColumn(v, 1); stats.Children.Add(v);
                    rowNo++;
                }
                foreach (var st in e.Stats) Stat(st.Label, st.Text);
                if (e.Thrust > 0 && now > 0) Stat("Thrust / RPI", $"{e.Thrust / now / 1000:0.##} kN per RPI");
                if (e.Mass > 0 && now > 0) Stat("Weight / RPI", $"{e.Mass / now:0.#} kg per RPI");
                rpiDetail.Children.Add(stats);
            }
            var id = Text(e.Key, Grey);
            SoftFontSize(id, 11 * s);
            id.Opacity = 0.6;
            id.TextWrapping = TextWrapping.Wrap;
            rpiDetail.Children.Add(id);
        }

        /// <summary>Right-hand list: every block with a cost or a limit in this class (click to edit, x to clear).</summary>
        private void RefreshSummary()
        {
            if (rpiSummary == null) return;
            double s = scale;
            rpiSummary.Children.Clear();
            var byKey = new Dictionary<string, Se2Bridge.BlockEntry>();
            foreach (var e in Se2Bridge.AllBlocks()) byKey[e.Key] = e;
            var keys = new HashSet<string>(rpiValues.Where(kv => kv.Value != 0).Select(kv => kv.Key));
            keys.UnionWith(rpiLimits.Keys);
            var listed = keys.OrderBy(k => byKey.TryGetValue(k, out var e) ? e.Name : k, StringComparer.OrdinalIgnoreCase)
                .ThenBy(k => byKey.TryGetValue(k, out var e) ? e.Size : 0).ToList();
            // The checked ship against the budget: a bar that fills (red once it's over).
            if (rpiShipCounts != null && rpiShipCounts.Count > 0)
            {
                double shipTotal = 0;
                foreach (var kv in rpiShipCounts)
                    if (rpiValues.TryGetValue(kv.Key, out double eachCost)) shipTotal += eachCost * kv.Value;
                double shipBudget = GateSettings.RpiBudgetFor(rpiShipMass, rpiMinWeight, rpiBudget, rpiSteps);
                bool shipOver = shipBudget > 0 && shipTotal > shipBudget + 1e-9;
                var shipLine = new Grid { Margin = new Thickness(0, 0, 12 * s, 2 * s) };
                var shipWord = Text(rpiShipName ?? "Ship", White);
                SoftFontSize(shipWord, 14 * s);
                shipWord.FontWeight = FontWeight.Bold;
                shipWord.TextTrimming = TextTrimming.CharacterEllipsis;
                shipWord.Margin = new Thickness(0, 0, 110 * s, 0);
                var shipSum = Text(shipBudget > 0 ? $"{Num(shipTotal)} / {Num(shipBudget)}" : Num(shipTotal) + " RPI", shipOver ? Brush(HudColor.Red) : Brush(HudColor.Green));
                SoftFontSize(shipSum, 14 * s);
                shipSum.FontWeight = FontWeight.Bold;
                shipSum.HorizontalAlignment = HorizontalAlignment.Right;
                shipLine.Children.Add(shipWord);
                shipLine.Children.Add(shipSum);
                rpiSummary.Children.Add(shipLine);
                if (shipBudget > 0)
                {
                    double barWide = 418 * s, barShare = Math.Clamp(shipTotal / shipBudget, 0, 1);
                    rpiSummary.Children.Add(new Grid
                    {
                        Width = barWide, Height = 5 * s, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10 * s),
                        Children =
                        {
                            new Border { Background = CellRest },
                            new Border { Width = Math.Max(2 * s, barWide * barShare), HorizontalAlignment = HorizontalAlignment.Left, Background = shipOver ? Brush(HudColor.Red) : Brush(HudColor.Green) },
                        },
                    });
                }
            }
            var title = Text($"IN THIS CLASS ({listed.Count})", White);
            SoftFontSize(title, 15 * s);
            title.FontWeight = FontWeight.Bold;
            title.VerticalAlignment = VerticalAlignment.Center;
            var titleHelp = Text("?", Brush(HudColor.Cyan));
            SoftFontSize(titleHelp, 13 * s);
            titleHelp.FontWeight = FontWeight.Bold;
            var titleDot = new Border
            {
                Child = titleHelp, Background = CellRest, CornerRadius = new CornerRadius(9 * s), Padding = new Thickness(7 * s, 0, 7 * s, 1 * s),
                Margin = new Thickness(8 * s, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            SmallTip(titleDot, "Type a block's RPI straight into its box in the list on the left. Click a block (or one of its sizes) to see what it does here and to limit how many a ship may have. Every block with a cost or a limit is listed here: click one to pick it, x to take it out of the class.");
            rpiSummary.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, titleDot } });
            rpiSummary.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(0, 2 * s, 12 * s, 4 * s) });
            // One entry a block, its sizes side by side under its name (each with its cost, any limit, and an x).
            foreach (var blockKeys in listed.GroupBy(k => byKey.TryGetValue(k, out var named) ? named.Name : k.Replace('_', ' ') + "  (not in this game version)"))
            {
                var blockName = Text(blockKeys.Key, White);
                SoftFontSize(blockName, 14 * s);
                blockName.TextTrimming = TextTrimming.CharacterEllipsis;
                blockName.Margin = new Thickness(2 * s, 4 * s, 12 * s, 1 * s);
                rpiSummary.Children.Add(blockName);
                var sizeChips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8 * s, 4 * s) };
                foreach (string k in blockKeys)
                {
                    string key = k;
                    var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var sizeWord = Text(byKey.TryGetValue(key, out var sized) ? SizeLabel(sized) : "-", Grey);
                    SoftFontSize(sizeWord, 13 * s);
                    sizeWord.VerticalAlignment = VerticalAlignment.Center;
                    chipRow.Children.Add(sizeWord);
                    double cost = rpiValues.TryGetValue(key, out double c) ? c : 0;
                    if (cost != 0)
                    {
                        var value = Text(Num(cost), Brush(HudColor.Yellow));
                        SoftFontSize(value, 14 * s);
                        value.FontWeight = FontWeight.Bold;
                        value.VerticalAlignment = VerticalAlignment.Center;
                        value.Margin = new Thickness(7 * s, 0, 0, 0);
                        chipRow.Children.Add(value);
                    }
                    if (rpiLimits.TryGetValue(key, out int limit))
                    {
                        var rule = Text(limit == 0 ? "BANNED" : "max " + limit, limit == 0 ? Brush(HudColor.Red) : Brush(HudColor.Cyan));
                        SoftFontSize(rule, 12 * s);
                        rule.VerticalAlignment = VerticalAlignment.Center;
                        rule.Margin = new Thickness(7 * s, 0, 0, 0);
                        chipRow.Children.Add(rule);
                    }
                    // x: take this size out of the class (its cost and its limit). A proper little button, easy to hit.
                    var cross = Text("x", Brush(HudColor.Red));
                    SoftFontSize(cross, 14 * s);
                    cross.FontWeight = FontWeight.Bold;
                    var remove = new Border
                    {
                        Child = cross, Background = Argb(255, 58, 26, 28), CornerRadius = new CornerRadius(3 * s),
                        Padding = new Thickness(8 * s, 1 * s, 8 * s, 3 * s), Margin = new Thickness(9 * s, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    };
                    remove.PointerPressed += (_, press) =>
                    {
                        press.Handled = true;
                        if (rpiLimits.ContainsKey(key)) SetBlockLimit(key, -1);
                        SetBlockValue(key, 0);
                        RefreshSummary();
                        if (rpiSelected == key) RefreshDetail();
                    };
                    chipRow.Children.Add(remove);
                    var sizeChip = new Border
                    {
                        Child = chipRow, Background = key == rpiSelected ? RowPicked : CellRest, CornerRadius = new CornerRadius(3 * s),
                        Padding = new Thickness(8 * s, 3 * s, 3 * s, 3 * s), Margin = new Thickness(0, 0, 6 * s, 4 * s),
                    };
                    sizeChip.PointerPressed += (_, press) => { press.Handled = true; SelectBlock(key); RefreshSummary(); };
                    sizeChips.Children.Add(sizeChip);
                }
                rpiSummary.Children.Add(sizeChips);
            }
        }

        /// <summary>
        /// A new class with Diggrok's Racing League Atmo Sprinter values (RPI calculator v1.1): atmospheric thrusters,
        /// batteries and gyroscopes by size, budget 325, minimum weight 15,000 kg.
        /// </summary>
        private void AddAtmoSprinterClass()
        {
            (double Size, double Rpi)[] thrusters = { (1, 3), (2.5, 18), (5, 110), (10, 1200) };
            (double Size, double Rpi)[] batteries = { (0.5, 4), (1.5, 30), (2.5, 210) };
            (double Size, double Rpi)[] gyros = { (0.5, 5), (2.5, 550), (7.5, 6500) };
            commands.Enqueue(new MenuCommand(MenuAction.RpiNewClass, value: 325, text: "Atmo Sprinter"));
            commands.Enqueue(new MenuCommand(MenuAction.SetRpiMinWeight, value: 15000));
            foreach (var e in Se2Bridge.AllBlocks())
            {
                if (e.Size <= 0) continue;
                string n = (e.Name + " " + e.Key + " " + e.Group).ToLowerInvariant();
                var table = n.Contains("atmo") && n.Contains("thrust") ? thrusters : n.Contains("gyro") ? gyros : n.Contains("batter") ? batteries : null;
                if (table == null) continue;
                double best = table[0].Rpi, gap = double.MaxValue;
                foreach (var row in table)
                {
                    double d = Math.Abs(row.Size - e.Size);
                    if (d <= gap) { gap = d; best = row.Rpi; }   // a tie goes to the bigger block
                }
                commands.Enqueue(new MenuCommand(MenuAction.SetRpiValue, value: best, text: e.Key));
            }
        }

        private void FillRace(HudModel.ControlsPanel p)
        {
            double s = scale;
            raceRows.Children.Clear();

            // Race setup: laps, practice laps and the two tools (ship analyser, autopilot) in one short section of
            // compact rows, where there were three sections with a page-wide button each.
            raceRows.Children.Add(Section("Race setup", "Laps per race and practice laps, and the two tools: the ship analyser and the autopilot. Point at each for what it does.", "Laps"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            Grid TightRow(Grid optionRow)
            {
                optionRow.MinHeight = 40 * s;
                optionRow.Margin = new Thickness(0, 1 * s, 24 * s, 1 * s);
                if (optionRow.Children.Count > 0 && optionRow.Children[0] is TextBlock rowName) rowName.FontSize = 17 * s;
                return optionRow;
            }
            foreach (var r in p.Settings.Where(r => r.Name.StartsWith("Laps per race") || r.Name.StartsWith("Practice laps")))
            {
                var lapsControl = r.Toggle ? MakeSwitch(r) : MakeSlider(r, null, true);
                if (!r.Toggle) { lapsControl.MinHeight = 0; lapsControl.Height = 34 * s; }
                raceRows.Children.Add(TightRow(OptionRow(r.Name, r.Description, lapsControl)));
            }
            string analyserAbout = "Closes the menu and hides the race HUD: look at a ship (or any grid) to see its mass, size, blocks, thrust and acceleration each way, gyroscopes, power and hydrogen (generated and stored), cargo, centre of mass and RPI. Left click a grid to keep its stats, right click another to compare. Esc leaves it, or press this button again.";
            string autopilotAbout = "Your ship flies the racing line (your best lap on this course, or the ghost you race against) on its own, round and round, joining it where it's nearest. "
                + "Its speed is Settings > Race > Autopilot speed (100% = as fast as that lap). Laps flown by the autopilot aren't timed. Press the key or the button again to take over. "
                + "Get within 500 m of the line first. It keeps flying when you switch to spectator, the orbit camera or a cinematic, so you can film it. Only in single player or when hosting.";
            Control ToolButton(string words, IBrush brush, string about, Action click)
            {
                var tool = SlimButton(words, brush, click);
                tool.HorizontalAlignment = HorizontalAlignment.Stretch;
                tool.PointerEntered += (_, __) => { if (description != null) description.Text = about; };
                return tool;
            }
            var analyserButton = ToolButton(p.AnalyserOpen ? "Ship analyser: close" : "Ship analyser", p.AnalyserOpen ? Brush(HudColor.Purple) : White, analyserAbout,
                () => commands.Enqueue(new MenuCommand(MenuAction.ShipAnalyser)));
            string autopilotWords = (p.AutopilotOn ? "Autopilot: take over" : p.AutopilotReady ? "Autopilot: start" : "Autopilot: no line yet")
                + (string.IsNullOrEmpty(p.AutopilotKey) ? "" : "   (" + p.AutopilotKey + ")");
            var autopilotButton = ToolButton(autopilotWords, p.AutopilotOn ? Brush(HudColor.Purple) : p.AutopilotReady ? White : Grey, autopilotAbout,
                () => { if (p.AutopilotOn || p.AutopilotReady) commands.Enqueue(new MenuCommand(MenuAction.Autopilot)); });
            autopilotButton.Margin = new Thickness(8 * s, 0, 0, 0);
            var tools = new Grid();
            tools.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            tools.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            Grid.SetColumn(analyserButton, 0); tools.Children.Add(analyserButton);
            Grid.SetColumn(autopilotButton, 1); tools.Children.Add(autopilotButton);
            raceRows.Children.Add(TightRow(OptionRow("Tools",
                "Ship analyser: look at any ship for its mass, thrust, power, cargo and RPI. Autopilot: your ship flies the racing line on its own. Point at each button for more.", tools)));

            // Race spotlight: export the race just flown; show an exported race off on its ship (in any world).
            // (With Settings > Menu > Filming off only the export and its folder show.)
            raceRows.Children.Add(Section("Race spotlight"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            raceRows.Children.Add(OptionRow("Export last race" + (p.LastRaceLine != null ? "  (" + p.LastRaceLine + ")" : ""),
                "Saves the race you just finished (times for every lap, penalties and the race stats) to a file in the race spotlight folder. Take the file to another world or another PC to show the race off on the ship that flew it.",
                Widen(ValueButton(p.CanExportRace ? "Export" : "No race finished yet", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.SpotlightExport))))));
            if (!GateSettings.FilmingOn)
                raceRows.Children.Add(OptionRow("Race files folder",
                    "Exported races are kept here. The rest of the race spotlight (showing a race off on its ship, the tour, the leaderboard) is under Settings > Menu > Filming.",
                    Widen(ValueButton("Open folder", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.SpotlightOpenFolder))))));
            if (GateSettings.FilmingOn)
            {
            // The pages: which show, in what order, for how long. A drop-down: the summary, and the full list when opened.
            int pagesOn = p.SpotPages.Count(x => x.On);
            raceRows.Children.Add(OptionRow($"Pages  ({pagesOn} of {p.SpotPages.Count} on)",
                "Which spotlight pages show, in what order, and for how long. Open the list to switch pages on or off, move them up or down, give one its own time (0 = its usual time) or reset them all.",
                Widen(WithDropArrow(ValueButton(spotPagesOpen ? "Close the list" : "Change the pages", spotPagesOpen ? Brush(HudColor.Yellow) : White, 0, () =>
                {
                    spotPagesOpen = !spotPagesOpen;
                    FillRace(p);
                }), spotPagesOpen, spotPagesOpen ? Brush(HudColor.Yellow) : White))));
            if (!spotPagesOpen)
            {
                var order = Text(string.Join("  >  ", p.SpotPages.Where(x => x.On).Select(x => x.Name)), Grey);
                order.TextWrapping = TextWrapping.Wrap;
                order.Margin = new Thickness(36 * s, 0, 24 * s, 8 * s);
                SoftFontSize(order, 15 * s);
                raceRows.Children.Add(order);
            }
            else
            {
                raceRows.Children.Add(SpotPageList(p));
            }
            // The leaderboard page: which ships are on it, and one row a ship or every race.
            int boardHidden = p.SpotBoardShips.Count(x => x.Hidden);
            raceRows.Children.Add(OptionRow($"Leaderboard  ({p.SpotBoardShips.Count} ship{(p.SpotBoardShips.Count == 1 ? "" : "s")}{(boardHidden > 0 ? $", {boardHidden} hidden" : "")})",
                "The spotlight's Leaderboard page. Open the list to leave ships off the board, and to choose between one row a ship (its fastest race: a ship that beats its own time moves up) or a row for every race.",
                Widen(WithDropArrow(ValueButton(spotBoardOpen ? "Close the list" : "Change the board", spotBoardOpen ? Brush(HudColor.Yellow) : White, 0, () =>
                {
                    spotBoardOpen = !spotBoardOpen;
                    FillRace(p);
                }), spotBoardOpen, spotBoardOpen ? Brush(HudColor.Yellow) : White))));
            if (spotBoardOpen) raceRows.Children.Add(SpotBoardList(p));
            raceRows.Children.Add(OptionRow("Spotlight tour",
"Goes through the ships in the order set under Leaderboard. For each one it sits you in the ship's cockpit, starts that race's spotlight with the autopilot flying the race and a cinematic filming it (the race's own, else the current one), and moves to the next ship a few seconds after the race ends. A race's ship is the grid named in its file; to use another, open that race's spotlight and left click the ship once (it's remembered). Ships hidden from the leaderboard are skipped. Ctrl+Shift+N starts the tour, and on the tour goes to the next ship straight away. Esc stops it.",
                Widen(ValueButton(p.SpotTourOn ? "Stop the tour" : "Start the tour", p.SpotTourOn ? Brush(HudColor.Purple) : White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.SpotTour))))));
            raceRows.Children.Add(OptionRow("Tour cinematic",
                "The cinematic that films every ship on the tour. Each race's own: the cinematic picked on each race below (or, where none is picked, the one open on the Cinematics tab). Pick one here and the whole tour is filmed with it, whatever the races have set. Click to go through your cinematics. "
                + "The podium at the end and the tour's other options are under Settings > Race spotlight.",
                Widen(ValueButton(string.IsNullOrEmpty(p.SpotTourCine) ? "Each race's own" : p.SpotTourCine, string.IsNullOrEmpty(p.SpotTourCine) ? Grey : Brush(HudColor.Purple), 0,
                    () => commands.Enqueue(new MenuCommand(MenuAction.SpotBoardSet, 0, 0, "*tourcine*"))))));
            raceRows.Children.Add(OptionRow("Race files folder",
                "Exported races are kept here. To import one from someone else, put their file (.spotlight.json) in this folder: it shows in the list below within a few seconds.",
                Widen(ValueButton("Open folder", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.SpotlightOpenFolder))))));
            if (p.Spotlights.Count == 0)
            {
                var noRaces = Text("No exported races yet. Finish a race and press Export, or put a race file in the folder.", Grey);
                noRaces.TextWrapping = TextWrapping.Wrap;
                noRaces.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
                SoftFontSize(noRaces, 16 * s);
                raceRows.Children.Add(noRaces);
            }
            foreach (var race in p.Spotlights)
            {
                string path = race.Path;
                bool showing = p.SpotlightOpen == path;
                (bool Race, string Cine) auto = p.SpotAuto.TryGetValue(path, out var autoNow) ? autoNow : (false, "");
                bool cineOpen = spotCineOpen == path;
                var spotButton = ValueButton(showing ? "Close spotlight" : "Spotlight", showing ? Brush(HudColor.Purple) : White, 0,
                    () => commands.Enqueue(new MenuCommand(showing ? MenuAction.SpotlightStop : MenuAction.SpotlightStart, text: path)));
                var raceButton = ValueButton(auto.Race ? "Auto race: on" : "Auto race: off", auto.Race ? Brush(HudColor.Green) : Grey, 0,
                    () => commands.Enqueue(new MenuCommand(MenuAction.SpotAutoSet, text: "Race|" + path)));
                IBrush cineBrush = cineOpen ? Brush(HudColor.Yellow) : string.IsNullOrEmpty(auto.Item2) ? Grey : Brush(HudColor.Purple);
                var cineButton = WithDropArrow(ValueButton(string.IsNullOrEmpty(auto.Item2) ? "No cinematic" : auto.Item2, cineBrush, 0, () =>
                {
                    spotCineOpen = cineOpen ? null : path;
                    FillRace(p);
                }), cineOpen, cineBrush);
                // Compare: this race head to head with the one in the spotlight (a page of its own, and its ghost alongside in the auto race).
                bool comparing = p.SpotCompare == path;
                var compareButton = ValueButton(comparing ? "Comparing" : "Compare", comparing ? Brush(HudColor.Orange) : Grey, 0,
                    () => commands.Enqueue(new MenuCommand(MenuAction.SpotCompareSet, text: path)));
                SmallTip(compareButton, comparing ? "Stop comparing with this race" : "Compare the spotlight's race with this one: a Head to head page, and this race's ghost flying alongside in the auto race (same course only)");
                // The four buttons a size smaller (the game's own buttons, scaled down whole: see SlimButton), so a
                // race is one slim line and the list is about half as tall.
                var trioButtons = new Control[] { spotButton, raceButton, cineButton, compareButton };
                var spotTrio = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), MinWidth = 260 };
                for (int tb = 0; tb < trioButtons.Length; tb++)
                {
                    trioButtons[tb].HorizontalAlignment = HorizontalAlignment.Stretch;
                    var slimmed = new LayoutTransformControl
                    {
                        Child = trioButtons[tb], LayoutTransform = new ScaleTransform(SlimScale, SlimScale),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(tb == 0 ? 0 : 6 * s, 0, 0, 0),
                    };
                    Grid.SetColumn(slimmed, tb);
                    spotTrio.Children.Add(slimmed);
                }
                var spotRaceRow = OptionRow(race.Name,
                    $"{race.Name}: {race.Info}.\n\nSpotlight closes the menu, hides the race HUD and puts the spotlight on the ship you're sitting in (left click another grid to move it): a panel shows this race's results, then its stats, then the ship's own stats, one after another. Esc leaves it.\n\n"
                    + "Auto race: when the spotlight starts, your ship is put at the start of this race's line and the autopilot flies the whole race, lap after lap (the course it was flown on has to be loaded; races exported before this version have no line).\n\n"
                    + "Cinematic: pick one to play on the ship as soon as the spotlight starts, to film the race again.\n\n"
                    + "Compare: pick a second race (on the same course) to set against the one in the spotlight: a Head to head page shows them lap by lap, and in the auto race its ghost flies alongside in orange.",
                    spotTrio);
                // (The ship on one line and its course under it, smaller: long names wrapped at odd places before.)
                if (spotRaceRow.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock spotRaceLabel)
                {
                    var spotNameParts = System.Text.RegularExpressions.Regex.Split(race.Name ?? "", @"\s+-\s+", System.Text.RegularExpressions.RegexOptions.None);
                    if (spotNameParts.Length >= 2)
                    {
                        spotRaceLabel.Text = null;
                        spotRaceLabel.Inlines = new Avalonia.Controls.Documents.InlineCollection
                        {
                            new Avalonia.Controls.Documents.Run(spotNameParts[0]) { FontWeight = FontWeight.Bold },
                            new Avalonia.Controls.Documents.LineBreak(),
                            new Avalonia.Controls.Documents.Run(string.Join(" - ", spotNameParts.Skip(1))) { FontSize = 12 * s, Foreground = Grey },
                        };
                    }
                    spotRaceLabel.FontSize = 16 * s;
                    spotRaceLabel.LineHeight = 19 * s;
                    spotRaceLabel.TextWrapping = TextWrapping.NoWrap;
                    spotRaceLabel.TextTrimming = TextTrimming.CharacterEllipsis;
                }
                // (Compact, with a faint line under each race so the list reads as rows.)
                spotRaceRow.MinHeight = 46 * s;
                spotRaceRow.Margin = new Thickness(0, 0, 24 * s, 0);
                var spotRowLine = new Border { Height = 1, Background = RowEdge, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16 * s, 0, 0, 0), IsHitTestVisible = false };
                Grid.SetColumnSpan(spotRowLine, 3);
                spotRaceRow.Children.Add(spotRowLine);
                raceRows.Children.Add(spotRaceRow);
                if (cineOpen)
                {
                    // The cinematics to pick from, straight under the row.
                    var choicesList = new StackPanel();
                    var cineChoices = new List<string> { "" };
                    cineChoices.AddRange(p.Cinematics);
                    if (p.Cinematics.Count == 0)
                    {
                        var none = Text("No cinematics yet: make one on the Cinematics tab.", Grey);
                        none.Margin = new Thickness(12 * s, 6 * s, 12 * s, 6 * s);
                        SoftFontSize(none, 15 * s);
                        choicesList.Children.Add(none);
                    }
                    IBrush optBack = Argb(255, 14, 34, 48);
                    foreach (string choice in cineChoices)
                    {
                        string pick = choice;
                        bool current = (auto.Item2 ?? "") == pick;
                        var optText = Text(pick == "" ? "No cinematic" : pick, current ? Brush(HudColor.Yellow) : White);
                        SoftFontSize(optText, 15 * s);
                        var opt = new Border
                        {
                            Background = optBack, Padding = new Thickness(10 * s, 6 * s, 10 * s, 6 * s), Child = optText,
                            BorderBrush = current ? Brush(HudColor.Yellow) : optBack, BorderThickness = new Thickness(3 * s, 0, 0, 0),
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        };
                        opt.PointerPressed += (_, e) =>
                        {
                            e.Handled = true;
                            spotCineOpen = null;
                            commands.Enqueue(new MenuCommand(MenuAction.SpotAutoSet, text: "Cine|" + pick + "|" + path));
                        };
                        choicesList.Children.Add(opt);
                    }
                    raceRows.Children.Add(new Border
                    {
                        Background = optBack, BorderBrush = Brush(HudColor.Yellow), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(0, 0, 4 * s, 4 * s), Margin = new Thickness(0, 0, 48 * s, 8 * s), Width = 260 * s,
                        HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(0, 2 * s, 0, 4 * s), Child = choicesList,
                    });
                }
            }
            }

            raceRows.Children.Add(Section("Ghost and racing line"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var about = Text("Pick whose lap you race against: the ghost, the racing line and the live gap all follow it. The 2nd ghost (Records tab) can fly alongside it.", Grey);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
            SoftFontSize(about, 16 * s);
            raceRows.Children.Add(about);

            bool other = p.Ships.Any(x => x.Racing) || p.Ghosts.Any(x => x.Active) || p.RacingPossible || p.RacingBestRace || p.SpotlightRacing != null;
            bool racingBestRace = p.RacingBestRace && !p.Ships.Any(x => x.Racing) && !p.Ghosts.Any(x => x.Active);
            bool racingPossible = p.RacingPossible && !p.Ships.Any(x => x.Racing) && !p.Ghosts.Any(x => x.Active);

            // A slim list to pick from: a dot (filled for the one raced), the name, a small tag for what it is, and
            // its time lined up on the right. Click a row to race it; hover for what it means.
            var list = new StackPanel { Margin = new Thickness(24 * s, 0, 24 * s, 6 * s) };
            void Choice(string name, string tag, string time, bool racing, bool ready, string tip, Action pick)
            {
                var g = new Grid { Height = 40 * s };
                foreach (var w in new[] { 30.0, 0, 90, 150 })
                    g.ColumnDefinitions.Add(new ColumnDefinition(w > 0 ? new GridLength(w * s) : new GridLength(1, GridUnitType.Star)));
                var dot = new Border
                {
                    Width = 12 * s, Height = 12 * s, CornerRadius = new CornerRadius(6 * s),
                    BorderBrush = racing ? Brush(HudColor.Purple) : Grey, BorderThickness = new Thickness(2 * s),
                    Background = racing ? Brush(HudColor.Purple) : Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                };
                g.Children.Add(dot);
                var title = Text(name, racing ? Brush(HudColor.Purple) : ready ? White : Grey);
                SoftFontSize(title, 18 * s);
                title.VerticalAlignment = VerticalAlignment.Center;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                if (racing) title.FontWeight = FontWeight.Bold;
                Grid.SetColumn(title, 1); g.Children.Add(title);
                var tagText = Text(tag, Grey);
                SoftFontSize(tagText, 12 * s);
                tagText.VerticalAlignment = VerticalAlignment.Center;
                tagText.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(tagText, 2); g.Children.Add(tagText);
                var timeText = Text(time ?? (ready ? "" : "not yet"), racing ? Brush(HudColor.Purple) : time != null ? Brush(HudColor.Yellow) : Grey);
                SoftFontSize(timeText, 18 * s);
                timeText.VerticalAlignment = VerticalAlignment.Center;
                timeText.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(timeText, 3); g.Children.Add(timeText);
                var row = new Border
                {
                    Child = g, Padding = new Thickness(10 * s, 0, 12 * s, 0), CornerRadius = new CornerRadius(4 * s),
                    Background = racing ? Argb(255, 30, 22, 48) : Argb(1, 0, 0, 0),   // (a touch of fill so the whole row can be clicked)
                    BorderBrush = racing ? Brush(HudColor.Purple.WithAlpha(150)) : Brushes.Transparent, BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 1 * s, 0, 1 * s),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                SmallTip(row, tip);
                row.PointerPressed += (_, e) => { if (!racing && ready) pick(); e.Handled = true; };
                list.Children.Add(row);
                // A hairline between rows.
                list.Children.Add(new Border { Height = 1, Background = Argb(60, 120, 150, 170), Margin = new Thickness(10 * s, 0, 10 * s, 0) });
            }

            string courseBest = p.Ships.Where(x => x.Fastest).Select(x => x.BestLap).FirstOrDefault();
            Choice("Course best", "LAP", courseBest, !other, p.CourseHasBest,
                "Your fastest lap on this course in any ship (the normal choice).",
                () => commands.Enqueue(new MenuCommand(MenuAction.RaceCourseBest)));
            string recordTime = p.BestRaceLine, recordTag = "RACE";
            if (recordTime != null && recordTime.Contains(", ")) { var bits = recordTime.Split(new[] { ", " }, 2, StringSplitOptions.None); recordTag = bits[0].ToUpperInvariant(); recordTime = bits[1]; }
            Choice("Course record race", recordTag, recordTime, racingBestRace, p.BestRaceLine != null,
                "The fastest whole race on this course for the number of laps set, lap for lap: on lap 1 the ghost flies that race's lap 1, on lap 2 its lap 2, and so on, so you race the record race itself (its slow first lap and all), not your best lap over and over. Kept from the next record race you set (races before this version have no path kept).",
                () => commands.Enqueue(new MenuCommand(MenuAction.RaceBestRace)));
            Choice("Possible lap (best sectors)", "LAP", p.PossibleLine, racingPossible, p.PossibleLine != null,
                "Your best sector from start to CP 1, CP 1 to CP 2 and so on, wherever you flew it, joined up into one lap: the ghost and racing line show the lap you'd do if every sector went as well as your best. It flies that lap on every lap of the race, so over a whole race it's your possible race. A sector whose best was set before paths were kept uses your best lap's path for that sector until you beat it.",
                () => commands.Enqueue(new MenuCommand(MenuAction.RacePossible)));
            foreach (var sh in p.Ships.Where(x => x.HasReplay))
            {
                string name = sh.Name;
                Choice(sh.Name, "SHIP", sh.BestLap, sh.Racing, true,
                    $"{sh.Name}'s best lap on this course ({sh.BestLap}{(string.IsNullOrEmpty(sh.Gap) || sh.Gap == "fastest" ? "" : ", " + sh.Gap)}).",
                    () => commands.Enqueue(new MenuCommand(MenuAction.RaceShip, text: name)));
            }
            foreach (var gr in p.Ghosts.Where(x => x.Fits))
            {
                string path = gr.Path;
                // The file's label ends with its time: shown in the time column.
                string label = gr.Label ?? "", time = null;
                int cut = label.LastIndexOf(' ');
                if (cut > 0 && label.Substring(cut + 1).Contains(':')) { time = label.Substring(cut + 1); label = label.Substring(0, cut); }
                Choice(label, "FILE", time, gr.Active, true, $"Ghost file: {gr.Label} ({gr.Detail}).",
                    () => commands.Enqueue(new MenuCommand(MenuAction.RaceGhostFile, text: path)));
            }
            raceRows.Children.Add(list);

            if (!p.Ships.Any(x => x.HasReplay) && !p.Ghosts.Any(x => x.Fits))
            {
                var none = Text("Finish a race in a ship and its best lap can be picked here; ghost files from other players (Records tab) show here too.", Grey);
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
                SoftFontSize(none, 16 * s);
                raceRows.Children.Add(none);
            }
        }

        // ---------------------------------------------------------------- Info tab

        /// <summary>The pages of the guide, each its own topic under Racing Display in the Help screen (F1).</summary>
        /// <summary>The guide's pages, sorted by what you're doing. Each page is a topic in the Help screen (F1).</summary>
        internal static readonly (string Group, string[] Pages)[] GuideGroups =
        {
            ("Start here", new[] { "What's new", "Getting started", "Menu and settings", "Keys" }),
            ("Racing", new[] { "Timer and colours", "Penalties", "HUD and markers", "Speedometer and flight aids", "Minimap and Race Map", "Ghosts and replays", "Results and records", "How it's worked out" }),
            ("Building", new[] { "Building courses", "Ship analyser" }),
            ("Filming", new[] { "Cameras and cinematics", "Showcase and video", "Autopilot" }),
        };

        internal static readonly string[] GuidePages = GuideGroups.SelectMany(g => g.Pages).ToArray();

        /// <summary>
        /// The guide for the Help screen (F1 > Racing Display): one page (pageName), or with search set, every
        /// entry on any page whose text contains all the words searched for, under its page and section names.
        /// </summary>
        internal static StackPanel BuildGuide(double s, string pageName, string search = null) => BuildInfo(s, pageName, search);

        private static readonly Dictionary<string, string> GuidePageNotes = new Dictionary<string, string>
        {
            ["What's new"] = "The latest additions, newest first.",
            ["Getting started"] = "Set up a course and fly your first race: the menu, edit mode, checkpoints, countdown and practice laps.",
            ["Menu and settings"] = "The Racing Display menu's tabs, settings, presets and the status strip.",
            ["Keys"] = "Every shortcut, as you have them set now.",
            ["Timer and colours"] = "The timer panel, the gap to your best, the checkpoint dots, and what each colour means.",
            ["Penalties"] = "Missed checkpoints, crashes, crash immunity and how penalties show in the lap list.",
            ["HUD and markers"] = "Checkpoint markers, the edge arrow, the lap list and splits, race messages and the race-HUD-only mode.",
            ["Speedometer and flight aids"] = "The speed dial with its resource bars and indicators, flight keys, roll and pitch, flip ship and the reset teleport.",
            ["Minimap and Race Map"] = "The minimap, the big Race Map and its switches, and named sectors.",
            ["Ghosts and replays"] = "The ghost and racing line, a 2nd ghost, ghost files and best-lap replays.",
            ["Results and records"] = "The speed graph, race stats and results after the finish, ship times, records and the race history.",
            ["How it's worked out"] = "How the gaps, possible lap, time at top speed, consistency, G-forces, penalties and graphs are worked out.",
            ["Building courses"] = "Course edit mode and its tool wheel, building a course from grid and antenna names, and how courses are saved with the world and shared.",
            ["Ship analyser"] = "Looking at a ship to see its mass, thrust and speed, comparing ships, and RPI classes.",
            ["Cameras and cinematics"] = "The orbit camera, cinematics (shots, effects, the timeline, keyframes) and switching them at gates.",
            ["Showcase and video"] = "Race spotlight: showing a race off to viewers, its pages and auto race.",
            ["Autopilot"] = "The autopilot that flies the racing line by itself.",
        };

        /// <summary>The start of the guide in the Help screen: what's on each page, by group (or one group's pages).</summary>
        internal static StackPanel BuildGuideIndex(double s, string onlyGroup = null)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 0, 12 * s) };
            var intro = Text(onlyGroup == null ? "Type above to search the whole guide, or pick one of the topics below Racing Display on the left:" : "Pick one of these topics on the left:", White);
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(24 * s, 0, 24 * s, 8 * s);
            SoftFontSize(intro, 18 * s);
            page.Children.Add(intro);
            foreach (var (group, pages) in GuideGroups)
            {
                if (onlyGroup != null && group != onlyGroup) continue;
                page.Children.Add(SectionHeader(group, s));
                page.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 6 * s) });
                foreach (string pageTitle in pages)
                {
                    var g = new Grid { Margin = new Thickness(24 * s, 4 * s, 24 * s, 4 * s) };
                    g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(290 * s, GridUnitType.Pixel)));
                    g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                    var name = Text(pageTitle, Brush(HudColor.Cyan));
                    name.FontWeight = FontWeight.Bold;
                    name.VerticalAlignment = VerticalAlignment.Top;
                    SoftFontSize(name, 18 * s);
                    var note = Text(GuidePageNotes.TryGetValue(pageTitle, out var n) ? n : "", White);
                    note.TextWrapping = TextWrapping.Wrap;
                    SoftFontSize(note, 18 * s);
                    Grid.SetColumn(note, 1);
                    g.Children.Add(name);
                    g.Children.Add(note);
                    page.Children.Add(g);
                }
            }
            return page;
        }

        // ---- Guide text, laid out: the first sentence stands out, the rest follow as short points under it; keys
        // are drawn as key boxes (showing the key as it's set now, not the default); "Settings > HUD > Speedometer"
        // is a link that opens the Settings tab at that setting.
        private static readonly System.Text.RegularExpressions.Regex GuideKey = new System.Text.RegularExpressions.Regex(
            @"(?:(?:Ctrl|Shift|Alt)\+)+(?:[A-Za-z0-9]{1,6}|\[|\]|')|\bEsc\b|\bF1\b");
        private static string[] guideSections;   // the Settings tab's section names, longest first

        /// <summary>The text cut into sentences (not inside brackets, not after "e.g." and the like).</summary>
        private static List<string> Sentences(string text)
        {
            var list = new List<string>();
            int depth = 0, from = 0;
            for (int i = 0; i < text.Length - 2; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if ((c == '.' || c == '?' || c == '!') && depth == 0 && text[i + 1] == ' ' && (char.IsUpper(text[i + 2]) || char.IsDigit(text[i + 2])))
                {
                    if (c == '.' && i >= 3 && (text.Substring(i - 3, 3) == "e.g" || text.Substring(i - 3, 3) == "i.e")) continue;
                    if (c == '.' && i >= 1 && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 2]) && i - from < 4) continue;   // "1. 2." lists
                    list.Add(text.Substring(from, i + 1 - from).Trim());
                    from = i + 2;
                }
            }
            if (from < text.Length) list.Add(text.Substring(from).Trim());
            return list;
        }

        /// <summary>Default shortcut text ("Ctrl+R") to the same action's shortcut as it's set now.</summary>
        private static Dictionary<string, string> CurrentKeys()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in KeyBindings.Actions)
            {
                string was = KeyBindings.Default(key).ToString(), now = KeyBindings.CurrentOrDefault(key).ToString();
                if (was != now && !map.ContainsKey(was)) map[was] = now;
            }
            return map;
        }

        private static Control GuideKeyCap(string key, double s)
        {
            var t = Text(key, White);
            t.FontWeight = FontWeight.Bold;
            SoftFontSize(t, 14 * s);
            return new Border
            {
                Child = t, BorderBrush = Argb(255, 150, 164, 176), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3 * s),
                Padding = new Thickness(5 * s, 0, 5 * s, 1 * s), Margin = new Thickness(1 * s, 0, 1 * s, 0),
            };
        }

        /// <summary>The setting a "Settings > Section > Name..." in the text names (at nameAt), or null: the longest name that fits.</summary>
        private static (string Search, int Length) GuideSettingAt(string text, int nameAt, string section)
        {
            string best = null;
            foreach (var def in SettingDefs.All)
            {
                if (!string.Equals(def.Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                string name = def.Name;
                int paren = name.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) name = name.Substring(0, paren);
                if (name.Length > (best?.Length ?? 0) && string.Compare(text, nameAt, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0) best = name;
            }
            return best == null ? (null, 0) : (best, best.Length);
        }

        /// <summary>Opens the Settings tab with the search box set (from a link in the guide).</summary>
        internal static string PendingSettingsSearch;
        private static void OpenSettingsAt(string search)
        {
            try
            {
                PendingSettingsSearch = search;
                var menu = Instance;
                if (menu.IsOpen) menu.CloseConfirm();   // (the guide over the menu)
                if (!TerminalTab.Added || !TerminalTab.OpenPage("Settings")) PluginLog.Info("Guide link: couldn't open the Settings tab (search: " + search + ")");
                else if (menu.IsOpen && menu.OnSettingsTab && menu.lastSettingsPanel != null) menu.FillSettings(menu.lastSettingsPanel);
            }
            catch (Exception e) { PluginLog.Error("Guide link: couldn't open the settings", e); }
        }

        private static void AddGuideInlines(Avalonia.Controls.Documents.InlineCollection inlines, string text, IBrush brush, FontWeight weight, double s, Dictionary<string, string> keys)
        {
            // Find the links and keys, in order, not overlapping.
            var spans = new List<(int At, int Length, string Key, string Setting)>();
            guideSections ??= SettingDefs.All.Select(d => d.Section).Distinct().OrderByDescending(x => x.Length).ToArray();
            const string lead = "Settings > ";
            for (int from = text.IndexOf(lead, StringComparison.Ordinal); from >= 0; from = text.IndexOf(lead, from + lead.Length, StringComparison.Ordinal))
            {
                int end = from + lead.Length;
                string section = guideSections.FirstOrDefault(x => string.Compare(text, end, x, 0, x.Length, StringComparison.OrdinalIgnoreCase) == 0);
                if (section == null) continue;
                end += section.Length;
                string search = section;
                if (string.Compare(text, end, " > ", 0, 3, StringComparison.Ordinal) == 0)
                {
                    var (name, len) = GuideSettingAt(text, end + 3, section);
                    if (name != null) { search = name; end += 3 + len; }
                }
                spans.Add((from, end - from, null, search));
            }
            foreach (System.Text.RegularExpressions.Match m in GuideKey.Matches(text))
                if (!spans.Any(x => m.Index < x.At + x.Length && x.At < m.Index + m.Length)) spans.Add((m.Index, m.Length, m.Value, null));
            spans.Sort((x, y) => x.At.CompareTo(y.At));
            int at = 0;
            void Plain(string part)
            {
                if (part.Length > 0) inlines.Add(new Avalonia.Controls.Documents.Run(part) { Foreground = brush, FontWeight = weight });
            }
            foreach (var span in spans)
            {
                if (span.At < at) continue;
                Plain(text.Substring(at, span.At - at));
                string shown = text.Substring(span.At, span.Length);
                Control c;
                if (span.Key != null) c = GuideKeyCap(keys != null && keys.TryGetValue(shown, out var now) ? now : shown, s);
                else
                {
                    var link = Text(shown, Brush(HudColor.Cyan));
                    link.TextDecorations = TextDecorations.Underline;
                    SoftFontSize(link, 17 * s);
                    string search = span.Setting;
                    var hit = new Border { Child = link, Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                    hit.PointerPressed += (_, e) => { e.Handled = true; OpenSettingsAt(search); };
                    c = hit;
                }
                inlines.Add(new Avalonia.Controls.Documents.InlineUIContainer(c) { BaselineAlignment = BaselineAlignment.Center });
                at = span.At + span.Length;
            }
            Plain(text.Substring(at));
        }

        /// <summary>One guide entry's text: the first sentence bright, the rest as short points under it.</summary>
        private static Control GuideText(string text, double s, Dictionary<string, string> keys)
        {
            var parts = Sentences(text);
            var stack = new StackPanel { Spacing = 3 * s };
            for (int i = 0; i < parts.Count; i++)
            {
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
                SoftFontSize(t, (i == 0 ? 18 : 16) * s);
                var inlines = new Avalonia.Controls.Documents.InlineCollection();
                AddGuideInlines(inlines, parts[i], i == 0 ? White : Argb(255, 176, 188, 198), FontWeight.Normal, s, keys);
                t.Inlines = inlines;
                if (i == 0) { stack.Children.Add(t); continue; }
                // A point: a small dash, then the sentence.
                var dash = new Border { Width = 6 * s, Height = 2 * s, Background = Argb(255, 96, 112, 126), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 11 * s, 8 * s, 0) };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(t, 1);
                row.Children.Add(dash);
                row.Children.Add(t);
                stack.Children.Add(row);
            }
            return stack;
        }

        private static StackPanel BuildInfo(double s, string pageName, string search)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 0, 12 * s) };
            string[] words = string.IsNullOrWhiteSpace(search) ? null
                : search.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words == null) search = null;
            string currentPage = null, pendingSection = null, lastHeader = null;
            Dictionary<string, string> keysNow = null;
            try { keysNow = CurrentKeys(); } catch { }

            void Page(string name) { currentPage = name; pendingSection = null; }
            // A section's header is only added once one of its entries is shown.
            void Section(string title) => pendingSection = title;

            string SampleText(Control c) => c switch
            {
                TextBlock t => t.Text ?? "",
                Panel p => string.Join(" ", p.Children.Select(SampleText)),
                Decorator d when d.Child != null => SampleText(d.Child),
                _ => "",
            };

            // One line: a sample (icon / colour / word) on the left, the explanation on the right.
            void Line(Control sample, string text)
            {
                if (search != null)
                {
                    if (currentPage != null)
                    {
                        string all = ((sample != null ? SampleText(sample) : "") + " " + text + " " + pendingSection + " " + currentPage).ToLowerInvariant();
                        if (!words.All(w => all.Contains(w))) return;
                    }
                }
                else if (pageName != null && currentPage != pageName) return;
                bool allPages = search != null || pageName == null;   // page names go in the headers
                if (currentPage != null && (pendingSection != null || allPages))
                {
                    string head = allPages ? currentPage + (pendingSection != null ? " > " + pendingSection : "") : pendingSection;
                    if (head != lastHeader)
                    {
                        page.Children.Add(SectionHeader(head, s));
                        page.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 6 * s) });
                        lastHeader = head;
                    }
                }
                var g = new Grid { Margin = new Thickness(24 * s, 6 * s, 24 * s, 6 * s) };
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(190 * s, GridUnitType.Pixel)));
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                if (sample != null)
                {
                    sample.VerticalAlignment = VerticalAlignment.Top;
                    sample.HorizontalAlignment = HorizontalAlignment.Left;
                    sample.Margin = new Thickness(0, 3 * s, 0, 0);
                    Grid.SetColumn(sample, 0);
                    g.Children.Add(sample);
                }
                var t = GuideText(text, s, keysNow);
                Grid.SetColumn(t, 1);
                g.Children.Add(t);
                page.Children.Add(g);
            }

            TextBlock Word(string text, HudColor colour)
            {
                var t = Text(text, Brush(colour));
                t.FontWeight = FontWeight.Bold;
                SoftFontSize(t, 18 * s);
                return t;
            }
            Control Dot(HudColor fill, HudColor stroke, bool hollow) => new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 14 * s, Height = 14 * s, StrokeThickness = 2 * s,
                Fill = hollow ? Brushes.Transparent : Brush(fill), Stroke = Brush(stroke),
            };
            Control Cross() => new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                Width = 14 * s, Height = 14 * s, Stretch = Stretch.Fill,
                Stroke = Brush(HudColor.Red), StrokeThickness = 2.2 * s,
            };
            Control Arrow() => new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 30,15 L 0,30 L 7,15 Z"),
                Width = 22 * s, Height = 22 * s, Stretch = Stretch.Fill, Fill = Brush(HudColor.Orange),
            };
            Control Row(params Control[] items)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s };
                foreach (var c in items) { c.VerticalAlignment = VerticalAlignment.Center; sp.Children.Add(c); }
                return sp;
            }

            Page("What's new");
            Section("Latest");
            Line(Word("Air gates", HudColor.Orange), "Gates and checkpoints placed in open air are now drawn in the world, as rings or squares, with arrows running through them the way to fly. The one to fly through next is brightest, with guide rails leading in. See Building courses > Air gates.");
            Line(Word("Markers", HudColor.Orange), "The checkpoint marker is now a ring that faces you, and the next one pulses. Settings > Markers > Marker icon has the old diamond, a chevron and a beacon.");
            Line(Word("Gravity", HudColor.Cyan), "Gravity has its own panel in the bottom right corner, with arrows showing which way it pulls on the ship: natural, artificial and the two together. Settings > HUD > Gravity indicator.");
            Line(Word("Lights", HudColor.Yellow), "A small lamp beside the speed unit shows while the ship's lights are on.");
            Line(Word("Flip ship", HudColor.Yellow), "Ctrl+Shift+F lifts a ship that's stuck upside down and turns it level. Settings > Race > Flip ship: lift height sets how high, and Settings > Race > Flip ship: penalty in a race sets what it costs in a race.");
            Line(Word("Speedometer", HudColor.Orange), "The dial now has resource bars round it (power use, battery, hydrogen) and a side panel with power, parking brake, antenna and dampener indicators, plus a safe speed icon. Each can be hidden in Settings > HUD.");
            Line(Word("Race results", HudColor.Cyan), "Esc closes the results and race stats after a race. They come back at the end of the next race.");
            Line(Word("Compare", HudColor.Purple), "Two spotlight races can be compared head to head: Compare on a race in the Race spotlight list.");
            Line(Word("Averages", HudColor.White), "The Records tab shows each ship's average time on the course (AVG).");
            Line(Word("Guide", HudColor.Cyan), "This guide is sorted into groups, has a Keys page showing your own shortcuts, and its Settings links open the Settings tab at that setting.");
            Section("Earlier");
            Line(Word("Drill ships", HudColor.White), "The speedometer, roll and keys panels now show when a ship tool (drill, grinder, welder) is selected.");
            Line(Word("RPI", HudColor.Yellow), "A ship's RPI shows in the block placer, the build menu and the grid info panel.");
            Line(Word("Cinematics", HudColor.Purple), "Gate switches can happen before, at or after a gate, and the camera keeps its distance from the ship.");

            Page("Keys");
            {
                string group = null;
                foreach (var (key, name) in KeyBindings.Actions)
                {
                    string cat = KeyBindings.CategoryOf(key);
                    if (cat != group) { group = cat; Section(cat); }
                    string now = KeyBindings.CurrentOrDefault(key).ToString(), was = KeyBindings.Default(key).ToString();
                    Line(GuideKeyCap(now, s), name + (now != was ? " (default: " + was.Replace("+", " + ") + ")" : ""));
                }
                Section("Changing keys");
                Line(Word("Controls tab", HudColor.Cyan), "Click a key on the menu's Controls tab, then press the new combination. Default puts one back, and Reset all puts them all back.");
            }

            Page("Getting started");
            Section("Getting started");
            Line(Word("Menu", HudColor.Cyan), "Press G (the terminal: Character, Grid, Build ... Map) and pick the Racing Display tab after Map, or press Shift+J for its Race page. Its tabs (Race, Records, Courses, Settings, Controls) are the ones named below.");
            Line(Word("1. Edit mode", HudColor.Orange), "Press Ctrl+F (or Courses tab > Course edit mode). Aim at the ring or arch you've built and left click: it becomes the start/finish gate, fitted to the ring's opening. Right click one to remove it.");
            Line(Word("2. Checkpoints", HudColor.Orange), "Hold Shift+R, pick Checkpoint on the wheel, then left click each checkpoint ring in the order you'll fly them. For a point-to-point course pick Start gate and Finish gate instead of Start/Finish. Press Ctrl+F again when you're done.");
            Line(Word("3. Race", HudColor.Green), "Fly through the gate to start (the right way, towards the first checkpoint; going through backwards or in the spectator camera doesn't start a race; Settings > Race > Only start going forwards turns the backwards check off). Each pass through the gate completes a lap. Set the number of laps (and any practice laps) on the Race tab (Shift+J).");
            Line(Word("Countdown", HudColor.Yellow), "For a standing start, turn on Settings > Race > Countdown start (off by default), then stop near the gate and hold still: you get 3, 2, 1, GO! The clock starts when you fly through the gate.");
            Line(Word("Practice", HudColor.Yellow), "Practice laps are timed but don't count. The race starts when you finish the last one at the gate.");

            Page("Building courses");
            Section("Course edit mode");
            Line(Word("Course edit mode", HudColor.Orange), "Ctrl+F (or Courses tab) turns it on. It also switches the game's debug draw on, so the gate and checkpoint boxes show (it's put back when you leave edit mode). Aim at a grid and left click to set it as the start / finish gate or the next checkpoint (it's outlined while you aim); right click a gate or checkpoint to remove it. Hold Shift+R for the tool wheel (Start/Finish, Start gate, Finish gate, Checkpoint; the current one is tinted in its colour): the cursor appears and the view stays still; point at a tool and let go (or click one). Shift+T changes the placement mode, shown at the top of the timer panel: On a grid (aim at a ring and click), Pick blocks (like the game's partial copy: left click one corner block, then the opposite corner block on the same grid, and the box between them, both included, becomes the gate or checkpoint; it's outlined as you aim, and right click clears the first corner) or In the air (in open air ahead of you, no grid needed: see Air gates below). Shift+L shows or hides your best lap line. A course that comes back through the same ring (the other way) can use it as two checkpoints: click a ring that's already a checkpoint, then click it again to add it once more. Each pass through it counts as the next of its checkpoints in order, and it has one marker labelled with both numbers (CP 2 / 5). Ctrl+U / Ctrl+Shift+U undo and redo course edits. Gates, checkpoints and the route between them (white lines with arrows and leg lengths) are always shown while editing, and the COURSE panel lists the type, length, each leg and anything worth fixing (checkpoints very close together, a ring turned side-on to the route). With a separate finish gate the course is point-to-point: one run from the start gate to the finish gate.");
            Section("Air gates");
            Line(Word("Placing", HudColor.Orange), "With the placement mode on In the air (Shift+T), the gate or checkpoint is placed ahead of you, facing the way you're looking. The tag by the crosshair says how big it is and how far ahead. Ctrl+] / Ctrl+[ change its size, Alt+] / Alt+[ its height on its own (for a wide or tall gate), Alt+= / Alt+- how far ahead it is, and Shift+O switches between a ring and a square. A big gate is kept far enough away to stay in view. On a ring, only the inside of the ring counts.");
            Line(Word("In a race", HudColor.Orange), "Air gates are drawn in the world (Settings > Course editing > Show air gates). The one to fly through next is the brightest: guide rails with arrow heads run in from either side, rings drift up to it, a ring closes in on its middle, and a red cross shows if you're on the wrong side. Gates further on are fainter, ones you've flown turn green and flash as you go through. Arrows run through every gate still to fly, the way to fly it. The start and finish have a chequered rim.");
            Line(Word("Look", HudColor.Cyan), "Settings > Course editing > Air gate look: Lines (thin outlines), Glow (a bright rim with a soft halo) or Glow (game 3D UI), which draws through the layer the game uses for its build previews: thicker lines, a dashed ring turning round the next gate and a bright spot running round its rim. With that look the next gate also shows faintly through a hill.");
            Line(Word("Gates on grids", HudColor.Cyan), "Checkpoints and gates set on grids get the running arrows too. Courses tab > This course > Grid checkpoint glow decides whether they also get the glowing frame: it's the course designer's choice, saved with the course.");
            Section("Auto-build from names");
            Line(Word("How", HudColor.Cyan), "Name the grids you've built for your course, then Courses tab > Build > Auto-build. The plugin looks through every grid in the world (and the antennas on them) and builds the course from the names: the start / finish gate and the checkpoints in number order, each fitted to its ring opening (Settings > Fit to ring opening). It replaces the current gate and checkpoints.");
            Line(Word("Start / Finish", HudColor.Cyan), "A grid or antenna with \"Start\" or \"Finish\" anywhere in its name (Start, Finish, Start/Finish, Start gate...) becomes the start / finish gate. Only one is used, and the course is a circuit: add a separate finish gate afterwards in edit mode for a point-to-point course. If none is found the old gate is kept.");
            Line(Word("CP 1, CP 2 ...", HudColor.Orange), "Checkpoints are named with a number: CP1, CP 2, CP-3, Checkpoint 4, Ring 5, Gate 6, #7 or just 8 (1 to 999, capital letters don't matter). The name must be just that: \"CP 3 big ring\" isn't picked up. They're flown in number order; gaps are fine (1, 2, 5 become CP 1, CP 2, CP 3). If two share a number, the first one found is used.");
            Line(Word("Naming", HudColor.White), "Rename a grid in its terminal (the grid's name), or give an antenna on it the name: handy when one big structure holds several rings, each with its own antenna. A grid's first name that fits decides what it is. Antenna names also show as markers on the HUD: Courses tab > Turn all off hides them.");
            Line(Word("Then", HudColor.Green), "Check the route in edit mode (Ctrl+F), fix anything with the tools, save it as a named course if you like, then save the game: courses are kept in the world's own save.");

            Section("Saving and sharing");
            Line(Word("Courses", HudColor.White), "The Courses tab saves and loads courses, changes the checkpoint order, builds a course from grids or antennas named Start / CP1 / CP2..., and can switch every antenna in the world off or on. Clear removes this world's gates, checkpoints and sectors (saved courses stay). Rename a saved course by typing the new name in Save as and pressing Rename next to it.");
            Line(Word("World save", HudColor.Cyan), "Courses belong to the world they're made in. They're written into the world's save folder (RaceDisplayCourses.json) every time you save the game, and loaded with the world, so they never show up in other worlds. Publish the world and players who download it, and have this plugin, get the course set up automatically (a new world started from the Workshop screen gets the course from that Workshop item, matched by its Workshop id).");
            Page("Timer and colours");
            Section("Timer panel");
            Line(Word("LAP 2 / 3", HudColor.White), "The lap you're on, and checkpoints reached this lap. The big number is this lap's time; TOTAL underneath is the race time so far, with penalties.");
            Line(Row(Word("-1.204", HudColor.Green), Word("+0.380", HudColor.Red)), "Gap to your best lap on this course: green means ahead of it, red means behind. Once you've set a best lap it updates all the time as you fly; before that, at each checkpoint.");
            Line(Row(Dot(HudColor.Green, HudColor.Green, false), Dot(HudColor.Orange, HudColor.White, false), Dot(HudColor.Grey, HudColor.Grey, true), Dot(HudColor.Red, HudColor.Red, true)),
                "Checkpoint dots: green = flown through, orange = the next one, grey = still to come, red ring = missed.");
            Line(Word("BEST LAP", HudColor.Yellow), "Left of the timer: your best lap on this course. POSSIBLE under it (purple) is your best sectors added up: the lap you'd do if every part went as well as your best.");
            Line(Word("BEST RACE", HudColor.Yellow), "Right of the timer: your best whole race on this course at the current number of laps. POSSIBLE under it (purple) is the possible lap times the number of laps: a whole race of perfect laps.");

            Section("Colours");
            Line(Word("Purple", HudColor.Purple), "Best ever on this course (all-time record), for a checkpoint time, a sector or a lap.");
            Line(Word("Green", HudColor.Green), "Best in this race, a good thing (ahead, clean race, new best lap), or a checkpoint flown through.");
            Line(Word("White", HudColor.White), "A normal time, slower than your best in this race.");
            Line(Word("Red", HudColor.Red), "Behind your best, a penalty, a crash, or a missed checkpoint.");
            Line(Word("Orange", HudColor.Orange), "The checkpoint to fly to next.");
            Line(Word("Yellow", HudColor.Yellow), "A sector slower than your best this race (sector colours), and lap times in the splits table, records and practice.");
            Line(Row(Dot(HudColor.Purple, HudColor.Purple, false), Dot(HudColor.Green, HudColor.Green, false), Dot(HudColor.Yellow, HudColor.Yellow, false)),
                "Sector colours: each checkpoint dot under the timer takes the colour of the sector you just flew, like F1 timing: purple = fastest ever on this course, green = your best this race, yellow = slower. Settings > Minimap > Sector colours.");

            Page("Penalties");
            Section("Penalties");
            Line(Row(Word("+5s", HudColor.Red), Dot(HudColor.Red, HudColor.Red, true)), "Missed checkpoint: you flew through a later checkpoint without this one. You get a red MISSED message straight away and its split shows MISSED. The penalty (5 s by default) is added to the lap.");
            Line(Row(Word("+5s", HudColor.Red), Cross()), "Crash: a sudden hard stop while going fast (20 m/s or more by default). The timer flashes red and the penalty is added to the lap.");
            Line(Word("Immunity", HudColor.Yellow), "After a crash penalty there's no other crash penalty for 5 s (Settings > Penalties > Crash immunity, up to 60 s, 0 = off), so bouncing off a wall or tumbling only counts once. With Crash immunity until back up to speed on, there's also no other crash penalty until you're back above Crash immunity speed (25 m/s by default).");
            Line(Word("+10s", HudColor.Red), "In the lap list, each lap shows its total penalty, then one ring per missed checkpoint and one cross per crash. Penalty sizes and crash sensitivity are on the Settings tab.");
            Line(Word("Sound", HudColor.Red), "Every penalty plays the penalty melody (Settings > Sound > Penalty melody, with its own volume). Turn it off for the old double beep. To use your own sound, put a Penalty.wav in %AppData%\\SpaceEngineers2\\AppData\\RaceDisplay and restart the game.");

            Page("HUD and markers");
            Section("Markers");
            Line(Word("CP 3  1.2 km", HudColor.Orange), "Each checkpoint has a label on screen. The one to fly to next is bigger, brighter and shows how far away it is. Ones you've flown through turn green and missed ones red. Settings > Markers > Only mark the next checkpoints shows markers on just the next few (Checkpoints marked ahead, 3 by default).");
            Line(Word("START / FINISH", HudColor.Cyan), "The start/finish gate (or START and a purple FINISH on a point-to-point course). It shows its distance when it's where you're heading (before the race and after the last checkpoint).");
            Line(Arrow(), "When the next checkpoint is off screen or behind you, an arrow at the edge of the screen points to it.");
            Line(Word("Icon", HudColor.Orange), "Settings > Markers > Marker icon picks what's drawn at each checkpoint: a ring that faces you, a diamond, a chevron or a tall beacon. The one to fly to next is bigger and moves. Thick marker lines draws them with thicker, smoother lines.");
            Section("Panels");
            Line(Word("Layout", HudColor.White), "Settings > HUD > HUD layout: Left (the default: the timer in the top left corner with the lap list under it; the minimap top right with best race and best lap beside it, and the splits under them), Right (the same, swapped round), or Centre (timer top centre).");
            Line(Word("LAPS", HudColor.White), "Lap list (left): every lap time, your best this race in green. After the finish the results appear under it.");
            Line(Word("SPLITS", HudColor.White), "Splits (right): for each checkpoint this lap, the gap to your best lap, the SECTOR time (from the previous checkpoint) and the lap time. After the finish it shows every lap side by side (sector times, or lap times if you switch that off), with top and average speed.");
            Section("Messages and HUD modes");
            Line(Word("Race messages", HudColor.Yellow), "GO!, checkpoints, laps, missed checkpoints and crashes drop down in a tab under the timer (long ones wrap onto a second line). Setup messages (gate placed and so on) show in the middle of the screen.");
            Line(Word("Race HUD only", HudColor.Cyan), "The game's HUD key (U) has an extra step: after the minimal HUD it hides the game's own HUD and flight HUD and shows only the race HUD; press U again to carry on to no HUD. Settings > HUD > Race HUD only turns it on or off directly.");
            Page("Speedometer and flight aids");
            Section("Speedometer");
            Line(Word("Resource bars", HudColor.Orange), "Rings round the outside of the dial show power use (USE), stored battery power (BAT) and hydrogen (H2), with their levels listed in the panel on the dial's side. A resource the ship doesn't have gets no ring, and its row in the panel is greyed with a dash. They come from the game's cockpit HUD, so they only show in a cockpit. Settings > HUD > Speedometer resource bars turns them off.");
            Line(Word("Indicators", HudColor.Orange), "Four tiles in the side panel: power, parking brake, antenna and inertia dampeners. A tile is orange while that thing is on. Each has its own switch in Settings > HUD.");
            Line(Word("Safe speed", HudColor.Green), "A small green gauge beside the speed unit shows while you're at or under the game's safe speed. Settings > HUD > Speedometer: safe speed indicator turns it off.");
            Line(Word("Lights", HudColor.Yellow), "A small lamp on the other side of the speed unit shows while the ship's lights are on.");
            Section("Flight aids");
            Line(Word("Gravity", HudColor.Cyan), "Settings > HUD > Gravity indicator: a panel in the bottom right corner with the strength of gravity in g and a dial of arrows showing which way it pulls on the ship, seen from behind it (straight down when you're level over a planet). The white arrow is the pull overall. With gravity generators about, a green arrow shows natural gravity and an orange one artificial, with each one's strength (N and A) underneath.");
            Line(Word("Speedometer", HudColor.White), "A round speed dial in the bottom left corner while you fly the course (its arc fills with speed and its scale grows to suit your ship) (Settings > HUD > Speedometer): with Race HUD only (the default, when the game's own speed display is hidden), always, or off. Under the speed: ABOVE GROUND, how high you are over the terrain straight below (not sea level), on or near a planet (Settings > HUD > Height above ground).");
            Line(Word("Gyro", HudColor.Cyan), "Race HUD only hides the game's HUD but keeps its flight gyro (the ring in the middle with the pitch angle) unless you turn off Settings > HUD > Race HUD only: keep the gyro. Gyro: target based switches it to the game's target based mode.");
            Line(Word("Flight keys", HudColor.Orange), "Settings > HUD > Flight keys (on unless you turn it off) shows Q W E, A S D, C and Space in the bottom right corner, lit orange while held.");
            Line(Word("Roll", HudColor.Cyan), "Settings > HUD > Roll indicator shows how far the ship is rolled from level, beside the flight keys: a bar that tilts with the ship over a level line, the angle (R = right wing down; yellow past 30 degrees, red past 60) and under it the roll speed in degrees a second. It needs gravity to know which way is up, so it only shows on or near a planet or moon.");
            Line(Word("Pitch", HudColor.Cyan), "Settings > HUD > Pitch indicator adds the pitch to the same box: how far the nose is above (UP) or below (DOWN) the horizon in degrees (yellow past 30, red past 60). Like the roll it needs gravity.");
            Line(Word("Which way is up", HudColor.Cyan), "Roll and pitch go by the way the cockpit block was placed when the ship was built, not by the seat inside it (on some cockpits the seat is turned, which made ships read upside down or nose to the sky). RaceDisplay.log notes once per ship where its facing came from.");
            Line(Word("Speed vector", HudColor.Cyan), "The white ring with wings (outlined in black) is where it's actually heading (its velocity), not where its nose points: in a drift they split apart. Settings > HUD > Speed vector marker.");
            Line(Word("Flip ship", HudColor.Yellow), "Crashed and stuck upside down? Ctrl+Shift+F lifts your ship straight up (5 m by default, Settings > Race > Flip ship: lift height) and turns it level with gravity, facing the way it was heading, then stops it. Needs gravity. During a race it costs what Settings > Race > Flip ship: penalty in a race says: nothing, a time penalty (5 s by default), the crash penalty, or a race reset. Uses the game's admin teleport, so it may not work on a server where you aren't an admin.");
            Line(Word("Reset teleport", HudColor.Yellow), "With Settings > Race > Reset race teleports to the start on, the reset race key (Ctrl+R) puts your ship back before the start: at your own start spot if you set one (Ctrl+T, or Courses tab > Start spot), otherwise 10 m before where you last came through the start gate, facing the way you were flying then (just behind the gate until you've started once). The ship is stopped, or comes out at Reset teleport exit speed if you set one (single player or hosting). Target based gyro is switched off and back on as it teleports, so it doesn't keep steering for where it was pointing before.");

            Page("Ship analyser");
            Section("Ship analyser");
            Line(Word("Analyser", HudColor.Yellow), "Race tab > Ship analyser > Open (the race HUD goes away; Esc brings it back, or press the button again: it says Close while the analyser is open), then look at a ship or any grid: a panel on the right shows its mass, size, blocks and PCU, thrust and acceleration each way (the grid's own forward, back, left, right, up and down), thrusters, gyroscopes, how much power and hydrogen it can generate and has stored (batteries and tanks), cargo space and how far its centre of mass is from its middle. On the grid itself a yellow cross marks the centre of mass, with arrows for its thrust each way (longer = more acceleration). Looking at nothing while in a ship shows your own ship.");
            Line(Word("Directions", HudColor.Yellow), "Forward, backward, left, right, up and down are taken from the cockpit block (the one you sit in on your own ship, else the first cockpit found), so the thrust each way and the arrows match the way the ship actually flies. The DIRECTIONS FROM row says whether a cockpit was found; without one the grid's own axes are used.");
            Line(Word("Acceleration", HudColor.Yellow), "The ship analyser works out how long the ship takes flat out from a standstill to a few speeds, and to the fastest it has gone in a race (from the history), with the distance it needs. No drag is counted. In gravity it also says how many times its weight the up thrust can hold.");
            Line(Word("Weight trim", HudColor.Yellow), "Under the RPI rows the analyser lists the heaviest block types (yellow = one of them fits in the spare weight), how much it can lose for its class minimum, and tips: which blocks to take off to get under the RPI budget, or which one saves the most RPI while staying over the minimum.");
            Line(Word("RPI", HudColor.Yellow), "Race Performance Index, for classes with a parts budget. RPI tab > RPI designer > Open. Manage classes: New class, Duplicate, Rename, Delete; Export saves a class file to the RPI classes folder (files there show under Import), Copy / Paste share a class through the clipboard. Set the budget and minimum weight; Lighter ships adds weight steps (e.g. from 14,000 kg: 275) so a ship under the minimum weight gets the smaller budget of the heaviest step it reaches. The list has every block in the game, one row per block with its sizes side by side: type a size's RPI straight into its box. Filter by category, With a cost or On last ship, search by name or size, and change the sort order. Click a block for its stats (mass, thrust, torque, power, storage) and to set the most a ship may have, or ban it. The Ship check line shows the last ship you analysed against the class as you edit. On the RPI tab pick the class in use; the Ship analyser then shows a ship's total RPI against its budget (red when over), the minimum weight, any broken block limits, and what the total is made of.");
            Line(Word("Compare", HudColor.Cyan), "Left click a grid to keep its stats (left click empty space to let go). Right click another grid to compare: its column is green where it's better (more thrust, less mass) and red where it's worse, and it's marked in blue on the grid. Right click empty space to stop comparing. The analyser is off while you race.");

            Page("Cameras and cinematics");
            Section("Orbit camera");
            Line(Word("Orbit camera", HudColor.Cyan), "A spectator camera that circles a ship: Cinematics tab > Orbit camera > Open, or its key (Controls tab, Ctrl+Shift+O unless changed). It starts on the grid you're looking at, or your own ship. Mouse swings round it, W / S move closer or further. Left click another grid to move to it; right click lets go, so you can fly free and left click a new one. Esc or the key again leaves it and gives you back what you were controlling.");
            Line(Word("Turn with ship", HudColor.Yellow), "The orbit lock key (Ctrl+Shift+K unless changed) swaps between a view that keeps its angle in the world and one that turns and rolls with the ship.");
            Line(Word("Follow and rotate", HudColor.Yellow), "Settings > Orbit camera: smooth follow (how loosely the camera trails the ship, 0 = fixed) and auto rotate (it circles the ship by itself after 3 seconds without mouse movement, 0 = off). Ctrl + mouse wheel speeds the auto rotate up or slows it down while the camera is on (past 0 it goes the other way). Race spotlight no longer starts it by itself: press its key during a spotlight to orbit the ship.");
            Section("Cinematics");
            Line(Word("Recording speed", HudColor.Purple), "Settings > Cinematics > Recording speed runs the autopilot and cinematics slower than real time (shot lengths, orbit turns and effects all together). At race speed the game can't load the terrain fast enough and the picture judders; record at 50% and speed the video up 2x afterwards (25% = 4x) for a smooth race-speed clip. The race spotlight's pages keep their own timing.");
            Line(Word("Cinematics", HudColor.Purple), "Camera timelines, on the Cinematics tab. Make one (type a name, New), close the menu and fly the camera to a view (the orbit camera is the easy way), then come back and drag a shot type onto the timeline where you want it (or click it to add it at the end): the shot takes the view the camera has. Play runs the shots on the ship you're looking at; Esc stops. While it plays the mouse looks around, and in an orbit shot it swings round the ship (W / S zoom).");
            Line(Word("Timeline", HudColor.Yellow), "Each shot is a block as wide as it is long, with a small sign for its type and a time ruler above. Click a block to pick it: everything about it shows in the inspector under the timeline (length, blend in, views, rotate, look at ship), with Preview, Duplicate (Ctrl+D) and Delete. Drag a block to move it; drag its right edge to change its length, or type the seconds and press Enter. Point at a block for a summary. The toolbar has Play / Stop, Loop, the whole length, Undo / Redo (Ctrl+Z / Ctrl+Y, every edit, until the game closes) and the zoom.");
            Line(Word("Playhead", HudColor.Yellow), "Click or drag on the time ruler to move the playhead (the white line): Play then starts from there (From start puts it back). While a cinematic plays the playhead follows it. Ctrl + mouse wheel over the timeline zooms in on the mouse, Shift + wheel scrolls along, Fit shows it all again.");
            Line(Word("Timeline length", HudColor.Yellow), "Toolbar > Timeline length: how long the whole cinematic runs (blank = as long as its shots). The timeline is drawn that long; if the shots end sooner the last one keeps going to the end, and anything past it is cut (a red line marks the end).");
            Line(Word("Shots", HudColor.Yellow), "Orbit circles the ship at its rotate speed. Chase is fixed to the ship and moves and rolls with it. Fixed camera stays put (Look at ship makes it turn to follow). Moving camera glides from its start view to its end view (Set start, fly on, Set end). For fixed and moving cameras, Place picks the exact spot in this world, or the same spot measured from the ship when the shot starts.");
            Line(Word("Camera path", HudColor.Cyan), "A shot that flies through any number of keyframes on a smooth curve. Drag + Camera path onto the timeline (it takes the current view as its first keyframe), close the menu, fly to the next view, open the menu, pick the shot and press Add keyframe; repeat. Remove last and Clear undo keyframes.");
            Line(Word("Keyframes", HudColor.Cyan), "Pick a camera path shot to see its keyframes as numbered buttons. Pick one to Go to it (the camera jumps there so you can adjust it), Set it to the current view, Add one after it, move it earlier or later with < and >, or Delete it. With Show in world on, the picked shot's camera views are drawn in the world as camera shapes (the picked one yellow) with the path's curve in pink. Other shots have Go to view (start / end) too.");
            Line(Word("Smooth cuts", HudColor.Cyan), "Every shot has Blend in (seconds): instead of a hard cut, the camera eases from where the last shot left it into the new shot over that time. 0 = a cut. It's drawn as the shaded corner at the start of the block; on the picked shot drag the white dot on its top edge to change it.");
            Line(Word("Effects layer", HudColor.Cyan), "Under the timeline is a second layer for changes that run over the shots: FOV (the field of view goes from one value to another, e.g. a slow zoom in), Tilt (the picture leans over) and Shake (a hand-held wobble). Drag one from the Effects chips onto the layer at the time it should start; click it to set its values; drag it or its right edge to move or stretch it; Delete removes it.");
            Line(Word("Effect layers", HudColor.Cyan), "+ Layer (under the effects) adds another effects layer, so two or more effects can run at the same time, e.g. a slow zoom while the picture tilts and shakes. Drag an effect up or down to move it to another layer; - Layer takes the bottom one away and moves its effects up. Where two FOVs overlap the higher layer's wins; tilts and shakes add together. Effects on one layer never sit on top of each other: drop one where that time is taken (its edge turns red while you drag) and it goes to the nearest free layer, or a new one; stretch one into the next and it stops where the next starts.");
            Line(Word("Fade, letterbox", HudColor.Cyan), "Fade (to and from black, 0% clear to 100% black) and Letterbox (cinema bars, % of the screen each) are effects like FOV: drag them onto an effects layer and set From and To.");
            Line(Word("Panel effect", HudColor.Cyan), "The Panel effect switches race spotlight's showcase panel at set times: Race results, Race stats, The ship, or Hidden. Lay them one after another on an effects layer to script the showcase; where none is running the pages change by themselves. Turn on In race spotlight (beside Play) so the cinematic plays when you pick the ship.");
            Line(Word("Any ship", HudColor.Yellow), "With Always film the picked ship on, a cinematic made on one ship plays on whichever ship you pick. Loop repeats it. Race spotlight plays this cinematic puts it on the ship you pick in race spotlight. Each cinematic is a file in the cinematics folder (Open folder), to share or back up.");
            Line(Word("Showcase", HudColor.Purple), "An example cinematic, Showcase, is made for you the first time you sit in a ship (or Cinematics tab > + Showcase example): an orbit reveal, a chase with a lens push, a wing camera, a head-on shot, one from above and a wide orbit, with fades and film bars. Its views are placed from the ship (its cockpit and size), so it works on any course.");
            Line(Word("Auto switch", HudColor.Purple), "Cinematics tab > Auto switch at gates: when the ship being filmed (by a playing cinematic or the orbit camera) flies through a gate of this course, the cinematic set for that gate starts on it. Click a gate's drop-down to pick a cinematic for it (or next cinematic = the one after the one playing, or nothing). Beside it, when: at the gate, a few seconds before (the cut happens on the approach) or a few seconds after (following the ship away). Kept per course.");
            Line(Word("Cinematic keys", HudColor.Purple), "Ctrl+Shift+P plays the picked cinematic on the ship in view (or stops the one playing). Ctrl+Shift+] and Ctrl+Shift+[ step to the next / previous cinematic; if one is playing it switches straight to the new one on the same ship. Change them on the Controls tab.");
            Line(Word("Debug timeline", HudColor.Yellow), "Debug timeline (beside Play, or Settings > Cinematics) draws the timeline at the bottom of the screen while a cinematic plays: the shots, the effects layer, a line at the time it has reached, and a line of text saying what is playing. The race HUD is hidden while it shows. Turn it off to record.");

            Page("Showcase and video");
            Section("Race spotlight");
            Line(Word("Spotlight", HudColor.Purple), "For showing a ship off after a race, in any world. Race tab > Race spotlight > Export saves the race you just finished (every lap's time, penalties and the race stats) to a file; Open folder shows where, and a file from someone else put there appears in the list. Press Spotlight on a race: the menu closes and the race HUD goes away. Look at the ship that flew it and left click: a panel then shows RACE RESULTS (course, pilot, date, race time, best lap, penalties, each lap), RACE STATS and THE SHIP (its mass, thrust, gyroscopes, power, cargo and RPI, with its centre of mass and thrust arrows drawn on it), changing every 8 seconds. Left click another grid to move the spotlight; Esc (or Close spotlight in the menu) leaves it.");
            Line(Word("For viewers", HudColor.Purple), "The spotlight is made for people watching, not the racer: it shows no dates, no best-lap call-outs or comparisons with your own records, and no colour codes; every number is plain white.");
            Line(Word("Compare", HudColor.Orange), "Race tab > Race spotlight > Compare on a second race (same course): the spotlight gets a Head to head page (lap by lap, totals, the winner), and with auto race that race's ghost flies alongside in orange.");
            Line(Word("More pages", HudColor.Purple), "Fun facts (distance, football pitches, average and top speed, the hardest turn in g) and Leaderboard (every saved race on the course with the same laps, fastest first). The lap replay also shows the course's length, checkpoints and height, the top speed (a white ring on the map where it was hit) and each lap's crashes (red crosses) and missed checkpoints (orange rings). They need races exported with this version.");
            Line(Word("On screen", HudColor.Purple), "While a cinematic films the autopilot flying a race, a live timing strip along the bottom shows the lap, the race clock and the speed, and a name tag follows the ship. Settings > Cinematics turns each off. At a lower recording speed they show the race's real time and speed.");
            Line(Word("Auto race", HudColor.Purple), "Each exported race in Race tab > Race spotlight has three buttons. Spotlight starts it on the ship you're sitting in (left click another grid to move it). Auto race (a toggle): when the spotlight starts, your ship is put at the start of that race's line and the autopilot flies the whole race, lap after lap; the course it was flown on has to be loaded, and races exported before this version have no line. The cinematic drop-down picks a cinematic to play on the ship as soon as the spotlight starts, so the race can be filmed again. Leaving the spotlight (Esc) stops the autopilot it started.");
            Line(Word("Spotlight speed", HudColor.Yellow), "Settings > Race spotlight > Spotlight page time sets how long each race spotlight page stays up; Spotlight page change speed sets how long the slide between pages takes (0 = instant).");
            Line(Word("Pages", HudColor.Cyan), "Race tab > Race spotlight lists the pages in the order they show: Intro (a big title card), Race results, Race stats, Ship card (big stat tiles), Lap by lap (a bar a lap, coloured by how close it came to the best), Race graphs, Lap replay, The ship, Outro (the final time). Switch any off, move them up or down, and give any its own time (0 = the usual). A cinematic's Panel effect can show any of them at a set time.");
            Line(Word("Lap replay", HudColor.Cyan), "Race spotlight has a LAP REPLAY page: the course from above, with each lap of the race flown again as a moving dot, one lap after another (in blue, with each lap's time ticking up). It shows for races exported from this version on; older race files don't have the laps' paths. A cinematic's Panel effect can pick it too. A key under the map says what each sign is: the ship, start, finish, checkpoints, and any crash (red cross), missed checkpoint (orange ring) or top speed (white ring).");
            Line(Word("Race graphs", HudColor.Green), "Race spotlight also has a RACE GRAPHS page: the speed graph and the time gap graph from the results, one above the other. Races exported from this version on.");
            Page("Autopilot");
            Section("Autopilot");
            Line(Word("Autopilot", HudColor.Cyan), "Ctrl+Shift+' (or Race tab > Autopilot) makes your ship fly the racing line by itself: your best lap on the course, or the ghost you race. It joins the line where it's nearest (within 500 m) and goes round and round until you press the key again. Settings > Race > Autopilot speed sets how fast (100% = the lap's own pace). Its laps aren't timed. Single player or hosting only.");
            Page("Results and records");
            Section("History");
            Line(Word("History tab", HudColor.Cyan), "Every race you finish is kept: the course, the ship, laps, time, FLOWN (without penalties), best lap, penalties (or clean) and top speed (purple = a personal best at the time). Filter to this course or all, this session or all time, one ship, or clean races only. Click a heading to sort by it; by date the races are grouped by day. The graph colours each race by its ship (personal bests ringed purple, flown times as a fainter line); point at a race on the graph or in the table and the other lights up. Watch (race spotlight) and Race show for races with a race file; Save to watch keeps the race you've just flown. Kept in History.jsonl beside the plugin's settings.");

            Page("Minimap and Race Map");
            Section("Maps");
            Line(Word("Minimap", HudColor.Cyan), "Round, in the top left corner (top right with HUD layout Left), showing the whole course. With Settings > Minimap > Minimap zooms in while moving on (off by default) it zooms in and follows you from 20 m/s, and zooms back out as you slow down. The next checkpoint pulses (with a dashed line from you to it); missed checkpoints are red. Each gate and checkpoint has a label, and the racing line uses its racing line colours; start gate facing up. Dashed route between the gates and checkpoints (next one orange, flown ones green), named sectors as coloured bands, the best lap's line, you (arrow) and the ghosts. Settings > Minimap.");
            Line(Word("Race Map", HudColor.Cyan), "G > Map > Race Map: a big version of the minimap. The switches above it (Sectors, Checkpoints, Labels, Route, Racing line, Ghosts, You) hide or show those on the Race Map and the minimap alike, and are remembered. Under it, the height round the lap: above the ground below (as under the speedometer; above the start gate where that can't be read), with the checkpoints marked. The game's own map pieces (Fast Travel, map data) are hidden while it shows.");
            Line(Word("Sectors", HudColor.Green), "Courses tab > Sectors: name parts of the course (e.g. \"Hairpin\", from CP 3 to CP 5). Each shows on the minimap and Race Map as a coloured band with its name, and is saved with the course.");
            Page("Ghosts and replays");
            Section("Ghosts");
            Line(Word("BEST LAP", HudColor.Purple), "The ghost (labelled BEST LAP) is where your best lap was at this moment of the lap: an outline of the ship that set it, flying with the same heading and roll (or a purple diamond with Settings > Ghost and racing line > Ghost as ship projection off). Ahead of you = you're behind your best; behind you = you're ahead. The magenta racing line is your best lap's path. Red BRAKE and green GO markers on it show where that lap braked hard and where it sped up again (Settings > Ghost and racing line > Braking markers; thickness, opacity and distance are there too). After a race the Race Map shows your best lap of the race (orange) over the record from before it (purple), with red bands where your line was well away from it. The Race tab (Shift+J) picks whose lap it is: your course best, a ship's best or a ghost file.");
            Line(Word("Racing line", HudColor.Yellow), "The best lap's line is coloured to show where time is won and lost (Settings > Ghost and racing line > Racing line colours). Speed: red where it was slow, yellow, green where it was fastest. Braking: red where it was slowing down (harder = redder), green where it was speeding up, white where it held its speed. Plain: magenta.");
            Line(Word("Possible line", HudColor.Purple), "The best possible line is joined together from the best bit between each pair of checkpoints, usually from different laps. Settings > Ghost and racing line > Smooth the possible line eases each join over a few seconds either side (1.5 by default): the two paths meet halfway through the gate, the ship's turn is blended and the speed changes steadily instead of jumping, with the sector times kept the same. The ghost, racing line, maps and autopilot all use the smoothed line.");
            Line(Word("2nd ghost", HudColor.Orange), "Records tab > 2nd ghost (on a ship's row or a ghost file) adds a second, orange ghost that flies at the same time as the main one, in races and in replays: e.g. your best against another ship's best. Its racing line shows too, in orange, and the main line turns purple so you can tell them apart. Press it again to turn it off.");
            Line(Word("Record race", HudColor.Purple), "Race tab > Course record race: the ghost flies the fastest whole race on this course for the laps set, lap for lap (its lap 1 on your lap 1, and so on), labelled RECORD RACE LAP n. Kept from record races set with this version. Possible lap / race flies your best sectors joined up on every lap.");
            Line(Word("Ghost files", HudColor.Purple), "Records tab > Ghost files: export your best lap (course best, or a ship's best) as a file to share, and load other players' files from the ghosts folder. Watch replays one with the spectator camera; Race makes the ghost, racing line and live gap follow it instead of your own best. A ghost only fits the course it was flown on.");
            Section("Replays");
            Line(Word("Replay", HudColor.Purple), "Records tab > Watch best lap (or the replay shortcut): the spectator camera follows the ghost of your ship round your best lap at real speed, then puts you back in your seat. Racing is paused while it plays. The camera orbits the ghost by default: move the mouse to swing round it, W / S to zoom; Ctrl+Shift+C switches to a fixed chase camera. Replays use the game's cinematic spectator (smooth movement, and its look-around keys let you aim away from the ghost); turn that off in Settings > Ghost and replay.");
            Page("Results and records");
            Section("After the race");
            Line(Word("Speed graph", HudColor.Green), "After a race two graphs show in their own panel on the right (left with the panels swapped), one above the other; Settings > Panels > Results graphs picks both, speed only, or off. Race spotlight has them too, on its RACE GRAPHS page. SPEED (top): your speed round the lap (start on the left, FIN on the right) for your best lap this race against the course record as it was before the race (or your last lap), shaded green where you were faster and red where slower. TIME GAP (under it): how far ahead (above the middle line, green) or behind (below, red) of that lap you were at each point, with the scale in seconds at the left. Lines marked CP n are the checkpoints; red ones were missed.");
            Line(Word("Race stats", HudColor.White), "After the finish the splits table adds G-forces per lap (G MAX = hardest cornering, G AVG = average cornering, BRAKE = hardest slowing down) and distance flown per lap (shortest in green). A separate RACE STATS box in the top corner shows the ship, how you did against its best, total distance, time at top speed, average lap and consistency (best to worst lap spread); turn it off in Settings > Panels. G-forces come from the ship's change of velocity, so impacts over 30 g are left out.");
            Line(Word("Results", HudColor.Yellow), "WITHOUT PENALTIES is the time you actually flew. The VS BEST column in the sectors table is this race's best for each sector (and lap) against the course best before the race: purple minus = you beat it, red plus = slower. The race results can compare with the best on the course (any ship) or the best in the ship you flew: press Ctrl+B (Controls tab: Results: overall / this ship) while they're showing to switch, or set it in Settings > Panels. In ship mode the headline, best lap colour, possible lap and the graph all use that ship's own bests.");
            Line(Word("Other laps", HudColor.Yellow), "With more than one lap, Shift+[ / Shift+] (Controls tab: Results: previous / next lap) step the graphs and the map through the race's best lap, lap 1, lap 2 and so on; the graph title says which one is showing.");
            Line(Word("Race Map", HudColor.Cyan), "The Race Map (G > Map > Race Map) shows sector colours along the racing line, gates as bars across the course (start / finish chequered), a scale bar and north arrow, the height round the lap under the map, and a key. Mouse wheel zooms, drag moves the map, double-click resets it; point at the line to see the speed, lap time and gap there (and where that is on the height profile).");
            Line(Word("Map after the race", HudColor.Orange), "After the finish the minimap and Race Map show the path you flew on the lap shown (orange, over the course record from before the race in purple when there is one), its checkpoints green (flown) or red (missed), and a red X where you crashed. On the Race Map the lap then plays back once (8 to 15 seconds): the line draws itself in with the lap time at its head, checkpoints turn green as they were flown or red when they were missed, and crashes appear where and when they happened. The LAP bar above the map shows which lap that is: its arrows step through the race's laps (best lap, lap 1, lap 2...) and Replay plays the lap again. On the minimap a white dot flies the shown lap again, then the results move on to the next lap by themselves (Settings > Minimap > Minimap replays the laps after a race); after you pick a lap yourself they stay on it for a minute.");
            Section("Records");
            Line(Word("Ships", HudColor.Cyan), "Every race saves times for the ship you flew, by its name. Records tab > Ships on this course compares them: fastest in purple, the others with their gap in yellow. Rename a ship in its terminal to keep separate times for each version. Race best / Watch best race against or replay that ship's best lap, and Race last / Watch last its last lap. Deleting the ship that holds the course best lap hands it to the next fastest ship (its lap, racing line and ghost).");
            Line(Word("Records", HudColor.Yellow), "The Records tab lists your best 5 races on this course for each number of laps, and has the reset buttons. The Show buttons above the lists pick which lap counts to see: click one to see only that, click others to add them, All shows them all again (remembered).");
            Page("How it's worked out");
            Section("Times and gaps");
            Line(Word("Lap time", HudColor.White), "Every lap time is the time you flew plus any penalty seconds for that lap. Best laps, best races and records use these times (penalties included); WITHOUT PENALTIES in the results is the flown time on its own.");
            Line(Word("Live gap", HudColor.Green), "The gap under the timer while you fly: the plugin finds the nearest point on the ghost's recorded path (your best lap, or whoever you race against) to where you are, and compares the ghost's lap time at that point with yours. Minus (green) = you're ahead, plus (red) = behind. The path is recorded 4 times a second, so the gap is accurate to roughly a tenth of a second. Settings > Panels > Gap under the timer hides it (e.g. for race days that are filmed).");
            Line(Word("Checkpoint gap", HudColor.Yellow), "The gap shown at each checkpoint (and in the GAP column of the splits): your time at that checkpoint this lap minus your best lap's time at the same checkpoint.");
            Line(Word("Sector", HudColor.White), "A sector is the stretch from one checkpoint to the next: start to CP 1, CP 1 to CP 2 and so on, and the last checkpoint to the finish. Its time is the difference between your times at the two ends, so it only counts when both were flown, in order. Penalties aren't in sector times.");
            Section("Possible bests");
            Line(Word("Possible lap", HudColor.Purple), "Your best time for each sector, from any lap on this course, added up: the lap you'd do if every sector went as well as your best. It only shows once every sector has a time. In ship mode it uses that ship's own best sectors. The Race tab can race its line: each best sector's path, joined up.");
            Line(Word("Possible race", HudColor.Purple), "The possible lap times the number of laps.");
            Line(Word("VS BEST", HudColor.Purple), "In the sectors table after a race: this race's best for each sector (and lap) minus the course's best before the race started. Minus (purple) = you beat it, plus (red) = slower.");
            Section("Race stats");
            Line(Word("Top / avg speed", HudColor.White), "Top speed is the fastest you went during the race. Average speed is the distance you flew divided by the time you flew (without penalties), for each lap; the results show the laps' average.");
            Line(Word("At top speed", HudColor.White), "How much of the race you spent within 10% of your top speed: your speed is sampled 4 times a second during the race, and this is the share of samples at 90% of the top speed or more.");
            Line(Word("Avg lap", HudColor.White), "All the lap times (penalties included) added up and divided by the number of laps.");
            Line(Word("Consistency", HudColor.White), "Your slowest lap minus your fastest lap: the smaller, the more even your laps were. Green under 1 second, yellow under 3, orange above.");
            Line(Word("Distance", HudColor.White), "The length of the path you actually flew, added up 4 times a second.");
            Line(Word("G-forces", HudColor.White), "Worked out from how fast the ship's velocity changes, divided by 9.81. G MAX is the hardest cornering (the part of the acceleration sideways to where you're going); G AVG is the average cornering while turning; BRAKE is the hardest slowing down (along where you're going); MAX G is the biggest of any kind. Spikes over 30 g (impacts) are left out.");
            Section("Penalties");
            Line(Word("Missed", HudColor.Red), "A checkpoint counts as missed when you pass a later one (or finish) without flying through it. Each miss adds the missed checkpoint penalty (Settings > Penalties).");
            Line(Word("Crash", HudColor.Red), "Settings > Penalties > Crash detection. Hull damage (the default): a crash is when the game deals collision damage to your ship, or reports a hit on it even with no damage (e.g. with a mod that turns collision damage off), so hard braking never counts and a scrape does. Crash hit strength needed and Crash damage needed set how big a hit must be, and it only counts at 20 m/s or more (Advanced > Crash minimum speed). It needs the game's server in your game (single player or hosting); where hits can't be seen (a dedicated server) it uses the speed drop. Speed drop: the ship was going at least 20 m/s and lost speed faster than the penalty hardness, losing at least 8 m/s and a quarter of its speed. Impact: no damage needed: slowing down harder than the ship's own thrusters ever could (worked out from its thrust and mass) is a crash, so braking never counts. One crash = one penalty, then no more for the crash immunity time (at least 1 second; and, if set, until you're back above the immunity speed).");
            Section("Graphs");
            Line(Word("Speed graph", HudColor.Green), "Speed against how far round the lap you were (0 at the start, FIN at the end, as a share of the lap's distance), so laps of different lengths line up. The shading compares the two lines at the same point of the lap.");
            Line(Word("Time gap graph", HudColor.Green), "For each point round the lap: your time to get there minus the other lap's. At checkpoints these are the real split times; in between they're shared out by how slow each lap was over each bit, so the curve is exact at the checkpoints and a close estimate between them. The finish value is the difference in flown time.");
            Line(Word("Race Map lines", HudColor.Orange), "After the race your lap is coloured by the gap to the record: where the gap was shrinking (over a few points either side) it's green, where it was growing red, white where it held. A faint red band marks stretches where your line stayed more than 2% of the map's width (at least 25 m) from the record's line. The height profile is height above the start gate along the line, against the share of the lap's distance. North is the world's Y axis (the planet's pole in most worlds).");
            Page("Menu and settings");
            Section("Racing Display menu");
            Line(Word("Race", HudColor.Green), "Press Shift+J (or the Race tab) to set the laps and practice laps and pick whose lap you race against: your course best, any ship's best lap on this course, or a ghost file. Speeds use the game's speed unit (Options > Interface).");
            Line(Word("Records", HudColor.Purple), "Your best races on this course for each lap count, every ship's best lap, and ghost files: race or watch any of them.");
            Line(Word("History", HudColor.Cyan), "Every race you have finished, with filters and a graph of your times on the course.");
            Line(Word("Courses", HudColor.White), "Save, load and share courses, change the checkpoint order, build a course from grid names, sectors, antennas.");
            Line(Word("RPI", HudColor.Yellow), "Pick the RPI class in use and open the RPI designer to make classes.");
            Line(Word("Cinematics", HudColor.Purple), "Make camera timelines: shots, effects, keyframes, auto switch at gates, play and preview.");
            Line(Word("Settings", HudColor.White), "Hide panels, choose the HUD layout, turn markers, the arrow or sounds off, and change penalties, distances and sizes on the Settings tab. Settings are grouped: Race, Race spotlight, HUD, Sound, Panels, Markers, Minimap, Ghost and racing line, Penalties, Replay, Cinematics, Orbit camera, Course editing and Advanced. Click a section's title (the arrow) to fold it away or open it (Collapse all / Expand all at the top); Advanced starts folded. A setting that only matters when another is on is greyed out while that one is off, and an orange dot (with a Reset button) marks every setting that isn't at its default. Presets save all the settings under a name to switch between them with Load; two come ready made: Racing (for filmed race days: lap time, laps and splits, speedometer, height, keys, roll and pitch; no best times, gaps, ghost or racing line; Ctrl+R doesn't teleport) and Practice (everything on, with the reset teleport). Export writes a preset to a file to share, and other players' files in the presets folder can be imported. The search box finds a setting by name or description, folded sections included; point at a setting to see what it does. Panels use the game's UI background opacity (Options > Interface) unless you turn Panel darkness: use the game's off.");
            Line(Word("Controls", HudColor.White), "The Controls tab lists every key and lets you change them: click one, then press the new key combination (Esc cancels); keys used by two actions show in red. Single keys use Shift: Shift+J for race setup; in edit mode Shift+R (hold) for the tool wheel, Shift+T for the placement mode, Shift+L for the best lap line and Shift+M to move a checkpoint (aim at it, press Shift+M, then left click its new place).");
            Line(Word("Status strip", HudColor.Cyan), "Across the top of the menu: the course, your ship, and whatever is running (cinematic, orbit camera, autopilot, race spotlight, ship analyser, replay, course edit mode), each with a Stop button that works from any tab.");
            Line(Word("?", HudColor.Cyan), "Point at the ? beside a section's title for a short explanation; click it to open this guide at the part about that section. Key reminders (Settings > HUD) show a mode's keys when it starts; turn them off for recording.");

            if (search != null && page.Children.Count == 0)
            {
                Page(null);
                Line(null, "Nothing found. Try another word, or pick a topic on the left.");
            }
            return page;
        }

        // ---------------------------------------------------------------- Courses tab

        private void FillCourses(HudModel.ControlsPanel p)
        {
            double s = scale;
            courseRows.Children.Clear();

            Control Buttons(params Button[] buttons)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var b in buttons) sp.Children.Add(b);
                return sp;
            }
            void Header(string text)
            {
                courseRows.Children.Add(Section(text));
                courseRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            }

            // ---- this course
            Header("This course");
            var summary = Text((p.CourseName != null ? $"'{p.CourseName}': " : "Not saved yet: ") + p.CourseSummary, null, "SubTitle", "Bright");
            summary.TextWrapping = TextWrapping.Wrap;
            summary.Margin = new Thickness(16 * s, 0, 24 * s, 4 * s);
            summary.FontSize = 16 * s;
            courseRows.Children.Add(summary);

            // Compact rows: slimmer buttons and name box, a smaller row name, less height.
            Grid Tight(Grid optionRow)
            {
                optionRow.MinHeight = 40 * s;
                optionRow.Margin = new Thickness(0, 1 * s, 24 * s, 1 * s);
                if (optionRow.Children.Count > 0 && optionRow.Children[0] is TextBlock rowName) rowName.FontSize = 17 * s;
                return optionRow;
            }
            Control Slim(string words, IBrush brush, Action click)
            {
                var slim = SlimButton(words, brush, click);
                slim.Margin = new Thickness(8 * s, 0, 0, 0);
                return slim;
            }

            // One line: the name, Save, and Clear course at its end (it still asks first), where there were two rows
            // and a Clear button the width of the page.
            if (string.IsNullOrEmpty(courseNameBox.Text) && p.CourseName != null) courseNameBox.Text = p.CourseName;
            if (courseNameBox.Parent is Panel oldParent) oldParent.Children.Remove(courseNameBox);
            courseNameBox.MinWidth = 0;
            courseNameBox.MinHeight = 0;
            courseNameBox.Height = 34 * s;
            courseNameBox.FontSize = 16 * s;
            courseNameBox.Padding = new Thickness(8 * s, 0, 8 * s, 0);
            courseNameBox.VerticalAlignment = VerticalAlignment.Center;
            var save = Slim("Save", White, () => commands.Enqueue(new MenuCommand(MenuAction.SaveCourse, text: courseNameBox.Text)));
            var clearCourse = Slim("Clear course", Brush(HudColor.Red), () => ShowConfirm("This removes the gates, checkpoints and sectors from this world. Saved courses stay in the list. It can't be undone.",
                () => commands.Enqueue(new MenuCommand(MenuAction.ClearCourse))));
            clearCourse.Margin = new Thickness(20 * s, 0, 0, 0);
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(courseNameBox, 0); nameRow.Children.Add(courseNameBox);
            Grid.SetColumn(save, 1); nameRow.Children.Add(save);
            Grid.SetColumn(clearCourse, 2); nameRow.Children.Add(clearCourse);
            courseRows.Children.Add(Tight(OptionRow("Save as",
                "Type a name and press Save to keep this course (gate and checkpoints). Saving with an existing name replaces it.\n\n"
                + "Clear course removes this world's start / finish gate, checkpoints and sectors (courses saved in the list below stay). Use it if a course turned up that doesn't belong to this world.", nameRow)));

            // ---- where the reset race key teleports you (with Settings > Race > Reset race teleports to the start):
            // a short name, and where the spot is now in grey after the buttons.
            var spotButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var setSpot = Slim("Set here", White, () => commands.Enqueue(new MenuCommand(MenuAction.SetStartSpot)));
            setSpot.Margin = new Thickness(0);
            spotButtons.Children.Add(setSpot);
            spotButtons.Children.Add(Slim("Clear", White, () => ShowConfirm("Forget the start spot? Resetting to the start will put the ship just behind the start gate instead.", () => commands.Enqueue(new MenuCommand(MenuAction.ClearStartSpot)))));
            var spotNow = Text(p.StartSpot ?? "Just behind the start gate", Grey);
            SoftFontSize(spotNow, 15 * s);
            spotNow.VerticalAlignment = VerticalAlignment.Center;
            spotNow.Margin = new Thickness(14 * s, 0, 0, 0);
            spotNow.TextTrimming = TextTrimming.CharacterEllipsis;
            spotButtons.Children.Add(spotNow);
            courseRows.Children.Add(Tight(OptionRow("Start spot",
                "Where the reset race key (Ctrl+R) puts your ship when Settings > Race > Reset race teleports to the start is on. Sit in your ship at the spot, facing the way you want to start, and press Set here (or the set start spot key, Ctrl+T, while flying): your seat goes back to exactly that place and direction. Clear goes back to just behind the start gate. Saved with the course in the world.",
                spotButtons)));

            // ---- the course designer's choice: do checkpoints on grids get the glowing frame as well as air gates
            var glowButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var glowToggle = Slim(p.CourseGridGlow ? "On" : "Off", p.CourseGridGlow ? Brush(HudColor.Green) : White, () => commands.Enqueue(new MenuCommand(MenuAction.ToggleCourseGridGlow)));
            glowToggle.Margin = new Thickness(0);
            glowButtons.Children.Add(glowToggle);
            var glowNow = Text(p.CourseGridGlow ? "Checkpoints on grids glow like air gates" : "Checkpoints on grids show only their markers", Grey);
            SoftFontSize(glowNow, 15 * s);
            glowNow.VerticalAlignment = VerticalAlignment.Center;
            glowNow.Margin = new Thickness(14 * s, 0, 0, 0);
            glowNow.TextTrimming = TextTrimming.CharacterEllipsis;
            glowButtons.Children.Add(glowNow);
            courseRows.Children.Add(Tight(OptionRow("Grid checkpoint glow",
                "For whoever designs the course: whether its checkpoints and gates set on grids (built rings and structures) get the glowing frame that air gates have, brightest on the one to fly through next. On suits a course whose rings are hard to see; Off leaves built rings to speak for themselves, with just their markers. Air gates always glow. Needs the Glow (game 3D UI) air gate look. Saved with the course in the world.",
                glowButtons)));

            // ---- named sectors
            Header("Sectors");
            var secAbout = Text("Name parts of the course (\"Canyon run\", \"The S bends\"): pick where a sector starts and ends. They show on the minimap and the Race Map, each in its own colour, and are saved with the course in the world.", Grey);
            secAbout.TextWrapping = TextWrapping.Wrap;
            secAbout.Margin = new Thickness(24 * s, 2 * s, 24 * s, 6 * s);
            SoftFontSize(secAbout, 16 * s);
            courseRows.Children.Add(secAbout);
            // The sectors as a small table: its colour on the maps, its name (click to rename), where it starts and
            // ends (pick to change), and a small x to delete it. Every row lines up.
            if (p.Sectors.Count > 0)
            {
                TextBlock Small(string text, IBrush brush, double size = 15)
                {
                    var tb = Text(text, brush);
                    tb.VerticalAlignment = VerticalAlignment.Center;
                    SoftFontSize(tb, size * s);
                    return tb;
                }
                Grid TableRow(bool header)
                {
                    var g = new Grid { Margin = new Thickness(24 * s, header ? 2 * s : 3 * s, 24 * s, header ? 2 * s : 3 * s) };
                    foreach (var w in new[] { 22.0, 300, 190, 40, 190, 0, 44 })
                        g.ColumnDefinitions.Add(new ColumnDefinition(w > 0 ? new GridLength(w * s) : new GridLength(1, GridUnitType.Star)));
                    return g;
                }
                var head = TableRow(true);
                foreach (var (label, col) in new[] { ("NAME", 1), ("FROM", 2), ("TO", 4) })
                {
                    var t = Small(label, Grey, 13);
                    t.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(t, col);
                    head.Children.Add(t);
                }
                courseRows.Children.Add(head);
                for (int i = 0; i < p.Sectors.Count; i++)
                {
                    int row = i;
                    var sec = p.Sectors[i];
                    var g = TableRow(false);
                    var swatch = new Border { Width = 12 * s, Height = 12 * s, CornerRadius = new CornerRadius(3 * s), Background = Brush(sec.Colour), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                    g.Children.Add(swatch);
                    // The name: edited in place (Enter or clicking away keeps it).
                    var nameBox = new TextBox
                    {
                        Text = sec.Name, MinHeight = 0, Height = 34 * s, FontSize = 17 * s,
                        Padding = new Thickness(6 * s, 0, 6 * s, 0), Margin = new Thickness(0, 0, 10 * s, 0),
                        VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    };
                    TidyBox(nameBox);
                    SmallTip(nameBox, "Click to rename; Enter or click away to keep it");
                    int from = sec.From, to = sec.To;
                    bool sent = false;
                    void Commit(string name, int f, int t)
                    {
                        if (sent) return;
                        if (name.Trim() == sec.Name && f == sec.From && t == sec.To) return;
                        sent = true;
                        commands.Enqueue(new MenuCommand(MenuAction.EditSector, row, text: $"{name.Trim()}|{f}|{t}"));
                    }
                    nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(nameBox.Text ?? "", from, to); e.Handled = true; } };
                    nameBox.LostFocus += (_, __) => Commit(nameBox.Text ?? "", from, to);
                    Grid.SetColumn(nameBox, 1);
                    g.Children.Add(nameBox);
                    // Where it starts and ends: picked straight from the course's gates.
                    ComboBox Point(int selected, Action<int> picked)
                    {
                        var cb = new ComboBox { ItemsSource = p.RoutePoints.ToList(), SelectedIndex = Math.Clamp(selected, -1, p.RoutePoints.Count - 1), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Height = 34 * s, MinHeight = 0, FontSize = 15 * s };
                        cb.SelectionChanged += (_, __) => { if (cb.SelectedIndex >= 0) picked(cb.SelectedIndex); };
                        return cb;
                    }
                    var fromBox = Point(from, v => { from = v; if (to > from) Commit(nameBox.Text ?? "", from, to); });
                    var toBox = Point(to, v => { to = v; if (to > from) Commit(nameBox.Text ?? "", from, to); });
                    Grid.SetColumn(fromBox, 2); g.Children.Add(fromBox);
                    var toWord = Small("to", Grey, 15);
                    toWord.HorizontalAlignment = HorizontalAlignment.Center; toWord.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(toWord, 3); g.Children.Add(toWord);
                    Grid.SetColumn(toBox, 4); g.Children.Add(toBox);
                    // Delete: a small quiet x at the end of the row (still asks first).
                    var del = new Button
                    {
                        Content = Cross(Grey), FontSize = 15 * s, Foreground = Grey, Background = Argb(255, 14, 34, 48),
                        Width = 34 * s, Height = 34 * s, MinHeight = 0, Padding = new Thickness(0),
                        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    };
                    SmallTip(del, $"Delete '{sec.Name}'");
                    del.Click += (_, __) => ShowConfirm($"Delete the sector '{sec.Name}'?", () => commands.Enqueue(new MenuCommand(MenuAction.DeleteSector, row)));
                    Grid.SetColumn(del, 6); g.Children.Add(del);
                    courseRows.Children.Add(g);
                }
            }
            if (p.RoutePoints.Count >= 2)
            {
                if (sectorFrom >= p.RoutePoints.Count - 1) sectorFrom = 0;
                if (sectorTo <= sectorFrom || sectorTo >= p.RoutePoints.Count) sectorTo = Math.Min(sectorFrom + 1, p.RoutePoints.Count - 1);
                ComboBox Picker(int selected, Action<int> picked)
                {
                    var cb = new ComboBox { ItemsSource = p.RoutePoints.ToList(), SelectedIndex = selected, MinWidth = 170 * s };
                    cb.SelectionChanged += (_, __) => { if (cb.SelectedIndex >= 0) picked(cb.SelectedIndex); };
                    return cb;
                }
                if (sectorNameBox.Parent is Panel oldSecParent) oldSecParent.Children.Remove(sectorNameBox);
                // A new sector: one row in the same columns as the table above (name, from, to), with Add at its end.
                var newSecHead = Text("NEW SECTOR", Grey);
                SoftFontSize(newSecHead, 13 * s);
                newSecHead.Margin = new Thickness(46 * s, 10 * s, 24 * s, 2 * s);
                courseRows.Children.Add(newSecHead);
                var newSecRow = new Grid { Background = Brushes.Transparent, Margin = new Thickness(24 * s, 3 * s, 24 * s, 6 * s) };
                foreach (var newSecWide in new[] { 22.0, 300, 190, 40, 190, 0 })
                    newSecRow.ColumnDefinitions.Add(new ColumnDefinition(newSecWide > 0 ? new GridLength(newSecWide * s) : new GridLength(1, GridUnitType.Star)));
                newSecRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                sectorNameBox.MinWidth = 0;
                sectorNameBox.MinHeight = 0;
                sectorNameBox.Height = 34 * s;
                sectorNameBox.FontSize = 17 * s;
                sectorNameBox.Padding = new Thickness(6 * s, 0, 6 * s, 0);
                sectorNameBox.Margin = new Thickness(0, 0, 10 * s, 0);
                sectorNameBox.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(sectorNameBox, 1); newSecRow.Children.Add(sectorNameBox);
                var newSecFrom = Picker(sectorFrom, v => sectorFrom = v);
                var newSecTo = Picker(sectorTo, v => sectorTo = v);
                newSecFrom.MinWidth = newSecTo.MinWidth = 0;
                newSecFrom.HorizontalAlignment = newSecTo.HorizontalAlignment = HorizontalAlignment.Stretch;
                newSecFrom.VerticalAlignment = newSecTo.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(newSecFrom, 2); newSecRow.Children.Add(newSecFrom);
                var newSecWord = Text("to", Grey);
                SoftFontSize(newSecWord, 15 * s);
                newSecWord.HorizontalAlignment = HorizontalAlignment.Center; newSecWord.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(newSecWord, 3); newSecRow.Children.Add(newSecWord);
                Grid.SetColumn(newSecTo, 4); newSecRow.Children.Add(newSecTo);
                var add = ValueButton("Add", White, 110, () =>
                {
                    commands.Enqueue(new MenuCommand(MenuAction.AddSector, text: $"{sectorFrom}|{sectorTo}|{sectorNameBox.Text}"));
                    sectorNameBox.Text = "";
                });
                add.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(add, 6); newSecRow.Children.Add(add);
                newSecRow.PointerEntered += (_, __) => description.Text = "A new sector: type its name, pick where it starts and ends along the course (the gate, then the checkpoints in order), and press Add.";
                courseRows.Children.Add(newSecRow);
            }

            // ---- edit mode
            Header("Course edit mode");
            courseRows.Children.Add(OptionRow("Course edit mode",
                $"Turn it on ({p.EditModeKey}), close the menu, then aim at a grid and left click to make it the start / finish gate or the next checkpoint; right click one to remove it. "
                + $"Hold {p.EditWheelKey} for the tool wheel (start / finish gate, start gate, finish gate, checkpoint): the cursor appears, point at one and let go (or click it). Shift+T changes the placement mode: on a grid, pick blocks (two corner blocks) or in the air. A separate finish gate makes a point-to-point course (one run, start to finish). No racing while it's on.",
                Buttons(Widen(ValueButton(p.EditMode ? "Turn off" : "Turn on", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.ToggleEditMode)))))));

            courseRows.Children.Add(OptionRow("Reverse course",
                "Fly the course the other way round: the checkpoints go in the opposite order (the last becomes the first), every gate is turned round, and on a point-to-point course the start and finish swap. The reversed course has its own best times; reversing it again brings the original's back. Also on a key in edit mode (Controls tab).",
                Buttons(Widen(ValueButton("Reverse", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.ReverseCourse)))))));

            // ---- world save
            Header("World save");
            courseRows.Children.Add(OptionRow("Save into the world now",
                (p.WorldFolder != null ? $"Courses belong to the world they're made in: the course and the courses saved above are written into the world's save folder ({GateSettings.WorldCourses.FileName}) every time you save the game, like the rest of the world. Changes you don't save are gone when you leave without saving. This button writes them straight away. Folder: {p.WorldFolder}"
                                       : "Courses are written into the world's save folder every time you save the game. The save folder hasn't been found yet: save the world once."),
                Buttons(Widen(ValueButton("Save to world", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.SaveToWorld)))))));
            courseRows.Children.Add(OptionRow("Back to the saved course",
                p.WorldHasCourses ? "Puts back the course as it was when the world was last saved (replaces the current gate and checkpoints)."
                                  : "This world's save has no race course yet.",
                Buttons(Widen(ValueButton("Load from world", White, 0, () => ShowConfirm("This replaces the current gate and checkpoints with the course saved in the world.", () => commands.Enqueue(new MenuCommand(MenuAction.LoadFromWorld))))))));

            // ---- build
            Header("Build");
            courseRows.Children.Add(OptionRow("Auto-build from names",
                "Finds grids (or antennas on them) named Start / Finish and CP1, CP2... (or just 1, 2, 3...) and builds the course from them in number order, fitted to their rings. Replaces the current checkpoints.",
                Buttons(Widen(ValueButton("Auto-build", White, 0, () => ShowConfirm("This replaces the current gate and checkpoints with ones built from grid and antenna names.", () => commands.Enqueue(new MenuCommand(MenuAction.AutoBuild))))))));
            courseRows.Children.Add(OptionRow("Antennas in this world",
                "Switch every antenna in the world off (their markers disappear from the HUD) or back on. Same as their on/off switch in the terminal.",
                Buttons(ValueButton("Turn all off", White, 200, () => commands.Enqueue(new MenuCommand(MenuAction.AntennasOff))),
                        ValueButton("Turn all on", White, 200, () => commands.Enqueue(new MenuCommand(MenuAction.AntennasOn))))));

            // ---- checkpoints
            Header("Checkpoints");
            if (p.CourseCheckpoints.Count == 0)
                courseRows.Children.Add(OptionRow("No checkpoints", "Add checkpoints with the Add checkpoint shortcut, or use Auto-build.", null));
            // A slim list: the checkpoint's number in an orange tag (like its marker in the world), how far it is from
            // the one before, and small icon buttons on the right to move it earlier / later or delete it.
            if (p.CourseCheckpoints.Count > 0)
            {
                var cpList = new StackPanel { Margin = new Thickness(24 * s, 0, 24 * s, 6 * s) };
                Button Icon(object glyph, IBrush brush, bool enabled, string tip, Action click)
                {
                    var b = new Button
                    {
                        Content = glyph, FontSize = 15 * s, Foreground = enabled ? brush : Argb(90, 160, 170, 178),
                        Background = Argb(255, 14, 34, 48), Width = 34 * s, Height = 30 * s, MinHeight = 0, Padding = new Thickness(0),
                        Margin = new Thickness(4 * s, 0, 0, 0), IsEnabled = enabled,
                        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    SmallTip(b, tip);
                    b.Click += (_, __) => click();
                    return b;
                }
                for (int i = 0; i < p.CourseCheckpoints.Count; i++)
                {
                    int row = i;
                    string text = p.CourseCheckpoints[i] ?? "";
                    // "CP 3   3.3 km from CP 2": the number, then the rest.
                    string number = $"CP {i + 1}", rest = text;
                    if (text.StartsWith("CP ", StringComparison.Ordinal))
                    {
                        int sp = text.IndexOf(' ', 3);
                        if (sp > 0) { number = text.Substring(0, sp); rest = text.Substring(sp).Trim(); }
                        else { number = text; rest = ""; }
                    }
                    var g = new Grid { Height = 38 * s };
                    g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70 * s)));
                    g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                    g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    var tagText = Text(number, Brush(HudColor.Orange));
                    SoftFontSize(tagText, 15 * s);
                    tagText.FontWeight = FontWeight.Bold;
                    var tag = new Border
                    {
                        Child = tagText, BorderBrush = Brush(HudColor.Orange.WithAlpha(170)), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3 * s), Padding = new Thickness(7 * s, 1 * s, 7 * s, 2 * s),
                        Background = Argb(150, 40, 24, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    };
                    g.Children.Add(tag);
                    var distance = Text(rest, White);
                    SoftFontSize(distance, 17 * s);
                    distance.VerticalAlignment = VerticalAlignment.Center;
                    distance.TextTrimming = TextTrimming.CharacterEllipsis;
                    Grid.SetColumn(distance, 1); g.Children.Add(distance);
                    var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    tools.Children.Add(Icon("\u25B2", White, i > 0, "Move it earlier in the course", () => commands.Enqueue(new MenuCommand(MenuAction.CheckpointUp, row))));
                    tools.Children.Add(Icon("\u25BC", White, i < p.CourseCheckpoints.Count - 1, "Move it later in the course", () => commands.Enqueue(new MenuCommand(MenuAction.CheckpointDown, row))));
                    tools.Children.Add(Icon(Cross(Brush(HudColor.Red)), Brush(HudColor.Red), true, "Delete it",
                        () => ShowConfirm($"Delete checkpoint {row + 1}? The ones after it move up a place, and best splits for this course may no longer match.", () => commands.Enqueue(new MenuCommand(MenuAction.DeleteCheckpoint, row)))));
                    Grid.SetColumn(tools, 2); g.Children.Add(tools);
                    var rowBox = new Border { Child = g, Padding = new Thickness(6 * s, 0, 4 * s, 0) };
                    SmallTip(rowBox, "Move this checkpoint earlier or later in the course, or delete it. The race resets, and the course's best checkpoint times are cleared when the order changes.");
                    cpList.Children.Add(rowBox);
                    if (i < p.CourseCheckpoints.Count - 1)
                        cpList.Children.Add(new Border { Height = 1, Background = Argb(60, 120, 150, 170), Margin = new Thickness(6 * s, 0, 6 * s, 0) });
                }
                courseRows.Children.Add(cpList);
            }

            // ---- saved courses
            Header("Saved courses in this world");
            if (p.SavedCourses.Count == 0)
                courseRows.Children.Add(OptionRow("None yet", "Save the current course above to keep it and switch between courses.", null));
            foreach (var (name, info, current) in p.SavedCourses)
            {
                string n = name;
                var load = ValueButton(current ? "Loaded" : "Load", current ? Grey : White, 130, () => commands.Enqueue(new MenuCommand(MenuAction.LoadCourse, text: n)));
                var del = ValueButton("Delete", Brush(HudColor.Red), 110, () =>
                    ShowConfirm($"Delete the saved course '{n}'? Its best times stay (they belong to the gate position).", () => commands.Enqueue(new MenuCommand(MenuAction.DeleteCourse, text: n))));
                var rename = ValueButton("Rename", White, 120, () =>
                    commands.Enqueue(new MenuCommand(MenuAction.RenameCourse, text: n + "\n" + (courseNameBox.Text ?? ""))));
                courseRows.Children.Add(OptionRow($"{name}   ({info})",
                    $"Load '{name}' to race it (replaces the current gate and checkpoints), or delete it. To rename it, type the new name in the Save as box above and press Rename.",
                    Buttons(load, rename, del)));
            }
        }

        private static Button Widen(Button b) { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.MinWidth = 200; return b; }

        // ---------------------------------------------------------------- Records tab

        private void FillRecords(HudModel.ControlsPanel p)
        {
            double s = scale;
            recordRows.Children.Clear();

            var title = Text(p.RecordsTitle ?? "", null, "SubTitle", "Bright");
            title.TextWrapping = TextWrapping.Wrap;
            title.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
            SoftFontSize(title, 18 * s);
            recordRows.Children.Add(title);

            var watch = ValueButton("Watch best lap", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.WatchReplay)));
            watch.HorizontalAlignment = HorizontalAlignment.Stretch;
            recordRows.Children.Add(OptionRow("Best-lap replay",
                "Watch your best lap on this course: the spectator camera follows the ghost of your ship round the lap. It starts when you close the menu, puts you back in your seat when it ends, and the replay shortcut (Controls tab) stops it early.",
                watch));

            // Which lap counts to show: a switch for each, all on at first (remembered in RecordsHidden.json).
            lastRecordsPanel = p;
            var groups = p.Records.Select(r => r.Group).Distinct().ToList();
            if (groups.Count > 1)
            {
                var chips = new WrapPanel { Margin = new Thickness(24 * s, 4 * s, 24 * s, 2 * s) };
                var showLabel = Text("Show:", Grey);
                SoftFontSize(showLabel, 18 * s);
                showLabel.VerticalAlignment = VerticalAlignment.Center;
                showLabel.Margin = new Thickness(0, 0, 10 * s, 0);
                chips.Children.Add(showLabel);
                void Chip(string text, bool on, Action click)
                {
                    var b = ValueButton(text, on ? Brush(HudColor.Purple) : Grey, 0, click);
                    b.Margin = new Thickness(0, 0, 8 * s, 4 * s);
                    b.Opacity = on ? 1 : 0.6;
                    chips.Children.Add(b);
                }
                bool allOn = groups.All(g => !HiddenRecordGroups.Contains(g));
                Chip("All", allOn, () => { foreach (var g in groups) HiddenRecordGroups.Remove(g); SaveHiddenGroups(); FillRecords(lastRecordsPanel); });
                foreach (var g in groups)
                {
                    string grp = g;
                    bool on = !HiddenRecordGroups.Contains(grp);
                    // A lap count's name: "1 LAP" -> "1 lap". Clicking toggles it; with all showing, the first click shows only that one.
                    Chip(grp.ToLowerInvariant(), on && !allOn, () =>
                    {
                        if (groups.All(x => !HiddenRecordGroups.Contains(x)))
                            foreach (var x in groups) { if (x != grp) HiddenRecordGroups.Add(x); }
                        else if (!HiddenRecordGroups.Remove(grp)) HiddenRecordGroups.Add(grp);
                        if (groups.All(x => HiddenRecordGroups.Contains(x))) foreach (var x in groups) HiddenRecordGroups.Remove(x);   // never none
                        SaveHiddenGroups();
                        FillRecords(lastRecordsPanel);
                    });
                }
                recordRows.Children.Add(chips);
            }

            string group = null;
            foreach (var r in p.Records)
            {
                if (groups.Count > 1 && HiddenRecordGroups.Contains(r.Group)) continue;
                if (r.Group != group)
                {
                    group = r.Group;
                    recordRows.Children.Add(Section(group));
                    recordRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
                    recordRows.Children.Add(RecordLine(s, "#", "TOTAL", "BEST LAP", "PENALTIES", "TOP SPEED", "SHIP", "DATE", Grey, Grey, false));
                }
                var colour = r.IsRecord ? Brush(HudColor.Purple) : White;
                var pen = r.Penalty == "clean" ? Brush(HudColor.Green) : Brush(HudColor.Red);
                var line = RecordLine(s, r.Rank.ToString(), r.Total, r.BestLap, r.Penalty, r.TopSpeed, r.Ship, r.Date, colour, pen, r.IsRecord);
                recordRows.Children.Add(line);
            }

            // Ships compared: best times for each ship you've raced this course in.
            recordRows.Children.Add(Section("Ships on this course"));
            recordRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            if (p.Ships.Count == 0)
            {
                var none = Text(p.CurrentShip != null
                    ? $"No ship times yet. Finish a race and {p.CurrentShip}'s times are saved here, then race another ship to compare."
                    : "No ship times yet. Finish a race in a ship and its times are saved here (by the ship's name), so you can compare ships.", Grey);
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
                SoftFontSize(none, 18 * s);
                recordRows.Children.Add(none);
            }
            else
            {
                recordRows.Children.Add(ShipLine(s, null, "SHIP", "BEST LAP", "GAP", p.ShipsRaceHeader, "GAP", p.ShipsAverageHeader, "TOP SPEED", "TURN G", "RACES", Grey));
                foreach (var sh in p.Ships)
                    recordRows.Children.Add(ShipLine(s, sh, (sh.Current ? "> " : "") + sh.Name, sh.BestLap, sh.Gap, sh.BestRace, sh.RaceGap, sh.Average, sh.TopSpeed, sh.TurnG, sh.Races, White));
                var note = Text("Times are saved for the ship you're flying, by its name: rename a ship in its terminal to keep separate times for each version. > marks the ship you're in now. AVG is its average race time here, over every race of that many laps in the history. Race best / Watch best race against or replay its best lap; Race last / Watch last do the same with the last lap you flew in it. Export saves its best lap as a ghost file.", Grey);
                note.TextWrapping = TextWrapping.Wrap;
                note.Margin = new Thickness(24 * s, 6 * s, 24 * s, 8 * s);
                SoftFontSize(note, 16 * s);
                recordRows.Children.Add(note);
            }

            // Ghost files: export your best line, load other players' (or your own older) ones.
            recordRows.Children.Add(Section("Ghost files"));
            recordRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var about = Text("A ghost file is a best lap (the line flown, how the ship was turned, its shape and speed) that you can share. "
                + "Put files from other players in the ghosts folder, then Watch them or Race against them: the ghost, racing line and live gap then follow that lap. "
                + "A ghost only fits the course it was flown on (same start gate and number of checkpoints, in the same world).", Grey);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
            SoftFontSize(about, 16 * s);
            recordRows.Children.Add(about);

            recordRows.Children.Add(OptionRow("Export course best",
                "Save your best lap on this course to a ghost file in the ghosts folder.",
                Widen(ValueButton(p.CourseHasBest ? "Export" : "No best lap yet", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.ExportGhost))))));

            if (pilotNameBox.Parent is Panel oldPilotParent) oldPilotParent.Children.Remove(pilotNameBox);
            if (string.IsNullOrEmpty(pilotNameBox.Text) && p.PilotName != null) pilotNameBox.Text = p.PilotName;
            var setName = ValueButton("Set", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.SetPilotName, text: pilotNameBox.Text)));
            setName.Margin = new Thickness(8 * s, 0, 0, 0);
            var pilotRow = new Grid();
            pilotRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            pilotRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(pilotNameBox, 0); pilotRow.Children.Add(pilotNameBox);
            Grid.SetColumn(setName, 1); pilotRow.Children.Add(setName);
            recordRows.Children.Add(OptionRow("Pilot name", "Your name, shown on ghost files you export so others know whose lap it is.", pilotRow));

            var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s };
            folderButtons.Children.Add(ValueButton("Open folder", White, 170, () => commands.Enqueue(new MenuCommand(MenuAction.OpenGhostFolder))));
            folderButtons.Children.Add(ValueButton("Reload", White, 120, () => commands.Enqueue(new MenuCommand(MenuAction.RefreshGhosts))));
            recordRows.Children.Add(OptionRow("Ghosts folder", p.GhostFolder ?? "", folderButtons));

            if (p.RacingAgainst != null)
                recordRows.Children.Add(OptionRow("Racing against",
                    $"{p.RacingAgainst}. Press to go back to racing against your own best.",
                    Widen(ValueButton("Race my best", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.RaceCourseBest))))));

            if (p.Ghosts.Count == 0)
            {
                var none = Text("No ghost files yet. Export one above, or copy .rdghost.json files from other players into the ghosts folder and press Reload.", Grey);
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
                SoftFontSize(none, 17 * s);
                recordRows.Children.Add(none);
            }
            foreach (var gr in p.Ghosts)
            {
                string path = gr.Path;
                var row = new Grid { Margin = new Thickness(24 * s, 3 * s, 24 * s, 3 * s) };
                row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var info = new StackPanel();
                var name = Text((gr.Active ? "> " : "") + gr.Label, gr.Active ? Brush(HudColor.Purple) : gr.Fits ? White : Grey);
                SoftFontSize(name, 19 * s);
                name.FontWeight = FontWeight.Bold;
                var detail = Text(gr.Detail ?? "", Grey);
                SoftFontSize(detail, 15 * s);
                info.Children.Add(name);
                info.Children.Add(detail);
                row.Children.Add(info);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s, VerticalAlignment = VerticalAlignment.Center };
                if (gr.Fits)
                {
                    buttons.Children.Add(ValueButton("Watch", White, 100, () => commands.Enqueue(new MenuCommand(MenuAction.WatchGhostFile, text: path))));
                    buttons.Children.Add(ValueButton(gr.Active ? "Racing" : "Race", White, 100, () => commands.Enqueue(new MenuCommand(MenuAction.RaceGhostFile, text: path))));
                    buttons.Children.Add(ValueButton(gr.Second ? "2nd: on" : "2nd ghost", gr.Second ? Brush(HudColor.Orange) : White, 120,
                        () => commands.Enqueue(new MenuCommand(MenuAction.SecondGhostFile, text: path))));
                }
                buttons.Children.Add(ValueButton("Delete", White, 100, () => ShowConfirm("Delete this ghost file? It can't be undone.",
                    () => commands.Enqueue(new MenuCommand(MenuAction.DeleteGhostFile, text: path)))));
                Grid.SetColumn(buttons, 1);
                row.Children.Add(buttons);
                recordRows.Children.Add(row);
            }

            // Records: reset best times (asks first).
            recordRows.Children.Add(Section("Reset"));
            recordRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * scale, 0, 24 * scale, 4 * scale) });
            recordRows.Children.Add(OptionRow("Best times for this course",
                "Forget the best lap, best splits, best race times and race history for the course you're on.",
                ConfirmButton("Reset", "This clears the best lap, best splits, best race times and race history for this course. It can't be undone.", MenuAction.ClearCourseRecords)));
            recordRows.Children.Add(OptionRow("Best times for all courses",
                "Forget the best times for every course in every world.",
                ConfirmButton("Reset all", "This clears the best times and race history for every course in every world. It can't be undone.", MenuAction.ClearAllRecords)));
        }

        /// <summary>One row of the ship comparison (sh = null for the heading), with a Delete button.</summary>
        private Grid ShipLine(double s, HudModel.ShipRow sh, string name, string lap, string gap, string race, string raceGap,
            string average, string top, string turn, string races, IBrush colour)
        {
            // Two lines per ship: the full name on top (never cut off, wraps if very long), then its times and the
            // buttons, lined up under each other in columns.
            var g = new Grid { Margin = new Thickness(24 * s, 6 * s, 24 * s, 6 * s) };
            double[] widths = { 20, 140, 110, 140, 120, 140, 120, 90, 70, 0 };
            foreach (var w in widths)
                g.ColumnDefinitions.Add(new ColumnDefinition(w > 0 ? new GridLength(w * s, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star)));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (sh != null)
            {
                // A faint card behind each ship (its name, times and buttons), edged in cyan for the one you're in:
                // the list was one run of text and buttons with nothing to say where one ship ended.
                var shipCard = new Border
                {
                    Background = Argb(46, 22, 38, 54),
                    BorderBrush = sh.Current ? Argb(150, 70, 190, 230) : Argb(70, 120, 140, 160),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4 * s),
                    Margin = new Thickness(-10 * s, -5 * s, -10 * s, -1 * s), IsHitTestVisible = false,
                };
                Grid.SetRowSpan(shipCard, 3); Grid.SetColumnSpan(shipCard, widths.Length);
                g.Children.Add(shipCard);
                g.Margin = new Thickness(24 * s, 9 * s, 24 * s, 7 * s);
            }
            // (The heading: small grey words on tight lines, like the other tables' headings.)
            if (sh == null) g.Margin = new Thickness(24 * s, 4 * s, 24 * s, 2 * s);
            string[] texts = { name, lap, gap, race, raceGap, average, top, turn, races };
            for (int i = 0; i < texts.Length; i++)
            {
                IBrush b = colour;
                if (sh != null)
                {
                    if ((i == 1 || i == 2) && sh.Fastest) b = Brush(HudColor.Purple);
                    else if ((i == 3 || i == 4) && sh.FastestRace) b = Brush(HudColor.Purple);
                    else if (i == 2 || i == 4) b = Brush(HudColor.Yellow);
                    else if (i == 0 && sh.Current) b = Brush(HudColor.Cyan);
                }
                var t = Text(texts[i] ?? "", b);
                SoftFontSize(t, (sh == null ? 14 : 19) * s);
                t.VerticalAlignment = VerticalAlignment.Center;
                if (i == 5 && sh != null && !string.IsNullOrEmpty(sh.AverageNote))
                    SmallTip(t, $"Average of {sh.AverageNote} in the race history (penalties included)");
                if (i == 0)
                {
                    // The name: its own line across the whole row.
                    if (sh != null) t.FontWeight = FontWeight.Bold;
                    t.TextWrapping = TextWrapping.Wrap;
                    Grid.SetRow(t, 0); Grid.SetColumn(t, 0); Grid.SetColumnSpan(t, widths.Length);
                }
                else
                {
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    Grid.SetRow(t, 1); Grid.SetColumn(t, i);
                }
                g.Children.Add(t);
            }
            if (sh != null)
            {
                // Buttons on their own line under the times, wrapping onto another line if the window is narrow.
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                string ship = sh.Name;
                var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16 * s, 4 * s, 0, 0) };
                void Add(Button b) { b.Margin = new Thickness(0, 0, 6 * s, 6 * s); b.Padding = new Thickness(12 * s, 0, 12 * s, 0); buttons.Children.Add(b); }
                if (sh.HasReplay)
                {
                    // Race against this ship's best lap (ghost, racing line and live gap), like the Race tab.
                    Add(ValueButton(sh.Racing ? "Racing best" : "Race best", sh.Racing ? Brush(HudColor.Purple) : White, 0,
                        () => commands.Enqueue(new MenuCommand(MenuAction.RaceShip, text: ship))));
                    Add(ValueButton("Watch best", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.WatchShipReplay, text: ship))));
                }
                if (sh.LastLap != null)
                {
                    // The last lap flown in this ship, whatever its time.
                    Add(ValueButton(sh.RacingLast ? $"Racing last ({sh.LastLap})" : $"Race last ({sh.LastLap})", sh.RacingLast ? Brush(HudColor.Purple) : White, 0,
                        () => commands.Enqueue(new MenuCommand(MenuAction.RaceShipLast, text: ship))));
                    Add(ValueButton("Watch last", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.WatchShipLast, text: ship))));
                }
                if (sh.HasReplay)
                {
                    Add(ValueButton("Export", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.ExportGhost, text: ship))));
                    Add(ValueButton(sh.Second ? "2nd ghost: on" : "2nd ghost", sh.Second ? Brush(HudColor.Orange) : White, 0,
                        () => commands.Enqueue(new MenuCommand(MenuAction.SecondGhostShip, text: ship))));
                }
                Add(ValueButton("Delete", White, 0, () => ShowConfirm($"Forget the times for {ship} on this course? It can't be undone.",
                    () => commands.Enqueue(new MenuCommand(MenuAction.DeleteShip, text: ship)))));
                Grid.SetRow(buttons, 2);
                Grid.SetColumn(buttons, 0);
                Grid.SetColumnSpan(buttons, widths.Length);
                g.Children.Add(buttons);
            }
            return g;
        }

        private Grid RecordLine(double s, string rank, string total, string bestLap, string penalty, string top, string ship, string date,
            IBrush colour, IBrush penaltyColour, bool bold)
        {
            var g = new Grid { Margin = new Thickness(24 * s, 3 * s, 24 * s, 3 * s) };
            double[] widths = { 50, 170, 170, 130, 140, 220, 0 };
            for (int i = 0; i < widths.Length; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition(widths[i] > 0 ? new GridLength(widths[i] * s, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star)));
            string[] texts = { rank, total, bestLap, penalty, top, ship, date };
            for (int i = 0; i < texts.Length; i++)
            {
                var t = Text(texts[i] ?? "", i == 3 ? penaltyColour : colour);
                SoftFontSize(t, 19 * s);
                if (bold && i == 1) t.FontWeight = FontWeight.Bold;
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }

        /// <summary>A button that asks "Are you sure?" in a popup before sending the action.</summary>
        private Control ConfirmButton(string text, string question, MenuAction action)
        {
            var button = ValueButton(text, White, 0, () => ShowConfirm(question, () => commands.Enqueue(new MenuCommand(action))));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            return button;
        }

        private Grid confirmLayer;

        /// <summary>A popup over the menu: the question, then Yes and Cancel.</summary>
        private void ShowConfirm(string question, Action onYes)
        {
            double s = scale;
            CloseConfirm();

            var title = Text("Are you sure?", White, "Header");
            SoftFontSize(title, 32 * s);
            title.HorizontalAlignment = HorizontalAlignment.Center;

            var message = Text(question, null, "SubTitle", "Bright");
            message.TextWrapping = TextWrapping.Wrap;
            message.TextAlignment = TextAlignment.Center;
            message.HorizontalAlignment = HorizontalAlignment.Center;
            SoftFontSize(message, 20 * s);

            var yes = MenuButton("Yes", () => { CloseConfirm(); onYes(); });
            var cancel = MenuButton("Cancel", CloseConfirm);
            yes.VerticalAlignment = cancel.VerticalAlignment = VerticalAlignment.Center;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 24 * s,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { yes, cancel },
            };

            var content = new StackPanel
            {
                Spacing = 22 * s,
                Margin = new Thickness(40 * s, 30 * s, 40 * s, 30 * s),
                Children = { title, message, buttons },
            };

            Control box;
            if (NewGameControl("Keen.VRage.UI.Shared.Controls.BeveledBorder.BeveledBorder") is Decorator bevel)
            {
                bevel.Classes.Add("PrimaryPanel");
                bevel.Child = content;
                box = bevel;
            }
            else
            {
                box = new Border
                {
                    Background = Argb(245, 14, 20, 26),
                    BorderBrush = Argb(160, 200, 210, 220),
                    BorderThickness = new Thickness(1),
                    Child = content,
                };
            }
            box.Width = 720 * s;
            box.HorizontalAlignment = HorizontalAlignment.Center;
            box.VerticalAlignment = VerticalAlignment.Center;

            // Dims the menu and blocks clicks to it until Yes or Cancel.
            confirmLayer = new Grid { Background = Argb(170, 0, 0, 0), Children = { box } };
            // In the terminal tab (G) there's no pause-menu overlay: the question goes over the tab's own page.
            Panel holder = overlay != null && Avalonia.VisualTree.VisualExtensions.GetVisualRoot(overlay) != null ? overlay : (Panel)terminalHost ?? overlay;
            if (holder == null) { onYes(); confirmLayer = null; return; }
            if (holder is Grid g && g.RowDefinitions.Count > 1) Grid.SetRowSpan(confirmLayer, g.RowDefinitions.Count);
            holder.Children.Add(confirmLayer);
            holder.Focus();
        }

        private void CloseConfirm()
        {
            if (confirmLayer?.Parent is Panel parent) parent.Children.Remove(confirmLayer);
            confirmLayer = null;
        }

        private void Send(int index, double value)
        {
            commands.Enqueue(new MenuCommand(MenuAction.SetValue, index, value));
            SettingChangedLocally(index, value);
        }

        /// <summary>Off | On: the game's own Settings switch, in its own style.</summary>
        private Control MakeSwitch(HudModel.SettingRow r)
        {
            int index = r.Index;
            var sw = NewGameControl("Keen.Game2.Client.UI.Library.Controls.ContentControls.Switch");
            if (sw != null && TrySet(sw, "IsChecked", r.Value >= 0.5))
            {
                sw.Classes.Add("OptionSubTitle"); // the game's settings style for it (full-width Off | On)
                sw.HorizontalAlignment = HorizontalAlignment.Stretch;
                sw.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == "IsChecked" && e.NewValue is bool on) Send(index, on ? 1 : 0);
                };
                return sw;
            }

            // Fallback: Avalonia's own toggle switch.
            var ts = new ToggleSwitch { IsChecked = r.Value >= 0.5, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
            ts.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty) Send(index, ts.IsChecked == true ? 1 : 0);
            };
            return ts;
        }

        private static readonly IBrush SliderBack = Argb(255, 8, 28, 40);
        private static readonly IBrush SliderEdge = Argb(200, 80, 105, 118);
        private static readonly IBrush SliderFill = Argb(235, 150, 174, 182);
        private static readonly IBrush SliderArrow = Brushes.White;
        private static readonly IBrush SliderArrowHover = Argb(255, 255, 190, 60);

        /// <summary>
        /// A slider drawn like the game's Settings sliders: ◀ [light fill | value in the middle] ▶ on a dark bar.
        /// Click or drag on the bar to set it; the arrows step by the setting's step. (Our own control, so the
        /// game's slider template can't add stray labels or change colours.)
        /// </summary>
        private Control MakeSlider(HudModel.SettingRow r, string unit = null, bool compact = false)
        {
            int index = r.Index;
            double s = scale;
            double value = r.Value;

            var fillColumns = new Grid();
            fillColumns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            fillColumns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            var fill = new Border { Background = SliderFill };
            Grid.SetColumn(fill, 0);
            fillColumns.Children.Add(fill);

            var valueText = Text("", White);
            valueText.HorizontalAlignment = HorizontalAlignment.Center;
            SoftFontSize(valueText, (compact ? 16 : 20) * s);

            var track = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
            track.Children.Add(fillColumns);
            track.Children.Add(valueText);

            void Show()
            {
                double f = r.Max > r.Min ? (value - r.Min) / (r.Max - r.Min) : 0;
                f = Math.Max(0, Math.Min(1, f));
                fillColumns.ColumnDefinitions[0].Width = new GridLength(f, GridUnitType.Star);
                fillColumns.ColumnDefinitions[1].Width = new GridLength(1 - f, GridUnitType.Star);
                fill.IsVisible = f > 0;
                int choice = (int)Math.Round(value);
                valueText.Text = r.Choices != null && choice >= 0 && choice < r.Choices.Length
                    ? r.Choices[choice]
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, r.Format, value)
                      // (The unit from the setting's name, unless the number's own format already ends in words.)
                      + (unit == null || !(r.Format ?? "").TrimEnd().EndsWith("}", StringComparison.Ordinal) ? "" : unit == "%" || unit == "\u00B0" ? unit : " " + unit);
            }

            void SetValue(double v)
            {
                if (r.Step > 0) v = r.Min + Math.Round((v - r.Min) / r.Step) * r.Step;
                v = Math.Max(r.Min, Math.Min(r.Max, Math.Round(v, 6)));
                if (Math.Abs(v - value) < 1e-9) return;
                value = v;
                Show();
                Send(index, v);
            }

            void FromPointer(Avalonia.Input.PointerEventArgs e)
            {
                double w = track.Bounds.Width;
                if (w <= 0) return;
                double f = Math.Max(0, Math.Min(1, e.GetPosition(track).X / w));
                SetValue(r.Min + f * (r.Max - r.Min));
            }

            bool dragging = false;
            track.PointerPressed += (_, e) => { dragging = true; e.Pointer.Capture(track); FromPointer(e); e.Handled = true; };
            track.PointerMoved += (_, e) => { if (dragging) FromPointer(e); };
            track.PointerReleased += (_, e) => { dragging = false; e.Pointer.Capture(null); e.Handled = true; };
            track.PointerCaptureLost += (_, __) => dragging = false;

            Control Arrow(bool left)
            {
                var shape = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(left ? "M 10,0 L 0,7 L 10,14 Z" : "M 0,0 L 10,7 L 0,14 Z"),
                    Fill = SliderArrow,
                    Stretch = Stretch.Uniform,
                    Width = (compact ? 9 : 11) * s,
                    Height = (compact ? 12 : 15) * s,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var hit = new Border { Background = Brushes.Transparent, Width = 44 * s, Child = shape };
                hit.PointerEntered += (_, __) => shape.Fill = SliderArrowHover;
                hit.PointerExited += (_, __) => shape.Fill = SliderArrow;
                hit.PointerPressed += (_, e) =>
                {
                    SetValue(value + (left ? -1 : 1) * (r.Step > 0 ? r.Step : (r.Max - r.Min) / 20));
                    e.Handled = true;
                };
                return hit;
            }

            var bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var leftArrow = Arrow(true);
            var rightArrow = Arrow(false);
            Grid.SetColumn(leftArrow, 0); bar.Children.Add(leftArrow);
            Grid.SetColumn(track, 1); bar.Children.Add(track);
            Grid.SetColumn(rightArrow, 2); bar.Children.Add(rightArrow);

            Show();
            return new Border
            {
                Background = SliderBack,
                BorderBrush = SliderEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4 * s),
                Height = 46 * s,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = bar,
            };
        }
    }
}
