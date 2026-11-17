using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RaceDisplay
{
    /// <summary>
    /// Adds the block's RPI cost (in the RPI class in use) to its description in the game's build menu
    /// (G > Build, the panel about the picked block).
    ///
    /// The build menu's detail panel is shown by a view model (BlockTileDetailViewModel). Its text properties
    /// whose name has "Description" in it get a postfix that adds a line such as "RPI: 12 (large), 3 (small)".
    /// The block is found from the view model's own members: anything the analyser knows as a block definition.
    /// What the view model holds is written to the log once, to help if a game update moves things.
    /// </summary>
    internal static class BuildMenuRpi
    {
        /// <summary>Block key (as the RPI class keeps it) to the RPI line to add, or null for none. Set by the controller.</summary>
        internal static Func<IReadOnlyList<string>, string> RpiText;

        private static readonly Dictionary<Type, List<Func<object, object>>> readers = new Dictionary<Type, List<Func<object, object>>>();
        private static bool dumped;

        public static void Apply(Harmony harmony)
        {
            try
            {
                var types = new List<Type>();
                foreach (var name in new[] { "BlockTileDetailViewModel", "BlockToolbarTileViewModel" })
                {
                    Type t = FindType(name);
                    if (t != null) types.Add(t);
                }
                if (types.Count == 0) { PluginLog.Info("Build menu RPI: the block detail view model wasn't found (no RPI in the build menu)"); return; }
                int patched = 0;
                foreach (var t in types)
                    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (p.PropertyType != typeof(string) || p.GetMethod == null) continue;
                        if (p.Name.IndexOf("Description", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            harmony.Patch(p.GetMethod, postfix: new HarmonyMethod(typeof(BuildMenuRpi), nameof(DescriptionPostfix)));
                            patched++;
                            PluginLog.Info($"Build menu RPI: patched {t.Name}.{p.Name}");
                        }
                        catch (Exception e) { PluginLog.Info($"Build menu RPI: couldn't patch {t.Name}.{p.Name}: {e.Message}"); }
                    }
                if (patched == 0)
                    PluginLog.Info("Build menu RPI: no text description found on " + string.Join(", ", types.Select(t => t.Name)) + ". Its members: "
                        + string.Join(", ", types.SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
            }
            catch (Exception e) { PluginLog.Error("Build menu RPI couldn't be set up", e); }
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || !(asm.GetName().Name ?? "").StartsWith("Game2", StringComparison.Ordinal)) continue;
                Type[] all;
                try { all = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { all = e.Types.Where(x => x != null).ToArray(); } catch { continue; }
                var hit = all.FirstOrDefault(x => x != null && x.Name == name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void DescriptionPostfix(object __instance, ref string __result)
        {
            try
            {
                if (__instance == null || RpiText == null) return;
                if (__result != null && __result.Contains("RPI:")) return;
                var keys = BlockKeys(__instance);
                if (keys.Count == 0) return;
                string line = RpiText(keys);
                if (string.IsNullOrEmpty(line)) return;
                __result = string.IsNullOrEmpty(__result) ? line : __result + "\n\n" + line;
            }
            catch { /* the description as the game has it */ }
        }

        /// <summary>The block keys (one per size) of whatever block the view model is about.</summary>
        private static List<string> BlockKeys(object vm)
        {
            var keys = new List<string>();
            var type = vm.GetType();
            if (!readers.TryGetValue(type, out var list))
            {
                list = new List<Func<object, object>>();
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (!f.FieldType.IsPrimitive && f.FieldType != typeof(string)) list.Add(o => f.GetValue(o));
                foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (p.GetIndexParameters().Length == 0 && p.GetMethod != null && !p.PropertyType.IsPrimitive && p.PropertyType != typeof(string)
                        && p.Name.IndexOf("Description", StringComparison.OrdinalIgnoreCase) < 0)
                        list.Add(o => p.GetValue(o));
                readers[type] = list;
                if (!dumped)
                {
                    dumped = true;
                    PluginLog.Info($"Build menu RPI: {type.Name} holds "
                        + string.Join(", ", type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
                }
            }
            void Take(object value, int depth)
            {
                if (value == null || depth > 2 || keys.Count > 8) return;
                if (value is string) return;
                var entry = value.GetType().Name.EndsWith("Definition", StringComparison.Ordinal) ? Se2Bridge.BlockOf(value) : null;
                if (entry != null && IsBlockDefinition(value)) { if (!keys.Contains(entry.Key)) keys.Add(entry.Key); return; }
                if (value is IEnumerable many && !(value is string))
                {
                    int n = 0;
                    foreach (var item in many) { if (++n > 12) break; Take(item, depth + 1); }
                    return;
                }
                // One level into view models / block kinds: a block kind lists its sizes' definitions.
                if (depth < 2 && (value.GetType().Name.Contains("Block") || value.GetType().Name.Contains("Tile")))
                    foreach (var name in new[] { "Definition", "BlockDefinition", "Definitions", "BlockDefinitions", "Blocks", "Variants", "Sizes" })
                    {
                        object inner;
                        try { inner = AccessTools.Property(value.GetType(), name)?.GetValue(value) ?? AccessTools.Field(value.GetType(), name)?.GetValue(value); }
                        catch { continue; }
                        if (inner != null && !ReferenceEquals(inner, value)) Take(inner, depth + 1);
                    }
            }
            foreach (var read in list)
            {
                object v;
                try { v = read(vm); } catch { continue; }
                Take(v, 0);
                if (keys.Count > 0 && keys.Count >= 2) break;
            }
            return keys;
        }

        private static bool IsBlockDefinition(object def)
        {
            for (var t = def.GetType(); t != null; t = t.BaseType)
                if (t.Name.Contains("CubeBlock") || t.Name == "BlockDefinition") return true;
            return AccessTools.Property(def.GetType(), "BlockKind") != null || AccessTools.Property(def.GetType(), "DisplaySize") != null;
        }
    }
}
