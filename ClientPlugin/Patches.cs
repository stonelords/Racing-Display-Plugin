using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Game.SessionComponents;
using Keen.VRage.Core;

namespace RaceDisplay
{
    /// <summary>
    /// Harmony hooks:
    ///  - VRageCore.Update(TimeSpan): the game's per-frame loop (it ticks the scene). Runs the timer every frame.
    ///  - WorldSessionComponent.Init / InitAsync / Destroy: tracks loaded worlds (world name, pause, client session).
    ///  - PlayerControllerComponent.Init: backup way to find the local player if the world hooks don't fire.
    ///  - GameMenu.UpdateButtons: adds the "Racing Display" button to the pause (Esc) menu.
    /// Every patch is applied on its own, and the log says which ones worked.
    /// </summary>
    internal static class Patches
    {
        internal static Action OnFrame;
        /// <summary>Esc (pause menu) pressed: return true to swallow it (used to leave a replay with Esc).</summary>
        internal static Func<bool> OnPauseMenuRequested;

        /// <summary>Race HUD only is on (set by the controller each frame from its setting).</summary>
        internal static volatile bool RaceHudOnlyNow;
        /// <summary>The game's HUD cycle key (U) asked to turn race HUD only on (1) or off (-1); 0 = nothing.</summary>
        internal static int RaceHudCycleRequest;
        private static bool loggedFirstFrame;

        /// <summary>Goes up each time the game saves the world (manual save, autosave): courses are written into the save then.</summary>
        internal static int WorldSaves;
        /// <summary>
        /// The HUD key cycles full -> minimal -> race HUD only -> none -> full. Race HUD only is the game's full UI with
        /// its own HUD screens hidden by the plugin, so from minimal it sets full and asks for race HUD only; from race
        /// HUD only it goes back to minimal first, so the game's own step then takes it on to none.
        /// </summary>
        private static bool HudCyclePrefix(object __instance)
        {
            try
            {
                var stateProp = AccessTools.Property(__instance.GetType(), "VisibilityState");
                var apply = AccessTools.Method(__instance.GetType(), "ApplyVisibilityState");
                if (stateProp == null || apply == null) return true;
                string state = stateProp.GetValue(__instance)?.ToString();
                // (Race HUD only can also be on from the Settings switch while the game is on full: then too it goes to none.)
                if (RaceHudOnlyNow)
                {
                    System.Threading.Interlocked.Exchange(ref RaceHudCycleRequest, -1);
                    RaceHudOnlyNow = false;
                    stateProp.SetValue(__instance, Enum.Parse(stateProp.PropertyType, "Minimal"));
                    return true;   // the game's step: minimal -> none
                }
                if (state == "Minimal")
                {
                    System.Threading.Interlocked.Exchange(ref RaceHudCycleRequest, 1);
                    RaceHudOnlyNow = true;
                    stateProp.SetValue(__instance, Enum.Parse(stateProp.PropertyType, "Full"));
                    apply.Invoke(__instance, null);
                    return false;
                }
            }
            catch (Exception e) { PluginLog.Error("Race HUD only: HUD key step failed", e); }
            return true;
        }

        /// <summary>The save has been written: put the race course in it straight away (publishing uploads the save
        /// right after saving, so a later write would miss the Workshop copy).</summary>
        internal static Action OnWorldSaveCompleted;
        private static void WorldSaveCompletedPostfix()
        {
            System.Threading.Interlocked.Increment(ref WorldSaves);
            try { OnWorldSaveCompleted?.Invoke(); }
            catch (Exception e) { PluginLog.Error("World save: writing the race course failed", e); }
        }

        private static void WorldSavePostfix() => System.Threading.Interlocked.Increment(ref WorldSaves);

        public static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(VRageCore), nameof(VRageCore.Update), new[] { typeof(TimeSpan) }),
                nameof(FramePostfix), "VRageCore.Update(TimeSpan)");

            foreach (var name in new[] { "Init", "InitAsync" })
                Patch(harmony, AccessTools.Method(typeof(WorldSessionComponent), name),
                    nameof(WorldCreatedPostfix), "WorldSessionComponent." + name);

            Patch(harmony, AccessTools.Method(typeof(WorldSessionComponent), "Destroy"),
                nameof(WorldDestroyedPostfix), "WorldSessionComponent.Destroy");

            Patch(harmony, AccessTools.Method(typeof(PlayerControllerComponent), "Init", Type.EmptyTypes),
                nameof(ControllerCreatedPostfix), "PlayerControllerComponent.Init()");

            // For opening the pause menu from code (the course edit tool wheel): the objects that open it,
            // and the arguments the game builds its view model with.
            foreach (var typeName in new[] { "Keen.Game2.AutoPauseWorldComponent", "Keen.Game2.Client.UI.Library.SharedUIComponent" })
            {
                var t = AccessTools.TypeByName(typeName);
                Patch(harmony, t == null ? null : AccessTools.Method(t, "Init", Type.EmptyTypes), nameof(CaptureInstancePostfix), typeName + ".Init");
            }
            var vmType = AccessTools.TypeByName("Keen.Game2.Client.UI.Menu.InGameMenu.InGameMenuScreenViewModel");
            if (vmType != null)
                foreach (var ctor in vmType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    Patch(harmony, ctor, nameof(PauseMenuViewModelPostfix), "InGameMenuScreenViewModel constructor");

            // For the edit tool wheel (the game's quick-select radial menu): InGameUI opens it, and the paint gun's
            // quick menu definition has the right cancel / mouse-look keys.
            // InGameUI (may be an interface): catch whatever object implements it when it's made.
            var inGameUi = AccessTools.TypeByName("Keen.Game2.Client.UI.InGame.InGameUI");
            if (inGameUi == null) PluginLog.Info("InGameUI type not found");
            else
            {
                PluginLog.Info($"InGameUI is {(inGameUi.IsInterface ? "an interface" : inGameUi.IsAbstract ? "an abstract class" : "a class")}");
                foreach (var t in GameInternals.GameTypes().Where(t => !t.IsAbstract && !t.IsInterface && inGameUi.IsAssignableFrom(t)).Take(5))
                    foreach (var ctor in t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        Patch(harmony, ctor, nameof(CaptureInGameUiPostfix), t.FullName + " constructor (InGameUI)");
            }
            var quickVm = AccessTools.TypeByName("Keen.Game2.Client.UI.Library.Controls.QuickMenu.QuickMenuScreenViewModel");
            if (quickVm != null)
                foreach (var ctor in quickVm.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    Patch(harmony, ctor, nameof(QuickMenuCreatedPostfix), "QuickMenuScreenViewModel constructor");
            var paintTool = AccessTools.TypeByName("Keen.Game2.Client.WorldObjects.Tools.PaintToolControllableComponent");
            var paintOpen = paintTool == null ? null : AccessTools.Method(paintTool, "OpenQuickSelectMenu");
            if (paintOpen != null) Patch(harmony, paintOpen, nameof(PaintMenuPrefixCapture), "PaintToolControllableComponent.OpenQuickSelectMenu");

            // The game's HUD cycle key (U: full -> minimal -> none): race HUD only goes in as a step after minimal.
            var screenManager = AccessTools.TypeByName("Keen.VRage.UI.Screens.ScreenManager");
            var cycle = screenManager == null ? null : AccessTools.Method(screenManager, "CycleVisibilityState");
            if (cycle == null) PluginLog.Error("Patch target not found: ScreenManager.CycleVisibilityState (race HUD only on the HUD key)");
            else
                try
                {
                    harmony.Patch(cycle, prefix: new HarmonyMethod(typeof(Patches), nameof(HudCyclePrefix)));
                    PluginLog.Info("Patched ScreenManager.CycleVisibilityState (race HUD only on the HUD key)");
                }
                catch (Exception e) { PluginLog.Error("Couldn't patch ScreenManager.CycleVisibilityState", e); }

            // The game saving the world: the race course is written into the save folder then.
            var saveComp = AccessTools.TypeByName("Keen.Game2.Simulation.RuntimeSystems.Saves.SaveSessionComponent");
            Patch(harmony, saveComp == null ? null : AccessTools.Method(saveComp, "Serialize"), nameof(WorldSavePostfix), "SaveSessionComponent.Serialize (world save)");
            Patch(harmony, saveComp == null ? null : AccessTools.Method(saveComp, "NotifyClientSaveCompletedAsync"), nameof(WorldSaveCompletedPostfix), "SaveSessionComponent.NotifyClientSaveCompletedAsync (world saved)");
            Patch(harmony, vmType == null ? null : AccessTools.Method(vmType, "SaveGameInternal"), nameof(WorldSavePostfix), "InGameMenuScreenViewModel.SaveGameInternal (save from the menu)");

            // Esc during a replay ends the replay instead of opening the pause menu.
            var inGameUiSession = AccessTools.TypeByName("Keen.Game2.Client.UI.InGame.SessionInGameUISessionComponent");
            var createMenu = inGameUiSession == null ? null : AccessTools.Method(inGameUiSession, "CreateInGameMenuScreen");
            if (createMenu == null) PluginLog.Error("Patch target not found: SessionInGameUISessionComponent.CreateInGameMenuScreen (Esc to leave replay)");
            else
                try
                {
                    harmony.Patch(createMenu, prefix: new HarmonyMethod(typeof(Patches), nameof(PauseMenuOpeningPrefix)));
                    PluginLog.Info("Patched SessionInGameUISessionComponent.CreateInGameMenuScreen (Esc to leave replay)");
                }
                catch (Exception e) { PluginLog.Error("Couldn't patch CreateInGameMenuScreen", e); }

            // Starting a world from the Workshop screen: remember which download it was (for its race course).
            var worldDetail = AccessTools.TypeByName("Keen.Game2.Client.UI.Shared.UGC.Detail.DetailActions.WorldDetailViewModel");
            Patch(harmony, worldDetail == null ? null : AccessTools.Method(worldDetail, "Start"),
                nameof(WorkshopWorldStartPostfix), "WorldDetailViewModel.Start (world started from the Workshop)");
            // New Game > scenario / Workshop world list: Start, and the quick action on a list item.
            var scenarioSelection = AccessTools.TypeByName("Keen.Game2.Client.UI.Menu.ScenarioSelection.ScenarioSelectionScreenViewModel");
            foreach (var name in new[] { "StartAction", "ProcessQuickAction" })
            {
                var m = scenarioSelection == null ? null : AccessTools.Method(scenarioSelection, name);
                if (m == null) { PluginLog.Error("Patch target not found: ScenarioSelectionScreenViewModel." + name); continue; }
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(Patches), nameof(WorldItemStartPrefix)));
                    PluginLog.Info($"Patched ScenarioSelectionScreenViewModel.{name} (world started from New Game)");
                }
                catch (Exception e) { PluginLog.Error("Couldn't patch ScenarioSelectionScreenViewModel." + name, e); }
            }

            var gameMenu = AccessTools.TypeByName("Keen.Game2.Client.UI.Menu.GameMenu");
            Patch(harmony, gameMenu == null ? null : AccessTools.Method(gameMenu, "UpdateButtons"),
                nameof(PauseMenuPostfix), "GameMenu.UpdateButtons (pause menu button)");
        }

        /// <summary>
        /// The Workshop download the next world was started from: its ids as text (the item id and the game's
        /// container name for it), and when. Only set when a world is started from the Workshop screen.
        /// </summary>
        public static volatile string[] WorkshopStartIds;
        public static DateTime WorkshopStartAt;

        private static void WorkshopWorldStartPostfix(object __instance) =>
            RememberStartedItem(AccessTools.Field(__instance.GetType(), "_viewModel")?.GetValue(__instance), "Workshop details");

        // StartAction(IUGCItemViewModel) / ProcessQuickAction(WorldViewModel): the item is the first argument.
        private static void WorldItemStartPrefix(object[] __args) =>
            RememberStartedItem(__args != null && __args.Length > 0 ? __args[0] : null, "New Game list");

        /// <summary>
        /// Notes every id-like value of the world item being started (and of its Model): the game's ids for it,
        /// which for a Workshop world include the Steam item id somewhere. Logged, so it's clear what was found.
        /// </summary>
        private static void RememberStartedItem(object item, string from)
        {
            try
            {
                if (item == null) return;
                var ids = new System.Collections.Generic.List<string>();
                void Collect(object o, int depth)
                {
                    if (o == null || depth > 1) return;
                    foreach (var prop in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (prop.GetIndexParameters().Length > 0) continue;
                        object v;
                        try { v = prop.GetValue(o); } catch { continue; }
                        if (v == null) continue;
                        if (v is string || v.GetType().IsPrimitive || v.GetType().Name.Contains("Id") || prop.Name.Contains("Id") || prop.Name.Contains("ID"))
                        {
                            ids.Add(v.ToString());
                            if (AccessTools.Field(v.GetType(), "Name")?.GetValue(v) is string n) ids.Add(n);
                        }
                        else if (prop.Name == "Model") Collect(v, depth + 1);
                    }
                }
                Collect(item, 0);
                WorkshopStartIds = ids.Where(x => !string.IsNullOrWhiteSpace(x) && x.Any(char.IsDigit)).Distinct().Take(40).ToArray();
                WorkshopStartAt = DateTime.Now;
                PluginLog.Info($"World started ({from}, {item.GetType().Name}): " + string.Join(" | ", WorkshopStartIds));
            }
            catch (Exception e) { PluginLog.Error("Couldn't read which world was started", e); }
        }

        private static bool PauseMenuOpeningPrefix(ref Keen.VRage.Library.Utils.IObservableDisposable __result)
        {
            try
            {
                if (OnPauseMenuRequested != null && OnPauseMenuRequested())
                {
                    __result = null;     // the Esc hotkey ignores the result
                    return false;        // don't open the pause menu
                }
            }
            catch (Exception e) { PluginLog.Error("Esc handler failed", e); }
            return true;
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodBase target, string postfix, string label)
        {
            if (target == null)
            {
                PluginLog.Error($"Patch target not found: {label}");
                return;
            }
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(Patches), postfix));
                PluginLog.Info($"Patched {label}");
            }
            catch (Exception e)
            {
                PluginLog.Error($"Failed to patch {label}", e);
            }
        }

        private static void FramePostfix()
        {
            if (!loggedFirstFrame)
            {
                loggedFirstFrame = true;
                PluginLog.Info("Per-frame hook is running");
            }
            try { OnFrame?.Invoke(); }
            catch (Exception e) { PluginLog.Error("Frame update failed", e); }
        }

        private static void WorldCreatedPostfix(WorldSessionComponent __instance)
        {
            PluginLog.Info("World component created");
            Se2Bridge.OnWorldCreated(__instance);
        }

        private static void WorldDestroyedPostfix(WorldSessionComponent __instance)
        {
            PluginLog.Info("World component destroyed");
            Se2Bridge.OnWorldDestroyed(__instance);
        }

        private static void PauseMenuPostfix(object __instance)
        {
            try
            {
                PauseMenuButton.Add(__instance as Avalonia.Controls.Control);
            }
            catch (Exception e) { PluginLog.Error("Couldn't add the Racing Display button to the pause menu", e); }
        }

        private static void QuickMenuCreatedPostfix(object[] __args)
        {
            var def = __args?.FirstOrDefault();
            if (def != null && QuickWheel.SavedDefinition == null) { QuickWheel.SavedDefinition = def; PluginLog.Info("Quick wheel: borrowed the game's quick menu definition"); }
        }

        /// <summary>The paint tool holds the InGameUI and its quick menu definition: keep them when its wheel opens.</summary>
        private static void PaintMenuPrefixCapture(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var ui = AccessTools.Field(t, "_gameUI")?.GetValue(__instance);
                if (ui != null) { GameInternals.Remember("InGameUI", ui); PluginLog.Info("Quick wheel: got InGameUI from the paint tool (" + ui.GetType().FullName + ")"); }
            }
            catch { }
        }

        private static void CaptureInGameUiPostfix(object __instance)
        {
            if (__instance == null) return;
            GameInternals.Remember("InGameUI", __instance);
            PluginLog.Info("Quick wheel: InGameUI created (" + __instance.GetType().FullName + ")");
        }

        private static void CaptureInstancePostfix(object __instance)
        {
            if (__instance != null) GameInternals.Remember(__instance);
        }

        private static void PauseMenuViewModelPostfix(object[] __args)
        {
            GameInternals.PauseMenuArgs = (object[])__args?.Clone();
        }

        private static void ControllerCreatedPostfix(PlayerControllerComponent __instance)
        {
            PluginLog.Info("Player controller created");
            Se2Bridge.OnControllerCreated(__instance);
        }
    }
}
