using System;
using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HarmonyLib;

namespace RaceDisplay
{
    /// <summary>
    /// Adds a "Racing Display" topic to the game's Help screen (F1), under "Controls": a search over the
    /// plugin's guide, followed in the list by a topic for each part of the plugin (getting started, building courses...).
    ///
    /// The Help screen (Keen.Game2.Client.UI.Library.Help.HelpScreen) fills its TreeView (PART_TreeView)
    /// with HelpScreenTopic objects in its constructor. Each topic has a Header (a text key, shown through
    /// the game's localisation) and a Content list; the right-hand side shows the selected topic's
    /// Content items, and controls in that list are shown as they are. So a postfix on the constructor
    /// inserts one more topic whose Content is our own control.
    /// </summary>
    internal static class HelpScreenPage
    {
        private const string Title = "Racing Display";

        /// <summary>True once the Help screen patch is in: the menu then drops its own Info tab.</summary>
        internal static bool Added { get; private set; }

        private static Type topicType;

        public static void Apply(Harmony harmony)
        {
            try
            {
                var screen = AccessTools.TypeByName("Keen.Game2.Client.UI.Library.Help.HelpScreen");
                topicType = AccessTools.TypeByName("Keen.Game2.Client.UI.Library.Help.Tabs.HelpScreenTopic");
                var ctor = screen?.GetConstructor(Type.EmptyTypes);
                if (ctor == null || topicType == null || AccessTools.Field(screen, "PART_TreeView") == null)
                {
                    PluginLog.Error("Help screen not found: the guide stays on the menu's Info tab");
                    return;
                }
                harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(HelpScreenPage), nameof(HelpScreenCreated)));
                Added = true;
                PluginLog.Info("Patched HelpScreen constructor (Racing Display help topic)");
            }
            catch (Exception e) { PluginLog.Error("Couldn't patch the Help screen", e); }
        }

        // Our topics and the titles to show for them (the tree would otherwise look their headers up as text keys).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> titles =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, string>();

        private static object NewTopic(string title, Control content, bool indent = false)
        {
            var topic = Activator.CreateInstance(topicType);
            topicType.GetProperty("Header")?.SetValue(topic, title);
            if (topicType.GetProperty("Content")?.GetValue(topic) is IList list) list.Add(content);
            // Topics that belong to Racing Display are shown indented under it (non-breaking spaces, so they're kept).
            titles.AddOrUpdate(topic, indent ? "\u00A0\u00A0\u00A0\u00A0" + title : title);
            return topic;
        }

        private static void HelpScreenCreated(object __instance)
        {
            try
            {
                if (!(AccessTools.Field(__instance.GetType(), "PART_TreeView")?.GetValue(__instance) is TreeView tree)) return;

                // "Racing Display": a search over the whole guide, then a topic for each part of the plugin,
                // all listed in the tree (straight after "Controls", header key "HelpControls", or at the end).
                object topic = null;
                topic = NewTopic(Title, new GuideHost(() => topic, s => SearchPage(s)));
                var all = new System.Collections.Generic.List<object> { topic };
                foreach (var name in RacingMenu.GuidePages)
                {
                    object sub = null;
                    string pageName = name;
                    sub = NewTopic(name, new GuideHost(() => sub, s => RacingMenu.BuildGuide(s, pageName)), indent: true);
                    all.Add(sub);
                }

                int at = tree.Items.Count;
                for (int i = 0; i < tree.Items.Count; i++)
                    if (topicType.IsInstanceOfType(tree.Items[i]) && (topicType.GetProperty("Header")?.GetValue(tree.Items[i]) as string) == "HelpControls")
                        at = i + 1;
                foreach (var t in all) tree.Items.Insert(at++, t);

                // The tree shows headers through the game's localisation, which doesn't know our names: whenever
                // rows appear (opening, expanding Racing Display), put the plain titles on ours.
                bool queued = false;
                void FixHeaders()
                {
                    if (queued) return;
                    queued = true;
                    Dispatcher.UIThread.Post(() => { queued = false; SetHeaderTexts(tree); }, DispatcherPriority.Background);
                }
                tree.Loaded += (_, __) => FixHeaders();
                tree.SelectionChanged += (_, __) => FixHeaders();
                tree.LayoutUpdated += (_, __) => { if (!tree.IsEffectivelyVisible) return; FixHeaders(); };
            }
            catch (Exception e) { PluginLog.Error("Couldn't add the Racing Display topic to the Help screen", e); }
        }

        /// <summary>The Racing Display page: a search box, then what matches (or, before searching, the list of pages).</summary>
        private static Control SearchPage(double s)
        {
            var results = new ContentControl();
            var box = new TextBox
            {
                Watermark = "Search the Racing Display guide",
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(24 * s, 4 * s, 24 * s, 8 * s),
            };
            DispatcherTimer wait = null;
            void Show()
            {
                string q = box.Text?.Trim();
                results.Content = !string.IsNullOrEmpty(q) ? RacingMenu.BuildGuide(s, null, q) : RacingMenu.BuildGuideIndex(s);
            }
            // Searched a moment after typing stops, not on every key.
            box.TextChanged += (_, __) =>
            {
                wait?.Stop();
                wait = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (t, e2) => { ((DispatcherTimer)t).Stop(); Show(); });
                wait.Start();
            };
            Show();
            return new StackPanel { Children = { box, results } };
        }

        private static int fixing;

        private static void SetHeaderTexts(TreeView tree)
        {
            if (fixing > 0) return;
            fixing++;
            try
            {
                foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>())
                {
                    object data = item.DataContext ?? item.Header;
                    if (data == null || !titles.TryGetValue(data, out var title)) continue;
                    foreach (var t in item.GetVisualDescendants().OfType<TextBlock>())
                    {
                        // Only the row's own text, not anything in nested rows.
                        if (t.FindAncestorOfType<TreeViewItem>() != item) continue;
                        if (t.Text != title) t.Text = title;
                        break;
                    }
                }
            }
            catch (Exception e) { PluginLog.Error("Couldn't set the Racing Display help titles", e); }
            finally { fixing--; }
        }

        /// <summary>
        /// The guide, built when it's first shown (so it's sized for the screen) and scrollable inside the
        /// Help screen's right-hand panel.
        /// </summary>
        private sealed class GuideHost : ScrollViewer
        {
            private readonly Func<object> topicOf;
            private readonly Func<double, Control> build;
            private double builtScale;
            private ContentPresenter panel;

            // Use the game's ScrollViewer look (styles go by type).
            protected override Type StyleKeyOverride => typeof(ScrollViewer);

            public GuideHost(Func<object> topic, Func<double, Control> build)
            {
                topicOf = topic;
                this.build = build;
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            }

            protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
            {
                base.OnAttachedToVisualTree(e);
                try
                {
                    double h = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 1080;
                    double s = h > 0 ? Math.Max(0.75, Math.Min(2.0, h / 1080.0)) : 1.0;
                    if (Content == null || Math.Abs(s - builtScale) > 0.01)
                    {
                        Content = build(s);
                        builtScale = s;
                    }

                    // The topic's items sit in a stack (unlimited height), so limit our height to the
                    // right-hand panel ourselves; otherwise the guide would run off the bottom.
                    var topic = topicOf();
                    panel = this.GetVisualAncestors().OfType<ContentPresenter>().FirstOrDefault(p => ReferenceEquals(p.Content, topic));
                    bool outerScroll = panel != null && panel.GetVisualAncestors().TakeWhile(v => !(v is TopLevel)).OfType<ScrollViewer>().Any();
                    if (panel != null && !outerScroll)
                    {
                        panel.PropertyChanged += PanelChanged;
                        FitToPanel();
                    }
                }
                catch (Exception ex) { PluginLog.Error("Couldn't show the Racing Display guide", ex); }
            }

            protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
            {
                if (panel != null) panel.PropertyChanged -= PanelChanged;
                panel = null;
                base.OnDetachedFromVisualTree(e);
            }

            private void PanelChanged(object sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == BoundsProperty) FitToPanel();
            }

            private void FitToPanel()
            {
                if (panel == null) return;
                double h = panel.Bounds.Height - panel.Padding.Top - panel.Padding.Bottom - 8;
                if (h > 50) MaxHeight = h;
            }
        }
    }
}
