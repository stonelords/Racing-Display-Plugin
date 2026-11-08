using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RaceDisplay
{
    /// <summary>
    /// The course edit tool wheel, made with the game's own quick-select radial menu (the one the paint gun uses):
    /// InGameUI.OpenQuickMenu(new QuickMenuScreenViewModel(definition, data, selectionChanged, hoverChanged)).
    /// The game shows the cursor and holds the camera while it's open. Everything is reached by reflection, as found
    /// in the game (see the plugin's notes); if something is missing the log says what.
    /// </summary>
    internal static class QuickWheel
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string NsRadial = "Keen.Game2.Client.UI.Library.Controls.RadialMenu.";
        private const string NsQuick = "Keen.Game2.Client.UI.Library.Controls.QuickMenu.";

        private static readonly ConcurrentQueue<int> picks = new ConcurrentQueue<int>();
        private static object handle;           // IObservableDisposable from OpenQuickMenu
        private static object viewModel;        // our QuickMenuScreenViewModel
        private static List<object> panels = new List<object>();
        private static volatile int hovered = -1;
        private static volatile bool open;

        /// <summary>A real QuickMenuDefinition, saved when the game builds one of its quick menus (has the cancel / mouse-look actions).</summary>
        public static object SavedDefinition;

        public static bool IsOpen => open;

        public static bool TryTakePick(out int tool) => picks.TryDequeue(out tool);

        /// <summary>Opens the wheel. False (and logged) if it couldn't.</summary>
        /// <param name="highlights">Optional: slices to tint (index -> colour), e.g. a switch that's on shows orange.
        /// Done on the wheel's own controls once it's on screen; the text stays.</param>
        public static bool Open(string[] labels, int current, IDictionary<int, (byte R, byte G, byte B)> highlights = null)
        {
            if (open) return true;
            try
            {
                var inGameUiType = AccessTools.TypeByName("Keen.Game2.Client.UI.InGame.InGameUI");
                var vmType = AccessTools.TypeByName(NsQuick + "QuickMenuScreenViewModel");
                var defType = AccessTools.TypeByName(NsQuick + "QuickMenuDefinition");
                var dataType = AccessTools.TypeByName(NsQuick + "QuickMenuData");
                var radialDataType = AccessTools.TypeByName(NsRadial + "RadialMenuData");
                var panelType = AccessTools.TypeByName(NsRadial + "BaseRadialMenuPanel");
                if (inGameUiType == null || vmType == null || defType == null || dataType == null || radialDataType == null || panelType == null)
                {
                    PluginLog.Info("Quick wheel: a game type is missing");
                    return false;
                }

                object inGameUi = GameInternals.Instance("InGameUI");
                if (inGameUi == null) { PluginLog.Info("Quick wheel: InGameUI not seen yet (open the paint gun wheel once)"); return false; }

                // The slices: one panel per tool.
                Type concrete = panelType.IsAbstract
                    ? GameInternals.GameTypes().FirstOrDefault(t => !t.IsAbstract && panelType.IsAssignableFrom(t) && t.Name == "PrefabRadialMenuPanel")
                    : panelType;
                if (concrete == null) { PluginLog.Info("Quick wheel: no panel type to use"); return false; }
                panels = new List<object>();
                for (int i = 0; i < labels.Length; i++)
                {
                    var label = labels[i];
                    object p = null;
                    p ??= Activator.CreateInstance(concrete, true);
                    SetAuto(p, "DisplayName", label);
                    SetAuto(p, "IsEnabled", true);
                    SetAuto(p, "CanHoverable", true);
                    panels.Add(p);
                }

                // RadialMenuData(ImmutableMergeableList<BaseRadialMenuPanel> panels, BaseRadialMenuPanel middlePanel)
                var rdCtor = radialDataType.GetConstructors(All).FirstOrDefault(c => c.GetParameters().Length == 2);
                if (rdCtor == null) { PluginLog.Info("Quick wheel: RadialMenuData constructor not found"); return false; }
                var listType = rdCtor.GetParameters()[0].ParameterType;
                object list = MakeList(listType, panelType, panels);
                if (list == null) return false;
                object radialData = rdCtor.Invoke(new[] { list, null });

                object data = Activator.CreateInstance(dataType, true);
                SetAuto(data, "RootMenu", radialData);

                object def = SavedDefinition ?? Activator.CreateInstance(defType, true);
                if (SavedDefinition == null) PluginLog.Info("Quick wheel: using an empty menu definition (open the paint gun wheel once to borrow its keys)");

                Action<object> selected = OnSelected;
                Action<object> hover = OnHover;
                var vmCtor = vmType.GetConstructors(All).First(c => c.GetParameters().Length == 4);
                hovered = -1;
                object vm = vmCtor.Invoke(new object[] { def, data, selected, hover });
                viewModel = vm;

                var openMethod = inGameUiType.GetMethods(All).FirstOrDefault(m => m.Name == "OpenQuickMenu" && m.GetParameters().Length == 1);
                if (openMethod == null) { PluginLog.Info("Quick wheel: InGameUI.OpenQuickMenu not found"); return false; }
                handle = openMethod.Invoke(inGameUi, new[] { vm });
                if (handle == null) { PluginLog.Info("Quick wheel: the game didn't open it (another screen open?)"); return false; }
                open = true;

                // Know when it closes (the game closes it on a pick, or on its cancel key).
                var ev = handle?.GetType().GetEvents(All).FirstOrDefault(e => e.Name == "OnDisposed")
                         ?? handle?.GetType().GetInterfaces().SelectMany(i => i.GetEvents()).FirstOrDefault(e => e.Name == "OnDisposed");
                if (ev != null)
                {
                    Action<object> closed = _ => { open = false; handle = null; viewModel = null; };
                    ev.AddEventHandler(handle, Delegate.CreateDelegate(ev.EventHandlerType, closed.Target, closed.Method));
                }
                PluginLog.Info("Quick wheel opened");
                if (highlights != null)
                    foreach (var kv in highlights)
                        if (kv.Key >= 0 && kv.Key < labels.Length) Tint(labels[kv.Key], kv.Value, 0);
                return true;
            }
            catch (Exception e)
            {
                PluginLog.Error("Quick wheel failed to open", e.InnerException ?? e);
                open = false;
                return false;
            }
        }

        private static bool tintLogged, shapesLogged;

        /// <summary>
        /// Tints one slice of the open wheel: finds the label's TextBlock on screen, then the nearest container round
        /// it that holds only that slice, and fills its shapes with the colour (text kept). Retried for a few frames
        /// while the wheel builds its controls.
        /// </summary>
        private static void Tint(string label, (byte R, byte G, byte B) colour, int attempt)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!open) return;
                    var root = RacingMenu.Instance?.RootPanel;
                    var top = root == null ? null : Avalonia.Controls.TopLevel.GetTopLevel(root);
                    Avalonia.Visual scope = (Avalonia.Visual)top ?? root;
                    var text = scope == null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(scope)
                        .OfType<Avalonia.Controls.TextBlock>().FirstOrDefault(t => t.Text == label && t.IsVisible);
                    if (text == null)
                    {
                        if (attempt < 20) Avalonia.Threading.DispatcherTimer.RunOnce(() => Tint(label, colour, attempt + 1), TimeSpan.FromMilliseconds(30));
                        else if (!tintLogged) { tintLogged = true; PluginLog.Info($"Quick wheel: couldn't find the '{label}' slice to tint"); }
                        return;
                    }
                    var fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(200, colour.R, colour.G, colour.B));
                    // Walk up to the slice: the first container that has a filled shape and only this one label.
                    foreach (var anc in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(text).Take(8))
                    {
                        var below = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(anc).ToList();
                        int labelsInside = below.OfType<Avalonia.Controls.TextBlock>().Count(t => !string.IsNullOrWhiteSpace(t.Text));
                        if (labelsInside > 1) break;   // gone past this slice
                        var shapes = below.OfType<Avalonia.Controls.Shapes.Shape>().Where(sh => sh.Fill != null).ToList();
                        if (shapes.Count > 0 && shapes.All(sh => sh.Bounds.Width < 1) && attempt < 20)
                        {
                            // Not laid out yet: try again next frame so sizes can be compared.
                            Avalonia.Threading.DispatcherTimer.RunOnce(() => Tint(label, colour, attempt + 1), TimeSpan.FromMilliseconds(30));
                            return;
                        }
                        if (shapes.Count > 0)
                        {
                            if (!shapesLogged)
                            {
                                shapesLogged = true;
                                foreach (var v in below.OfType<Avalonia.Visual>())
                                    if (v is Avalonia.Controls.Control c)
                                        PluginLog.Info($"Quick wheel slice part: {c.GetType().Name} name='{c.Name}' classes='{string.Join(" ", c.Classes)}' "
                                            + $"size={c.Bounds.Width:0}x{c.Bounds.Height:0} opacity={c.Opacity:0.00} visible={c.IsVisible}"
                                            + (c is Avalonia.Controls.Shapes.Shape sh2 ? $" fill={(sh2.Fill as Avalonia.Media.ISolidColorBrush)?.Color} stroke={(sh2.Stroke as Avalonia.Media.ISolidColorBrush)?.Color}" : ""));
                            }
                            // Only the part the game lights up on hover (the outer band), not the whole slice:
                            // a shape named / styled for hover or highlight, else one that's see-through until
                            // hovered, else the thinnest one (the band along the rim).
                            bool Hinted(Avalonia.Controls.Control c) =>
                                new[] { c.Name ?? "" }.Concat(c.Classes).Any(n => n.IndexOf("hover", StringComparison.OrdinalIgnoreCase) >= 0
                                    || n.IndexOf("highlight", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("select", StringComparison.OrdinalIgnoreCase) >= 0
                                    || n.IndexOf("outer", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("rim", StringComparison.OrdinalIgnoreCase) >= 0
                                    || n.IndexOf("arc", StringComparison.OrdinalIgnoreCase) >= 0);
                            var pick = shapes.Where(Hinted).ToList();
                            if (pick.Count == 0)
                                pick = shapes.Where(sh => sh.Opacity < 0.05 || (sh.Fill is Avalonia.Media.ISolidColorBrush sb && sb.Color.A < 10)).ToList();
                            if (pick.Count == 0 && shapes.Count > 1)
                                pick = new List<Avalonia.Controls.Shapes.Shape> { shapes.OrderBy(sh => sh.Bounds.Width * sh.Bounds.Height).First() };
                            if (pick.Count == 0) pick = shapes;
                            foreach (var sh in pick) { sh.Fill = fill; if (sh.Opacity < 0.05) sh.Opacity = 1; }
                            if (!tintLogged) { tintLogged = true; PluginLog.Info($"Quick wheel: tinted '{label}' ({pick.Count} of {shapes.Count} shape(s) in {anc.GetType().Name})"); }
                            return;
                        }
                        if (anc is Avalonia.Controls.Border b && b.Background != null) { b.Background = fill; return; }
                        if (anc is Avalonia.Controls.Panel pnl && pnl.Background != null) { pnl.Background = fill; return; }
                    }
                    // No slice shape found: at least colour the text.
                    text.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(colour.R, colour.G, colour.B));
                    if (!tintLogged) { tintLogged = true; PluginLog.Info($"Quick wheel: no slice shape round '{label}', coloured the text instead"); }
                }
                catch (Exception e) { if (!tintLogged) { tintLogged = true; PluginLog.Error("Quick wheel: tint failed", e); } }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Let go of the wheel key: pick the slice under the cursor (if any) and close, the same way the game does
        /// (SelectionChanged on the screen, which then asks to close), so the game puts its UI back as it should.
        /// </summary>
        public static void Release()
        {
            if (!open) return;
            int h = hovered;
            var vm = viewModel;
            try
            {
                if (h >= 0 && h < panels.Count && vm != null)
                {
                    var sel = vm.GetType().GetMethod("SelectionChanged", All);
                    if (sel != null) { sel.Invoke(vm, new[] { panels[h] }); return; } // calls OnSelected + closes
                }
                Close();
            }
            catch (Exception e) { PluginLog.Info("Quick wheel release: " + (e.InnerException ?? e).Message); Close(); }
        }

        /// <summary>Asks the screen to close (like pressing its cancel key); disposing it is the last resort.</summary>
        public static void Close()
        {
            var vm = viewModel;
            var h = handle;
            try
            {
                var close = vm?.GetType().GetMethod("SignalWantsClose", All);
                if (close != null) { close.Invoke(vm, null); return; }
            }
            catch (Exception e) { PluginLog.Info("Quick wheel close: " + (e.InnerException ?? e).Message); }
            handle = null;
            viewModel = null;
            open = false;
            try { (h as IDisposable)?.Dispose(); } catch (Exception e) { PluginLog.Info("Quick wheel dispose: " + e.Message); }
        }

        private static void OnSelected(object panel)
        {
            int i = panels.IndexOf(panel);
            if (i >= 0) { picks.Enqueue(i); hovered = -1; }
        }

        private static void OnHover(object panel) => hovered = panel == null ? -1 : panels.IndexOf(panel);

        /// <summary>Sets a property, or its compiler-made backing field if it has no setter.</summary>
        private static void SetAuto(object o, string name, object value)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite && p.GetSetMethod(true) != null) { p.SetValue(o, value); return; }
                var f = t.GetField($"<{name}>k__BackingField", All | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(o, value); return; }
            }
            PluginLog.Info($"Quick wheel: couldn't set {o.GetType().Name}.{name}");
        }

        private static bool describedList, describedInner;

        /// <summary>
        /// Builds a collection type (like the game's MergeableList&lt;T&gt;) holding the items: from a list / array /
        /// enumerable constructor, or a parameterless one followed by Add(item).
        /// </summary>
        private static object MakeCollection(Type type, Type itemType, List<object> items)
        {
            if (type.IsInterface || type.IsAbstract) return null;
            var typedListType = typeof(List<>).MakeGenericType(itemType);
            var typed = (IList)Activator.CreateInstance(typedListType);
            foreach (var i in items) typed.Add(i);
            var array = Array.CreateInstance(itemType, items.Count);
            for (int k = 0; k < items.Count; k++) array.SetValue(items[k], k);

            foreach (var c in type.GetConstructors(All).Where(c => c.GetParameters().Length == 1))
            {
                var pt = c.GetParameters()[0].ParameterType;
                object arg = pt.IsInstanceOfType(typed) ? typed : pt.IsInstanceOfType(array) ? array : null;
                if (arg == null) continue;
                try { return c.Invoke(new[] { arg }); } catch { }
            }
            var add = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(itemType));
            var empty = type.GetConstructor(All, null, Type.EmptyTypes, null);
            if (add != null && (empty != null || type.IsValueType))
            {
                try
                {
                    object o = empty != null ? empty.Invoke(null) : Activator.CreateInstance(type);
                    foreach (var i in items) add.Invoke(o, new[] { i });
                    return o;
                }
                catch (Exception e) { PluginLog.Info("Quick wheel: filling " + type.Name + " failed: " + (e.InnerException ?? e).Message); }
            }
            if (!describedInner)
            {
                describedInner = true;
                PluginLog.Info("Quick wheel: don't know how to make " + type.FullName);
                GameInternals.Describe(type, "QUICKWHEEL INNER");
            }
            return null;
        }

        /// <summary>Builds the game's ImmutableMergeableList of panels (whichever way it can be made).</summary>
        private static object MakeList(Type listType, Type itemType, List<object> items)
        {
            var typedListType = typeof(List<>).MakeGenericType(itemType);
            var typed = (IList)Activator.CreateInstance(typedListType);
            foreach (var i in items) typed.Add(i);
            var array = Array.CreateInstance(itemType, items.Count);
            for (int k = 0; k < items.Count; k++) array.SetValue(items[k], k);

            // A constructor taking a list / array / enumerable.
            foreach (var c in listType.GetConstructors(All).Where(c => c.GetParameters().Length == 1))
            {
                var pt = c.GetParameters()[0].ParameterType;
                object arg = pt.IsInstanceOfType(typed) ? typed : pt.IsInstanceOfType(array) ? array : null;
                if (arg == null) continue;
                try { return c.Invoke(new[] { arg }); } catch { }
            }
            // A static factory (Create / From ...).
            foreach (var m in listType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.GetParameters().Length == 1 && listType.IsAssignableFrom(m.ReturnType)))
            {
                var pt = m.GetParameters()[0].ParameterType;
                object arg = pt.IsInstanceOfType(typed) ? typed : pt.IsInstanceOfType(array) ? array : null;
                if (arg == null) continue;
                try { return m.Invoke(null, new[] { arg }); } catch { }
            }
            // An implicit conversion.
            var conv = listType.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == listType
                && (m.GetParameters()[0].ParameterType.IsInstanceOfType(typed) || m.GetParameters()[0].ParameterType.IsInstanceOfType(array)));
            if (conv != null)
            {
                var pt = conv.GetParameters()[0].ParameterType;
                return conv.Invoke(null, new[] { pt.IsInstanceOfType(typed) ? typed : (object)array });
            }
            // A constructor taking another collection type we can build (e.g. MergeableList<T>).
            foreach (var c in listType.GetConstructors(All).Where(c => c.GetParameters().Length == 1 && !c.GetParameters()[0].ParameterType.IsByRef))
            {
                var inner = MakeCollection(c.GetParameters()[0].ParameterType, itemType, items);
                if (inner == null) continue;
                try { return c.Invoke(new[] { inner }); } catch (Exception e) { PluginLog.Info("Quick wheel: list constructor failed: " + (e.InnerException ?? e).Message); }
            }
            if (!describedList)
            {
                describedList = true;
                PluginLog.Info("Quick wheel: don't know how to make " + listType.FullName);
                GameInternals.Describe(listType, "QUICKWHEEL LIST");
            }
            return null;
        }
    }
}
