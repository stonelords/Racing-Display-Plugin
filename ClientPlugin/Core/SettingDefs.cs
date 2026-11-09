using System;

namespace RaceDisplay.Core
{
    /// <summary>One adjustable setting on the Settings tab: a slider, or an on/off switch.</summary>
    public sealed class SettingDef
    {
        public string Section;
        public string Name;
        public string Description;
        public bool Toggle;          // on/off switch instead of a slider
        public double Min, Max, Step;
        public string Format = "{0:0}"; // how the slider shows its value (string.Format)
        public string[] Choices;     // slider of named choices: shows Choices[value] instead of the number
        public Func<GateSettings, double> Get;
        public Action<GateSettings, double> Set;

        /// <summary>Rounds to the step and keeps it in range.</summary>
        public double Clean(double v)
        {
            if (Toggle) return v >= 0.5 ? 1 : 0;
            if (Step > 0) v = Min + Math.Round((v - Min) / Step) * Step;
            return Math.Max(Min, Math.Min(Max, Math.Round(v, 6)));
        }
    }

    /// <summary>Everything on the Settings tab, in order. Values live in GateSettings (RaceDisplay.json).</summary>
    public static class SettingDefs
    {
        private static double B(bool b) => b ? 1 : 0;

        public static readonly SettingDef[] All =
        {
            // ---- Penalties
            new SettingDef
            {
                Section = "Penalties", Name = "Missed checkpoint penalty (s)", Min = 0, Max = 30, Step = 0.5, Format = "{0:0.0}",
                Description = "Seconds added to the lap for every checkpoint you miss. 0 turns it off.",
                Get = s => s.PenaltySeconds, Set = (s, v) => s.PenaltySeconds = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash penalty (s)", Min = 0, Max = 30, Step = 0.5, Format = "{0:0.0}",
                Description = "Seconds added for every crash during a race. 0 turns crash penalties off.",
                Get = s => s.CrashPenaltySeconds, Set = (s, v) => s.CrashPenaltySeconds = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash immunity (s)", Min = 0, Max = 60, Step = 0.5, Format = "{0:0.0}",
                Description = "After a crash penalty, further crashes don't add a penalty until this many seconds have passed (so bouncing off a wall or tumbling only counts once). 0 turns it off.",
                Get = s => s.CrashImmunitySeconds, Set = (s, v) => s.CrashImmunitySeconds = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash minimum speed (m/s)", Min = 0, Max = 150, Step = 1,
                Description = "Crashes only count when you were going at least this fast.",
                Get = s => s.CrashMinSpeed, Set = (s, v) => s.CrashMinSpeed = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash hardness (m/s²)", Min = 10, Max = 150, Step = 5,
                Description = "How sudden the stop must be to count as a crash. Lower = more sensitive. The default is 20 (about 2 g); raise it if hard braking counts as a crash.",
                Get = s => s.CrashDeceleration, Set = (s, v) => s.CrashDeceleration = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash minimum speed lost (m/s)", Min = 1, Max = 50, Step = 1,
                Description = "The impact must take away at least this much speed, so small scrapes don't count.",
                Get = s => s.CrashMinSpeedLoss, Set = (s, v) => s.CrashMinSpeedLoss = v,
            },

            // ---- Race start
            new SettingDef
            {
                Section = "Race start", Name = "Only start going forwards", Toggle = true,
                Description = "On: going through the start gate backwards (away from the first checkpoint) doesn't start a race. Turn it off if your course starts by turning round behind the gate, or to start from either side.",
                Get = s => B(s.ForwardStartOnly), Set = (s, v) => s.ForwardStartOnly = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Race start", Name = "Countdown start", Toggle = true,
                Description = "Stop near the start gate and hold still to get a 3-2-1-GO countdown. Off: no countdown, the race just starts when you fly through the gate.",
                Get = s => B(s.CountdownEnabled), Set = (s, v) => s.CountdownEnabled = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Race start", Name = "Countdown distance from gate (m)", Min = 10, Max = 300, Step = 5,
                Description = "Stop within this distance of the start gate to get the 3-2-1-GO countdown.",
                Get = s => s.CountdownDistance, Set = (s, v) => s.CountdownDistance = v,
            },
            new SettingDef
            {
                Section = "Race start", Name = "Hold still before countdown (s)", Min = 0, Max = 5, Step = 0.5, Format = "{0:0.0}",
                Description = "How long you must stay stopped near the gate before the countdown starts. 0 turns the countdown off.",
                Get = s => s.CountdownHoldSeconds, Set = (s, v) => s.CountdownHoldSeconds = v,
            },
            new SettingDef
            {
                Section = "Race start", Name = "Laps per race (0 = unlimited)", Min = 0, Max = 20, Step = 1,
                Description = "How many laps make a race. 0 keeps timing laps until you reset.",
                Get = s => s.LapTarget, Set = (s, v) => s.LapTarget = (int)Math.Round(v),
            },

            new SettingDef
            {
                Section = "Race start", Name = "Practice laps (0 = none)", Min = 0, Max = 10, Step = 1,
                Description = "Laps flown before the race starts. They're timed but don't count towards the race or your best times. The race starts when you complete the last one at the gate.",
                Get = s => s.PracticeLaps, Set = (s, v) => s.PracticeLaps = (int)Math.Round(v),
            },

            // ---- Gates and checkpoints
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Fit to ring opening", Toggle = true,
                Description = "Fit gates and checkpoints to the hole in a ring or arch, ignoring its legs. Off = the whole grid.",
                Get = s => B(s.FitToOpening), Set = (s, v) => s.FitToOpening = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Extra space round grids (m)", Min = 0, Max = 20, Step = 0.5, Format = "{0:0.0}",
                Description = "Added on every side of a gate or checkpoint when it's placed.",
                Get = s => s.GridPadding, Set = (s, v) => s.GridPadding = v,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Edit mode reach (m)", Min = 50, Max = 5000, Step = 50,
                Description = "In course edit mode, how far away a grid can be when you aim at it and click.",
                Get = s => s.GridSearchDistance, Set = (s, v) => s.GridSearchDistance = v,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Best lap line in edit mode", Toggle = true,
                Description = "Show your best lap's racing line while editing the course, to see how checkpoints sit against the line people fly. Also switched with Shift+L in edit mode (Controls tab: Best lap line while editing).",
                Get = s => B(s.EditRacingLine), Set = (s, v) => s.EditRacingLine = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Air gate size (m)", Min = 5, Max = 300, Step = 5,
                Description = "Size of a gate or checkpoint placed in open air (placement mode In the air, Shift+T in edit mode; no grid needed). The grow / shrink keys change it while placing in the air.",
                Get = s => s.AirCheckpointSize, Set = (s, v) => s.AirCheckpointSize = v,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Air gate distance (m)", Min = 5, Max = 500, Step = 5,
                Description = "How far ahead of you (along where you're looking) a gate or checkpoint is placed with In the air on.",
                Get = s => s.AirCheckpointDistance, Set = (s, v) => s.AirCheckpointDistance = v,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Re-arm distance (m)", Min = 5, Max = 300, Step = 5,
                Description = "How far you must fly from the gate before the next pass through it counts.",
                Get = s => s.RearmDistance, Set = (s, v) => s.RearmDistance = v,
            },
            new SettingDef
            {
                Section = "Gates and checkpoints", Name = "Shortest lap (s)", Min = 0, Max = 60, Step = 1,
                Description = "Laps quicker than this are ignored (stops double counts at the gate).",
                Get = s => s.MinLapSeconds, Set = (s, v) => s.MinLapSeconds = v,
            },

            // ---- Display
            new SettingDef
            {
                Section = "Display", Name = "Lap list", Toggle = true,
                Description = "The list of lap times (and the results under it after the finish).",
                Get = s => B(s.ShowLapList), Set = (s, v) => s.ShowLapList = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Splits panel", Toggle = true,
                Description = "Checkpoint times and gaps during the race, and the full splits table after the finish.",
                Get = s => B(s.ShowSplits), Set = (s, v) => s.ShowSplits = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Race stats", Toggle = true,
                Description = "After the finish: ship, distance, G-forces, time near top speed, average lap and consistency, in a box in the top corner.",
                Get = s => B(s.ShowRaceStats), Set = (s, v) => s.ShowRaceStats = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Results against this ship", Toggle = true,
                Description = "Off: the race results compare with the best on the course in any ship. On: with the best in the ship you flew. The results shortcut (Ctrl+B) switches while they're showing.",
                Get = s => B(s.ResultsForShip), Set = (s, v) => s.ResultsForShip = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Best lap / best race boxes", Toggle = true,
                Description = "The boxes either side of the timer with your best lap (and possible lap) and best race.",
                Get = s => B(s.ShowBestBoxes), Set = (s, v) => s.ShowBestBoxes = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Swap sides", Toggle = true,
                Description = "Lap list and results on the right, splits on the left.",
                Get = s => B(s.SwapSides), Set = (s, v) => s.SwapSides = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Checkpoint labels and arrow", Toggle = true,
                Description = "HUD labels on the checkpoints, and an arrow at the edge of the screen pointing to the next one when it's off screen or behind you.",
                Get = s => B(s.ShowCheckpointArrow), Set = (s, v) => s.ShowCheckpointArrow = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Sector colours", Toggle = true,
                Description = "Colour each sector like F1 timing: purple = fastest ever on this course, green = your best this race, yellow = slower. Shown on the checkpoint dots under the timer as you fly through, and in the splits.",
                Get = s => B(s.SectorColours), Set = (s, v) => s.SectorColours = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Racing line colours", Min = 0, Max = 2, Step = 1, Choices = new[] { "Plain", "Speed", "Braking" },
                Description = "How the best lap's racing line is coloured, to see where time is won and lost. Speed: red where it was slow (braking, tight turns), through yellow, to green where it was fastest. Braking: red where it was slowing down (the harder, the redder), green where it was speeding up, white where it held its speed. Plain: magenta. (With a 2nd ghost on the lines are purple and orange.)",
                Get = s => s.RacingLineColourMode, Set = (s, v) => s.RacingLineColourMode = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Minimap", Name = "Minimap", Toggle = true,
                Description = "A small round map of the course above the lap list, with a label by each gate and checkpoint and the racing line in its racing line colours. It zooms in and follows you while you're moving, and shows the whole course when you stop. It shows the route between the gates and checkpoints (the next one orange), your best lap's line, you (the arrow) and the ghosts. The course's start gate faces up.",
                Get = s => B(s.ShowMinimap), Set = (s, v) => s.ShowMinimap = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Minimap", Name = "Minimap size", Min = 150, Max = 500, Step = 10,
                Description = "How big the minimap is (it also follows the HUD size).",
                Get = s => s.MinimapSize, Set = (s, v) => s.MinimapSize = v,
            },
            new SettingDef
            {
                Section = "Minimap", Name = "Minimap zooms in while moving", Toggle = true,
                Description = "On: from 20 m/s the minimap zooms in and follows you, and zooms back out to the whole course as you slow down. Off: it always shows the whole course.",
                Get = s => B(s.MinimapFollow), Set = (s, v) => s.MinimapFollow = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Minimap", Name = "Minimap zoom while moving (m)", Min = 300, Max = 10000, Step = 100,
                Description = "While you're moving, the minimap zooms in and follows you, showing this much of the course across. When you stop it zooms back out to the whole course. Set it to the top for the whole course all the time.",
                Get = s => s.MinimapZoom, Set = (s, v) => s.MinimapZoom = v,
            },
            new SettingDef
            {
                Section = "Display", Name = "Racing line", Toggle = true,
                Description = "A faint purple line through the world along your best lap's path.",
                Get = s => B(s.ShowRacingLine), Set = (s, v) => s.ShowRacingLine = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Sector times after the finish", Toggle = true,
                Description = "On: the table after the finish shows each sector's time (checkpoint to checkpoint). Off: lap time at each checkpoint.",
                Get = s => B(s.SectorTable), Set = (s, v) => s.SectorTable = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Sounds", Toggle = true,
                Description = "Beeps for the countdown, checkpoints, laps, missed checkpoints, crashes and new records. (The sounds used can be changed in RaceDisplay.json.)",
                Get = s => B(s.Sounds), Set = (s, v) => s.Sounds = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Checkpoint markers", Toggle = true,
                Description = "Always-visible markers on the gate and checkpoints.",
                Get = s => B(s.ShowMarkers), Set = (s, v) => s.ShowMarkers = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Marker size (%)", Min = 1, Max = 10, Step = 0.5, Format = "{0:0.0}",
                Description = "Size of the markers, as a share of the distance to them (they stay the same size on screen).",
                Get = s => s.MarkerSize * 100, Set = (s, v) => s.MarkerSize = v / 100,
            },
            new SettingDef
            {
                Section = "Display", Name = "HUD size", Min = 0.5, Max = 2, Step = 0.05, Format = "{0:0.00}",
                Description = "Overall size of the race panels and text.",
                Get = s => s.HudScale, Set = (s, v) => s.HudScale = v,
            },
            new SettingDef
            {
                Section = "Display", Name = "Race HUD only", Toggle = true,
                Description = "Hide the game's own HUD (speed and altitude, toolbar, character status, hints, notifications) and show only the race HUD. Also a step in the game's HUD key (U): full, minimal, race HUD only, none.",
                Get = s => B(s.RaceHudOnly), Set = (s, v) => s.RaceHudOnly = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Panel darkness: use the game's", Toggle = true,
                Description = "On: the race panels' backgrounds use the game's UI background opacity (Options > Interface). Off: they use Panel darkness below.",
                Get = s => s.UseGameUiOpacity ? 1 : 0, Set = (s, v) => s.UseGameUiOpacity = v > 0.5,
            },
            new SettingDef
            {
                Section = "Display", Name = "Panel darkness", Min = 0, Max = 255, Step = 5,
                Description = "How dark the race panels' backgrounds are. 255 = solid. Only used when \"Panel darkness: use the game's\" is off.",
                Get = s => s.BackgroundOpacity, Set = (s, v) => s.BackgroundOpacity = (byte)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Display", Name = "Results shown for (s, 0 = until next race)", Min = 0, Max = 60, Step = 1,
                Description = "How long the results stay on screen after the finish before fading out. 0 keeps them until you start the next race.",
                Get = s => s.ResultsSeconds, Set = (s, v) => s.ResultsSeconds = v,
            },
            new SettingDef
            {
                Section = "Display", Name = "Laps shown in the list", Min = 3, Max = 20, Step = 1,
                Description = "How many laps the lap list on the left shows before older ones scroll off.",
                Get = s => s.MaxLapRows, Set = (s, v) => s.MaxLapRows = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Ghost and replay", Name = "Ghost", Toggle = true,
                Description = "A purple marker where your best lap was at this moment of the lap, so you can see if you're ahead of it or behind it.",
                Get = s => B(s.ShowGhost), Set = (s, v) => s.ShowGhost = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Ghost and replay", Name = "Ghost as ship projection", Toggle = true,
                Description = "On: the ghost is a see-through copy of the ship that set your best lap, flying it with the same heading and roll. Off: a small diamond.",
                Get = s => B(s.GhostProjection), Set = (s, v) => s.GhostProjection = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Ghost and replay", Name = "Orbit camera", Toggle = true,
                Description = "On: during a replay the camera circles the ghost and keeps aiming at it: move the mouse (or A / D, up / down) to swing round it, W / S to zoom in and out. Off: a fixed chase camera behind the ghost. Ctrl+Shift+C switches during a replay.",
                Get = s => B(s.ReplayOrbit), Set = (s, v) => s.ReplayOrbit = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Ghost and replay", Name = "Cinematic camera", Toggle = true,
                Description = "Turn on the game's cinematic spectator for replays: smooth, eased camera movement, and its look-around keys let you aim away from the ghost while it keeps tracking. If you lock the cinematic camera onto a real ship or player with the game's lock key, the game drives the camera until you unlock. Your own cinematic setting comes back when the replay ends.",
                Get = s => B(s.ReplayCinematic), Set = (s, v) => s.ReplayCinematic = v >= 0.5,
            },
        };
    }
}
