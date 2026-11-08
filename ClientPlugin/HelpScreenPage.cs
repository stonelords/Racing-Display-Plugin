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
    /// Adds a "Racing Display" topic to the game's Help screen (F1), under "Controls", holding the
    /// plugin's guide (what used to be the Info tab of the Racing Display menu).
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

        private static void HelpScreenCreated(object __instance)
        {
            try
            {
                if (!(AccessTools.Field(__instance.GetType(), "PART_TreeView")?.GetValue(__instance) is TreeView tree)) return;

                var topic = Activator.CreateInstance(topicType);
                topicType.GetProperty("Header")?.SetValue(topic, Title);
                if (!(topicType.GetProperty("Content")?.GetValue(topic) is IList content)) return;
                content.Add(new GuideHost(topic));

                // Straight after "Controls" (header key "HelpControls"), or at the end.
                int at = tree.Items.Count;
                for (int i = 0; i < tree.Items.Count; i++)
                    if (topicType.IsInstanceOfType(tree.Items[i]) && (topicType.GetProperty("Header")?.GetValue(tree.Items[i]) as string) == "HelpControls")
                        at = i + 1;
                tree.Items.Insert(at, topic);

                // The tree shows headers through the game's localisation, which doesn't know our name:
                // once the rows exist, put the plain title on ours.
                void FixHeader() => Dispatcher.UIThread.Post(() => SetHeaderText(tree, topic), DispatcherPriority.Background);
                tree.Loaded += (_, __) => FixHeader();
                tree.SelectionChanged += (_, __) => FixHeader();
            }
            catch (Exception e) { PluginLog.Error("Couldn't add the Racing Display topic to the Help screen", e); }
        }

        private static void SetHeaderText(TreeView tree, object topic)
        {
            try
            {
                foreach (var item in tree.GetVisualDescendants().OfType<TreeViewItem>())
                {
                    if (!ReferenceEquals(item.DataContext, topic) && !ReferenceEquals(item.Header, topic)) continue;
                    foreach (var t in item.GetVisualDescendants().OfType<TextBlock>())
                    {
                        // Only the row's own text, not anything in nested rows.
                        if (t.FindAncestorOfType<TreeViewItem>() != item) continue;
                        if (t.Text != Title) t.Text = Title;
                        break;
                    }
                }
            }
            catch (Exception e) { PluginLog.Error("Couldn't set the Racing Display help title", e); }
        }

        /// <summary>
        /// The guide, built when it's first shown (so it's sized for the screen) and scrollable inside the
        /// Help screen's right-hand panel.
        /// </summary>
        private sealed class GuideHost : ScrollViewer
        {
            private readonly object topic;
            private double builtScale;
            private ContentPresenter panel;

            // Use the game's ScrollViewer look (styles go by type).
            protected override Type StyleKeyOverride => typeof(ScrollViewer);

            public GuideHost(object topic)
            {
                this.topic = topic;
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
                        Content = RacingMenu.BuildGuide(s);
                        builtScale = s;
                    }

                    // The topic's items sit in a stack (unlimited height), so limit our height to the
                    // right-hand panel ourselves; otherwise the guide would run off the bottom.
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
