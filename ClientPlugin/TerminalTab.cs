using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.UI.TerminalScreen;
using Keen.VRage.Library.Localization;

namespace RaceDisplay
{
    /// <summary>
    /// Puts the Racing Display menu in the game's terminal screen (G: Character, Grid, Build, Contracts, Trade, Map)
    /// as one more tab after Map, with sub-tabs Race, Records, Courses, Settings and Controls.
    ///
    /// TerminalScreenViewModel.BuildScreenTree fills Categories (the top tabs) and SubScreens (the tabs under them,
    /// each with a factory for its content); a postfix adds ours. A sub-screen whose content is a Control is shown as
    /// it is. All five share one control (RacingMenu's page and description), which switches page when the selected
    /// sub-screen changes.
    /// </summary>
    internal static class TerminalTab
    {
        private const string CategoryName = "RacingDisplay";
        internal static readonly string[] Pages = { "Race", "Records", "Courses", "Settings", "Controls" };

        /// <summary>Our tabs and their plain titles (the game would look the title up as a text key).</summary>
        private static readonly ConditionalWeakTable<object, string> titles = new ConditionalWeakTable<object, string>();

        internal static bool Added { get; private set; }

        // For opening the terminal on our tab from the race setup key.
        private static object inGameUi;
        private static InteractionInfoBox lastInteraction;
        private sealed class InteractionInfoBox { public object Value; }

        public static void Apply(Harmony harmony)
        {
            try
            {
                var vm = typeof(TerminalScreenViewModel);
                var build = AccessTools.Method(vm, "BuildScreenTree");
                if (build == null) { PluginLog.Error("Terminal tab: BuildScreenTree not found (Racing Display stays in the pause menu)"); return; }
                harmony.Patch(build, postfix: new HarmonyMethod(typeof(TerminalTab), nameof(BuildScreenTreePostfix)));

                var changed = AccessTools.Method(vm, "OnSelectedSubScreenChanged");
                if (changed != null) harmony.Patch(changed, postfix: new HarmonyMethod(typeof(TerminalTab), nameof(SelectionPostfix)));

                foreach (var t in new[] { typeof(TerminalCategoryTab), typeof(TerminalSubScreen) })
                {
                    var refresh = AccessTools.Method(t, "RefreshLocalizedHeader");
                    if (refresh != null) harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(TerminalTab), nameof(HeaderPostfix)));
                }

                // Remember the in-game UI and the last terminal opening's interaction (to open it on our tab with K).
                var ui = AccessTools.TypeByName("Keen.Game2.Client.UI.InGame.InGameUI");
                if (ui != null)
                    foreach (var impl in GameInternals.GameTypes().Where(x => !x.IsAbstract && !x.IsInterface && ui.IsAssignableFrom(x)).Take(5))
                        foreach (var m in impl.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                     .Where(m => m.Name.EndsWith("OpenTerminalScreen") && m.GetParameters().Length == 2))
                            harmony.Patch(m, prefix: new HarmonyMethod(typeof(TerminalTab), nameof(OpenTerminalPrefix)));

                // The player's keybinds handler opens the terminal with G; Shift+J uses it to open the terminal too.
                var keybinds = AccessTools.TypeByName("Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerComponent");
                var kInit = keybinds == null ? null : AccessTools.GetDeclaredMethods(keybinds).FirstOrDefault(m => m.Name == "Init");
                if (kInit != null) harmony.Patch(kInit, postfix: new HarmonyMethod(typeof(TerminalTab), nameof(KeybindsInitPostfix)));
                else PluginLog.Info("Terminal tab: PlayerKeybindsInputHandlerComponent.Init not found (J needs G once first)");

                Added = true;
                PluginLog.Info("Terminal tab: patched (Racing Display tab after Map)");
            }
            catch (Exception e) { PluginLog.Error("Terminal tab: couldn't patch the terminal screen", e); }
        }

        private static readonly List<WeakReference> keybindHandlers = new List<WeakReference>();
        private static void KeybindsInitPostfix(object __instance)
        {
            lock (keybindHandlers)
            {
                keybindHandlers.RemoveAll(w => !w.IsAlive);
                keybindHandlers.Add(new WeakReference(__instance));
            }
        }

        // Set by J: once the terminal has opened, go to Racing Display > Race.
        private static long selectRaceUntil;

        private static void OpenTerminalPrefix(object __instance, object[] __args)
        {
            inGameUi = __instance;
            if (__args != null && __args.Length > 0 && __args[0] != null) lastInteraction = new InteractionInfoBox { Value = __args[0] };
        }

        private static void HeaderPostfix(object __instance)
        {
            if (!titles.TryGetValue(__instance, out var title)) return;
            try { AccessTools.Property(__instance.GetType(), "LocalizedHeader")?.SetValue(__instance, title); }
            catch { }
        }

        private static void SetTitle(object tab, string title)
        {
            titles.AddOrUpdate(tab, title);
            HeaderPostfix(tab);
        }

        private static void BuildScreenTreePostfix(TerminalScreenViewModel __instance)
        {
            lastVm = new WeakReference<TerminalScreenViewModel>(__instance);
            try
            {
                var vmType = typeof(TerminalScreenViewModel);
                var catProp = AccessTools.Property(vmType, "Categories");
                var subProp = AccessTools.Property(vmType, "SubScreens");
                var categories = (catProp.GetValue(__instance) as IEnumerable<TerminalCategoryTab>)?.ToList() ?? new List<TerminalCategoryTab>();
                var subs = (subProp.GetValue(__instance) as IEnumerable<TerminalSubScreen>)?.ToList() ?? new List<TerminalSubScreen>();
                if (categories.Any(c => c.Id.Value == CategoryName)) return;

                var category = (TerminalCategoryTab)AccessTools.Constructor(typeof(TerminalCategoryTab), new[] { typeof(TerminalCategoryId), typeof(LocKey), typeof(bool) })
                    .Invoke(new object[] { new TerminalCategoryId(CategoryName), LocKey.FromString("Racing Display"), true });
                SetTitle(category, "Racing Display");

                // One control for all our sub-screens: made when the first is shown.
                Avalonia.Controls.Control shared = null;
                object Content(TerminalSubScreen s)
                {
                    if (shared == null) shared = RacingMenu.Instance.CreateTerminalHost();
                    return shared;
                }

                foreach (var page in Pages)
                {
                    var sub = (TerminalSubScreen)AccessTools.Constructor(typeof(TerminalSubScreen),
                            new[] { typeof(TerminalCategoryTab), typeof(TerminalScreenId), typeof(LocKey), typeof(bool), typeof(Type), typeof(Func<TerminalSubScreen, object>) })
                        .Invoke(new object[] { category, new TerminalScreenId(CategoryName + "/" + page), LocKey.FromString(page), true,
                            typeof(RacingMenu), (Func<TerminalSubScreen, object>)Content });
                    SetTitle(sub, page);
                    subs.Add(sub);
                }

                // Map > Race Map: the whole course as a big map, after the Map tab's own pages (GPS ...). Its id starts
                // with the Map tab's (the terminal groups pages by that), not ours.
                var mapCat = categories.FirstOrDefault(c => string.Equals(c.Id.Value, "Map", StringComparison.OrdinalIgnoreCase))
                             ?? categories.FirstOrDefault(c => c.Id.Value != null && c.Id.Value.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0);
                if (mapCat != null)
                {
                    RaceMapView mapView = null;
                    object MapContent(TerminalSubScreen s) => mapView ?? (mapView = new RaceMapView());
                    var mapSub = (TerminalSubScreen)AccessTools.Constructor(typeof(TerminalSubScreen),
                            new[] { typeof(TerminalCategoryTab), typeof(TerminalScreenId), typeof(LocKey), typeof(bool), typeof(Type), typeof(Func<TerminalSubScreen, object>) })
                        .Invoke(new object[] { mapCat, new TerminalScreenId(mapCat.Id.Value + "/RaceMap"), LocKey.FromString("Race Map"), true,
                            typeof(RaceMapView), (Func<TerminalSubScreen, object>)MapContent });
                    SetTitle(mapSub, "Race Map");
                    int last = subs.FindLastIndex(x => ReferenceEquals(x.Category, mapCat));
                    subs.Insert(last >= 0 ? last + 1 : subs.Count, mapSub);
                }
                else PluginLog.Info("Terminal tab: no Map tab found for the Race Map page");

                // After Map (the last tab), or at the end.
                int at = categories.Count;
                for (int i = 0; i < categories.Count; i++)
                    if (categories[i].Id.Value != null && categories[i].Id.Value.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0) at = i + 1;
                categories.Insert(at, category);

                catProp.SetValue(__instance, (IReadOnlyList<TerminalCategoryTab>)categories);
                subProp.SetValue(__instance, (IReadOnlyList<TerminalSubScreen>)subs);
            }
            catch (Exception e) { PluginLog.Error("Terminal tab: couldn't add the Racing Display tab", e); }
        }

        /// <summary>A different sub-screen was picked: if it's one of ours, show that page.</summary>
        private static void SelectionPostfix(TerminalScreenViewModel __instance)
        {
            try
            {
                lastVm = new WeakReference<TerminalScreenViewModel>(__instance);
                var selected = AccessTools.Property(typeof(TerminalScreenViewModel), "SelectedSubScreen")?.GetValue(__instance) as TerminalSubScreen;
                var id = selected?.Id.Value;
                if (id == null || !id.StartsWith(CategoryName + "/")) return;
                RacingMenu.Instance.ShowTerminalPage(id.Substring(CategoryName.Length + 1));
            }
            catch (Exception e) { PluginLog.Error("Terminal tab: page switch failed", e); }
        }

        /// <summary>
        /// Opens the terminal on Racing Display > Race (the race setup key). Uses the in-game UI and interaction of the
        /// last time the terminal was opened (with G), so it needs the terminal to have been opened once.
        /// </summary>
        private static WeakReference<TerminalScreenViewModel> lastVm;
        private static Avalonia.Threading.DispatcherTimer selectTimer;

        /// <summary>
        /// After Shift+J opened the terminal: keep selecting Racing Display > Race (a few times a second, for a few seconds)
        /// until it's the selected page. The terminal picks its own first page as it opens, which can come after ours.
        /// </summary>
        private static void StartSelectingRace()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                selectTimer?.Stop();
                int stableTicks = 0;
                selectTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), Avalonia.Threading.DispatcherPriority.Background, (_, __) =>
                {
                    if (Environment.TickCount64 > selectRaceUntil) { selectTimer.Stop(); return; }
                    TerminalScreenViewModel vm = null;
                    if (lastVm == null || !lastVm.TryGetTarget(out vm) || vm == null) return;
                    var selected = AccessTools.Property(typeof(TerminalScreenViewModel), "SelectedSubScreen")?.GetValue(vm) as TerminalSubScreen;
                    if (selected?.Id.Value == CategoryName + "/Race")
                    {
                        // Stays selected for half a second: done.
                        if (++stableTicks >= 5) { selectTimer.Stop(); selectRaceUntil = 0; }
                        return;
                    }
                    stableTicks = 0;
                    SelectRace(vm);
                });
                selectTimer.Start();
            });
        }

        private static void SelectRace(TerminalScreenViewModel vm)
        {
            try
            {
                var byCategory = AccessTools.Method(typeof(TerminalScreenViewModel), "TrySelect", new[] { typeof(TerminalCategoryId) });
                byCategory?.Invoke(vm, new object[] { new TerminalCategoryId(CategoryName) });
                var byScreen = AccessTools.Method(typeof(TerminalScreenViewModel), "TrySelect", new[] { typeof(TerminalScreenId) });
                var ok = byScreen?.Invoke(vm, new object[] { new TerminalScreenId(CategoryName + "/Race") });
                if (ok is bool b && !b) PluginLog.Info("Terminal tab: TrySelect(Racing Display / Race) said no");
            }
            catch (Exception e) { PluginLog.Error("Terminal tab: couldn't select Racing Display > Race", e); }
        }

        public static bool OpenRacePage()
        {
            if (RacingMenu.Instance.IsOpen) return true;
            // 1) Like pressing G (the game's own keybinds handler opens the terminal, on the Character tab), then
            //    switch to Racing Display > Race as soon as it's open.
            try
            {
                var selType = AccessTools.TypeByName("Keen.Game2.Client.WorldObjects.Shared.PlayerKeybindsInputHandlerDefinition+CharacterScreenSelection");
                object handler = null;
                lock (keybindHandlers)
                {
                    keybindHandlers.RemoveAll(w => !w.IsAlive);
                    for (int i = keybindHandlers.Count - 1; i >= 0 && handler == null; i--)
                    {
                        var h = keybindHandlers[i].Target;
                        var interactor = h == null ? null : AccessTools.Property(h.GetType(), "TerminalInteractor")?.GetValue(h);
                        if (interactor != null) handler = h;
                    }
                }
                var toggle = handler == null ? null : AccessTools.Method(handler.GetType(), "ToggleScreen");
                if (toggle != null && selType != null)
                {
                    selectRaceUntil = Environment.TickCount64 + 4000;
                    lastVm = null;
                    toggle.Invoke(handler, new[] { Activator.CreateInstance(selType) });
                    StartSelectingRace();
                    return true;
                }
            }
            catch (Exception e) { selectRaceUntil = 0; PluginLog.Error("Terminal tab: couldn't open the terminal like G does", e); }

            // 2) Straight onto our tab, with what the last terminal opening used.
            try
            {
                if (inGameUi == null || lastInteraction == null) return false;
                var m = inGameUi.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name.EndsWith("OpenTerminalScreen") && x.GetParameters().Length == 2
                                         && x.GetParameters()[1].ParameterType == typeof(TerminalScreenId?));
                if (m == null) return false;
                m.Invoke(inGameUi, new[] { lastInteraction.Value, (object)(TerminalScreenId?)new TerminalScreenId(CategoryName + "/Race") });
                return true;
            }
            catch (Exception e)
            {
                PluginLog.Error("Terminal tab: couldn't open the terminal on Racing Display", e);
                return false;
            }
        }
    }
}
