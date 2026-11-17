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
                    // Only a folder with this world's own name: guessing (the newest save, any save folder) could pick
                    // another world's folder, and its race course would turn up here. A new world that hasn't been
                    // saved yet simply has no folder.
                    saveFolder = saveFolder ?? (world != null ? SearchSaveFolder(world) : null);
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

        // The entity being flown. A hand tool (drill, grinder, welder) sits on top of the seat in the control
        // stack and has no position of its own, so look past it to the seat (or anything with a position).
        Entity ControlledEntity()
        {
            Entity top = controller?.GetTopControllable()?.Entity;
            if (top != null && (top.TryGet<SeatComponent>(default) != null || top.Data.Has<WorldTransform>())) return top;
            try
            {
                var stack = controller?.ControlledEntities;
                if (stack == null) return top;
                Entity withPose = null;
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    Entity e = stack[i].Controllable?.Entity;
                    if (e == null || e == top) continue;
                    if (e.TryGet<SeatComponent>(default) != null) return e;
                    if (withPose == null && e.Data.Has<WorldTransform>()) withPose = e;
                }
                return withPose ?? top;
            }
            catch { return top; }
        }

        public bool TryGetControlledPose(out Vec3D position, out Vec3D forward, out Vec3D up)
        {
            position = forward = up = Vec3D.Zero;
            ownGrid = null;

            Entity entity = ControlledEntity();
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

        // Set when a world is started from the Workshop screen; only good for a few minutes (the world it started).
        public string[] WorkshopStartIds =>
            Patches.WorkshopStartIds != null && (DateTime.Now - Patches.WorkshopStartAt).TotalMinutes < 10 ? Patches.WorkshopStartIds : null;

        public void ClearWorkshopStart() => Patches.WorkshopStartIds = null;

        public bool TryGetFacing(out Vec3D forward)
        {
            forward = Vec3D.Zero;
            try
            {
                Entity entity = ControlledEntity();
                if (entity == null) return false;
                // In a seat: the seat block's own facing, if it has one.
                var seat = entity.TryGet<SeatComponent>(default);
                Entity seatEntity = seat?.Entity;
                Entity e = seatEntity != null && seatEntity.Data.Has<WorldTransform>() ? seatEntity : entity;
                if (!e.Data.Has<WorldTransform>()) return false;
                Vector3 f = EntityTransformFunctions.GetWorldTransform(e.Data).Orientation.GetForward();
                forward = new Vec3D(f.X, f.Y, f.Z);
                return forward.Length > 1e-6;
            }
            catch (Exception ex) { LogOnce("Facing failed: " + ex.Message); return false; }
        }

        public bool TryGetGridVelocity(object gridObj, out Vec3D velocity)
        {
            velocity = Vec3D.Zero;
            try
            {
                if (!(gridObj is Entity grid) || !grid.Data.TryGet(out RigidBodyData rb)) return false;
                Vector3 v = rb.LinearVelocity;
                velocity = new Vec3D(v.X, v.Y, v.Z);
                return double.IsFinite(velocity.X) && double.IsFinite(velocity.Y) && double.IsFinite(velocity.Z);
            }
            catch { return false; }
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
        private List<IControllable> spectatorStack; // all of it, bottom (the character) to top (the cockpit)

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

        // ---- The game's own spectator lock: the camera kept at a place relative to a grid by the game itself, as it
        // draws each frame, which is smoother than the plugin moving it once an update. Found in SpectatorComponent:
        // _lockTarget (the grid), _lockMode (Follow keeps the camera in the grid's own frame) and _lockLocalTransform
        // (where the camera sits in that frame).
        private System.Reflection.FieldInfo lockTargetField, lockModeField, lockLocalField, lockLastPosField;
        private bool lockFieldsLookedUp, lockBroken;
        private object lockedOn;
        // What the lock fields held before we took over, put back exactly when we let go. (The game's own "no lock"
        // state: clearing the target by hand while the mode said None crashed the spectator's free-move update,
        // since None means "a target is still picked, just not followed".)
        private object lockOrigTarget, lockOrigMode, lockOrigLocal, lockOrigLastPos;
        private object lockSpectator;   // the spectator those were taken from
        private bool lockSaved;
        private Vec3D lockGridPos, lockGridF, lockGridU;   // the grid where it was when the camera was last placed

        private bool LookUpLockFields(object spectator)
        {
            if (!lockFieldsLookedUp)
            {
                lockFieldsLookedUp = true;
                var t = spectator.GetType();
                lockTargetField = t.GetField("_lockTarget", Any);
                lockModeField = t.GetField("_lockMode", Any);
                lockLocalField = t.GetField("_lockLocalTransform", Any);
                lockLastPosField = t.GetField("_lastTargetPosition", Any);
                bool ok = lockTargetField != null && lockModeField != null && lockLocalField != null && lockLocalField.FieldType == typeof(WorldTransform);
                PluginLog.Info("Spectator lock: " + (ok ? "found, cinematics follow ships with the game's own lock" : "not usable in this game version"));
                if (!ok) lockBroken = true;
            }
            return !lockBroken;
        }

        /// <summary>A WeakEntityReference (or whatever the lock field holds) for a grid; null if it can't be made.</summary>
        private static object MakeEntityReference(Type type, Entity grid)
        {
            foreach (var ctor in type.GetConstructors(Any))
            {
                var ps = ctor.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(grid)) return ctor.Invoke(new object[] { grid });
            }
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(grid) && type.IsAssignableFrom(m.ReturnType)) return m.Invoke(null, new object[] { grid });
            }
            return null;
        }

        public bool SetSpectatorFollow(object gridObj, Vec3D position, Vec3D forward, Vec3D up)
        {
            try
            {
                if (lockBroken || !(gridObj is Entity grid) || !grid.Data.Has<WorldTransform>()) return false;
                var spectator = SpectatorNow();
                if (spectator == null || !LookUpLockFields(spectator)) return false;
                // The camera in the grid's frame: x right, y up, z back (the game's axes).
                WorldTransform gt = EntityTransformFunctions.GetWorldTransform(grid.Data);
                Vec3D gf = V(gt.Orientation.GetForward()).Normalized(), gu = V(gt.Orientation.GetUp()).Normalized();
                Vec3D gr = Vec3D.Cross(gf, gu).Normalized(), gb = gf * -1;
                Vec3D Local(Vec3D v) => new Vec3D(Vec3D.Dot(v, gr), Vec3D.Dot(v, gu), Vec3D.Dot(v, gb));
                Vec3D d = position - ToVec(gt.Position);
                var local = View(Local(d), Local(forward.Normalized()), Local(up.Normalized()));
                if (!ReferenceEquals(lockedOn, grid))
                {
                    if (!lockSaved || !ReferenceEquals(lockSpectator, spectator))
                    {
                        lockOrigTarget = lockTargetField.GetValue(spectator);
                        lockOrigMode = lockModeField.GetValue(spectator);
                        lockOrigLocal = lockLocalField.GetValue(spectator);
                        lockOrigLastPos = lockLastPosField?.GetValue(spectator);
                        lockSpectator = spectator;
                        lockSaved = true;
                    }
                    object reference = MakeEntityReference(lockTargetField.FieldType, grid);
                    if (reference == null) { lockBroken = true; PluginLog.Info("Spectator lock: couldn't point it at a grid, back to moving the camera"); return false; }
                    lockTargetField.SetValue(spectator, reference);
                    lockModeField.SetValue(spectator, Enum.Parse(lockModeField.FieldType, "Follow"));
                    if (lockLastPosField != null && lockLastPosField.FieldType == typeof(Vector3D)) lockLastPosField.SetValue(spectator, gt.Position);
                    lockedOn = grid;
                }
                lockLocalField.SetValue(spectator, local);
                lockGridPos = ToVec(gt.Position); lockGridF = gf; lockGridU = gu;
                // This frame too, straight away (the game keeps it there from now on).
                var view = View(position, forward, up);
                EntityTransformFunctions.SetWorldTransform(spectator.Entity.Data, in view);
                return true;
            }
            catch (Exception e)
            {
                lockBroken = true;
                PluginLog.Info("Spectator lock failed, back to moving the camera: " + e.Message);
                ClearSpectatorFollow();
                return false;
            }
        }

        public void ClearSpectatorFollow()
        {
            if (lockedOn == null) return;
            lockedOn = null;
            try
            {
                // The spectator we locked (even if the player has already been handed back to the cockpit).
                object spectator = lockSpectator ?? SpectatorNow();
                if (spectator == null || lockTargetField == null || !lockSaved) return;
                lockTargetField.SetValue(spectator, lockOrigTarget);
                lockModeField?.SetValue(spectator, lockOrigMode);
                if (lockOrigLocal != null) lockLocalField?.SetValue(spectator, lockOrigLocal);
                if (lockOrigLastPos != null) lockLastPosField?.SetValue(spectator, lockOrigLastPos);
                PluginLog.Info($"Spectator lock let go (back to {lockOrigMode ?? "none"}, target {(lockOrigTarget == null ? "none" : "kept")})");
            }
            catch (Exception e) { PluginLog.Info("Spectator lock not cleared: " + e.Message); }
            finally { lockSaved = false; lockSpectator = null; lockOrigTarget = lockOrigMode = lockOrigLocal = lockOrigLastPos = null; }
        }

        public bool StartSpectator(Vec3D position, Vec3D forward, Vec3D up)
        {
            try
            {
                var session = CurrentSession();
                var modes = session?.SessionComponents.TryGet<SpectatingModesSessionComponent>(default);
                if (modes == null || controller == null) { PluginLog.Info("Replay: no spectator system"); return false; }
                spectatorReturn = controller.GetTopControllable();
                // The whole control stack (you, then the cockpit you're in): all of it is taken back afterwards.
                // (Only the cockpit was before: you were left out of your character, and flying then juddered.)
                spectatorStack = new List<IControllable>();
                try { foreach (var held in controller.ControlledEntities) if (held.Controllable != null) spectatorStack.Add(held.Controllable); }
                catch { spectatorStack.Clear(); }
                modes.SwitchToSpectator(View(position, forward, up));
                PluginLog.Info($"Replay: switching to spectator (was controlling {string.Join(" > ", spectatorStack.Select(c => c?.Entity?.DebugName))})");
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

        private bool loggedSpectatorUnder;

        /// <summary>
        /// The spectator camera you're in, or null. Usually the top of your control stack, but not always: a ship
        /// tool (or anything else the game puts on top) can sit over it, so the whole stack is looked through.
        /// (Only the top was looked at before: with a tool over the spectator the camera was never found, so a
        /// cinematic never took hold and the camera was left behind.)
        /// </summary>
        private SpectatorComponent SpectatorNow()
        {
            var top = controller?.GetTopControllable()?.Entity?.TryGet<SpectatorComponent>(default);
            if (top != null) return top;
            try
            {
                var stack = controller?.ControlledEntities;
                if (stack == null) return null;
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    var found = stack[i].Controllable?.Entity?.TryGet<SpectatorComponent>(default);
                    if (found == null) continue;
                    if (!loggedSpectatorUnder)
                    {
                        loggedSpectatorUnder = true;
                        PluginLog.Info("Spectator: found under " + (controller.GetTopControllable()?.Entity?.DebugName ?? "?") + " in the control stack (not on top)");
                    }
                    return found;
                }
            }
            catch { }
            return null;
        }

        public bool InSpectator
        {
            get
            {
                try { return SpectatorNow() != null; }
                catch { return false; }
            }
        }

        public bool TryGetSpectatorView(out Vec3D position, out Vec3D forward, out Vec3D up)
        {
            position = forward = up = Vec3D.Zero;
            try
            {
                var spectator = SpectatorNow();
                if (spectator == null || !spectator.Entity.Data.Has<WorldTransform>()) return false;
                var t = EntityTransformFunctions.GetWorldTransform(spectator.Entity.Data);
                position = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
                Vector3 f = t.Orientation.GetForward(), u = t.Orientation.GetUp();
                forward = new Vec3D(f.X, f.Y, f.Z);
                up = new Vec3D(u.X, u.Y, u.Z);
                // Locked on (the game carries the camera along with the grid): give it back as if the grid hadn't
                // moved since the camera was last placed, so only the mouse's and keys' own changes show.
                if (lockedOn is Entity grid && grid.Data.Has<WorldTransform>())
                {
                    WorldTransform gt = EntityTransformFunctions.GetWorldTransform(grid.Data);
                    Vec3D gf = V(gt.Orientation.GetForward()).Normalized(), gu = V(gt.Orientation.GetUp()).Normalized();
                    Vec3D gr = Vec3D.Cross(gf, gu).Normalized();
                    Vec3D r0 = Vec3D.Cross(lockGridF, lockGridU).Normalized();
                    Vec3D Back(Vec3D v) => r0 * Vec3D.Dot(v, gr) + lockGridU * Vec3D.Dot(v, gu) + lockGridF * Vec3D.Dot(v, gf);
                    position = lockGridPos + Back(position - ToVec(gt.Position));
                    forward = Back(forward);
                    up = Back(up);
                }
                return true;
            }
            catch { return false; }
        }

        public bool SetSpectatorView(Vec3D position, Vec3D forward, Vec3D up, bool stopMotion)
        {
            try
            {
                var spectator = SpectatorNow();
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
                var spectator = SpectatorNow();
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
            ClearSpectatorFollow();
            if (cinematicOn) SetSpectatorCinematic(false);
            try
            {
                if (controller == null) return;
                bool spectating = SpectatorNow() != null;
                if (spectating && spectatorReturn != null)
                {
                    // Let go of the spectator, then take back everything you controlled before, in order: your
                    // character first, then the cockpit it sits in.
                    controller.RequestUnpossessionAll();
                    var stack = spectatorStack != null && spectatorStack.Count > 0 ? spectatorStack : new List<IControllable> { spectatorReturn };
                    foreach (var c in stack) if (c != null) controller.RequestPossession(c);
                    PluginLog.Info($"Replay: back to {string.Join(" > ", stack.Select(c => c?.Entity?.DebugName))}");
                }
            }
            catch (Exception e) { PluginLog.Error("Replay: couldn't leave the spectator camera", e); }
            spectatorReturn = null;
            spectatorStack = null;
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

        // ---- camera field of view (cinematic effects). By name, so a game update that changes it can't break the build.
        private bool fovLooked, fovIsSet;
        private System.Reflection.MethodInfo fovSet, fovReset;
        private System.Reflection.FieldInfo fovDefault;

        private void LookUpFov(CameraComponent cam)
        {
            if (fovLooked) return;
            fovLooked = true;
            var t = cam.GetType();
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            fovSet = t.GetMethods(any).FirstOrDefault(m => m.Name == "SetCustomFOV");
            fovReset = t.GetMethod("ResetCustomFOV", any, null, Type.EmptyTypes, null);
            fovDefault = t.GetField("_defaultFov", any);
            PluginLog.Info($"Camera field of view: {(fovSet != null && fovReset != null ? "can be set (" + fovSet.GetParameters().Length + " values)" : "can't be set in this game version")}");
        }

        /// <summary>The camera's own field of view as the game holds it (radians in the versions seen), and the same in degrees.</summary>
        private bool GameFov(CameraComponent cam, out double raw, out double degrees)
        {
            raw = degrees = 0;
            try
            {
                if (fovDefault != null && fovDefault.GetValue(cam) is float f && f > 0.01) raw = f;
                else raw = cam.FieldOfView;
            }
            catch { return false; }
            if (!(raw > 0.01)) return false;
            degrees = raw > 3.2 ? raw : raw * 180 / Math.PI;   // (more than pi can only be degrees)
            return true;
        }

        public double DefaultFov
        {
            get
            {
                try
                {
                    var cam = Camera();
                    if (cam == null) return 0;
                    LookUpFov(cam);
                    return GameFov(cam, out _, out double degrees) ? degrees : 0;
                }
                catch { return 0; }
            }
        }

        public bool SetCameraFov(double? degrees)
        {
            try
            {
                var cam = Camera();
                if (cam == null) return false;
                LookUpFov(cam);
                if (fovSet == null || fovReset == null) return false;
                if (degrees == null)
                {
                    if (fovIsSet) { fovReset.Invoke(cam, null); fovIsSet = false; }
                    return true;
                }
                if (!GameFov(cam, out double raw, out double own)) return false;
                double wanted = Math.Clamp(degrees.Value, 5, 150);
                float value = (float)(raw > 3.2 ? wanted : wanted * Math.PI / 180);   // in the unit the game uses
                var ps = fovSet.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) args[i] = i == 0 ? value : 1f;   // (2nd value: mouse sensitivity, left as it is)
                if (ps.Length == 0 || ps[0].ParameterType != typeof(float)) return false;
                fovSet.Invoke(cam, args);
                fovIsSet = true;
                return true;
            }
            catch (Exception e) { LogOnce("Camera field of view couldn't be set: " + e.Message); return false; }
        }

        /// <summary>The name the player gave a grid (null when it has none).</summary>
        private static string GivenName(Entity grid)
        {
            try
            {
                var gc = grid.TryGet<CubeGridComponent>(default);
                string name = gc == null ? null : Raw(gc.DisplayName);
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch { return null; }
        }

        public string GridName(object gridObj)
        {
            try
            {
                if (!(gridObj is Entity grid) || !grid.Data.Has<WorldTransform>()) return null;
                return GivenName(grid) ?? grid.DebugName;
            }
            catch { return null; }
        }

        public object FindGrid(string name, Vec3D near)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                Session session = CurrentSession();
                if (session == null) return null;
                Entity found = null;
                double best = double.MaxValue;
                foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
                {
                    if (e == null || !TryGridBox(e, near, out GridBox gb, out _)) continue;
                    if (gb.Distance >= best) continue;
                    if (!string.Equals(GivenName(e) ?? gb.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    best = gb.Distance;
                    found = e;
                }
                return found;
            }
            catch (Exception e) { LogOnce("Cinematic: couldn't look for the grid: " + e.Message); return null; }
        }

        public void WatchWheel(bool on) => MouseWheel.Watch(on);
        public int TakeWheel() => MouseWheel.Take();

        // Each grid's middle (in its own coordinates) and size, worked out from its blocks every 2 seconds at most.
        private readonly Dictionary<Entity, (Vec3D Local, double Radius, double At)> poseCache = new Dictionary<Entity, (Vec3D, double, double)>();

        public bool TryGetGridPose(object gridObj, out Vec3D centre, out Vec3D x, out Vec3D y, out Vec3D z, out double radius, out Vec3D gravityDown)
        {
            centre = x = y = z = gravityDown = Vec3D.Zero;
            radius = 0;
            try
            {
                if (!(gridObj is Entity grid) || !grid.Data.Has<WorldTransform>()) return false;
                // The middle of the blocks, in grid coordinates, only changes when blocks do: worked out every 2 seconds.
                if (!poseCache.TryGetValue(grid, out var known) || TimeSeconds - known.At > 2 || TimeSeconds < known.At)
                {
                    var storage = grid.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
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
                    if (poseCache.Count > 32) poseCache.Clear();   // (grids come and go)
                    known = (new Vec3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2),
                             0.5 * Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ)),
                             TimeSeconds);
                    poseCache[grid] = known;
                }
                Vec3D poseLocal = known.Local;
                double poseRadius = known.Radius;
                WorldTransform t = EntityTransformFunctions.GetWorldTransform(grid.Data);
                x = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                y = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                z = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
                var pivot = new Vec3D(t.Position.X, t.Position.Y, t.Position.Z);
                centre = pivot + x * poseLocal.X + y * poseLocal.Y + z * poseLocal.Z;
                radius = poseRadius;
                try
                {
                    if (grid.Data.TryGet(out Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData g))
                    {
                        var sum = new Vec3D(g.GravitySum.X, g.GravitySum.Y, g.GravitySum.Z);
                        if (sum.Length > 0.05) gravityDown = sum.Normalized();
                    }
                }
                catch { /* no gravity read: the camera keeps the up it had */ }
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Orbit camera: couldn't read the grid: " + e.Message);
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

        /// <summary>
        /// Moves the ship you're flying so your seat is at <paramref name="seatPosition"/>, facing <paramref name="forward"/>
        /// with <paramref name="up"/> as up, and stops it. Uses the game's admin teleport (as its debug screens do).
        /// </summary>
        public bool TeleportShip(Vec3D seatPosition, Vec3D forward, Vec3D up)
        {
            try
            {
                if (ownGrid == null || !ownGrid.Data.Has<WorldTransform>()) return false;
                WorldTransform grid = EntityTransformFunctions.GetWorldTransform(ownGrid.Data);
                // The seat's frame (falls back to the grid's own if there's no seat).
                Entity seat = ControlledEntity()?.TryGet<SeatComponent>(default)?.Entity;
                WorldTransform seatT = seat != null && seat.Data.Has<WorldTransform>() ? EntityTransformFunctions.GetWorldTransform(seat.Data) : grid;
                Vec3D f = V(seatT.Orientation.GetForward()).Normalized(), u = V(seatT.Orientation.GetUp());
                if (seat != null) PilotAxes(seat, ref f, ref u);   // (the same frame TryGetSeatPose gives)
                f = f.Normalized();
                u = (u - f * Vec3D.Dot(u, f)).Normalized();
                Vec3D sd = Vec3D.Cross(f, u);
                Vec3D df = forward.Normalized(), du = (up - df * Vec3D.Dot(up, df)).Normalized();
                if (du.Length < 0.5) return false;
                Vec3D ds = Vec3D.Cross(df, du);
                // Turn that takes the seat's frame onto the wanted one, applied to the whole grid.
                Vec3D Turn(Vec3D v) => df * Vec3D.Dot(v, f) + du * Vec3D.Dot(v, u) + ds * Vec3D.Dot(v, sd);
                Vec3D gf = Turn(V(grid.Orientation.GetForward())), gu = Turn(V(grid.Orientation.GetUp()));
                Vec3D offset = Turn(ToVec(seatT.Position) - ToVec(grid.Position));
                Vec3D pos = seatPosition - offset;
                var target = new WorldTransform(new Vector3D(pos.X, pos.Y, pos.Z),
                    Quaternion.CreateFromForwardUp(new Vector3((float)gf.X, (float)gf.Y, (float)gf.Z), new Vector3((float)gu.X, (float)gu.Y, (float)gu.Z)));
                StopShip();
                Keen.Game2.Simulation.GameSystems.AdminTools.EntityAdmin.TeleportEntity(ownGrid, target);
                PluginLog.Info($"Teleport: ship to {seatPosition}");
                return true;
            }
            catch (Exception ex) { PluginLog.Error("Teleport failed", ex); return false; }
        }

        public bool Seated
        {
            get { try { return ControlledEntity()?.TryGet<SeatComponent>(default) != null; } catch { return false; } }
        }

        // A cockpit's "pilotable" part (the game's IPilotable, found by name: TryPilotAsync(character) / TryUnpilotAsync()).
        private readonly List<Entity> pilotScratch = new List<Entity>();

        private (object Part, Type Face) PilotableOn(Entity blockEntity)
        {
            if (blockEntity == null) return (null, null);
            pilotScratch.Clear();
            CollectBlockEntities(blockEntity, pilotScratch, 0);
            if (!pilotScratch.Contains(blockEntity)) pilotScratch.Add(blockEntity);
            foreach (Entity part in pilotScratch)
            {
                var comps = Member(part, "Components") as System.Collections.IEnumerable;
                if (comps == null) continue;
                foreach (object comp in comps)
                {
                    if (comp == null) continue;
                    foreach (Type face in comp.GetType().GetInterfaces())
                        if (face.Name == "IPilotable") return (comp, face);
                }
            }
            return (null, null);
        }

        public bool LeaveSeat()
        {
            try
            {
                var seat = ControlledEntity()?.TryGet<SeatComponent>(default);
                if (seat == null) return false;
                foreach (Entity e in new[] { seat.Entity, seat.Entity?.TryGet<CubeBlockComponent>(default)?.Entity, ControlledEntity() })
                {
                    var (part, face) = PilotableOn(e);
                    if (part == null) continue;
                    face.GetMethod("TryUnpilotAsync")?.Invoke(part, null);
                    PluginLog.Info("Seat: asked to leave the seat");
                    return true;
                }
                LogOnce("Seat: no pilotable part found on the seat you're in");
                return false;
            }
            catch (Exception ex) { PluginLog.Error("Seat: leaving failed", ex); return false; }
        }

        public bool SitInGrid(object grid)
        {
            try
            {
                if (!(grid is Entity g)) return false;
                Entity me = null;
                var stack = controller?.ControlledEntities;
                if (stack != null) foreach (var held in stack) { me = held.Controllable?.Entity; if (me != null) break; }   // (the bottom of the stack: your character)
                if (me == null) return false;
                var storage = g.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                if (storage == null) return false;
                foreach (var b in storage.GetAllCubeBlocks())
                {
                    Entity ent = null;
                    try { ent = b?.Entity; } catch { }
                    if (ent == null) continue;
                    var (part, face) = PilotableOn(ent);
                    if (part == null) continue;
                    bool Flag(string name, bool fallback) { try { return face.GetProperty(name)?.GetValue(part) is bool v ? v : fallback; } catch { return fallback; } }
                    if (!Flag("IsEnabled", true) || !Flag("IsFunctional", true) || Flag("HasPilot", false)) continue;
                    face.GetMethod("TryPilotAsync")?.Invoke(part, new object[] { me });
                    PluginLog.Info("Seat: asked to sit in a cockpit of " + GridName(grid));
                    return true;
                }
                PluginLog.Info("Seat: no free, working cockpit on " + GridName(grid));
                return false;
            }
            catch (Exception ex) { PluginLog.Error("Seat: sitting failed", ex); return false; }
        }

        public bool TeleportCharacter(Vec3D position, Vec3D forward, Vec3D up)
        {
            try
            {
                // (ControlledEntity looks past a hand tool, which has no position of its own, to the character.)
                Entity me = ControlledEntity();
                if (me == null || !me.Data.Has<WorldTransform>()) return false;
                if (me.TryGet<SeatComponent>(default) != null) return false;   // (seated: the seat holds you)
                Vec3D f = forward.Normalized(), u = (up - f * Vec3D.Dot(up, f)).Normalized();
                if (u.Length < 0.5) return false;
                var target = new WorldTransform(new Vector3D(position.X, position.Y, position.Z),
                    Quaternion.CreateFromForwardUp(new Vector3((float)f.X, (float)f.Y, (float)f.Z), new Vector3((float)u.X, (float)u.Y, (float)u.Z)));
                Keen.Game2.Simulation.GameSystems.AdminTools.EntityAdmin.TeleportEntity(me, target);
                PluginLog.Info($"Teleport: character to {position}");
                return true;
            }
            catch (Exception ex) { PluginLog.Error("Character teleport failed", ex); return false; }
        }

        /// <summary>Asks the game to stop the ship you're flying (its velocity to zero).</summary>
        public void StopShip()
        {
            try
            {
                if (ownGrid == null) return;
                var session = CurrentSession();
                var server = session == null ? null : GameEntityExtensions.TryGet<Keen.Game2.Simulation.Replication.IGameServer>(session);
                if (server == null) { LogOnce("Stop ship: no game server"); return; }
                server.StopEntityMovement(ownGrid);
            }
            catch (Exception ex) { LogOnce("Stop ship failed: " + ex.Message); }
        }

        public bool SetShipVelocity(Vec3D velocity)
        {
            try
            {
                if (ownGrid == null || !ownGrid.Data.Has<WorldTransform>()) return false;
                var v = new Vector3((float)velocity.X, (float)velocity.Y, (float)velocity.Z);
                // The server's copy of the ship is the one that moves it (single player / hosting); the client's as well so it shows at once.
                Vector3D at = EntityTransformFunctions.GetWorldTransform(ownGrid.Data).Position;
                var storage = ownGrid.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                Entity twin = ServerTwin(ownGrid, ToVec(at), storage != null ? storage.GetAllCubeBlocks().Length : 0);
                bool done = false;
                foreach (Entity e in new[] { twin, ownGrid })
                {
                    if (e == null) continue;
                    ref RigidBodyData rb = ref e.Data.TryGetWritePtr<RigidBodyData>();
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref rb)) continue;
                    rb.LinearVelocity = v;
                    done |= ReferenceEquals(e, twin);
                }
                if (!done) LogOnce("Exit speed: the server's copy of the ship wasn't found (only works in single player or when hosting)");
                return done;
            }
            catch (Exception ex) { LogOnce("Exit speed failed: " + ex.Message); return false; }
        }

        // ---- Height above the ground: the game's planet surface lookup (IPlanetSurfaceProvider), found by reflection.
        private object surfaceProvider;
        private Session surfaceSession;
        private MethodInfo surfaceMethod;
        private bool surfaceSearched;
        private double surfaceRetryAt;
        private static List<Type> surfaceTypes;

        public bool TryGetGroundDistance(Vec3D position, out double metres)
        {
            metres = 0;
            try
            {
                var session = CurrentSession();
                if (session == null) return false;
                if (!ReferenceEquals(session, surfaceSession)) { surfaceSession = session; surfaceProvider = null; surfaceSearched = false; }
                if (surfaceProvider == null)
                {
                    double now = TimeSeconds;
                    if (surfaceSearched && now < surfaceRetryAt) return false;
                    surfaceSearched = true;
                    surfaceRetryAt = now + 10;
                    surfaceProvider = FindSurfaceProvider(session);
                    if (surfaceProvider == null) return false;
                }
                var args = new object[] { new Vector3D(position.X, position.Y, position.Z), null, null };
                if (!(surfaceMethod.Invoke(surfaceProvider, args) is bool found) || !found) return false;
                metres = Convert.ToDouble(args[2]);
                return double.IsFinite(metres);
            }
            catch (Exception e) { LogOnce("Ground height failed: " + e.Message); surfaceProvider = null; return false; }
        }

        private MethodInfo altitudeMethod;
        private object altitudeFor;

        public bool TryGetSeaAltitude(Vec3D position, out double metres)
        {
            metres = 0;
            try
            {
                // (The same session component as the ground height: the game's planet tracker, its TryGetAltitude.)
                object provider = surfaceProvider;
                if (provider == null) return false;
                if (!ReferenceEquals(provider, altitudeFor))
                {
                    altitudeFor = provider;
                    altitudeMethod = provider.GetType().GetMethod("TryGetAltitude", new[] { typeof(Vector3D) });
                    PluginLog.Info("Sea level height: " + (altitudeMethod != null ? "from " + provider.GetType().Name + ".TryGetAltitude" : "no TryGetAltitude on " + provider.GetType().Name));
                }
                if (altitudeMethod == null) return false;
                object result = altitudeMethod.Invoke(provider, new object[] { new Vector3D(position.X, position.Y, position.Z) });
                if (result == null) return false;
                metres = Convert.ToDouble(result);
                return double.IsFinite(metres);
            }
            catch (Exception e) { LogOnce("Sea level height failed: " + e.Message); return false; }
        }

        /// <summary>The session component that answers "how far above the terrain is this point" (whatever it's called this version).</summary>
        private object FindSurfaceProvider(Session session)
        {
            var iface = AccessTools.TypeByName("Keen.VRage.Voxels.Components.IPlanetSurfaceProvider");
            if (iface == null) { LogOnce("Ground height: IPlanetSurfaceProvider not found"); return null; }
            surfaceMethod = iface.GetMethod("TryFindPlanetAndSurface");
            if (surfaceMethod == null) { LogOnce("Ground height: TryFindPlanetAndSurface not found"); return null; }
            object comps = session.SessionComponents;
            var tryGet = comps?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "TryGet" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1);
            if (tryGet == null) { LogOnce("Ground height: no SessionComponents.TryGet"); return null; }
            object defaultArg = tryGet.GetParameters()[0].ParameterType.IsValueType ? Activator.CreateInstance(tryGet.GetParameters()[0].ParameterType) : null;
            if (surfaceTypes == null)
            {
                // (Looked through once: going through every type is slow.)
                surfaceTypes = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); } catch { continue; }
                    foreach (var t in types)
                        if (t != null && !t.IsAbstract && !t.IsInterface && iface.IsAssignableFrom(t)) surfaceTypes.Add(t);
                }
                PluginLog.Info("Ground height: planet surface lookups found: " + (surfaceTypes.Count == 0 ? "none" : string.Join(", ", surfaceTypes.Select(t => t.Name))));
            }
            {
                foreach (var t in surfaceTypes)
                {
                    try
                    {
                        object found = tryGet.MakeGenericMethod(t).Invoke(comps, new[] { defaultArg });
                        if (found != null)
                        {
                            PluginLog.Info($"Ground height: using {t.Name}");
                            return found;
                        }
                    }
                    catch { /* not a session component */ }
                }
            }
            LogOnce("Ground height: no planet surface lookup in this session");
            return null;
        }

        public bool HasLocalServer
        {
            get
            {
                try
                {
                    var clientSession = CurrentSession();
                    lock (Worlds)
                        foreach (var weak in Worlds)
                            if (weak.TryGetTarget(out var world) && world.OwnedSession != null && !ReferenceEquals(world.OwnedSession, clientSession)) return true;
                }
                catch { }
                return false;
            }
        }

        public List<string> RemoveLostGrids(IReadOnlyCollection<string> names)
        {
            var removed = new List<string>();
            if (names == null || names.Count == 0) return removed;
            try
            {
                // The watchdog names an entity by its id in the server's scene ("64:1"), so only the server's grids are
                // looked at (the client's own scene has its own ids), matched on that id (or on the grid's name).
                var found = new List<(Session Session, Entity Grid, string Name)>();
                var clientSession = CurrentSession();
                lock (Worlds)
                {
                    foreach (var weak in Worlds)
                    {
                        if (!weak.TryGetTarget(out var world)) continue;
                        var session = world.OwnedSession;
                        if (session == null || ReferenceEquals(session, clientSession)) continue;
                        foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
                        {
                            if (e == null) continue;
                            var ids = new List<string> { e.DebugName, e.ToString() };
                            foreach (string member in new[] { "DEntity", "Handle", "Id", "EntityId" })
                                try { if (Member(e, member) is object v) ids.Add(v.ToString()); } catch { }
                            foreach (string lost in names)
                                if (ids.Any(id => !string.IsNullOrEmpty(id) && (id == lost || id.StartsWith(lost + "_", StringComparison.Ordinal))))
                                { found.Add((session, e, $"{e.DebugName} ({lost})")); break; }
                        }
                    }
                }
                var deleted = new HashSet<Entity>();
                foreach (var (session, grid, name) in found)
                {
                    if (!deleted.Add(grid)) continue;
                    try
                    {
                        Keen.Game2.Simulation.GameSystems.AdminTools.EntityAdmin.DeleteEntity(session, grid);
                        removed.Add(name);
                        PluginLog.Info($"Removed a grid the game found inside the planet: {name}");
                    }
                    catch (Exception ex) { PluginLog.Info($"Couldn't remove the grid inside the planet ({name}): {ex.Message}"); }
                }
                if (found.Count == 0)
                {
                    // (What the server's grids look like, so the matching can be put right.)
                    var sample = new List<string>();
                    lock (Worlds)
                        foreach (var weak in Worlds)
                        {
                            if (!weak.TryGetTarget(out var world) || world.OwnedSession == null || ReferenceEquals(world.OwnedSession, clientSession)) continue;
                            foreach (Entity e in world.OwnedSession.GetEntitiesOfType<CubeGridComponent>())
                            {
                                if (e == null || sample.Count >= 6) break;
                                string dent = null;
                                try { dent = Member(e, "DEntity")?.ToString(); } catch { }
                                sample.Add($"{e.DebugName} / {e} / {dent}");
                            }
                        }
                    PluginLog.Info("Grid inside the planet: not found (" + string.Join(", ", names) + "); server grids look like: " + string.Join("; ", sample));
                }
            }
            catch (Exception ex) { PluginLog.Info("Grids inside the planet not removed: " + ex.Message); }
            return removed;
        }

        public object ServerCopy(object gridObj)
        {
            try
            {
                if (!(gridObj is Entity grid) || !grid.Data.Has<WorldTransform>()) return null;
                Vector3D at = EntityTransformFunctions.GetWorldTransform(grid.Data).Position;
                var storage = grid.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                return ServerTwin(grid, ToVec(at), storage != null ? storage.GetAllCubeBlocks().Length : 0);
            }
            catch { return null; }
        }

        public bool SetShipMotion(object gridObj, Vec3D velocity, Vec3D spin)
        {
            try
            {
                if (!(gridObj is Entity grid) || !grid.Data.Has<WorldTransform>()) return false;
                var v = new Vector3((float)velocity.X, (float)velocity.Y, (float)velocity.Z);
                var w = new Vector3((float)spin.X, (float)spin.Y, (float)spin.Z);
                Vector3D at = EntityTransformFunctions.GetWorldTransform(grid.Data).Position;
                var storage = grid.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                Entity twin = ServerTwin(grid, ToVec(at), storage != null ? storage.GetAllCubeBlocks().Length : 0);
                bool done = false;
                foreach (Entity e in new[] { twin, grid })
                {
                    if (e == null) continue;
                    ref RigidBodyData rb = ref e.Data.TryGetWritePtr<RigidBodyData>();
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref rb)) continue;
                    rb.LinearVelocity = v;
                    rb.AngularVelocity = w;
                    done |= ReferenceEquals(e, twin);
                }
                if (!done) LogOnce("Autopilot: the server's copy of the ship wasn't found (only works in single player or when hosting)");
                return done;
            }
            catch (Exception ex) { LogOnce("Autopilot couldn't move the ship: " + ex.Message); return false; }
        }

        private readonly List<object> gyroCockpits = new List<object>();

        private static void SetTargetGyro(object cockpit, bool on)
        {
            var prop = cockpit.GetType().GetProperty("TargetBasedGyro", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            prop?.SetValue(cockpit, on);
        }

        public int TargetGyroOff()
        {
            gyroCockpits.Clear();
            try
            {
                if (ownGrid == null || !ownGrid.Data.Has<WorldTransform>()) return 0;
                Vector3D at = EntityTransformFunctions.GetWorldTransform(ownGrid.Data).Position;
                var storage = ownGrid.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                // (The setting lives on the server's copy of the cockpit; without one, the client's own is tried.)
                Entity source = ServerTwin(ownGrid, ToVec(at), storage != null ? storage.GetAllCubeBlocks().Length : 0) ?? ownGrid;
                var blocks = source.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                if (blocks == null) return 0;
                var found = new List<object>();
                foreach (var b in blocks.GetAllCubeBlocks())
                {
                    Entity ent = b?.Entity;
                    if (ent == null) continue;
                    blockEntities.Clear();
                    CollectBlockEntities(ent, blockEntities, 0);
                    foreach (Entity part in blockEntities)
                    {
                        if (!(Member(part, "Components") is System.Collections.IEnumerable comps)) continue;
                        foreach (object comp in comps)
                            if (comp != null && comp.GetType().Name == "CockpitComponent" && Member(comp, "TargetBasedGyro") is bool on && on) found.Add(comp);
                    }
                }
                foreach (object cockpit in found) { SetTargetGyro(cockpit, false); gyroCockpits.Add(cockpit); }
                if (gyroCockpits.Count > 0) PluginLog.Info($"Teleport: target based gyro off and back on for {gyroCockpits.Count} cockpit(s)");
            }
            catch (Exception ex) { LogOnce("Target based gyro reset failed: " + (ex.InnerException ?? ex).Message); }
            return gyroCockpits.Count;
        }

        public void TargetGyroRestore()
        {
            try { foreach (object cockpit in gyroCockpits) SetTargetGyro(cockpit, true); }
            catch (Exception ex) { LogOnce("Target based gyro couldn't be turned back on: " + (ex.InnerException ?? ex).Message); }
            gyroCockpits.Clear();
        }

        public bool TryGetGravityParts(out Vec3D natural, out Vec3D artificial)
        {
            natural = artificial = Vec3D.Zero;
            if (Environment.TickCount64 < gravityPartsRestUntil) return false;
            try
            {
                // (The game's HUD keeps a probe for each kind of gravity; GameUiHud finds them.)
                object[] probes = GameUiHud.GravityProbes;
                string[] names = GameUiHud.GravityProbeNames;
                if (probes == null || names == null || probes.Length < 2 || names.Length != probes.Length) return false;
                // The probes as they are this frame (the game makes new ones when you change seat). Just changed:
                // left alone for a moment, while the new ones settle and the old ones go.
                FieldInfo[] fields = GameUiHud.GravityProbeFields;
                object hud = GameUiHud.GravityProbeVm;
                if (fields != null && hud != null && fields.Length == probes.Length)
                {
                    var now = new object[fields.Length];
                    bool changed = gravityProbesSeen == null || gravityProbesSeen.Length != now.Length;
                    for (int i = 0; i < fields.Length; i++)
                    {
                        now[i] = fields[i].GetValue(hud);
                        if (now[i] == null) { gravityProbesSeen = null; return false; }
                        if (!changed && !ReferenceEquals(now[i], gravityProbesSeen[i])) changed = true;
                    }
                    gravityProbesSeen = now;
                    probes = now;
                    if (changed) { gravityPartsRestUntil = Environment.TickCount64 + 700; return false; }
                }
                var pulls = new Vec3D[probes.Length];
                var read = new bool[probes.Length];
                int n = -1, a = -1;
                for (int i = 0; i < probes.Length; i++)
                {
                    // (A probe from a seat you've left is gone from the world: reading it throws, and every throw
                    // cost a fifth of a second, a stutter getting into a cockpit. Only ones still in a scene are read.)
                    if (!(probes[i] is Entity probe) || probe.DEntity.IsEmpty || (object)probe.Scene == null) return false;
                    if (!probe.Data.TryGet(out Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData pull)) continue;
                    pulls[i] = new Vec3D(pull.GravitySum.X, pull.GravitySum.Y, pull.GravitySum.Z);
                    read[i] = true;
                    string name = names[i].ToLowerInvariant();
                    if (n < 0 && name.Contains("natur")) n = i;
                    else if (a < 0 && name.Contains("artific")) a = i;
                }
                if (n < 0 || a < 0)
                {
                    // Not named: the pair whose strengths match what the game's HUD shows for each kind.
                    double wantN = GameUiHud.GravityNaturalG * 9.81, wantA = GameUiHud.GravityArtificialG * 9.81, best = 1.5;
                    n = a = -1;
                    for (int i = 0; i < probes.Length; i++)
                        for (int j = 0; j < probes.Length; j++)
                        {
                            if (i == j || !read[i] || !read[j]) continue;
                            double off = Math.Abs(pulls[i].Length - wantN) + Math.Abs(pulls[j].Length - wantA);
                            if (off < best) { best = off; n = i; a = j; }
                        }
                }
                if (n < 0 || a < 0) return false;
                natural = pulls[n];
                artificial = pulls[a];
                string how = $"natural = {names[n]}, artificial = {names[a]}";
                LogOnce("Gravity arrows: " + how);
                return true;
            }
            catch (Exception ex)
            {
                // (Not tried again with these probes, and not at all for a few seconds: no run of throws.)
                GameUiHud.GravityProbes = null;
                gravityPartsRestUntil = Environment.TickCount64 + 5000;
                LogOnce("Gravity parts not read: " + ex.Message);
                return false;
            }
        }

        private long gravityPartsRestUntil;
        private object[] gravityProbesSeen;

        private PropertyInfo shipLightsProp;
        private bool shipLightsLooked;

        public bool? ShipLightsOn
        {
            get
            {
                try
                {
                    var grid = ownGrid?.TryGet<CubeGridComponent>(default);
                    if (grid == null) return null;
                    if (!shipLightsLooked)
                    {
                        shipLightsLooked = true;
                        shipLightsProp = typeof(CubeGridComponent).GetProperty("LightsAreTurnedOn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        PluginLog.Info("Ship lights: " + (shipLightsProp != null ? "read from the grid's LightsAreTurnedOn" : "the grid has no LightsAreTurnedOn; the lamp icon stays off"));
                    }
                    return shipLightsProp?.GetValue(grid) as bool?;
                }
                catch { return null; }
            }
        }

        public double GravityStrength
        {
            get
            {
                try
                {
                    if (ownGrid == null || !ownGrid.Data.TryGet(out Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData pull)) return 0;
                    return new Vec3D(pull.GravitySum.X, pull.GravitySum.Y, pull.GravitySum.Z).Length;
                }
                catch { return 0; }
            }
        }

        public bool TryGetGravity(out Vec3D down)
        {
            down = Vec3D.Zero;
            try
            {
                if (ownGrid == null || !ownGrid.Data.TryGet(out Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData g)) return false;
                var sum = new Vec3D(g.GravitySum.X, g.GravitySum.Y, g.GravitySum.Z);
                if (sum.Length < 0.05) return false;
                down = sum.Normalized();
                return true;
            }
            catch (Exception ex) { LogOnce("Gravity not read: " + ex.Message); return false; }
        }

        private static Vec3D V(Vector3 v) => new Vec3D(v.X, v.Y, v.Z);

        // ---------------------------------------------------------------- ship analyser

        public object OwnGrid => ownGrid;

        /// <summary>
        /// The grid whose nearest block the ray hits first (blocks, not the grid's whole box, so the station or
        /// platform you stand on doesn't get in the way of the ship you look at).
        /// </summary>
        public object LookedAtGrid(Vec3D origin, Vec3D direction, double maxDistance, bool includeOwn)
        {
            try
            {
                Session session = CurrentSession();
                if (session == null) return null;
                Vec3D end = origin + direction * maxDistance;
                Entity bestGrid = null;
                double best = maxDistance;
                foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
                {
                    if (e == null || (!includeOwn && ReferenceEquals(e, ownGrid))) continue;
                    if (!TryGridBox(e, origin, out GridBox gb, out WorldTransform t)) continue;
                    if (gb.Distance > best) continue;   // a nearer block has been found already
                    var test = new TimingGate(gb.Center, gb.Forward, gb.Up, gb.Width, gb.Height, gb.Depth);
                    if (!test.SegmentIntersects(origin, end) && !test.Contains(origin)) continue;
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
                        BoundingBox local = CubeGridCoords.GridToLocal(b.AABB);
                        double[] lo = { Math.Min(local.Min.X, local.Max.X), Math.Min(local.Min.Y, local.Max.Y), Math.Min(local.Min.Z, local.Max.Z) };
                        double[] hi = { Math.Max(local.Min.X, local.Max.X), Math.Max(local.Min.Y, local.Max.Y), Math.Max(local.Min.Z, local.Max.Z) };
                        double hit = RayHitsBox(ro, rd, lo, hi);
                        // A block the camera is inside (your own cockpit, or the floor you stand in) doesn't count.
                        if (hit <= 0.05 || hit >= best) continue;
                        best = hit; bestGrid = e;
                    }
                }
                return bestGrid;
            }
            catch (Exception e) { LogOnce("Analyser: looked-at grid failed: " + e.Message); return null; }
        }

        private static object Member(object o, string name)
        {
            if (o == null) return null;
            // Most derived first, one class at a time (a derived class can hide a base member of the same name,
            // which makes a plain GetProperty throw "ambiguous match").
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var type = o.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var p in type.GetProperties(flags))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return p.GetValue(o);
                var f = type.GetField(name, flags);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static bool HasMember(object o, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var type = o?.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var p in type.GetProperties(flags)) if (p.Name == name) return true;
                if (type.GetField(name, flags) != null) return true;
            }
            return false;
        }

        private static bool IsOrDerives(object o, string typeName)
        {
            for (var type = o?.GetType(); type != null; type = type.BaseType) if (type.Name == typeName) return true;
            return false;
        }

        /// <summary>"kW", "kWh", "L"... for the game's unit enum (empty if it has none).</summary>
        private static string UnitSymbol(object unit)
        {
            switch (unit?.ToString())
            {
                case "Kilowatts": return "kW";
                case "KilowattHours": return "kWh";
                case "Liters": return "L";
                case "LitersPerSecond": return "L/s";
                case "CubicMeters": return "m3";
                case "Kilograms": return "kg";
                case null: case "None": return "";
                default: return unit.ToString();
            }
        }

        // ---- Singleplayer runs a server session alongside the client one. The client's copy of a grid only has the
        // parts the client needs (no gyroscope, inventory, battery or tank parts), so those are read from the
        // server's copy of the same grid: the server grid in the same place with the same number of blocks.
        private readonly Dictionary<Entity, Entity> serverTwins = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, double> twinSearchedAt = new Dictionary<Entity, double>();

        private Entity ServerTwin(Entity clientGrid, Vec3D at, int blocks)
        {
            try
            {
                // (A fast ship's two copies can be a few frames apart: the allowed gap grows with its speed.)
                double near = 10;
                if (clientGrid.Data.TryGet(out RigidBodyData clientRb)) { var cv = clientRb.LinearVelocity; near += Math.Sqrt(cv.X * cv.X + cv.Y * cv.Y + cv.Z * cv.Z) * 0.25; }
                // One found before stays the one while it's still there and not far off: looking through every grid
                // on the server is slow (it held the game up for a third of a second during a cinematic).
                bool had = serverTwins.TryGetValue(clientGrid, out Entity known);
                if (had && known != null && known.Data.Has<WorldTransform>()
                    && (ToVec(EntityTransformFunctions.GetWorldTransform(known.Data).Position) - at).Length < Math.Max(near, 500))
                    return known;
                // ...and a search that found nothing isn't tried again straight away.
                if (twinSearchedAt.TryGetValue(clientGrid, out double searched) && TimeSeconds - searched < 2 && TimeSeconds >= searched)
                    return had && known != null && known.Data.Has<WorldTransform>() ? known : null;
                if (twinSearchedAt.Count > 100) twinSearchedAt.Clear();
                twinSearchedAt[clientGrid] = TimeSeconds;
                var clientSession = CurrentSession();
                Entity best = null;
                double bestScore = double.MaxValue;
                lock (Worlds)
                {
                    foreach (var weak in Worlds)
                    {
                        if (!weak.TryGetTarget(out var world)) continue;
                        var session = world.OwnedSession;
                        if (session == null || ReferenceEquals(session, clientSession)) continue;
                        foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
                        {
                            if (e == null || !e.Data.Has<WorldTransform>()) continue;
                            double d = (ToVec(EntityTransformFunctions.GetWorldTransform(e.Data).Position) - at).Length;
                            if (d > near) continue;
                            var st = e.TryGet<CubeGridComponent>(default)?.CubeBlockStorage;
                            int n = st != null ? st.GetAllCubeBlocks().Length : 0;
                            double score = d + Math.Abs(n - blocks) * 5;
                            if (score < bestScore) { bestScore = score; best = e; }
                        }
                    }
                }
                if (serverTwins.Count > 100) serverTwins.Clear();
                serverTwins[clientGrid] = best;
                return best;
            }
            catch (Exception ex) { LogOnce("Analyser: server grid lookup failed: " + ex.Message); return null; }
        }

        /// <summary>A number from a float, double, int or the game's fixed-point type.</summary>
        private static double Number(object v)
        {
            switch (v)
            {
                case null: return 0;
                case float f: return f;
                case double d: return d;
                case int i: return i;
                case long l: return l;
            }
            var type = v.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>)) return 0;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if ((m.Name == "op_Explicit" || m.Name == "op_Implicit") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == type
                    && (m.ReturnType == typeof(double) || m.ReturnType == typeof(float)))
                    return Convert.ToDouble(m.Invoke(null, new[] { v }));
            return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) ? x : 0;
        }

        private static string ResourceName(object resourceType)
        {
            if (resourceType == null) return "resource";
            string n = null;
            try { n = Member(resourceType, "DebugNameShort") as string; } catch { /* below */ }
            if (string.IsNullOrWhiteSpace(n)) n = (Member(resourceType, "DebugName") ?? Member(resourceType, "Id") ?? resourceType)?.ToString() ?? "resource";
            int dot = n.LastIndexOfAny(new[] { '/', '.', ':' });
            return dot >= 0 && dot < n.Length - 1 ? n.Substring(dot + 1) : n;
        }

        private static readonly HashSet<string> loggedAnalyserTypes = new HashSet<string>();
        private readonly List<Entity> blockEntities = new List<Entity>();

        /// <summary>An entity and the entities under it (a few levels down).</summary>
        private static void CollectBlockEntities(Entity e, List<Entity> into, int depth)
        {
            if (e == null || into.Contains(e)) return;
            if (depth > 0 && e.TryGet<CubeGridComponent>(default) != null) return;   // a subgrid on a rotor or piston: its own grid
            into.Add(e);
            if (depth >= 4 || into.Count > 64) return;
            Span<Entity> kids;
            try { kids = HierarchyComponentExtensions.TryGetDirectChilds(e); }
            catch { return; }
            foreach (Entity k in kids) CollectBlockEntities(k, into, depth + 1);
        }

        /// <summary>Adds one block part (thruster, gyroscope, inventory, battery / tank, producer) to the stats. Returns the kind of block it makes this (null = nothing special).</summary>
        // The thruster part found on the block being read (AnalyseGrid clears these before each block).
        private static Vec3D? partThrustDir;
        private static double partThrustPower;

        private static string AddPart(ShipStats st, CubeBlockComponent b, object comp, List<string> seenTypes)
        {
            string tn = comp.GetType().Name;
            if (loggedAnalyserTypes.Count < 400 && loggedAnalyserTypes.Add(tn)) seenTypes.Add(tn);
            if (tn == "ThrusterComponent")
            {
                var td = Member(comp, "Definition");
                double power = Number(Member(td, "ThrustPower"));
                object dirObj = Member(td, "ThrustDirection");
                if (dirObj == null) return null;
                st.Thrusters++;
                var gridDir = b.BlockOrientation.TransformDirection((Base6Directions.Direction)Convert.ToInt32(dirObj));
                Vector3 v = Base6Directions.GetVector(gridDir);
                // (forward = -Z, backward = +Z, left = -X, right = +X, up = +Y, down = -Y)
                int slot = v.Z < -0.5f ? 0 : v.Z > 0.5f ? 1 : v.X < -0.5f ? 2 : v.X > 0.5f ? 3 : v.Y > 0.5f ? 4 : 5;
                st.Thrust[slot] += power;
                partThrustDir = new Vec3D(v.X, v.Y, v.Z);
                partThrustPower = power;
                return "Thrusters";
            }
            if (tn == "CockpitComponent" && st.CockpitForwardGrid == null)
            {
                // The first cockpit: its forward and up become the ship's (see AnalyseGrid).
                object orient = b.BlockOrientation;
                var fd = Member(orient, "Forward");
                var ud = Member(orient, "Up");
                if (fd != null && ud != null)
                {
                    Vector3 fv = Base6Directions.GetVector((Base6Directions.Direction)Convert.ToInt32(fd));
                    Vector3 uv = Base6Directions.GetVector((Base6Directions.Direction)Convert.ToInt32(ud));
                    st.CockpitForwardGrid = new Vec3D(fv.X, fv.Y, fv.Z);
                    st.CockpitUpGrid = new Vec3D(uv.X, uv.Y, uv.Z);
                }
                return null;
            }
            if (tn == "GyroscopeComponent")
            {
                st.Gyros++;
                st.GyroTorque += Number(Member(Member(comp, "_definition") ?? Member(comp, "Definition"), "MaxTorque"));
                return "Gyroscopes";
            }
            if (tn == "InventoryComponent")
            {
                var idef = Member(comp, "Definition");
                if (idef != null && Member(idef, "ExcludeFromCapacityStats") is bool ex && ex) return null;
                st.Inventories++;
                st.StorageMax += Number(Member(idef, "MaxMass"));
                st.StorageUsed += Number(Member(comp, "DynamicMass"));
                return "Cargo";
            }
            if (IsOrDerives(comp, "ResourceContainerComponent"))
            {
                // Batteries, hydrogen / oxygen tanks: how much they hold now and at most.
                var rt = Member(comp, "ResourceType") ?? Member(Member(comp, "_definition"), "ResourceType");
                string key = ResourceName(rt);
                st.StoredNow[key] = (st.StoredNow.TryGetValue(key, out double n) ? n : 0) + Number(Member(comp, "CurrentChargeValue"));
                double max = Number(Member(comp, "MaxChargeValue"));
                if (max <= 0) max = Number(Member(Member(comp, "_definition"), "MaxCapacity"));
                st.StoredMax[key] = (st.StoredMax.TryGetValue(key, out double m) ? m : 0) + max;
                st.Containers[key] = (st.Containers.TryGetValue(key, out int c) ? c : 0) + 1;
                if (!st.StoreUnits.ContainsKey(key)) st.StoreUnits[key] = UnitSymbol(Member(rt, "StorageUnits"));
                return "Batteries and tanks";
            }
            // Producers (power, hydrogen...): MaxOutput (solar, wind...) or MaxResourceProduction (engines, generators).
            var d = Member(comp, "Definition") ?? Member(comp, "_definition");
            if (d == null) return null;
            object resource = null; double most = 0;
            if (HasMember(d, "MaxOutput")) { resource = Member(d, "ResourceType"); most = Number(Member(d, "MaxOutput")); }
            else if (HasMember(d, "MaxResourceProduction")) { resource = Member(d, "ResourceProduced"); most = Number(Member(d, "MaxResourceProduction")); }
            else return null;
            if (most <= 0) return null;
            string k = ResourceName(resource);
            st.Output[k] = (st.Output.TryGetValue(k, out double o) ? o : 0) + most;
            if (!st.OutputUnits.ContainsKey(k)) st.OutputUnits[k] = UnitSymbol(Member(resource, "FlowRateUnits"));
            return "Generators";
        }

        /// <summary>Which kind wins when a block has several parts (a thruster with a small tank is a thruster).</summary>
        private static int KindRank(string kind)
        {
            switch (kind)
            {
                case "Thrusters": return 6;
                case "Gyroscopes": return 5;
                case "Generators": return 4;
                case "Batteries and tanks": return 3;
                case "Cargo": return 2;
                case "Other": return 1;
                default: return 0;
            }
        }

        /// <summary>One block type: the game's internal name (the key RPI values are kept by), how it's shown, and its icon.</summary>
        internal sealed class BlockEntry
        {
            public string Key, Name, Category, Group;
            public double Size;        // metres (its longest side)
            public object Icon;        // the game's icon handle (null = none)
            public object Image;       // the icon as a picture, once the menu has made it
            public bool ImageTried;
            public object Definition;  // the game's block definition
            /// <summary>What the block does, for judging a fair cost: mass, thrust, torque, power, storage...</summary>
            public readonly List<(string Label, string Text)> Stats = new List<(string, string)>();
            public double Mass, Thrust;   // kg, N (0 = not a thruster)
        }

        private static string Si(double v, string unit)
        {
            double a = Math.Abs(v);
            return a >= 1e6 ? $"{v / 1e6:0.##} M{unit}" : a >= 1e3 ? $"{v / 1e3:0.##} k{unit}" : $"{v:0.##} {unit}";
        }

        /// <summary>Fills in a block's stats from its definition and the definitions of its other parts.</summary>
        private static void FillStats(BlockEntry e, List<object> parts)
        {
            if (e.Stats.Count > 0) return;   // done already
            try
            {
                e.Mass = Number(Member(e.Definition, "Mass"));
                if (e.Mass > 0) e.Stats.Add(("Mass", $"{e.Mass:#,0.#} kg"));
                double pcu = Number(Member(e.Definition, "PCU"));
                if (pcu > 0) e.Stats.Add(("PCU", $"{pcu:0}"));
            }
            catch { /* no mass */ }
            if (parts == null) return;
            foreach (object d in parts)
            {
                if (d == null || ReferenceEquals(d, e.Definition)) continue;
                try
                {
                    if (HasMember(d, "ThrustPower"))
                    {
                        e.Thrust = Number(Member(d, "ThrustPower"));
                        if (e.Thrust > 0)
                        {
                            e.Stats.Add(("Thrust", Si(e.Thrust, "N")));
                            if (e.Mass > 0) e.Stats.Add(("Thrust / weight", $"{e.Thrust / e.Mass:0.#} N per kg"));
                        }
                        double use = Number(Member(d, "ResourcesRequiredToThrust"));
                        if (use > 0)
                        {
                            e.Stats.Add(("Uses at full thrust", $"{use:#,0.##}"));
                            if (e.Thrust > 0) e.Stats.Add(("Thrust / use", $"{e.Thrust / use:0.#} N each"));
                        }
                    }
                    else if (HasMember(d, "MaxTorque"))
                    {
                        double torque = Number(Member(d, "MaxTorque"));
                        if (torque > 0)
                        {
                            e.Stats.Add(("Max torque", Si(torque, "N m")));
                            if (e.Mass > 0) e.Stats.Add(("Torque / weight", Si(torque / e.Mass, "N m") + " per kg"));
                        }
                    }
                    else if (HasMember(d, "MaxCapacity"))
                    {
                        object rt = Member(d, "ResourceType");
                        string what = ResourceName(rt);
                        double cap = Number(Member(d, "MaxCapacity"));
                        if (cap > 0) e.Stats.Add((what + " stored", $"{cap:#,0.##} {UnitSymbol(Member(rt, "StorageUnits"))}".Trim()));
                        double give = Number(Member(d, "MaxDischargeRate"));
                        if (give > 0) e.Stats.Add((what + " max output", $"{give:#,0.##} {UnitSymbol(Member(rt, "FlowRateUnits"))}".Trim()));
                    }
                    else if (HasMember(d, "MaxOutput") || HasMember(d, "MaxResourceProduction"))
                    {
                        bool plain = HasMember(d, "MaxOutput");
                        object rt = Member(d, plain ? "ResourceType" : "ResourceProduced");
                        double most = Number(Member(d, plain ? "MaxOutput" : "MaxResourceProduction"));
                        if (most > 0) e.Stats.Add((ResourceName(rt) + " generated", $"{most:#,0.##} {UnitSymbol(Member(rt, "FlowRateUnits"))}".Trim()));
                    }
                    else if (d.GetType().Name == "InventoryDefinition")
                    {
                        double cargo = Number(Member(d, "MaxMass"));
                        if (cargo > 0) e.Stats.Add(("Cargo", $"{cargo:#,0.#} kg"));
                    }
                }
                catch { /* this part has nothing to show */ }
            }
        }

        private static readonly Dictionary<object, BlockEntry> blockEntries = new Dictionary<object, BlockEntry>();
        private static List<BlockEntry> allBlocks;

        private static object FirstOf(object list)
        {
            try { if (list is System.Collections.IEnumerable e) foreach (object x in e) return x; }
            catch { /* an empty game array */ }
            return null;
        }

        private static string LocText(object locKey)
        {
            try
            {
                string text = locKey?.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch { return null; }
        }

        /// <summary>A block type from its definition (kept, so each is only worked out once).</summary>
        internal static BlockEntry BlockOf(object def)
        {
            if (def == null) return null;
            lock (blockEntries)
            {
                if (blockEntries.TryGetValue(def, out var known)) return known;
                string key = null;
                try { key = Member(def, "DebugNameShort") as string; } catch { /* below */ }
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = def.ToString() ?? "";
                    int colon = key.LastIndexOf(" : ", StringComparison.Ordinal);
                    if (colon >= 0) key = key.Substring(colon + 3);
                    try { key = Path.GetFileNameWithoutExtension(key); } catch { /* keep */ }
                }
                BlockEntry entry = null;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    entry = new BlockEntry { Key = key.Trim(), Definition = def };
                    try { entry.Size = Number(Member(def, "DisplaySize")); } catch { /* unknown */ }
                    try
                    {
                        object ui = Member(def, "UIData");
                        entry.Name = LocText(Member(ui, "Name"));
                        entry.Icon = Member(ui, "Icon");
                    }
                    catch { /* no name / icon */ }
                    try
                    {
                        // Its place in the build menu: block kind > group (tile) > subcategory > category.
                        object kind = Member(def, "BlockKind");
                        entry.Group = LocText(Member(kind, "Name"));
                        object sub = FirstOf(Member(FirstOf(Member(kind, "BlockGroups")), "Subcategories"));
                        entry.Category = LocText(Member(FirstOf(Member(sub, "Categories")), "Name")) ?? LocText(Member(sub, "Name"));
                    }
                    catch { /* not in the build menu */ }
                    if (entry.Name == null) entry.Name = entry.Group ?? entry.Key.Replace('_', ' ');
                    if (entry.Category == null) entry.Category = "Other";
                }
                blockEntries[def] = entry;
                return entry;
            }
        }

        /// <summary>Every block type in the game (read once), sorted by name then size.</summary>
        private static readonly object allBlocksLock = new object();

        internal static List<BlockEntry> AllBlocks()
        {
            if (allBlocks != null) return allBlocks;
            // (Read ahead of time on a background thread too, for the RPI in the game's panels: one at a time.)
            lock (allBlocksLock)
            {
                if (allBlocks != null) return allBlocks;
                return ReadAllBlocks();
            }
        }

        private static List<BlockEntry> ReadAllBlocks()
        {
            var list = new List<BlockEntry>();
            try
            {
                var managerType = AccessTools.TypeByName("Keen.VRage.Library.Definitions.DefinitionManager");
                var singleton = AccessTools.TypeByName("Keen.VRage.Library.Utils.Singleton`1")?.MakeGenericType(managerType);
                object manager = singleton?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                MethodInfo getAll = null;
                foreach (var m in managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (m.Name == "GetDefinitionsOfTypeAndDescendants" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { getAll = m; break; }
                if (manager == null || getAll == null) { PluginLog.Info("RPI designer: the game's block list wasn't found"); return list; }
                // A block is made of several parts (thruster, gyroscope, battery...): each block definition's other parts.
                var partsOf = new Dictionary<object, List<object>>();
                try
                {
                    var compositeType = AccessTools.TypeByName("Keen.VRage.DCS.Definitions.EntityCompositeDefinition");
                    if (compositeType != null && getAll.MakeGenericMethod(compositeType).Invoke(manager, null) is System.Collections.IEnumerable composites)
                        foreach (object composite in composites)
                        {
                            if (!(Member(composite, "Definitions") is System.Collections.IEnumerable inside)) continue;
                            var parts = new List<object>();
                            try { foreach (object part in inside) if (part != null) parts.Add(part); } catch { continue; }
                            foreach (object part in parts)
                                if (part is CubeBlockDefinition && !partsOf.ContainsKey(part)) partsOf[part] = parts;
                        }
                }
                catch (Exception statsError) { PluginLog.Info("RPI designer: block stats not read: " + (statsError.InnerException ?? statsError).Message); }
                var seen = new HashSet<string>();
                if (getAll.MakeGenericMethod(typeof(CubeBlockDefinition)).Invoke(manager, null) is System.Collections.IEnumerable defs)
                    foreach (object def in defs)
                    {
                        var entry = BlockOf(def);
                        if (entry == null || !seen.Add(entry.Key)) continue;
                        FillStats(entry, partsOf.TryGetValue(def, out var its) ? its : null);
                        list.Add(entry);
                    }
                PluginLog.Info($"RPI designer: stats found for {list.Count(x => x.Stats.Count > 2)} of {list.Count} block types");
                list.Sort((x, y) =>
                {
                    int c = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
                    if (c == 0) c = x.Size.CompareTo(y.Size);
                    return c != 0 ? c : string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
                });
                PluginLog.Info($"RPI designer: {list.Count} block types, categories: " + string.Join(", ", list.Select(x => x.Category).Distinct().OrderBy(x => x)));
                if (list.Count > 0) PluginLog.Info("RPI designer: e.g. " + string.Join("; ", list.Take(8).Select(x => $"{x.Key} = {x.Name} ({x.Size:0.##} m, {x.Category} / {x.Group}, icon {(x.Icon != null ? "yes" : "no")})")));
                if (list.Count > 0) allBlocks = list;   // (none yet: the game's still loading; read again next time)
            }
            catch (Exception e) { PluginLog.Info("RPI designer: couldn't read the game's blocks: " + (e.InnerException ?? e).Message); }
            return list;
        }

        public ShipStats AnalyseGrid(object gridObj)
        {
            try
            {
                if (!(gridObj is Entity grid) || !grid.Data.Has<WorldTransform>()) return null;
                var gc = grid.TryGet<CubeGridComponent>(default);
                var storage = gc?.CubeBlockStorage;
                if (storage == null) return null;
                WorldTransform t = EntityTransformFunctions.GetWorldTransform(grid.Data);
                var st = new ShipStats { Grid = grid };
                if (TryGridBox(grid, ToVec(t.Position), out GridBox gb, out _))
                {
                    st.Name = gb.Name;
                    st.SizeX = gb.Width; st.SizeY = gb.Height; st.SizeZ = gb.Depth;
                    st.BoxCenter = gb.Center;
                }
                else st.BoxCenter = ToVec(t.Position);
                try { string dn = Raw(gc.DisplayName); if (!string.IsNullOrWhiteSpace(dn)) st.Name = dn.Trim(); } catch { /* keep the debug name */ }
                st.Right = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                st.Up = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                st.Forward = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, -1), t));   // the game's forward is -Z
                double blockMass = 0;
                foreach (var b in storage.GetAllCubeBlocks())
                {
                    if (b == null) continue;
                    st.Blocks++;
                    try
                    {
                        var def = b.Definition;
                        if (def != null) { st.Pcu += def.PCU; blockMass += def.Mass; }
                    }
                    catch (Exception ex) { LogOnce("Analyser: block definition: " + ex.Message); }
                }

                // The parts: from the server's copy of the grid when there is one (singleplayer), else the client's.
                Entity source = ServerTwin(grid, ToVec(t.Position), st.Blocks);
                st.FromServer = source != null;
                if (source == null) source = grid;
                var sourceStorage = source.TryGet<CubeGridComponent>(default)?.CubeBlockStorage ?? storage;
                var seenTypes = new List<string>();
                st.GridOrigin = ToVec(t.Position);
                st.GridX = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
                st.GridY = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
                st.GridZ = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
                foreach (var b in sourceStorage.GetAllCubeBlocks())
                {
                    if (b == null) continue;
                    partThrustDir = null; partThrustPower = 0;
                    Entity ent = null;
                    try { ent = b.Entity; } catch (Exception ex) { LogOnce("Analyser: block entity: " + ex.Message); }
                    string kind = null;
                    if (ent != null)
                    {
                        // A block's parts can also sit on entities under the block's own one.
                        blockEntities.Clear();
                        CollectBlockEntities(ent, blockEntities, 0);
                        foreach (Entity part in blockEntities)
                        {
                            System.Collections.IEnumerable comps = null;
                            try { comps = Member(part, "Components") as System.Collections.IEnumerable; }
                            catch (Exception ex) { LogOnce("Analyser: block components: " + ex.Message); }
                            if (comps == null) continue;
                            foreach (object comp in comps)
                            {
                                if (comp == null) continue;
                                try
                                {
                                    string partKind = AddPart(st, b, comp, seenTypes);
                                    if (KindRank(partKind) > KindRank(kind)) kind = partKind;
                                }
                                catch (Exception ex) { LogOnce($"Analyser: {comp.GetType().Name}: {ex.Message}"); }
                            }
                        }
                    }
                    // Block types, for the RPI count.
                    try
                    {
                        var type = BlockOf(b.Definition);
                        if (type != null)
                        {
                            st.BlockCounts[type.Key] = (st.BlockCounts.TryGetValue(type.Key, out int have) ? have : 0) + 1;
                            double massOf = b.Definition?.Mass ?? 0;
                            st.BlockMass[type.Key] = (st.BlockMass.TryGetValue(type.Key, out double had) ? had : 0) + massOf;
                            if (!st.BlockInfo.TryGetValue(type.Key, out var info) || KindRank(kind) > KindRank(info.Kind))
                                st.BlockInfo[type.Key] = (kind ?? "Other", type.Size, type.Size > 0 ? $"{type.Name} {type.Size:0.##} m" : type.Name);
                            // Where the block is on the grid (for the analyser's drawings on the ship).
                            if (ent != null && ent.Data.Has<WorldTransform>())
                            {
                                Vec3D rel = ToVec(EntityTransformFunctions.GetWorldTransform(ent.Data).Position) - st.GridOrigin;
                                st.Spots.Add(new BlockSpot
                                {
                                    Local = new Vec3D(Vec3D.Dot(rel, st.GridX), Vec3D.Dot(rel, st.GridY), Vec3D.Dot(rel, st.GridZ)),
                                    Key = type.Key, Kind = kind ?? "Other", Mass = massOf, Size = type.Size,
                                    ThrustDir = partThrustDir, ThrustPower = partThrustPower,
                                });
                            }
                        }
                    }
                    catch (Exception ex) { LogOnce("Analyser: block type: " + ex.Message); }
                }
                if (seenTypes.Count > 0) PluginLog.Info($"Analyser: block components seen ({(st.FromServer ? "server" : "client")}): " + string.Join(", ", seenTypes));
                UseCockpitAxes(st, grid, t);
                try
                {
                    if (grid.Data.TryGet(out Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData gravity))
                    {
                        var g = new Vec3D(gravity.GravitySum.X, gravity.GravitySum.Y, gravity.GravitySum.Z);
                        if (g.Length > 0.05) { st.Gravity = g.Length; st.GravityDown = g.Normalized(); }
                    }
                }
                catch (Exception ex) { LogOnce("Analyser: gravity: " + ex.Message); }
                // Mass and centre of mass from the physics when it has them, else the blocks' own masses.
                bool physics = false;
                try
                {
                    if (grid.Data.TryGet(out RigidBodyMassProperties mp) && mp.Mass > 0)
                    {
                        st.Mass = mp.Mass;
                        st.CenterOfMass = ToVec(t.Position) + ToVec(WorldTransform.TransformDirection(new Vector3D(mp.CenterOfMass.X, mp.CenterOfMass.Y, mp.CenterOfMass.Z), t));
                        physics = true;
                    }
                }
                catch (Exception ex) { LogOnce("Analyser: mass properties: " + ex.Message); }
                if (!physics)
                {
                    st.Mass = blockMass;
                    st.CenterOfMass = st.BoxCenter;
                }
                st.ComOffset = (st.CenterOfMass - st.BoxCenter).Length;
                return st;
            }
            catch (Exception e) { LogOnce("Analyser: couldn't analyse the grid: " + e.Message); return null; }
        }

        /// <summary>
        /// Forward / backward (and the rest) from the cockpit rather than the grid's own axes, which can point any
        /// way depending on how the ship was built. The seat you're in when it's your own ship, else the first
        /// cockpit found. The thrust each way is moved to match, and so are the analyser's arrows.
        /// </summary>
        private void UseCockpitAxes(ShipStats st, Entity grid, WorldTransform t)
        {
            Vec3D gx = ToVec(WorldTransform.TransformDirection(new Vector3D(1, 0, 0), t));
            Vec3D gy = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 1, 0), t));
            Vec3D gz = ToVec(WorldTransform.TransformDirection(new Vector3D(0, 0, 1), t));
            Vec3D Snap(Vec3D v)
            {
                // The nearest of the grid's six directions.
                double ax = Math.Abs(v.X), ay = Math.Abs(v.Y), az = Math.Abs(v.Z);
                return ax >= ay && ax >= az ? new Vec3D(Math.Sign(v.X), 0, 0) : ay >= az ? new Vec3D(0, Math.Sign(v.Y), 0) : new Vec3D(0, 0, Math.Sign(v.Z));
            }
            Vec3D? f = st.CockpitForwardGrid, u = st.CockpitUpGrid;
            try
            {
                if (ReferenceEquals(grid, OwnGrid) && TryGetSeatPose(out _, out Vec3D seatF, out Vec3D seatU))
                {
                    f = Snap(new Vec3D(Vec3D.Dot(seatF, gx), Vec3D.Dot(seatF, gy), Vec3D.Dot(seatF, gz)));
                    u = Snap(new Vec3D(Vec3D.Dot(seatU, gx), Vec3D.Dot(seatU, gy), Vec3D.Dot(seatU, gz)));
                }
            }
            catch { /* the first cockpit then */ }
            if (f == null || u == null || Math.Abs(Vec3D.Dot(f.Value, u.Value)) > 0.1) return;
            Vec3D fw = f.Value, up = u.Value, right = Vec3D.Cross(fw, up);
            // The thrust was added up along the grid's axes: -Z, +Z, -X, +X, +Y, -Y. Put each into the cockpit's slot.
            Vec3D[] gridWays = { new Vec3D(0, 0, -1), new Vec3D(0, 0, 1), new Vec3D(-1, 0, 0), new Vec3D(1, 0, 0), new Vec3D(0, 1, 0), new Vec3D(0, -1, 0) };
            var raw = (double[])st.Thrust.Clone();
            Array.Clear(st.Thrust, 0, 6);
            for (int i = 0; i < 6; i++)
            {
                Vec3D v = gridWays[i];
                int slot = Vec3D.Dot(v, fw) > 0.5 ? 0 : Vec3D.Dot(v, fw) < -0.5 ? 1
                         : Vec3D.Dot(v, right) < -0.5 ? 2 : Vec3D.Dot(v, right) > 0.5 ? 3
                         : Vec3D.Dot(v, up) > 0.5 ? 4 : 5;
                st.Thrust[slot] += raw[i];
            }
            Vec3D World(Vec3D v) => gx * v.X + gy * v.Y + gz * v.Z;
            st.Forward = World(fw).Normalized();
            st.Up = World(up).Normalized();
            st.Right = World(right).Normalized();
            st.CockpitAxes = true;
        }

        public bool TryGetSeatPose(out Vec3D position, out Vec3D forward, out Vec3D up)
        {
            position = forward = up = Vec3D.Zero;
            try
            {
                if (ownGrid == null || !ownGrid.Data.Has<WorldTransform>()) return false;
                Entity seat = ControlledEntity()?.TryGet<SeatComponent>(default)?.Entity;
                WorldTransform t = seat != null && seat.Data.Has<WorldTransform>() ? EntityTransformFunctions.GetWorldTransform(seat.Data) : EntityTransformFunctions.GetWorldTransform(ownGrid.Data);
                position = ToVec(t.Position);
                forward = V(t.Orientation.GetForward());
                up = V(t.Orientation.GetUp());
                if (seat != null) PilotAxes(seat, ref forward, ref up);
                return true;
            }
            catch (Exception ex) { LogOnce("Seat pose failed: " + ex.Message); return false; }
        }

        private PropertyInfo seatPilotProperty;
        private bool seatPilotLooked;
        private readonly HashSet<string> seatFlipLogged = new HashSet<string>();

        /// <summary>
        /// Some cockpit blocks' own frame doesn't match the way the pilot sits (their "up" points at the floor,
        /// say), which made the roll indicator read upside down. The pilot sitting in the seat is placed the
        /// right way, so the seat's axes are swapped round to the ones nearest the pilot's own forward and up.
        /// </summary>
        private void PilotAxes(Entity seat, ref Vec3D forward, ref Vec3D up)
        {
            try
            {
                Vec3D f = forward.Normalized(), u = up.Normalized(), r = Vec3D.Cross(f, u);
                // Up becomes whichever of the seat's up, down, left or right is nearest the wanted way up
                // (the seat's forward is kept: pitch read right on every ship).
                Vec3D[] axes = { u, u * -1, r, r * -1 };
                Vec3D Nearest(Vec3D want, out double dot)
                {
                    Vec3D best = axes[0];
                    dot = double.MinValue;
                    foreach (var a in axes) { double d = Vec3D.Dot(a, want); if (d > dot) { dot = d; best = a; } }
                    return best;
                }

                // 1. The cockpit block's own way up, as it was placed on the grid (the way the builder set it).
                Vec3D? blockUp = null, blockFwd = null;
                var cb = seat.TryGet<CubeBlockComponent>(default);
                Entity gridEntity = cb?.Grid?.Entity;
                if (cb != null && gridEntity != null && gridEntity.Data.Has<WorldTransform>())
                {
                    object orient = cb.BlockOrientation;
                    var fd = Member(orient, "Forward");
                    var ud = Member(orient, "Up");
                    if (fd != null && ud != null)
                    {
                        WorldTransform gt = EntityTransformFunctions.GetWorldTransform(gridEntity.Data);
                        Vector3 fv = Base6Directions.GetVector((Base6Directions.Direction)Convert.ToInt32(fd));
                        Vector3 uv = Base6Directions.GetVector((Base6Directions.Direction)Convert.ToInt32(ud));
                        blockFwd = ToVec(WorldTransform.TransformDirection(new Vector3D(fv.X, fv.Y, fv.Z), gt)).Normalized();
                        blockUp = ToVec(WorldTransform.TransformDirection(new Vector3D(uv.X, uv.Y, uv.Z), gt)).Normalized();
                    }
                }

                // 2. The pilot sitting in it (if the game says who): their way up.
                Vec3D? pilotUp = null;
                var seatComp = seat.TryGet<SeatComponent>(default);
                if (seatComp != null)
                {
                    if (!seatPilotLooked)
                    {
                        seatPilotLooked = true;
                        seatPilotProperty = seatComp.GetType().GetProperty("Pilot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    object pv = seatPilotProperty?.GetValue(seatComp);
                    Entity pilot = pv as Entity ?? pv?.GetType().GetProperty("Entity")?.GetValue(pv) as Entity;
                    if (pilot != null && pilot.Data.Has<WorldTransform>())
                    {
                        WorldTransform pt = EntityTransformFunctions.GetWorldTransform(pilot.Data);
                        bool near = !seat.Data.Has<WorldTransform>() || (ToVec(pt.Position) - ToVec(EntityTransformFunctions.GetWorldTransform(seat.Data).Position)).Length <= 6;
                        Vec3D pu = V(pt.Orientation.GetUp()).Normalized();
                        if (near && pu.Length > 0.5) pilotUp = pu;
                    }
                }

                // The cockpit block's own frame, as placed by the builder, whenever it can be read: the seat's frame can be
                // turned any way inside the block (upside down on one ship, nose to the sky on another), the block's can't.
                // Otherwise the seat's forward, with the pilot's way up.
                Vec3D nf = f, nu = u;
                string source = "seat";
                double dot;
                if (blockUp.HasValue && blockFwd.HasValue && Math.Abs(Vec3D.Dot(blockUp.Value, blockFwd.Value)) < 0.1)
                {
                    nf = blockFwd.Value;
                    nu = blockUp.Value;
                    source = "cockpit block";
                }
                else if (pilotUp.HasValue && Math.Abs(Vec3D.Dot(pilotUp.Value, f)) < 0.7)
                {
                    var pick = Nearest(pilotUp.Value, out dot);
                    if (dot > 0.8) { nu = pick; source = "pilot"; }
                }

                string name = ShipName ?? "?";
                if (seatFlipLogged.Add(name))
                    PluginLog.Info($"Seat on {name}: facing from the {source}"
                        + (Vec3D.Dot(nf, f) < 0.99 || Vec3D.Dot(nu, u) < 0.99 ? " (the seat's own frame is turned: corrected)" : "")
                        + $". Seat vs block: forward {(blockFwd.HasValue ? Vec3D.Dot(blockFwd.Value, f).ToString("0.00") : "?")}, up {(blockUp.HasValue ? Vec3D.Dot(blockUp.Value, u).ToString("0.00") : "?")}; "
                        + $"pilot up vs block up {(pilotUp.HasValue && blockUp.HasValue ? Vec3D.Dot(pilotUp.Value, blockUp.Value).ToString("0.00") : "?")}");
                forward = nf;
                up = nu;
            }
            catch (Exception e) { LogOnce("Seat axes: " + e.Message); }
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
                var soundClock = System.Diagnostics.Stopwatch.StartNew();
                playOneShot?.Invoke(null, new object[] { eventPath });
                PluginLog.Slow("Sound " + eventPath, soundClock.Elapsed.TotalMilliseconds);
            }
            catch (Exception e) { LogOnce("Sound failed: " + (e.InnerException?.Message ?? e.Message)); }
        }

        public void PlayWav(string name, double volume) => WavPlayer.Play(name, volume);

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

        // ---- The game's 3D UI layer (UI3DSessionComponent: build previews, symmetry planes). It has its own shader,
        // thick lines and a colour per corner, but only takes draws between its update and render jobs: so ours are
        // kept, and drawn from a patch just before it hands the frame to the renderer.
        private struct GlowShape { public bool Quad, Depth, Hidden, Ribbon; public Vector3D A, B, C, D; public ColorSRGB Ca, Cb, Cc, Cd; public float Thick; }
        private static readonly object glowLock = new object();
        private static List<GlowShape> glowBuilding = new List<GlowShape>();   // this frame's, being added to
        private static List<GlowShape> glowShown = new List<GlowShape>();      // the last finished frame's, drawn by the patch
        private static long glowShownAt;
        private static MethodInfo ui3dLine, ui3dQuad;
        // The layer's own full quad method (camera-relative corners, a texture spot and a dash mark per corner). The
        // simple one maps each quad edge to edge, and the layer's shader fades every quad at its edges (the tiled
        // look of the gate rims, which is wanted there). With every corner on the middle of the texture there's no
        // edge: used for the racing line's solid strips.
        private static MethodInfo ui3dQuadFull;
        private static FieldInfo ui3dCamera;
        private static object[] ui3dFullArgs;
        // The layer's own line method, told NOT to keep a constant thickness on screen: the simple one multiplies the
        // thickness by the distance, so a thickness given in metres (already growing with distance) made far-off
        // lines enormous: distant markers turned into spiky blobs. Through this one, thickness is plain metres.
        private static MethodInfo ui3dLineMetres;
        private static readonly object[] ui3dLineMetresArgs = new object[7];
        private static object ui3dInFront, ui3dBoth, ui3dBehind;
        private static volatile object ui3dInstance;
        private static int ui3dErrors;
        private static bool ui3dPatched;

        public bool GlowLayerReady => ui3dPatched && ui3dInstance != null && ui3dErrors < 5;

        public void DrawGlowLine(Vec3D from, HudColor fromColour, Vec3D to, HudColor toColour, double thickness, bool depth)
        {
            if (Bad(from) || Bad(to) || glowBuilding.Count >= 20000) return;
            glowBuilding.Add(new GlowShape
            {
                Depth = depth, Thick = (float)thickness,
                A = new Vector3D(from.X, from.Y, from.Z), B = new Vector3D(to.X, to.Y, to.Z), Ca = ToColor(fromColour), Cb = ToColor(toColour),
            });
        }

        public void DrawGlowRibbon(Vec3D from, Vec3D to, HudColor colour, double thickness, bool depth)
        {
            if (Bad(from) || Bad(to) || glowBuilding.Count >= 20000) return;
            glowBuilding.Add(new GlowShape
            {
                Ribbon = true, Depth = depth, Thick = (float)thickness,
                A = new Vector3D(from.X, from.Y, from.Z), B = new Vector3D(to.X, to.Y, to.Z), Ca = ToColor(colour), Cb = ToColor(colour),
            });
        }

        public void DrawGlowLineHidden(Vec3D from, HudColor fromColour, Vec3D to, HudColor toColour, double thickness)
        {
            if (Bad(from) || Bad(to) || glowBuilding.Count >= 20000) return;
            glowBuilding.Add(new GlowShape
            {
                Hidden = true, Thick = (float)thickness,
                A = new Vector3D(from.X, from.Y, from.Z), B = new Vector3D(to.X, to.Y, to.Z), Ca = ToColor(fromColour), Cb = ToColor(toColour),
            });
        }

        public void DrawGlowQuad(Vec3D a, HudColor ca, Vec3D b, HudColor cb, Vec3D c, HudColor cc, Vec3D d, HudColor cd, bool depth)
        {
            if (Bad(a) || Bad(b) || Bad(c) || Bad(d) || glowBuilding.Count >= 20000) return;
            glowBuilding.Add(new GlowShape
            {
                Quad = true, Depth = depth,
                A = new Vector3D(a.X, a.Y, a.Z), B = new Vector3D(b.X, b.Y, b.Z), C = new Vector3D(c.X, c.Y, c.Z), D = new Vector3D(d.X, d.Y, d.Z),
                Ca = ToColor(ca), Cb = ToColor(cb), Cc = ToColor(cc), Cd = ToColor(cd),
            });
        }

        /// <summary>End of our frame: what was added becomes what the patch draws (until the next frame's is ready).</summary>
        private void PublishGlow()
        {
            lock (glowLock)
            {
                var done = glowBuilding;
                glowBuilding = glowShown;
                glowBuilding.Clear();
                glowShown = done;
                glowShownAt = Environment.TickCount64;
            }
        }

        /// <summary>Finds the 3D UI layer's drawing methods and patches the job that sends it to the renderer.</summary>
        internal static void ApplyUi3d(Harmony harmony)
        {
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                Type layer = AccessTools.TypeByName("Keen.Game2.Client.GameSystems.UI3D.UI3DSessionComponent");
                Type drawing = AccessTools.TypeByName("Keen.Game2.Client.GameSystems.UI3D.UI3DDefaultRendering");
                Type depthType = layer?.GetNestedType("DepthBehavior", BindingFlags.Public | BindingFlags.NonPublic);
                if (layer == null || drawing == null || depthType == null) { PluginLog.Info("3D UI layer: not found (air gate glow uses the plain drawing)"); return; }
                ui3dInFront = Enum.Parse(depthType, "InFront");
                ui3dBoth = Enum.Parse(depthType, "Both");
                try { ui3dBehind = Enum.Parse(depthType, "Behind"); } catch { ui3dBehind = null; }
                foreach (var m in drawing.GetMethods(any))
                {
                    var p = m.GetParameters();
                    // DrawLine(layer, from, colour, to, colour, thickness, depth) and DrawQuad(layer, 4 x (corner, colour), depth)
                    if (m.Name == "DrawLine" && p.Length == 7 && p[1].ParameterType == typeof(Vector3D) && p[2].ParameterType == typeof(ColorSRGB)
                        && p[3].ParameterType == typeof(Vector3D) && p[5].ParameterType == typeof(float)) ui3dLine = m;
                    if (m.Name == "DrawQuad" && p.Length == 10 && p[1].ParameterType == typeof(Vector3D) && p[2].ParameterType == typeof(ColorSRGB)) ui3dQuad = m;
                }
                ui3dCamera = layer.GetField("_cameraTransform", any);
                if (ui3dCamera != null && ui3dCamera.FieldType != typeof(Vector3D)) ui3dCamera = null;
                foreach (var m in layer.GetMethods(any))
                {
                    var p = m.GetParameters();
                    if (m.Name == "DrawLine" && !m.IsStatic && p.Length == 7 && p[0].ParameterType == typeof(Vector3) && p[1].ParameterType == typeof(ColorSRGB)
                        && p[2].ParameterType == typeof(Vector3) && p[4].ParameterType == typeof(float) && p[6].ParameterType == typeof(bool) && ui3dCamera != null)
                    {
                        ui3dLineMetres = m;
                        ui3dLineMetresArgs[6] = false;   // not a constant thickness on screen: metres
                        PluginLog.Info("3D UI layer: line thickness in metres (the layer's own DrawLine)");
                    }
                }
                foreach (var m in layer.GetMethods(any))
                {
                    var p = m.GetParameters();
                    if (m.Name != "DrawQuad" || m.IsStatic || p.Length != 19 || p[0].ParameterType != typeof(Vector3) || p[1].ParameterType != typeof(ColorSRGB)
                        || p[2].ParameterType != typeof(Vector2) || p[3].ParameterType != typeof(Vector2) || p[16].ParameterType != typeof(float)) continue;
                    if (ui3dCamera == null) break;
                    ui3dQuadFull = m;
                    ui3dFullArgs = new object[19];
                    for (int corner = 0; corner < 4; corner++)
                    {
                        ui3dFullArgs[corner * 4 + 2] = new Vector2(0.5f, 0.5f);   // the middle of the texture: no edge fade
                        ui3dFullArgs[corner * 4 + 3] = new Vector2(1f, 1f);       // (not dashed, not dotted)
                    }
                    ui3dFullArgs[16] = p[16].HasDefaultValue && p[16].DefaultValue is float size ? size : 1f;
                    ui3dFullArgs[18] = p[18].HasDefaultValue && p[18].DefaultValue != null ? Enum.ToObject(p[18].ParameterType, p[18].DefaultValue) : Enum.ToObject(p[18].ParameterType, 0);
                    PluginLog.Info($"3D UI layer: solid strips through the layer's full DrawQuad (size {ui3dFullArgs[16]}, shade {ui3dFullArgs[18]})");
                    break;
                }
                if (ui3dLine == null || ui3dQuad == null) { PluginLog.Info($"3D UI layer: drawing methods not found (line {ui3dLine != null}, quad {ui3dQuad != null})"); return; }
                // The job that submits the layer to the renderer (tagged [RenderJob]); failing that, its update job.
                MethodInfo render = null, update = null;
                foreach (var m in layer.GetMethods(any))
                {
                    if (m.IsStatic || m.IsAbstract) continue;
                    object[] tags;
                    try { tags = m.GetCustomAttributes(false); } catch { continue; }
                    foreach (object tag in tags)
                    {
                        string name = tag.GetType().Name;
                        if (name == "RenderJob" || name == "RenderJobAttribute") render = m;
                        else if (name == "UpdateJob" || name == "UpdateJobAttribute") update = m;
                    }
                }
                if (render != null) harmony.Patch(render, prefix: new HarmonyMethod(typeof(Se2Bridge), nameof(Ui3dDrawNow)));
                else if (update != null) harmony.Patch(update, postfix: new HarmonyMethod(typeof(Se2Bridge), nameof(Ui3dDrawNow)));
                else { PluginLog.Info("3D UI layer: no render or update job found to draw from"); return; }
                ui3dPatched = true;
                MakeFastCalls(layer);
                PluginLog.Info("3D UI layer: drawing from " + (render != null ? "before its render job (" + render.Name + ")" : "after its update job (" + update.Name + ")"));
            }
            catch (Exception e) { PluginLog.Info("3D UI layer: not set up: " + (e.InnerException ?? e).Message); }
        }

        // The two calls made most (every piece of the racing line, every line of a gate) as compiled delegates: a
        // reflection call boxes every argument, hundreds of times a frame. Null if they couldn't be made (then the
        // reflection calls are used).
        private static Action<object, Vector3, ColorSRGB, Vector3, ColorSRGB, float, object> ui3dLineFast;
        private static Action<object, Vector3, ColorSRGB, Vector3, ColorSRGB, Vector3, ColorSRGB, Vector3, ColorSRGB, object> ui3dStripFast;

        private static void MakeFastCalls(Type layer)
        {
            try
            {
                var self = System.Linq.Expressions.Expression.Parameter(typeof(object), "layer");
                var depth = System.Linq.Expressions.Expression.Parameter(typeof(object), "depth");
                System.Linq.Expressions.ParameterExpression Corner(string name) => System.Linq.Expressions.Expression.Parameter(typeof(Vector3), name);
                System.Linq.Expressions.ParameterExpression Ink(string name) => System.Linq.Expressions.Expression.Parameter(typeof(ColorSRGB), name);
                var on = System.Linq.Expressions.Expression.Convert(self, layer);
                if (ui3dLineMetres != null)
                {
                    var p = ui3dLineMetres.GetParameters();
                    var a = Corner("a"); var b = Corner("b"); var ca = Ink("ca"); var cb = Ink("cb");
                    var thick = System.Linq.Expressions.Expression.Parameter(typeof(float), "thick");
                    var call = System.Linq.Expressions.Expression.Call(on, ui3dLineMetres, a, ca, b, cb, thick,
                        System.Linq.Expressions.Expression.Convert(depth, p[5].ParameterType), System.Linq.Expressions.Expression.Constant(false));
                    ui3dLineFast = System.Linq.Expressions.Expression.Lambda<Action<object, Vector3, ColorSRGB, Vector3, ColorSRGB, float, object>>(call, self, a, ca, b, cb, thick, depth).Compile();
                }
                if (ui3dQuadFull != null)
                {
                    var p = ui3dQuadFull.GetParameters();
                    var middle = System.Linq.Expressions.Expression.Constant(new Vector2(0.5f, 0.5f));
                    var plain = System.Linq.Expressions.Expression.Constant(new Vector2(1f, 1f));
                    var a = Corner("a"); var b = Corner("b"); var c = Corner("c"); var d = Corner("d");
                    var ca = Ink("ca"); var cb = Ink("cb"); var cc = Ink("cc"); var cd = Ink("cd");
                    var call = System.Linq.Expressions.Expression.Call(on, ui3dQuadFull,
                        a, ca, middle, plain, b, cb, middle, plain, c, cc, middle, plain, d, cd, middle, plain,
                        System.Linq.Expressions.Expression.Constant((float)ui3dFullArgs[16]),
                        System.Linq.Expressions.Expression.Convert(depth, p[17].ParameterType),
                        System.Linq.Expressions.Expression.Constant(ui3dFullArgs[18], p[18].ParameterType));
                    ui3dStripFast = System.Linq.Expressions.Expression.Lambda<Action<object, Vector3, ColorSRGB, Vector3, ColorSRGB, Vector3, ColorSRGB, Vector3, ColorSRGB, object>>(
                        call, self, a, ca, b, cb, c, cc, d, cd, depth).Compile();
                }
                PluginLog.Info($"3D UI layer: fast calls made (lines {ui3dLineFast != null}, strips {ui3dStripFast != null})");
            }
            catch (Exception e)
            {
                ui3dLineFast = null; ui3dStripFast = null;
                PluginLog.Info("3D UI layer: fast calls not made (the slower way is used): " + (e.InnerException ?? e).Message);
            }
        }

        private static long ui3dSlowLoggedAt;

        private static readonly object[] ui3dLineArgs = new object[7], ui3dQuadArgs = new object[10];
        private static bool ui3dSeenLogged;
        private static Vector3D glowCamera;

        private static void Ui3dDrawNow(object __instance)
        {
            if (__instance == null || ui3dErrors >= 5) return;
            ui3dInstance = __instance;
            if (!ui3dSeenLogged) { ui3dSeenLogged = true; PluginLog.Info("3D UI layer: its job is running (ready to draw)"); }
            try
            {
                lock (glowLock)
                {
                    if (glowShown.Count == 0 || Environment.TickCount64 - glowShownAt > 250) return;   // (nothing new for a while: our frames stopped)
                    if (ui3dCamera != null) glowCamera = (Vector3D)ui3dCamera.GetValue(__instance);   // (where the camera is this frame)
                    long drawStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < glowShown.Count; i++)
                    {
                        GlowShape shape = glowShown[i];
                        if (shape.Hidden && ui3dBehind == null) continue;
                        object depth = shape.Hidden ? ui3dBehind : shape.Depth ? ui3dInFront : ui3dBoth;
                        if (shape.Ribbon && ui3dQuadFull != null)
                        {
                            // A solid strip turned to face the camera, with no fade at its ends (the layer's lines
                            // fade at each end, so a line made of many short ones looked dashed and washed out far
                            // off): a little longer than asked at both ends, so the pieces of a curve overlap.
                            double ax = shape.A.X - glowCamera.X, ay = shape.A.Y - glowCamera.Y, az = shape.A.Z - glowCamera.Z;
                            double bx = shape.B.X - glowCamera.X, by = shape.B.Y - glowCamera.Y, bz = shape.B.Z - glowCamera.Z;
                            double dx = bx - ax, dy = by - ay, dz = bz - az;
                            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (length < 1e-6) continue;
                            dx /= length; dy /= length; dz /= length;
                            double mx = (ax + bx) / 2, my = (ay + by) / 2, mz = (az + bz) / 2;
                            double px = dy * mz - dz * my, py = dz * mx - dx * mz, pz = dx * my - dy * mx;   // across it, as seen
                            double across = Math.Sqrt(px * px + py * py + pz * pz);
                            if (across < 1e-9) continue;
                            double halfWide = shape.Thick / 2 / across, more = shape.Thick * 0.2;
                            px *= halfWide; py *= halfWide; pz *= halfWide;
                            ax -= dx * more; ay -= dy * more; az -= dz * more;
                            bx += dx * more; by += dy * more; bz += dz * more;
                            if (ui3dStripFast != null)
                            {
                                ui3dStripFast(__instance,
                                    new Vector3((float)(ax + px), (float)(ay + py), (float)(az + pz)), shape.Ca, new Vector3((float)(bx + px), (float)(by + py), (float)(bz + pz)), shape.Ca,
                                    new Vector3((float)(bx - px), (float)(by - py), (float)(bz - pz)), shape.Ca, new Vector3((float)(ax - px), (float)(ay - py), (float)(az - pz)), shape.Ca, depth);
                                continue;
                            }
                            var q = ui3dFullArgs;
                            q[0] = new Vector3((float)(ax + px), (float)(ay + py), (float)(az + pz)); q[1] = shape.Ca;
                            q[4] = new Vector3((float)(bx + px), (float)(by + py), (float)(bz + pz)); q[5] = shape.Ca;
                            q[8] = new Vector3((float)(bx - px), (float)(by - py), (float)(bz - pz)); q[9] = shape.Ca;
                            q[12] = new Vector3((float)(ax - px), (float)(ay - py), (float)(az - pz)); q[13] = shape.Ca;
                            q[17] = depth;
                            ui3dQuadFull.Invoke(__instance, q);
                        }
                        else if (shape.Quad)
                        {
                            var q = ui3dQuadArgs;
                            q[0] = __instance; q[1] = shape.A; q[2] = shape.Ca; q[3] = shape.B; q[4] = shape.Cb; q[5] = shape.C; q[6] = shape.Cc; q[7] = shape.D; q[8] = shape.Cd; q[9] = depth;
                            ui3dQuad.Invoke(null, q);
                        }
                        else if (ui3dLineFast != null)
                        {
                            ui3dLineFast(__instance,
                                new Vector3((float)(shape.A.X - glowCamera.X), (float)(shape.A.Y - glowCamera.Y), (float)(shape.A.Z - glowCamera.Z)), shape.Ca,
                                new Vector3((float)(shape.B.X - glowCamera.X), (float)(shape.B.Y - glowCamera.Y), (float)(shape.B.Z - glowCamera.Z)), shape.Cb, shape.Thick, depth);
                        }
                        else if (ui3dLineMetres != null)
                        {
                            var l = ui3dLineMetresArgs;
                            l[0] = new Vector3((float)(shape.A.X - glowCamera.X), (float)(shape.A.Y - glowCamera.Y), (float)(shape.A.Z - glowCamera.Z)); l[1] = shape.Ca;
                            l[2] = new Vector3((float)(shape.B.X - glowCamera.X), (float)(shape.B.Y - glowCamera.Y), (float)(shape.B.Z - glowCamera.Z)); l[3] = shape.Cb;
                            l[4] = shape.Thick; l[5] = depth;
                            ui3dLineMetres.Invoke(__instance, l);
                        }
                        else
                        {
                            var l = ui3dLineArgs;
                            l[0] = __instance; l[1] = shape.A; l[2] = shape.Ca; l[3] = shape.B; l[4] = shape.Cb; l[5] = shape.Thick; l[6] = depth;
                            ui3dLine.Invoke(null, l);
                        }
                    }
                    // (A frame's drawing that took long is logged, at most every ten seconds: to see what it costs.)
                    double drawMs = (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency;
                    if (drawMs > 3 && Environment.TickCount64 - ui3dSlowLoggedAt > 10000)
                    {
                        ui3dSlowLoggedAt = Environment.TickCount64;
                        PluginLog.Info($"3D UI layer: drawing {glowShown.Count} shapes took {drawMs:0.0} ms");
                    }
                }
            }
            catch (Exception e)
            {
                ui3dErrors++;
                PluginLog.Info("3D UI layer: drawing failed" + (ui3dErrors >= 5 ? " (given up: air gate glow uses the plain drawing)" : "") + ": " + (e.InnerException ?? e).Message);
            }
        }

        // Marker lines are kept and drawn together once a frame (FlushMarkerLines): the racing line alone is thousands
        // of them, and switching the game's debug draw on and off round each one cost more than drawing it.
        private readonly List<(Vector3D A, Vector3D B, ColorSRGB C)> pendingLines = new List<(Vector3D, Vector3D, ColorSRGB)>();

        public void DrawMarkerLine(Vec3D from, Vec3D to, HudColor color)
        {
            if (Bad(from) || Bad(to) || pendingLines.Count >= 20000) return;
            pendingLines.Add((new Vector3D(from.X, from.Y, from.Z), new Vector3D(to.X, to.Y, to.Z), ToColor(color)));
        }

        /// <summary>Draws the marker lines kept this frame (forced on, so they show without debug draw and with the HUD hidden).</summary>
        public void FlushMarkerLines()
        {
            PublishGlow();
            if (pendingLines.Count == 0) return;
            try
            {
                var dd = DebugDraw;
                if (dd == null) return;
                var builder = dd.DebugDraw;
                WithEnabled(builder, Enabled3D, () =>
                {
                    foreach (var (a, b, c) in pendingLines) builder.AddLine(a, b, c, false, DrawTimeout);
                });
            }
            finally { pendingLines.Clear(); }
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

        public void OpenEditWheel(string[] labels, int current, int wheelKey, IDictionary<int, (byte R, byte G, byte B)> highlights = null, WheelLook look = null)
        {
            // The game's own quick-select radial menu (like the paint gun's); the pause-menu wheel if that fails.
            if (!QuickWheel.Open(labels, current, highlights, look))
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
