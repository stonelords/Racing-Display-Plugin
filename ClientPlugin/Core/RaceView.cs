namespace RaceDisplay.Core
{
    /// <summary>Extra race state from the controller that the HUD shows (gap, results, countdown, flash).</summary>
    public sealed class RaceView
    {
        /// <summary>Records for the current course (may be null).</summary>
        public GateSettings.CourseRecord Record;

        /// <summary>Latest gap to the best lap ("-1.204"), shown in the timer panel. Null = none.</summary>
        public string Delta;
        public HudColor DeltaColour = HudColor.White;

        public HudModel.ResultsPanel Results;

        public string Countdown;
        public HudColor CountdownColour = HudColor.White;

        public HudColor? Flash;

        /// <summary>Show "GO!" in the box under the timer (just after the race starts).</summary>
        public bool ShowGo;

        /// <summary>Top and average speed (m/s) of each completed race lap, in order.</summary>
        public System.Collections.Generic.IReadOnlyList<(double Top, double Avg)> LapSpeeds;

        /// <summary>Best sectors added up ("possible lap"), or null.</summary>
        public double? PossibleLap;

        /// <summary>G-force and distance of each completed race lap, in order.</summary>
        public System.Collections.Generic.IReadOnlyList<LapStats> LapStats;

        /// <summary>Race stats shown under the splits after the finish (label, value, colour).</summary>
        public System.Collections.Generic.IReadOnlyList<(string Label, string Value, HudColor Colour)> RaceStats;
    }

    /// <summary>How hard a lap was flown, measured from the ship's change of velocity.</summary>
    public sealed class LapStats
    {
        public double MaxG;        // biggest acceleration in any direction
        public double MaxTurnG;    // biggest sideways (cornering) acceleration
        public double AvgTurnG;    // average cornering acceleration while moving
        public double MaxBrakeG;   // hardest slowing down
        public double Distance;    // metres flown
    }
}
