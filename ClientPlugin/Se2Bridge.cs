using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.Game2.Client.GameSystems.CameraSystems;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Client.WorldObjects.Spectators;
using Keen.VRage.Library.Localization;
using System.Linq;
using Keen.VRage.Core.Game.Systems;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Render;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Physics.Data;
using Keen.VRage.UI.EngineComponents;
using Keen.VRage.UI.Screens;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// The only file that talks to Space Engineers 2. Built from the game's own code:
    ///  - controlled ship lookup copies ControlledEntityDebugScreen.GetControlledEntity (Game2.Client)
    ///  - on-screen text copies AutoTestScenario.DrawMessage / StatsDebugScreen.Draw (IDebugDraw.DebugDraw2D.AddText)
    ///  - the client world comes from WorldSessionComponent instances captured by Patches.cs
    /// </summary>
    internal sealed class Se2Bridge : IGameBridge
    {
        // ---- Worlds captured by the Harmony patch on WorldSessionComponent.Init/InitAsync
        private static readonly List<WeakReference<WorldSessionComponent>> Worlds = new List<WeakReference<WorldSessionComponent>>();

        internal static void OnWorldCreated(WorldSessionComponent world)
        {
            lock (Worlds) Worlds.Add(new WeakReference<WorldSessionComponent>(world));
        }

        internal static void OnWorldDestroyed(WorldSessionComponent world)
        {
            lock (Worlds) Worlds.RemoveAll(w => !w.TryGetTarget(out var x) || ReferenceEquals(x, world));
        }

        // ---- Backup: player controllers captured by the Harmony patch on PlayerControllerComponent.Init
        private static readonly List<WeakReference<PlayerControllerComponent>> Controllers = new List<WeakReference<PlayerControllerComponent>>();

        internal static void OnControllerCreated(PlayerControllerComponent c)
        {
            lock (Controllers)
            {
                Controllers.RemoveAll(w => !w.TryGetTarget(out _));
                Controllers.Add(new WeakReference<PlayerControllerComponent>(c));
            }
        }

        private WorldSessionComponent clientWorld;
        private PlayerControllerComponent controller;
        private string lastState = "";
        private Entity ownGrid; // the ship being flown, so Ctrl+J / Ctrl+K never pick it

        /// <summary>Call once per frame. Finds the client-side world and the local player's controller.</summary>
        public void Refresh()
        {
            clientWorld = null;
            controller = null;
            string route = "none";

            // Route 1: from the loaded worlds (also gives the world name and pause state).
            lock (Worlds)
            {
                Worlds.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (var weak in Worlds)
                {
                    if (!weak.TryGetTarget(out var world)) continue;
                    var session = world.OwnedSession;
                    // Only the client session has ClientPlayersSessionComponent (singleplayer also runs a server session).
                    var players = session?.SessionComponents?.TryGet<ClientPlayersSessionComponent>(default);
                    var local = players?.LocalPlayerController;
                    if (local == null) continue;
                    clientWorld = world;
                    controller = local;
                    route = "world";
                    break;
                }
            }

            // Route 2 (backup): the most recent player controller that is controlling something.
            if (controller == null)
            {
                lock (Controllers)
                {
                    for (int i = Controllers.Count - 1; i >= 0; i--)
                    {
                        if (Controllers[i].TryGetTarget(out var c) && c.ControlledEntities != null && c.ControlledEntities.Count > 0)
                        {
                            controller = c;
                            route = "controller";
                            break;
                        }
                    }
                }
            }

            string state = $"worlds={CountAlive(Worlds)} controllers={CountAlive(Controllers)} route={route} world='{clientWorld?.DisplayName}'";
            if (state != lastState)
            {
                lastState = state;
                PluginLog.Info("State: " + state);
            }

            UpdateClock();
        }

        private static int CountAlive<T>(List<WeakReference<T>> list) where T : class
        {
            lock (list)
            {
                int n = 0;
                foreach (var w in list) if (w.TryGetTarget(out _)) n++;
                return n;
            }
        }

        public bool InWorld => controller != null;

        public string WorldId => clientWorld?.DisplayName ?? "default";

        private string saveFolderFor, saveFolder;
        private double saveFolderAt = double.NegativeInfinity;

        /// <summary>
        /// The world's save folder. First looks for a path to an existing folder in the world/session objects
        /// (properties named like Path / Folder / Directory / Save), then for a folder named after the world under
        /// %AppData%\SpaceEngineers2. Found once per world; retried every 10 s while not found.
        /// </summary>
        public string WorldSaveFolder
        {
            get
            {
                if (clientWorld == null) return null;
                string world = WorldName;
                string key = world ?? "(no name)";
                if (key == saveFolderFor && (saveFolder != null || TimeSeconds - saveFolderAt < 10)) return saveFolder;
                bool firstTry = key != saveFolderFor;
                saveFolderFor = key;
                saveFolderAt = TimeSeconds;
                saveFolder = null;
                try
                {
                    var candidates = new List<string>();
                    var roots = new List<object>();
                    lock (Worlds)
                        foreach (var weak in Worlds)
                            if (weak.TryGetTarget(out var w)) { roots.Add(w); roots.Add(w.OwnedSession); }
                    var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    foreach (var r in roots) CollectPaths(r, 0, candidates, seen);
                    candidates = candidates.Distinct().Where(c => !IsTempFolder(c)).ToList();
                    if (firstTry)
                    {
                        PluginLog.Info($"World save folder search: world name '{world}', {candidates.Count} folder(s) in the game's objects");
                        foreach (var c in candidates) PluginLog.Info("  candidate: " + c);
                        LogSaveTree();
                    }
                    // Where the game keeps saves: %AppData%\SpaceEngineers2\AppData\SaveGames\<world name>.
                    if (world != null) saveFolder = DirectSaveFolder(world);
                    if (world != null && saveFolder == null)
                        saveFolder = candidates.FirstOrDefault(c => string.Equals(Path.GetFileName(c.TrimEnd('\\', '/')), world, StringComparison.OrdinalIgnoreCase));
                    saveFolder = saveFolder
                              ?? candidates.FirstOrDefault(c => c.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0)
                              ?? (world != null ? SearchSaveFolder(world) : null)
                              ?? NewestSaveFolder();
                    if (firstTry || saveFolder != null)
                        PluginLog.Info(saveFolder != null ? $"World save folder: {saveFolder}" : "World save folder not found");
                }
                catch (Exception e) { LogOnce("World save folder search failed: " + e.Message); }
                return saveFolder;
            }
        }

        private static bool IsTempFolder(string path) =>
            path.IndexOf("\\Temp\\", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(".appDataTemp", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string DirectSaveFolder(string world)
        {
            string safe = world;
            foreach (char ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
            foreach (var name in new[] { world, safe })
            {
                string dir = Path.Combine(SpaceEngineers2Folder, "AppData", "SaveGames", name);
                if (Directory.Exists(dir)) return dir;
            }
            return null;
        }

        /// <summary>The world's name: the first loaded world object that has one (the client one may not).</summary>
        public string WorldName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(clientWorld?.DisplayName)) return clientWorld.DisplayName;
                lock (Worlds)
                    foreach (var weak in Worlds)
                        if (weak.TryGetTarget(out var w) && !string.IsNullOrWhiteSpace(w.DisplayName)) return w.DisplayName;
                return null;
            }
        }

        private static string SpaceEngineers2Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2");

        /// <summary>
        /// When the world's name isn't known: the most recently written folder inside a "Saves" folder under
        /// %AppData%\SpaceEngineers2 (the world you just saved). The folder that directly holds the files is used.
        /// </summary>
        private static string NewestSaveFolder()
        {
            string root = SpaceEngineers2Folder;
            if (!Directory.Exists(root)) return null;
            var savesDirs = new List<string>();
            void FindSaves(string dir, int depth)
            {
                if (depth > 3) return;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { return; }
                foreach (var sub in subs)
                {
                    string n = Path.GetFileName(sub);
                    if (n.StartsWith(".") || string.Equals(n, "Temp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0) savesDirs.Add(sub);
                    else FindSaves(sub, depth + 1);
                }
            }
            FindSaves(root, 0);
            string best = null; DateTime bestTime = DateTime.MinValue;
            void Walk(string dir, int depth)
            {
                if (depth > 3) return;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { return; }
                foreach (var sub in subs)
                {
                    string bn = Path.GetFileName(sub);
                    if (bn.StartsWith(".") || bn.StartsWith("Backup", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        // A world folder: has files of its own. Its newest file says when it was last saved.
                        var files = Directory.GetFiles(sub);
                        if (files.Length > 0)
                        {
                            DateTime t = files.Max(f => File.GetLastWriteTimeUtc(f));
                            if (t > bestTime) { bestTime = t; best = sub; }
                        }
                    }
                    catch { }
                    Walk(sub, depth + 1);
                }
            }
            foreach (var d in savesDirs) Walk(d, 0);
            return best;
        }

        /// <summary>Logs the folders under %AppData%\SpaceEngineers2 (3 levels, names only) once, to see how saves are laid out.</summary>
        private static bool savedTreeLogged;
        private static void LogSaveTree()
        {
            if (savedTreeLogged) return;
            savedTreeLogged = true;
            int lines = 0;
            void Walk(string dir, int depth, string indent)
            {
                if (depth > 3 || lines > 80) return;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { return; }
                foreach (var sub in subs)
                {
                    if (lines++ > 80) return;
                    int files = 0;
                    try { files = Directory.GetFiles(sub).Length; } catch { }
                    PluginLog.Info($"  {indent}{Path.GetFileName(sub)}  ({files} files)");
                    Walk(sub, depth + 1, indent + "  ");
                }
            }
            PluginLog.Info("Folders in " + SpaceEngineers2Folder + ":");
            Walk(SpaceEngineers2Folder, 0, "");
        }

        private static readonly string[] PathWords = { "path", "folder", "directory", "dir", "save", "location" };

        /// <summary>Strings that are paths to existing folders (or files, giving their folder) in an object and the objects it holds, two levels deep.</summary>
        private static void CollectPaths(object o, int depth, List<string> found, HashSet<object> seen)
        {
            if (o == null || depth > 2 || !seen.Add(o)) return;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = o.GetType();
            if (type.IsPrimitive || type.IsEnum || o is string) return;
            foreach (var member in type.GetMembers(all))
            {
                string name = member.Name.ToLowerInvariant();
                if (!PathWords.Any(w => name.Contains(w))) continue;
                object value = null;
                try
                {
                    if (member is FieldInfo f) value = f.GetValue(o);
                    else if (member is PropertyInfo p && p.GetIndexParameters().Length == 0 && p.CanRead) value = p.GetValue(o);
                    else continue;
                }
                catch { continue; }
                if (value is string sv)
                {
                    if (sv.Length < 4 || !Path.IsPathRooted(sv)) continue;
                    if (Directory.Exists(sv)) found.Add(sv);
                    else if (File.Exists(sv)) found.Add(Path.GetDirectoryName(sv));
                }
                else if (value != null && !(value is System.Collections.IEnumerable) && !value.GetType().IsValueType)
                    CollectPaths(value, depth + 1, found, seen);
            }
        }

        /// <summary>A folder named after the world under %AppData%\SpaceEngineers2 (four levels deep at most), newest first.</summary>
        private static string SearchSaveFolder(string world)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2");
            if (!Directory.Exists(root)) return null;
            string safe = world;
            foreach (char ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
            var matches = new List<string>();
            void Walk(string dir, int depth)
            {
                if (depth > 4) return;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { return; }
                foreach (var sub in subs)
                {
                    string n = Path.GetFileName(sub);
                    if (n.StartsWith(".") || string.Equals(n, "Temp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(n, world, StringComparison.OrdinalIgnoreCase) || string.Equals(n, safe, StringComparison.OrdinalIgnoreCase))
                        matches.Add(sub);
                    if (!string.Equals(n, "RaceDisplay", StringComparison.OrdinalIgnoreCase)) Walk(sub, depth + 1);
                }
            }
            Walk(root, 0);
            return matches.OrderByDescending(d => { try { return Directory.GetLastWriteTimeUtc(d); } catch { return DateTime.MinValue; } }).FirstOrDefault();
        }

        public bool TryGetControlledPose(out Vec3D position, out Vec3D forward, out Vec3D up)
        {
            position = forward = up = Vec3D.Zero;
            ownGrid = null;

            Entity entity = controller?.GetTopControllable()?.Entity;
            if (entity == null) { LogOnce("No controlled entity (GetTopControllable returned nothing)"); return false; }

            // In a seat: track the whole ship (grid), the same way the game's Controlled Entity debug screen does.
            var seat = entity.TryGet<SeatComponent>(default);
            if (seat != null)
            {
                var grid = seat.Entity?.TryGet<CubeBlockComponent>(default)?.Grid?.Entity;
                if (grid != null) { entity = grid; ownGrid = grid; }
            }

            if (!entity.Data.Has<WorldTransform>()) { LogOnce("Controlled entity has no WorldTransform: " + entity.DebugName); return false; }

            WorldTransform t = EntityTransformFunctions.GetWorldTransform(entity.Data);
            position = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
            Vector3 f = t.Orientation.GetForward();
            Vector3 u = t.Orientation.GetUp();
            forward = new Vec3D(f.X, f.Y, f.Z);
            up = new Vec3D(u.X, u.Y, u.Z);
            return true;
        }

        public bool TryGetShipVelocity(out Vec3D velocity)
        {
            velocity = Vec3D.Zero;
            if (ownGrid == null) return false; // on foot: crashes don't count
            if (!ownGrid.Data.TryGet(out RigidBodyData rb)) { LogOnce("Ship has no RigidBodyData: crash detection off"); return false; }
            Vector3 v = rb.LinearVelocity;
            velocity = new Vec3D(v.X, v.Y, v.Z);
            return true;
        }

        private static Vec3D ToVec(Vector3D v) => new Vec3D(v.X, v.Y, v.Z);

        /// <summary>Whole-grid box (grid's own orientation) for one grid. False if it has no size.</summary>
        private static bool TryGridBox(Entity e, Vec3D from, out GridBox box, out WorldTransform t)
        {
            box = default; t = default;
            if (e == null || !e.Data.Has<WorldTransform>()) return false;
            if (!e.Data.TryGet(out BoundingBoxData bb)) return false;
            Vector3 size = bb.BoundingBox.Size;
            if (!(size.X > 0 && size.Y > 0 && size.Z > 0)) return false;
            Vector3 c = bb.BoundingBox.Center;
            t = EntityTransformFunctions.GetWorldTransform(e.Data);
            Vec3D ax = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
            Vec3D ay = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
            Vec3D az = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
            Vec3D center = ToVec(t.Position) + ax * c.X + ay * c.Y + az * c.Z;
            double halfDiagonal = 0.5 * Math.Sqrt(size.X * size.X + size.Y * size.Y + size.Z * size.Z);
            box = new GridBox
            {
                Name = e.DebugName ?? "grid",
                Center = center, Forward = az, Up = ay,
                Width = size.X, Height = size.Y, Depth = size.Z,
                Distance = Math.Max(0, (center - from).Length - halfDiagonal),
            };
            return true;
        }

        private Session CurrentSession() =>
            clientWorld?.OwnedSession ?? (controller != null ? GameEntityExtensions.GetSession(controller) : null);

        private static string Raw(LocalizableText text)
        {
            try { return text.RawText; } catch { return null; }
        }

        public List<NamedGrid> FindNamedGrids(Func<string, bool> wanted, bool fitToOpening)
        {
            var result = new List<NamedGrid>();
            var session = CurrentSession();
            if (session == null) { PluginLog.Info("Auto-build: no session"); return result; }

            // Names for each grid: the grid's own name, plus the names of antennas on it
            // (the antenna name is what shows on its HUD marker).
            var names = new Dictionary<Entity, List<string>>();
            void AddName(Entity grid, string n)
            {
                if (grid == null || string.IsNullOrWhiteSpace(n)) return;
                if (!names.TryGetValue(grid, out var list)) names[grid] = list = new List<string>();
                if (!list.Contains(n.Trim())) list.Add(n.Trim());
            }

            foreach (Entity g in session.GetEntitiesOfType<CubeGridComponent>())
            {
                var grid = g?.TryGet<CubeGridComponent>(default);
                if (grid != null) AddName(g, Raw(grid.DisplayName));
            }
            int antennas = 0;
            foreach (Entity a in session.GetEntitiesOfType<AntennaComponent>())
            {
                if (a == null) continue;
                antennas++;
                Entity root = HierarchyComponentExtensions.GetRoot(a);
                var fb = a.TryGet<FunctionalBlockComponent>(default);
                if (fb != null) AddName(root, Raw(fb.DisplayName));
                var ant = a.TryGet<AntennaComponent>(default);
                if (ant != null && ant.BroadcastCustomMessage) AddName(root, Raw(ant.CustomMessage));
            }

            Vec3D from = Vec3D.Zero;
            foreach (var kv in names)
            {
                if (ReferenceEquals(kv.Key, ownGrid)) continue;
                var matching = kv.Value.Where(n => wanted(n)).ToList();
                if (matching.Count == 0) continue;
                if (!TryGridBox(kv.Key, from, out GridBox box, out WorldTransform t)) continue;
                if (fitToOpening) TryFitToOpening(kv.Key, t, ref box);
                var ng = new NamedGrid { Box = box };
                ng.Names.AddRange(matching);
                result.Add(ng);
            }
            PluginLog.Info($"Auto-build: {names.Count} named grids ({antennas} antennas), {result.Count} with course names: "
                + string.Join(", ", result.Select(r => string.Join("/", r.Names))));
            return result;
        }

        // ---- Spectator camera (best-lap replay)

        private IControllable spectatorReturn; // what the player controlled before the replay (their cockpit)

        public bool IsPaused
        {
            get
            {
                try { return clientWorld?.Pause?.IsPaused ?? false; }
                catch { return false; }
            }
        }

        private static WorldTransform View(Vec3D position, Vec3D forward, Vec3D up) =>
            new WorldTransform(new Vector3D(position.X, position.Y, position.Z),
                Quaternion.CreateFromForwardUp(new Vector3((float)forward.X, (float)forward.Y, (float)forward.Z),
                                               new Vector3((float)up.X, (float)up.Y, (float)up.Z)));

        public bool StartSpectator(Vec3D position, Vec3D forward, Vec3D up)
        {
            try
            {
                var session = CurrentSession();
                var modes = session?.SessionComponents.TryGet<SpectatingModesSessionComponent>(default);
                if (modes == null || controller == null) { PluginLog.Info("Replay: no spectator system"); return false; }
                spectatorReturn = controller.GetTopControllable();
                modes.SwitchToSpectator(View(position, forward, up));
                PluginLog.Info($"Replay: switching to spectator (was controlling {spectatorReturn?.Entity?.DebugName})");
                return true;
            }
            catch (Exception e)
            {
                PluginLog.Error("Replay: couldn't switch to the spectator camera", e);
                return false;
            }
        }

        private bool triedStopMotion;
        private System.Reflection.MethodInfo stopMotion;

        // Clears any drift the spectator camera has. The method name differs between game versions,
        // so it is looked up once; if none is found the camera is simply moved each frame.
        private void StopSpectatorMotion(object spectator)
        {
            if (!triedStopMotion)
            {
                triedStopMotion = true;
                foreach (var name in new[] { "ResetMotion", "ResetVelocity", "StopMotion", "Stop", "ResetMovement" })
                {
                    stopMotion = spectator.GetType().GetMethod(name,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (stopMotion != null) break;
                }
                PluginLog.Info("Replay: spectator stop method = " + (stopMotion?.Name ?? "none"));
            }
            try { stopMotion?.Invoke(spectator, null); } catch { }
        }

        public bool SetSpectatorView(Vec3D position, Vec3D forward, Vec3D up) => SetSpectatorView(position, forward, up, true);

        public bool InSpectator
        {
            get
            {
                try { return controller?.GetTopControllable()?.Entity?.TryGet<SpectatorComponent>(default) != null; }
                catch { return false; }
            }
        }

        public bool TryGetSpectatorView(out Vec3D position, out Vec3D forward, out Vec3D up)
        {
            position = forward = up = Vec3D.Zero;
            try
            {
                var spectator = controller?.GetTopControllable()?.Entity?.TryGet<SpectatorComponent>(default);
                if (spectator == null || !spectator.Entity.Data.Has<WorldTransform>()) return false;
                var t = EntityTransformFunctions.GetWorldTransform(spectator.Entity.Data);
                position = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
                Vector3 f = t.Orientation.GetForward(), u = t.Orientation.GetUp();
                forward = new Vec3D(f.X, f.Y, f.Z);
                up = new Vec3D(u.X, u.Y, u.Z);
                return true;
            }
            catch { return false; }
        }

        public bool SetSpectatorView(Vec3D position, Vec3D forward, Vec3D up, bool stopMotion)
        {
            try
            {
                var spectator = controller?.GetTopControllable()?.Entity?.TryGet<SpectatorComponent>(default);
                if (spectator == null) return false;
                if (cinematicOn && CinematicLocked(spectator)) return true;   // player locked the cinematic camera onto something: let the game drive
                var view = View(position, forward, up);
                if (stopMotion) StopSpectatorMotion(spectator);
                EntityTransformFunctions.SetWorldTransform(spectator.Entity.Data, in view);
                if (cinematicOn) RefreshCinematicOffset(spectator);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Replay: couldn't move the spectator camera: " + e.Message);
                return false;
            }
        }

        // ---- Cinematic spectator (added to the game in "Hostile Encounters"); everything by name so older games still work.
        private const System.Reflection.BindingFlags Any =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        private bool cinematicOn;              // the replay switched cinematic mode on
        private bool? cinematicBefore;         // what the player had before
        private System.Reflection.PropertyInfo cinematicProp, lockActiveProp;
        private System.Reflection.MethodInfo applyOffset;
        private bool cinematicLookedUp;

        private void LookUpCinematic(object spectator)
        {
            if (cinematicLookedUp) return;
            cinematicLookedUp = true;
            var t = spectator.GetType();
            cinematicProp = t.GetProperty("CinematicModeEnabled", Any);
            lockActiveProp = t.GetProperty("IsLockActive", Any);
            applyOffset = t.GetMethod("ApplyCameraRotationOffset", Any, null, Type.EmptyTypes, null);
            PluginLog.Info($"Replay: cinematic spectator {(cinematicProp != null && cinematicProp.CanWrite ? "found" : "not in this game version")}"
                + $", lock {(lockActiveProp != null ? "yes" : "no")}, rotate offset {(applyOffset != null ? "yes" : "no")}");
        }

        public bool SetSpectatorCinematic(bool on)
        {
            try
            {
                var spectator = controller?.GetTopControllable()?.Entity?.TryGet<SpectatorComponent>(default);
                if (spectator == null) { cinematicOn = false; return false; }
                LookUpCinematic(spectator);
                if (cinematicProp == null || !cinematicProp.CanWrite) { cinematicOn = false; return false; }
                if (on)
                {
                    if (!cinematicOn) cinematicBefore = (bool)cinematicProp.GetValue(spectator);
                    if (!(bool)cinematicProp.GetValue(spectator)) cinematicProp.SetValue(spectator, true);
                    cinematicOn = true;
                }
                else
                {
                    if (cinematicOn && cinematicBefore.HasValue && (bool)cinematicProp.GetValue(spectator) != cinematicBefore.Value)
                        cinematicProp.SetValue(spectator, cinematicBefore.Value);
                    cinematicOn = false;
                    cinematicBefore = null;
                }
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Replay: couldn't switch the cinematic spectator: " + e.Message);
                cinematicOn = false;
                return false;
            }
        }

        private bool CinematicLocked(object spectator)
        {
            try { LookUpCinematic(spectator); return lockActiveProp != null && (bool)lockActiveProp.GetValue(spectator); }
            catch { return false; }
        }

        // The cinematic camera's look-around offset is a fixed render transform the game only refreshes while the
        // spectator moves by itself; after we move it, bring the offset along so the view doesn't freeze.
        private void RefreshCinematicOffset(object spectator)
        {
            try { applyOffset?.Invoke(spectator, null); } catch { applyOffset = null; }
        }

        public void EndSpectator()
        {
            if (cinematicOn) SetSpectatorCinematic(false);
            try
            {
                if (controller == null) return;
                bool spectating = controller.GetTopControllable()?.Entity?.TryGet<SpectatorComponent>(default) != null;
                if (spectating && spectatorReturn != null)
                {
                    // Same as the game does when leaving spectator: let go of everything, take the cockpit back.
                    controller.RequestUnpossessionAll();
                    controller.RequestPossession(spectatorReturn);
                    PluginLog.Info($"Replay: back to {spectatorReturn.Entity?.DebugName}");
                }
            }
            catch (Exception e) { PluginLog.Error("Replay: couldn't leave the spectator camera", e); }
            spectatorReturn = null;
        }

        private Entity centreGrid;
        private Vec3D centreLocal;
        private double centreAt = double.NegativeInfinity;

        public bool TryGetShipCentre(out Vec3D centre)
        {
            centre = Vec3D.Zero;
            try
            {
                if (ownGrid == null || !ownGrid.Data.Has<WorldTransform>()) return false;
                // The centre in grid coordinates only changes when blocks do: worked out again every 2 seconds.
                if (!ReferenceEquals(ownGrid, centreGrid) || TimeSeconds - centreAt > 2 || TimeSeconds < centreAt)
                {
                    var storage = ownGrid.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                    if (storage == null) return false;
                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                    int count = 0;
                    foreach (var block in storage.GetAllCubeBlocks())
                    {
                        if (block == null) continue;
                        BoundingBox b = CubeGridCoords.GridToLocal(block.AABB);
                        minX = Math.Min(minX, Math.Min(b.Min.X, b.Max.X)); maxX = Math.Max(maxX, Math.Max(b.Min.X, b.Max.X));
                        minY = Math.Min(minY, Math.Min(b.Min.Y, b.Max.Y)); maxY = Math.Max(maxY, Math.Max(b.Min.Y, b.Max.Y));
                        minZ = Math.Min(minZ, Math.Min(b.Min.Z, b.Max.Z)); maxZ = Math.Max(maxZ, Math.Max(b.Min.Z, b.Max.Z));
                        count++;
                    }
                    if (count == 0) return false;
                    centreLocal = new Vec3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
                    if (!ReferenceEquals(ownGrid, centreGrid))
                        PluginLog.Info($"Ship centre is {centreLocal.Length:F1} m from the grid's pivot ({count} blocks)");
                    centreGrid = ownGrid;
                    centreAt = TimeSeconds;
                }
                if (!TryGetShipAxes(out Vec3D ax, out Vec3D ay, out Vec3D az)) return false;
                WorldTransform t = EntityTransformFunctions.GetWorldTransform(ownGrid.Data);
                var pivot = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
                centre = pivot + ax * centreLocal.X + ay * centreLocal.Y + az * centreLocal.Z;
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Ship centre failed (gates use the grid's pivot): " + e.Message);
                return false;
            }
        }

        public bool TryGetLookRay(out Vec3D origin, out Vec3D direction, out Vec3D right, out Vec3D up)
        {
            origin = direction = right = up = Vec3D.Zero;
            try
            {
                var cam = Camera();
                if (cam == null || cam.Entity == null || !cam.Entity.Data.Has<WorldTransform>()) return false;
                WorldTransform t = EntityTransformFunctions.GetWorldTransform(cam.Entity.Data);
                origin = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
                // Make sure we know which way the camera really faces (see TryProjectToScreen).
                TryProjectToScreen(origin + ToVec3(t.Orientation.GetForward()) * 100, out _, out _, out _);
                Vec3D f = ToVec3(t.Orientation.GetForward());
                up = ToVec3(t.Orientation.GetUp()).Normalized();
                direction = (flipForward ? f * -1 : f).Normalized();
                right = Vec3D.Cross(direction, up).Normalized();
                return direction.Length > 0.5;
            }
            catch (Exception e) { LogOnce("Look ray failed: " + e.Message); return false; }
        }

        private static Vec3D ToVec3(Vector3 v) => new Vec3D(v.X, v.Y, v.Z);

        public bool TryGetLookedAtGrid(Vec3D origin, Vec3D direction, double maxDistance, bool fitToOpening, out GridBox box)
        {
            box = default;
            try
            {
                Session session = clientWorld?.OwnedSession ?? (controller != null ? GameEntityExtensions.GetSession(controller) : null);
                if (session == null) return false;
                Entity bestEntity = null;
                WorldTransform bestT = default;
                double bestAlong = double.MaxValue;
                Vec3D end = origin + direction * maxDistance;
                foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
                {
                    if (e == null || ReferenceEquals(e, ownGrid)) continue;
                    if (!TryGridBox(e, origin, out GridBox gb, out WorldTransform t)) continue;
                    var test = new TimingGate(gb.Center, gb.Forward, gb.Up, gb.Width, gb.Height, gb.Depth);
                    if (!test.SegmentIntersects(origin, end)) continue;
                    // Nearest along the ray: how far to the box's near side (roughly).
                    double along = Vec3D.Dot(gb.Center - origin, direction) - 0.5 * Math.Min(gb.Width, Math.Min(gb.Height, gb.Depth));
                    if (along < 0 && test.Contains(origin)) along = 0;
                    if (along >= bestAlong) continue;
                    bestAlong = along; bestEntity = e; bestT = t; box = gb;
                }
                if (bestEntity == null) return false;
                box.Distance = Math.Max(0, bestAlong);
                if (fitToOpening) TryFitToOpening(bestEntity, bestT, ref box);
                return true;
            }
            catch (Exception e) { LogOnce("Looked-at grid failed: " + e.Message); return false; }
        }

        public bool TryGetLookedAtBlock(Vec3D origin, Vec3D direction, double maxDistance, out BlockPick block)
        {
            block = null;
            try
            {
                Session session = CurrentSession();
                if (session == null) return false;
                Vec3D end = origin + direction * maxDistance;
                double best = maxDistance;
                foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
                {
                    if (e == null || ReferenceEquals(e, ownGrid)) continue;
                    if (!TryGridBox(e, origin, out GridBox gb, out WorldTransform t)) continue;
                    if (gb.Distance > best) continue;   // a nearer block has been found already
                    var test = new TimingGate(gb.Center, gb.Forward, gb.Up, gb.Width, gb.Height, gb.Depth);
                    if (!test.SegmentIntersects(origin, end)) continue;
                    var storage = e.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                    if (storage == null) continue;

                    // The ray in the grid's own frame, then tested against each block's box.
                    Vec3D ax = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                    Vec3D ay = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                    Vec3D az = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
                    Vec3D o = origin - ToVec(t.Position);
                    double[] ro = { Vec3D.Dot(o, ax), Vec3D.Dot(o, ay), Vec3D.Dot(o, az) };
                    double[] rd = { Vec3D.Dot(direction, ax), Vec3D.Dot(direction, ay), Vec3D.Dot(direction, az) };
                    foreach (var b in storage.GetAllCubeBlocks())
                    {
                        if (b == null) continue;
                        BoundingBoxI cells = b.AABB;
                        BoundingBox local = CubeGridCoords.GridToLocal(cells);
                        double[] lo = { Math.Min(local.Min.X, local.Max.X), Math.Min(local.Min.Y, local.Max.Y), Math.Min(local.Min.Z, local.Max.Z) };
                        double[] hi = { Math.Max(local.Min.X, local.Max.X), Math.Max(local.Min.Y, local.Max.Y), Math.Max(local.Min.Z, local.Max.Z) };
                        double hit = RayHitsBox(ro, rd, lo, hi);
                        if (hit < 0 || hit >= best) continue;
                        best = hit;
                        block = new BlockPick
                        {
                            Grid = e, GridName = gb.Name, Distance = hit,
                            Min = new[] { cells.Min.X, cells.Min.Y, cells.Min.Z },
                            Max = new[] { cells.Max.X, cells.Max.Y, cells.Max.Z },
                        };
                    }
                }
                return block != null;
            }
            catch (Exception e) { LogOnce("Looked-at block failed: " + e.Message); return false; }
        }

        /// <summary>Where a ray first enters a box (distance along it, 0 if it starts inside), or -1 if it misses.</summary>
        private static double RayHitsBox(double[] o, double[] d, double[] lo, double[] hi)
        {
            double enter = 0, exit = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-12)
                {
                    if (o[i] < lo[i] || o[i] > hi[i]) return -1;
                    continue;
                }
                double a = (lo[i] - o[i]) / d[i], b = (hi[i] - o[i]) / d[i];
                if (a > b) (a, b) = (b, a);
                enter = Math.Max(enter, a);
                exit = Math.Min(exit, b);
                if (enter > exit) return -1;
            }
            return enter;
        }

        public bool TryGetBlockSpan(BlockPick a, BlockPick b, out GridBox box)
        {
            box = default;
            try
            {
                if (a == null || b == null || !ReferenceEquals(a.Grid, b.Grid) || !(a.Grid is Entity e) || !e.Data.Has<WorldTransform>()) return false;
                WorldTransform t = EntityTransformFunctions.GetWorldTransform(e.Data);
                var cells = new BoundingBoxI(
                    new Vector3I(Math.Min(a.Min[0], b.Min[0]), Math.Min(a.Min[1], b.Min[1]), Math.Min(a.Min[2], b.Min[2])),
                    new Vector3I(Math.Max(a.Max[0], b.Max[0]), Math.Max(a.Max[1], b.Max[1]), Math.Max(a.Max[2], b.Max[2])));
                BoundingBox local = CubeGridCoords.GridToLocal(cells);
                double lx = Math.Min(local.Min.X, local.Max.X), hx = Math.Max(local.Min.X, local.Max.X);
                double ly = Math.Min(local.Min.Y, local.Max.Y), hy = Math.Max(local.Min.Y, local.Max.Y);
                double lz = Math.Min(local.Min.Z, local.Max.Z), hz = Math.Max(local.Min.Z, local.Max.Z);
                Vec3D ax = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                Vec3D ay = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                Vec3D az = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
                double w = hx - lx, h = hy - ly, d = hz - lz;
                box = new GridBox
                {
                    Name = a.GridName,
                    Center = ToVec(t.Position) + ax * ((lx + hx) / 2) + ay * ((ly + hy) / 2) + az * ((lz + hz) / 2),
                    Forward = az, Up = ay,
                    Width = w, Height = h, Depth = d,
                    IsOpening = true,
                    ThroughAxis = w <= h && w <= d ? 0 : h <= d ? 1 : 2,
                };
                return true;
            }
            catch (Exception ex) { LogOnce("Block span failed: " + ex.Message); return false; }
        }

        public bool TryGetShipAxes(out Vec3D x, out Vec3D y, out Vec3D z)
        {
            x = y = z = Vec3D.Zero;
            try
            {
                if (ownGrid == null || !ownGrid.Data.Has<WorldTransform>()) return false;
                WorldTransform t = EntityTransformFunctions.GetWorldTransform(ownGrid.Data);
                x = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                y = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                z = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
                return true;
            }
            catch { return false; }
        }

        public string ShipName
        {
            get
            {
                try
                {
                    var grid = ownGrid?.TryGet<CubeGridComponent>(default);
                    if (grid == null) return null;
                    string name = Raw(grid.DisplayName);
                    return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                }
                catch (Exception e) { LogOnce("Ship name failed: " + e.Message); return null; }
            }
        }

        public List<double[]> GetShipShape(int maxBoxes)
        {
            var boxes = new List<double[]>();
            try
            {
                var storage = ownGrid?.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                if (storage == null) return boxes;
                foreach (var block in storage.GetAllCubeBlocks())
                {
                    if (block == null) continue;
                    BoundingBox local = CubeGridCoords.GridToLocal(block.AABB);
                    boxes.Add(new[]
                    {
                        Math.Round(Math.Min(local.Min.X, local.Max.X), 2), Math.Round(Math.Min(local.Min.Y, local.Max.Y), 2), Math.Round(Math.Min(local.Min.Z, local.Max.Z), 2),
                        Math.Round(Math.Max(local.Min.X, local.Max.X), 2), Math.Round(Math.Max(local.Min.Y, local.Max.Y), 2), Math.Round(Math.Max(local.Min.Z, local.Max.Z), 2),
                    });
                }
                // Biggest blocks first, so a big ship still looks like itself when it's cut down to maxBoxes.
                boxes.Sort((a, b) => Volume(b).CompareTo(Volume(a)));
                if (boxes.Count > maxBoxes) boxes.RemoveRange(maxBoxes, boxes.Count - maxBoxes);
            }
            catch (Exception e) { LogOnce("Ship shape failed: " + e.Message); }
            return boxes;
        }

        private static double Volume(double[] b) => (b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2]);

        /// <summary>Names a block goes by (entity name, block type, terminal name), for finding antennas and for the log.</summary>
        private static string DescribeBlock(Entity e)
        {
            var fb = e.TryGet<FunctionalBlockComponent>(default);
            string def = null;
            try { def = fb?.Definition?.ToString(); } catch { /* ignore */ }
            return $"{e.DebugName} | {def} | {(fb != null ? Raw(fb.DisplayName) : null)}";
        }

        /// <summary>
        /// Turns antennas on or off with their power switch, the same way the game's "use" interaction does it from
        /// the client. Antenna blocks are found on every grid's block list (by their antenna component, or "antenna"
        /// in their name or block type), because the antenna part itself may only exist on the server.
        /// </summary>
        public int SetAntennasEnabled(bool enabled, out int total)
        {
            total = 0;
            int found = 0;
            int changed = 0;
            var session = CurrentSession();
            if (session == null) { PluginLog.Info("Antennas: no session"); return 0; }

            var done = new HashSet<PowerableBlockComponent>();
            var samples = new List<string>();
            int blocks = 0, powered = 0;

            void Switch(Entity e, PowerableBlockComponent power)
            {
                if (power == null || !done.Add(power)) return;
                found++;
                try
                {
                    if (power.Enabled == enabled) return;
                    power.Enabled = enabled;
                    changed++;
                }
                catch (Exception ex) { PluginLog.Error($"Antenna switch failed on {e.DebugName}", ex); }
            }

            foreach (Entity g in session.GetEntitiesOfType<CubeGridComponent>())
            {
                var storage = g?.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                if (storage == null) continue;
                foreach (var block in storage.GetAllCubeBlocks())
                {
                    var e = block?.Entity;
                    if (e == null) continue;
                    blocks++;
                    var power = e.TryGet<PowerableBlockComponent>(default);
                    if (power == null) continue;
                    powered++;
                    string what = DescribeBlock(e);
                    bool isAntenna = e.TryGet<AntennaComponent>(default) != null
                                     || what.IndexOf("antenna", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (samples.Count < 15 && (isAntenna || samples.Count < 8)) samples.Add(what + (isAntenna ? "  [ANTENNA]" : ""));
                    if (isAntenna) Switch(e, power);
                }
            }

            // Antenna entities the session knows about directly (in case they aren't in a grid's block list).
            foreach (Entity a in session.GetEntitiesOfType<AntennaComponent>())
            {
                if (a == null) continue;
                var power = a.TryGet<PowerableBlockComponent>(default)
                            ?? HierarchyComponentExtensions.GetRoot(a)?.TryGet<PowerableBlockComponent>(default);
                if (power == null) { if (samples.Count < 20) samples.Add($"antenna entity {a.DebugName} has no power switch"); continue; }
                Switch(a, power);
            }

            total = found;
            PluginLog.Info($"Antennas {(enabled ? "on" : "off")}: {changed} changed of {total} antenna blocks "
                + $"({blocks} blocks, {powered} with a power switch). Seen: {string.Join("; ", samples)}");
            return changed;
        }

        /// <summary>
        /// Replaces the whole-grid box with the grid's opening (the hole in a ring), found from its blocks.
        /// Leaves the box unchanged if the grid has no enclosed hole.
        /// </summary>
        private void TryFitToOpening(Entity gridEntity, WorldTransform t, ref GridBox box)
        {
            try
            {
                var storage = gridEntity.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                if (storage == null) { PluginLog.Info("Opening: grid has no block storage"); return; }

                // One cell box per block (Min inclusive, Max exclusive), so even huge grids are quick.
                var boxes = new List<OpeningFinder.CellBox>();
                foreach (var block in storage.GetAllCubeBlocks())
                {
                    if (block == null) continue;
                    BoundingBoxI bb = block.AABB;
                    boxes.Add(new OpeningFinder.CellBox(
                        new[] { bb.Min.X, bb.Min.Y, bb.Min.Z },
                        new[] { bb.Max.X, bb.Max.Y, bb.Max.Z }));
                }
                if (boxes.Count == 0) { PluginLog.Info($"Opening: '{box.Name}' has no blocks listed, using whole grid"); return; }

                var r = OpeningFinder.Find(boxes);
                if (r == null) { PluginLog.Info($"Opening: '{box.Name}' ({boxes.Count} blocks) encloses no hole, using whole grid"); return; }

                // Back to grid-local metres with the game's own conversion.
                var cells = new BoundingBoxI(new Vector3I(r.Min[0], r.Min[1], r.Min[2]), new Vector3I(r.Max[0], r.Max[1], r.Max[2]));
                BoundingBox local = CubeGridCoords.GridToLocal(cells);
                double lx = Math.Min(local.Min.X, local.Max.X), hx = Math.Max(local.Min.X, local.Max.X);
                double ly = Math.Min(local.Min.Y, local.Max.Y), hy = Math.Max(local.Min.Y, local.Max.Y);
                double lz = Math.Min(local.Min.Z, local.Max.Z), hz = Math.Max(local.Min.Z, local.Max.Z);

                Vec3D ax = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                Vec3D ay = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                Vec3D az = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));

                box.Center = ToVec(t.Position) + ax * ((lx + hx) / 2) + ay * ((ly + hy) / 2) + az * ((lz + hz) / 2);
                box.Forward = az;
                box.Up = ay;
                box.Width = hx - lx;
                box.Height = hy - ly;
                box.Depth = hz - lz;
                box.IsOpening = true;
                box.ThroughAxis = r.ThinAxis;
                PluginLog.Info($"Opening: '{box.Name}' ({boxes.Count} blocks, map {r.MapWidth}x{r.MapHeight}, {r.CellsPerPixel} cells/pixel) hole {box.Width:F1} x {box.Height:F1} x {box.Depth:F1} m, through axis {"XYZ"[r.ThinAxis]}");
            }
            catch (Exception e)
            {
                PluginLog.Error("Opening search failed, using whole grid", e);
            }
        }

        private readonly HashSet<string> logged = new HashSet<string>();

        private void LogOnce(string message)
        {
            if (logged.Add(message)) PluginLog.Info(message);
        }

        // ---- Drawing (game debug-draw service; the same one the game's own debug overlays use)

        // Zero = draw for this frame only. If the text flickers, try TimeSpan.FromMilliseconds(20).
        private static readonly TimeSpan DrawTimeout = TimeSpan.Zero;

        private static IDebugDraw DebugDraw => EngineComponent.FromTag<IDebugDraw>();

        // The shared debug builders only accept shapes while the game's "debug draw" setting is on
        // (MeshBuilder/MeshBuilder2D check a private _enabled flag in their Add methods; Commit and the
        // renderer don't). We switch the flag on just for our own calls and put it straight back, so the
        // HUD and gate always show while the game's other debug visuals stay hidden.
        // MeshBuilder2D draws text itself but sends lines/triangles to an inner MeshBuilder (_builder),
        // which has its own flag.
        private static readonly FieldInfo Enabled2D = AccessTools.Field(typeof(MeshBuilder2D), "_enabled");
        private static readonly FieldInfo Enabled3D = AccessTools.Field(typeof(MeshBuilder), "_enabled");
        private static readonly FieldInfo Inner2DBuilder = AccessTools.Field(typeof(MeshBuilder2D), "_builder");

        // ---- Debug draw switched on for course edit mode (and put back afterwards)
        private MemberInfo globalDebugMember;
        private bool searchedGlobalDebug;
        private bool? debugBefore;
        private readonly List<(object Builder, FieldInfo Flag, bool Was)> buildersBefore = new List<(object, FieldInfo, bool)>();

        private MemberInfo GlobalDebugMember()
        {
            if (searchedGlobalDebug) return globalDebugMember;
            searchedGlobalDebug = true;
            var all = GameInternals.DebugDrawSwitches();
            foreach (var m in all) PluginLog.Info($"Debug draw switch candidate: {m.DeclaringType?.FullName}.{m.Name}");
            globalDebugMember = all.FirstOrDefault(m => m.Name == "EnabledDebugDraw")
                             ?? all.FirstOrDefault(m => m.Name.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) >= 0)
                             ?? all.FirstOrDefault();
            if (globalDebugMember != null) PluginLog.Info($"Debug draw switch: {globalDebugMember.DeclaringType?.FullName}.{globalDebugMember.Name}");
            else PluginLog.Info("No debug draw switch found: only the plugin's own lines are drawn in edit mode");
            return globalDebugMember;
        }

        private static bool? ReadBool(MemberInfo m)
        {
            try { return m is PropertyInfo p ? (bool)p.GetValue(null) : m is FieldInfo f ? (bool)f.GetValue(null) : (bool?)null; } catch { return null; }
        }

        private static void WriteBool(MemberInfo m, bool v)
        {
            try { if (m is PropertyInfo p && p.CanWrite) p.SetValue(null, v); else if (m is FieldInfo f) f.SetValue(null, v); } catch { }
        }

        public void SetDebugDraw(bool on)
        {
            PluginLog.Info("Debug draw " + (on ? "on" : "off"));
            try
            {
                var m = GlobalDebugMember();
                var dd = DebugDraw;
                if (on)
                {
                    if (debugBefore.HasValue) return; // already on
                    debugBefore = m != null ? ReadBool(m) ?? false : false;
                    if (m != null) WriteBool(m, true);
                    // The builders copy the switch when they're made, so switch them on too.
                    buildersBefore.Clear();
                    void On(object b, FieldInfo f)
                    {
                        if (b == null || f == null) return;
                        buildersBefore.Add((b, f, (bool)f.GetValue(b)));
                        f.SetValue(b, true);
                    }
                    if (dd != null)
                    {
                        On(dd.DebugDraw, Enabled3D);
                        On(dd.DebugDraw2D, Enabled2D);
                        On(Inner2DBuilder?.GetValue(dd.DebugDraw2D), Enabled3D);
                    }
                    PluginLog.Info("Debug draw on for course edit mode");
                }
                else
                {
                    if (!debugBefore.HasValue) return;
                    if (m != null) WriteBool(m, debugBefore.Value);
                    foreach (var (b, f, was) in buildersBefore) { try { f.SetValue(b, was); } catch { } }
                    buildersBefore.Clear();
                    debugBefore = null;
                    PluginLog.Info("Debug draw put back after course edit mode");
                }
            }
            catch (Exception e) { LogOnce("Switching debug draw failed: " + e.Message); }
        }

        private static void WithEnabled(object builder, FieldInfo flag, Action draw)
        {
            if (flag == null || builder == null) { draw(); return; } // field renamed in a game update: falls back to needing debug draw
            bool wasEnabled = (bool)flag.GetValue(builder);
            if (!wasEnabled) flag.SetValue(builder, true);
            try { draw(); }
            finally { if (!wasEnabled) flag.SetValue(builder, false); }
        }

        private void WithShapes2D(MeshBuilder2D builder, Action draw)
        {
            object inner = Inner2DBuilder?.GetValue(builder);
            if (inner == null) LogOnce("MeshBuilder2D._builder not found: HUD panels need the game's debug draw switched on");
            WithEnabled(inner, Enabled3D, draw);
        }

        // ---- Game UI HUD (Avalonia overlay on the ScreenManager's root panel)

        private readonly GameUiHud gameUi = new GameUiHud();
        private PropertyInfo rootPanelProperty;

        private bool lastUiVisible = true;

        public bool ShowHud(HudModel model)
        {
            RefreshGameOptions();
            if (gameUi.Failed) return false;
            if (gameUi.Projector == null) gameUi.Projector = TryProjectToScreen;

            var uiEngine = EngineComponent.TryGet<UIEngineComponent>();
            if (uiEngine == null) { LogOnce("Game UI: UIEngineComponent not found, using debug-draw HUD"); return false; }

            IScreenManager screens = uiEngine.ScreenManager;
            if (screens == null) { LogOnce("Game UI: no ScreenManager yet, using debug-draw HUD"); return false; }

            // Follow the game's "hide HUD" toggle.
            bool uiVisible = screens.UIVisible;
            if (uiVisible != lastUiVisible)
            {
                lastUiVisible = uiVisible;
                string state = "?";
                try { state = screens.VisibilityState.ToString(); } catch { }
                PluginLog.Info($"Game UI visible: {uiVisible} (visibility state {state})");
            }
            if (!uiVisible) model = null;

            // ScreenManager.RootPanel is private: read it by reflection.
            if (rootPanelProperty == null)
                rootPanelProperty = AccessTools.Property(screens.GetType(), "RootPanel");
            if (rootPanelProperty == null) { LogOnce("Game UI: ScreenManager.RootPanel not found, using debug-draw HUD"); return false; }

            var panel = rootPanelProperty.GetValue(screens) as Avalonia.Controls.Panel;
            if (panel == null) { LogOnce("Game UI: root panel not created yet, using debug-draw HUD"); return false; }
            RacingMenu.Instance.RootPanel = panel;

            return gameUi.Show(panel, model);
        }

        // ---- Camera projection (HUD labels on checkpoints, edge arrow)

        private CameraComponent camera;
        private Entity cameraEntity;
        private double calibratedAt = double.NegativeInfinity;
        private bool flipForward, flipBehind;

        private CameraComponent Camera()
        {
            var e = controller?.CameraSystem?.RenderCameraEntity;
            if (e == null) return null;
            if (!ReferenceEquals(e, cameraEntity))
            {
                cameraEntity = e;
                camera = e.TryGet<CameraComponent>(default);
                calibratedAt = double.NegativeInfinity;
            }
            return camera;
        }

        private static Vector2 Project(CameraComponent cam, Vec3D p)
        {
            var v = new Vector3D(p.X, p.Y, p.Z);
            return cam.WorldToScreenPoint(in v);
        }

        public bool TryProjectToScreen(Vec3D world, out double x, out double y, out bool behind)
        {
            x = y = 0.5; behind = false;
            try
            {
                var cam = Camera();
                if (cam == null || cam.Entity == null || !cam.Entity.Data.Has<WorldTransform>()) return false;
                var res = cam.Resolution;
                if (res.X <= 0 || res.Y <= 0) return false;

                WorldTransform t = EntityTransformFunctions.GetWorldTransform(cam.Entity.Data);
                var camPos = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
                Vector3 f3 = t.Orientation.GetForward(), u3 = t.Orientation.GetUp();
                var fwd = new Vec3D(f3.X, f3.Y, f3.Z);
                var up = new Vec3D(u3.X, u3.Y, u3.Z);

                // Check (once a second) which way the camera really faces and whether points behind it come out
                // mirrored, by projecting test points: one straight ahead (should land mid-screen) and one behind
                // and above (with a perspective divide it lands below the centre, i.e. mirrored).
                if (TimeSeconds - calibratedAt > 1.0 || TimeSeconds < calibratedAt)
                {
                    calibratedAt = TimeSeconds;
                    Vector2 ahead = Project(cam, camPos + fwd * 100);
                    double ax = ahead.X / res.X, ay = ahead.Y / res.Y;
                    flipForward = Math.Abs(ax - 0.5) > 0.2 || Math.Abs(ay - 0.5) > 0.2;
                    var realFwd = flipForward ? fwd * -1 : fwd;
                    Vector2 back = Project(cam, camPos - realFwd * 100 + up * 50);
                    flipBehind = back.Y / res.Y > 0.5;
                }
                var forward = flipForward ? fwd * -1 : fwd;

                Vec3D rel = world - camPos;
                behind = Vec3D.Dot(rel, forward) < 0.5;
                Vector2 sp = Project(cam, world);
                x = sp.X / res.X;
                y = sp.Y / res.Y;
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) { x = 0.5; y = 1; }
                if (behind && flipBehind) { x = 1 - x; y = 1 - y; } // point the direction the right way round
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Camera projection failed (no HUD checkpoint labels): " + e.Message);
                return false;
            }
        }

        public bool HudVisible
        {
            get
            {
                try { return EngineComponent.TryGet<UIEngineComponent>()?.ScreenManager?.UIVisible ?? true; }
                catch { return true; }
            }
        }

        private static MethodInfo playOneShot;
        private static bool lookedForSound;

        public void PlaySound(string eventPath)
        {
            if (string.IsNullOrEmpty(eventPath)) return;
            try
            {
                if (!lookedForSound)
                {
                    lookedForSound = true;
                    var t = AccessTools.TypeByName("Keen.VRage.UI.AvaloniaInterface.Effects.UISoundPlayer");
                    playOneShot = t == null ? null : AccessTools.Method(t, "PlayOneShot", new[] { typeof(string) });
                    if (playOneShot == null) PluginLog.Info("UISoundPlayer.PlayOneShot not found: no sounds");
                }
                playOneShot?.Invoke(null, new object[] { eventPath });
            }
            catch (Exception e) { LogOnce("Sound failed: " + (e.InnerException?.Message ?? e.Message)); }
        }

        // ---- Racing Display controls menu (opened from the pause menu)

        public bool ControlsMenuOpen => RacingMenu.Instance.IsOpen;
        public bool TryTakeMenuCommand(out MenuCommand command) => RacingMenu.Instance.TryTake(out command);
        public void ShowControlsMenu(HudModel.ControlsPanel panel) => RacingMenu.Instance.Show(panel);
        public void CloseControlsMenu() => RacingMenu.Instance.RequestClose();

        public int ScreenWidth => DebugDraw?.ScreenResolution.X ?? 0;
        public int ScreenHeight => DebugDraw?.ScreenResolution.Y ?? 0;

        public void DrawText(string text, double x, double y, double scale, HudColor color, TextAlign align)
        {
            var dd = DebugDraw;
            if (dd == null) { LogOnce("IDebugDraw service not found, cannot draw text"); return; }
            if (Enabled2D == null) LogOnce("MeshBuilder2D._enabled not found: text needs the game's debug draw switched on");
            var builder = dd.DebugDraw2D;
            var pos = new Vector2((float)x, (float)y);
            var a = align == TextAlign.Left ? IMeshConsumer.TextAlignmentEnum.AlignLeft
                  : align == TextAlign.Right ? IMeshConsumer.TextAlignmentEnum.AlignRight
                  : IMeshConsumer.TextAlignmentEnum.AlignCenter;
            // Game overlays use scale 0.5 for normal text; our scale 1.0 maps to that.
            WithEnabled(builder, Enabled2D, () => builder.AddText(pos, text, ToColor(color), (float)(scale * 0.5), true, a, DrawTimeout));
        }

        public void DrawRect(double x, double y, double width, double height, HudColor fill)
        {
            var dd = DebugDraw;
            if (dd == null) return;
            var b = dd.DebugDraw2D;
            var c = ToColor(fill);
            var tl = new Vector2((float)x, (float)y);
            var tr = new Vector2((float)(x + width), (float)y);
            var bl = new Vector2((float)x, (float)(y + height));
            var br = new Vector2((float)(x + width), (float)(y + height));
            WithShapes2D(b, () =>
            {
                b.AddTriangle(tl, tr, br, c, DrawTimeout);
                b.AddTriangle(tl, br, bl, c, DrawTimeout);
            });
        }

        public void DrawRectOutline(double x, double y, double width, double height, HudColor color)
        {
            var dd = DebugDraw;
            if (dd == null) return;
            var b = dd.DebugDraw2D;
            var c = ToColor(color);
            var tl = new Vector2((float)x, (float)y);
            var tr = new Vector2((float)(x + width), (float)y);
            var bl = new Vector2((float)x, (float)(y + height));
            var br = new Vector2((float)(x + width), (float)(y + height));
            WithShapes2D(b, () =>
            {
                b.AddLine(tl, tr, c, null, DrawTimeout);
                b.AddLine(tr, br, c, null, DrawTimeout);
                b.AddLine(br, bl, c, null, DrawTimeout);
                b.AddLine(bl, tl, c, null, DrawTimeout);
            });
        }

        private static bool Finite(Vec3D v) =>
            double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z) && Math.Abs(v.X) < 1e9 && Math.Abs(v.Y) < 1e9 && Math.Abs(v.Z) < 1e9;
        private bool badPointLogged;
        private bool Bad(Vec3D v)
        {
            if (Finite(v)) return false;
            if (!badPointLogged) { badPointLogged = true; PluginLog.Info($"Skipped drawing a bad point ({v.X}, {v.Y}, {v.Z})"); }
            return true;
        }

        public void DrawWorldLine(Vec3D from, Vec3D to, HudColor color)
        {
            var dd = DebugDraw;
            if (dd == null || Bad(from) || Bad(to)) return;
            PluginLog.Step("debug-draw line");
            // Gate/checkpoint outlines deliberately follow the game's debug-draw setting
            // (only visible with debug draw on), unlike the HUD panels.
            // depth: false = drawn on top of everything, so you can find the gate through terrain.
            dd.DebugDraw.AddLine(new Vector3D(from.X, from.Y, from.Z), new Vector3D(to.X, to.Y, to.Z),
                ToColor(color), false, DrawTimeout);
        }

        public void DrawMarkerLines(IReadOnlyList<Vec3D> points, HudColor colour, bool depth)
        {
            var dd = DebugDraw;
            if (dd == null || points == null) return;
            var builder = dd.DebugDraw;
            var c = ToColor(colour);
            WithEnabled(builder, Enabled3D, () =>
            {
                PluginLog.Step("debug-draw marker lines");
                for (int i = 0; i + 1 < points.Count; i += 2)
                {
                    Vec3D a = points[i], b = points[i + 1];
                    if (Bad(a) || Bad(b)) continue;
                    builder.AddLine(new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z), c, depth, DrawTimeout);
                }
            });
        }

        public void DrawMarkerTriangles(IReadOnlyList<Vec3D> corners, IReadOnlyList<HudColor> colours, bool depth)
        {
            var dd = DebugDraw;
            if (dd == null || corners == null) return;
            var builder = dd.DebugDraw;
            WithEnabled(builder, Enabled3D, () =>
            {
                PluginLog.Step("debug-draw triangles");
                for (int i = 0; i + 2 < corners.Count; i += 3)
                {
                    Vec3D a = corners[i], b = corners[i + 1], c = corners[i + 2];
                    if (Bad(a) || Bad(b) || Bad(c)) continue;
                    builder.AddTriangle(new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z), new Vector3D(c.X, c.Y, c.Z),
                        ToColor(colours[i / 3]), depth, DrawTimeout);
                }
            });
        }

        public void DrawMarkerLine(Vec3D from, Vec3D to, HudColor color)
        {
            var dd = DebugDraw;
            if (dd == null || Bad(from) || Bad(to)) return;
            var builder = dd.DebugDraw;
            // Forced on (like the HUD fallback), so markers show without debug draw and with the HUD hidden.
            WithEnabled(builder, Enabled3D, () => builder.AddLine(new Vector3D(from.X, from.Y, from.Z), new Vector3D(to.X, to.Y, to.Z),
                ToColor(color), false, DrawTimeout));
        }

        public void DrawMarkerText(Vec3D position, string text, HudColor color, double scale)
        {
            var dd = DebugDraw;
            if (dd == null || Bad(position)) return;
            var builder = dd.DebugDraw;
            WithEnabled(builder, Enabled3D, () =>
            {
                var p = new Vector3D(position.X, position.Y, position.Z);
                builder.AddText(in p, text, ToColor(color), (float)scale, IMeshConsumer.TextAlignmentEnum.AlignCenter,
                    false, false, DrawTimeout, IMeshConsumer.FontTypes.Standard);
            });
        }

        private static ColorSRGB ToColor(HudColor c) => new ColorSRGB(c.R, c.G, c.B, c.A);

        // ---- Clock: real time, but frozen while the game is paused

        private readonly Stopwatch realClock = Stopwatch.StartNew();
        private double lastReal;
        private double gameTime;

        private void UpdateClock()
        {
            double now = realClock.Elapsed.TotalSeconds;
            double delta = now - lastReal;
            lastReal = now;
            bool paused = clientWorld?.Pause?.IsPaused ?? false;
            if (!paused && delta < 1.0) // ignore long hitches/loading screens
                gameTime += delta;
        }

        public double TimeSeconds => gameTime;

        public string ConfigFolder => PluginPaths.Folder;

        // ---- Keyboard: read straight from Windows, only while the game window has focus.
        // (Which keys do what is decided by the controller from the player's bindings.)

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private readonly uint ourPid = (uint)Environment.ProcessId;

        /// <summary>
        /// The race setup key: opens the pause menu from code, and the Racing Display menu opens on its Race tab as
        /// soon as the pause menu's buttons are built (PauseMenuButton.Add).
        /// </summary>
        public object WorldHandle => clientWorld;

        public bool RaceMapShown => RaceMapView.Shown;
        public void ShowRaceMap(HudModel.MinimapPanel map) => RaceMapView.Push(map);

        public bool? TakeHudCycle(bool raceHudOnly)
        {
            int r = System.Threading.Interlocked.Exchange(ref Patches.RaceHudCycleRequest, 0);
            if (r == 0) Patches.RaceHudOnlyNow = raceHudOnly;
            return r > 0 ? true : r < 0 ? false : (bool?)null;
        }

        public int WorldSaveCount => System.Threading.Volatile.Read(ref Patches.WorldSaves);

        public bool OpenRaceMenu()
        {
            if (RacingMenu.Instance.IsOpen) return true;
            if (TerminalTab.Added) return TerminalTab.OpenRacePage();
            RacingMenu.Instance.OpenRaceTabNext = true;
            if (TryOpenPauseMenu()) return true;
            RacingMenu.Instance.OpenRaceTabNext = false;
            return false;
        }

        public void OpenEditWheel(string[] labels, int current, int wheelKey, IDictionary<int, (byte R, byte G, byte B)> highlights = null)
        {
            // The game's own quick-select radial menu (like the paint gun's); the pause-menu wheel if that fails.
            if (!QuickWheel.Open(labels, current, highlights))
                WheelFailed = true;
        }

        public void ReleaseEditWheel() => QuickWheel.Release();

        /// <summary>Set when the wheel couldn't open (read and cleared by the controller to tell the player).</summary>
        public bool WheelFailed { get; set; }

        private List<Type> inGameMenuImpls;
        private bool pauseMenuLogged;

        /// <summary>
        /// Opens the pause menu from code: finds the game object that implements IInGameMenu (an engine or session
        /// component), then calls a parameterless Open...Menu method on it, or OpenInGameMenu with a new view model.
        /// Logs what it finds the first time.
        /// </summary>
        private bool TryOpenPauseMenu()
        {
            try
            {
                var iface = AccessTools.TypeByName("Keen.Game2.Client.UI.Menu.InGameMenu.IInGameMenu");
                var vmType = AccessTools.TypeByName("Keen.Game2.Client.UI.Menu.InGameMenu.InGameMenuScreenViewModel");
                if (iface == null) { PluginLog.Info("Pause menu: IInGameMenu not found"); return false; }
                if (inGameMenuImpls == null)
                    inGameMenuImpls = GameInternals.GameTypes().Where(t => !t.IsAbstract && !t.IsInterface && iface.IsAssignableFrom(t)).ToList();
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                bool log = !pauseMenuLogged;
                pauseMenuLogged = true;
                if (log)
                {
                    foreach (var t in inGameMenuImpls)
                        PluginLog.Info($"Pause menu: IInGameMenu is implemented by {t.FullName} (base {t.BaseType?.FullName}); methods: "
                            + string.Join("; ", t.GetMethods(all | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").Distinct().Take(30)));
                    if (vmType != null)
                        foreach (var c in vmType.GetConstructors(all))
                            PluginLog.Info("Pause menu: InGameMenuScreenViewModel(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                }

                foreach (var impl in inGameMenuImpls.OrderBy(t => t.Name.Contains("AutoPause") ? 0 : 1))
                {
                    object instance = GameInternals.Instance(impl.FullName) ?? FindComponent(impl);
                    if (log) PluginLog.Info($"Pause menu: instance of {impl.Name}: {(instance != null ? "found" : "not found")}");
                    if (instance == null) continue;

                    // 1) A parameterless method that opens it (e.g. OpenMenu / ToggleInGameMenu).
                    var opener = impl.GetMethods(all).FirstOrDefault(m => m.GetParameters().Length == 0 && !m.IsStatic
                        && (m.Name == "OpenInGameMenu" || m.Name == "ToggleInGameMenu" || m.Name == "OpenPauseMenu"));
                    if (opener != null)
                    {
                        PluginLog.Info($"Pause menu: calling {impl.Name}.{opener.Name}()");
                        opener.Invoke(instance, null);
                        return true;
                    }

                    // 2) OpenInGameMenu(new InGameMenuScreenViewModel(...)).
                    var open = iface.GetMethod("OpenInGameMenu");
                    if (open == null || vmType == null) continue;
                    var saved = GameInternals.PauseMenuArgs;
                    if (log) PluginLog.Info("Pause menu: view model arguments " + (saved != null ? "saved from the game's own pause menu" : "not seen yet (open the pause menu once with Esc)"));
                    foreach (var c in vmType.GetConstructors(all).OrderBy(c => c.GetParameters().Length))
                    {
                        var ps = c.GetParameters();
                        var args = ps.Select((p, k) =>
                            saved != null && k < saved.Length && (saved[k] == null || p.ParameterType.IsInstanceOfType(saved[k])) ? saved[k]
                            : p.ParameterType.IsInstanceOfType(instance) ? instance
                            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                        object vm;
                        try { vm = c.Invoke(args); } catch (Exception e) { if (log) PluginLog.Info("Pause menu: view model constructor failed: " + (e.InnerException ?? e).Message); continue; }
                        PluginLog.Info("Pause menu: calling OpenInGameMenu");
                        open.Invoke(instance, new[] { vm });
                        return true;
                    }
                }
            }
            catch (Exception e) { PluginLog.Info("Pause menu: opening from code failed: " + (e.InnerException ?? e).Message); }
            return false;
        }

        // ---- the game's interface options (speed unit, UI background opacity)
        private long optionsReadAt;
        private object guiOptions;

        /// <summary>
        /// Reads Options > Interface (GUIOptions: SpeedUnit and UIBackgroundOpacity) about twice a second, into
        /// <see cref="GameOptions"/>. The options live in EngineOptionsStorageEngineComponent._data, keyed by type.
        /// </summary>
        private void RefreshGameOptions()
        {
            long now = Environment.TickCount64;
            if (now - optionsReadAt < 500) return;
            optionsReadAt = now;
            try
            {
                if (guiOptions == null)
                {
                    var storageType = AccessTools.TypeByName("Keen.VRage.Core.EngineComponents.EngineOptionsStorageEngineComponent");
                    var guiType = AccessTools.TypeByName("Keen.Game2.Client.UI.Options.GUIOptions");
                    if (storageType == null || guiType == null) { LogOnce("Game options: GUIOptions not found (speeds in m/s)"); return; }
                    var storage = FindComponent(storageType);
                    if (AccessTools.Field(storageType, "_data")?.GetValue(storage) is System.Collections.IDictionary data && data.Contains(guiType))
                        guiOptions = data[guiType];
                    if (guiOptions == null) return;
                    PluginLog.Info("Game options: found the interface options (speed unit, UI opacity)");
                }
                var t = guiOptions.GetType();
                if (t.GetProperty("SpeedUnit")?.GetValue(guiOptions) is object unit) GameOptions.SpeedUnit = Convert.ToInt32(unit);
                if (t.GetProperty("UIBackgroundOpacity")?.GetValue(guiOptions) is float opacity) GameOptions.UiBackgroundOpacity = opacity;
            }
            catch (Exception e) { LogOnce("Game options: reading failed: " + (e.InnerException ?? e).Message); guiOptions = null; }
        }

        /// <summary>An engine component or (client) session component of the given type, via the game's generic TryGet.</summary>
        private object FindComponent(Type type)
        {
            try
            {
                if (typeof(EngineComponent).IsAssignableFrom(type))
                {
                    var m = typeof(EngineComponent).GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .FirstOrDefault(x => x.Name == "TryGet" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
                    var r = m?.MakeGenericMethod(type).Invoke(null, null);
                    if (r != null) return r;
                }
                var session = clientWorld?.OwnedSession;
                var comps = session?.SessionComponents;
                if (comps != null)
                {
                    var m = comps.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(x => x.Name == "TryGet" && x.IsGenericMethodDefinition && x.GetParameters().Length == 1);
                    if (m != null)
                    {
                        var g = m.MakeGenericMethod(type);
                        var pt = g.GetParameters()[0].ParameterType;
                        var r = g.Invoke(comps, new[] { pt.IsValueType ? Activator.CreateInstance(pt) : null });
                        if (r != null) return r;
                    }
                }
                // The local player's controller and its components (the menu may hang off the player).
                if (controller != null && type.IsInstanceOfType(controller)) return controller;
            }
            catch (Exception e) { LogOnce($"Finding {type.Name} failed: " + (e.InnerException ?? e).Message); }
            return null;
        }
        public bool TryTakeWheelPick(out int tool) => QuickWheel.TryTakePick(out tool);
        public bool EditWheelOpen => QuickWheel.IsOpen;

        public bool HasFocus
        {
            get
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                return pid == ourPid;
            }
        }

        public bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
}
