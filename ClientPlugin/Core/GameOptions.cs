using System;

namespace RaceDisplay.Core
{
    /// <summary>
    /// The game's own interface options that the HUD follows: the speed unit and the UI background opacity
    /// (Options > Interface). Filled in by the game bridge; the defaults are used until it has read them.
    /// </summary>
    public static class GameOptions
    {
        /// <summary>0 = m/s, 1 = km/h, 2 = mph (the game's SpeedUnit enum order).</summary>
        public static volatile int SpeedUnit;

        /// <summary>The game's UI background opacity, 0 to 1; negative until it's been read.</summary>
        public static double UiBackgroundOpacity = -1;

        public static string SpeedUnitLabel => SpeedUnit == 1 ? "km/h" : SpeedUnit == 2 ? "mph" : "m/s";

        /// <summary>A speed in m/s converted to the game's unit.</summary>
        public static double ToUnit(double metresPerSecond) =>
            SpeedUnit == 1 ? metresPerSecond * 3.6 : SpeedUnit == 2 ? metresPerSecond * 2.23694 : metresPerSecond;

        /// <summary>"435 m/s", "1566 km/h" or "973 mph".</summary>
        public static string Speed(double metresPerSecond) => $"{ToUnit(metresPerSecond):0} {SpeedUnitLabel}";

        /// <summary>Just the number, in the game's unit.</summary>
        public static string SpeedNumber(double metresPerSecond) => $"{ToUnit(metresPerSecond):0}";
    }
}
