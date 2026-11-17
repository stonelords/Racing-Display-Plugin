using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.VRage.Core.Plugins;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// Loaded by Space Engineers 2's built-in plugin loader (no Pulsar needed).
    /// Steam launch option:  -plugins:C:\Path\To\RaceDisplay.dll
    ///
    /// The game (Keen.VRage.Core.Plugins.PluginHost) finds every class implementing IPlugin,
    /// creates it with a (PluginHost) constructor if there is one, otherwise a no-argument
    /// constructor, and calls Dispose() on exit if the class is IDisposable.
    /// </summary>
    public class Plugin : IPlugin, IDisposable
    {
        private readonly Harmony harmony;
        private readonly Se2Bridge bridge;
        private readonly RaceGateController controller;
        private int errorCount;

        public Plugin(PluginHost host)
        {
            PluginLog.Start();
            PluginLog.Info("Plugin created by the game's plugin loader");
            bridge = new Se2Bridge();
            controller = new RaceGateController(bridge);

            Patches.OnFrame = OnFrame;
            Patches.OnPauseMenuRequested = controller.EscapePressed;
            Patches.OnWorldSaveCompleted = controller.WorldSaveCompleted;
            harmony = new Harmony("RaceDisplay");
            Patches.Apply(harmony);
            Se2Bridge.ApplyUi3d(harmony);
            HelpScreenPage.Apply(harmony);
            TerminalTab.Apply(harmony);
            BuildMenuRpi.RpiText = controller.RpiTextFor;
            BuildMenuRpi.Apply(harmony);
            PluginLog.Info("Setup finished");
            PrepareCode();
        }

        /// <summary>
        /// Compiles the plugin's code ahead of time, on a background thread while the game sits in its menus.
        /// Otherwise each bit is compiled the first time it runs, and the first race of a session hitched as the
        /// race start, checkpoint and results code all got compiled at once (the game's log counts it as JIT stalls).
        /// </summary>
        private static void PrepareCode()
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(5000);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    int done = 0;
                    Type[] types;
                    try { types = typeof(Plugin).Assembly.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
                    foreach (var type in types)
                    {
                        if (type.ContainsGenericParameters) continue;
                        var methods = new List<System.Reflection.MethodBase>();
                        try { methods.AddRange(type.GetMethods(all)); methods.AddRange(type.GetConstructors(all)); } catch { continue; }
                        foreach (var m in methods)
                        {
                            try
                            {
                                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                                if ((m.MethodImplementationFlags & System.Reflection.MethodImplAttributes.InternalCall) != 0) continue;
                                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
                                done++;
                            }
                            catch { /* (one that can't be compiled ahead: it's compiled when it runs, as before) */ }
                        }
                    }
                    PluginLog.Info($"Compiled {done} methods ahead of time in {watch.ElapsedMilliseconds} ms (no hitch the first time each runs)");
                }
                catch (Exception e) { PluginLog.Info("Compiling ahead of time stopped: " + e.Message); }
            })
            { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "RaceDisplay prepare" };
            thread.Start();
        }

        private void OnFrame()
        {
            if (errorCount > 10) return; // something in the game changed; stop rather than spam every frame
            try
            {
                PluginLog.Step("bridge refresh");
                bridge.Refresh();
                controller.Update();
                GameUiExtras.Frame(controller, bridge.TimeSeconds);   // RPI in the block placer and the grid info panel
                PluginLog.Step("debug-draw marker lines (batched)");
                bridge.FlushMarkerLines();
                PluginLog.Step("game");   // the rest of the frame is the game's own time
            }
            catch (Exception e)
            {
                errorCount++;
                PluginLog.Error($"Error during frame update ({errorCount})", e);
                if (errorCount > 10) PluginLog.Error("Too many errors, the plugin has stopped running");
            }
        }

        public void Dispose()
        {
            Patches.OnFrame = null;
            Patches.OnPauseMenuRequested = null;
            harmony?.UnpatchAll("RaceDisplay");
        }
    }
}
