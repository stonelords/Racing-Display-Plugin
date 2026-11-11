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
        private Button resetButton;
        private int builtRecordsVersion = -1, builtRpiVersion = -1;
        private StackPanel rpiRows;
        private ScrollViewer rpiPage;
        private TabItem rpiTab;
        private Control designerView;
        private Border pageDivider;
        private ScrollViewer descScroll;
        private HudModel.ControlsPanel lastRpiPanel;
        private bool OnRpiTab => tabs != null && tabs.SelectedItem == rpiTab;
        private TextBlock note, description;
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
            if (shown && open) Apply();   // fill the page if its contents arrived before it was shown
        }

        private void SelectPage(string name)
        {
            if (tabs == null) return;
            shownTerminalPage = name;
            var tab = name == "Records" ? recordsTab : name == "RPI" ? rpiTab : name == "Courses" ? coursesTab : name == "Settings" ? settingsTab : name == "Controls" ? controlsTab : raceTab;
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
                    FillControls(p);
                }
                bool raceChanged = false;
                if (p.SettingsVersion != builtSettingsVersion)
                {
                    builtSettingsVersion = p.SettingsVersion;
                    FillSettings(p);
                    raceChanged = true;
                }
                if (p.RecordsVersion != builtRecordsVersion)
                {
                    builtRecordsVersion = p.RecordsVersion;
                    FillRecords(p);
                    raceChanged = true;
                }
                if (raceChanged) FillRace(p);
                if (p.RpiVersion != builtRpiVersion)
                {
                    builtRpiVersion = p.RpiVersion;
                    FillRpi(p);
                }
                if (p.CoursesVersion != builtCoursesVersion)
                {
                    builtCoursesVersion = p.CoursesVersion;
                    FillCourses(p);
                }
            }
            catch (Exception e)
            {
                PluginLog.Error("Racing Display menu update failed", e);
                CloseNow();
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
            tabs.Items.Add(raceTab);
            tabs.Items.Add(recordsTab);
            tabs.Items.Add(coursesTab);
            tabs.Items.Add(rpiTab);
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
            pilotNameBox = new TextBox { Watermark = "Your name (optional)", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            sectorNameBox = new TextBox { Watermark = "Sector name", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            controlsPage = new ScrollViewer { Content = controlRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // Search box above the settings: type part of a setting's name (or words from its description).
            settingsSearch = new TextBox { Watermark = "Search settings", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(16 * s, 4 * s, 24 * s, 8 * s) };
            settingsSearch.TextChanged += (_, __) => { if (lastSettingsPanel != null) FillSettings(lastSettingsPanel); };
            presetNameBox = new TextBox { Watermark = "Preset name (e.g. Testing, Race day)", MinWidth = 260 * s, VerticalContentAlignment = VerticalAlignment.Center };
            presetRows = new StackPanel();
            settingsPage = new ScrollViewer
            {
                Content = new StackPanel { Children = { presetRows, settingsSearch, settingRows } },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            page = new ContentControl();

            note = Text("", Brush(HudColor.Yellow));
            SoftFontSize(note, 17 * s);
            note.Margin = new Thickness(16 * s, 6 * s, 0, 0);
            note.IsVisible = false;

            description = Text("", null, "SubTitle", "Bright");
            description.TextWrapping = TextWrapping.Wrap;
            SoftFontSize(description, 17 * s);
            description.Margin = new Thickness(20 * s, 4 * s, 8 * s, 0);
            description.VerticalAlignment = VerticalAlignment.Top;

            // The list (settings, controls, records ...) on the left; the description of whatever you point at
            // in its own column on the right, so it never covers a row.
            var inner = new Grid { Margin = new Thickness(24 * s, 24 * s, 24 * s, 18 * s) };
            inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Pixel)));
            inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(440 * s, GridUnitType.Pixel)));
            inner.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            inner.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(page, 0); Grid.SetColumn(page, 0); inner.Children.Add(page);
            Grid.SetRow(note, 1); Grid.SetColumn(note, 0); inner.Children.Add(note);
            var divider = new Border { Background = SectionLine, Margin = new Thickness(0, 4 * s, 0, 4 * s) };
            pageDivider = divider;
            Grid.SetColumn(divider, 1); Grid.SetRowSpan(divider, 2); inner.Children.Add(divider);
            descScroll = new ScrollViewer { Content = description, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(descScroll, 2); Grid.SetRowSpan(descScroll, 2); inner.Children.Add(descScroll);
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
                commands.Enqueue(new MenuCommand(OnSettingsTab ? MenuAction.ResetSettings : MenuAction.ResetAll)));
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
            page.Content = designer ? designerView : OnRpiTab ? rpiPage : OnRaceTab ? racePage : OnCoursesTab ? coursesPage : OnInfoTab ? infoPage : OnRecordsTab ? recordsPage : OnSettingsTab ? settingsPage : controlsPage;
            defaultDescription = OnRpiTab
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
            if (resetButton != null) resetButton.IsVisible = !OnRecordsTab && !OnInfoTab && !OnCoursesTab && !OnRaceTab && !OnRpiTab; // those tabs have their own buttons
            description.Text = defaultDescription;
            overlay?.Focus(); // never leave focus on a slider while keys are being chosen
        }

        // ---------------------------------------------------------------- Controls tab

        private void FillControls(HudModel.ControlsPanel p)
        {
            controlRows.Children.Clear();
            string section = null;
            for (int i = 0; i < p.Rows.Count; i++)
            {
                var r = p.Rows[i];
                int row = i;
                if (r.Section != null && r.Section != section)
                {
                    section = r.Section;
                    controlRows.Children.Add(SectionHeader(section));
                    controlRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * scale, 0, 24 * scale, 4 * scale) });
                }
                IBrush textBrush = r.Capturing ? White : r.Clash ? Brush(HudColor.Red) : r.Changed ? Brush(HudColor.Yellow) : White;

                var bind = ValueButton(r.Binding, textBrush, 0, () => commands.Enqueue(new MenuCommand(MenuAction.Change, row)));
                if (r.Capturing) bind.Background = CaptureBack;

                var reset = ValueButton("Default", Grey, 110, () => commands.Enqueue(new MenuCommand(MenuAction.Default, row)));
                reset.Margin = new Thickness(8 * scale, 0, 0, 0);
                reset.Opacity = r.Changed ? 1 : 0; // keep the space so rows line up
                reset.IsHitTestVisible = r.Changed;

                bind.HorizontalAlignment = HorizontalAlignment.Stretch;
                var value = new Grid();
                value.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                value.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                Grid.SetColumn(bind, 0); value.Children.Add(bind);
                Grid.SetColumn(reset, 1); value.Children.Add(reset);
                string about = r.Capturing
                    ? "Press the new key combination (with Ctrl / Shift / Alt if you want them). Esc or click again to cancel."
                    : $"{r.Name}: {r.Binding}. Click to change it." + (r.Clash ? " This shortcut is also used by another action." : "");
                controlRows.Children.Add(OptionRow(r.Name, about, value));
            }

            note.Text = p.Note ?? "";
            note.IsVisible = !string.IsNullOrEmpty(note.Text);
            if (p.Rows.Any(r => r.Capturing)) description.Text = "Press the new key combination (with Ctrl / Shift / Alt if you want them). Esc or click again to cancel.";
        }

        // ---------------------------------------------------------------- Settings tab

        private TextBox settingsSearch;
        private HudModel.ControlsPanel lastSettingsPanel;

        private void FillSettings(HudModel.ControlsPanel p)
        {
            if (!ReferenceEquals(lastSettingsPanel, p) || presetRows.Children.Count == 0) FillPresets(p);
            lastSettingsPanel = p;
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
                .Where(Matches).OrderBy(r => order.IndexOf(r.Section)).ToList();
            if (rows.Count == 0)
            {
                var none = Text($"No settings match \"{q}\".", Grey);
                none.Margin = new Thickness(24 * s, 8 * s, 24 * s, 8 * s);
                SoftFontSize(none, 18 * s);
                settingRows.Children.Add(none);
            }
            string section = null;
            foreach (var r in rows)
            {
                if (r.Section != section)
                {
                    section = r.Section;
                    settingRows.Children.Add(CollapsibleHeader(section, searching));
                    settingRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
                }
                // A closed section shows just its header (searching opens every section).
                if (!searching && CollapsedSections.Contains(r.Section)) continue;

                var control = r.Toggle ? MakeSwitch(r) : MakeSlider(r);
                string about = r.Description;
                if (r.DependsOn != null)
                    about += $" (Only used with \"{r.DependsOn}\" {(r.DependsOnOff ? "off" : "on")}.)";
                var row = OptionRow(r.Name, about, control);
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
                var reset = ValueButton("Reset", Brush(HudColor.Orange), 84, () =>
                {
                    if (!settingViews.TryGetValue(index, out var view)) return;
                    Send(index, view.Row.Default);
                    if (lastSettingsPanel != null) FillSettings(lastSettingsPanel);   // shows the default value
                });
                reset.MinHeight = 30 * s;
                reset.HorizontalAlignment = HorizontalAlignment.Right;
                reset.VerticalAlignment = VerticalAlignment.Center;
                reset.Margin = new Thickness(0, 0, 8 * s, 0);
                reset.IsVisible = r.Changed;
                Grid.SetColumn(reset, 0);
                row.Children.Add(reset);
                // Keep the name clear of the Reset button.
                if (row.Children.Count > 0 && row.Children[0] is TextBlock nameText)
                    nameText.Margin = new Thickness(nameText.Margin.Left, 0, 96 * s, 0);
                resetButtons[r.Index] = reset;
                settingViews[r.Index] = (r, row, control, dot);
                SetRowEnabled(row, control, r.Enabled);
                settingRows.Children.Add(row);
            }
        }

        // Settings tab rows as shown, by setting index: to grey out dependants and move the "changed" dot as values change.
        private readonly Dictionary<int, (HudModel.SettingRow Row, Grid View, Control Value, Control Dot)> settingViews =
            new Dictionary<int, (HudModel.SettingRow, Grid, Control, Control)>();

        private readonly Dictionary<int, Button> resetButtons = new Dictionary<int, Button>();

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

        private Control CollapsibleHeader(string section, bool searching)
        {
            bool closed = !searching && CollapsedSections.Contains(section);
            // The arrow is drawn (the game's font has no arrow characters): pointing right when folded, down when open.
            double sc = scale;
            var head = SectionHeader(section);
            head.Margin = new Thickness(10 * sc, head.Margin.Top, 0, head.Margin.Bottom);
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
            var holder = new Border { Background = Brushes.Transparent, Child = line, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            holder.PointerPressed += (_, e) =>
            {
                if (searching) return;
                if (!CollapsedSections.Remove(section)) CollapsedSections.Add(section);
                try
                {
                    System.IO.Directory.CreateDirectory(PluginPaths.Folder);
                    System.IO.File.WriteAllText(SectionsFile, System.Text.Json.JsonSerializer.Serialize(CollapsedSections.ToList()));
                }
                catch { }
                e.Handled = true;
                if (lastSettingsPanel != null) FillSettings(lastSettingsPanel);
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
            presetRows.Children.Add(SectionHeader("Presets"));
            presetRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });

            if (p.Presets.Count > 0)
            {
                if (pickedPreset == null || !p.Presets.Contains(pickedPreset))
                    pickedPreset = p.CurrentPreset != null && p.Presets.Contains(p.CurrentPreset) ? p.CurrentPreset : p.Presets[0];
                var pick = new ComboBox { ItemsSource = p.Presets.ToList(), SelectedItem = pickedPreset, MinWidth = 220 * s };
                pick.SelectionChanged += (_, __) => { if (pick.SelectedItem is string n) pickedPreset = n; };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s };
                row.Children.Add(pick);
                row.Children.Add(ValueButton("Load", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.LoadPreset, text: pickedPreset))));
                row.Children.Add(ValueButton("Delete", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.DeletePreset, text: pickedPreset))));
                row.Children.Add(ValueButton("Export", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.ExportPreset, text: pickedPreset))));
                presetRows.Children.Add(OptionRow("Preset" + (p.CurrentPreset != null ? $" (now: {p.CurrentPreset})" : ""),
                    "Load puts every setting on this tab (and the laps on the Race tab) back to how they were when the preset was saved. Keys, records and courses aren't part of a preset.", row));
            }

            if (presetNameBox.Parent is Panel oldParent) oldParent.Children.Remove(presetNameBox);
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var save = ValueButton("Save", White, 110, () =>
            {
                commands.Enqueue(new MenuCommand(MenuAction.SavePreset, text: presetNameBox.Text));
                pickedPreset = presetNameBox.Text?.Trim();
                presetNameBox.Text = "";
            });
            save.Margin = new Thickness(8 * s, 0, 0, 0);
            Grid.SetColumn(presetNameBox, 0); nameRow.Children.Add(presetNameBox);
            Grid.SetColumn(save, 1); nameRow.Children.Add(save);
            presetRows.Children.Add(OptionRow("Save settings as",
                "Type a name (e.g. Testing or Race day) and press Save to keep all the current settings as a preset. Saving with an existing name updates it.", nameRow));

            // Sharing: Export writes the picked preset to a file in the presets folder; files there (yours, or ones
            // copied in from someone else) are listed with an Import button.
            presetRows.Children.Add(OptionRow("Preset files",
                $"Export writes a preset to a .racepreset.json file in {p.PresetFolder} to share. To use someone else's, copy their file into that folder (Open folder), then press Import next to it. Importing a preset with the same name as one you have replaces yours.",
                Widen(ValueButton("Open folder", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.OpenPresetFolder))))));
            foreach (var (name, path) in p.PresetFiles)
            {
                string file = path;
                presetRows.Children.Add(OptionRow("    " + name, $"Preset file: {System.IO.Path.GetFileName(path)}. Import adds it to your presets.",
                    Widen(ValueButton("Import", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.ImportPreset, text: file))))));
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

            rpiRows.Children.Add(SectionHeader("RPI designer"));
            rpiRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            rpiRows.Children.Add(OptionRow("RPI designer",
                "Make RPI classes: give any block in the game a cost, set the class budget and minimum weight, and export or import classes to share them.",
                Widen(ValueButton("Open", White, 0, OpenDesigner))));

            rpiRows.Children.Add(SectionHeader("Class in use"));
            rpiRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var about = Text(p.RpiClasses.Count == 0
                ? "No classes yet. Open the RPI designer to make one (or import one)."
                : "Pick the class the Ship analyser (Race tab) checks ships against: it shows a ship's total RPI against the class budget, the minimum weight and what the total is made of.", Grey);
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

        private static bool boxPartsLogged;

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
                    var parts = new List<string>();
                    foreach (var v in box.GetVisualDescendants())
                    {
                        string name = v is Control named ? named.Name : null;
                        parts.Add(v.GetType().Name + (string.IsNullOrEmpty(name) ? "" : "#" + name));
                        if (v is ScrollBar || v is Button || v is ToggleButton
                            || (name != null && (name.IndexOf("Clear", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Reveal", StringComparison.OrdinalIgnoreCase) >= 0)))
                            v.IsVisible = false;
                    }
                    if (!boxPartsLogged && parts.Count > 0) { boxPartsLogged = true; PluginLog.Info("RPI designer: a value box is made of: " + string.Join(", ", parts)); }
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
                    limits.Children.Add(Label("Budget (max RPI)", Grey, 16));
                    var budgetBox = SmallBox(rpiBudget, "no budget", 110, v => { rpiBudget = v; commands.Enqueue(new MenuCommand(MenuAction.SetRpiBudget, value: v)); RefreshCheck(); });
                    budgetBox.Margin = new Thickness(0, 0, 24 * s, 4 * s);
                    limits.Children.Add(budgetBox);
                    limits.Children.Add(Label("Min weight (kg)", Grey, 16));
                    var weightBox = SmallBox(rpiMinWeight, "no minimum", 110, v => { rpiMinWeight = v; commands.Enqueue(new MenuCommand(MenuAction.SetRpiMinWeight, value: v)); RefreshCheck(); });
                    weightBox.Margin = new Thickness(0, 0, 32 * s, 4 * s);
                    limits.Children.Add(weightBox);
                }
                limits.Children.Add(Label("Import", Grey, 16));
                foreach (var f in p.RpiFiles)
                {
                    string path = f.Path;
                    limits.Children.Add(Small(f.Name, White, () => commands.Enqueue(new MenuCommand(MenuAction.RpiImport, text: path))));
                }
                if (p.RpiFiles.Count == 0) limits.Children.Add(Label("put a class file in the folder (Open folder) and it shows here, or Paste one", Grey, 14));
                top.Children.Add(limits);

                // Lighter ships: weight steps that trade weight for budget.
                if (p.RpiCurrent != null)
                {
                    var steps = new WrapPanel { Orientation = Orientation.Horizontal };
                    steps.Children.Add(Label("Lighter ships", Grey, 16));
                    foreach (var step in rpiSteps.OrderByDescending(x => x.Weight))
                    {
                        double w = step.Weight;
                        var chip = Small($"from {step.Weight:#,0} kg: {Num(step.Budget)} RPI   x", Grey, () => commands.Enqueue(new MenuCommand(MenuAction.RpiRemoveStep, value: w)));
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
                    steps.Children.Add(Label("(a ship under the minimum weight gets the budget of the heaviest step it reaches; click a step to remove it)", Grey, 13));
                    top.Children.Add(steps);
                }
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
                rpiCheck.Text = "Ship check: look at a ship with the Ship analyser (Race tab), then come back: its RPI shows here as you edit.";
                rpiCheck.Foreground = Grey;
                return;
            }
            double total = 0;
            foreach (var kv in rpiShipCounts)
                if (rpiValues.TryGetValue(kv.Key, out double cost)) total += cost * kv.Value;
            double budget = GateSettings.RpiBudgetFor(rpiShipMass, rpiMinWeight, rpiBudget, rpiSteps);
            double lowest = GateSettings.RpiLowestWeight(rpiMinWeight, rpiSteps);
            bool over = budget > 0 && total > budget + 1e-9;
            bool light = lowest > 0 && rpiShipMass < lowest;
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
                var b = ValueButton(text, on ? Brush(HudColor.Purple) : dim ? Grey : White, 0, click);
                b.Margin = new Thickness(0, 0, 8 * s, 0);
                b.Padding = new Thickness(12 * s, 0, 12 * s, 0);
                b.MinHeight = 34 * s;
                if (dim && !on) b.Opacity = 0.65;
                rpiCats.Children.Add(b);
            }
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
            foreach (var group in groups)
            {
                if (shown >= rpiShowGroups) break;
                shown++;
                var first = group[0];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(250 * s, GridUnitType.Pixel)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                var icon = IconBox(first, 34 * s);
                Grid.SetColumn(icon, 0); grid.Children.Add(icon);
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8 * s, 0, 8 * s, 0) };
                var name = Text(first.Name, White);
                SoftFontSize(name, 15 * s);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                names.Children.Add(name);
                var place = Text(first.Category, Grey);
                SoftFontSize(place, 11 * s);
                place.Opacity = 0.8;
                names.Children.Add(place);
                Grid.SetColumn(names, 1); grid.Children.Add(names);

                // Its sizes: "1 m [box]  2 m [box] ..." (click a size for its details on the right).
                var sizes = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var e in group)
                {
                    var entry = e;
                    var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s };
                    int have = OnShip(entry);
                    var size = Text(SizeLabel(entry) + (have > 0 ? $"  x{have}" : ""), have > 0 ? Brush(HudColor.Cyan) : White);
                    SoftFontSize(size, 14 * s);
                    size.VerticalAlignment = VerticalAlignment.Center;
                    size.MinWidth = 46 * s;
                    size.TextAlignment = TextAlignment.Right;
                    cell.Children.Add(size);
                    var box = SmallBox(ValueOf(entry), "-", 72, v => SetBlockValue(entry.Key, v));
                    box.GotFocus += (_, __) => SelectBlock(entry.Key);
                    cell.Children.Add(box);
                    var holder = new Border
                    {
                        Child = cell, Background = CellRest, Tag = CellRest, Padding = new Thickness(6 * s, 2 * s, 4 * s, 2 * s),
                        Margin = new Thickness(0, 1 * s, 6 * s, 1 * s),
                    };
                    holder.PointerPressed += (_, press) => { press.Handled = true; SelectBlock(entry.Key); };
                    rpiRowsByKey[entry.Key] = (holder, box);
                    sizes.Children.Add(holder);
                }
                Grid.SetColumn(sizes, 2); grid.Children.Add(sizes);
                var row = ClickRow(grid, () => SelectBlock(first.Key), s);
                row.Margin = new Thickness(0, 0, 12 * s, 0);
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
                var pick = Text("Type a block's RPI straight into its box in the list. Click a block (or one of its sizes) to see what it does here and to limit how many a ship may have.", Grey);
                pick.TextWrapping = TextWrapping.Wrap;
                SoftFontSize(pick, 15 * s);
                rpiDetail.Children.Add(pick);
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
            var title = Text($"IN THIS CLASS ({listed.Count})", White);
            SoftFontSize(title, 15 * s);
            title.FontWeight = FontWeight.Bold;
            rpiSummary.Children.Add(title);
            rpiSummary.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(0, 2 * s, 12 * s, 2 * s) });
            foreach (string k in listed)
            {
                string key = k;
                string shown = byKey.TryGetValue(key, out var e) ? BlockLabel(e) : key.Replace('_', ' ') + "  (not in this game version)";
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var name = Text(shown, White);
                SoftFontSize(name, 14 * s);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(name, 0); grid.Children.Add(name);
                if (rpiLimits.TryGetValue(key, out int limit))
                {
                    var rule = Text(limit == 0 ? "BANNED" : "max " + limit, limit == 0 ? Brush(HudColor.Red) : Brush(HudColor.Cyan));
                    SoftFontSize(rule, 12 * s);
                    rule.VerticalAlignment = VerticalAlignment.Center;
                    rule.Margin = new Thickness(8 * s, 0, 0, 0);
                    Grid.SetColumn(rule, 1); grid.Children.Add(rule);
                }
                double cost = rpiValues.TryGetValue(key, out double c) ? c : 0;
                var value = Text(cost != 0 ? Num(cost) : "", Brush(HudColor.Yellow));
                SoftFontSize(value, 14 * s);
                value.FontWeight = FontWeight.Bold;
                value.VerticalAlignment = VerticalAlignment.Center;
                value.Margin = new Thickness(10 * s, 0, 0, 0);
                Grid.SetColumn(value, 2); grid.Children.Add(value);
                // x: take the block out of the class (its cost and its limit).
                var cross = Text("x", Brush(HudColor.Red));
                SoftFontSize(cross, 15 * s);
                cross.FontWeight = FontWeight.Bold;
                var remove = new Border { Child = cross, Background = CellRest, Padding = new Thickness(8 * s, 0, 8 * s, 2 * s), Margin = new Thickness(10 * s, 0, 0, 0) };
                remove.PointerPressed += (_, press) =>
                {
                    press.Handled = true;
                    if (rpiLimits.ContainsKey(key)) SetBlockLimit(key, -1);
                    SetBlockValue(key, 0);
                    RefreshSummary();
                    if (rpiSelected == key) RefreshDetail();
                };
                Grid.SetColumn(remove, 3); grid.Children.Add(remove);
                var line = ClickRow(grid, () => SelectBlock(key), s);
                line.Margin = new Thickness(0, 0, 12 * s, 0);
                rpiSummary.Children.Add(line);
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

            raceRows.Children.Add(SectionHeader("Laps"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            foreach (var r in p.Settings.Where(r => r.Name.StartsWith("Laps per race") || r.Name.StartsWith("Practice laps")))
                raceRows.Children.Add(OptionRow(r.Name, r.Description, r.Toggle ? MakeSwitch(r) : MakeSlider(r)));

            raceRows.Children.Add(SectionHeader("Ship analyser"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            raceRows.Children.Add(OptionRow("Ship analyser",
                "Closes the menu and hides the race HUD: look at a ship (or any grid) to see its mass, size, blocks, thrust and acceleration each way, gyroscopes, power and hydrogen (generated and stored), cargo, centre of mass and RPI. Left click a grid to keep its stats, right click another to compare. Esc leaves it, or press this button again.",
                Widen(ValueButton(p.AnalyserOpen ? "Close" : "Open", p.AnalyserOpen ? Brush(HudColor.Purple) : White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.ShipAnalyser))))));

            // Race spotlight: export the race just flown; show an exported race off on its ship (in any world).
            raceRows.Children.Add(SectionHeader("Race spotlight"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            raceRows.Children.Add(OptionRow("Export last race" + (p.LastRaceLine != null ? "  (" + p.LastRaceLine + ")" : ""),
                "Saves the race you just finished (times for every lap, penalties and the race stats) to a file in the race spotlight folder. Take the file to another world or another PC to show the race off on the ship that flew it.",
                Widen(ValueButton(p.CanExportRace ? "Export" : "No race finished yet", White, 0, () => commands.Enqueue(new MenuCommand(MenuAction.SpotlightExport))))));
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
                raceRows.Children.Add(OptionRow(race.Name,
                    $"{race.Name}: {race.Info}. Spotlight closes the menu and hides the race HUD: look at the ship and left click it, and a panel shows this race's results, then its stats, then the ship's own stats, one after another. Esc leaves it.",
                    Widen(ValueButton(showing ? "Close spotlight" : "Spotlight", showing ? Brush(HudColor.Purple) : White, 0,
                        () => commands.Enqueue(new MenuCommand(showing ? MenuAction.SpotlightStop : MenuAction.SpotlightStart, text: path))))));
            }

            raceRows.Children.Add(SectionHeader("Ghost and racing line"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var about = Text("Pick whose lap you race against: the ghost, the racing line and the live gap all follow it. The 2nd ghost (Records tab) can fly alongside it.", Grey);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
            SoftFontSize(about, 16 * s);
            raceRows.Children.Add(about);

            bool other = p.Ships.Any(x => x.Racing) || p.Ghosts.Any(x => x.Active) || p.RacingPossible;
            raceRows.Children.Add(OptionRow("Course best",
                "Your fastest lap on this course in any ship (the normal choice).",
                Widen(ValueButton(!other ? "Racing" : p.CourseHasBest ? "Race" : "No best lap yet", !other ? Brush(HudColor.Purple) : White, 0,
                    () => commands.Enqueue(new MenuCommand(MenuAction.RaceCourseBest))))));

            bool racingPossible = p.RacingPossible && !p.Ships.Any(x => x.Racing) && !p.Ghosts.Any(x => x.Active);
            raceRows.Children.Add(OptionRow("Possible lap (best sectors)" + (p.PossibleLine != null ? "  " + p.PossibleLine : ""),
                "Your best sector from start to CP 1, CP 1 to CP 2 and so on, wherever you flew it, joined up into one lap: the ghost and racing line show the lap you'd do if every sector went as well as your best. A sector whose best was set before paths were kept uses your best lap's path for that sector until you beat it.",
                Widen(ValueButton(racingPossible ? "Racing" : p.PossibleLine != null ? "Race" : "Not ready yet", racingPossible ? Brush(HudColor.Purple) : White, 0,
                    () => commands.Enqueue(new MenuCommand(MenuAction.RacePossible))))));

            foreach (var sh in p.Ships.Where(x => x.HasReplay))
            {
                string name = sh.Name;
                raceRows.Children.Add(OptionRow($"{sh.Name}  {sh.BestLap}",
                    $"{sh.Name}'s best lap on this course ({sh.BestLap}{(string.IsNullOrEmpty(sh.Gap) || sh.Gap == "fastest" ? "" : ", " + sh.Gap)}).",
                    Widen(ValueButton(sh.Racing ? "Racing" : "Race", sh.Racing ? Brush(HudColor.Purple) : White, 0,
                        () => commands.Enqueue(new MenuCommand(MenuAction.RaceShip, text: name))))));
            }

            foreach (var gr in p.Ghosts.Where(x => x.Fits))
            {
                string path = gr.Path;
                raceRows.Children.Add(OptionRow("File: " + gr.Label,
                    $"Ghost file: {gr.Label} ({gr.Detail}).",
                    Widen(ValueButton(gr.Active ? "Racing" : "Race", gr.Active ? Brush(HudColor.Purple) : White, 0,
                        () => commands.Enqueue(new MenuCommand(MenuAction.RaceGhostFile, text: path))))));
            }

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
        internal static readonly string[] GuidePages =
        {
            "Getting started", "Building courses", "Timer and colours", "Penalties", "HUD and markers",
            "Minimap and Race Map", "Ghosts and replays", "Results and records", "How it's worked out", "Menu, settings and keys",
        };

        /// <summary>
        /// The guide for the Help screen (F1 > Racing Display): one page (pageName), or with search set, every
        /// entry on any page whose text contains all the words searched for, under its page and section names.
        /// </summary>
        internal static StackPanel BuildGuide(double s, string pageName, string search = null) => BuildInfo(s, pageName, search);

        private static readonly string[] GuidePageNotes =
        {
            "Set up a course and fly your first race: the menu, edit mode, checkpoints, countdown and practice laps.",
            "Course edit mode and its tool wheel, building a course from grid and antenna names, and how courses are saved with the world and shared.",
            "The timer panel, the gap to your best, the checkpoint dots, and what each colour means.",
            "Missed checkpoints, crashes, crash immunity and how penalties show in the lap list.",
            "Checkpoint markers, the edge arrow, the lap list and splits, race messages and the race-HUD-only mode.",
            "The minimap, the big Race Map and its switches, and named sectors.",
            "The ghost and racing line, a 2nd ghost, ghost files and best-lap replays.",
            "The speed graph, race stats and results after the finish, ship times and records.",
            "How the gaps, possible lap, time at top speed, consistency, G-forces, penalties and graphs are worked out.",
            "The Racing Display menu's Race, Settings and Controls tabs, and the keys.",
        };

        /// <summary>The start of the guide in the Help screen: what's on each page.</summary>
        internal static StackPanel BuildGuideIndex(double s)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 0, 12 * s) };
            var intro = Text("Type above to search the whole guide, or pick one of the topics below Racing Display on the left:", White);
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(24 * s, 0, 24 * s, 8 * s);
            SoftFontSize(intro, 18 * s);
            page.Children.Add(intro);
            for (int i = 0; i < GuidePages.Length; i++)
            {
                var g = new Grid { Margin = new Thickness(24 * s, 4 * s, 24 * s, 4 * s) };
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(260 * s, GridUnitType.Pixel)));
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                var name = Text(GuidePages[i], Brush(HudColor.Cyan));
                name.FontWeight = FontWeight.Bold;
                SoftFontSize(name, 18 * s);
                var note = Text(i < GuidePageNotes.Length ? GuidePageNotes[i] : "", White);
                note.TextWrapping = TextWrapping.Wrap;
                SoftFontSize(note, 18 * s);
                Grid.SetColumn(note, 1);
                g.Children.Add(name);
                g.Children.Add(note);
                page.Children.Add(g);
            }
            return page;
        }

        private static StackPanel BuildInfo(double s, string pageName, string search)
        {
            var page = new StackPanel { Margin = new Thickness(0, 0, 0, 12 * s) };
            string[] words = string.IsNullOrWhiteSpace(search) ? null
                : search.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words == null) search = null;
            string currentPage = null, pendingSection = null, lastHeader = null;

            void Page(string name) { currentPage = name; pendingSection = null; }
            // A section's header is only added once one of its entries is shown.
            void Section(string title) => pendingSection = title;

            string SampleText(Control c) => c switch
            {
                TextBlock t => t.Text ?? "",
                Panel p => string.Join(" ", p.Children.Select(SampleText)),
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
                var g = new Grid { Margin = new Thickness(24 * s, 4 * s, 24 * s, 4 * s) };
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(170 * s, GridUnitType.Pixel)));
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                if (sample != null)
                {
                    sample.VerticalAlignment = VerticalAlignment.Top;
                    sample.HorizontalAlignment = HorizontalAlignment.Left;
                    sample.Margin = new Thickness(0, 3 * s, 0, 0);
                    Grid.SetColumn(sample, 0);
                    g.Children.Add(sample);
                }
                var t = Text(text, White);
                t.TextWrapping = TextWrapping.Wrap;
                SoftFontSize(t, 18 * s);
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

            Page("Getting started");
            Section("Getting started");
            Line(Word("Menu", HudColor.Cyan), "Press G (the terminal: Character, Grid, Build ... Map) and pick the Racing Display tab after Map, or press Shift+J for its Race page. Its tabs (Race, Records, Courses, Settings, Controls) are the ones named below.");
            Line(Word("1. Edit mode", HudColor.Orange), "Press Ctrl+F (or Courses tab > Course edit mode). Aim at the ring or arch you've built and left click: it becomes the start/finish gate, fitted to the ring's opening. Right click one to remove it.");
            Line(Word("2. Checkpoints", HudColor.Orange), "Hold Shift+R, pick Checkpoint on the wheel, then left click each checkpoint ring in the order you'll fly them. For a point-to-point course pick Start gate and Finish gate instead of Start/Finish. Press Ctrl+F again when you're done.");
            Line(Word("3. Race", HudColor.Green), "Fly through the gate to start (the right way, towards the first checkpoint; going through backwards or in the spectator camera doesn't start a race; Settings > Race > Only start going forwards turns the backwards check off). Each pass through the gate completes a lap. Set the number of laps (and any practice laps) on the Race tab (Shift+J).");
            Line(Word("Countdown", HudColor.Yellow), "For a standing start, stop near the gate and hold still: you get 3, 2, 1, GO! The clock starts when you fly through the gate.");
            Line(Word("Practice", HudColor.Yellow), "Practice laps are timed but don't count. The race starts when you finish the last one at the gate.");

            Page("Building courses");
            Section("Course edit mode");
            Line(Word("Course edit mode", HudColor.Orange), "Ctrl+F (or Courses tab) turns it on. It also switches the game's debug draw on, so the gate and checkpoint boxes show (it's put back when you leave edit mode). Aim at a grid and left click to set it as the start / finish gate or the next checkpoint (it's outlined while you aim); right click a gate or checkpoint to remove it. Hold Shift+R for the tool wheel (Start/Finish, Start gate, Finish gate, Checkpoint; the current one is tinted in its colour): the cursor appears and the view stays still; point at a tool and let go (or click one). Shift+T changes the placement mode, shown at the top of the timer panel: On a grid (aim at a ring and click), Pick blocks (like the game's partial copy: left click one corner block, then the opposite corner block on the same grid, and the box between them, both included, becomes the gate or checkpoint; it's outlined as you aim, and right click clears the first corner) or In the air (in open air ahead of you; grow / shrink keys change the size). Shift+L shows or hides your best lap line. A course that comes back through the same ring (the other way) can use it as two checkpoints: click a ring that's already a checkpoint, then click it again to add it once more. Each pass through it counts as the next of its checkpoints in order, and it has one marker labelled with both numbers (CP 2 / 5). Ctrl+U / Ctrl+Shift+U undo and redo course edits. Gates, checkpoints and the route between them (white lines with arrows and leg lengths) are always shown while editing, and the COURSE panel lists the type, length, each leg and anything worth fixing (checkpoints very close together, a ring turned side-on to the route). With a separate finish gate the course is point-to-point: one run from the start gate to the finish gate.");
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

            Page("HUD and markers");
            Section("Markers");
            Line(Word("CP 3  1.2 km", HudColor.Orange), "Each checkpoint has a label on screen. The one to fly to next is bigger, brighter and shows how far away it is. Ones you've flown through turn green and missed ones red. Settings > Markers > Only mark the next checkpoints shows markers on just the next few (Checkpoints marked ahead, 3 by default).");
            Line(Word("START / FINISH", HudColor.Cyan), "The start/finish gate (or START and a purple FINISH on a point-to-point course). It shows its distance when it's where you're heading (before the race and after the last checkpoint).");
            Line(Arrow(), "When the next checkpoint is off screen or behind you, an arrow at the edge of the screen points to it.");
            Section("Panels");
            Line(Word("Layout", HudColor.White), "Settings > HUD > HUD layout: Centre (timer top centre), or Left / Right (the timer in that top corner with the lap list under it; the minimap in the other corner with best race and best lap beside it, and the splits under them).");
            Line(Word("LAPS", HudColor.White), "Lap list (left): every lap time, your best this race in green. After the finish the results appear under it.");
            Line(Word("SPLITS", HudColor.White), "Splits (right): for each checkpoint this lap, the gap to your best lap, the SECTOR time (from the previous checkpoint) and the lap time. After the finish it shows every lap side by side (sector times, or lap times if you switch that off), with top and average speed.");
            Section("Messages and HUD modes");
            Line(Word("Race messages", HudColor.Yellow), "GO!, checkpoints, laps, missed checkpoints and crashes drop down in a tab under the timer (long ones wrap onto a second line). Setup messages (gate placed and so on) show in the middle of the screen.");
            Line(Word("Race HUD only", HudColor.Cyan), "The game's HUD key (U) has an extra step: after the minimal HUD it hides the game's own HUD and flight HUD and shows only the race HUD; press U again to carry on to no HUD. Settings > HUD > Race HUD only turns it on or off directly.");
            Section("Flight aids");
            Line(Word("Speedometer", HudColor.White), "A round speed dial in the bottom left corner while you fly the course (its arc fills with speed and its scale grows to suit your ship) (Settings > HUD > Speedometer): with Race HUD only (the default, when the game's own speed display is hidden), always, or off.");
            Line(Word("Gyro", HudColor.Cyan), "Race HUD only hides the game's HUD but keeps its flight gyro (the ring in the middle with the pitch angle) unless you turn off Settings > HUD > Race HUD only: keep the gyro. Gyro: target based switches it to the game's target based mode.");
            Line(Word("Flight keys", HudColor.Orange), "Settings > HUD > Flight keys (on unless you turn it off) shows Q W E, A S D, C and Space in the bottom right corner, lit orange while held.");
            Line(Word("Roll", HudColor.Cyan), "Settings > HUD > Roll indicator shows how far the ship is rolled from level, beside the flight keys: a bar that tilts with the ship over a level line, the angle (R = right wing down; yellow past 30 degrees, red past 60) and under it the roll speed in degrees a second. It needs gravity to know which way is up, so it only shows on or near a planet or moon.");
            Line(Word("Pitch", HudColor.Cyan), "Settings > HUD > Pitch indicator adds the pitch to the same box: how far the nose is above (UP) or below (DOWN) the horizon in degrees (yellow past 30, red past 60). Like the roll it needs gravity.");
            Line(Word("Reset teleport", HudColor.Yellow), "With Settings > Race > Reset race teleports to the start on, the reset race key (Ctrl+R) puts your ship back before the start: at your own start spot if you set one (Ctrl+T, or Courses tab > Start spot), otherwise 10 m before where you last came through the start gate, facing the way you were flying then (just behind the gate until you've started once). The ship is stopped, or comes out at Reset teleport exit speed if you set one (single player or hosting). Target based gyro is switched off and back on as it teleports, so it doesn't keep steering for where it was pointing before.");
            Line(Word("Speed vector", HudColor.Cyan), "The bright green ring with wings (outlined in black) is where it's actually heading (its velocity), not where its nose points: in a drift they split apart. Settings > HUD > Speed vector marker.");
            Section("Ship analyser");
            Line(Word("Analyser", HudColor.Yellow), "Race tab > Ship analyser > Open (the race HUD goes away; Esc brings it back, or press the button again: it says Close while the analyser is open), then look at a ship or any grid: a panel on the right shows its mass, size, blocks and PCU, thrust and acceleration each way (the grid's own forward, back, left, right, up and down), thrusters, gyroscopes, how much power and hydrogen it can generate and has stored (batteries and tanks), cargo space and how far its centre of mass is from its middle. On the grid itself a yellow cross marks the centre of mass, with arrows for its thrust each way (longer = more acceleration). Looking at nothing while in a ship shows your own ship.");
            Line(Word("RPI", HudColor.Yellow), "Race Performance Index, for classes with a parts budget. RPI tab > RPI designer > Open. Manage classes: New class, Duplicate, Rename, Delete; Export saves a class file to the RPI classes folder (files there show under Import), Copy / Paste share a class through the clipboard. Set the budget and minimum weight; Lighter ships adds weight steps (e.g. from 14,000 kg: 275) so a ship under the minimum weight gets the smaller budget of the heaviest step it reaches. The list has every block in the game, one row per block with its sizes side by side: type a size's RPI straight into its box. Filter by category, With a cost or On last ship, search by name or size, and change the sort order. Click a block for its stats (mass, thrust, torque, power, storage) and to set the most a ship may have, or ban it. The Ship check line shows the last ship you analysed against the class as you edit. On the RPI tab pick the class in use; the Ship analyser then shows a ship's total RPI against its budget (red when over), the minimum weight, any broken block limits, and what the total is made of.");
            Line(Word("Compare", HudColor.Cyan), "Left click a grid to keep its stats (left click empty space to let go). Right click another grid to compare: its column is green where it's better (more thrust, less mass) and red where it's worse, and it's marked in blue on the grid. Right click empty space to stop comparing. The analyser is off while you race.");
            Section("Race spotlight");
            Line(Word("Spotlight", HudColor.Purple), "For showing a ship off after a race, in any world. Race tab > Race spotlight > Export saves the race you just finished (every lap's time, penalties and the race stats) to a file; Open folder shows where, and a file from someone else put there appears in the list. Press Spotlight on a race: the menu closes and the race HUD goes away. Look at the ship that flew it and left click: a panel then shows RACE RESULTS (course, pilot, date, race time, best lap, penalties, each lap), RACE STATS and THE SHIP (its mass, thrust, gyroscopes, power, cargo and RPI, with its centre of mass and thrust arrows drawn on it), changing every 8 seconds. Left click another grid to move the spotlight; Esc (or Close spotlight in the menu) leaves it.");
            Page("Minimap and Race Map");
            Section("Maps");
            Line(Word("Minimap", HudColor.Cyan), "Round, in the top left corner (top right with HUD layout Left), showing the whole course. With Settings > Minimap > Minimap zooms in while moving on (off by default) it zooms in and follows you from 20 m/s, and zooms back out as you slow down. The next checkpoint pulses (with a dashed line from you to it); missed checkpoints are red. Each gate and checkpoint has a label, and the racing line uses its racing line colours; start gate facing up. Dashed route between the gates and checkpoints (next one orange, flown ones green), named sectors as coloured bands, the best lap's line, you (arrow) and the ghosts. Settings > Minimap.");
            Line(Word("Race Map", HudColor.Cyan), "G > Map > Race Map: a big version of the minimap. The switches above it (Sectors, Checkpoints, Labels, Route, Racing line, Ghosts, You) hide or show those on the Race Map and the minimap alike, and are remembered. The game's own map pieces (Fast Travel, map data) are hidden while it shows.");
            Line(Word("Sectors", HudColor.Green), "Courses tab > Sectors: name parts of the course (e.g. \"Hairpin\", from CP 3 to CP 5). Each shows on the minimap and Race Map as a coloured band with its name, and is saved with the course.");
            Page("Ghosts and replays");
            Section("Ghosts");
            Line(Word("BEST LAP", HudColor.Purple), "The ghost (labelled BEST LAP) is where your best lap was at this moment of the lap: an outline of the ship that set it, flying with the same heading and roll (or a purple diamond with Settings > Ghost and racing line > Ghost as ship projection off). Ahead of you = you're behind your best; behind you = you're ahead. The magenta racing line is your best lap's path. Red BRAKE and green GO markers on it show where that lap braked hard and where it sped up again (Settings > Ghost and racing line > Braking markers; thickness, opacity and distance are there too). After a race the Race Map shows your best lap of the race (orange) over the record from before it (purple), with red bands where your line was well away from it. The Race tab (Shift+J) picks whose lap it is: your course best, a ship's best or a ghost file.");
            Line(Word("Racing line", HudColor.Yellow), "The best lap's line is coloured to show where time is won and lost (Settings > Ghost and racing line > Racing line colours). Speed: red where it was slow, yellow, green where it was fastest. Braking: red where it was slowing down (harder = redder), green where it was speeding up, white where it held its speed. Plain: magenta.");
            Line(Word("2nd ghost", HudColor.Orange), "Records tab > 2nd ghost (on a ship's row or a ghost file) adds a second, orange ghost that flies at the same time as the main one, in races and in replays: e.g. your best against another ship's best. Its racing line shows too, in orange, and the main line turns purple so you can tell them apart. Press it again to turn it off.");
            Line(Word("Ghost files", HudColor.Purple), "Records tab > Ghost files: export your best lap (course best, or a ship's best) as a file to share, and load other players' files from the ghosts folder. Watch replays one with the spectator camera; Race makes the ghost, racing line and live gap follow it instead of your own best. A ghost only fits the course it was flown on.");
            Section("Replays");
            Line(Word("Replay", HudColor.Purple), "Records tab > Watch best lap (or the replay shortcut): the spectator camera follows the ghost of your ship round your best lap at real speed, then puts you back in your seat. Racing is paused while it plays. The camera orbits the ghost by default: move the mouse to swing round it, W / S to zoom; Ctrl+Shift+C switches to a fixed chase camera. Replays use the game's cinematic spectator (smooth movement, and its look-around keys let you aim away from the ghost); turn that off in Settings > Ghost and replay.");
            Page("Results and records");
            Section("After the race");
            Line(Word("Speed graph", HudColor.Green), "In the results, two graphs take turns, every 5 seconds by default (Settings > Panels > Results graphs switch every; 0 = speed graph only). 1/2 SPEED: your speed round the lap (start on the left, FIN on the right) for your best lap this race against the course record as it was before the race (or your last lap), shaded green where you were faster and red where slower. 2/2 TIME GAP: how far ahead (above the middle line, green) or behind (below, red) of that lap you were at each point, with the scale in seconds at the left. Lines marked CP n are the checkpoints; red ones were missed.");
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
            Line(Word("Live gap", HudColor.Green), "The gap under the timer while you fly: the plugin finds the nearest point on the ghost's recorded path (your best lap, or whoever you race against) to where you are, and compares the ghost's lap time at that point with yours. Minus (green) = you're ahead, plus (red) = behind. The path is recorded 4 times a second, so the gap is accurate to roughly a tenth of a second.");
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
            Line(Word("Crash", HudColor.Red), "By default (Settings > Penalties > Crash detection: Hull damage) a crash is when the game deals collision damage to your ship's blocks, so hard braking or a landing never counts and a scrape does (Crash damage needed sets how big a hit must be, and like the speed drop it only counts at 20 m/s or more: Advanced > Crash minimum speed). That needs the game's server in your game (single player or hosting); until damage has been seen it uses the speed drop instead: the ship was going at least 20 m/s and lost speed faster than the penalty hardness (in m/s², default 20), losing at least 8 m/s and a quarter of its speed. One crash = one penalty, then no more for the crash immunity time (at least 1 second; and, if set, until you're back above the immunity speed).");
            Section("Graphs");
            Line(Word("Speed graph", HudColor.Green), "Speed against how far round the lap you were (0 at the start, FIN at the end, as a share of the lap's distance), so laps of different lengths line up. The shading compares the two lines at the same point of the lap.");
            Line(Word("Time gap graph", HudColor.Green), "For each point round the lap: your time to get there minus the other lap's. At checkpoints these are the real split times; in between they're shared out by how slow each lap was over each bit, so the curve is exact at the checkpoints and a close estimate between them. The finish value is the difference in flown time.");
            Line(Word("Race Map lines", HudColor.Orange), "After the race your lap is coloured by the gap to the record: where the gap was shrinking (over a few points either side) it's green, where it was growing red, white where it held. A faint red band marks stretches where your line stayed more than 2% of the map's width (at least 25 m) from the record's line. The height profile is height above the start gate along the line, against the share of the lap's distance. North is the world's Y axis (the planet's pole in most worlds).");
            Page("Menu, settings and keys");
            Section("Racing Display menu");
            Line(Word("Race", HudColor.Green), "Press Shift+J (or the Race tab) to set the laps and practice laps and pick whose lap you race against: your course best, any ship's best lap on this course, or a ghost file. Speeds use the game's speed unit (Options > Interface).");
            Line(Word("Settings", HudColor.White), "Hide panels, choose the HUD layout, turn markers, the arrow or sounds off, and change penalties, distances and sizes on the Settings tab. Settings are grouped: Race, HUD, Panels, Markers, Minimap, Ghost and racing line, Penalties, Replay, Course editing and Advanced. Click a section's title (the arrow) to fold it away or open it; Advanced starts folded. A setting that only matters when another is on is greyed out while that one is off, and an orange dot (with a Reset button) marks every setting that isn't at its default. Presets at the top save all the settings under a name (e.g. Testing or Race day) to switch between them with Load; Export writes one to a file to share, and other players' files in the presets folder can be imported. The search box finds a setting by name or description, folded sections included; point at a setting to see what it does. Panels use the game's UI background opacity (Options > Interface) unless you turn Panel darkness: use the game's off.");
            Line(Word("Controls", HudColor.White), "The Controls tab lists every key and lets you change them: click one, then press the new key combination (Esc cancels); keys used by two actions show in red. Single keys use Shift: Shift+J for race setup; in edit mode Shift+R (hold) for the tool wheel, Shift+T for the placement mode, Shift+L for the best lap line and Shift+M to move a checkpoint (aim at it, press Shift+M, then left click its new place).");

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
                courseRows.Children.Add(SectionHeader(text));
                courseRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            }

            // ---- this course
            Header("This course");
            var summary = Text((p.CourseName != null ? $"'{p.CourseName}': " : "Not saved yet: ") + p.CourseSummary, null, "SubTitle", "Bright");
            summary.TextWrapping = TextWrapping.Wrap;
            summary.Margin = new Thickness(24 * s, 2 * s, 24 * s, 6 * s);
            SoftFontSize(summary, 18 * s);
            courseRows.Children.Add(summary);

            if (string.IsNullOrEmpty(courseNameBox.Text) && p.CourseName != null) courseNameBox.Text = p.CourseName;
            if (courseNameBox.Parent is Panel oldParent) oldParent.Children.Remove(courseNameBox);
            var save = ValueButton("Save", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.SaveCourse, text: courseNameBox.Text)));
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            nameRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            save.Margin = new Thickness(8 * s, 0, 0, 0);
            Grid.SetColumn(courseNameBox, 0); nameRow.Children.Add(courseNameBox);
            Grid.SetColumn(save, 1); nameRow.Children.Add(save);
            courseRows.Children.Add(OptionRow("Save as", "Type a name and press Save to keep this course (gate and checkpoints). Saving with an existing name replaces it.", nameRow));
            courseRows.Children.Add(OptionRow("Clear course",
                "Removes this world's start / finish gate, checkpoints and sectors (courses saved in the list below stay). Use it if a course turned up that doesn't belong to this world.",
                ConfirmButton("Clear", "This removes the gates, checkpoints and sectors from this world. Saved courses stay in the list. It can't be undone.", MenuAction.ClearCourse)));

            // ---- where the reset race key teleports you (with Settings > Race > Reset race teleports to the start)
            var spotButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s };
            spotButtons.Children.Add(ValueButton("Set here", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.SetStartSpot))));
            spotButtons.Children.Add(ValueButton("Clear", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.ClearStartSpot))));
            courseRows.Children.Add(OptionRow("Start spot: " + (p.StartSpot ?? "Just behind the start gate"),
                "Where the reset race key (Ctrl+R) puts your ship when Settings > Race > Reset race teleports to the start is on. Sit in your ship at the spot, facing the way you want to start, and press Set here (or the set start spot key, Ctrl+T, while flying): your seat goes back to exactly that place and direction. Clear goes back to just behind the start gate. Saved with the course in the world.",
                spotButtons));

            // ---- named sectors
            Header("Sectors");
            var secAbout = Text("Name parts of the course (\"Canyon run\", \"The S bends\"): pick where a sector starts and ends. They show on the minimap and the Race Map, each in its own colour, and are saved with the course in the world.", Grey);
            secAbout.TextWrapping = TextWrapping.Wrap;
            secAbout.Margin = new Thickness(24 * s, 2 * s, 24 * s, 6 * s);
            SoftFontSize(secAbout, 16 * s);
            courseRows.Children.Add(secAbout);
            for (int i = 0; i < p.Sectors.Count; i++)
            {
                int row = i;
                courseRows.Children.Add(OptionRow(p.Sectors[i].Name, $"Sector '{p.Sectors[i].Name}': {p.Sectors[i].Range}.",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 12 * s,
                        Children = { Text(p.Sectors[i].Range, White), ValueButton("Delete", White, 110, () => commands.Enqueue(new MenuCommand(MenuAction.DeleteSector, row))) },
                    }));
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
                var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * s };
                addRow.Children.Add(Picker(sectorFrom, v => sectorFrom = v));
                addRow.Children.Add(Text("to", White));
                addRow.Children.Add(Picker(sectorTo, v => sectorTo = v));
                courseRows.Children.Add(OptionRow("New sector: from", "Where the sector starts and ends along the course (the gate, then the checkpoints in order).", addRow));
                var nameAdd = new Grid();
                nameAdd.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                nameAdd.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var add = ValueButton("Add", White, 110, () =>
                {
                    commands.Enqueue(new MenuCommand(MenuAction.AddSector, text: $"{sectorFrom}|{sectorTo}|{sectorNameBox.Text}"));
                    sectorNameBox.Text = "";
                });
                add.Margin = new Thickness(8 * s, 0, 0, 0);
                Grid.SetColumn(sectorNameBox, 0); nameAdd.Children.Add(sectorNameBox);
                Grid.SetColumn(add, 1); nameAdd.Children.Add(add);
                courseRows.Children.Add(OptionRow("New sector: name", "Type the sector's name and press Add.", nameAdd));
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
            for (int i = 0; i < p.CourseCheckpoints.Count; i++)
            {
                int row = i;
                var up = ValueButton("Up", i > 0 ? White : Grey, 90, () => commands.Enqueue(new MenuCommand(MenuAction.CheckpointUp, row)));
                var down = ValueButton("Down", i < p.CourseCheckpoints.Count - 1 ? White : Grey, 90, () => commands.Enqueue(new MenuCommand(MenuAction.CheckpointDown, row)));
                var del = ValueButton("Delete", Brush(HudColor.Red), 110, () => commands.Enqueue(new MenuCommand(MenuAction.DeleteCheckpoint, row)));
                up.IsEnabled = i > 0;
                down.IsEnabled = i < p.CourseCheckpoints.Count - 1;
                courseRows.Children.Add(OptionRow(p.CourseCheckpoints[i],
                    "Move this checkpoint earlier or later in the course, or delete it. The race resets, and the course's best checkpoint times are cleared when the order changes.",
                    Buttons(up, down, del)));
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
                    recordRows.Children.Add(SectionHeader(group));
                    recordRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
                    recordRows.Children.Add(RecordLine(s, "#", "TOTAL", "BEST LAP", "PENALTIES", "TOP SPEED", "SHIP", "DATE", Grey, Grey, false));
                }
                var colour = r.IsRecord ? Brush(HudColor.Purple) : White;
                var pen = r.Penalty == "clean" ? Brush(HudColor.Green) : Brush(HudColor.Red);
                var line = RecordLine(s, r.Rank.ToString(), r.Total, r.BestLap, r.Penalty, r.TopSpeed, r.Ship, r.Date, colour, pen, r.IsRecord);
                recordRows.Children.Add(line);
            }

            // Ships compared: best times for each ship you've raced this course in.
            recordRows.Children.Add(SectionHeader("Ships on this course"));
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
                recordRows.Children.Add(ShipLine(s, null, "SHIP", "BEST LAP", "GAP", p.ShipsRaceHeader, "GAP", "TOP SPEED", "TURN G", "RACES", Grey));
                foreach (var sh in p.Ships)
                    recordRows.Children.Add(ShipLine(s, sh, (sh.Current ? "> " : "") + sh.Name, sh.BestLap, sh.Gap, sh.BestRace, sh.RaceGap, sh.TopSpeed, sh.TurnG, sh.Races, White));
                var note = Text("Times are saved for the ship you're flying, by its name: rename a ship in its terminal to keep separate times for each version. > marks the ship you're in now. Race best / Watch best race against or replay its best lap; Race last / Watch last do the same with the last lap you flew in it. Export saves its best lap as a ghost file.", Grey);
                note.TextWrapping = TextWrapping.Wrap;
                note.Margin = new Thickness(24 * s, 6 * s, 24 * s, 8 * s);
                SoftFontSize(note, 16 * s);
                recordRows.Children.Add(note);
            }

            // Ghost files: export your best line, load other players' (or your own older) ones.
            recordRows.Children.Add(SectionHeader("Ghost files"));
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
            recordRows.Children.Add(SectionHeader("Reset"));
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
            string top, string turn, string races, IBrush colour)
        {
            // Two lines per ship: the full name on top (never cut off, wraps if very long), then its times and the
            // buttons, lined up under each other in columns.
            var g = new Grid { Margin = new Thickness(24 * s, 6 * s, 24 * s, 6 * s) };
            double[] widths = { 20, 140, 110, 140, 120, 120, 90, 70, 0 };
            foreach (var w in widths)
                g.ColumnDefinitions.Add(new ColumnDefinition(w > 0 ? new GridLength(w * s, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star)));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] texts = { name, lap, gap, race, raceGap, top, turn, races };
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
                SoftFontSize(t, 19 * s);
                t.VerticalAlignment = VerticalAlignment.Center;
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
        private Control MakeSlider(HudModel.SettingRow r)
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
            SoftFontSize(valueText, 20 * s);

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
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, r.Format, value);
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
                    Width = 11 * s,
                    Height = 15 * s,
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
