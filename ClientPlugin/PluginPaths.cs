using System;
using System.IO;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>Where the settings file and log live.</summary>
    internal static class PluginPaths
    {
        private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>%AppData%\SpaceEngineers2\AppData\RaceDisplay</summary>
        public static string Folder => Path.Combine(Roaming, "SpaceEngineers2", "AppData", "RaceDisplay");

    }
}
