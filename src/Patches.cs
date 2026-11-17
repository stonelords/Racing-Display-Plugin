using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Game.SessionComponents;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;

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

        private static void PatchFinalizer(Harmony harmony, System.Reflection.MethodBase target, string finalizer, string what)
        {
            if (target == null) { PluginLog.Info("Patch target not found: " + what); return; }
            try
            {
                harmony.Patch(target, finalizer: new HarmonyMethod(typeof(Patches), finalizer));
                PluginLog.Info("Patched " + what);
            }
            catch (Exception e) { PluginLog.Error("Couldn't patch " + what, e); }
        }

        private static bool loggedGoneShip, loggedGoneShipOpen;

        /// <summary>
        /// The terminal open on a ship that's been removed: no crash. (The terminal stays open: shutting it meant the
        /// Racing Display menu couldn't be opened either, until you left the removed seat.) The game's part of the
        /// update that needs the ship is skipped, the rest of the terminal works.
        /// </summary>
        private static Exception TerminalUpdateFinalizer(Exception __exception)
        {
            if (!(__exception is NullReferenceException)) return __exception;
            // What it was open on: skipped from now on (see the prefix). (Kept small: only a few are ever gone.)
            if (brokenTerminalEntities.Count > 50) brokenTerminalEntities.Clear();
            // (Only the ones that are gone, found by reading them once here: not the character opening it.)
            int before = brokenTerminalEntities.Count;
            foreach (var o in terminalNow)
            {
                if (!(o is Keen.VRage.DCS.Components.Entity e)) continue;
                bool gone;
                try { gone = !e.Data.Has<WorldTransform>(); }
                catch { gone = true; }
                if (gone) brokenTerminalEntities.Add(e);
            }
            if (brokenTerminalEntities.Count == before) foreach (var o in terminalNow) brokenTerminalEntities.Add(o);
            if (!loggedGoneShip)
            {
                loggedGoneShip = true;
                PluginLog.Info("Terminal: its ship is gone (cut or removed); the game would have crashed here. Caught (logged once)");
            }
            return null;
        }

        // Where the terminal keeps the entities it's open on (found once by looking at its fields): an Entity field,
        // or an Entity inside one of its fields (its interaction info).
        private static List<Func<object, object>> terminalEntityReaders;
        private static bool loggedTerminalSkip;
        private static readonly List<object> terminalNow = new List<object>();
        private static readonly HashSet<object> brokenTerminalEntities = new HashSet<object>(ReferenceEqualityComparer.Instance);

        private static bool TerminalUpdatePrefix(object __instance)
        {
            try
            {
                if (__instance == null) return true;
                if (terminalEntityReaders == null)
                {
                    terminalEntityReaders = new List<Func<object, object>>();
                    var found = new List<string>();
                    var entityType = typeof(Keen.VRage.DCS.Components.Entity);
                    const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    foreach (var f in __instance.GetType().GetFields(all))
                    {
                        if (entityType.IsAssignableFrom(f.FieldType)) { var ff = f; terminalEntityReaders.Add(o => ff.GetValue(o)); found.Add(f.Name); continue; }
                        if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.Namespace?.StartsWith("Keen") != true) continue;
                        if (f.FieldType.Name.IndexOf("Interaction", StringComparison.Ordinal) < 0) continue;
                        foreach (var inner in f.FieldType.GetFields(all))
                            if (entityType.IsAssignableFrom(inner.FieldType))
                            {
                                var outer = f; var ii = inner;
                                terminalEntityReaders.Add(o => { object box = outer.GetValue(o); return box == null ? null : ii.GetValue(box); });
                                found.Add(f.Name + "." + inner.Name);
                            }
                    }
                    PluginLog.Info("Terminal: checks its ship is still there through " + (found.Count > 0 ? string.Join(", ", found) : "nothing found (only the error is caught)"));
                }
                terminalNow.Clear();
                foreach (var read in terminalEntityReaders)
                    if (read(__instance) is Keen.VRage.DCS.Components.Entity e)
                    {
                        // One the game's update has already failed on (it's gone): skip it, as catching the
                        // error every frame left the game at a few frames a second.
                        if (brokenTerminalEntities.Contains(e))
                        {
                            if (!loggedTerminalSkip) { loggedTerminalSkip = true; PluginLog.Info("Terminal: open on a ship that's gone (cut or removed): the game's update of it is skipped, so it doesn't crash"); }
                            return false;
                        }
                        terminalNow.Add(e);
                    }
            }
            catch { /* run the game's own */ }
            return true;
        }

        /// <summary>The game's analytics for a terminal opened on a ship already known to be gone: skipped (its error, caught, took a quarter of a second every time).</summary>
        private static bool TerminalOpenedPrefix(object[] __args)
        {
            try
            {
                if (brokenTerminalEntities.Count == 0 || terminalEntityReaders == null || __args == null) return true;
                foreach (object arg in __args)
                {
                    if (arg == null || arg.GetType().Name != "TerminalScreenViewModel") continue;
                    foreach (var read in terminalEntityReaders)
                        if (read(arg) is object e && brokenTerminalEntities.Contains(e)) return false;
                }
            }
            catch { }
            return true;
        }

        private static Exception TerminalOpenedFinalizer(Exception __exception)
        {
            if (!(__exception is NullReferenceException)) return __exception;
            if (!loggedGoneShipOpen) { loggedGoneShipOpen = true; PluginLog.Info("Terminal: opened on a ship that's gone; the game's error there was caught (logged once)"); }
            return null;
        }

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

            // Collision damage to a ship (runs where the game's server runs: single player or hosting).
            var impact = AccessTools.TypeByName("Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss.GridImpactDamageReceiverComponent");
            var damageBlock = impact == null ? null : AccessTools.Method(impact, "DamageBlock");
            impactGridField = impact == null ? null : AccessTools.Field(impact, "_grid");
            Patch(harmony, damageBlock, nameof(ImpactDamagePostfix), "GridImpactDamageReceiverComponent.DamageBlock (crash damage)");
            ImpactHookPatched = damageBlock != null && impactGridField != null;
            // The contact signal the damage comes from: a hit, even when it does no damage (a mod that turns collision
            // damage off, say), so a crash can still be told.
            var onContact = impact == null ? null : AccessTools.Method(impact, "OnContact");
            Patch(harmony, onContact, nameof(ContactPostfix), "GridImpactDamageReceiverComponent.OnContact (hits without damage)");

            // A grid the game finds inside a planet: noted (the plugin removes it), and the game's own assert skipped,
            // since that grid stops the world loading (an assert, or the graphics card hanging a few seconds later).
            var watchdog = AccessTools.TypeByName("Keen.VRage.Voxels.Components.VoxelPlanetWatchdogComponent");
            var assertCenter = watchdog == null ? null : AccessTools.Method(watchdog, "AssertEntityInCenter");
            if (assertCenter == null) PluginLog.Info("Patch target not found: VoxelPlanetWatchdogComponent.AssertEntityInCenter (grids lost inside a planet)");
            else
                try
                {
                    harmony.Patch(assertCenter, prefix: new HarmonyMethod(typeof(Patches), nameof(LostInPlanetPrefix)));
                    PluginLog.Info("Patched VoxelPlanetWatchdogComponent.AssertEntityInCenter (grids lost inside a planet)");
                }
                catch (Exception e) { PluginLog.Error("Failed to patch VoxelPlanetWatchdogComponent.AssertEntityInCenter", e); }

            // The game's own crash: cut (or otherwise remove) the ship you're sitting in, then open the terminal, and
            // the terminal looks for that ship, which is gone. Caught here, so the game carries on.
            var terminalVm = AccessTools.TypeByName("Keen.Game2.Client.UI.TerminalScreen.TerminalScreenViewModel");
            var terminalUpdate = terminalVm == null ? null : AccessTools.Method(terminalVm, "Update");
            PatchFinalizer(harmony, terminalUpdate, nameof(TerminalUpdateFinalizer),
                "TerminalScreenViewModel.Update (terminal on a removed ship)");
            // ...and before it: skipped while the ship it's on is gone, so the error (slow: the game ran at a few
            // frames a second with it caught every frame) doesn't happen at all.
            if (terminalUpdate != null)
                try { harmony.Patch(terminalUpdate, prefix: new HarmonyMethod(typeof(Patches), nameof(TerminalUpdatePrefix))); }
                catch (Exception e) { PluginLog.Error("Couldn't patch TerminalScreenViewModel.Update (prefix)", e); }
            var prodAnalytics = AccessTools.TypeByName("Keen.Game2.Client.RuntimeSystems.Analytics.SessionComponents.ProductionAnalyticsSessionComponent");
            var terminalOpened = prodAnalytics == null ? null : AccessTools.Method(prodAnalytics, "OnTerminalOpened");
            PatchFinalizer(harmony, terminalOpened, nameof(TerminalOpenedFinalizer),
                "ProductionAnalyticsSessionComponent.OnTerminalOpened (terminal on a removed ship)");
            if (terminalOpened != null)
                try { harmony.Patch(terminalOpened, prefix: new HarmonyMethod(typeof(Patches), nameof(TerminalOpenedPrefix))); }
                catch (Exception e) { PluginLog.Error("Couldn't patch OnTerminalOpened (prefix)", e); }

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

        // ---- Grids lost inside a planet (the game's watchdog found them near its centre): their names, for removal.
        internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> LostInPlanet = new System.Collections.Concurrent.ConcurrentQueue<string>();
        internal static volatile bool RemoveLostGrids = true;

        private static bool LostInPlanetPrefix(object[] __args)
        {
            if (!RemoveLostGrids) return true;
            try
            {
                object entity = __args != null && __args.Length > 0 ? __args[0] : null;
                string name = null;
                if (entity != null)
                    foreach (string member in new[] { "DebugName", "Name" })
                    {
                        var prop = entity.GetType().GetProperty(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (prop?.GetValue(entity) is string text && text.Length > 0) { name = text; break; }
                    }
                name ??= entity?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && LostInPlanet.Count < 50) LostInPlanet.Enqueue(name);
            }
            catch { }
            return false;   // (the game's own check would stop here: the grid is dealt with by the plugin instead)
        }

        // ---- Crash damage: where a ship's block took collision damage, and how much (read by the race each frame).
        internal static readonly System.Collections.Concurrent.ConcurrentQueue<(double X, double Y, double Z, float Damage)> ImpactDamage
            = new System.Collections.Concurrent.ConcurrentQueue<(double, double, double, float)>();
        /// <summary>The damage hook has seen collision damage at least once (so it works here).</summary>
        internal static volatile bool ImpactDamageSeen;
        /// <summary>The damage hook is in (collision damage will be seen wherever the game's server runs in this game).</summary>
        internal static bool ImpactHookPatched;
        private static System.Reflection.FieldInfo impactGridField;
        private static int impactLogs;

        // ---- Hits: a ship touched something (the game's contact signal, damage or not), and how hard.
        internal static readonly System.Collections.Concurrent.ConcurrentQueue<(double X, double Y, double Z, double Magnitude, object Grid)> Contacts
            = new System.Collections.Concurrent.ConcurrentQueue<(double, double, double, double, object)>();
        internal static volatile bool ContactSeen;
        private static System.Reflection.FieldInfo contactMagnitudeField;
        private static int contactLogs;

        private static void ContactPostfix(object __instance, object[] __args)
        {
            try
            {
                object info = __args != null && __args.Length > 0 ? __args[0] : null;
                double magnitude = 0;
                if (info != null)
                {
                    contactMagnitudeField ??= info.GetType().GetField("Magnitude");
                    if (contactMagnitudeField?.GetValue(info) is object m) magnitude = Convert.ToDouble(m);
                }
                var grid = impactGridField?.GetValue(__instance) as Keen.VRage.DCS.Components.Component;
                var entity = grid?.Entity;
                if (entity == null || !entity.Data.Has<WorldTransform>()) return;
                var p = Keen.VRage.Core.Game.Data.EntityTransformFunctions.GetWorldTransform(entity.Data).Position;
                ContactSeen = true;
                if (Contacts.Count < 512) Contacts.Enqueue((p.X, p.Y, p.Z, magnitude, entity));
                if (contactLogs < 10) { contactLogs++; PluginLog.Info($"Hit seen: strength {magnitude:0.###} to a ship at ({p.X:0}, {p.Y:0}, {p.Z:0})"); }
            }
            catch (Exception e)
            {
                if (contactLogs < 10) { contactLogs++; PluginLog.Error("Hit hook failed", e); }
            }
        }

        private static void ImpactDamagePostfix(object __instance, object[] __args)
        {
            try
            {
                float damage = __args != null && __args.Length > 2 && __args[2] is float f ? f : 0;
                var grid = impactGridField?.GetValue(__instance) as Keen.VRage.DCS.Components.Component;
                var entity = grid?.Entity;
                if (entity == null || !entity.Data.Has<WorldTransform>()) return;
                var p = Keen.VRage.Core.Game.Data.EntityTransformFunctions.GetWorldTransform(entity.Data).Position;
                ImpactDamageSeen = true;
                if (ImpactDamage.Count < 512) ImpactDamage.Enqueue((p.X, p.Y, p.Z, damage));
                if (impactLogs < 5) { impactLogs++; PluginLog.Info($"Crash damage seen: {damage:0.##} to a ship at ({p.X:0}, {p.Y:0}, {p.Z:0})"); }
            }
            catch (Exception e)
            {
                if (impactLogs < 5) { impactLogs++; PluginLog.Error("Crash damage hook failed", e); }
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
