using System;
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
            HelpScreenPage.Apply(harmony);
            TerminalTab.Apply(harmony);
            PluginLog.Info("Setup finished");
        }

        private void OnFrame()
        {
            if (errorCount > 10) return; // something in the game changed; stop rather than spam every frame
            try
            {
                PluginLog.Step("bridge refresh");
                bridge.Refresh();
                controller.Update();
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
