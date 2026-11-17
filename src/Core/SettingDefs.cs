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
        /// <summary>Only matters when the on/off setting with this name is on (or off, with DependsOnOff).</summary>
        public string DependsOn;
        public bool DependsOnOff;
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
            // ---- Menu
            new SettingDef
            {
                Section = "Menu", Name = "Filming", Toggle = true,
                Description = "The tools for filming and showing races off: the Cinematics tab (orbit camera, cinematics), the Race spotlight section of the Race tab (spotlight, tour, leaderboard) and their settings. Off: all of that is hidden, for a simpler menu when you only want to race. Nothing is deleted; turn it back on and it's all as you left it.",
                Get = s => B(s.Filming), Set = (s, v) => { s.Filming = v >= 0.5; GateSettings.FilmingOn = s.Filming; },
            },
            // ---- Race
            new SettingDef
            {
                Section = "Race", Name = "Race type", Min = 0, Max = 1, Step = 1,
                Choices = new[] { "Time trial", "Grand prix" },
                Description = "Time trial: one ship against the clock, chasing best laps, ghosts and records. This is what the plugin does today. Grand prix: ships racing each other from one start, placed by who crosses the line first. Grand prix is for multiplayer and has no rules of its own yet, so for now it races just like a time trial.",
                Get = s => s.RaceType, Set = (s, v) => s.RaceType = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Race", Name = "Only start going forwards", Toggle = true,
                Description = "On: going through the start gate backwards (away from the first checkpoint) doesn't start a race. Turn it off if your course starts by turning round behind the gate, or to start from either side.",
                Get = s => B(s.ForwardStartOnly), Set = (s, v) => s.ForwardStartOnly = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Race", Name = "Reset race teleports to the start", Toggle = true,
                Description = "On: the reset race key (Ctrl+R) also moves your ship back to just behind the start gate, facing the course (or to your own start spot: Courses tab > Start spot > Set here), and stops it (so with Countdown start on you get the countdown straight away). Uses the game's admin teleport, so it may not work on a server where you aren't an admin.",
                Get = s => B(s.ResetTeleports), Set = (s, v) => s.ResetTeleports = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Reset race teleports to the start",
                Section = "Race", Name = "Reset teleport exit speed (m/s, 0 = stopped)", Min = 0, Max = 300, Step = 5,
                Description = "The speed your ship has when it comes out of the reset race teleport, along the way it's facing: for a rolling start without flying back round. 0 = stopped (needed for Countdown start). Works in single player and when you host; on someone else's server the ship just stops.",
                Get = s => s.ResetExitSpeed, Set = (s, v) => s.ResetExitSpeed = v,
            },
            new SettingDef
            {
                Section = "Race", Name = "Flip ship: lift height (m)", Min = 0, Max = 50, Step = 1,
                Description = "The flip ship key (Ctrl+Shift+F) lifts your ship this far straight up and turns it level with gravity, facing the way it was heading: for when you've crashed and are stuck upside down. Needs gravity (on or near a planet or moon). Uses the game's admin teleport, so it may not work on a server where you aren't an admin.",
                Get = s => s.FlipHeight, Set = (s, v) => s.FlipHeight = v,
            },
            new SettingDef
            {
                Section = "Race", Name = "Flip ship: penalty in a race", Min = 0, Max = 3, Step = 1,
                Choices = new[] { "None", "Time penalty", "Counts as a crash", "Resets the race" },
                Description = "What flipping your ship back over costs during a race. Time penalty: the seconds below are added to the lap. Counts as a crash: the crash penalty (Settings > Crashes). Resets the race: the race starts again. Outside a race a flip is always free.",
                Get = s => s.FlipPenalty, Set = (s, v) => s.FlipPenalty = (int)Math.Round(v),
            },
            new SettingDef
            {
                DependsOn = "Flip ship: penalty in a race",
                Section = "Race", Name = "Flip ship: time penalty (seconds)", Min = 0, Max = 60, Step = 0.5,
                Description = "Seconds added to the lap each time you flip the ship during a race (with Flip ship: penalty in a race set to Time penalty).",
                Get = s => s.FlipPenaltySeconds, Set = (s, v) => s.FlipPenaltySeconds = v,
            },
            new SettingDef
            {
                Section = "Race spotlight", Name = "Spotlight page time (seconds)", Min = 2, Max = 60, Step = 1,
                Description = "Race spotlight: how long each page (race results, race stats, the ship) stays up before it changes to the next one.",
                Get = s => s.SpotlightPageSeconds, Set = (s, v) => s.SpotlightPageSeconds = v,
            },
            new SettingDef
            {
                Section = "Race spotlight", Name = "Tour: podium at the end", Toggle = true,
                Description = "After the tour's last ship has flown, a podium card shows the top three ships of the tour with their times, before the tour finishes.",
                Get = s => B(s.SpotTourPodium), Set = (s, v) => s.SpotTourPodium = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Tour: podium at the end",
                Section = "Race spotlight", Name = "Tour: podium seconds", Min = 3, Max = 60, Step = 1,
                Description = "How long the podium card stays up at the end of the tour.",
                Get = s => s.SpotTourPodiumSeconds, Set = (s, v) => s.SpotTourPodiumSeconds = v,
            },
            new SettingDef
            {
                Section = "Race spotlight", Name = "Tour: skip burned races", Toggle = true,
                Description = "On: the spotlight tour leaves out burned races (ones reset before the finish), so a ship whose only race file is burned isn't on the tour. Off: a ship with no finished race is shown with its burned one; a ship that has a finished race always uses that.",
                Get = s => B(s.SpotTourSkipBurned), Set = (s, v) => s.SpotTourSkipBurned = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Race spotlight", Name = "Leaderboard: seconds a place", Min = 0.3, Max = 6, Step = 0.1,
                Description = "How slowly the Leaderboard page moves: on the spotlight tour, how long the new ship takes over each place as it climbs from the bottom; with the one-at-a-time reveal, how long between ships coming on. Bigger = slower.",
                Get = s => s.SpotBoardClimbSeconds, Set = (s, v) => s.SpotBoardClimbSeconds = v,
            },
            new SettingDef
            {
                Section = "Race spotlight", Name = "Wait before the race and cinematic start (seconds)", Min = 0, Max = 30, Step = 0.5,
                Description = "Race spotlight with Auto race or its own cinematic: the ship is put at the start straight away, then waits this long before the autopilot sets off and the cinematic starts (and on the spotlight tour, for each ship). 0 = no wait.",
                Get = s => s.SpotlightStartDelay, Set = (s, v) => s.SpotlightStartDelay = v,
            },
            new SettingDef
            {
                Section = "Race spotlight", Name = "Spotlight page change speed (seconds, 0 = instant)", Min = 0, Max = 3, Step = 0.05,
                Description = "Race spotlight: how long a page takes to slide and fade out, and the next one to slide and fade in. 0 = the pages just swap.",
                Get = s => s.SpotlightSlideSeconds, Set = (s, v) => s.SpotlightSlideSeconds = v,
            },
            new SettingDef
            {
                Section = "Race", Name = "Countdown start", Toggle = true,
                Description = "Stop near the start gate and hold still to get a 3-2-1-GO countdown. Off (the default): no countdown, the race just starts when you fly through the gate.",
                Get = s => B(s.CountdownEnabled), Set = (s, v) => s.CountdownEnabled = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Countdown start",
                Section = "Race", Name = "Countdown distance from gate (m)", Min = 10, Max = 300, Step = 5,
                Description = "Stop within this distance of the start gate to get the 3-2-1-GO countdown.",
                Get = s => s.CountdownDistance, Set = (s, v) => s.CountdownDistance = v,
            },
            new SettingDef
            {
                DependsOn = "Countdown start",
                Section = "Race", Name = "Hold still before countdown (s)", Min = 0, Max = 5, Step = 0.5, Format = "{0:0.0}",
                Description = "How long you must stay stopped near the gate before the countdown starts. 0 turns the countdown off.",
                Get = s => s.CountdownHoldSeconds, Set = (s, v) => s.CountdownHoldSeconds = v,
            },
            new SettingDef
            {
                Section = "Race", Name = "Laps per race (0 = unlimited)", Min = 0, Max = 20, Step = 1,
                Description = "How many laps make a race. 0 keeps timing laps until you reset.",
                Get = s => s.LapTarget, Set = (s, v) => s.LapTarget = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Race", Name = "Practice laps (0 = none)", Min = 0, Max = 10, Step = 1,
                Description = "Laps flown before the race starts. They're timed but don't count towards the race or your best times. The race starts when you complete the last one at the gate.",
                Get = s => s.PracticeLaps, Set = (s, v) => s.PracticeLaps = (int)Math.Round(v),
            },
            // ---- HUD
            new SettingDef
            {
                Section = "HUD", Name = "Key reminders", Toggle = true,
                Description = "When the orbit camera, a cinematic, a replay, the race spotlight or the autopilot starts, a short line on screen reminds you of its keys (Esc to stop, W / S to zoom...). Off: no reminders, e.g. while you record. Messages about problems still show.",
                Get = s => B(s.KeyHints), Set = (s, v) => s.KeyHints = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "HUD layout", Min = 0, Max = 2, Step = 1, Choices = new[] { "Left", "Centre", "Right" },
                Description = "Where the timer goes. Left / Right: the timer in that top corner with the lap list under it; the minimap in the other corner with best race and best lap beside it and the splits under them. Centre: top centre, with best lap and best race beside it; minimap and lap list on the left, splits on the right.",
                Get = s => s.TimerInCorner ? (s.SwapSides ? 0 : 2) : 1,
                Set = (s, v) =>
                {
                    int layout = (int)Math.Round(v);
                    s.TimerInCorner = layout != 1;   // 0 Left, 1 Centre, 2 Right
                    s.SwapSides = layout == 0;
                },
            },
            new SettingDef
            {
                Section = "HUD", Name = "HUD size", Min = 0.5, Max = 2, Step = 0.05, Format = "{0:0.00}",
                Description = "Overall size of the race panels and text.",
                Get = s => s.HudScale, Set = (s, v) => s.HudScale = v,
            },
            new SettingDef
            {
                Section = "HUD", Name = "HUD opacity (%)", Min = 20, Max = 100, Step = 5,
                Description = "How solid the whole race HUD is: panels, text, minimap and speedometer together. 100 = as normal; lower lets more of the view show through. (Panel darkness below only changes the panels' backgrounds.)",
                Get = s => s.HudOpacity, Set = (s, v) => s.HudOpacity = v,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Minimal HUD", Toggle = true,
                Description = "On: while racing only the timer (with the gap to your best lap beside it), the speedometer and the next gate's marker show. The lap list, splits, best times, minimap, attitude and keys are left out. The results after the finish show as usual.",
                Get = s => B(s.MinimalHud), Set = (s, v) => s.MinimalHud = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Screen edge glow", Toggle = true,
                Description = "A soft glow round the edge of the screen that fades in and out: green when you pass a checkpoint (purple for the fastest ever there), red when you get a penalty.",
                Get = s => B(s.EdgeGlow), Set = (s, v) => s.EdgeGlow = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Panel darkness: use the game's", Toggle = true,
                Description = "On: the race panels' backgrounds use the game's UI background opacity (Options > Interface). Off: they use Panel darkness below.",
                Get = s => s.UseGameUiOpacity ? 1 : 0, Set = (s, v) => s.UseGameUiOpacity = v > 0.5,
            },
            new SettingDef
            {
                DependsOn = "Panel darkness: use the game's", DependsOnOff = true,
                Section = "HUD", Name = "Panel darkness", Min = 0, Max = 255, Step = 5,
                Description = "How dark the race panels' backgrounds are. 255 = solid. Only used when \"Panel darkness: use the game's\" is off.",
                Get = s => s.BackgroundOpacity, Set = (s, v) => s.BackgroundOpacity = (byte)Math.Round(v),
            },
            new SettingDef
            {
                Section = "HUD", Name = "Race HUD only", Toggle = true,
                Description = "Hide the game's own HUD (speed and altitude, toolbar, character status, hints, notifications) and show only the race HUD. Also a step in the game's HUD key (U): full, minimal, race HUD only, none.",
                Get = s => B(s.RaceHudOnly), Set = (s, v) => s.RaceHudOnly = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Race HUD only",
                Section = "HUD", Name = "Race HUD only: keep the gyro", Toggle = true,
                Description = "Keeps the game's flight gyro (the ring in the middle of the screen with the pitch angle) showing when Race HUD only hides the rest of the game's HUD.",
                Get = s => B(s.KeepGyro), Set = (s, v) => s.KeepGyro = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Race HUD only: keep the gyro",
                Section = "HUD", Name = "Gyro: target based", Toggle = true,
                Description = "Switches the game's gyro to its target based mode while the race HUD is showing. Off leaves the gyro the way the game has it.",
                Get = s => B(s.GyroTargetBased), Set = (s, v) => s.GyroTargetBased = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Speedometer", Min = 0, Max = 2, Step = 1, Choices = new[] { "Off", "With Race HUD only", "Always" },
                Description = "A round speed dial in the bottom left corner while you fly the course, in the game's speed unit: the arc fills as you speed up (orange near the top, red in the last tenth) with the speed in big numbers in the middle. With Race HUD only: shown when the game's own speed display is hidden.",
                Get = s => s.Speedometer, Set = (s, v) => s.Speedometer = (int)Math.Round(v),
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer max speed (0 = automatic)", Min = 0, Max = 5000, Step = 50,
                Description = "The speed at the top of the speedometer dial, in the game's speed unit (km/h, m/s or mph, Options > Interface). 0 = automatic: a round number a bit above the fastest this ship has gone, growing if you go faster. Faster than the max fills the dial to the end.",
                Get = s => s.SpeedometerMax, Set = (s, v) => s.SpeedometerMax = v,
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer resource bars", Toggle = true,
                Description = "Three rings round the speedometer: hydrogen, stored battery power and power use, with their names and levels in the pod on the dial's side. From the game's cockpit HUD, so only in a cockpit.",
                Get = s => B(s.SpeedoResourceBars), Set = (s, v) => s.SpeedoResourceBars = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer: power indicator", Toggle = true,
                Description = "A tile in the pod on the speedometer's side: orange while the ship's power is on.",
                Get = s => B(s.SpeedoPowerTile), Set = (s, v) => s.SpeedoPowerTile = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer: dampener indicator", Toggle = true,
                Description = "A tile in the pod on the speedometer's side: orange while the inertia dampeners are on.",
                Get = s => B(s.SpeedoDampenerTile), Set = (s, v) => s.SpeedoDampenerTile = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer: parking brake indicator", Toggle = true,
                Description = "A tile in the pod on the speedometer's side: orange while the parking brake is on.",
                Get = s => B(s.SpeedoParkingTile), Set = (s, v) => s.SpeedoParkingTile = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer: antenna indicator", Toggle = true,
                Description = "A tile in the pod on the speedometer's side: orange while the antenna is on.",
                Get = s => B(s.SpeedoAntennaTile), Set = (s, v) => s.SpeedoAntennaTile = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Speedometer",
                Section = "HUD", Name = "Speedometer: safe speed indicator", Toggle = true,
                Description = "A small green gauge in the speedometer's bottom gap while the game says you're at a safe speed.",
                Get = s => B(s.SpeedoSafeSpeed), Set = (s, v) => s.SpeedoSafeSpeed = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Show the toolbar slot you press (seconds, 0 = off)", Min = 0, Max = 10, Step = 0.5,
                Description = "With Race HUD only on (the game's toolbar hidden): pressing a toolbar number key (1 to 9, 0) shows that slot of the game's toolbar, with its own icon, for this long, so you can see what you switched. 0 = never.",
                Get = s => s.ToolbarPeekSeconds, Set = (s, v) => s.ToolbarPeekSeconds = v,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Toolbar bar", Toggle = true,
                Description = "With Race HUD only on (the game's toolbar hidden): a slim bar at the foot of the screen with each toolbar slot's icon on it, small, so you can see what's on the toolbar at a glance. It isn't the toolbar itself: pressing a slot's key still pops the game's own slot up for a moment.",
                Get = s => B(s.ToolbarMini), Set = (s, v) => s.ToolbarMini = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Toolbar bar",
                Section = "HUD", Name = "Toolbar bar: icon size (%)", Min = 30, Max = 90, Step = 5,
                Description = "How big the toolbar bar's icons are, as a share of a toolbar slot.",
                Get = s => s.ToolbarMiniSize, Set = (s, v) => s.ToolbarMiniSize = v,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Gravity indicator", Toggle = true,
                Description = "A small panel in the bottom right corner, beside the roll and pitch panel: how strong gravity is where your ship is (in g), an arrow showing which way it pulls on the ship (seen from behind it: straight down when you're level over a planet), and how much of it is natural (N) and artificial (A) when gravity generators add theirs. Needs the speedometer on.",
                Get = s => B(s.GravityPanel), Set = (s, v) => s.GravityPanel = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Flight keys", Toggle = true,
                Description = "Shows Q W E, A S D, C and Space in the bottom right corner, lighting up while they're held: handy for videos and for seeing your inputs.",
                Get = s => B(s.FlightKeys > 0), Set = (s, v) => s.FlightKeys = v >= 0.5 ? 2 : 0,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Roll indicator", Toggle = true,
                Description = "Shows how far your ship is rolled from level, in the bottom right corner (beside the flight keys): a bar that tilts with the ship, the angle in degrees (R = right wing down) and under it how fast it's rolling in degrees a second. Needs gravity to know which way is up, so it only shows on or near a planet or moon.",
                Get = s => B(s.RollIndicator), Set = (s, v) => s.RollIndicator = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Pitch indicator", Toggle = true,
                Description = "Shows how far your ship's nose is above (UP) or below (DOWN) the horizon in degrees, in the same box as the roll indicator in the bottom right corner. Needs gravity to know which way is up, so it only shows on or near a planet or moon.",
                Get = s => B(s.PitchIndicator), Set = (s, v) => s.PitchIndicator = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Height above ground", Toggle = true,
                Description = "Under the speedometer: how high the ship is above the ground straight below it (the terrain, not sea level). Only on or near a planet or moon. Needs the speedometer on.",
                Get = s => B(s.GroundHeight), Set = (s, v) => s.GroundHeight = v >= 0.5,
            },
            new SettingDef
            {
                Section = "HUD", Name = "Speed vector marker", Toggle = true,
                Description = "A white ring with wings, outlined in black, where the ship is actually heading (its velocity), which can be well off where it's pointing when you drift through a corner.",
                Get = s => B(s.VelocityMarker), Set = (s, v) => s.VelocityMarker = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Sound", Name = "Sounds", Toggle = true,
                Description = "All of the plugin's sounds: the countdown, checkpoints, laps, penalties and new records. (The game sounds used can be changed in RaceDisplay.json.)",
                Get = s => B(s.Sounds), Set = (s, v) => s.Sounds = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Sounds",
                Section = "Sound", Name = "Checkpoint sound", Toggle = true,
                Description = "A beep each time you fly through a checkpoint in a race. (Missed checkpoints play the penalty sound instead.)",
                Get = s => B(s.CheckpointSound), Set = (s, v) => s.CheckpointSound = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Sounds",
                Section = "Sound", Name = "Penalty melody", Toggle = true,
                Description = "Play a short melody when you get a penalty (a missed checkpoint or a crash) instead of the double beep. To use your own sound, put a Penalty.wav in %AppData%\\SpaceEngineers2\\AppData\\RaceDisplay.",
                Get = s => B(s.PenaltyMelody), Set = (s, v) => s.PenaltyMelody = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Penalty melody",
                Section = "Sound", Name = "Penalty melody volume", Min = 0, Max = 100, Step = 5,
                Description = "How loud the penalty melody is, in %. It plays through Windows, so the game's volume settings don't change it.",
                Get = s => s.PenaltyVolume, Set = (s, v) => s.PenaltyVolume = Math.Clamp(Math.Round(v), 0, 100),
            },
            // ---- Panels
            new SettingDef
            {
                Section = "Panels", Name = "Gap under the timer", Toggle = true,
                Description = "The red / green number under the lap time: how far behind (+) or ahead (-) of the best lap you are. Off for race days or videos, where people watching won't know what it's compared with. The ghost's label and the splits still show gaps.",
                Get = s => B(s.TimerGap), Set = (s, v) => s.TimerGap = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Lap list", Toggle = true,
                Description = "The list of lap times (and the results under it after the finish).",
                Get = s => B(s.ShowLapList), Set = (s, v) => s.ShowLapList = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Lap list",
                Section = "Panels", Name = "Laps shown in the list", Min = 3, Max = 20, Step = 1,
                Description = "How many laps the lap list on the left shows before older ones scroll off.",
                Get = s => s.MaxLapRows, Set = (s, v) => s.MaxLapRows = (int)Math.Round(v),
            },
            new SettingDef
            {
                DependsOn = "Lap list",
                Section = "Panels", Name = "Lap penalties as icons", Toggle = true,
                Description = "On: a lap with penalties shows a red ring for each missed checkpoint and a red cross for each crash. Off: it says it in words instead, e.g. \"+15s  2 missed, 1 crash\".",
                Get = s => B(s.PenaltyIcons), Set = (s, v) => s.PenaltyIcons = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Splits panel", Toggle = true,
                Description = "Checkpoint times and gaps during the race, and the full splits table after the finish.",
                Get = s => B(s.ShowSplits), Set = (s, v) => s.ShowSplits = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Race stats", Toggle = true,
                Description = "After the finish: ship, distance, G-forces, time near top speed, average lap and consistency, in a box in the top corner.",
                Get = s => B(s.ShowRaceStats), Set = (s, v) => s.ShowRaceStats = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Best lap / best race boxes", Toggle = true,
                Description = "The boxes either side of the timer with your best lap (and possible lap) and best race.",
                Get = s => B(s.ShowBestBoxes), Set = (s, v) => s.ShowBestBoxes = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Sector times after the finish", Toggle = true,
                Description = "On: the table after the finish shows each sector's time (checkpoint to checkpoint). Off: lap time at each checkpoint.",
                Get = s => B(s.SectorTable), Set = (s, v) => s.SectorTable = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Results shown for (s, 0 = until next race)", Min = 0, Max = 60, Step = 1,
                Description = "How long the results stay on screen after the finish before fading out. 0 keeps them until you start the next race.",
                Get = s => s.ResultsSeconds, Set = (s, v) => s.ResultsSeconds = v,
            },
            new SettingDef
            {
                Section = "Panels", Name = "Results graphs", Min = 0, Max = 2, Step = 1,
                Choices = new[] { "Off", "Speed only", "Speed and time gap" },
                Description = "After a race the graphs show in their own panel on the right (on the left with the panels swapped): speed round the lap on top and the time gap round the lap under it.",
                Get = s => s.ResultsGraphs, Set = (s, v) => s.ResultsGraphs = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Panels", Name = "Results against this ship", Toggle = true,
                Description = "Off: the race results compare with the best on the course in any ship. On: with the best in the ship you flew. The results shortcut (Ctrl+B) switches while they're showing.",
                Get = s => B(s.ResultsForShip), Set = (s, v) => s.ResultsForShip = v >= 0.5,
            },
            // ---- Markers
            new SettingDef
            {
                Section = "Markers", Name = "Checkpoint markers", Toggle = true,
                Description = "Always-visible markers on the gate and checkpoints.",
                Get = s => B(s.ShowMarkers), Set = (s, v) => s.ShowMarkers = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Checkpoint markers",
                Section = "Markers", Name = "Marker size (%)", Min = 1, Max = 10, Step = 0.5, Format = "{0:0.0}",
                Description = "Size of the markers, as a share of the distance to them (they stay the same size on screen).",
                Get = s => s.MarkerSize * 100, Set = (s, v) => s.MarkerSize = v / 100,
            },
            new SettingDef
            {
                DependsOn = "Checkpoint markers",
                Section = "Markers", Name = "Marker icon", Min = 0, Max = 3, Step = 1,
                Choices = new[] { "Ring", "Diamond", "Chevron", "Beacon" },
                Description = "The icon drawn at each checkpoint and gate. Ring: a circle that always faces you. Diamond: the old wire diamond. Chevron: an arrow head pointing down at the spot. Beacon: a tall thin beam standing on it, easy to pick out from far off. The one to fly to next is bigger and moves: the ring pulses with cross marks, the diamond spins, the chevron doubles up and bobs, the beacon has a ring climbing it.",
                Get = s => s.MarkerIcon, Set = (s, v) => s.MarkerIcon = (int)Math.Round(v),
            },
            new SettingDef
            {
                DependsOn = "Checkpoint markers",
                Section = "Markers", Name = "Thick marker lines", Toggle = true,
                Description = "Draws the markers with thick, smooth lines through the layer the game uses for its build previews. Off (or if that layer can't be used): thin plain lines.",
                Get = s => B(s.MarkerThick), Set = (s, v) => s.MarkerThick = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Markers", Name = "Checkpoint labels and arrow", Toggle = true,
                Description = "HUD labels on the checkpoints, and an arrow at the edge of the screen pointing to the next one when it's off screen or behind you.",
                Get = s => B(s.ShowCheckpointArrow), Set = (s, v) => s.ShowCheckpointArrow = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Markers", Name = "Only mark the next checkpoints", Toggle = true,
                Description = "On: only the next few checkpoints (and the gate once it's among them) have markers and labels, so a long or twisting course isn't cluttered. How many: Checkpoints marked ahead. Off: every checkpoint is marked. Edit mode always shows them all.",
                Get = s => B(s.LimitMarkers), Set = (s, v) => s.LimitMarkers = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Only mark the next checkpoints",
                Section = "Markers", Name = "Checkpoints marked ahead", Min = 1, Max = 10, Step = 1,
                Description = "With Only mark the next checkpoints on: how many of the coming checkpoints have markers (the next one counts as 1). Before a race the gate counts as the first.",
                Get = s => s.MarkersAhead, Set = (s, v) => s.MarkersAhead = (int)Math.Round(v),
            },
            // ---- Minimap
            new SettingDef
            {
                Section = "Minimap", Name = "Minimap", Toggle = true,
                Description = "A small round map of the course above the lap list, with a label by each gate and checkpoint and the racing line in its racing line colours. It zooms in and follows you while you're moving, and shows the whole course when you stop. It shows the route between the gates and checkpoints (the next one orange), your best lap's line, you (the arrow) and the ghosts. The course's start gate faces up.",
                Get = s => B(s.ShowMinimap), Set = (s, v) => s.ShowMinimap = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Minimap",
                Section = "Minimap", Name = "Minimap: heading up", Toggle = true,
                Description = "On: the minimap turns so your ship always points up and the course swings round it, like a sat nav. Off: the map stays put and your arrow turns.",
                Get = s => B(s.MinimapHeadingUp), Set = (s, v) => s.MinimapHeadingUp = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Minimap",
                Section = "Minimap", Name = "Minimap size", Min = 150, Max = 500, Step = 10,
                Description = "How big the minimap is (it also follows the HUD size).",
                Get = s => s.MinimapSize, Set = (s, v) => s.MinimapSize = v,
            },
            new SettingDef
            {
                DependsOn = "Minimap",
                Section = "Minimap", Name = "Minimap zooms in while moving", Toggle = true,
                Description = "On: from 20 m/s the minimap zooms in and follows you, and zooms back out to the whole course as you slow down. Off: it always shows the whole course.",
                Get = s => B(s.MinimapFollow), Set = (s, v) => s.MinimapFollow = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Minimap",
                Section = "Minimap", Name = "Minimap replays the laps after a race", Toggle = true,
                Description = "On: while the results are up, a white dot flies each lap again on the minimap, then the results move on to the next lap (best lap, lap 1, lap 2...), graphs and all. Off: the minimap just shows the lap picked with the results lap keys.",
                Get = s => B(s.MinimapLapReplay), Set = (s, v) => s.MinimapLapReplay = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Minimap zooms in while moving",
                Section = "Minimap", Name = "Minimap zoom while moving (m)", Min = 300, Max = 10000, Step = 100,
                Description = "While you're moving, the minimap zooms in and follows you, showing this much of the course across. When you stop it zooms back out to the whole course. Set it to the top for the whole course all the time.",
                Get = s => s.MinimapZoom, Set = (s, v) => s.MinimapZoom = v,
            },
            new SettingDef
            {
                Section = "Minimap", Name = "Sector colours", Toggle = true,
                Description = "Colour each sector like F1 timing: purple = fastest ever on this course, green = your best this race, yellow = slower. Shown on the checkpoint dots under the timer as you fly through, and in the splits.",
                Get = s => B(s.SectorColours), Set = (s, v) => s.SectorColours = v >= 0.5,
            },
            // ---- Ghost and racing line
            new SettingDef
            {
                Section = "Ghost and racing line", Name = "Ghost ship detail", Min = 0, Max = 2, Step = 1,
                Choices = new[] { "Full", "Outline only", "Box" },
                Description = "How the ghost ship is drawn. Full: its see-through hull and glowing outline (the hull is left out when the ghost is right beside you, and a far-off ghost is a box, to keep the frame rate up). Outline only: just the outline, much lighter on the frame rate. Box: a plain see-through box, the lightest. Try Outline only or Box if the frame rate drops near the ghost.",
                Get = s => s.GhostDetail, Set = (s, v) => s.GhostDetail = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Ghost and racing line", Name = "Smooth the possible line (seconds each side of a checkpoint)", Min = 0, Max = 4, Step = 0.25, Format = "{0:0.##} s",
                Description = "The best possible line is stitched together from the best bit of the course between each pair of checkpoints, which usually come from different laps: so at every checkpoint it jumps from where one lap went through the gate to where another did, and from one speed to another. This eases those joins over this many seconds either side: the two paths meet halfway (still through the gate), the ship's turn is blended, and the speed changes smoothly instead of all at once, keeping the sector times the same. 0 = no smoothing.",
                Get = s => s.PossibleSmoothing, Set = (s, v) => s.PossibleSmoothing = v,
            },
            new SettingDef
            {
                Section = "Ghost and racing line", Name = "Ghost", Toggle = true,
                Description = "A purple marker where your best lap was at this moment of the lap, so you can see if you're ahead of it or behind it.",
                Get = s => B(s.ShowGhost), Set = (s, v) => s.ShowGhost = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Ghost",
                Section = "Ghost and racing line", Name = "Ghost as ship projection", Toggle = true,
                Description = "On: the ghost is a see-through copy of the ship that set your best lap, flying it with the same heading and roll. Off: a small diamond.",
                Get = s => B(s.GhostProjection), Set = (s, v) => s.GhostProjection = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Ghost and racing line", Name = "Racing line", Toggle = true,
                Description = "A faint purple line through the world along your best lap's path.",
                Get = s => B(s.ShowRacingLine), Set = (s, v) => s.ShowRacingLine = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Racing line",
                Section = "Ghost and racing line", Name = "Racing line colours", Min = 0, Max = 2, Step = 1, Choices = new[] { "Plain", "Speed", "Braking" },
                Description = "How the best lap's racing line is coloured, to see where time is won and lost. Speed: red where it was slow (braking, tight turns), through yellow, to green where it was fastest. Braking: red where it was slowing down (the harder, the redder), green where it was speeding up, white where it held its speed. Plain: magenta. (With a 2nd ghost on the lines are purple and orange.)",
                Get = s => s.RacingLineColourMode, Set = (s, v) => s.RacingLineColourMode = (int)Math.Round(v),
            },
            new SettingDef
            {
                DependsOn = "Racing line",
                Section = "Ghost and racing line", Name = "Racing line thickness", Min = 0.5, Max = 4, Step = 0.25, Format = "{0:0.00}x",
                Description = "How thick the racing line is drawn. 1 = normal.",
                Get = s => s.RacingLineWidth, Set = (s, v) => s.RacingLineWidth = v,
            },
            new SettingDef
            {
                Section = "Ghost and racing line", Name = "Smooth lines (game 3D UI)", Toggle = true,
                Description = "Draws the racing line and the ghost ship's outline through the layer the game uses for its build previews: one thick, smooth line instead of several thin ones. Off (or if that layer can't be used): the plain lines as before.",
                Get = s => B(s.GlowLines), Set = (s, v) => s.GlowLines = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Racing line",
                Section = "Ghost and racing line", Name = "Racing line opacity (%)", Min = 10, Max = 100, Step = 5,
                Description = "How see-through the racing line is. 100 = solid.",
                Get = s => s.RacingLineOpacity, Set = (s, v) => s.RacingLineOpacity = v,
            },
            new SettingDef
            {
                DependsOn = "Racing line",
                Section = "Ghost and racing line", Name = "Racing line distance (m)", Min = 300, Max = 6000, Step = 100,
                Description = "How far from you the racing line is drawn. Shorter keeps the view clearer; longer shows more of what's coming.",
                Get = s => s.RacingLineDistance, Set = (s, v) => s.RacingLineDistance = v,
            },
            new SettingDef
            {
                DependsOn = "Racing line",
                Section = "Ghost and racing line", Name = "Braking markers", Toggle = true,
                Description = "Markers on the racing line where your best lap started braking hard (red BRAKE, with the speed it braked from) and where it was speeding up again (green GO, with the slowest speed through the corner).",
                Get = s => B(s.BrakeMarkers), Set = (s, v) => s.BrakeMarkers = v >= 0.5,
            },
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
                Section = "Penalties", Name = "Crash immunity until back up to speed", Toggle = true,
                Description = "On: after a crash penalty, further crashes don't add a penalty until you're flying faster than Crash immunity speed again (so scraping along or bumping about after a crash only counts once). Works together with Crash immunity (s): both have to be over.",
                Get = s => B(s.CrashImmuneUntilSpeed), Set = (s, v) => s.CrashImmuneUntilSpeed = v >= 0.5,
            },
            new SettingDef
            {
                DependsOn = "Crash immunity until back up to speed",
                Section = "Penalties", Name = "Crash immunity speed (m/s)", Min = 5, Max = 200, Step = 5, Format = "{0:0}",
                Description = "With Crash immunity until back up to speed on: the speed to get back above before a crash counts again.",
                Get = s => s.CrashRearmSpeed, Set = (s, v) => s.CrashRearmSpeed = v,
            },
            // ---- Replay
            new SettingDef
            {
                Section = "Replay", Name = "Orbit camera", Toggle = true,
                Description = "On: during a replay the camera circles the ghost and keeps aiming at it: move the mouse (or A / D, up / down) to swing round it, W / S to zoom in and out. Off: a fixed chase camera behind the ghost. Ctrl+Shift+C switches during a replay.",
                Get = s => B(s.ReplayOrbit), Set = (s, v) => s.ReplayOrbit = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Replay", Name = "Cinematic camera", Toggle = true,
                Description = "Turn on the game's cinematic spectator for replays: smooth, eased camera movement, and its look-around keys let you aim away from the ghost while it keeps tracking. If you lock the cinematic camera onto a real ship or player with the game's lock key, the game drives the camera until you unlock. Your own cinematic setting comes back when the replay ends.",
                Get = s => B(s.ReplayCinematic), Set = (s, v) => s.ReplayCinematic = v >= 0.5,
            },
            // ---- Autopilot
            new SettingDef
            {
                Section = "Race", Name = "Autopilot speed (% of the lap)", Min = 25, Max = 150, Step = 5, Format = "{0:0}%",
                Description = "The autopilot (Ctrl+Shift+', or Race tab > Autopilot) flies your ship along the racing line by itself. 100% flies it as fast as the lap it was made from; less is slower (easier to watch, or to learn the line from); more is faster than the lap.",
                Get = s => s.AutopilotSpeed, Set = (s, v) => s.AutopilotSpeed = v,
            },
            // ---- Cinematics
            new SettingDef
            {
                Section = "Cinematics", Name = "Follow with the game's own lock", Toggle = true,
                Description = "Orbit and chase shots, and the orbit camera, are kept on the ship by the game's own spectator lock (the one its own spectator uses), which moves the camera with the ship as each frame is drawn: much smoother at speed. Off: the plugin moves the camera itself every update.",
                Get = s => B(s.CineGameLock), Set = (s, v) => s.CineGameLock = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Cinematics", Name = "Live timing strip", Toggle = true,
                Description = "While a cinematic films the autopilot flying a race: the lap, the race clock and the speed along the bottom of the screen, like a TV broadcast.",
                Get = s => B(s.FilmStrip), Set = (s, v) => s.FilmStrip = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Cinematics", Name = "Hide the HUD", Toggle = true,
                Description = "While a cinematic plays: the race HUD (timer, laps, splits, minimap, speedometer, flight keys, roll, checkpoint labels, messages) and the game's own HUD are hidden, for a clean picture. Fades, film bars, the live timing strip, the name tag and race spotlight's pages still show. The HUD comes back when the cinematic stops.",
                Get = s => B(s.CineHideHud), Set = (s, v) => s.CineHideHud = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Cinematics", Name = "Name tag on the ship", Toggle = true,
                Description = "While a cinematic or the race spotlight films a ship: a small tag with its name (and its speed, when the timing strip isn't showing) that follows it on screen.",
                Get = s => B(s.FilmNameTag), Set = (s, v) => s.FilmNameTag = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Cinematics", Name = "Recording speed", Min = 25, Max = 100, Step = 5, Format = "{0:0}%",
                Description = "Runs the autopilot and cinematics slower than real time, for a smoother recording: at speed the game can't load the terrain fast enough and the picture judders. 50% = half speed: speed the video up 2x afterwards (25% = 4x, 75% = 1.33x) and it plays at race speed. Shot lengths, orbit turns and effects all slow down together, so the timing is right once sped up. 100% = real time.",
                Get = s => s.RecordingSpeed, Set = (s, v) => s.RecordingSpeed = Math.Clamp(Math.Round(v / 5) * 5, 25, 100),
            },
            new SettingDef
            {
                Section = "Cinematics", Name = "Show the timeline while a cinematic plays (debug)", Toggle = true,
                Description = "On: while a cinematic plays, its timeline is drawn at the bottom of the screen (the shots, the effects layer, a line at the time it has reached, and what is playing now) and the race HUD is hidden. For checking a cinematic; turn it off to record.",
                Get = s => B(s.CineDebug), Set = (s, v) => s.CineDebug = v >= 0.5,
            },
            // ---- Orbit camera
            new SettingDef
            {
                Section = "Orbit camera", Name = "Orbit camera turns with the ship", Toggle = true,
                Description = "Off: the orbit camera keeps its angle in the world while the ship turns under it. On: the view is fixed to the ship and turns (and rolls) with it, like a chase camera you can put anywhere. The orbit lock key swaps this while the camera is on.",
                Get = s => B(s.OrbitLocked), Set = (s, v) => s.OrbitLocked = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Orbit camera", Name = "Orbit camera smooth follow (%)", Min = 0, Max = 100, Step = 5,
                Description = "How loosely the orbit camera follows the ship. 0 = fixed to it. Higher: the ship pulls ahead in the picture when it speeds up and the camera eases after it.",
                Get = s => s.OrbitSmooth, Set = (s, v) => s.OrbitSmooth = v,
            },
            new SettingDef
            {
                Section = "Orbit camera", Name = "Orbit camera auto rotate (degrees a second, 0 = off)", Min = -45, Max = 45, Step = 1,
                Description = "When you leave the mouse alone for 3 seconds, the orbit camera slowly circles the ship by itself at this speed. 0 = it stays where you put it; below 0 it circles the other way. Ctrl + mouse wheel changes it while the orbit camera is on.",
                Get = s => s.OrbitAutoRotate, Set = (s, v) => s.OrbitAutoRotate = v,
            },
            new SettingDef
            {
                Section = "Orbit camera", Name = "Ctrl + mouse wheel changes the auto rotate", Toggle = true,
                Description = "The game doesn't tell plugins about the mouse wheel, so this watches it with a Windows mouse hook, only while the orbit camera is on (it only adds up how far the wheel turns; nothing else is looked at or kept). Some anti-cheat or antivirus programs dislike mouse hooks: turn this off and the hook is never used.",
                Get = s => B(s.OrbitWheel), Set = (s, v) => s.OrbitWheel = v >= 0.5,
            },
            // ---- Course editing
            new SettingDef
            {
                Section = "Course editing", Name = "Fit to ring opening", Toggle = true,
                Description = "Fit gates and checkpoints to the hole in a ring or arch, ignoring its legs. Off = the whole grid.",
                Get = s => B(s.FitToOpening), Set = (s, v) => s.FitToOpening = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Extra space round grids (m)", Min = 0, Max = 20, Step = 0.5, Format = "{0:0.0}",
                Description = "Added on every side of a gate or checkpoint when it's placed.",
                Get = s => s.GridPadding, Set = (s, v) => s.GridPadding = v,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Edit mode reach (m)", Min = 50, Max = 5000, Step = 50,
                Description = "In course edit mode, how far away a grid can be when you aim at it and click.",
                Get = s => s.GridSearchDistance, Set = (s, v) => s.GridSearchDistance = v,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Air gate size (m)", Min = 5, Max = 300, Step = 5,
                Description = "Size of a gate or checkpoint placed in open air (placement mode In the air, Shift+T in edit mode; no grid needed). The grow / shrink keys change it while placing in the air.",
                Get = s => s.AirCheckpointSize, Set = (s, v) => s.AirCheckpointSize = v,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Air gate distance (m)", Min = 5, Max = 500, Step = 5,
                Description = "How far ahead of you (along where you're looking) a gate or checkpoint is placed with In the air on.",
                Get = s => s.AirCheckpointDistance, Set = (s, v) => s.AirCheckpointDistance = v,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Air gate height (m)", Min = 0, Max = 300, Step = 5,
                Description = "Height of a gate or checkpoint placed in open air, when it should be different from its size (which is then its width): a wide low gate, or a tall narrow one; with round air gates on it's an oval. 0 = the same as the size. The air gate taller / shorter keys change it while placing in the air.",
                Get = s => s.AirGateHeight, Set = (s, v) => s.AirGateHeight = v,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Round air gates", Toggle = true,
                Description = "Gates and checkpoints placed in open air are rings instead of square frames: you have to fly through the inside of the ring. Only changes the ones you place from now on; ones already placed keep their shape. The air gate ring / square key switches it while placing in the air.",
                Get = s => B(s.AirGateRound), Set = (s, v) => s.AirGateRound = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Show air gates", Toggle = true,
                Description = "Draws the gates and checkpoints placed in open air, since there's no ring there to see: the one to fly through next is bright with a ring closing in on its middle, the ones after it are faint, ones already flown turn green, and a red cross shows when you're on the wrong side of the next one. Hidden behind terrain and ships. (Always drawn in course edit mode.)",
                Get = s => B(s.AirGateDraw), Set = (s, v) => s.AirGateDraw = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Air gate look", Min = 0, Max = 2, Step = 1,
                Choices = new[] { "Lines", "Glow", "Glow (game 3D UI)" },
                Description = "How air gates are drawn outside course edit mode. Lines: thin outlines. Glow: a bright rim with a soft halo fading out either side, and a faint film across the opening. Glow (game 3D UI): the same, drawn through the layer the game uses for its build previews and symmetry planes, with its shader: thick smooth lines and smooth fades. Experimental: if that layer can't be used it falls back to Glow by itself (the log says why).",
                Get = s => s.AirGateLook, Set = (s, v) => s.AirGateLook = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Course editing", Name = "Best lap line in edit mode", Toggle = true,
                Description = "Show your best lap's racing line while editing the course, to see how checkpoints sit against the line people fly. Also switched with Shift+L in edit mode (Controls tab: Best lap line while editing).",
                Get = s => B(s.EditRacingLine), Set = (s, v) => s.EditRacingLine = v >= 0.5,
            },
            // ---- Advanced
            new SettingDef
            {
                Section = "Advanced", Name = "Remove grids lost inside a planet", Toggle = true,
                Description = "When the game finds a grid inside a planet's core as a world loads (it warns the save may be unplayable, then often crashes), the plugin deletes that grid so the world loads. Save the world afterwards to keep it fixed. Only grids the game itself reports are removed; single player or hosting only.",
                Get = s => B(s.RemoveLostGrids), Set = (s, v) => s.RemoveLostGrids = v >= 0.5,
            },
            new SettingDef
            {
                Section = "Advanced", Name = "Crash minimum speed (m/s)", Min = 0, Max = 150, Step = 1,
                Description = "Crashes only count when you were going at least this fast, with either crash detection (for hull damage: your top speed in the half second before the hit), so bumps while parking or lining up never cost you.",
                Get = s => s.CrashMinSpeed, Set = (s, v) => s.CrashMinSpeed = v,
            },
            new SettingDef
            {
                Section = "Advanced", Name = "Crash hardness (m/s²)", Min = 10, Max = 150, Step = 5,
                Description = "How sudden the stop must be to count as a crash. Lower = more sensitive. The default is 20 (about 2 g); raise it if hard braking counts as a crash.",
                Get = s => s.CrashDeceleration, Set = (s, v) => s.CrashDeceleration = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash detection", Min = 0, Max = 3, Step = 1, Choices = new[] { "Hull damage", "Speed drop", "Either", "Impact" },
                Description = "Hull damage: a crash is when your ship's blocks take collision damage (the game's own damage), or the game reports a hit even with no damage (e.g. a mod that turns collision damage off), so hard braking can't count and scrapes do. It needs the game's server in your game (single player or hosting); where it can't see hits (e.g. on a dedicated server) it uses the speed drop instead.\n\n"
                    + "Speed drop: a sudden loss of speed (penalty hardness). Either: hull damage or speed drop.\n\n"
                    + "Impact: no damage needed at all. A crash is slowing down harder than the ship's own thrusters ever could (worked out from its thrust and mass), so braking, however hard, never counts but hitting something does; hits the game reports count too. Good with mods that make ships take no collision damage.",
                Get = s => s.CrashDetection, Set = (s, v) => s.CrashDetection = (int)Math.Round(v),
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash hit strength needed", Min = 0, Max = 50, Step = 0.5,
                Description = "With Hull damage or Impact: how hard a hit the game reports (with or without damage) must be to count as a crash, relative to the ship's mass. 0 = any hit at or above the crash minimum speed. The log (RaceDisplay.log) shows the strength of the first few hits, to help pick a value.",
                Get = s => s.CrashMinHit, Set = (s, v) => s.CrashMinHit = v,
            },
            new SettingDef
            {
                Section = "Penalties", Name = "Crash damage needed", Min = 0, Max = 500, Step = 5,
                Description = "With Hull damage: how much collision damage one hit must do (all blocks together) to count as a crash. 0 = any damage. The log (RaceDisplay.log) shows the damage of the first few hits, to help pick a value.",
                Get = s => s.CrashMinDamage, Set = (s, v) => s.CrashMinDamage = v,
            },
            new SettingDef
            {
                Section = "Advanced", Name = "Crash minimum speed lost (m/s)", Min = 1, Max = 50, Step = 1,
                Description = "The impact must take away at least this much speed, so small scrapes don't count.",
                Get = s => s.CrashMinSpeedLoss, Set = (s, v) => s.CrashMinSpeedLoss = v,
            },
            new SettingDef
            {
                Section = "Advanced", Name = "Re-arm distance (m)", Min = 5, Max = 300, Step = 5,
                Description = "How far you must fly from the gate before the next pass through it counts.",
                Get = s => s.RearmDistance, Set = (s, v) => s.RearmDistance = v,
            },
            new SettingDef
            {
                Section = "Advanced", Name = "Shortest lap (s)", Min = 0, Max = 60, Step = 1,
                Description = "Laps quicker than this are ignored (stops double counts at the gate).",
                Get = s => s.MinLapSeconds, Set = (s, v) => s.MinLapSeconds = v,
            },
        };
    }
}
