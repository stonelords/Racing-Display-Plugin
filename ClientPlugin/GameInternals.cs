using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RaceDisplay
{
    /// <summary>Reflection helpers for game parts the plugin can't reference directly, and one-off diagnostics for the log.</summary>
    internal static class GameInternals
    {
        /// <summary>All types in the game's (Keen) assemblies, even from assemblies that only partly load.</summary>
        public static IEnumerable<Type> GameTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name ?? "";
                if (!name.StartsWith("Keen", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("VRage", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("Game2", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("SpaceEngineers", StringComparison.OrdinalIgnoreCase))
                    continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types) yield return t;
            }
        }

        private static readonly Dictionary<string, WeakReference> instances = new Dictionary<string, WeakReference>();

        /// <summary>Keeps (weakly) the latest created object of its type, for calling into later.</summary>
        public static void Remember(object o)
        {
            lock (instances) instances[o.GetType().FullName] = new WeakReference(o);
        }

        /// <summary>Keeps (weakly) an object under a name of our choosing.</summary>
        public static void Remember(string key, object o)
        {
            if (o == null) return;
            lock (instances) instances[key] = new WeakReference(o);
        }

        public static object Instance(string fullName)
        {
            lock (instances) return instances.TryGetValue(fullName, out var w) ? w.Target : null;
        }

        /// <summary>The arguments the game last built the pause menu's view model with.</summary>
        public static volatile object[] PauseMenuArgs;

        /// <summary>Logs a type's base classes, constructors, fields, properties, methods and events (for working out how to use it).</summary>
        public static void Describe(Type t, string tag)
        {
            if (t == null) return;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var chain = new List<string>();
            for (var b = t; b != null && b != typeof(object); b = b.BaseType) chain.Add(b.FullName);
            PluginLog.Info($"{tag}: {string.Join(" : ", chain)}; interfaces: {string.Join(", ", t.GetInterfaces().Select(i => i.Name))}");
            for (var b = t; b != null && b != typeof(object) && !(b.Namespace ?? "").StartsWith("Avalonia"); b = b.BaseType)
            {
                PluginLog.Info($"{tag} [{b.Name}] ctors: " + string.Join(" | ", b.GetConstructors(all).Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")")));
                PluginLog.Info($"{tag} [{b.Name}] fields: " + string.Join("; ", b.GetFields(all).Select(f => f.FieldType.Name + " " + f.Name).Take(40)));
                PluginLog.Info($"{tag} [{b.Name}] props: " + string.Join("; ", b.GetProperties(all).Select(p => p.PropertyType.Name + " " + p.Name).Take(40)));
                PluginLog.Info($"{tag} [{b.Name}] methods: " + string.Join("; ", b.GetMethods(all).Where(m => !m.IsSpecialName).Select(m => m.ReturnType.Name + " " + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").Distinct().Take(50)));
                PluginLog.Info($"{tag} [{b.Name}] events: " + string.Join("; ", b.GetEvents(all).Select(e => e.EventHandlerType?.Name + " " + e.Name)));
            }
        }

        /// <summary>Static bool members whose name mentions debug draw, for switching it on.</summary>
        public static List<MemberInfo> DebugDrawSwitches()
        {
            var found = new List<MemberInfo>();
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var t in GameTypes())
            {
                if (t.Name.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var m in t.GetMembers(st))
                {
                    if (m.Name.IndexOf("DebugDraw", StringComparison.OrdinalIgnoreCase) < 0 && m.Name.IndexOf("EnableDraw", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (m is PropertyInfo p && p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0) found.Add(m);
                    else if (m is FieldInfo f && f.FieldType == typeof(bool) && !f.IsLiteral) found.Add(m);
                }
            }
            return found;
        }
    }
}
