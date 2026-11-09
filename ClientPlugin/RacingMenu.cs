using System;
using System.Collections.Concurrent;
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
        private TextBox courseNameBox, pilotNameBox, sectorNameBox;
        private int sectorFrom, sectorTo = 1;
        private int builtCoursesVersion = -1;
        private StackPanel controlRows, settingRows, recordRows;
        private Button resetButton;
        private int builtRecordsVersion = -1;
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
            var tab = name == "Records" ? recordsTab : name == "Courses" ? coursesTab : name == "Settings" ? settingsTab : name == "Controls" ? controlsTab : raceTab;
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
            var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 4 * s, 0, 4 * s), MinHeight = 50 * s };
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
            tabs.Items.Add(raceTab);
            tabs.Items.Add(recordsTab);
            tabs.Items.Add(coursesTab);
            tabs.Items.Add(settingsTab);
            tabs.Items.Add(controlsTab);
            if (infoTab != null) tabs.Items.Add(infoTab);
            tabs.SelectedIndex = 0;
            tabs.SelectionChanged += (_, __) => ShowPage();

            controlRows = new StackPanel();
            settingRows = new StackPanel();
            recordRows = new StackPanel();
            raceRows = new StackPanel();
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
            settingsPage = new ScrollViewer
            {
                Content = new StackPanel { Children = { settingsSearch, settingRows } },
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
            Grid.SetColumn(divider, 1); Grid.SetRowSpan(divider, 2); inner.Children.Add(divider);
            var descScroll = new ScrollViewer { Content = description, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
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
            page.Content = OnRaceTab ? racePage : OnCoursesTab ? coursesPage : OnInfoTab ? infoPage : OnRecordsTab ? recordsPage : OnSettingsTab ? settingsPage : controlsPage;
            defaultDescription = OnRaceTab
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
            if (resetButton != null) resetButton.IsVisible = !OnRecordsTab && !OnInfoTab && !OnCoursesTab && !OnRaceTab; // those tabs have their own buttons
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
            note.IsVisible = !string.IsNullOrEmpty(p.Note);
            if (p.Rows.Any(r => r.Capturing)) description.Text = "Press the new key combination (with Ctrl / Shift / Alt if you want them). Esc or click again to cancel.";
        }

        // ---------------------------------------------------------------- Settings tab

        private TextBox settingsSearch;
        private HudModel.ControlsPanel lastSettingsPanel;

        private void FillSettings(HudModel.ControlsPanel p)
        {
            lastSettingsPanel = p;
            settingRows.Children.Clear();
            string section = null;
            // Grouped by section (in the order sections first appear), filtered by the search box.
            var order = p.Settings.Select(r => r.Section).Distinct().ToList();
            string q = settingsSearch?.Text?.Trim() ?? "";
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool Matches(HudModel.SettingRow r)
            {
                string hay = (r.Section + " " + r.Name + " " + r.Description).ToLowerInvariant();
                return words.All(w => hay.Contains(w.ToLowerInvariant()));
            }
            var rows = p.Settings.Where(Matches).OrderBy(r => order.IndexOf(r.Section)).ToList();
            if (rows.Count == 0)
            {
                var none = Text($"No settings match \"{q}\".", Grey);
                none.Margin = new Thickness(24 * scale, 8 * scale, 24 * scale, 8 * scale);
                SoftFontSize(none, 18 * scale);
                settingRows.Children.Add(none);
            }
            foreach (var r in rows)
            {
                if (r.Section != section)
                {
                    section = r.Section;
                    var head = SectionHeader(section);
                    var line = new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * scale, 0, 24 * scale, 4 * scale) };
                    settingRows.Children.Add(head);
                    settingRows.Children.Add(line);
                }
                var control = r.Toggle ? MakeSwitch(r) : MakeSlider(r);
                settingRows.Children.Add(OptionRow(r.Name, r.Description, control));
            }

        }

        // ---------------------------------------------------------------- Race tab

        /// <summary>Race setup: laps and practice laps, and which lap the ghost and racing line follow.</summary>
        private void FillRace(HudModel.ControlsPanel p)
        {
            double s = scale;
            raceRows.Children.Clear();

            raceRows.Children.Add(SectionHeader("Laps"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            foreach (var r in p.Settings.Where(r => r.Name.StartsWith("Laps per race") || r.Name.StartsWith("Practice laps")))
                raceRows.Children.Add(OptionRow(r.Name, r.Description, r.Toggle ? MakeSwitch(r) : MakeSlider(r)));

            raceRows.Children.Add(SectionHeader("Ghost and racing line"));
            raceRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
            var about = Text("Pick whose lap you race against: the ghost, the racing line and the live gap all follow it. The 2nd ghost (Records tab) can fly alongside it.", Grey);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s);
            SoftFontSize(about, 16 * s);
            raceRows.Children.Add(about);

            bool other = p.Ships.Any(x => x.Racing) || p.Ghosts.Any(x => x.Active);
            raceRows.Children.Add(OptionRow("Course best",
                "Your fastest lap on this course in any ship (the normal choice).",
                Widen(ValueButton(!other ? "Racing" : p.CourseHasBest ? "Race" : "No best lap yet", !other ? Brush(HudColor.Purple) : White, 0,
                    () => commands.Enqueue(new MenuCommand(MenuAction.RaceCourseBest))))));

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
            "Minimap and Race Map", "Ghosts and replays", "Results and records", "Menu, settings and keys",
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
            Line(Word("3. Race", HudColor.Green), "Fly through the gate to start (the right way, towards the first checkpoint; going through backwards or in the spectator camera doesn't start a race; Settings > Race start > Only start going forwards turns the backwards check off). Each pass through the gate completes a lap. Set the number of laps (and any practice laps) on the Race tab (Shift+J).");
            Line(Word("Countdown", HudColor.Yellow), "For a standing start, stop near the gate and hold still: you get 3, 2, 1, GO! The clock starts when you fly through the gate.");
            Line(Word("Practice", HudColor.Yellow), "Practice laps are timed but don't count. The race starts when you finish the last one at the gate.");

            Page("Building courses");
            Section("Course edit mode");
            Line(Word("Course edit mode", HudColor.Orange), "Ctrl+F (or Courses tab) turns it on. It also switches the game's debug draw on, so the gate and checkpoint boxes show (it's put back when you leave edit mode). Aim at a grid and left click to set it as the start / finish gate or the next checkpoint (it's outlined while you aim); right click a gate or checkpoint to remove it. Hold Shift+R for the tool wheel (Start/Finish, Start gate, Finish gate, Checkpoint; the current one is tinted in its colour): the cursor appears and the view stays still; point at a tool and let go (or click one). Shift+T changes the placement mode, shown at the top of the timer panel: On a grid (aim at a ring and click), Pick blocks (like the game's partial copy: left click one corner block, then the opposite corner block on the same grid, and the box between them, both included, becomes the gate or checkpoint; it's outlined as you aim, and right click clears the first corner) or In the air (in open air ahead of you; grow / shrink keys change the size). Shift+L shows or hides your best lap line. Ctrl+U / Ctrl+Shift+U undo and redo course edits. Gates, checkpoints and the route between them (white lines with arrows and leg lengths) are always shown while editing, and the COURSE panel lists the type, length, each leg and anything worth fixing (checkpoints very close together, a ring turned side-on to the route). With a separate finish gate the course is point-to-point: one run from the start gate to the finish gate.");
            Section("Auto-build from names");
            Line(Word("How", HudColor.Cyan), "Name the grids you've built for your course, then Courses tab > Build > Auto-build. The plugin looks through every grid in the world (and the antennas on them) and builds the course from the names: the start / finish gate and the checkpoints in number order, each fitted to its ring opening (Settings > Fit to ring opening). It replaces the current gate and checkpoints.");
            Line(Word("Start / Finish", HudColor.Cyan), "A grid or antenna with \"Start\" or \"Finish\" anywhere in its name (Start, Finish, Start/Finish, Start gate...) becomes the start / finish gate. Only one is used, and the course is a circuit: add a separate finish gate afterwards in edit mode for a point-to-point course. If none is found the old gate is kept.");
            Line(Word("CP 1, CP 2 ...", HudColor.Orange), "Checkpoints are named with a number: CP1, CP 2, CP-3, Checkpoint 4, Ring 5, Gate 6, #7 or just 8 (1 to 999, capital letters don't matter). The name must be just that: \"CP 3 big ring\" isn't picked up. They're flown in number order; gaps are fine (1, 2, 5 become CP 1, CP 2, CP 3). If two share a number, the first one found is used.");
            Line(Word("Naming", HudColor.White), "Rename a grid in its terminal (the grid's name), or give an antenna on it the name: handy when one big structure holds several rings, each with its own antenna. A grid's first name that fits decides what it is. Antenna names also show as markers on the HUD: Courses tab > Turn all off hides them.");
            Line(Word("Then", HudColor.Green), "Check the route in edit mode (Ctrl+F), fix anything with the tools, save it as a named course if you like, then save the game: courses are kept in the world's own save.");

            Section("Saving and sharing");
            Line(Word("Courses", HudColor.White), "The Courses tab saves and loads courses, changes the checkpoint order, builds a course from grids or antennas named Start / CP1 / CP2..., and can switch every antenna in the world off or on.");
            Line(Word("World save", HudColor.Cyan), "Courses belong to the world they're made in. They're written into the world's save folder (RaceDisplayCourses.json) every time you save the game, and loaded with the world, so they never show up in other worlds. Publish the world and players who download it, and have this plugin, get the course set up automatically (a new world started from a Workshop download finds the course from the Workshop copy).");
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
                "Sector colours: each checkpoint dot under the timer takes the colour of the sector you just flew, like F1 timing: purple = fastest ever on this course, green = your best this race, yellow = slower. Settings > Display > Sector colours.");

            Page("Penalties");
            Section("Penalties");
            Line(Row(Word("+5s", HudColor.Red), Dot(HudColor.Red, HudColor.Red, true)), "Missed checkpoint: you flew through a later checkpoint without this one. You get a red MISSED message straight away and its split shows MISSED. The penalty (5 s by default) is added to the lap.");
            Line(Row(Word("+5s", HudColor.Red), Cross()), "Crash: a sudden hard stop while going fast (20 m/s or more by default). The timer flashes red and the penalty is added to the lap.");
            Line(Word("Immunity", HudColor.Yellow), "After a crash penalty there's no other crash penalty for 5 s (Settings > Penalties > Crash immunity, up to 60 s, 0 = off), so bouncing off a wall or tumbling only counts once. The crash message says how long it lasts.");
            Line(Word("+10s", HudColor.Red), "In the lap list, each lap shows its total penalty, then one ring per missed checkpoint and one cross per crash. Penalty sizes and crash sensitivity are on the Settings tab.");

            Page("HUD and markers");
            Section("Markers");
            Line(Word("CP 3  1.2 km", HudColor.Orange), "Each checkpoint has a label on screen. The one to fly to next is bigger, brighter and shows how far away it is. Ones you've flown through turn green and missed ones red.");
            Line(Word("START / FINISH", HudColor.Cyan), "The start/finish gate (or START and a purple FINISH on a point-to-point course). It shows its distance when it's where you're heading (before the race and after the last checkpoint).");
            Line(Arrow(), "When the next checkpoint is off screen or behind you, an arrow at the edge of the screen points to it.");
            Section("Panels");
            Line(Word("LAPS", HudColor.White), "Lap list (left): every lap time, your best this race in green. After the finish the results appear under it.");
            Line(Word("SPLITS", HudColor.White), "Splits (right): for each checkpoint this lap, the gap to your best lap, the SECTOR time (from the previous checkpoint) and the lap time. After the finish it shows every lap side by side (sector times, or lap times if you switch that off), with top and average speed.");
            Section("Messages and HUD modes");
            Line(Word("Race messages", HudColor.Yellow), "GO!, checkpoints, laps, missed checkpoints and crashes drop down in a tab under the timer (long ones wrap onto a second line). Setup messages (gate placed and so on) show in the middle of the screen.");
            Line(Word("Race HUD only", HudColor.Cyan), "The game's HUD key (U) has an extra step: after the minimal HUD it hides the game's own HUD and flight HUD and shows only the race HUD; press U again to carry on to no HUD. Settings > Display > Race HUD only turns it on or off directly.");
            Page("Minimap and Race Map");
            Section("Maps");
            Line(Word("Minimap", HudColor.Cyan), "Round, in the top left corner (top right with swapped sides), showing the whole course. With Settings > Minimap > Minimap zooms in while moving on (off by default) it zooms in and follows you from 20 m/s, and zooms back out as you slow down. The next checkpoint pulses (with a dashed line from you to it); missed checkpoints are red. Each gate and checkpoint has a label, and the racing line uses its racing line colours; start gate facing up. Dashed route between the gates and checkpoints (next one orange, flown ones green), named sectors as coloured bands, the best lap's line, you (arrow) and the ghosts. Settings > Minimap.");
            Line(Word("Race Map", HudColor.Cyan), "G > Map > Race Map: a big version of the minimap. The switches above it (Sectors, Checkpoints, Labels, Route, Racing line, Ghosts, You) hide or show those on the Race Map and the minimap alike, and are remembered.");
            Line(Word("Sectors", HudColor.Green), "Courses tab > Sectors: name parts of the course (e.g. \"Hairpin\", from CP 3 to CP 5). Each shows on the minimap and Race Map as a coloured band with its name, and is saved with the course.");
            Page("Ghosts and replays");
            Section("Ghosts");
            Line(Word("BEST LAP", HudColor.Purple), "The ghost (labelled BEST LAP) is where your best lap was at this moment of the lap: an outline of the ship that set it, flying with the same heading and roll (or a purple diamond with Settings > Ghost and replay > Ghost as ship projection off). Ahead of you = you're behind your best; behind you = you're ahead. The magenta racing line is your best lap's path. The Race tab (Shift+J) picks whose lap it is: your course best, a ship's best or a ghost file.");
            Line(Word("Racing line", HudColor.Yellow), "The best lap's line is coloured to show where time is won and lost (Settings > Display > Racing line colours). Speed: red where it was slow, yellow, green where it was fastest. Braking: red where it was slowing down (harder = redder), green where it was speeding up, white where it held its speed. Plain: magenta.");
            Line(Word("2nd ghost", HudColor.Orange), "Records tab > 2nd ghost (on a ship's row or a ghost file) adds a second, orange ghost that flies at the same time as the main one, in races and in replays: e.g. your best against another ship's best. Its racing line shows too, in orange, and the main line turns purple so you can tell them apart. Press it again to turn it off.");
            Line(Word("Ghost files", HudColor.Purple), "Records tab > Ghost files: export your best lap (course best, or a ship's best) as a file to share, and load other players' files from the ghosts folder. Watch replays one with the spectator camera; Race makes the ghost, racing line and live gap follow it instead of your own best. A ghost only fits the course it was flown on.");
            Section("Replays");
            Line(Word("Replay", HudColor.Purple), "Records tab > Watch best lap (or the replay shortcut): the spectator camera follows the ghost of your ship round your best lap at real speed, then puts you back in your seat. Racing is paused while it plays. The camera orbits the ghost by default: move the mouse to swing round it, W / S to zoom; Ctrl+Shift+C switches to a fixed chase camera. Replays use the game's cinematic spectator (smooth movement, and its look-around keys let you aim away from the ghost); turn that off in Settings > Ghost and replay.");
            Page("Results and records");
            Section("After the race");
            Line(Word("Speed graph", HudColor.Green), "In the results: your speed round the lap (start on the left, finish on the right) for your best lap this race and the course record, so you can see where you lost speed. Dashed lines with numbers mark the checkpoints, and a dot on each line shows where that lap went through them.");
            Line(Word("Race stats", HudColor.White), "After the finish the splits table adds G-forces per lap (G MAX = hardest cornering, G AVG = average cornering, BRAKE = hardest slowing down) and distance flown per lap (shortest in green). A separate RACE STATS box in the top corner shows the ship, how you did against its best, total distance, time at top speed, average lap and consistency (best to worst lap spread); turn it off in Settings > Display. G-forces come from the ship's change of velocity, so impacts over 30 g are left out.");
            Line(Word("Results", HudColor.Yellow), "The race results can compare with the best on the course (any ship) or the best in the ship you flew: press Ctrl+B (Controls tab: Results: overall / this ship) while they're showing to switch, or set it in Settings > Display. In ship mode the headline, best lap colour, possible lap and the graph all use that ship's own bests.");
            Section("Records");
            Line(Word("Ships", HudColor.Cyan), "Every race saves times for the ship you flew, by its name. Records tab > Ships on this course compares them: fastest in purple, the others with their gap in yellow. Rename a ship in its terminal to keep separate times for each version.");
            Line(Word("Records", HudColor.Yellow), "The Records tab lists your best 5 races on this course for each number of laps, and has the reset buttons.");
            Page("Menu, settings and keys");
            Section("Racing Display menu");
            Line(Word("Race", HudColor.Green), "Press Shift+J (or the Race tab) to set the laps and practice laps and pick whose lap you race against: your course best, any ship's best lap on this course, or a ghost file. Speeds use the game's speed unit (Options > Interface).");
            Line(Word("Settings", HudColor.White), "Hide panels, swap sides, turn markers, the arrow or sounds off, and change penalties, distances and sizes on the Settings tab. The search box at the top finds a setting by name or description; point at a setting to see what it does. Panels use the game's UI background opacity (Options > Interface) unless you turn Panel darkness: use the game's off.");
            Line(Word("Controls", HudColor.White), "The Controls tab lists every key and lets you change them: click one, then press the new key combination (Esc cancels); keys used by two actions show in red. Single keys use Shift: Shift+J for race setup, Shift+R (hold) for the edit tool wheel.");

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
                courseRows.Children.Add(OptionRow($"{name}   ({info})", $"Load '{name}' to race it (replaces the current gate and checkpoints), or delete it.", Buttons(load, del)));
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

            string group = null;
            foreach (var r in p.Records)
            {
                if (r.Group != group)
                {
                    group = r.Group;
                    recordRows.Children.Add(SectionHeader(group));
                    recordRows.Children.Add(new Border { Height = 1, Background = SectionLine, Margin = new Thickness(24 * s, 0, 24 * s, 4 * s) });
                    recordRows.Children.Add(RecordLine(s, "#", "TOTAL", "BEST LAP", "PENALTIES", "TOP SPEED", "SHIP", "DATE", Grey, Grey, false));
                }
                var colour = r.IsRecord ? Brush(HudColor.Purple) : White;
                var pen = r.Penalty == "clean" ? Brush(HudColor.Green) : Brush(HudColor.Red);
                recordRows.Children.Add(RecordLine(s, r.Rank.ToString(), r.Total, r.BestLap, r.Penalty, r.TopSpeed, r.Ship, r.Date, colour, pen, r.IsRecord));
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
                var note = Text("Times are saved for the ship you're flying, by its name: rename a ship in its terminal to keep separate times for each version. > marks the ship you're in now. Watch replays a ship's best lap; Export saves it as a ghost file.", Grey);
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
                string ship = sh.Name;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * s, HorizontalAlignment = HorizontalAlignment.Right };
                if (sh.HasReplay)
                {
                    buttons.Children.Add(ValueButton("Watch", White, 100, () => commands.Enqueue(new MenuCommand(MenuAction.WatchShipReplay, text: ship))));
                    buttons.Children.Add(ValueButton("Export", White, 100, () => commands.Enqueue(new MenuCommand(MenuAction.ExportGhost, text: ship))));
                    buttons.Children.Add(ValueButton(sh.Second ? "2nd: on" : "2nd ghost", sh.Second ? Brush(HudColor.Orange) : White, 120,
                        () => commands.Enqueue(new MenuCommand(MenuAction.SecondGhostShip, text: ship))));
                }
                buttons.Children.Add(ValueButton("Delete", White, 100, () => ShowConfirm($"Forget the times for {ship} on this course? It can't be undone.",
                    () => commands.Enqueue(new MenuCommand(MenuAction.DeleteShip, text: ship)))));
                Grid.SetRow(buttons, 1);
                Grid.SetColumn(buttons, 8);
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
            overlay.Children.Add(confirmLayer);
            overlay.Focus();
        }

        private void CloseConfirm()
        {
            if (confirmLayer != null && overlay != null) overlay.Children.Remove(confirmLayer);
            confirmLayer = null;
        }

        private void Send(int index, double value) => commands.Enqueue(new MenuCommand(MenuAction.SetValue, index, value));

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
