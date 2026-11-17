using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceDisplay.Core
{
    /// <summary>
    /// Ties it together each frame: reads shortcuts, feeds positions to the LapTimer,
    /// and draws the HUD panels and gate outline.
    /// </summary>
    public sealed class RaceGateController
    {
        private const double GrowFactor = 1.25;

        private readonly IGameBridge game;
        private readonly LapTimer timer = new LapTimer();
        private readonly string settingsPath;
        private readonly GateSettings settings;
        private readonly HudRenderer hud;
        private readonly CrashDetector crash = new CrashDetector();
        private readonly KeyInput keys = new KeyInput();

        // Controls menu (Esc > Racing Display): open/closed, selected row, waiting for a new key.
        private bool controlsOpen;
        private int controlsRow;
        private bool capturing;
        private string controlsNote;
        private int settingsVersion;          // bumped when the menu's Settings tab must reload its values
        private long settingsDirtySince = -1; // slider changes are saved shortly after the last one
        private string loadedWorld;
        private Vec3D? playerPosition; // for marker distance
        private double worldLoadedAt;

        private string message;
        private HudColor messageColour = HudColor.Yellow;
        private double messageUntil;

        // Race messages (GO, crash, checkpoint, lap) go above the lap list.
        private string raceMessage;
        private HudColor raceMessageColour;
        private double raceMessageUntil;

        // Live gap to the best lap (timer panel), results panel, timer flash.
        private string gapText;
        private HudColor gapColour;
        private HudModel.ResultsPanel results, shipResults; // against the course best / this ship's best
        private double resultsUntil;
        private HudColor? flashColour;
        private double flashUntil;

        // Countdown (standing start): stop within CountdownDistance of the gate, hold still, 3-2-1-GO.
        private double? stillSince, countdownStart;
        private double goUntil = double.NegativeInfinity; // "GO!" under the timer until this time
        private double lastGoShown = double.NegativeInfinity;
        private bool countdownAllowed = true; // must leave the gate area after a race before the next countdown
        private int lastCountdownBeep = -1;

        // Current lap: path (for the live gap next time), top speed and distance (for average speed).
        private const double TraceInterval = 0.25;
        private List<double[]> lapTrace = new List<double[]>();
        private double lapTopSpeed, lapDistance;
        private Vec3D? lapLastPos;
        private readonly List<(double Top, double Avg)> lapSpeeds = new List<(double, double)>(); // race laps
        private List<double[]> lapSpeedTrace = new List<double[]>();              // [distance so far, speed]
        private readonly List<List<double[]>> raceSpeedTraces = new List<List<double[]>>(); // per race lap, [share 0..1, speed]
        private readonly List<List<double[]>> raceLapTraces = new List<List<double[]>>();   // per race lap, its path (for the Race Map comparison)
        // Race Map after the race: your best lap this race against the best lap from before it.
        private List<double[]> recordTraceAtStart, compareMine, compareRef;
        private string compareMineLabel, compareRefLabel;
        private double currentSpeed;
        // G-force tracking: velocity a moment ago, and this lap's figures.
        private Vec3D? gLastVel;
        private double gLastTime, lapMaxG, lapMaxTurnG, lapTurnGSum, lapTurnTime, lapMaxBrakeG;
        private readonly List<LapStats> lapStats = new List<LapStats>();               // race laps
        private readonly List<double> raceSpeedSamples = new List<double>();            // every quarter second of the race
        private List<(string Label, string Value, HudColor Colour)> raceStats;
        private string raceShipName;
        private readonly List<double[]> lapCheckpointMarks = new List<double[]>(); // [distance so far, speed, checkpoint number]
        private List<double[]> currentShip; // shape of the ship flying this race (for the ghost projection)

        // Best-lap replay: the spectator camera follows the ghost round the best lap.
        private bool replayPending, replaying, replayCameraReady;
        private volatile bool escapeStop, escapeAnalyser;
        // Race results: on screen right now (so Esc closes them), and closed with Esc (until the next finish).
        private volatile bool resultsOnScreen, resultsClosed;

        /// <summary>The game is about to open the pause menu (Esc). During a replay: end the replay instead.</summary>
        /// <summary>
        /// The game has just written the world save (called from the save's own thread): write the race course into it
        /// now, before anything (like publishing to the Workshop) copies the save.
        /// </summary>
        public void WorldSaveCompleted()
        {
            lock (worldFileLock)
            {
                if (!worldCoursesChecked) return;   // this world's courses weren't read yet: don't overwrite them
                SaveCoursesToWorld(true);
                keepWrittenUntil = Environment.TickCount64 + 60000;
            }
        }
        private readonly object worldFileLock = new object();

        public bool EscapePressed()
        {
            if (analyserMode) { escapeAnalyser = true; return true; }
            if (orbitCam) { escapeOrbit = true; return true; }
            // Race results on screen: Esc closes them (and the race stats) instead of opening the pause menu.
            if (!replaying && resultsOnScreen && !controlsOpen) { resultsClosed = true; resultsOnScreen = false; return true; }
            if (!replaying) return false;
            escapeStop = true;
            return true;
        }
        private long replayStartMs, replayWaitMs;
        private double replayLength;
        private List<double[]> replayTrace, replayShip;
        private string replayLabel = "BEST LAP", replayTitle;
        // A replay asked for from the menu: what to play (null = the course best).
        private List<double[]> pendingTrace, pendingShip;
        private string pendingLabel, pendingTitle;
        // A ghost file to race against instead of your own best (null = your best on the course).
        private GhostFile raceGhost;
        private List<GhostFile> ghostFiles;
        private double? shipLapAtStart;
        private List<double[]> shipSpeedAtStart;
        private List<double?> shipSplitsAtStart;
        // The course's bests when the race started (the race itself overwrites them as it goes).
        private double? recordLapAtStart;
        private List<double[]> recordSpeedAtStart;
        private List<double?> recordSplitsAtStart, recordSectorsAtStart, recordBestSplitsAtStart;
        private string GhostFolder => Path.Combine(game.ConfigFolder, "Ghosts");
        private Vec3D? replayCam;
        private Vec3D? orbitLastCam, orbitLastDir;
        private Vec3D orbitDir;
        private double orbitDist = 20;
        private bool orbitInputSeen, orbitLogged;
        private bool replayCinematicTried;     // asked the game for its cinematic spectator this replay     // where the ghost was last frame (orbit camera moves with it)
        private Vec3D replayDir = new Vec3D(0, 0, 1);
        private double ReplayTime => replayCameraReady ? (Environment.TickCount64 - replayStartMs) / 1000.0 : 0;
        private int coursesVersion;
        private double raceTopSpeed;

        // Live gap to the best lap: where we are along the best lap's path.
        private int traceIndex;
        private string liveGapText;
        private HudColor liveGapColour;

        // Checkpoints already warned about as missed this lap.
        private readonly HashSet<int> warnedMissed = new HashSet<int>();

        // Sounds waiting to play: (when, Environment.TickCount64 ms; event path).
        private readonly List<(long Due, string Event)> soundQueue = new List<(long, string)>();

        private int recordsVersion;

        public RaceGateController(IGameBridge game)
        {
            this.game = game;
            settingsPath = Path.Combine(game.ConfigFolder, "RaceDisplay.json");
            settings = GateSettings.Load(settingsPath);
            KeyBindings.Current = k => settings.GetBinding(k);   // (the guide shows the keys as they are now)
            ApplySettings();
            timer.Triggered += OnTriggered;
            hud = new HudRenderer(game, settings);
        }

        /// <summary>Copies settings into the timer and crash detector (at start and after the Settings tab changes them).</summary>
        private void ApplySettings()
        {
            timer.MinLapSeconds = settings.MinLapSeconds;
            timer.LapTarget = settings.LapTarget;
            timer.PracticeLaps = settings.PracticeLaps;
            timer.RearmDistance = settings.RearmDistance;
            timer.ForwardStartOnly = settings.ForwardStartOnly;
            timer.PenaltySeconds = settings.PenaltySeconds;
            timer.CrashPenaltySeconds = settings.CrashPenaltySeconds;
            crash.MinSpeed = settings.CrashMinSpeed;
            crash.MinDeceleration = settings.CrashDeceleration;
            crash.MinSpeedLoss = settings.CrashMinSpeedLoss;
            crash.MinSpeedLossFraction = settings.CrashMinSpeedLossFraction;
        }

        /// <summary>Call every frame/tick.</summary>
        public void Update()
        {
            if (!game.InWorld)
            {
                loadedWorld = null; loadedHandle = null;
                if (editMode) { editMode = false; game.SetDebugDraw(false); }
                analyserMode = false; anaLook = anaA = anaB = null;
                replaying = replayPending = replayCameraReady = false;
                game.ShowHud(null);
                return;
            }

            PluginLog.Step("frame");
            bool hasPose = game.TryGetControlledPose(out Vec3D pos, out Vec3D fwd, out Vec3D up);
            Vec3D? position = hasPose ? pos : (Vec3D?)null;
            playerPosition = position;
            playerForward = hasPose ? (game.TryGetFacing(out Vec3D facing) ? facing : fwd) : (Vec3D?)null;
            if (hasPose) PluginLog.Step(game.ShipName != null ? "pose (in ship)" : "pose");

            if (loadedWorld != game.WorldId)
            {
                if (loadedWorld != null && loadedHandle != null && ReferenceEquals(loadedHandle, game.WorldHandle))
                    OnWorldRenamed(loadedWorld, game.WorldId);   // Save As: same world, new name; keep the course
                else
                {
                    PluginLog.StartTrace(60);
                    OnWorldLoaded(position);
                }
                loadedHandle = game.WorldHandle;
            }
            PluginLog.Step("world courses");
            CheckWorldCourses();
            KeepWorldCourseFile();

            PluginLog.Step("keys / menu");
            keys.Update(game, settings.GetBinding, capturing);
            // The game's HUD key (U) has race HUD only as one of its steps.
            var hudCycle = game.TakeHudCycle(settings.RaceHudOnly);
            if (hudCycle.HasValue && hudCycle.Value != settings.RaceHudOnly)
            {
                settings.RaceHudOnly = hudCycle.Value;
                settings.Save(settingsPath);
                if (hudCycle.Value) Notify("Race HUD only");
            }
            HandleControlsMenu();

            // Best-lap replay: while it plays, racing is paused and the camera follows the ghost.
            if (!controlsOpen && keys.WasPressed(GateKey.Replay)) ToggleReplay();
            if (!controlsOpen && keys.WasPressed(GateKey.ResultsMode)) ToggleResultsMode();
            // (While the results are up: after the finish, also once the timer has re-armed for the next start.)
            if (!controlsOpen && timer.State != TimerState.Running && results != null)
            {
                if (keys.WasPressed(GateKey.ResultsPrevLap)) StepResultsLap(-1);
                if (keys.WasPressed(GateKey.ResultsNextLap)) StepResultsLap(1);
            }
            if (replayPending && !controlsOpen && !game.IsPaused) BeginReplay();
            if (escapeStop) { escapeStop = false; if (replaying) StopReplay("Replay stopped"); }
            if (rpiSaveAt >= 0 && game.TimeSeconds >= rpiSaveAt) { rpiSaveAt = -1; settings.Save(settingsPath); }
            if (replaying)
            {
                UpdateReplay();
                PlayQueuedSounds();
                Draw();
                return;
            }

            // Spectator orbit camera: circles the picked grid. Its key or Esc leaves it.
            if (escapeOrbit) { escapeOrbit = false; StopOrbitCam("Orbit camera off"); }
            if (!controlsOpen && keys.WasPressed(GateKey.OrbitCamera)) ToggleOrbitCam();
            if (!controlsOpen && keys.WasPressed(GateKey.CineNext)) StepCinematic(1);
            if (!controlsOpen && keys.WasPressed(GateKey.CinePrev)) StepCinematic(-1);
            if (!controlsOpen && keys.WasPressed(GateKey.CinePlayStop)) PlayStopCinematic();
            PluginLog.Step("orbit / cinematic");
            if (orbitCam) UpdateOrbitCam();
            PluginLog.Step("autopilot");

            // Grids the game found lost inside the planet (they stop the world loading): removed once seen.
            Patches.RemoveLostGrids = settings.RemoveLostGrids;
            if (!Patches.LostInPlanet.IsEmpty)
            {
                var lostNames = new HashSet<string>();
                while (Patches.LostInPlanet.TryDequeue(out string lostName)) lostNames.Add(lostName);
                var gone = game.RemoveLostGrids(lostNames);
                if (gone.Count > 0) Notify($"Removed {gone.Count} broken grid{(gone.Count == 1 ? "" : "s")} the game found inside the planet. Save the world to keep it fixed.", HudColor.Yellow);
            }

            // A second stop request just after a teleport, in case the first one ran before the ship moved.
            if (stopShipAt.HasValue && game.TimeSeconds >= stopShipAt.Value) { stopShipAt = null; game.StopShip(); }
            if (launchAt.HasValue && game.TimeSeconds >= launchAt.Value) { launchAt = null; game.SetShipVelocity(launchVelocity); }
            if (gyroRestoreAt.HasValue && game.TimeSeconds >= gyroRestoreAt.Value) { gyroRestoreAt = null; game.TargetGyroRestore(); }
            // After the autopilot, once you're back in the seat (straight away, or whenever you leave the spectator):
            // settle the ship like after a teleport. Its controls had been left with the old heading and the
            // autopilot's motion, and flying it then shook as the two fought.
            if (apSettlePending && !autopilot && !orbitCam && game.OwnGrid != null && !game.InSpectator)
            {
                apSettlePending = false;
                game.StopShip();
                int settleGyros = game.TargetGyroOff();
                gyroRestoreAt = settleGyros > 0 ? game.TimeSeconds + 0.8 : (double?)null;
                PluginLog.Info("Autopilot: back in the seat, ship settled");
            }
            // The autopilot keeps flying in every mode (race spotlight, cinematics, the debug timeline).
            if (!controlsOpen && keys.WasPressed(GateKey.Autopilot)) ToggleAutopilot();
            // Spotlight tour: starts it, or (on the tour) cuts to the next ship now.
            if (!controlsOpen && !editMode && keys.WasPressed(GateKey.SpotTourNext))
            {
                if (spotTour == null) { StartSpotTour(); if (spotTour == null && controlsNote != null) { Notify(controlsNote, HudColor.Yellow); controlsNote = null; } }
                else SpotTourStep();
            }
            UpdateSpotAuto();
            if (autopilot) UpdateAutopilot();

            if (orbitCam && cinePlay != null && settings.CineDebug && !analyserMode)
            {
                // Cinematic debug: only the timeline on screen, no race HUD.
                PlayQueuedSounds();
                DrawCineDebug();
                return;
            }

            // Ship analyser mode: no racing and no race HUD, only the analyser panel. Esc leaves it.
            if (escapeAnalyser) { escapeAnalyser = false; SetAnalyserMode(false, position); }
            if (analyserMode)
            {
                PlayQueuedSounds();
                DrawAnalyserMode();
                return;
            }

            if (!controlsOpen && keys.WasPressed(GateKey.EditMode)) SetEditMode(!editMode, position);
            // Course edits are recorded for undo: the course before this frame's keys and clicks, kept if they changed it.
            var courseBefore = editMode ? SnapCourse() : null;
            int wheelDid = wheelDo;
            wheelDo = 0;
            if (editMode && !controlsOpen && (keys.WasPressed(GateKey.Undo) || wheelDid == 1)) { UndoRedo(true, position); courseBefore = null; }
            else if (editMode && !controlsOpen && (keys.WasPressed(GateKey.Redo) || wheelDid == 2)) { UndoRedo(false, position); courseBefore = null; }
            else if (editMode && !controlsOpen && (keys.WasPressed(GateKey.ReverseCourse) || wheelDid == 3)) ReverseCourse(position);
            if (!controlsOpen) HandleKeys(hasPose, pos, fwd, up, position);

            // Course edit mode: no racing; the tool wheel and clicks place / remove gates and checkpoints.
            if (editMode)
            {
                UpdateEditMode(position);
                if (courseBefore != null)
                {
                    var after = SnapCourse();
                    if (after.Sig != courseBefore.Sig)
                    {
                        undoStack.Add(courseBefore);
                        if (undoStack.Count > 100) undoStack.RemoveAt(0);
                        redoStack.Clear();
                    }
                }
                PlayQueuedSounds();
                Draw();
                return;
            }

            PluginLog.Step("timer");
            Vec3D? velocity = hasPose && game.TryGetShipVelocity(out Vec3D v) ? v : (Vec3D?)null;
            lastVelocity = velocity;
            // Gates and checkpoints are tested with the middle of the ship, not the grid's pivot (which can be
            // well off to one side, so passes near the edge of a gate were missed).
            Vec3D? detectAt = position;
            if (hasPose && game.TryGetShipCentre(out Vec3D centre)) detectAt = centre;
            // The spectator camera flying through a gate or checkpoint doesn't count: the timer keeps the ship's last
            // position until you're back in control.
            if (game.InSpectator) detectAt = null;
            // Autopilot laps aren't timed: they'd be the racing line's own lap again.
            if (!autopilot) timer.Update(detectAt, game.TimeSeconds);
            TrackLap(position, velocity);
            UpdateLiveGap(position);

            // A crash during the immunity time after the last crash penalty doesn't count.
            // ...nor, with that setting on, before you're back above the crash immunity speed.
            if (crashSlowLock && velocity.HasValue && velocity.Value.Length > settings.CrashRearmSpeed) crashSlowLock = false;
            // Crash: hull damage (the game's collision damage to your ship), a sudden speed drop, or either.
            int mode = settings.CrashDetection;
            // Impact: the speed drop, but only harder than the ship's thrusters could ever slow it (so braking never counts).
            crash.MinDeceleration = mode == 3 ? ImpactDeceleration() : settings.CrashDeceleration;
            bool speedCrash = crash.Update(velocity, game.TimeSeconds);
            RememberSpeed(velocity);
            // Hull damage only counts when you hit something going at least the crash minimum speed (as the speed drop does).
            bool damageCrash = DamageCrash(position, out double hitDamage) && RecentTopSpeed() >= settings.CrashMinSpeed;
            // A hit the game reported (damage or not): counts with Hull damage and Impact.
            bool hitCrash = HitCrash(position, out double hitStrength) && RecentTopSpeed() >= settings.CrashMinSpeed;
            if (hitCrash && mode != 1 && mode != 2) damageCrash = true;
            // Hull damage falls back on the speed drop only where damage can't be seen at all (no damage hook, or a
            // dedicated server's game where the damage happens elsewhere). It used to fall back until the first
            // hit was seen, so in a session with no crash yet, braking hard counted as a crash.
            if (mode == 0 && game.TimeSeconds >= localServerCheckAt)
            {
                localServerCheckAt = game.TimeSeconds + 5;
                damageVisible = Patches.ImpactHookPatched && game.HasLocalServer;
            }
            bool crashed = mode == 1 ? speedCrash
                : mode == 2 ? speedCrash || damageCrash
                : mode == 3 ? speedCrash || damageCrash
                : damageCrash || (speedCrash && !damageVisible && !Patches.ImpactDamageSeen && !Patches.ContactSeen);
            if (crashed && game.TimeSeconds - lastCrashPenaltyAt >= Math.Max(settings.CrashImmunitySeconds, 1.0)
                && !(settings.CrashImmuneUntilSpeed && crashSlowLock) && timer.AddCrash())
            {
                lastCrashPenaltyAt = game.TimeSeconds;
                if (position.HasValue) lapCrashSpots.Add((position.Value, timer.CurrentLapSeconds(game.TimeSeconds)));
                crashSlowLock = true;
                double impactSpeed = speedCrash ? crash.LastImpactSpeed : RecentTopSpeed();
                RaceNotify($"CRASH at {GameOptions.Speed(impactSpeed)}!  +{settings.CrashPenaltySeconds:0.#}s penalty", HudColor.Red);
                if (hitCrash) PluginLog.Info($"Crash: a hit of strength {hitStrength:0.###} at {GameOptions.Speed(impactSpeed)}");
                else if (damageCrash) PluginLog.Info($"Crash: hull damage {hitDamage:0.##} at {GameOptions.Speed(impactSpeed)}");
                else if (mode == 3) PluginLog.Info($"Crash: slowed harder than the thrusters can ({crash.MinDeceleration:0} m/s² or more) from {GameOptions.Speed(impactSpeed)}");
                FlashTimer(HudColor.Red);
                PenaltySound(120);
            }

            UpdateCountdown(position, velocity, game.TimeSeconds);
            PlayQueuedSounds();

            PluginLog.Step("draw");
            Draw();
        }

        // ---- Crash by hull damage: collision damage the game dealt to a ship near you this frame.
        private readonly Queue<(double T, double Speed)> recentSpeeds = new Queue<(double, double)>();
        private double localServerCheckAt = double.NegativeInfinity;
        private bool damageVisible;   // hull damage can be seen in this game (the hook is in and the server runs here)

        /// <summary>A hit to your ship this frame (the game's contact signal, with or without damage), at least the hit strength set.</summary>
        private bool HitCrash(Vec3D? position, out double strength)
        {
            strength = 0;
            bool hit = false;
            // Only your own ship's hits (the game's copy of it, or the server's): not another ship parked nearby.
            object own = game.OwnGrid, ownServer = own != null && !Patches.Contacts.IsEmpty ? game.ServerCopy(own) : null;
            while (Patches.Contacts.TryDequeue(out var c))
            {
                if (!position.HasValue || timer.State != TimerState.Running) continue;
                bool mine = ReferenceEquals(c.Grid, own) || ReferenceEquals(c.Grid, ownServer)
                    || (ownServer == null && (new Vec3D(c.X, c.Y, c.Z) - position.Value).Length <= 60);   // (the server's copy not found yet: by place)
                if (!mine) continue;
                strength = Math.Max(strength, c.Magnitude);
                if (c.Magnitude >= settings.CrashMinHit) hit = true;
            }
            return hit;
        }

        // ---- Impact: how hard the ship's own thrusters can slow it, so anything harder is a hit.
        private object impactShip;
        private double impactShipAt = double.NegativeInfinity, impactShipDecel;

        private double ImpactDeceleration()
        {
            object ship = game.OwnGrid;
            double now = game.TimeSeconds;
            if (ship != null && (!ReferenceEquals(ship, impactShip) || now - impactShipAt > 15))
            {
                impactShip = ship;
                impactShipAt = now;
                try
                {
                    var st = game.AnalyseGrid(ship);
                    double thrust = st?.Thrust?.Max() ?? 0;
                    double best = st != null && st.Mass > 0 ? thrust / st.Mass : 0;
                    if (best > 0 && Math.Abs(best - impactShipDecel) > 1) PluginLog.Info($"Impact crashes: {game.ShipName ?? "this ship"} can slow at {best:0} m/s² by itself; a crash is more than {Math.Max(settings.CrashDeceleration, best * 1.5 + 15):0} m/s²");
                    impactShipDecel = best;
                }
                catch { /* keep the last */ }
            }
            // Half as much again as the thrusters (and some for gravity), never less than the usual speed drop's.
            return Math.Max(settings.CrashDeceleration, impactShipDecel * 1.5 + 15);
        }

        private bool DamageCrash(Vec3D? position, out double damage)
        {
            damage = 0;
            bool near = false;
            while (Patches.ImpactDamage.TryDequeue(out var hit))
            {
                // The damaged grid's origin is somewhere in your ship: close to your seat.
                if (!position.HasValue || timer.State != TimerState.Running) continue;
                if ((new Vec3D(hit.X, hit.Y, hit.Z) - position.Value).Length > 120) continue;
                near = true;
                damage += hit.Damage;
            }
            return near && damage > 0 && damage >= settings.CrashMinDamage;
        }

        /// <summary>Speeds over the last half second, for the speed you hit at (damage comes after the impact).</summary>
        private void RememberSpeed(Vec3D? velocity)
        {
            double now = game.TimeSeconds;
            if (velocity.HasValue) recentSpeeds.Enqueue((now, velocity.Value.Length));
            while (recentSpeeds.Count > 0 && now - recentSpeeds.Peek().T > 0.5) recentSpeeds.Dequeue();
        }

        private double RecentTopSpeed()
        {
            double top = 0;
            foreach (var r in recentSpeeds) top = Math.Max(top, r.Speed);
            return top;
        }

        // ---------------------------------------------------------------- course edit mode

        private bool editMode;
        private int editTool = 3;                 // index into WheelLabels
        private const int ToolStartFinish = 0, ToolStart = 1, ToolFinish = 2, ToolCheckpoint = 3;
        // Short: the game's wheel cuts long names. The last two aren't tools but switches (shown orange when on):
        // "In the air" places every tool's gate / checkpoint in open air instead of on a grid; "Racing line" shows
        // or hides the best lap line.
        // The wheel only holds what you place. How it's placed (on a grid, pick blocks, in the air) is the placement
        // mode, with its own key; Pick blocks works like the game's partial copy (two corner blocks).
        private static readonly string[] WheelLabels = { "Start/Finish", "Start gate", "Finish gate", "Checkpoint" };
        // The whole wheel: the tools, then the other edit actions (picked like a tool, done at once).
        private const int WheelMove = 4, WheelPlacement = 5, WheelUndo = 6, WheelRedo = 7, WheelReverse = 8;
        private static readonly string[] WheelAll = { "Start/Finish", "Start gate", "Finish gate", "Checkpoint", "Move CP", "Placement", "Undo", "Redo", "Reverse" };
        private static readonly string[] WheelIcons =
        {
            "M 5,22 L 5,2 M 5,3 L 19,3 L 19,12 L 5,12 M 12,3 L 12,12 M 5,7.5 L 19,7.5",
            "M 9,7 L 17,12 L 9,17 Z M 22,12 A 10,10 0 1 1 2,12 A 10,10 0 1 1 22,12",
            "M 5,22 L 5,2 M 5,3 L 19,3 L 19,12 L 5,12",
            "M 22,12 A 10,10 0 1 1 2,12 A 10,10 0 1 1 22,12 M 15,12 A 3,3 0 1 1 9,12 A 3,3 0 1 1 15,12",
            "M 12,2 L 12,22 M 2,12 L 22,12 M 9,5 L 12,2 L 15,5 M 9,19 L 12,22 L 15,19 M 5,9 L 2,12 L 5,15 M 19,9 L 22,12 L 19,15",
            "M 12,2 L 21,7 L 21,17 L 12,22 L 3,17 L 3,7 Z M 3,7 L 12,12 L 21,7 M 12,12 L 12,22",
            "M 9,6 L 4,11 L 9,16 M 4,11 L 15,11 A 5,5 0 0 1 15,21 L 11,21",
            "M 15,6 L 20,11 L 15,16 M 20,11 L 9,11 A 5,5 0 0 0 9,21 L 13,21",
            "M 4,8 L 20,8 M 16,4 L 20,8 L 16,12 M 20,16 L 4,16 M 8,12 L 4,16 L 8,20",
        };
        private int wheelDo;   // an action picked on the wheel, done on the next frame: 1 undo, 2 redo, 3 reverse
        private Vec3D? sameRingAt;                 // a checkpoint ring clicked again: a 2nd click adds it once more
        private double sameRingTime;
        private BlockPick blockFirst;              // Pick blocks: the first corner, once clicked
        private TimingGate blockPreview;           // Pick blocks: the aimed block, or the box from the first corner to it
        private TimingGate airPreview;             // where a gate / checkpoint would go in the air (In the air switch)
        private bool lmbWas, rmbWas;
        private GridBox? preview;
        private double previewAt = double.NegativeInfinity;
        private const double EditReach = 3000;    // how far away a grid can be picked (m)

        private string PlacementName => settings.EditAirPlace ? "IN THE AIR" : settings.EditBlockPick ? "PICK BLOCKS" : "ON A GRID";

        private int movingCp = -1;   // edit mode: the checkpoint being moved (its next placement replaces it), or -1

        private string ToolName => movingCp >= 0 ? $"MOVING CP {movingCp + 1}" : (settings.EditBlockPick && !settings.EditAirPlace && blockFirst != null ? "CORNER 2: " : "") + (
            editTool == ToolStartFinish ? "START / FINISH GATE"
            : editTool == ToolStart ? "START GATE"
            : editTool == ToolFinish ? "FINISH GATE"
            : $"CHECKPOINT {timer.Checkpoints.Count + 1}");

        private HudColor ToolColour => editTool == ToolCheckpoint ? HudColor.Orange : editTool == ToolFinish ? HudColor.Purple : HudColor.Cyan;

        private double? stopShipAt;
        private readonly bool[] peekKeyDown = new bool[10];
        private int peekSlot = -1;
        private double peekUntil;
        private string seaShown;   // height above sea level, as shown beside the height above ground

        /// <summary>Makes where your seat is now (and its facing) the reset race teleport spot. Returns what happened.</summary>
        private string SetStartSpotHere()
        {
            if (timer.Gate == null) return "Set up the course first (a start gate)";
            if (!game.TryGetSeatPose(out Vec3D spPos, out Vec3D spFwd, out Vec3D spUp)) return "Sit in your ship's seat at the spot first";
            settings.CourseStartSpot = new GateSettings.SavedGate
            {
                World = game.WorldId,
                Center = new[] { spPos.X, spPos.Y, spPos.Z },
                Forward = new[] { spFwd.X, spFwd.Y, spFwd.Z },
                Up = new[] { spUp.X, spUp.Y, spUp.Z },
            };
            settings.Save(settingsPath);
            coursesVersion++;
            return "Start spot set here";
        }

        /// <summary>Moves the ship to just behind the start gate, facing the first checkpoint, and stops it.</summary>
        // Where you came through the start gate last time (10 m before it, facing the way you were going): the
        // reset race teleport goes there when you haven't set a start spot of your own.
        private Vec3D? autoStartAt, autoStartForward, autoStartUp, autoStartGate;
        private double? launchAt, gyroRestoreAt;
        private bool apSettlePending;
        private Vec3D launchVelocity;

        private void RememberStartEntry()
        {
            var g = timer.Gate;
            if (g == null || !game.TryGetSeatPose(out Vec3D seat, out Vec3D fwd, out Vec3D up)) return;
            Vec3D dir = lastVelocity.HasValue && lastVelocity.Value.Length > 2 ? lastVelocity.Value.Normalized() : fwd.Normalized();
            autoStartAt = seat - dir * 10;
            autoStartForward = dir;
            autoStartUp = up;
            autoStartGate = g.Center;
        }

        /// <summary>
        /// Flip ship upright: lifts the ship (FlipHeight, along gravity's up) and turns it level, facing the way it was
        /// heading, then stops it. During a race it costs what Settings > Race > Flip ship: penalty in a race says.
        /// </summary>
        private void FlipShipUpright()
        {
            if (!game.TryGetGravity(out Vec3D down) || down.Length < 1e-6) { Notify("Flip ship: needs gravity (on or near a planet or moon)", HudColor.Yellow); return; }
            if (!game.TryGetSeatPose(out Vec3D seat, out Vec3D fwd, out Vec3D up)) { Notify("Flip ship: sit in your ship's seat first", HudColor.Yellow); return; }
            Vec3D worldUp = (down * -1).Normalized();
            // Facing: the nose's direction along the ground; if it points straight up or down, the way the roof faces.
            Vec3D level = fwd - worldUp * Vec3D.Dot(fwd, worldUp);
            if (level.Length < 0.15)
            {
                level = up - worldUp * Vec3D.Dot(up, worldUp);
                if (Vec3D.Dot(fwd, worldUp) > 0) level = level * -1;   // (nose up: the roof faced back, so forward is the other way)
            }
            if (level.Length < 1e-6) level = Math.Abs(worldUp.X) < 0.9 ? Vec3D.Cross(worldUp, new Vec3D(1, 0, 0)) : Vec3D.Cross(worldUp, new Vec3D(0, 1, 0));
            level = level.Normalized();
            Vec3D at = seat + worldUp * Math.Max(0, settings.FlipHeight);
            bool racing = timer.State == TimerState.Running;
            int gyros = game.TargetGyroOff();
            if (!game.TeleportShip(at, level, worldUp))
            {
                if (gyros > 0) game.TargetGyroRestore();
                Notify("Flip ship: couldn't move the ship (it uses the game's admin teleport)", HudColor.Yellow);
                return;
            }
            stopShipAt = game.TimeSeconds + 0.5;
            gyroRestoreAt = gyros > 0 ? game.TimeSeconds + 0.8 : (double?)null;
            // (The jump isn't a crash, and nor is the stop or the landing straight after.)
            lastCrashPenaltyAt = game.TimeSeconds + 1.5;
            if (!racing || settings.FlipPenalty == 0) { Notify("Ship flipped upright"); return; }
            switch (settings.FlipPenalty)
            {
                case 1:
                    if (timer.AddPenalty(settings.FlipPenaltySeconds))
                    {
                        RaceNotify($"Ship flipped upright  +{settings.FlipPenaltySeconds:0.#}s penalty", HudColor.Red);
                        FlashTimer(HudColor.Red);
                        PenaltySound(120);
                    }
                    break;
                case 2:
                    if (timer.AddCrash())
                    {
                        RaceNotify($"Ship flipped upright: counts as a crash  +{settings.CrashPenaltySeconds:0.#}s penalty", HudColor.Red);
                        FlashTimer(HudColor.Red);
                        PenaltySound(120);
                    }
                    break;
                case 3:
                    RememberBurnedRace();
                    timer.Reset();
                    results = shipResults = null;
                    gapText = null;
                    countdownStart = null;
                    countdownAllowed = true;
                    RaceNotify("Ship flipped upright: race reset", HudColor.Yellow);
                    break;
            }
        }

        private bool TeleportToStart()
        {
            var g = timer.Gate;
            if (g == null) return false;
            Vec3D at, forward, upward;
            var spot = settings.CourseStartSpot;
            if (spot?.Center != null && spot.Forward != null && spot.Up != null && (spot.World == null || spot.World == game.WorldId))
            {
                Vec3D V3(double[] a) => new Vec3D(a[0], a[1], a[2]);
                at = V3(spot.Center); forward = V3(spot.Forward); upward = V3(spot.Up);
            }
            else if (autoStartAt.HasValue && autoStartGate.HasValue && (autoStartGate.Value - g.Center).Length < 1)
            {
                at = autoStartAt.Value; forward = autoStartForward.Value; upward = autoStartUp.Value;
            }
            else
            {
                Vec3D dir = g.Forward.Normalized();
                if (timer.Checkpoints.Count > 0 && Vec3D.Dot(timer.Checkpoints[0].Center - g.Center, dir) < 0) dir = dir * -1;
                double back = Math.Max(g.HalfDepth + 25, Math.Min(settings.CountdownDistance * 0.6, g.HalfDepth + 60));
                at = g.Center - dir * back; forward = dir; upward = g.Up;
            }
            // Target based gyro can keep steering for where it pointed before the teleport: off now, back on once the ship's there.
            int gyros = game.TargetGyroOff();
            if (!game.TeleportShip(at, forward, upward))
            {
                if (gyros > 0) game.TargetGyroRestore();
                Notify("Couldn't teleport (fly a ship to use this)", HudColor.Yellow);
                return false;
            }
            stopShipAt = game.TimeSeconds + 0.5;
            gyroRestoreAt = gyros > 0 ? game.TimeSeconds + 0.8 : (double?)null;
            // Out of the teleport at the chosen speed (after the stop, so the stop doesn't cancel it).
            launchAt = settings.ResetExitSpeed > 0 ? game.TimeSeconds + 0.75 : (double?)null;
            launchVelocity = forward.Normalized() * settings.ResetExitSpeed;
            return true;
        }

        private void SetEditMode(bool on, Vec3D? position)
        {
            if (on == editMode) return;
            editMode = on;
            game.SetDebugDraw(on); // so the gate and checkpoint boxes show while editing
            preview = null;
            blockFirst = null; blockPreview = null;
            movingCp = -1;
            lmbWas = rmbWas = true; // ignore a button already held
            if (on)
            {
                timer.Reset();
                results = shipResults = null;
                countdownStart = null;
                // (No message under the panel: the panel itself lists the keys now.)
            }
            else
            {
                timer.Resync(position);
                RaceNotify("Course edit mode off. Fly through the gate to race.", HudColor.Green);
            }
            coursesVersion++;
        }

        private void UpdateEditMode(Vec3D? position)
        {
            bool canAct = !controlsOpen && !game.IsPaused && game.HasFocus;
            bool hasRay = game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out Vec3D right, out Vec3D up);

            // Tool wheel: opens with the cursor showing and the view still (through the pause menu).
            bool lmb = canAct && game.IsKeyDown(0x01), rmb = canAct && game.IsKeyDown(0x02);
            bool lClick = lmb && !lmbWas, rClick = rmb && !rmbWas;
            lmbWas = lmb; rmbWas = rmb;
            if (keys.WasPressed(GateKey.EditWheel))
            {
                if (!canAct) PluginLog.Info($"Edit wheel key ignored: menu open={controlsOpen} paused={game.IsPaused} focus={game.HasFocus}");
                else if (!game.EditWheelOpen)
                {
                    // The current tool's slice is tinted a dark shade of its colour (cyan / purple / orange), so it
                    // can't be mistaken for the bright hover highlight.
                    var c = ToolColour;
                    var tint = new Dictionary<int, (byte R, byte G, byte B)> { [editTool] = ((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45)) };
                    (byte, byte, byte) Rgb(HudColor h) => (h.R, h.G, h.B);
                    var grey = ((byte)200, (byte)206, (byte)212);
                    string placeNow = PlacementName.ToLowerInvariant();
                    var look = new WheelLook
                    {
                        Colours = new[] { Rgb(HudColor.Cyan), Rgb(HudColor.Cyan), Rgb(HudColor.Purple), Rgb(HudColor.Orange), Rgb(HudColor.Yellow), Rgb(HudColor.Green), grey, grey, grey },
                        Icons = WheelIcons,
                        Disabled = new[] { false, false, false, false, timer.Checkpoints.Count == 0, false, undoStack.Count == 0, redoStack.Count == 0, timer.Gate == null },
                        Notes = new[]
                        {
                            "One gate to start and finish at: a circuit",
                            "Where a point to point course starts",
                            "Where a point to point course ends",
                            $"Checkpoint {timer.Checkpoints.Count + 1}: click each ring in flying order",
                            "Aim at a checkpoint first, then pick this and click its new place",
                            $"Now {placeNow}. Changes to the next: on a grid, pick blocks, in the air",
                            undoStack.Count == 0 ? "Nothing to undo" : "Undo the last course edit",
                            redoStack.Count == 0 ? "Nothing to redo" : "Redo the edit you undid",
                            "Fly the course the other way round",
                        },
                        IdleTitle = ToolName, IdleNote = "Point at a slice and let go",
                    };
                    game.OpenEditWheel(WheelAll, editTool, settings.GetBinding(GateKey.EditWheel).Key & 0xFF, tint, look);
                    if (game.WheelFailed)
                    {
                        game.WheelFailed = false;
                        Notify("Tool wheel isn't ready: equip the paint gun and open its wheel once, then try again", HudColor.Yellow);
                    }
                }
            }
            if (game.EditWheelOpen && !keys.IsDown(GateKey.EditWheel) && !game.IsKeyDown(settings.GetBinding(GateKey.EditWheel).Key & 0xFF))
                game.ReleaseEditWheel();
            bool gotPick = game.TryTakeWheelPick(out int picked);
            // The wheel's actions (not tools): done here as if their key was pressed; undo / redo / reverse next frame.
            bool wheelPlacement = gotPick && picked == WheelPlacement, wheelMove = gotPick && picked == WheelMove;
            if (gotPick && picked >= WheelUndo && picked <= WheelReverse) wheelDo = picked - WheelUndo + 1;
            if (gotPick && picked >= WheelLabels.Length) { gotPick = false; lmbWas = rmbWas = true; }
            // Placement mode (on a grid / pick blocks / in the air) and the racing line have their own keys.
            if (canAct && (wheelPlacement || !game.EditWheelOpen && keys.WasPressed(GateKey.PlacementMode)))
            {
                bool wasAir = settings.EditAirPlace, wasBlocks = settings.EditBlockPick && !wasAir;
                settings.EditBlockPick = !wasAir && !wasBlocks;   // grid -> blocks
                settings.EditAirPlace = wasBlocks;                // blocks -> air, air -> grid
                blockFirst = null; blockPreview = null; airPreview = null; preview = null;
                settings.Save(settingsPath);
                game.PlaySound(settings.SoundCheckpoint);
                Notify(settings.EditAirPlace
                    ? $"Placing: in the air, {settings.AirCheckpointDistance:0} m ahead ({settings.GetBinding(GateKey.Grow)} / {settings.GetBinding(GateKey.Shrink)}: size)"
                    : settings.EditBlockPick
                    ? "Placing: pick blocks. Left click one corner block, then the opposite corner; the box between them becomes the gate or checkpoint (right click clears the first corner)"
                    : "Placing: on a grid. Aim at a ring or gate structure and left click", HudColor.Cyan);
            }
            // Move a checkpoint: aim at it and press the key, then left click where it should go (same number).
            if (canAct && (wheelMove || !game.EditWheelOpen && keys.WasPressed(GateKey.MoveCheckpoint)) && hasRay)
            {
                var cps = WorldCheckpoints();
                int aimed = -1; double nearest = double.MaxValue;
                for (int i = 0; i < cps.Count; i++)
                {
                    var cp = cps[i].ToGate();
                    if (!AimedAt(cp, eye, dir)) continue;
                    double along = Vec3D.Dot(cp.Center - eye, dir);
                    if (along < nearest) { nearest = along; aimed = i; }
                }
                if (aimed < 0) Notify($"Aim at a checkpoint and press {settings.GetBinding(GateKey.MoveCheckpoint)} to move it", HudColor.Yellow);
                else
                {
                    movingCp = aimed;
                    game.PlaySound(settings.SoundCheckpoint);
                    Notify($"Moving CP {aimed + 1}: left click its new place (any placement mode); right click cancels", HudColor.Cyan);
                }
            }
            if (canAct && !game.EditWheelOpen && keys.WasPressed(GateKey.EditLine))
            {
                settings.EditRacingLine = !settings.EditRacingLine;
                settings.Save(settingsPath);
                game.PlaySound(settings.SoundCheckpoint);
                Notify(settings.EditRacingLine ? "Best lap line shown while editing" : "Best lap line hidden while editing", HudColor.Purple);
            }
            if (gotPick && picked >= 0 && picked < WheelLabels.Length)
            {
                editTool = picked;
                lmbWas = rmbWas = true; // the click that picked it doesn't also place something
                game.PlaySound(settings.SoundCheckpoint);
                Notify($"Tool: {WheelLabels[editTool].ToLowerInvariant()}", ToolColour);
                return;
            }
            if (game.EditWheelOpen) return;

            // What you're aiming at (checked 5 times a second): outlined so you can see what a click will pick.
            bool air = settings.EditAirPlace;
            airPreview = hasRay && air ? AirGate(eye, dir, up) : null;
            if (hasRay && !air && game.TimeSeconds - previewAt > 0.2)
            {
                previewAt = game.TimeSeconds;
                preview = game.TryGetLookedAtGrid(eye, dir, settings.GridSearchDistance, false, out GridBox gb) ? gb : (GridBox?)null;
            }
            bool blocks = settings.EditBlockPick && !air;
            if (hasRay && blocks && game.TimeSeconds - previewAt > 0.1)
            {
                previewAt = game.TimeSeconds;
                blockPreview = null;
                if (game.TryGetLookedAtBlock(eye, dir, settings.GridSearchDistance, out BlockPick aimed)
                    && game.TryGetBlockSpan(blockFirst ?? aimed, blockFirst != null && ReferenceEquals(blockFirst.Grid, aimed.Grid) ? aimed : blockFirst ?? aimed, out GridBox span))
                    blockPreview = new TimingGate(span.Center, span.Forward, span.Up, span.Width, span.Height, span.Depth);
                else if (blockFirst != null && game.TryGetBlockSpan(blockFirst, blockFirst, out GridBox first))
                    blockPreview = new TimingGate(first.Center, first.Forward, first.Up, first.Width, first.Height, first.Depth);
            }
            if (!blocks) { blockPreview = null; blockFirst = null; }
            if (air || blocks) preview = null;
            if (!hasRay) return;

            if (lClick)
            {
                TimingGate gate;
                string where;
                if (air)
                {
                    // In open air, ahead of you, facing the way you're looking.
                    gate = airPreview;
                    where = $"in the air ({(settings.AirGateRound ? "ring" : "square")}, {settings.AirCheckpointSize:0} m, {AirPlaceDistance():0} m ahead)";
                }
                else if (blocks)
                {
                    // Like partial copy: first click one corner block, then the opposite one.
                    if (!game.TryGetLookedAtBlock(eye, dir, settings.GridSearchDistance, out BlockPick picked2))
                    {
                        Notify("Aim at a block and left click", HudColor.Yellow);
                        return;
                    }
                    if (blockFirst == null || !ReferenceEquals(blockFirst.Grid, picked2.Grid))
                    {
                        bool moved = blockFirst != null;
                        blockFirst = picked2;
                        Sound(settings.SoundCheckpoint);
                        Notify((moved ? "That block is on another grid, so it's the first corner now. " : "First corner set on '" + picked2.GridName + "'. ")
                            + "Now left click the opposite corner block (right click cancels)", HudColor.Cyan);
                        return;
                    }
                    if (!game.TryGetBlockSpan(blockFirst, picked2, out GridBox span))
                    {
                        Notify("Couldn't make a box from those blocks", HudColor.Red);
                        return;
                    }
                    blockFirst = null;
                    gate = GateFromGrid(span);
                    where = $"across the picked blocks of '{span.Name}' ({span.Width:0.#} x {span.Height:0.#} x {span.Depth:0.#} m)";
                }
                else
                {
                    if (!game.TryGetLookedAtGrid(eye, dir, settings.GridSearchDistance, settings.FitToOpening, out GridBox g))
                    {
                        Notify($"Aim at a grid (a ring or gate structure) and left click, or change the placement mode ({settings.GetBinding(GateKey.PlacementMode)}) to pick blocks or in the air", HudColor.Yellow);
                        return;
                    }
                    gate = GateFromGrid(g);
                    where = $"{(g.IsOpening ? "in the opening of" : "round")} '{g.Name}'";
                }
                if (movingCp >= 0)
                {
                    var list = WorldCheckpoints();
                    if (movingCp < list.Count)
                    {
                        list[movingCp] = GateSettings.SavedGate.From(gate, game.WorldId);
                        SaveCheckpoints(list, position);
                        Notify($"CP {movingCp + 1} moved {where} (still CP {movingCp + 1})", HudColor.Cyan);
                    }
                    movingCp = -1;
                }
                else if (editTool == ToolStartFinish)
                {
                    SetGate(gate, position);
                    SetFinishGate(null, position);
                    Notify($"Start / finish gate set {where} (a circuit: start and finish in the same place)", HudColor.Cyan);
                }
                else if (editTool == ToolStart)
                {
                    SetGate(gate, position);
                    Notify(timer.FinishGate != null ? $"Start gate set {where}" : $"Start gate set {where}. Add a finish gate for a point-to-point course (else it's also the finish)", HudColor.Cyan);
                }
                else if (editTool == ToolFinish)
                {
                    if (timer.Gate != null && (timer.Gate.Center - gate.Center).Length < 2) { Notify("That's the start gate: use Start / Finish gate for a circuit", HudColor.Yellow); return; }
                    SetFinishGate(gate, position);
                    Notify($"Finish gate set {where}: the race now ends here (one run, start to finish)", HudColor.Purple);
                }
                else
                {
                    var list = WorldCheckpoints();
                    int already = list.FindIndex(x => (x.ToGate().Center - gate.Center).Length < 2);
                    if (already >= 0)
                    {
                        // Clicking it again soon after adds it once more: for a course that comes back through
                        // the same ring (the other way). Each pass through it then counts as the next one in order.
                        bool confirm = sameRingAt.HasValue && (sameRingAt.Value - gate.Center).Length < 2 && game.TimeSeconds - sameRingTime < 6;
                        if (!confirm)
                        {
                            sameRingAt = gate.Center; sameRingTime = game.TimeSeconds;
                            Notify($"That's already CP {already + 1}. Click it again to use it as CP {list.Count + 1} too (for a course that comes back through it)", HudColor.Yellow);
                            return;
                        }
                        sameRingAt = null;
                    }
                    if (timer.Gate != null && (timer.Gate.Center - gate.Center).Length < 2) { Notify("That's the start / finish gate", HudColor.Yellow); return; }
                    list.Add(GateSettings.SavedGate.From(gate, game.WorldId));
                    SaveCheckpoints(list, position);
                    Notify(already >= 0 ? $"Checkpoint {list.Count} set on the same ring as CP {already + 1}: each pass through it counts as the next one in order" : $"Checkpoint {list.Count} set {where}", HudColor.Cyan);
                }
                Sound(settings.SoundCheckpoint);
            }
            else if (rClick && movingCp >= 0)
            {
                Notify($"Moving CP {movingCp + 1} cancelled", HudColor.Yellow);
                movingCp = -1;
            }
            else if (rClick && blocks && blockFirst != null)
            {
                blockFirst = null;
                Notify("First corner cleared", HudColor.Yellow);
            }
            else if (rClick)
            {
                // Remove the gate or checkpoint (of the current tool) nearest the aim line.
                if (editTool == ToolFinish)
                {
                    if (timer.FinishGate != null && AimedAt(timer.FinishGate, eye, dir))
                    {
                        SetFinishGate(null, position);
                        Notify("Finish gate removed: the start gate is the finish again", HudColor.Cyan);
                        Sound(settings.SoundMissed);
                    }
                    else Notify("Aim at the finish gate and right click to remove it", HudColor.Yellow);
                }
                else if (editTool == ToolStartFinish || editTool == ToolStart)
                {
                    if (timer.Gate != null && AimedAt(timer.Gate, eye, dir))
                    {
                        SetGate(null, position);
                        Notify("Start / finish gate removed", HudColor.Cyan);
                        Sound(settings.SoundMissed);
                    }
                    else Notify("Aim at the start / finish gate and right click to remove it", HudColor.Yellow);
                }
                else
                {
                    var list = WorldCheckpoints();
                    int best = -1; double bestAlong = double.MaxValue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var cp = list[i].ToGate();
                        if (!AimedAt(cp, eye, dir)) continue;
                        double along = Vec3D.Dot(cp.Center - eye, dir);
                        if (along < bestAlong) { bestAlong = along; best = i; }
                    }
                    if (best < 0) { Notify("Aim at a checkpoint and right click to remove it", HudColor.Yellow); return; }
                    list.RemoveAt(best);
                    SaveCheckpoints(list, position);
                    Notify($"CP {best + 1} removed; the ones after it move up ({list.Count} left)", HudColor.Cyan);
                    Sound(settings.SoundMissed);
                }
            }
        }

        /// <summary>
        /// How far ahead an air gate is placed: the setting, but never so close that a big gate fills the screen
        /// (at least nine tenths of its size away, so the whole frame is in view).
        /// </summary>
        private double AirPlaceDistance()
        {
            double size = Math.Clamp(settings.AirCheckpointSize, 5, 300);
            double tall = settings.AirGateHeight > 0 ? Math.Clamp(settings.AirGateHeight, 5, 300) : size;
            return Math.Max(Math.Clamp(settings.AirCheckpointDistance, 5, 500), Math.Max(size, tall) * 0.9);
        }

        /// <summary>The air checkpoint the Air checkpoint tool would place: ahead along the look direction.</summary>
        private TimingGate AirGate(Vec3D eye, Vec3D dir, Vec3D up)
        {
            Vec3D f = dir.Normalized();
            Vec3D u = up - f * Vec3D.Dot(up, f);
            u = u.Length > 0.05 ? u.Normalized() : (timer.Gate?.Up ?? new Vec3D(0, 1, 0));
            double size = Math.Clamp(settings.AirCheckpointSize, 5, 300);
            double tall = settings.AirGateHeight > 0 ? Math.Clamp(settings.AirGateHeight, 5, 300) : size;
            return new TimingGate(eye + f * AirPlaceDistance(), f, u, size, tall, Math.Max(4, Math.Min(size, tall) * 0.15), true, settings.AirGateRound);
        }

        private static string Metres(double m) => m >= 1000 ? $"{m / 1000:0.00} km" : $"{m:0} m";

        /// <summary>The course in flying order: (name, gate). Start, checkpoints, then the finish (or back to the start).</summary>
        private List<(string Name, TimingGate Gate)> CourseRoute()
        {
            var route = new List<(string, TimingGate)>();
            if (timer.Gate != null) route.Add((timer.FinishGate != null ? "Start" : "Start/Finish", timer.Gate));
            for (int i = 0; i < timer.Checkpoints.Count; i++) route.Add(($"CP {i + 1}", timer.Checkpoints[i]));
            if (timer.FinishGate != null) route.Add(("Finish", timer.FinishGate));
            else if (timer.Gate != null && timer.Checkpoints.Count > 0) route.Add(("Start/Finish", timer.Gate));
            return route;
        }

        /// <summary>Edit mode: lines joining the gates in order, with arrows showing the way round and leg lengths.</summary>
        private void DrawCourseRoute(HudModel model)
        {
            var route = CourseRoute();
            if (route.Count < 2) return;
            Vec3D cam = game.TryGetLookRay(out Vec3D eye, out _, out _, out _) ? eye : (playerPosition ?? route[0].Gate.Center);
            Vec3D worldUp = timer.Gate?.Up ?? route[0].Gate.Up;
            var colour = new HudColor(235, 235, 245, 230);
            for (int i = 0; i + 1 < route.Count; i++)
            {
                Vec3D a = route[i].Gate.Center, b = route[i + 1].Gate.Center;
                Vec3D leg = b - a;
                double len = leg.Length;
                if (len < 0.5) continue;
                Vec3D d = leg * (1 / len);
                Vec3D side = Vec3D.Cross(d, worldUp);
                if (side.Length < 0.1) side = Vec3D.Cross(d, route[i].Gate.Right);
                side = side.Normalized();
                Vec3D lift = Vec3D.Cross(side, d).Normalized();
                game.DrawMarkerLine(a, b, colour);
                // Arrows along the leg (one per ~300 m, at least one), sized to stay visible at a distance.
                int arrows = Math.Max(1, Math.Min(6, (int)(len / 300)));
                for (int k = 1; k <= arrows; k++)
                {
                    Vec3D p = a + leg * (k / (double)(arrows + 1));
                    double s = Math.Clamp((p - cam).Length * 0.03, 1.5, 80);
                    Vec3D tip = p + d * (s * 0.6), back = p - d * (s * 0.6);
                    game.DrawMarkerLine(tip, back + side * s * 0.6, colour);
                    game.DrawMarkerLine(tip, back - side * s * 0.6, colour);
                    game.DrawMarkerLine(tip, back + lift * s * 0.6, colour);
                    game.DrawMarkerLine(tip, back - lift * s * 0.6, colour);
                }
                Vec3D mid = a + leg * 0.5;
                double ts = Math.Clamp((mid - cam).Length * 0.03, 1.5, 80);
                // The leg's length: a small tag on the HUD (easier to read against rock than text drawn in the world).
                Vec3D tagAt = mid + lift * ts * 1.4;
                if (game.TryProjectToScreen(tagAt, out double tx, out double ty, out bool tagBehind) && !tagBehind && tx > 0.02 && tx < 0.98 && ty > 0.02 && ty < 0.98)
                    model.Markers.Add(new HudModel.ScreenMarker { X = tx, Y = ty, Text = Metres(len), Colour = new HudColor(255, 225, 230, 235), Quiet = true, World = tagAt });
            }
        }

        /// <summary>Edit mode: the course panel (a summary, each leg with a bar for its length, and things worth fixing).</summary>
        private HudModel.CoursePanel CourseInfo(int aimedRoute)
        {
            var panel = new HudModel.CoursePanel();
            var route = CourseRoute();
            bool p2p = timer.FinishGate != null;
            double total = 0, longest = 0;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                double len = (route[i + 1].Gate.Center - route[i].Gate.Center).Length;
                total += len;
                longest = Math.Max(longest, len);
            }
            panel.Summary = (timer.Gate == null ? "NO START GATE YET" : p2p ? "POINT TO POINT" : "CIRCUIT")
                + $"  \u00B7  {timer.Checkpoints.Count} CP" + (route.Count >= 2 ? "  \u00B7  " + Metres(total) : "");
            var second = new List<string>();
            if (!p2p && timer.Gate != null) second.Add(settings.LapTarget > 0 ? $"{settings.LapTarget} laps {Metres(total * settings.LapTarget)}" : "unlimited laps");
            var rec = CourseRecord(false);
            if (rec?.BestLap != null) second.Add("best " + LapTimer.Format(rec.BestLap.Value));
            panel.Summary2 = second.Count > 0 ? string.Join("  \u00B7  ", second) : null;

            // Each leg, named by the gate it ends at (long courses: the first ones, then a count).
            const int maxLegs = 16;
            for (int i = 0; i + 1 < route.Count && i < maxLegs; i++)
            {
                double len = (route[i + 1].Gate.Center - route[i].Gate.Center).Length;
                string name = route[i + 1].Name == "Start/Finish" ? "S/F" : route[i + 1].Name;
                bool cp = name.StartsWith("CP");
                // (Aiming at the start / finish gate of a circuit lights the last leg, the one that ends there.)
                bool aimed = aimedRoute == i + 1 || (aimedRoute == 0 && !p2p && i + 1 == route.Count - 1);
                panel.Legs.Add((name, Metres(len), longest > 0 ? len / longest : 0, aimed, cp ? HudColor.Orange : name == "Finish" ? HudColor.Purple : HudColor.Cyan));
            }
            if (route.Count - 1 > maxLegs) panel.More = $"... {route.Count - 1 - maxLegs} more legs";

            // Warnings.
            if (timer.Gate == null) panel.Warnings.Add(("Fix", "set a start / finish gate"));
            else if (timer.Checkpoints.Count == 0) panel.Warnings.Add(("Tip", "add checkpoints so the route can't be cut short"));
            for (int i = 0; i + 1 < route.Count; i++)
            {
                double len = (route[i + 1].Gate.Center - route[i].Gate.Center).Length;
                if (len < 25 && !(i + 1 == route.Count - 1 && route.Count == 2))
                    panel.Warnings.Add(("Close together", $"{route[i].Name} and {route[i + 1].Name} ({len:0} m)"));
            }
            // Ring-shaped gates (thin) turned side-on to the way you arrive are hard or impossible to fly through.
            for (int i = 1; i < route.Count; i++)
            {
                var g = route[i].Gate;
                if (g.Depth > Math.Min(g.Width, g.Height) * 0.6) continue;
                Vec3D arrive = (g.Center - route[i - 1].Gate.Center).Normalized();
                if (Math.Abs(Vec3D.Dot(arrive, g.Forward)) < 0.35)
                    panel.Warnings.Add(("Side-on", $"{route[i].Name} faces across the route"));
            }
            return panel;
        }

        /// <summary>Edit mode: the gate of the course being aimed at (its place in CourseRoute, the nearest if several), or -1.</summary>
        private int AimedRouteGate(out string name)
        {
            name = null;
            if (!game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out _)) return -1;
            var route = CourseRoute();
            int best = -1; double bestAlong = double.MaxValue;
            for (int i = 0; i < route.Count; i++)
            {
                if (i > 0 && ReferenceEquals(route[i].Gate, route[0].Gate)) continue;   // (a circuit ends at its start gate: once)
                if (!AimedAt(route[i].Gate, eye, dir)) continue;
                double along = Vec3D.Dot(route[i].Gate.Center - eye, dir);
                if (along < bestAlong) { bestAlong = along; best = i; }
            }
            if (best >= 0) name = route[best].Name;
            return best;
        }

        private int editAimedPoint = -1;   // the aimed-at gate as a minimap point (lit there), or -1

        /// <summary>True if the aim line passes through the gate's box, or close to its centre (aiming at the ring).</summary>
        private static bool AimedAt(TimingGate g, Vec3D eye, Vec3D dir)
        {
            if (g.SegmentIntersects(eye, eye + dir * EditReach)) return true;
            double along = Vec3D.Dot(g.Center - eye, dir);
            if (along <= 0) return false;
            double off = (eye + dir * along - g.Center).Length;
            return off < Math.Max(g.Width, Math.Max(g.Height, g.Depth)) * 0.75 + 3;
        }

        /// <summary>Edit mode on screen: the timer box shows the tool and the controls; the aimed-at grid is outlined.</summary>
        private void DrawEditMode(HudModel model)
        {
            // No start gate yet means no timer panel from the race side: make one, so the tool and keys still show.
            model.Timer ??= new HudModel.TimerPanel();
            {
                model.Timer.Label = "COURSE EDIT MODE";
                model.Timer.Tag = PlacementName;
                model.Timer.TagColour = settings.EditAirPlace ? HudColor.Cyan : settings.EditBlockPick ? HudColor.Yellow : HudColor.Green;
                model.Timer.Big = ToolName;
                model.Timer.BigColour = ToolColour;
                model.Timer.Compact = true;
                // The keys, one a row: the key, then what it does. Undo / redo are greyed out with nothing to undo / redo.
                bool blocks = settings.EditBlockPick && !settings.EditAirPlace;
                var keyRows = new List<(string, string, bool)>
                {
                    ("Left click", blocks ? (blockFirst == null ? "first corner block" : "opposite corner block") : settings.EditAirPlace ? "place" : "set", false),
                    ("Right click", blocks && blockFirst != null ? "cancel" : "remove", false),
                };
                if (settings.EditAirPlace && !settings.EditBlockPick || settings.EditAirPlace)
                {
                    keyRows.Add(($"{settings.GetBinding(GateKey.Grow)} / {settings.GetBinding(GateKey.Shrink)}", $"size ({settings.AirCheckpointSize:0} m)", false));
                    keyRows.Add(($"{settings.GetBinding(GateKey.AirTaller)} / {settings.GetBinding(GateKey.AirShorter)}", settings.AirGateHeight > 0 ? $"height ({settings.AirGateHeight:0} m)" : "height (same)", false));
                    keyRows.Add((settings.GetBinding(GateKey.AirShape).ToString(), settings.AirGateRound ? "shape (ring)" : "shape (square)", false));
                    keyRows.Add(($"{settings.GetBinding(GateKey.AirFurther)} / {settings.GetBinding(GateKey.AirNearer)}", $"distance ({AirPlaceDistance():0} m)", false));
                }
                keyRows.Add((settings.GetBinding(GateKey.EditWheel).ToString(), "tools (hold)", false));
                keyRows.Add((settings.GetBinding(GateKey.PlacementMode).ToString(), "placement", false));
                keyRows.Add((settings.GetBinding(GateKey.Undo).ToString(), "undo", undoStack.Count == 0));
                keyRows.Add((settings.GetBinding(GateKey.Redo).ToString(), "redo", redoStack.Count == 0));
                keyRows.Add((settings.GetBinding(GateKey.EditMode).ToString(), "done", false));
                model.Timer.Keys = keyRows;
                model.Timer.Footer = null;
                model.Timer.Delta = null;
                model.Timer.Dots = null;
                model.Timer.Hint = null;
                // Best lap / best race boxes aren't needed while editing.
                model.Timer.BestLap = null;
                model.Timer.BestLapNote = null;
                model.Timer.BestRace = null;
                model.Timer.BestRaceNote = null;
            }
            model.Splits = null;
            model.Results = null;
            model.Countdown = null;
            model.RaceStats = null;
            // The gate being aimed at: lit in the course list and on the minimap, and named in the tag by the crosshair.
            int aimedRoute = AimedRouteGate(out string aimedName);
            model.Course = CourseInfo(aimedRoute);
            {
                var route = CourseRoute();
                bool p2p = timer.FinishGate != null;
                editAimedPoint = aimedRoute < 0 ? -1
                    : aimedRoute == 0 || (!p2p && aimedRoute == route.Count - 1 && timer.Checkpoints.Count > 0 && aimedRoute > timer.Checkpoints.Count) ? 0
                    : p2p && aimedRoute == route.Count - 1 ? 1
                    : (p2p ? 2 : 1) + (aimedRoute - 1);
            }
            DrawCourseRoute(model);
            if (!game.EditWheelOpen)
            {
                // By the crosshair: what a left click does, and what's being aimed at.
                string act = movingCp >= 0 ? ToolName : "SET " + ToolName;
                model.Markers.Add(new HudModel.ScreenMarker
                {
                    X = 0.5, Y = 0.57, Text = act, Colour = ToolColour, Quiet = true,
                    // (Placing in the air: how big it is and how far off, since that's hard to judge by eye.)
                    Sub = airPreview != null
                        ? (settings.AirGateHeight > 0 ? $"{airPreview.Width:0} x {airPreview.Height:0} m" : $"{airPreview.Width:0} m")
                            + (airPreview.Round ? " ring" : " square") + $" \u00b7 {AirPlaceDistance():0} m ahead"
                        : aimedName != null ? "aiming at " + aimedName : null,
                });
            }
            if (blockPreview != null && !game.EditWheelOpen)
            {
                var c = blockPreview.GetCorners();
                for (int i = 0; i < TimingGate.Edges.GetLength(0); i++)
                    game.DrawMarkerLine(c[TimingGate.Edges[i, 0]], c[TimingGate.Edges[i, 1]], blockFirst != null ? HudColor.Yellow : ToolColour);
            }
            if (airPreview != null && !game.EditWheelOpen)
            {
                DrawAirGate(airPreview, ToolColour, 1, true);
            }
            if (preview.HasValue)
            {
                // Outline the grid you're aiming at.
                var g = preview.Value;
                var box = new TimingGate(g.Center, g.Forward, g.Up, g.Width, g.Height, g.Depth);
                var c = ToolColour;
                Vec3D r = box.Right * box.HalfWidth, u = box.Up * box.HalfHeight, f = box.Forward * box.HalfDepth;
                Vec3D[] k = new Vec3D[8];
                for (int i = 0; i < 8; i++)
                    k[i] = box.Center + r * ((i & 1) != 0 ? 1 : -1) + u * ((i & 2) != 0 ? 1 : -1) + f * ((i & 4) != 0 ? 1 : -1);
                int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
                for (int e = 0; e < 12; e++) game.DrawMarkerLine(k[edges[e, 0]], k[edges[e, 1]], c);
            }
        }

        /// <summary>
        /// For checkpoints in the same place (a ring flown through twice): only one of them gets a marker, labelled
        /// with all their numbers ("CP 2 / 5"). It's the next one to fly if that's among them, else the first not
        /// flown yet, else the last. False for the others.
        /// </summary>
        private bool LimitingMarkers => settings.LimitMarkers && !editMode && !replaying;

        /// <summary>
        /// Only mark the next checkpoints: is checkpoint i among the next MarkersAhead targets? In a race they're the
        /// checkpoints still to fly from the next one on; before a race the start gate comes first, then CP 1, 2...
        /// </summary>
        private bool CheckpointAhead(int i, int next, bool racing)
        {
            if (!LimitingMarkers) return true;
            int ahead = Math.Max(1, settings.MarkersAhead);
            if (!racing) return 1 + i < ahead;
            if (next < 0 || i < next || timer.IsCheckpointHit(i)) return false;
            int rank = 0;
            for (int j = next; j < i; j++) if (!timer.IsCheckpointHit(j)) rank++;
            return rank < ahead;
        }

        /// <summary>
        /// Only mark the next checkpoints, for the gates: the start (finish = false) is the first target before a
        /// race; the finish (or start / finish on a circuit) comes after the last checkpoint still to fly.
        /// </summary>
        private bool GateAhead(bool finish, bool racing)
        {
            if (!LimitingMarkers) return true;
            int ahead = Math.Max(1, settings.MarkersAhead);
            if (!racing) return !finish || timer.Checkpoints.Count + 1 < ahead;
            if (!finish) return false;
            int left = 0;
            int next = timer.NextCheckpoint;
            if (next >= 0) for (int j = next; j < timer.Checkpoints.Count; j++) if (!timer.IsCheckpointHit(j)) left++;
            return left < ahead;
        }

        private bool MarkerFor(int i, int next, out string name)
        {
            var group = timer.SameSpot(i);
            name = $"CP {i + 1}";
            if (group.Count <= 1) return true;
            int shown = group.Contains(next) ? next : -1;
            if (shown < 0) foreach (int j in group) if (!timer.IsCheckpointHit(j)) { shown = j; break; }
            if (shown < 0) shown = group[group.Count - 1];
            if (i != shown) return false;
            name = "CP " + string.Join(" / ", group.Select(j => (j + 1).ToString()));
            return true;
        }

        // ---------------------------------------------------------------- lap tracking, live gap, sounds

        private void StartLapTracking()
        {
            lapTrace = new List<double[]>();
            lapCrashSpots = new List<(Vec3D, double)>();
            lapSpeedTrace = new List<double[]>();
            lapTopSpeed = 0;
            lapDistance = 0;
            lapMaxG = lapMaxTurnG = lapTurnGSum = lapTurnTime = lapMaxBrakeG = 0;
            gLastVel = null;
            lapCheckpointMarks.Clear();
            lapLastPos = playerPosition;
            traceIndex = 0;
            liveGapText = null;
            warnedMissed.Clear();
        }

        /// <summary>Records the lap's path every quarter second, its top speed and the distance flown.</summary>
        private void TrackLap(Vec3D? position, Vec3D? velocity)
        {
            if (timer.State != TimerState.Running || !position.HasValue) { lapLastPos = position; gLastVel = null; return; }
            TrackG(velocity);
            Vec3D p = position.Value;
            if (lapLastPos.HasValue)
            {
                double step = (p - lapLastPos.Value).Length;
                if (step < 2000) lapDistance += step; // ignore teleports
            }
            lapLastPos = p;
            currentSpeed = velocity.HasValue ? velocity.Value.Length : 0;
            lapTopSpeed = Math.Max(lapTopSpeed, currentSpeed);

            double t = timer.CurrentLapSeconds(game.TimeSeconds);
            if (lapTrace.Count == 0 || t - lapTrace[lapTrace.Count - 1][3] >= TraceInterval)
            {
                // [x, y, z, lap time] and, when flying a ship, its axes (for the ghost projection).
                if (game.TryGetShipAxes(out Vec3D ax, out Vec3D ay, out Vec3D az))
                    lapTrace.Add(new[]
                    {
                        Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1), Math.Round(t, 3),
                        Math.Round(ax.X, 3), Math.Round(ax.Y, 3), Math.Round(ax.Z, 3),
                        Math.Round(ay.X, 3), Math.Round(ay.Y, 3), Math.Round(ay.Z, 3),
                        Math.Round(az.X, 3), Math.Round(az.Y, 3), Math.Round(az.Z, 3),
                    });
                else
                    lapTrace.Add(new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1), Math.Round(t, 3) });
                lapSpeedTrace.Add(new[] { lapDistance, Math.Round(currentSpeed, 1) });
                if (!timer.InPractice) raceSpeedSamples.Add(currentSpeed);
            }
        }

        private const double G = 9.81;

        /// <summary>
        /// G-forces from the change in the ship's velocity over a tenth of a second: overall, sideways (cornering)
        /// and slowing down. Impacts (over 30 g) are left out so a crash doesn't count as a corner.
        /// </summary>
        private void TrackG(Vec3D? velocity)
        {
            double now = game.TimeSeconds;
            if (!velocity.HasValue) { gLastVel = null; return; }
            if (!gLastVel.HasValue || now < gLastTime || now - gLastTime > 0.5) { gLastVel = velocity; gLastTime = now; return; }
            double dt = now - gLastTime;
            if (dt < 0.1) return;

            Vec3D v = velocity.Value;
            Vec3D a = (v - gLastVel.Value) * (1.0 / dt);
            gLastVel = v; gLastTime = now;

            double g = a.Length / G;
            if (g > 30 || double.IsNaN(g)) return;
            lapMaxG = Math.Max(lapMaxG, g);
            double speed = v.Length;
            if (speed < 5) return;
            Vec3D dir = v * (1.0 / speed);
            double along = Vec3D.Dot(a, dir);
            double turn = (a - dir * along).Length / G;
            lapMaxTurnG = Math.Max(lapMaxTurnG, turn);
            lapTurnGSum += turn * dt;
            lapTurnTime += dt;
            lapMaxBrakeG = Math.Max(lapMaxBrakeG, -along / G);
        }

        private LapStats FinishedLapStats() => new LapStats
        {
            MaxG = lapMaxG,
            MaxTurnG = lapMaxTurnG,
            AvgTurnG = lapTurnTime > 0 ? lapTurnGSum / lapTurnTime : 0,
            MaxBrakeG = lapMaxBrakeG,
            Distance = lapDistance,
        };

        private void ResetRaceStats()
        {
            RememberStartEntry();
            lapStats.Clear();
            raceSpeedSamples.Clear();
            raceStats = null;
            raceShipName = game.ShipName;
            GateSettings.ShipRecord sr = null;
            var crec = CourseRecord(false);
            if (raceShipName != null && crec?.Ships != null) crec.Ships.TryGetValue(raceShipName, out sr);
            shipLapAtStart = sr?.BestLap;
            shipSpeedAtStart = sr?.BestLapSpeed;
            recordTraceAtStart = crec?.BestLapTrace;
            compareMine = compareRef = null; compareMineLabel = compareRefLabel = null;
            shipSplitsAtStart = sr?.BestLapSplits != null ? new List<double?>(sr.BestLapSplits) : null;
            recordLapAtStart = crec?.BestLap;
            recordSpeedAtStart = crec?.BestLapSpeed;
            recordSplitsAtStart = crec?.BestLapSplits != null ? new List<double?>(crec.BestLapSplits) : null;
            recordSectorsAtStart = crec?.BestSectors != null ? new List<double?>(crec.BestSectors) : null;
            recordBestSplitsAtStart = crec?.BestSplits != null ? new List<double?>(crec.BestSplits) : null;
        }

        /// <summary>The path and ship shape the ghost, racing line and live gap use: a raced-against ghost file, else your best.</summary>
        private List<double[]> GhostTrace => raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count) ? raceGhost.Trace
            : (racePossible ? PossibleTrace() : raceBestRace ? BestRaceLapTrace() : null) ?? CourseRecord(false)?.BestLapTrace;

        private bool raceBestRace;   // racing the course record race, lap for lap

        /// <summary>The course record race's lap for the lap being flown now (lap 1 on lap 1...), or null.</summary>
        private List<double[]> BestRaceLapTrace()
        {
            var laps = BestRaceTracesFor(timer.LapTarget);
            if (laps == null || timer.InPractice) return null;
            int i = Math.Clamp(timer.Laps.Count, 0, laps.Count - 1);
            return laps[i] != null && laps[i].Count >= 8 ? laps[i] : null;
        }

        private List<List<double[]>> BestRaceTracesFor(int lapCount)
        {
            var rec = CourseRecord(false);
            return rec?.BestRaceTraces != null && rec.BestRaceTraces.TryGetValue(lapCount, out var laps) && laps != null && laps.Count > 0 ? laps : null;
        }

        /// <summary>A new course record race: keep its laps' paths, so it can be raced as a ghost.</summary>
        private void KeepBestRaceTraces(GateSettings.CourseRecord rec, int laps)
        {
            if (raceLapTraces.Count != laps || raceLapTraces.Any(t => t == null || t.Count < 8)) return;
            if (rec.BestRaceTraces == null) rec.BestRaceTraces = new Dictionary<int, List<List<double[]>>>();
            rec.BestRaceTraces[laps] = raceLapTraces.Select(t => new List<double[]>(t)).ToList();
        }
        private List<double[]> GhostShip => raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count) ? raceGhost.ShipShape : CourseRecord(false)?.BestLapShip;

        /// <summary>The lap's speed trace with distance turned into a share of the lap (0..1), thinned to 200 points.</summary>
        private List<double[]> NormalisedSpeedTrace()
        {
            var result = new List<double[]>();
            if (lapSpeedTrace.Count < 2 || lapDistance <= 0) return result;
            int step = Math.Max(1, lapSpeedTrace.Count / 200);
            for (int i = 0; i < lapSpeedTrace.Count; i += step)
                result.Add(new[] { Math.Round(lapSpeedTrace[i][0] / lapDistance, 4), lapSpeedTrace[i][1] });
            // Checkpoint hits go in as extra points with a third value (the checkpoint number), in order.
            foreach (var m in lapCheckpointMarks)
            {
                var row = new[] { Math.Round(Math.Min(1, m[0] / lapDistance), 4), Math.Round(m[1], 1), m[2] };
                int at = result.FindIndex(r => r[0] > row[0]);
                if (at < 0) result.Add(row); else result.Insert(at, row);
            }
            return result;
        }

        /// <summary>
        /// Where the best lap was at this lap time, and which way the ship faced (its axes; null if the recording
        /// has none). False if the lap time is past the end of the best lap.
        /// </summary>
        private static bool GhostPose(List<double[]> trace, double lapTime, out Vec3D pos, out Vec3D[] axes)
        {
            pos = Vec3D.Zero; axes = null;
            if (trace == null || trace.Count < 2 || lapTime > trace[trace.Count - 1][3]) return false;
            int lo = 0, hi = trace.Count - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (trace[mid][3] <= lapTime) lo = mid; else hi = mid; }
            var a = trace[lo]; var b = trace[hi];
            double span = b[3] - a[3];
            double u = span > 1e-6 ? Math.Max(0, Math.Min(1, (lapTime - a[3]) / span)) : 0;
            Vec3D Lerp(int i) => new Vec3D(a[i] + (b[i] - a[i]) * u, a[i + 1] + (b[i + 1] - a[i + 1]) * u, a[i + 2] + (b[i + 2] - a[i + 2]) * u);
            pos = Lerp(0);
            if (a.Length >= 13 && b.Length >= 13)
                axes = new[] { Lerp(4).Normalized(), Lerp(7).Normalized(), Lerp(10).Normalized() };
            return true;
        }

        /// <summary>(top speed, average speed) of the lap just finished.</summary>
        private (double Top, double Avg) FinishedLapSpeeds(double flownSeconds) =>
            (lapTopSpeed, flownSeconds > 0 ? lapDistance / flownSeconds : 0);

        /// <summary>
        /// Gap to the best lap right now: finds where we are along the best lap's recorded path and compares
        /// the times. Searches a little ahead of the last match (so the start and finish, which are in the
        /// same place, can't be confused), or around the same lap time if we've wandered off the line.
        /// </summary>
        private void UpdateLiveGap(Vec3D? position)
        {
            liveGapText = null;
            if (timer.State != TimerState.Running || !position.HasValue) return;
            var trace = GhostTrace;
            if (trace == null || trace.Count < 8) return;

            Vec3D p = position.Value;
            double lapTime = timer.CurrentLapSeconds(game.TimeSeconds);

            double bestDist = double.MaxValue, bestT = 0;
            int bestJ = -1;
            void Search(int from, int to)
            {
                for (int j = Math.Max(0, from); j < Math.Min(trace.Count - 1, to); j++)
                {
                    var a = trace[j]; var b = trace[j + 1];
                    var pa = new Vec3D(a[0], a[1], a[2]);
                    var ab = new Vec3D(b[0], b[1], b[2]) - pa;
                    double len2 = Vec3D.Dot(ab, ab);
                    double u = len2 > 1e-6 ? Math.Max(0, Math.Min(1, Vec3D.Dot(p - pa, ab) / len2)) : 0;
                    double d = (pa + ab * u - p).Length;
                    if (d < bestDist) { bestDist = d; bestJ = j; bestT = a[3] + u * (b[3] - a[3]); }
                }
            }

            Search(traceIndex - 4, traceIndex + 40);
            if (bestJ < 0 || bestDist > 150)
            {
                // Off the line: look anywhere the best lap was within 20 s of our lap time.
                int lo = 0, hi = trace.Count - 1;
                while (lo < trace.Count && trace[lo][3] < lapTime - 20) lo++;
                while (hi > 0 && trace[hi][3] > lapTime + 20) hi--;
                bestDist = double.MaxValue; bestJ = -1;
                Search(lo - 1, hi + 1);
                if (bestJ < 0 || bestDist > 400) return;
            }
            traceIndex = bestJ;
            (liveGapText, liveGapColour) = HudRenderer.Gap(lapTime - bestT);
        }

        private void Sound(string eventPath, int delayMs = 0)
        {
            if (!settings.Sounds || string.IsNullOrEmpty(eventPath)) return;
            soundQueue.Add((Environment.TickCount64 + delayMs, eventPath));
        }

        /// <summary>The penalty sound: the melody if it's on, otherwise the missed beep twice (the second one gapMs later).</summary>
        private void PenaltySound(int gapMs)
        {
            if (!settings.Sounds) return;
            if (settings.PenaltyMelody) { game.PlayWav("Penalty", settings.PenaltyVolume / 100.0); return; }
            Sound(settings.SoundMissed);
            Sound(settings.SoundMissed, gapMs);
        }

        private void PlayQueuedSounds()
        {
            if (soundQueue.Count == 0) return;
            long now = Environment.TickCount64;
            for (int i = soundQueue.Count - 1; i >= 0; i--)
            {
                if (soundQueue[i].Due > now) continue;
                game.PlaySound(soundQueue[i].Event);
                soundQueue.RemoveAt(i);
            }
        }

        private void Notify(string text, HudColor? colour = null)
        {
            message = text;
            messageColour = colour ?? HudColor.Yellow;
            messageUntil = game.TimeSeconds + 3.0;
        }

        /// <summary>A reminder of the keys for a mode that's just started (Settings > HUD > Key reminders turns these off).</summary>
        private void KeyHint(string text, HudColor colour)
        {
            if (!settings.KeyHints) return;
            message = text;
            messageColour = colour;
            messageUntil = game.TimeSeconds + 5.0;
        }

        private void RaceNotify(string text, HudColor colour)
        {
            raceMessage = text;
            raceMessageColour = colour;
            raceMessageUntil = game.TimeSeconds + 3.0;
        }

        // ---------------------------------------------------------------- controls menu (Esc > Racing Display)

        /// <summary>
        /// The controls menu is opened from the game's pause menu. Click a shortcut, then press the new key
        /// combination (Esc or clicking it again cancels). Race shortcuts are paused while it's open.
        /// The menu only sends clicks; key capture and saving happen here on the game thread.
        /// </summary>
        private void HandleControlsMenu()
        {
            var actions = KeyBindings.Actions;
            bool hadCommand = false;
            while (game.TryTakeMenuCommand(out var c))
            {
                hadCommand = true;
                bool validRow = c.Row >= 0 && c.Row < actions.Length;
                switch (c.Action)
                {
                    case MenuAction.Opened:
                        cinematics = null;   // read the cinematics folder again (files may have been put there)
                        cineVersion++;
                        capturing = false;
                        presetFilesCache = null;
                        controlsNote = null;
                        settingsVersion++;
                        recordsVersion++;
                        coursesVersion++;
                        break;
                    case MenuAction.Change when validRow:
                        if (capturing && controlsRow == c.Row) { capturing = false; controlsNote = "Cancelled"; }
                        else { controlsRow = c.Row; capturing = true; controlsNote = null; }
                        break;
                    case MenuAction.Default when validRow:
                        capturing = false;
                        controlsRow = c.Row;
                        settings.SetBinding(actions[c.Row].Key, null);
                        settings.Save(settingsPath);
                        controlsNote = $"{actions[c.Row].Name}: back to {KeyBindings.Default(actions[c.Row].Key)}";
                        break;
                    case MenuAction.ResetAll:
                        capturing = false;
                        foreach (var (k, _) in actions) settings.SetBinding(k, null);
                        settings.Save(settingsPath);
                        controlsNote = "All controls are back to their defaults";
                        break;
                    case MenuAction.SetValue when c.Row >= 0 && c.Row < SettingDefs.All.Length:
                    {
                        var def = SettingDefs.All[c.Row];
                        def.Set(settings, def.Clean(c.Value));
                        ApplySettings();
                        if (settingsDirtySince < 0) settingsDirtySince = Environment.TickCount64;
                        break;
                    }
                    case MenuAction.SavePreset:
                    {
                        string name = (c.Text ?? "").Trim();
                        if (name.Length == 0) { controlsNote = "Type a name for the preset first (e.g. Testing or Race day)"; break; }
                        var presets = LoadPresets();
                        var values = new Dictionary<string, double>();
                        foreach (var def in SettingDefs.All) values[def.Name] = def.Get(settings);
                        bool replaced = presets.Remove(presets.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? "");
                        presets[name] = values;
                        SavePresets(presets);
                        settings.CurrentPreset = name;
                        settings.Save(settingsPath);
                        settingsVersion++;
                        controlsNote = replaced ? $"Preset '{name}' updated with the current settings" : $"Preset '{name}' saved";
                        break;
                    }
                    case MenuAction.LoadPreset:
                    {
                        var presets = LoadPresets();
                        if (c.Text == null || !presets.TryGetValue(c.Text, out var values)) { controlsNote = "Pick a preset to load"; break; }
                        // Settings not in the preset (added after it was saved) keep their current value.
                        foreach (var def in SettingDefs.All)
                            if (values.TryGetValue(def.Name, out double v)) def.Set(settings, def.Clean(v));
                        settings.CurrentPreset = c.Text;
                        ApplySettings();
                        settings.Save(settingsPath);
                        settingsDirtySince = -1;
                        settingsVersion++;
                        controlsNote = $"Preset '{c.Text}' loaded";
                        break;
                    }
                    case MenuAction.DeletePreset:
                    {
                        var presets = LoadPresets();
                        if (c.Text == null || !presets.Remove(c.Text)) break;
                        SavePresets(presets);
                        if (settings.CurrentPreset == c.Text) { settings.CurrentPreset = null; settings.Save(settingsPath); }
                        settingsVersion++;
                        controlsNote = $"Preset '{c.Text}' deleted";
                        break;
                    }
                    case MenuAction.ExportPreset:
                    {
                        var presets = LoadPresets();
                        if (c.Text == null || !presets.TryGetValue(c.Text, out var values)) { controlsNote = "Pick a preset to export"; break; }
                        try
                        {
                            Directory.CreateDirectory(PresetFolder);
                            string file = Path.Combine(PresetFolder, SafeFileName(c.Text) + PresetExtension);
                            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new PresetFile { Name = c.Text, Values = values },
                                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                            settingsVersion++;
                            presetFilesCache = null;
                            controlsNote = $"Preset '{c.Text}' exported to {file}";
                        }
                        catch (Exception e) { controlsNote = "Couldn't export the preset: " + e.Message; }
                        break;
                    }
                    case MenuAction.ImportPreset:
                    {
                        var file = c.Text != null && InFolder(c.Text, PresetFolder) ? ReadPresetFile(c.Text) : null;
                        if (file == null) { controlsNote = "Couldn't read that preset file"; break; }
                        var presets = LoadPresets();
                        string name = file.Name;
                        bool replaced = presets.Remove(presets.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? "");
                        presets[name] = file.Values;
                        SavePresets(presets);
                        settingsVersion++;
                        controlsNote = (replaced ? $"Preset '{name}' replaced" : $"Preset '{name}' imported") + ": pick it and press Load to use it";
                        break;
                    }
                    case MenuAction.OpenPresetFolder:
                        try
                        {
                            Directory.CreateDirectory(PresetFolder);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = PresetFolder, UseShellExecute = true });
                        }
                        catch (Exception e) { controlsNote = "Couldn't open the folder: " + e.Message; }
                        settingsVersion++;   // shows files copied in meanwhile when the menu refreshes
                        presetFilesCache = null;
                        break;
                    case MenuAction.RenameCourse:
                    {
                        var parts = (c.Text ?? "").Split('\n');
                        string from = parts[0], to = parts.Length > 1 ? parts[1].Trim() : "";
                        if (to.Length == 0) { controlsNote = "Type the new name in the Save as box first, then press Rename"; break; }
                        string world = game.WorldId;
                        var course = settings.SavedCourses.FirstOrDefault(x => x.World == world && x.Name == from);
                        if (course == null) break;
                        if (settings.SavedCourses.Any(x => x.World == world && !ReferenceEquals(x, course) && string.Equals(x.Name, to, StringComparison.OrdinalIgnoreCase)))
                        { controlsNote = $"There's already a course called '{to}'"; break; }
                        course.Name = to;
                        if (settings.CurrentCourseName.TryGetValue(world, out var cur) && cur == from) settings.CurrentCourseName[world] = to;
                        settings.Save(settingsPath);
                        coursesVersion++;
                        controlsNote = $"'{from}' renamed to '{to}' (saved with the world next time it's saved)";
                        break;
                    }
                    case MenuAction.ClearCourse:
                        ApplyCourse(new GateSettings.SavedCourse());
                        SaveCoursesToWorld(true);   // so a course found in a Workshop download doesn't come back
                        controlsNote = "Course cleared: this world has no gates or checkpoints now (saved courses are still in the list)";
                        break;
                    case MenuAction.ResetSettings:
                    {
                        var defaults = new GateSettings();
                        foreach (var def in SettingDefs.All) def.Set(settings, def.Get(defaults));
                        ApplySettings();
                        settings.Save(settingsPath);
                        settingsDirtySince = -1;
                        settingsVersion++;
                        controlsNote = "All settings are back to their defaults";
                        break;
                    }
                    case MenuAction.WatchShipReplay:
                    {
                        var sr = CourseRecord(false)?.Ships;
                        if (sr != null && c.Text != null && sr.TryGetValue(c.Text, out var ship) && ship.BestLapTrace != null && ship.BestLapTrace.Count >= 2)
                        {
                            pendingTrace = ship.BestLapTrace; pendingShip = ship.BestLapShip;
                            pendingLabel = "SHIP BEST"; pendingTitle = $"{c.Text} {LapTimer.Format(ship.BestLap ?? 0)}";
                            replayPending = true;
                            controlsNote = $"Replay of {c.Text}'s best lap starts when you close the menu (press Esc)";
                        }
                        else controlsNote = "No recorded best lap for that ship yet (race it once with this version)";
                        break;
                    }
                    case MenuAction.ExportGhost: ExportGhost(c.Text); break;
                    case MenuAction.WatchGhostFile:
                    {
                        var g = FindGhost(c.Text);
                        if (g == null) controlsNote = "That ghost file couldn't be read";
                        else if (!g.Fits(timer.Gate, timer.Checkpoints.Count)) controlsNote = "That ghost was flown on a different course (start gate or checkpoints don't match)";
                        else
                        {
                            pendingTrace = g.Trace; pendingShip = g.ShipShape;
                            pendingLabel = "GHOST"; pendingTitle = g.Label;
                            replayPending = true;
                            controlsNote = $"Replay of {g.Label} starts when you close the menu (press Esc)";
                        }
                        break;
                    }
                    case MenuAction.RaceGhostFile:
                    {
                        var g = FindGhost(c.Text);
                        if (g == null) controlsNote = "That ghost file couldn't be read";
                        else if (!g.Fits(timer.Gate, timer.Checkpoints.Count)) controlsNote = "That ghost was flown on a different course (start gate or checkpoints don't match)";
                        else { raceGhost = g; racePossible = false; raceBestRace = false; recordsVersion++; controlsNote = $"Racing against {g.Label}: the ghost, racing line and live gap use it"; }
                        break;
                    }
                    case MenuAction.SecondGhostShip:
                    {
                        string key = "ship:" + c.Text;
                        if (secondGhostKey == key) { ClearSecondGhost(); controlsNote = "2nd ghost off"; break; }
                        var sr = CourseRecord(false)?.Ships;
                        if (sr != null && c.Text != null && sr.TryGetValue(c.Text, out var ship) && ship.BestLapTrace != null && ship.BestLapTrace.Count >= 2)
                        {
                            SetSecondGhost(key, ship.BestLapTrace, ship.BestLapShip, $"{c.Text} {LapTimer.Format(ship.BestLap ?? 0)}");
                            controlsNote = $"2nd ghost: {c.Text}'s best lap (orange), raced and watched alongside the main ghost";
                        }
                        else controlsNote = "No recorded best lap for that ship yet";
                        break;
                    }
                    case MenuAction.SecondGhostFile:
                    {
                        if (secondGhostKey == c.Text) { ClearSecondGhost(); controlsNote = "2nd ghost off"; break; }
                        var g = FindGhost(c.Text);
                        if (g == null) controlsNote = "That ghost file couldn't be read";
                        else if (!g.Fits(timer.Gate, timer.Checkpoints.Count)) controlsNote = "That ghost was flown on a different course";
                        else
                        {
                            SetSecondGhost(c.Text, g.Trace, g.ShipShape, g.Label);
                            controlsNote = $"2nd ghost: {g.Label} (orange), raced and watched alongside the main ghost";
                        }
                        break;
                    }
                    case MenuAction.RaceShipLast:
                    {
                        var g = c.Text == null ? null : MakeGhost(c.Text, last: true);
                        if (g == null) { controlsNote = "No last lap kept for that ship yet (race it once with this version)"; break; }
                        g.Path = "shiplast:" + c.Text;
                        g.Ship = c.Text + " (last lap)";
                        raceGhost = g; racePossible = false; raceBestRace = false; recordsVersion++;
                        controlsNote = $"Racing against {c.Text}'s last lap: the ghost, racing line and live gap use it";
                        break;
                    }
                    case MenuAction.WatchShipLast:
                    {
                        var sr = CourseRecord(false)?.Ships;
                        if (sr != null && c.Text != null && sr.TryGetValue(c.Text, out var ship) && ship.LastLapTrace != null && ship.LastLapTrace.Count >= 2)
                        {
                            pendingTrace = ship.LastLapTrace; pendingShip = ship.LastLapShip;
                            pendingLabel = "LAST LAP"; pendingTitle = $"{c.Text} last lap {LapTimer.Format(ship.LastLap ?? 0)}";
                            replayPending = true;
                            controlsNote = $"Replay of {c.Text}'s last lap starts when you close the menu (press Esc)";
                        }
                        else controlsNote = "No last lap kept for that ship yet (race it once with this version)";
                        break;
                    }
                    case MenuAction.RaceShip:
                    {
                        var g = c.Text == null ? null : MakeGhost(c.Text);
                        if (g == null) { controlsNote = "No recorded best lap for that ship yet"; break; }
                        g.Path = "ship:" + c.Text;
                        raceGhost = g; racePossible = false; raceBestRace = false; recordsVersion++;
                        controlsNote = $"Racing against {c.Text}'s best lap: the ghost, racing line and live gap use it";
                        break;
                    }
                    case MenuAction.RacePossible:
                        raceGhost = null; racePossible = true; raceBestRace = false; recordsVersion++;
                        controlsNote = PossibleTrace() != null
                            ? "Racing against your possible lap: every best sector joined up"
                            : "Racing against your possible lap once every sector has a recorded path (until then, your best lap)";
                        break;
                    case MenuAction.RaceBestRace:
                    {
                        raceGhost = null; racePossible = false; raceBestRace = true; recordsVersion++;
                        var bestRaceLaps = BestRaceTracesFor(timer.LapTarget);
                        controlsNote = bestRaceLaps != null
                            ? $"Racing the course record race ({timer.LapTarget} lap{(timer.LapTarget == 1 ? "" : "s")}): its lap 1 on your lap 1, and so on"
                            : $"Racing the course record race once a {timer.LapTarget}-lap record race has been flown with this version (until then, your best lap)";
                        break;
                    }
                    case MenuAction.RaceCourseBest:
                        raceGhost = null; racePossible = false; raceBestRace = false; recordsVersion++;
                        controlsNote = "Racing against your own best again";
                        break;
                    case MenuAction.DeleteGhostFile:
                        try
                        {
                            if (c.Text != null && c.Text.EndsWith(GhostFile.Extension) && InFolder(c.Text, GhostFolder) && File.Exists(c.Text)) File.Delete(c.Text);
                            if (raceGhost?.Path == c.Text) raceGhost = null;
                            ghostFiles = null; recordsVersion++;
                            controlsNote = "Ghost file deleted";
                        }
                        catch (Exception e) { controlsNote = "Couldn't delete it: " + e.Message; }
                        break;
                    case MenuAction.OpenGhostFolder:
                        try
                        {
                            Directory.CreateDirectory(GhostFolder);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = GhostFolder, UseShellExecute = true });
                        }
                        catch (Exception e) { controlsNote = "Couldn't open the folder: " + e.Message; }
                        break;
                    case MenuAction.RefreshGhosts:
                        ghostFiles = null; recordsVersion++;
                        controlsNote = "Ghost files reloaded";
                        break;
                    case MenuAction.SetPilotName:
                        settings.PilotName = string.IsNullOrWhiteSpace(c.Text) ? null : c.Text.Trim();
                        settings.Save(settingsPath);
                        recordsVersion++;
                        controlsNote = settings.PilotName == null ? "Exported ghosts won't have a name on them" : $"Exported ghosts will say they're by {settings.PilotName}";
                        break;
                    case MenuAction.SpotlightExport:
                    {
                        if (lastRace == null) { controlsNote = "Finish a race first: then it can be exported"; break; }
                        try
                        {
                            Directory.CreateDirectory(SpotlightFolder);
                            string spotName = SafeFileName($"{lastRace.Ship} - {lastRace.Course} - {DateTime.Now:yyyy-MM-dd HHmm}");
                            File.WriteAllText(Path.Combine(SpotlightFolder, spotName + SpotlightExtension),
                                System.Text.Json.JsonSerializer.Serialize(lastRace, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                            spotFilesCache = null;
                            recordsVersion++;
                            controlsNote = $"Race exported as '{spotName}' (Race spotlight > Open folder to find it and take it to another world or PC)";
                        }
                        catch (Exception spotError) { controlsNote = "Couldn't export the race: " + spotError.Message; }
                        break;
                    }
                    case MenuAction.OrbitCamera:
                        if (orbitCam) { StopOrbitCam(null); controlsNote = "Orbit camera closed"; break; }
                        if (replaying) { controlsNote = "Stop the replay first"; break; }
                        game.CloseControlsMenu();
                        ToggleOrbitCam();
                        break;
                    case MenuAction.CineShowcase:
                    {
                        object showShip = game.OwnGrid ?? CineSubject();
                        var made = showShip == null ? null : MakeShowcase(showShip, "Showcase");
                        controlsNote = made == null ? "Get in a ship (or look at one) first: the showcase is fitted to its size and the way its cockpit faces"
                            : $"'{made.Name}' made for {game.GridName(showShip) ?? "this ship"}: 6 shots and effects as an example. Press Play, then change it however you like";
                        break;
                    }
                    case MenuAction.CineNew:
                    {
                        string cineName = (c.Text ?? "").Trim();
                        if (cineName.Length == 0) cineName = "Cinematic";
                        string cineBase = cineName;
                        int cineN = 2;
                        while (Cinematics().Any(x => string.Equals(x.Name, cineName, StringComparison.OrdinalIgnoreCase))) cineName = $"{cineBase} {cineN++}";
                        var cineMade = new CinematicFile { Name = cineName };
                        Cinematics().Add(cineMade);
                        settings.CinematicName = cineName;
                        settings.Save(settingsPath);
                        SaveCinematic(cineMade);
                        controlsNote = $"Cinematic '{cineName}' made. Fly the camera to a view, then add a shot";
                        break;
                    }
                    case MenuAction.CineSelect:
                        settings.CinematicName = c.Text;
                        settings.Save(settingsPath);
                        cineVersion++;
                        break;
                    case MenuAction.CineDelete:
                    {
                        var cineGone = CineCurrent;
                        if (cineGone == null) break;
                        try { File.Delete(CinePath(cineGone)); } catch (Exception cineDeleteError) { PluginLog.Info("Cinematic file not deleted: " + cineDeleteError.Message); }
                        Cinematics().Remove(cineGone);
                        cineUndo.Remove(cineGone); cineRedo.Remove(cineGone); cineLastSaved.Remove(cineGone);
                        if (ReferenceEquals(cinePlay, cineGone)) EndCinematic(null);
                        settings.CinematicName = Cinematics().FirstOrDefault()?.Name;
                        settings.Save(settingsPath);
                        cineVersion++;
                        controlsNote = $"Cinematic '{cineGone.Name}' deleted";
                        break;
                    }
                    case MenuAction.CineRename:
                    {
                        var cineRenamed = CineCurrent;
                        string cineNewName = (c.Text ?? "").Trim();
                        if (cineRenamed == null) break;
                        if (cineNewName.Length == 0) { controlsNote = "Type the new name in the box first"; break; }
                        if (Cinematics().Any(x => !ReferenceEquals(x, cineRenamed) && string.Equals(x.Name, cineNewName, StringComparison.OrdinalIgnoreCase))) { controlsNote = $"There's already a cinematic called '{cineNewName}'"; break; }
                        try { File.Delete(CinePath(cineRenamed)); } catch (Exception cineRenameError) { PluginLog.Info("Cinematic file not renamed: " + cineRenameError.Message); }
                        cineRenamed.Name = cineNewName;
                        settings.CinematicName = cineNewName;
                        settings.Save(settingsPath);
                        SaveCinematic(cineRenamed);
                        controlsNote = $"Renamed to '{cineNewName}'";
                        break;
                    }
                    case MenuAction.CineAddShot:
                    {
                        var cineInto = CineCurrent;
                        if (cineInto == null) { controlsNote = "Make a cinematic first"; break; }
                        var cineAdded = new CineShot
                        {
                            Type = c.Text ?? "Orbit", Track = c.Text == "Fixed",
                            RotateSpeed = settings.OrbitAutoRotate != 0 ? settings.OrbitAutoRotate : 6,
                        };
                        if (c.Row >= 0 && c.Row < cineInto.Shots.Count) cineInto.Shots.Insert(c.Row, cineAdded);   // dropped between two shots
                        else cineInto.Shots.Add(cineAdded);
                        controlsNote = $"{CineTypeName(cineAdded.Type)} shot added. " + CaptureShot(cineAdded, cineAdded.Type == "Path" ? 2 : 0)
                                       + (cineAdded.Type == "Path" ? ". Fly to the next view and press Add keyframe (2 or more needed)" : "");
                        SaveCinematic(cineInto);
                        break;
                    }
                    case MenuAction.CineShotDelete:
                    case MenuAction.CineShotMove:
                    case MenuAction.CineShotCapture:
                    case MenuAction.CineShotSet:
                    {
                        var cineEdited = CineCurrent;
                        if (cineEdited == null || c.Row < 0 || c.Row >= cineEdited.Shots.Count) break;
                        var cineShotEdited = cineEdited.Shots[c.Row];
                        if (c.Action == MenuAction.CineShotDelete) cineEdited.Shots.RemoveAt(c.Row);
                        else if (c.Action == MenuAction.CineShotMove && c.Text == "to")
                        {
                            // Dragged along the timeline: Value = the place it ends up at.
                            int cinePlace = Math.Clamp((int)Math.Round(c.Value), 0, cineEdited.Shots.Count - 1);
                            if (cinePlace == c.Row) break;
                            cineEdited.Shots.RemoveAt(c.Row);
                            cineEdited.Shots.Insert(cinePlace, cineShotEdited);
                        }
                        else if (c.Action == MenuAction.CineShotMove)
                        {
                            int cineTo = c.Row + (c.Value < 0 ? -1 : 1);
                            if (cineTo < 0 || cineTo >= cineEdited.Shots.Count) break;
                            cineEdited.Shots[c.Row] = cineEdited.Shots[cineTo];
                            cineEdited.Shots[cineTo] = cineShotEdited;
                        }
                        else if (c.Action == MenuAction.CineShotCapture)
                        {
                            int.TryParse(c.Text ?? "", out int cineKeyAt);
                            if (c.Text == null) cineKeyAt = -1;
                            controlsNote = CaptureShot(cineShotEdited, (int)Math.Round(c.Value), cineKeyAt);
                        }
                        else if (c.Text == "KeyGo") { controlsNote = GoToView(cineShotEdited, (int)Math.Round(c.Value)); }
                        else if (c.Text == "KeyDelete" || c.Text == "KeyLeft" || c.Text == "KeyRight")
                        {
                            int cineK = (int)Math.Round(c.Value), cineN = cineShotEdited.Keys.Count;
                            while (cineShotEdited.KeysShip.Count < cineN) cineShotEdited.KeysShip.Add(null);
                            if (cineK < 0 || cineK >= cineN) break;
                            if (c.Text == "KeyDelete")
                            {
                                cineShotEdited.Keys.RemoveAt(cineK);
                                cineShotEdited.KeysShip.RemoveAt(cineK);
                                controlsNote = $"Keyframe {cineK + 1} deleted";
                            }
                            else
                            {
                                int cineTo2 = cineK + (c.Text == "KeyLeft" ? -1 : 1);
                                if (cineTo2 < 0 || cineTo2 >= cineN) break;
                                (cineShotEdited.Keys[cineK], cineShotEdited.Keys[cineTo2]) = (cineShotEdited.Keys[cineTo2], cineShotEdited.Keys[cineK]);
                                (cineShotEdited.KeysShip[cineK], cineShotEdited.KeysShip[cineTo2]) = (cineShotEdited.KeysShip[cineTo2], cineShotEdited.KeysShip[cineK]);
                                if (cineEditShot == c.Row && cineEditKey == cineK) cineEditKey = cineTo2;
                            }
                        }
                        else if (c.Text == "ClearKeys") { cineShotEdited.Keys.Clear(); cineShotEdited.KeysShip.Clear(); controlsNote = "Keyframes cleared"; }
                        else if (c.Text == "BlendTo") cineShotEdited.Blend = Math.Clamp(c.Value, 0, 30);
                        else if (c.Text == "SecondsTo") cineShotEdited.Seconds = Math.Clamp(c.Value, 0.5, 600);
                        else if (c.Text == "RotateTo") cineShotEdited.RotateSpeed = Math.Clamp(c.Value, -45, 45);
                        else if (c.Text == "Seconds") cineShotEdited.Seconds = Math.Clamp(cineShotEdited.Seconds + c.Value, 0.5, 600);
                        else if (c.Text == "Rotate") cineShotEdited.RotateSpeed = Math.Clamp(cineShotEdited.RotateSpeed + c.Value, -45, 45);
                        else if (c.Text == "Track") cineShotEdited.Track = !cineShotEdited.Track;
                        else if (c.Text == "Relative") cineShotEdited.Relative = !cineShotEdited.Relative;
                        SaveCinematic(cineEdited);
                        break;
                    }
                    case MenuAction.CineSet:
                    {
                        var cineSet = CineCurrent;
                        if (c.Text == "Spotlight") { settings.SpotlightCinematic = !settings.SpotlightCinematic; settings.Save(settingsPath); cineVersion++; break; }
                        if (c.Text == "Auto") { settings.CineAutoSwitch = !settings.CineAutoSwitch; settings.Save(settingsPath); cineVersion++; break; }
                        if (c.Text == "Preview") { settings.CinePreview = !settings.CinePreview; settings.Save(settingsPath); cineVersion++; break; }
                        if (c.Text == "Debug") { settings.CineDebug = !settings.CineDebug; settings.Save(settingsPath); cineVersion++; settingsVersion++; break; }
                        if (cineSet == null) break;
                        if (c.Text == "AddLane") cineSet.Lanes = Math.Min(8, cineSet.Lanes + 1);
                        else if (c.Text == "RemoveLane" && cineSet.Lanes > 1)
                        {
                            // The bottom layer goes; anything on it moves up onto the one above.
                            cineSet.Lanes--;
                            foreach (var cineFxMoved in cineSet.Effects) if (cineFxMoved.Lane >= cineSet.Lanes) cineFxMoved.Lane = cineSet.Lanes - 1;
                        }
                        else if (c.Text == "Loop") cineSet.Loop = !cineSet.Loop;
                        else if (c.Text == "Follow") cineSet.FollowPicked = !cineSet.FollowPicked;
                        else if (c.Text == "Length")
                        {
                            double cineLen = double.IsFinite(c.Value) ? Math.Clamp(Math.Round(c.Value * 10) / 10, 0, 3600) : 0;
                            if (cineLen > 0 && cineLen < 1) cineLen = 1;
                            cineSet.TimelineLength = cineLen;
                            controlsNote = cineLen > 0 ? $"Timeline length {cineLen:0.#}s" : "Timeline length: as long as the shots";
                        }
                        SaveCinematic(cineSet);
                        break;
                    }
                    case MenuAction.CinePlay:
                    {
                        var cineToPlay = CineCurrent;
                        if (cineToPlay == null) break;
                        if (replaying) { controlsNote = "Stop the replay first"; break; }
                        object cineSubject = orbitCam && ocGrid != null ? ocGrid
                            : (game.TryGetLookRay(out Vec3D cineEye, out Vec3D cineLook, out _, out _) ? game.LookedAtGrid(cineEye, cineLook, 5000, true) : null)
                              ?? spotGrid ?? cineLastGrid ?? game.OwnGrid;
                        game.CloseControlsMenu();
                        StartCinematic(cineToPlay, cineSubject, c.Row, c.Text == "From" ? c.Value : 0);
                        break;
                    }
                    case MenuAction.HistoryFilter:
                        if (c.Text == "Course") historyThisCourse = !historyThisCourse;
                        else if (c.Text == "Session") historySessionOnly = !historySessionOnly;
                        historyVersion++;
                        break;
                    case MenuAction.HistoryRace:
                        RaceSpotlight(c.Text, false);
                        break;
                    case MenuAction.HistoryClear:
                        try { lock (historyWriteLock) if (File.Exists(HistoryPath)) File.Move(HistoryPath, HistoryPath + $".{DateTime.Now:yyyyMMdd-HHmmss}.old"); }
                        catch (Exception historyError) { controlsNote = "Couldn't clear it: " + historyError.Message; break; }
                        history = new List<HistoryEntry>();
                        historyVersion++;
                        controlsNote = "History cleared (the old file was kept beside it, renamed .old)";
                        break;
                    case MenuAction.HistoryOpen:
                        try
                        {
                            string historyDir = Path.GetDirectoryName(HistoryPath);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = historyDir, UseShellExecute = true });
                        }
                        catch (Exception historyError) { controlsNote = "Couldn't open the folder: " + historyError.Message; }
                        break;
                    case MenuAction.Autopilot:
                        game.CloseControlsMenu();
                        ToggleAutopilot();
                        break;
                    case MenuAction.CineTriggerSet:
                    {
                        string ctKey = CourseKey();
                        if (ctKey == null) { controlsNote = "Load or make a course first"; break; }
                        double ctKeep = settings.CineTriggers.FirstOrDefault(x => x.Course == ctKey && x.Gate == c.Row)?.Offset ?? 0;
                        settings.CineTriggers.RemoveAll(x => x.Course == ctKey && x.Gate == c.Row);
                        // (Kept with no cinematic when its start time was changed, so the before / after choice stays.)
                        if (!string.IsNullOrEmpty(c.Text) || ctKeep != 0) settings.CineTriggers.Add(new GateSettings.CineTrigger { Course = ctKey, Gate = c.Row, Cinematic = string.IsNullOrEmpty(c.Text) ? null : c.Text, Offset = ctKeep });
                        settings.Save(settingsPath);
                        cineVersion++;
                        break;
                    }
                    case MenuAction.CineTriggerTime:
                    {
                        string ctKey = CourseKey();
                        if (ctKey == null) { controlsNote = "Load or make a course first"; break; }
                        var ctFor = settings.CineTriggers.FirstOrDefault(x => x.Course == ctKey && x.Gate == c.Row);
                        if (ctFor == null) settings.CineTriggers.Add(ctFor = new GateSettings.CineTrigger { Course = ctKey, Gate = c.Row });
                        ctFor.Offset = double.IsFinite(c.Value) ? Math.Clamp(Math.Round(c.Value * 10) / 10, -30, 30) : 0;
                        settings.Save(settingsPath);
                        cineVersion++;
                        break;
                    }
                    case MenuAction.SpotTour:
                        if (spotTour != null) { EndSpotTour("Spotlight tour stopped"); break; }
                        StartSpotTour();
                        break;
                    case MenuAction.SpotBoardSet:
                    {
                        if (c.Text == "*best*") settings.SpotBoardBestPerShip = !settings.SpotBoardBestPerShip;
                        else if (c.Text == "*reveal*") settings.SpotBoardReveal = !settings.SpotBoardReveal;
                        else if (c.Text == "*tourcine*")
                        {
                            // The tour's cinematic: each race's own, then each cinematic in turn.
                            var tourChoices = new List<string> { "" };
                            tourChoices.AddRange(Cinematics().Select(x => x.Name));
                            int tourChoice = tourChoices.IndexOf(settings.SpotTourCinematic ?? "");
                            settings.SpotTourCinematic = tourChoices[(Math.Max(0, tourChoice) + 1) % tourChoices.Count];
                        }
                        else if (c.Text != null && (c.Text.StartsWith("^") || c.Text.StartsWith("v")) && c.Text.Length > 1 && c.Value == 7)
                        {
                            // Move a ship up / down the running order: the order as listed now is written down first.
                            string moved = c.Text.Substring(1);
                            var running = (spotShipsCache ?? new List<(string Ship, int Races, bool Hidden)>()).Select(x => x.Ship).ToList();
                            int from = running.FindIndex(x => string.Equals(x, moved, StringComparison.OrdinalIgnoreCase));
                            int to = from + (c.Text[0] == '^' ? -1 : 1);
                            if (from >= 0 && to >= 0 && to < running.Count) { running.RemoveAt(from); running.Insert(to, moved); }
                            settings.SpotBoardOrder = running;
                        }
                        else if (!string.IsNullOrWhiteSpace(c.Text))
                        {
                            var hiddenShips = settings.SpotBoardHiddenShips ??= new List<string>();
                            if (hiddenShips.RemoveAll(x => string.Equals(x, c.Text, StringComparison.OrdinalIgnoreCase)) == 0) hiddenShips.Add(c.Text);
                        }
                        spotBoard = null;
                        spotShipsCache = null;
                        settings.Save(settingsPath);
                        recordsVersion++;
                        break;
                    }
                    case MenuAction.SpotPageSet:
                    {
                        var spList = SpotPages();
                        if (c.Text == "Reset") { spList.Clear(); SpotPages(); settings.Save(settingsPath); recordsVersion++; break; }
                        if (c.Text == "AllOn" || c.Text == "AllOff") { foreach (var each in spList) each.On = c.Text == "AllOn"; settings.Save(settingsPath); recordsVersion++; break; }
                        int spAt = spList.FindIndex(x => x.Page == c.Row);
                        if (spAt < 0) break;
                        var spPage = spList[spAt];
                        if (c.Text == "On") spPage.On = !spPage.On;
                        else if (c.Text == "Seconds") spPage.Seconds = Math.Clamp(c.Value, 0, 120);
                        else if (c.Text == "Up" && spAt > 0) { spList.RemoveAt(spAt); spList.Insert(spAt - 1, spPage); }
                        else if (c.Text == "Down" && spAt < spList.Count - 1) { spList.RemoveAt(spAt); spList.Insert(spAt + 1, spPage); }
                        settings.Save(settingsPath);
                        recordsVersion++;
                        break;
                    }
                    case MenuAction.SpotExportImages:
                    {
                        string spNote = StartSpotExport();
                        if (spNote != null) controlsNote = spNote;
                        else game.CloseControlsMenu();
                        break;
                    }
                    case MenuAction.CineFocus:
                        cineEditShot = c.Row;
                        cineEditKey = (int)Math.Round(c.Value);
                        break;
                    case MenuAction.CineFxAdd:
                    {
                        var cineFxInto = CineCurrent;
                        if (cineFxInto == null) break;
                        double cineOwnFov = game.DefaultFov > 1 ? Math.Round(game.DefaultFov) : 70;
                        var cineFx = new CineEffect { Type = c.Text ?? "Fov", Start = Math.Max(0, Math.Round(c.Value * 2) / 2), Seconds = 4, Lane = Math.Clamp(c.Row, 0, cineFxInto.Lanes - 1) };
                        if (cineFx.Type == "Fov") { cineFx.From = cineOwnFov; cineFx.To = Math.Max(20, cineOwnFov - 30); }
                        else if (cineFx.Type == "Roll") { cineFx.From = 0; cineFx.To = 15; }
                        else if (cineFx.Type == "Panel") { cineFx.To = 0; cineFx.Seconds = 8; }
                        else if (cineFx.Type == "Fade") { cineFx.From = 0; cineFx.To = 100; cineFx.Seconds = 1.5; }
                        else if (cineFx.Type == "Bars") { cineFx.From = 0; cineFx.To = 12; cineFx.Seconds = 2; }
                        else cineFx.To = 0.5;
                        cineFxInto.Effects.Add(cineFx);
                        controlsNote = PlaceEffect(cineFxInto, cineFx, false) ?? controlsNote;
                        SaveCinematic(cineFxInto);
                        break;
                    }
                    case MenuAction.CineFxSet:
                    case MenuAction.CineFxDelete:
                    {
                        var cineFxIn = CineCurrent;
                        if (cineFxIn == null || c.Row < 0 || c.Row >= cineFxIn.Effects.Count) break;
                        var cineFxEdited = cineFxIn.Effects[c.Row];
                        if (c.Action == MenuAction.CineFxDelete) cineFxIn.Effects.RemoveAt(c.Row);
                        else if (c.Text == "Start") cineFxEdited.Start = Math.Clamp(c.Value, 0, 3600);
                        else if (c.Text == "Seconds") cineFxEdited.Seconds = Math.Clamp(c.Value, 0.5, 600);
                        else if (c.Text == "From") cineFxEdited.From = Math.Clamp(c.Value, -180, 180);
                        else if (c.Text == "Hold") cineFxEdited.To = cineFxEdited.From;
                        else if (c.Text == "To") cineFxEdited.To = Math.Clamp(c.Value, -180, 180);
                        else if (c.Text == "Lane") cineFxEdited.Lane = Math.Clamp((int)Math.Round(c.Value), 0, cineFxIn.Lanes - 1);
                        else if (c.Text != null && c.Text.StartsWith("Move|"))
                        {
                            // Dragged: a new start (Value) and layer, in one go.
                            cineFxEdited.Start = Math.Clamp(c.Value, 0, 3600);
                            if (int.TryParse(c.Text.Substring(5), out int cineFxLane)) cineFxEdited.Lane = Math.Clamp(cineFxLane, 0, cineFxIn.Lanes - 1);
                        }
                        if (c.Action != MenuAction.CineFxDelete && (c.Text == "Start" || c.Text == "Seconds" || c.Text == "Lane" || (c.Text != null && c.Text.StartsWith("Move|"))))
                            controlsNote = PlaceEffect(cineFxIn, cineFxEdited, c.Text == "Seconds") ?? controlsNote;
                        SaveCinematic(cineFxIn);
                        break;
                    }
                    case MenuAction.CineShotDuplicate:
                    {
                        var cineDupIn = CineCurrent;
                        if (cineDupIn == null || c.Row < 0 || c.Row >= cineDupIn.Shots.Count) break;
                        var cineCopy = System.Text.Json.JsonSerializer.Deserialize<CineShot>(System.Text.Json.JsonSerializer.Serialize(cineDupIn.Shots[c.Row]));
                        cineCopy.Keys ??= new List<double[]>();
                        cineCopy.KeysShip ??= new List<double[]>();
                        cineDupIn.Shots.Insert(c.Row + 1, cineCopy);
                        controlsNote = $"Shot {c.Row + 1} copied: the copy is shot {c.Row + 2}";
                        SaveCinematic(cineDupIn);
                        break;
                    }
                    case MenuAction.CineUndo:
                    case MenuAction.CineRedo:
                    {
                        var cineBack = CineCurrent;
                        if (cineBack == null) break;
                        bool cineIsUndo = c.Action == MenuAction.CineUndo;
                        var cineFrom = (cineIsUndo ? cineUndo : cineRedo).TryGetValue(cineBack, out var cineStackFrom) ? cineStackFrom : null;
                        if (cineFrom == null || cineFrom.Count == 0) { controlsNote = cineIsUndo ? "Nothing to undo" : "Nothing to redo"; break; }
                        string cineState = cineFrom[cineFrom.Count - 1];
                        cineFrom.RemoveAt(cineFrom.Count - 1);
                        CineStack(cineIsUndo ? cineRedo : cineUndo, cineBack).Add(CineSnap(cineBack));
                        try { RestoreCinematic(cineBack, cineState); controlsNote = cineIsUndo ? "Undone" : "Redone"; }
                        catch (Exception cineUndoError) { controlsNote = "Couldn't undo: " + cineUndoError.Message; }
                        break;
                    }
                    case MenuAction.CineStop:
                        if (cinePlay != null) EndCinematic("Cinematic stopped");
                        break;
                    case MenuAction.CineOpenFolder:
                        try
                        {
                            Directory.CreateDirectory(CineFolder);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = CineFolder, UseShellExecute = true });
                        }
                        catch (Exception cineFolderError) { controlsNote = "Couldn't open the folder: " + cineFolderError.Message; }
                        break;
                    case MenuAction.SpotlightStart: StartSpotlight(c.Text); break;
                    case MenuAction.SpotCompareSet:
                    {
                        string cmpPath = c.Text;
                        if (string.IsNullOrEmpty(cmpPath) || cmpPath == spotComparePath) { SetSpotCompare(null); controlsNote = "Comparing off"; break; }
                        var cmp = InFolder(cmpPath, SpotlightFolder) ? ReadSpotlight(cmpPath) : null;
                        if (cmp == null) { controlsNote = "Couldn't read that race file"; break; }
                        SetSpotCompare(cmpPath, cmp);
                        controlsNote = $"Comparing with {cmp.Ship}'s race ({cmp.Date}, {LapTimer.Format(cmp.Total)}): the spotlight shows a head to head page, and with auto race its ghost flies alongside";
                        break;
                    }
                    case MenuAction.SpotAutoSet:
                    {
                        var autoParts = (c.Text ?? "").Split('|');
                        string autoFile = autoParts.Length >= 2 ? autoParts[autoParts.Length - 1] : null;
                        if (autoFile == null || !InFolder(autoFile, SpotlightFolder)) break;
                        settings.SpotlightAuto ??= new Dictionary<string, GateSettings.SpotAuto>();
                        string autoKey = Path.GetFileName(autoFile);
                        if (!settings.SpotlightAuto.TryGetValue(autoKey, out var autoSet) || autoSet == null) settings.SpotlightAuto[autoKey] = autoSet = new GateSettings.SpotAuto();
                        if (autoParts[0] == "Race")
                        {
                            autoSet.Race = !autoSet.Race;
                            controlsNote = autoSet.Race ? "Auto race on: when this race's spotlight starts, your ship goes to the start and the autopilot flies the race" : "Auto race off";
                        }
                        else if (autoParts[0] == "Cine" && autoParts.Length >= 3)
                        {
                            autoSet.Cinematic = string.IsNullOrEmpty(autoParts[1]) ? null : autoParts[1];
                            controlsNote = autoSet.Cinematic == null ? "No cinematic when this spotlight starts" : $"When this spotlight starts, '{autoSet.Cinematic}' plays on the ship";
                        }
                        if (!autoSet.Race && autoSet.Cinematic == null && autoSet.Grid == null) settings.SpotlightAuto.Remove(autoKey);
                        settings.Save(settingsPath);
                        recordsVersion++;
                        break;
                    }
                    case MenuAction.SpotlightStop:
                        if (analyserMode && spotlight != null) { SetAnalyserMode(false, playerPosition); controlsNote = "Race spotlight closed"; }
                        break;
                    case MenuAction.SpotlightOpenFolder:
                        try
                        {
                            Directory.CreateDirectory(SpotlightFolder);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = SpotlightFolder, UseShellExecute = true });
                        }
                        catch (Exception spotFolderError) { controlsNote = "Couldn't open the folder: " + spotFolderError.Message; }
                        break;
                    case MenuAction.ResultsLapPrev: StepResultsLap(-1); break;
                    case MenuAction.ResultsLapNext: StepResultsLap(1); break;
                    case MenuAction.SetRpiValue:
                    {
                        var rpiClass = RpiClassInUse;
                        if (rpiClass == null || string.IsNullOrEmpty(c.Text)) break;
                        if (c.Value == 0) rpiClass.Values.Remove(c.Text); else rpiClass.Values[c.Text] = c.Value;
                        rpiSaveAt = game.TimeSeconds + 1;   // saved a moment after the last change (not on every key typed)
                        break;
                    }
                    case MenuAction.SetRpiBudget:
                        if (RpiClassInUse != null) { RpiClassInUse.Budget = Math.Max(0, c.Value); rpiSaveAt = game.TimeSeconds + 1; }
                        break;
                    case MenuAction.SetRpiMinWeight:
                        if (RpiClassInUse != null) { RpiClassInUse.MinWeight = Math.Max(0, c.Value); rpiSaveAt = game.TimeSeconds + 1; }
                        break;
                    case MenuAction.RpiNewClass:
                    {
                        string newName = c.Text?.Trim();
                        if (string.IsNullOrEmpty(newName)) { controlsNote = "Type a name for the new class first"; break; }
                        if (RpiClassNamed(newName) != null) { controlsNote = $"There's already a class called '{newName}'"; break; }
                        settings.RpiClasses.Add(new GateSettings.RpiClass { Name = newName, Budget = Math.Max(0, c.Value) });
                        settings.RpiClassName = newName;
                        RpiChanged();
                        controlsNote = $"RPI class '{newName}' made and in use";
                        break;
                    }
                    case MenuAction.RpiSelectClass:
                    {
                        var rpiPicked = RpiClassNamed(c.Text);
                        settings.RpiClassName = rpiPicked?.Name;
                        RpiChanged();
                        controlsNote = rpiPicked != null ? $"RPI class in use: {rpiPicked.Name}" : "No RPI class in use";
                        break;
                    }
                    case MenuAction.RpiRenameClass:
                    {
                        var renamed = RpiClassInUse;
                        string rpiTo = c.Text?.Trim();
                        if (renamed == null) { controlsNote = "Pick a class to rename first"; break; }
                        if (string.IsNullOrEmpty(rpiTo)) { controlsNote = "Type the new name first"; break; }
                        var rpiClash = RpiClassNamed(rpiTo);
                        if (rpiClash != null && !ReferenceEquals(rpiClash, renamed)) { controlsNote = $"There's already a class called '{rpiTo}'"; break; }
                        renamed.Name = rpiTo;
                        settings.RpiClassName = rpiTo;
                        RpiChanged();
                        controlsNote = $"Class renamed to '{rpiTo}'";
                        break;
                    }
                    case MenuAction.RpiDeleteClass:
                    {
                        var rpiGone = RpiClassNamed(c.Text);
                        if (rpiGone == null) break;
                        settings.RpiClasses.Remove(rpiGone);
                        if (settings.RpiClassName == rpiGone.Name) settings.RpiClassName = settings.RpiClasses.FirstOrDefault()?.Name;
                        RpiChanged();
                        controlsNote = $"RPI class '{rpiGone.Name}' deleted";
                        break;
                    }
                    case MenuAction.RpiExport:
                    {
                        var exported = RpiClassInUse;
                        if (exported == null) { controlsNote = "Pick a class to export first"; break; }
                        try
                        {
                            Directory.CreateDirectory(RpiFolder);
                            string exportPath = Path.Combine(RpiFolder, SafeFileName(exported.Name) + RpiExtension);
                            File.WriteAllText(exportPath, System.Text.Json.JsonSerializer.Serialize(exported, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                            rpiFilesCache = null;
                            rpiVersion++;
                            controlsNote = $"Exported to {Path.GetFileName(exportPath)} in the RPI classes folder (Open folder to find it and share it)";
                        }
                        catch (Exception exportError) { controlsNote = "Couldn't export: " + exportError.Message; }
                        break;
                    }
                    case MenuAction.RpiImport:
                    {
                        var imported = c.Text != null && InFolder(c.Text, RpiFolder) ? ReadRpiFile(c.Text) : null;
                        if (imported == null) { controlsNote = "Couldn't read that class file"; break; }
                        AddRpiClass(imported, "imported");
                        break;
                    }
                    case MenuAction.RpiImportText:
                    {
                        GateSettings.RpiClass pasted = null;
                        try { pasted = string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > 2_000_000 ? null : System.Text.Json.JsonSerializer.Deserialize<GateSettings.RpiClass>(c.Text); }
                        catch (Exception pasteError) { PluginLog.Info("RPI class paste: " + pasteError.Message); }
                        if (pasted == null || string.IsNullOrWhiteSpace(pasted.Name)) { controlsNote = "The clipboard doesn't hold an RPI class (copy one with the Copy button first)"; break; }
                        pasted.Name = pasted.Name.Trim();
                        CleanRpiClass(pasted);
                        AddRpiClass(pasted, "pasted");
                        break;
                    }
                    case MenuAction.RpiDuplicateClass:
                    {
                        var original = RpiClassInUse;
                        if (original == null) { controlsNote = "Pick a class to duplicate first"; break; }
                        string copyName = string.IsNullOrWhiteSpace(c.Text) ? original.Name + " copy" : c.Text.Trim();
                        if (RpiClassNamed(copyName) != null) { controlsNote = $"There's already a class called '{copyName}': type another name first"; break; }
                        settings.RpiClasses.Add(new GateSettings.RpiClass
                        {
                            Name = copyName, Budget = original.Budget, MinWeight = original.MinWeight,
                            Values = new Dictionary<string, double>(original.Values),
                            Limits = new Dictionary<string, int>(original.Limits),
                            Steps = original.Steps.Select(x => new GateSettings.RpiStep { Weight = x.Weight, Budget = x.Budget }).ToList(),
                        });
                        settings.RpiClassName = copyName;
                        RpiChanged();
                        controlsNote = $"'{original.Name}' duplicated as '{copyName}' (now in use)";
                        break;
                    }
                    case MenuAction.SetRpiLimit:
                    {
                        var limited = RpiClassInUse;
                        if (limited == null || string.IsNullOrEmpty(c.Text)) break;
                        if (c.Value < 0) limited.Limits.Remove(c.Text); else limited.Limits[c.Text] = (int)Math.Round(c.Value);
                        rpiSaveAt = game.TimeSeconds + 1;
                        break;
                    }
                    case MenuAction.RpiAddStep:
                    {
                        var stepped = RpiClassInUse;
                        if (stepped == null) break;
                        if (!double.TryParse(c.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double stepBudget) || c.Value <= 0)
                        { controlsNote = "Type the step's weight and budget first"; break; }
                        if (stepped.MinWeight <= 0 || c.Value >= stepped.MinWeight) { controlsNote = "A step's weight has to be under the class's minimum weight (set that first)"; break; }
                        stepped.Steps.RemoveAll(x => Math.Abs(x.Weight - c.Value) < 0.5);
                        stepped.Steps.Add(new GateSettings.RpiStep { Weight = c.Value, Budget = Math.Max(0, stepBudget) });
                        stepped.Steps.Sort((x, y) => y.Weight.CompareTo(x.Weight));
                        RpiChanged();
                        controlsNote = $"Ships from {c.Value:#,0} kg get a budget of {stepBudget:0.##}";
                        break;
                    }
                    case MenuAction.RpiRemoveStep:
                    {
                        var unstepped = RpiClassInUse;
                        if (unstepped == null) break;
                        unstepped.Steps.RemoveAll(x => Math.Abs(x.Weight - c.Value) < 0.5);
                        RpiChanged();
                        break;
                    }
                    case MenuAction.RpiOpenFolder:
                        try
                        {
                            Directory.CreateDirectory(RpiFolder);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = RpiFolder, UseShellExecute = true });
                        }
                        catch (Exception folderError) { controlsNote = "Couldn't open the folder: " + folderError.Message; }
                        break;
                    case MenuAction.ShipAnalyser:
                        if (analyserMode && spotlight == null) { SetAnalyserMode(false, playerPosition); controlsNote = "Ship analyser closed"; break; }
                        if (analyserMode) SetAnalyserMode(false, playerPosition);   // (the spotlight was open: over to the plain analyser)
                        if (replaying) { controlsNote = "Stop the replay first"; break; }
                        SetAnalyserMode(true, playerPosition);
                        game.CloseControlsMenu();
                        break;
                    case MenuAction.ToggleEditMode:
                        SetAnalyserMode(false, playerPosition);
                        SetEditMode(!editMode, playerPosition);
                        controlsNote = editMode ? "Course edit mode on: close the menu, aim at a grid and left click" : "Course edit mode off";
                        break;
                    case MenuAction.SaveToWorld: SaveCoursesToWorld(false); break;
                    case MenuAction.ReverseCourse:
                        ReverseCourse(playerPosition);
                        controlsNote = timer.Gate == null ? "Set up a course first" : $"Course reversed: now {(CourseReversed ? "the other way round" : "the original way round")}";
                        break;
                    case MenuAction.LoadFromWorld:
                    {
                        string folder = game.WorldSaveFolder;
                        var wc = folder == null ? null : GateSettings.WorldCourses.Load(folder);
                        if (wc?.Current?.Gate == null) { controlsNote = folder == null ? "Couldn't find this world's save folder" : "This world's save has no race course"; break; }
                        MergeWorldCourses(wc);
                        ApplyCourse(wc.Current);
                        controlsNote = $"Course '{wc.Current.Name ?? "course"}' loaded from the world save ({timer.Checkpoints.Count} checkpoints)";
                        break;
                    }
                    case MenuAction.DeleteShip:
                    {
                        var r = CourseRecord(false);
                        if (r?.Ships != null && c.Text != null && r.Ships.TryGetValue(c.Text, out var gone) && r.Ships.Remove(c.Text))
                        {
                            string moved = PromoteNextBestShip(r, gone);
                            settings.Save(settingsPath);
                            recordsVersion++;
                            minimapKey = 0;   // the racing line may have changed
                            controlsNote = $"Times for {c.Text} cleared" + (moved != null ? $". Course best lap is now {moved}'s" : "");
                        }
                        break;
                    }
                    case MenuAction.ClearCourseRecords:
                    {
                        string key = CourseKey();
                        if (key != null && settings.Courses.Remove(key))
                        {
                            settings.Save(settingsPath);
                            recordsVersion++;
                            controlsNote = "Best times for this course cleared";
                        }
                        else controlsNote = key == null ? "No course here yet: place a start gate first" : "This course has no best times yet";
                        break;
                    }
                    case MenuAction.SaveCourse: SaveCourse(c.Text); break;
                    case MenuAction.AddSector:
                    {
                        var parts = (c.Text ?? "").Split(new[] { '|' }, 3);
                        int last = CourseRoute().Count - 1;
                        if (parts.Length < 3 || !int.TryParse(parts[0], out int from) || !int.TryParse(parts[1], out int to)) break;
                        string name = parts[2].Trim();
                        if (name.Length == 0) { controlsNote = "Type a name for the sector first"; break; }
                        if (last < 1) { controlsNote = "Set up the course first (a gate and at least one checkpoint)"; break; }
                        from = Math.Clamp(from, 0, last); to = Math.Clamp(to, 0, last);
                        if (to <= from) { controlsNote = "The sector has to end after it starts"; break; }
                        settings.CourseSectors.Add(new GateSettings.NamedSector { Name = name, From = from, To = to });
                        coursesVersion++;
                        controlsNote = $"Sector '{name}' added (saved with the world next time it's saved)";
                        break;
                    }
                    case MenuAction.EditSector:
                    {
                        if (c.Row < 0 || c.Row >= settings.CourseSectors.Count) break;
                        var sec = settings.CourseSectors[c.Row];
                        var parts = (c.Text ?? "").Split(new[] { '|' }, 3);
                        if (parts.Length < 3 || !int.TryParse(parts[1], out int from) || !int.TryParse(parts[2], out int to)) break;
                        int last = CourseRoute().Count - 1;
                        from = Math.Clamp(from, 0, Math.Max(0, last)); to = Math.Clamp(to, 0, Math.Max(0, last));
                        if (to <= from) { controlsNote = "The sector has to end after it starts"; recordsVersion++; coursesVersion++; break; }
                        string newName = parts[0].Trim();
                        if (newName.Length == 0) newName = sec.Name;
                        bool renamed = newName != sec.Name, moved = from != sec.From || to != sec.To;
                        if (!renamed && !moved) break;
                        controlsNote = renamed && moved ? $"Sector '{sec.Name}' is now '{newName}', with a new range"
                            : renamed ? $"Sector '{sec.Name}' renamed to '{newName}'" : $"Sector '{sec.Name}' moved";
                        sec.Name = newName; sec.From = from; sec.To = to;
                        coursesVersion++;
                        break;
                    }
                    case MenuAction.DeleteSector:
                        if (c.Row >= 0 && c.Row < settings.CourseSectors.Count)
                        {
                            controlsNote = $"Sector '{settings.CourseSectors[c.Row].Name}' removed";
                            settings.CourseSectors.RemoveAt(c.Row);
                            coursesVersion++;
                        }
                        break;
                    case MenuAction.SetStartSpot: controlsNote = SetStartSpotHere(); break;
                    case MenuAction.ClearStartSpot:
                        settings.CourseStartSpot = null;
                        settings.Save(settingsPath);
                        coursesVersion++;
                        controlsNote = "Start spot cleared: reset teleports to just behind the start gate";
                        break;
                    case MenuAction.ToggleCourseGridGlow:
                        settings.CourseGridGlow = !settings.CourseGridGlow;
                        settings.Save(settingsPath);
                        coursesVersion++;
                        controlsNote = settings.CourseGridGlow
                            ? "This course: checkpoints on grids get the glowing frame too (with the Glow (game 3D UI) air gate look)"
                            : "This course: checkpoints on grids show only their markers";
                        break;
                    case MenuAction.LoadCourse: LoadCourse(c.Text); break;
                    case MenuAction.DeleteCourse:
                        settings.SavedCourses.RemoveAll(x => x.World == game.WorldId && x.Name == c.Text);
                        if (CurrentCourseName() == c.Text) settings.CurrentCourseName.Remove(game.WorldId);
                        settings.Save(settingsPath);
                        coursesVersion++;
                        controlsNote = $"Course '{c.Text}' deleted";
                        break;
                    case MenuAction.CheckpointUp:
                    case MenuAction.CheckpointDown:
                    case MenuAction.DeleteCheckpoint:
                        EditCheckpoint(c.Action, c.Row);
                        break;
                    case MenuAction.AutoBuild: AutoBuild(); break;
                    case MenuAction.StopReplay:
                        if (replaying || replayPending) StopReplay("Replay stopped");
                        break;
                    case MenuAction.WatchReplay:
                        pendingTrace = null;
                        if (CourseRecord(false)?.BestLapTrace == null) controlsNote = "No best lap recorded on this course yet";
                        else { replayPending = true; controlsNote = "The replay starts when you close the menu (press Esc)"; }
                        break;
                    case MenuAction.AntennasOff:
                    case MenuAction.AntennasOn:
                    {
                        bool on = c.Action == MenuAction.AntennasOn;
                        int changed = game.SetAntennasEnabled(on, out int total);
                        controlsNote = total == 0 ? "No antennas found in this world"
                            : $"Turned {(on ? "on" : "off")} {changed} of {total} antenna{(total == 1 ? "" : "s")}"
                              + (changed < total ? $" ({total - changed} were already {(on ? "on" : "off")})" : "");
                        break;
                    }
                    case MenuAction.ClearAllRecords:
                        settings.Courses.Clear();
                        settings.BestSeconds = null;
                        settings.Save(settingsPath);
                        recordsVersion++;
                        controlsNote = "Best times for every course cleared";
                        break;
                    case MenuAction.Close:
                        capturing = false;
                        game.CloseControlsMenu();
                        break;
                }
            }

            controlsOpen = game.ControlsMenuOpen;

            // Save slider changes half a second after the last one, or when the menu closes.
            if (settingsDirtySince >= 0 && (!controlsOpen || Environment.TickCount64 - settingsDirtySince > 500))
            {
                settings.Save(settingsPath);
                settingsDirtySince = -1;
            }

            if (!controlsOpen) { capturing = false; return; }

            if (capturing)
            {
                foreach (int vk in keys.NewKeys)
                {
                    if (vk == 0x1B) // Esc
                    {
                        capturing = false;
                        controlsNote = "Cancelled";
                        break;
                    }
                    var action = actions[controlsRow].Key;
                    var b = new KeyBinding(vk, keys.Ctrl, keys.Shift, keys.Alt);
                    settings.SetBinding(action, b);
                    settings.Save(settingsPath);
                    capturing = false;
                    string clash = ClashFor(action, b);
                    controlsNote = clash == null ? $"{actions[controlsRow].Name}: {b}" : $"{b} is also used by: {clash}";
                    break;
                }
            }

            // The menu's contents only need rebuilding about 8 times a second (straight away after a click).
            long nowMs = Environment.TickCount64;
            if (!hadCommand && !capturing && nowMs - panelBuiltAt < 120) return;
            panelBuiltAt = nowMs;
            game.ShowControlsMenu(BuildControlsPanel());
        }

        private long panelBuiltAt;

        // ---------------------------------------------------------------- courses

        private string CurrentCourseName() => settings.CurrentCourseName.TryGetValue(game.WorldId, out var n) ? n : null;

        private static GateSettings.SavedGate Copy(GateSettings.SavedGate g, string world) => GateSettings.SavedGate.From(g.ToGate(), world);

        /// <summary>Copy of a start spot (a position and facing, no size), or null if it's incomplete.</summary>
        private static GateSettings.SavedGate CopySpot(GateSettings.SavedGate g, string world) =>
            g?.Center?.Length == 3 && g.Forward?.Length == 3 && g.Up?.Length == 3
                ? new GateSettings.SavedGate { World = world, Center = (double[])g.Center.Clone(), Forward = (double[])g.Forward.Clone(), Up = (double[])g.Up.Clone() }
                : null;

        private void SaveCourse(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) { controlsNote = "Type a name for the course first"; return; }
            if (timer.Gate == null) { controlsNote = "Place a start/finish gate first"; return; }
            string world = game.WorldId;
            settings.SavedCourses.RemoveAll(x => x.World == world && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            settings.SavedCourses.Add(new GateSettings.SavedCourse
            {
                Name = name,
                World = world,
                Gate = GateSettings.SavedGate.From(timer.Gate, world),
                Finish = timer.FinishGate == null ? null : GateSettings.SavedGate.From(timer.FinishGate, world),
                Checkpoints = WorldCheckpoints().Select(x => Copy(x, world)).ToList(),
                Reversed = CourseReversed,
                Sectors = settings.CourseSectors.Select(x => new GateSettings.NamedSector { Name = x.Name, From = x.From, To = x.To }).ToList(),
                StartSpot = settings.CourseStartSpot == null ? null : CopySpot(settings.CourseStartSpot, world),
                GridGlow = settings.CourseGridGlow,
            });
            settings.CurrentCourseName[world] = name;
            settings.Save(settingsPath);
            coursesVersion++;
            controlsNote = $"Course '{name}' saved ({timer.Checkpoints.Count} checkpoints)";
        }

        private void LoadCourse(string name)
        {
            string world = game.WorldId;
            var course = settings.SavedCourses.FirstOrDefault(x => x.World == world && x.Name == name);
            if (course == null) { controlsNote = $"Course '{name}' not found"; return; }
            SetGate(course.Gate?.ToGate(), playerPosition);
            SetFinishGate(course.Finish?.ToGate(), playerPosition);
            SaveCheckpoints(course.Checkpoints.Select(x => Copy(x, world)).ToList(), playerPosition);
            settings.CourseSectors = (course.Sectors ?? new List<GateSettings.NamedSector>()).Select(x => new GateSettings.NamedSector { Name = x.Name, From = x.From, To = x.To }).ToList();
            settings.CourseStartSpot = course.StartSpot == null ? null : CopySpot(course.StartSpot, world);
            settings.CourseGridGlow = course.GridGlow;
            settings.CurrentCourseName[world] = name;
            settings.Save(settingsPath);
            controlsNote = $"Course '{name}' loaded. Fly through the gate to race.";
        }

        /// <summary>Move a checkpoint up or down the order, or delete it. The race resets.</summary>
        private void EditCheckpoint(MenuAction action, int row)
        {
            var list = WorldCheckpoints();
            if (row < 0 || row >= list.Count) return;
            if (action == MenuAction.DeleteCheckpoint)
            {
                list.RemoveAt(row);
                controlsNote = $"CP {row + 1} deleted; the ones after it move up";
            }
            else
            {
                int other = action == MenuAction.CheckpointUp ? row - 1 : row + 1;
                if (other < 0 || other >= list.Count) return;
                (list[row], list[other]) = (list[other], list[row]);
                controlsNote = $"CP {row + 1} is now CP {other + 1}";
                // The order changed, so this course's checkpoint times and best-lap path no longer match it.
                var rec = CourseRecord(false);
                if (rec != null)
                {
                    rec.BestSplits.Clear();
                    rec.BestSectors.Clear();
                    rec.BestSectorTraces = null; possibleTrace = null;
                    rec.BestLapSplits.Clear();
                    rec.BestLapTrace = null;
                    rec.BestLapSpeed = null;
                    if (rec.Ships != null)
                        foreach (var sh in rec.Ships.Values) { sh.BestSectors?.Clear(); sh.BestLapSpeed = null; }
                }
            }
            SaveCheckpoints(list, playerPosition);
        }

        /// <summary>"Start", "Finish", "Start/Finish" = the gate; "CP3", "CP 3", "Checkpoint 3", "#3" or just "3" = checkpoint 3.</summary>
        public static bool ParseCourseName(string name, out int checkpoint, out bool isGate)
        {
            checkpoint = 0;
            isGate = false;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string t = name.Trim().ToLowerInvariant();
            if (t.Contains("start") || t.Contains("finish")) { isGate = true; return true; }
            var m = Regex.Match(t, @"^(?:cp|checkpoint|check point|ring|gate)?\s*[#\-_.:]?\s*(\d{1,3})$");
            return m.Success && int.TryParse(m.Groups[1].Value, out checkpoint) && checkpoint > 0;
        }

        /// <summary>
        /// Builds the course from names: a grid (or an antenna on it) called "Start", "Finish" or "Start/Finish"
        /// becomes the gate, and ones called "CP1", "CP 2", "Checkpoint 3" or just "4" become checkpoints in number order.
        /// </summary>
        private void AutoBuild()
        {
            var grids = game.FindNamedGrids(nm => ParseCourseName(nm, out _, out _), settings.FitToOpening);
            NamedGrid gateGrid = null;
            var cps = new SortedDictionary<int, NamedGrid>();
            foreach (var g in grids)
                foreach (var nm in g.Names)
                {
                    if (!ParseCourseName(nm, out int cp, out bool isGate)) continue;
                    if (isGate) { if (gateGrid == null) gateGrid = g; }
                    else if (!cps.ContainsKey(cp)) cps[cp] = g;
                    break; // first usable name decides what this grid is
                }

            if (gateGrid == null && cps.Count == 0)
            {
                controlsNote = "No grids or antennas named Start/Finish, CP1, CP2... (or 1, 2, 3...) found";
                return;
            }
            string world = game.WorldId;
            if (gateGrid != null) SetGate(GateFromGrid(gateGrid.Box), playerPosition);
            SaveCheckpoints(cps.Values.Select(g => GateSettings.SavedGate.From(GateFromGrid(g.Box), world)).ToList(), playerPosition);
            settings.CurrentCourseName.Remove(world);
            settings.Save(settingsPath);
            controlsNote = (gateGrid != null ? $"Built: start/finish '{gateGrid.Names[0]}'" : "Built (no Start/Finish found, kept the old gate)")
                + $" + {cps.Count} checkpoint{(cps.Count == 1 ? "" : "s")}"
                + (cps.Count > 0 ? $" ({string.Join(", ", cps.Keys)})" : "");
        }

        /// <summary>Name of another action using the same shortcut, or null.</summary>
        /// <summary>The edit tool wheel only works in edit mode and Reset race only outside it, so they can share a key.</summary>
        private static bool SharedOk(GateKey a, GateKey b) =>
            (a == GateKey.EditWheel && b == GateKey.ResetTimer) || (a == GateKey.ResetTimer && b == GateKey.EditWheel);

        private string ClashFor(GateKey action, KeyBinding b)
        {
            foreach (var (k, name) in KeyBindings.Actions)
                if (k != action && !SharedOk(k, action) && settings.GetBinding(k).SameAs(b)) return name;
            return null;
        }

        private HudModel.ControlsPanel BuildControlsPanel()
        {
            var p = new HudModel.ControlsPanel();
            for (int i = 0; i < KeyBindings.Actions.Length; i++)
            {
                var (key, name) = KeyBindings.Actions[i];
                var b = settings.GetBinding(key);
                bool selected = i == controlsRow;
                p.Rows.Add(new HudModel.ControlsRow
                {
                    Name = name,
                    Section = KeyBindings.CategoryOf(key),
                    Binding = selected && capturing ? "Press keys..." : b.ToString(),
                    Capturing = selected && capturing,
                    Clash = ClashFor(key, b) != null,
                    Changed = !b.SameAs(KeyBindings.Default(key)),
                });
            }
            p.Note = controlsNote;
            p.Help = capturing
                ? "Press the new key combination (with Ctrl / Shift / Alt if you want them).  Esc or click again to cancel."
                : "Click a shortcut to change it.";

            // Records tab: this course's best races, best 5 per lap count.
            p.RecordsVersion = recordsVersion;
            var rec = CourseRecord(false);
            if (timer.Gate == null) p.RecordsTitle = "No course here yet: place a start gate to start racing.";
            else if (rec == null || rec.History.Count == 0) p.RecordsTitle = "No finished races on this course yet. Set a lap count and finish a race to fill this in.";
            else
            {
                double? possible = PossibleLap(rec, timer.Checkpoints.Count);
                p.RecordsTitle = $"This course: {timer.Checkpoints.Count} checkpoint{(timer.Checkpoints.Count == 1 ? "" : "s")}"
                    + (rec.BestLap.HasValue ? $"   \u00B7   best lap {LapTimer.Format(rec.BestLap.Value)}" : "")
                    + (possible.HasValue ? $"   \u00B7   possible lap {LapTimer.Format(possible.Value)}" : "")
                    + (possible.HasValue && timer.LapTarget > 0 ? $"   \u00B7   possible {timer.LapTarget}-lap race {LapTimer.Format(possible.Value * timer.LapTarget)}" : "");
                foreach (var g in rec.History.GroupBy(x => x.Laps).OrderBy(x => x.Key))
                {
                    int rank = 0;
                    foreach (var h in g.OrderBy(x => x.Total))
                    {
                        rank++;
                        p.Records.Add(new HudModel.HistoryRow
                        {
                            Group = $"{g.Key} LAP{(g.Key == 1 ? "" : "S")}",
                            Rank = rank,
                            Total = LapTimer.Format(h.Total),
                            Date = h.Date.ToString("d MMM  HH:mm"),
                            Penalty = h.Penalty > 0 ? $"+{h.Penalty:0.#}s" : "clean",
                            BestLap = LapTimer.Format(h.BestLap),
                            TopSpeed = h.TopSpeed > 0 ? GameOptions.Speed(h.TopSpeed) : "",
                            Ship = h.Ship ?? "",
                            IsRecord = rank == 1,
                        });
                    }
                }
            }

            // Records tab: ships compared on this course.
            p.CurrentShip = game.ShipName;
            if (rec?.Ships != null && rec.Ships.Count > 0)
            {
                int raceLaps = timer.LapTarget > 0 ? timer.LapTarget
                    : rec.Ships.Values.SelectMany(x => x.BestRace.Keys).DefaultIfEmpty(1).GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key;
                p.ShipsRaceHeader = $"BEST {raceLaps}-LAP";
                double fastestLap = rec.Ships.Values.Where(x => x.BestLap.HasValue).Select(x => x.BestLap.Value).DefaultIfEmpty(0).Min();
                double fastestRace = rec.Ships.Values.Where(x => x.BestRace.ContainsKey(raceLaps)).Select(x => x.BestRace[raceLaps]).DefaultIfEmpty(0).Min();
                var averages = ShipAverages(CurrentCourseName(), raceLaps);
                p.ShipsAverageHeader = $"AVG {raceLaps}-LAP";
                foreach (var kv in rec.Ships.OrderBy(x => x.Value.BestLap ?? double.MaxValue))
                {
                    var sr = kv.Value;
                    bool hasRace = sr.BestRace.TryGetValue(raceLaps, out double race);
                    p.Ships.Add(new HudModel.ShipRow
                    {
                        Name = kv.Key,
                        BestLap = sr.BestLap.HasValue ? LapTimer.Format(sr.BestLap.Value) : "-",
                        Gap = !sr.BestLap.HasValue ? "" : sr.BestLap.Value <= fastestLap + 1e-9 ? "fastest" : $"+{sr.BestLap.Value - fastestLap:0.000}",
                        BestRace = hasRace ? LapTimer.Format(race) : "-",
                        RaceGap = !hasRace ? "" : race <= fastestRace + 1e-9 ? "fastest" : $"+{race - fastestRace:0.000}",
                        TopSpeed = sr.TopSpeed > 0 ? GameOptions.Speed(sr.TopSpeed) : "",
                        TurnG = sr.MaxTurnG > 0 ? $"{sr.MaxTurnG:0.0} g" : "",
                        Races = sr.Races.ToString(),
                        Average = averages.TryGetValue(kv.Key, out var av) ? LapTimer.Format(av.Average) : "-",
                        AverageNote = averages.TryGetValue(kv.Key, out var av2) ? $"{av2.Count} race{(av2.Count == 1 ? "" : "s")}" : "",
                        Fastest = sr.BestLap.HasValue && sr.BestLap.Value <= fastestLap + 1e-9,
                        FastestRace = hasRace && race <= fastestRace + 1e-9,
                        Current = kv.Key == p.CurrentShip,
                        HasReplay = sr.BestLapTrace != null && sr.BestLapTrace.Count >= 2,
                        Second = secondGhostKey == "ship:" + kv.Key,
                        Racing = racingShip == kv.Key,
                        LastLap = sr.LastLap.HasValue && sr.LastLapTrace != null && sr.LastLapTrace.Count >= 2 ? LapTimer.Format(sr.LastLap.Value) : null,
                        RacingLast = raceGhost?.Path == "shiplast:" + kv.Key,
                    });
                }
            }

            // Records tab: ghost files.
            if (ghostFiles == null) ghostFiles = GhostFile.LoadAll(GhostFolder);
            p.GhostFolder = GhostFolder;
            p.PilotName = settings.PilotName;
            p.CourseHasBest = rec?.BestLapTrace != null;
            p.RacingPossible = racePossible;
            p.RacingBestRace = raceBestRace;
            {
                var bestRaceLaps = BestRaceTracesFor(timer.LapTarget);
                p.BestRaceLine = bestRaceLaps != null && rec != null && rec.BestRace.TryGetValue(timer.LapTarget, out double bestRaceTime)
                    ? $"{timer.LapTarget} lap{(timer.LapTarget == 1 ? "" : "s")}, {LapTimer.Format(bestRaceTime)}" : null;
            }
            var possibleLine = PossibleTrace();
            p.PossibleLine = possibleLine != null ? LapTimer.Format(possibleLine[possibleLine.Count - 1][3]) : null;
            bool racingFile = raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count);
            if (!racingFile) foreach (var row in p.Ships) { row.Racing = false; row.RacingLast = false; }
            p.RacingAgainst = racingFile ? raceGhost.Label : null;
            foreach (var gf in ghostFiles.OrderByDescending(x => x.Fits(timer.Gate, timer.Checkpoints.Count)).ThenBy(x => x.LapTime))
            {
                bool fits = gf.Fits(timer.Gate, timer.Checkpoints.Count);
                p.Ghosts.Add(new HudModel.GhostRow
                {
                    Path = gf.Path,
                    Label = gf.Label,
                    Detail = fits ? $"{gf.Course}, {gf.Date:d MMM yyyy}" : $"{gf.Course} - a different course",
                    Fits = fits,
                    Active = racingFile && raceGhost.Path == gf.Path,
                    Second = secondGhostKey == gf.Path,
                });
            }

            // Courses tab.
            p.CoursesVersion = coursesVersion;
            p.WorldFolder = game.WorldSaveFolder;
            p.EditMode = editMode;
            p.AnalyserOpen = analyserMode && spotlight == null;
            p.OrbitOpen = orbitCam;
            p.AutopilotOn = autopilot;
            p.Replaying = replaying || replayPending;
            p.SpotExporting = spotExport != null;
            foreach (var sp in SpotPages()) p.SpotPages.Add((sp.Page, SpotPageName(sp.Page), sp.On, sp.Seconds));
            p.AutopilotReady = AutopilotLine() != null;
            p.AutopilotKey = settings.GetBinding(GateKey.Autopilot).ToString();
            p.OrbitKey = settings.GetBinding(GateKey.OrbitCamera).ToString();
            p.OrbitLockKey = settings.GetBinding(GateKey.OrbitLock).ToString();
            p.SpotlightOpen = analyserMode && spotlight != null ? spotlightPath : null;
            p.SpotCompare = spotComparePath;
            p.SpotlightRacing = RacingSpotlight;
            p.CanExportRace = lastRace != null;
            p.LastRaceLine = lastRace == null ? null : $"{lastRace.Ship}, {lastRace.Course}, {LapTimer.Format(lastRace.Total)}{(lastRace.Burned ? ", burned" : "")}";
            p.SpotlightFolder = SpotlightFolder;
            // (The race files folder is looked at every few seconds, not every time the menu updates.)
            if (spotFilesCache == null || Environment.TickCount64 - spotFilesAt > 3000)
            {
                spotFilesAt = Environment.TickCount64;
                var spotFound = new List<(string Name, string Info, string Path)>();
                try
                {
                    if (Directory.Exists(SpotlightFolder))
                        foreach (string spotFile in Directory.GetFiles(SpotlightFolder, "*" + SpotlightExtension).OrderByDescending(x => File.GetLastWriteTime(x)).Take(30))
                        {
                            var read = ReadSpotlightCached(spotFile);
                            if (read == null) continue;
                            spotFound.Add(($"{read.Ship}  -  {read.Course}{(read.Burned ? "  (burned)" : "")}", $"{(read.Burned ? "BURNED (reset before the finish): " : "")}{LapTimer.Format(read.Total)} over {read.Laps.Count} lap{(read.Laps.Count == 1 ? "" : "s")}, {read.Date}{(string.IsNullOrWhiteSpace(read.Pilot) ? "" : ", by " + read.Pilot)}", spotFile));
                        }
                }
                catch (Exception spotListError) { PluginLog.Info("Race spotlight folder: " + spotListError.Message); }
                if (spotFilesCache != null && !spotFilesCache.SequenceEqual(spotFound)) recordsVersion++;
                spotFilesCache = spotFound;
            }
            // The ships that have race files (for the leaderboard's hide list): read with the files, every few seconds.
            if (spotShipsCache == null || spotShipsAt != spotFilesAt)
            {
                spotShipsAt = spotFilesAt;
                var shipsFound = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Directory.Exists(SpotlightFolder))
                        foreach (string spotFile in Directory.GetFiles(SpotlightFolder, "*" + SpotlightExtension).Take(300))
                        {
                            string shipName = ReadSpotlightCached(spotFile)?.Ship;
                            if (string.IsNullOrWhiteSpace(shipName)) continue;
                            shipsFound[shipName] = shipsFound.TryGetValue(shipName, out int had) ? had + 1 : 1;
                        }
                }
                catch { }
                // (A hidden ship stays in the list even with its files gone, so it can be shown again.)
                foreach (string hiddenName in settings.SpotBoardHiddenShips ?? new List<string>()) if (!shipsFound.ContainsKey(hiddenName)) shipsFound[hiddenName] = 0;
                // (In the running order set on the Race tab; ships not placed yet go after, by name.)
                var shipList = shipsFound.OrderBy(x => SpotRunIndex(x.Key)).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (x.Key, x.Value, (settings.SpotBoardHiddenShips ?? new List<string>()).Any(h => string.Equals(h, x.Key, StringComparison.OrdinalIgnoreCase)))).ToList();
                if (spotShipsCache != null && !spotShipsCache.SequenceEqual(shipList)) recordsVersion++;
                spotShipsCache = shipList;
            }
            p.SpotBoardShips.AddRange(spotShipsCache);
            p.SpotBoardBestPerShip = settings.SpotBoardBestPerShip;
            p.SpotBoardReveal = settings.SpotBoardReveal;
            p.SpotTourOn = spotTour != null;
            p.SpotTourCine = !string.IsNullOrEmpty(settings.SpotTourCinematic) && Cinematics().Any(x => x.Name == settings.SpotTourCinematic) ? settings.SpotTourCinematic : null;
            GateSettings.FilmingOn = settings.Filming;
            p.Spotlights.AddRange(spotFilesCache);
            foreach (var spotListed in spotFilesCache)
            {
                var autoNow = SpotAutoFor(spotListed.Path);
                p.SpotAuto[spotListed.Path] = (autoNow.Race, autoNow.Cinematic ?? "");
            }
            p.EditModeKey = settings.GetBinding(GateKey.EditMode).ToString();
            p.EditWheelKey = settings.GetBinding(GateKey.EditWheel).ToString();
            p.WorldHasCourses = p.WorldFolder != null && File.Exists(Path.Combine(p.WorldFolder, GateSettings.WorldCourses.FileName));
            p.CourseName = CurrentCourseName();
            int n = timer.Checkpoints.Count;
            p.CourseSummary = timer.Gate == null
                ? (n == 0 ? "No course yet: place a start/finish gate and checkpoints, or use Auto-build." : $"No start/finish gate yet, {n} checkpoint{(n == 1 ? "" : "s")}.")
                : timer.FinishGate != null
                    ? $"Start gate, {n} checkpoint{(n == 1 ? "" : "s")} and a separate finish gate (one run, start to finish)."
                    : $"Start/finish gate and {n} checkpoint{(n == 1 ? "" : "s")}.";
            Vec3D? previous = timer.Gate?.Center;
            for (int i = 0; i < n; i++)
            {
                var cpc = timer.Checkpoints[i].Center;
                string fromText = previous.HasValue ? $"   {Distance((cpc - previous.Value).Length)} from {(i == 0 ? "the gate" : $"CP {i}")}" : "";
                p.CourseCheckpoints.Add($"CP {i + 1}{fromText}");
                previous = cpc;
            }
            var routeNow = CourseRoute();
            for (int i = 0; i < routeNow.Count; i++)
                p.RoutePoints.Add(i == routeNow.Count - 1 && i > 0 && timer.FinishGate == null ? "Lap end (S/F)" : routeNow[i].Name);
            p.CourseGridGlow = settings.CourseGridGlow;
            var spot = settings.CourseStartSpot;
            p.StartSpot = spot?.Center == null || timer.Gate == null ? "Just behind the start gate"
                : $"Your spot, {Distance((new Vec3D(spot.Center[0], spot.Center[1], spot.Center[2]) - timer.Gate.Center).Length)} from the start gate";
            for (int k = 0; k < settings.CourseSectors.Count; k++)
            {
                var sec = settings.CourseSectors[k];
                string Pt(int i) => i >= 0 && i < p.RoutePoints.Count ? p.RoutePoints[i] : $"#{i}";
                p.Sectors.Add((sec.Name, $"{Pt(sec.From)} to {Pt(sec.To)}", sec.From, sec.To, SectorColours[k % SectorColours.Length]));
            }
            foreach (var sc in settings.SavedCourses.Where(x => x.World == game.WorldId).OrderBy(x => x.Name))
                p.SavedCourses.Add((sc.Name, $"{sc.Checkpoints.Count} checkpoint{(sc.Checkpoints.Count == 1 ? "" : "s")}", sc.Name == p.CourseName));

            p.SettingsVersion = settingsVersion;
            p.CineVersion = cineVersion;
            p.CineDefaultFov = game.DefaultFov;
            p.CineFolder = CineFolder;
            p.CineSpotlight = settings.SpotlightCinematic;
            p.CineDebugOn = settings.CineDebug;
            p.CineAutoOn = settings.CineAutoSwitch;
            if (timer.Gate != null)
            {
                string ctCourse = CourseKey();
                GateSettings.CineTrigger Assigned(int gate) => settings.CineTriggers.FirstOrDefault(x => x.Course == ctCourse && x.Gate == gate);
                void AddGate(string name, int gate) { var t = Assigned(gate); p.CineGates.Add((name, gate, t?.Cinematic, t?.Offset ?? 0)); }
                AddGate(timer.FinishGate != null ? "Start" : "Start / finish", 0);
                for (int ctIndex = 1; ctIndex <= timer.Checkpoints.Count; ctIndex++) AddGate($"CP {ctIndex}", ctIndex);
                if (timer.FinishGate != null) AddGate("Finish", -1);
            }
            FillHistoryPanel(p);
            p.CinePreviewOn = settings.CinePreview;
            if (!settings.DefaultShowcaseAdded && game.OwnGrid != null && !Cinematics().Any(x => x.Name.StartsWith("Showcase", StringComparison.OrdinalIgnoreCase)))
                MakeShowcase(game.OwnGrid, "Showcase");
            p.CinePlaying = cinePlay != null;
            foreach (var cineListed in Cinematics()) p.Cinematics.Add(cineListed.Name);
            var cineShown = CineCurrent;
            if (cineShown != null)
            {
                p.CineCurrent = cineShown.Name;
                p.CineLoop = cineShown.Loop;
                p.CineFollow = cineShown.FollowPicked;
                p.CineLength = cineShown.TimelineLength;
                p.CineUndos = cineUndo.TryGetValue(cineShown, out var cineU) ? cineU.Count : 0;
                p.CineRedos = cineRedo.TryGetValue(cineShown, out var cineR) ? cineR.Count : 0;
                p.CineShotPlaying = cinePlay != null && cinePlay.Name == cineShown.Name ? cineShot : -1;
                if (p.CineShotPlaying >= 0 && p.CineShotPlaying < cineShown.Shots.Count)
                    p.CineTime = cineShown.Shots.Take(p.CineShotPlaying).Sum(x => x.Seconds)
                                 + Math.Clamp(CineNow - cineShotAt, 0, CinePlayLength(cineShown, p.CineShotPlaying));
                p.CineLanes = Math.Max(1, cineShown.Lanes);
                foreach (var cineFxRow in cineShown.Effects)
                    p.CineEffects.Add((cineFxRow.Type, cineFxRow.Start, cineFxRow.Seconds, cineFxRow.From, cineFxRow.To, cineFxRow.Lane));
                foreach (var cineRow in cineShown.Shots)
                    p.CineShots.Add(new HudModel.CineShotRow
                    {
                        Type = cineRow.Type, Target = cineRow.Target, Seconds = cineRow.Seconds, Rotate = cineRow.RotateSpeed,
                        Track = cineRow.Track, Relative = cineRow.Relative,
                        HasShipView = cineRow.Type == "Path" ? cineRow.Keys.Count > 0 && cineRow.KeysShip.Count >= cineRow.Keys.Count && cineRow.KeysShip.All(x => x != null) : cineRow.ViewShip != null,
                        Keys = cineRow.Keys.Count, Blend = cineRow.Blend,
                        Ready = cineRow.Type == "Orbit" ? cineRow.ViewShip != null && cineRow.Dist > 0
                              : cineRow.Type == "Chase" ? cineRow.ViewShip != null
                              : cineRow.Type == "Path" ? cineRow.Keys.Count >= 2
                              : cineRow.View != null || cineRow.ViewShip != null,
                        ReadyEnd = cineRow.View2 != null || cineRow.View2Ship != null,
                    });
            }
            p.RpiVersion = rpiVersion;
            foreach (var rpiClass in settings.RpiClasses)
                p.RpiClasses.Add((rpiClass.Name, rpiClass.Budget, rpiClass.MinWeight, rpiClass.Values.Count(x => x.Value != 0)));
            var rpiInUse = RpiClassInUse;
            p.RpiCurrent = rpiInUse?.Name;
            if (rpiLastShip != null)
            {
                p.RpiShipName = rpiLastShip.Name ?? "Grid";
                p.RpiShipMass = rpiLastShip.Mass;
                p.RpiShipCounts = rpiLastShip.BlockCounts;
            }
            if (rpiInUse != null)
            {
                p.RpiValues = new Dictionary<string, double>(rpiInUse.Values);
                p.RpiBudget = rpiInUse.Budget;
                p.RpiMinWeight = rpiInUse.MinWeight;
                p.RpiLimits = new Dictionary<string, int>(rpiInUse.Limits);
                foreach (var rpiStep in rpiInUse.Steps) p.RpiSteps.Add((rpiStep.Weight, rpiStep.Budget));
            }
            // (The class files folder is looked at every few seconds, not every time the menu updates.)
            if (rpiFilesCache == null || Environment.TickCount64 - rpiFilesAt > 3000)
            {
                rpiFilesAt = Environment.TickCount64;
                var rpiFound = new List<(string, string)>();
                try
                {
                    if (Directory.Exists(RpiFolder))
                        foreach (string rpiFile in Directory.GetFiles(RpiFolder, "*" + RpiExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            rpiFound.Add((Path.GetFileName(rpiFile).Replace(RpiExtension, ""), rpiFile));
                }
                catch (Exception rpiError) { PluginLog.Info("RPI classes folder: " + rpiError.Message); }
                if (rpiFilesCache != null && !rpiFilesCache.SequenceEqual(rpiFound)) rpiVersion++;
                rpiFilesCache = rpiFound;
                p.RpiVersion = rpiVersion;
            }
            p.RpiFiles.AddRange(rpiFilesCache);
            p.RpiFolder = RpiFolder;
            p.Presets.AddRange(LoadPresets().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            p.CurrentPreset = settings.CurrentPreset;
            p.PresetFolder = PresetFolder;
            // The preset folder is looked at every few seconds, not every time the menu updates.
            if (presetFilesCache == null || Environment.TickCount64 - presetFilesAt > 3000)
            {
                presetFilesAt = Environment.TickCount64;
                presetFilesCache = new List<(string, string)>();
                try
                {
                    if (Directory.Exists(PresetFolder))
                        foreach (var f in Directory.GetFiles(PresetFolder, "*" + PresetExtension).OrderBy(x => x))
                        {
                            var pf = ReadPresetFile(f);
                            if (pf != null) presetFilesCache.Add((pf.Name, f));
                        }
                }
                catch (Exception e) { PluginLog.Error("Couldn't list the preset files", e); }
            }
            p.PresetFiles.AddRange(presetFilesCache);
            var defaults = new GateSettings();
            for (int i = 0; i < SettingDefs.All.Length; i++)
            {
                var d = SettingDefs.All[i];
                bool enabled = true;
                if (d.DependsOn != null)
                {
                    var dep = SettingDefs.All.FirstOrDefault(x => x.Name == d.DependsOn);
                    if (dep != null) enabled = (dep.Get(settings) >= 0.5) != d.DependsOnOff;
                }
                double value = d.Clean(d.Get(settings));
                p.Settings.Add(new HudModel.SettingRow
                {
                    Index = i, Section = d.Section, Name = d.Name, Description = d.Description, Format = d.Format, Choices = d.Choices,
                    Toggle = d.Toggle, Min = d.Min, Max = d.Max, Step = d.Step, Value = value,
                    Enabled = enabled, DependsOn = d.DependsOn, DependsOnOff = d.DependsOnOff,
                    Default = d.Clean(d.Get(defaults)),
                    Changed = Math.Abs(value - d.Clean(d.Get(defaults))) > 1e-6,
                });
            }
            return p;
        }

        // ---------------------------------------------------------------- settings presets

        /// <summary>Presets file: preset name -> (setting name -> value), next to RaceDisplay.json.</summary>
        private string PresetsPath => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "RaceDisplayPresets.json");

        private Dictionary<string, Dictionary<string, double>> presetsCache;
        private List<(string Name, string Path)> presetFilesCache;
        private long presetFilesAt;

        /// <summary>The presets (read from disk once, then kept; saving updates both). Callers may change the copy.</summary>
        /// <summary>
        /// The two presets every player starts with. Racing: for race days that are filmed, clean and easy to follow
        /// (lap time, speed, height, keys, roll and pitch, laps and splits; no best times, gaps, ghost or racing line).
        /// Practice: everything on, to work on your line and your ship. Only the settings named change.
        /// </summary>
        private static Dictionary<string, Dictionary<string, double>> DefaultPresets() => new Dictionary<string, Dictionary<string, double>>
        {
            ["Racing"] = new Dictionary<string, double>
            {
                ["Gap under the timer"] = 0, ["Best lap / best race boxes"] = 0, ["Race stats"] = 0,
                ["Lap list"] = 1, ["Splits panel"] = 1, ["Lap penalties as icons"] = 1,
                ["Speedometer"] = 1, ["Height above ground"] = 1, ["Flight keys"] = 1, ["Roll indicator"] = 1, ["Pitch indicator"] = 1,
                ["Speed vector marker"] = 0, ["Key reminders"] = 0,
                ["Ghost"] = 0, ["Racing line"] = 0, ["Braking markers"] = 0,
                ["Checkpoint markers"] = 1, ["Checkpoint labels and arrow"] = 1, ["Minimap"] = 1,
                ["Results against this ship"] = 0,
                ["Reset race teleports to the start"] = 0,
            },
            ["Practice"] = new Dictionary<string, double>
            {
                ["Gap under the timer"] = 1, ["Best lap / best race boxes"] = 1, ["Race stats"] = 1,
                ["Lap list"] = 1, ["Splits panel"] = 1, ["Lap penalties as icons"] = 1,
                ["Speedometer"] = 1, ["Height above ground"] = 1, ["Flight keys"] = 1, ["Roll indicator"] = 1, ["Pitch indicator"] = 1,
                ["Speed vector marker"] = 1, ["Key reminders"] = 1,
                ["Ghost"] = 1, ["Racing line"] = 1, ["Braking markers"] = 1,
                ["Checkpoint markers"] = 1, ["Checkpoint labels and arrow"] = 1, ["Minimap"] = 1,
                ["Sector times after the finish"] = 1, ["Results graphs"] = 2,
                ["Reset race teleports to the start"] = 1,
            },
        };

        private Dictionary<string, Dictionary<string, double>> LoadPresets()
        {
            if (presetsCache == null)
            {
                presetsCache = ReadPresets();
                // The built-in presets kept as they came: settings added to them later are filled in (a preset saved
                // over them has every setting already, so nothing changes there).
                bool topUp = false;
                foreach (var kv in DefaultPresets())
                    if (presetsCache.TryGetValue(kv.Key, out var have) && have != null)
                        foreach (var v in kv.Value)
                            if (!have.ContainsKey(v.Key)) { have[v.Key] = v.Value; topUp = true; }
                if (topUp) try { SavePresets(new Dictionary<string, Dictionary<string, double>>(presetsCache)); } catch { }
                if (!settings.DefaultPresetsAdded)
                {
                    // Added once: the player can change or delete them like their own.
                    foreach (var kv in DefaultPresets())
                        if (!presetsCache.Keys.Any(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase))) presetsCache[kv.Key] = kv.Value;
                    settings.DefaultPresetsAdded = true;
                    try { SavePresets(new Dictionary<string, Dictionary<string, double>>(presetsCache)); } catch { }
                    settings.Save(settingsPath);
                }
            }
            return new Dictionary<string, Dictionary<string, double>>(presetsCache);
        }

        private Dictionary<string, Dictionary<string, double>> ReadPresets()
        {
            try
            {
                if (File.Exists(PresetsPath))
                    return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(PresetsPath))
                        ?? new Dictionary<string, Dictionary<string, double>>();
            }
            catch (Exception e) { PluginLog.Error("Couldn't read the settings presets", e); }
            return new Dictionary<string, Dictionary<string, double>>();
        }

        /// <summary>Exported presets (one per file, to share): %AppData%\...\RaceDisplay\Presets\name.racepreset.json</summary>
        private string PresetFolder => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "Presets");
        private const string PresetExtension = ".racepreset.json";

        private sealed class PresetFile
        {
            public string Name { get; set; }
            public Dictionary<string, double> Values { get; set; }
        }

        private static PresetFile ReadPresetFile(string path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return null;
                var f = System.Text.Json.JsonSerializer.Deserialize<PresetFile>(SafeRead.Text(path));
                if (f?.Values == null || f.Values.Count == 0) return null;
                if (string.IsNullOrWhiteSpace(f.Name))
                    f.Name = Path.GetFileName(path).Replace(PresetExtension, "");
                f.Name = f.Name.Trim();
                return f;
            }
            catch { return null; }
        }

        /// <summary>The file is inside that folder (so a file action can't reach anywhere else).</summary>
        private static bool InFolder(string file, string folder)
        {
            try
            {
                string f = Path.GetFullPath(file), d = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return f.StartsWith(d, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SafeFileName(string name)
        {
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name.Trim();
        }

        private void SavePresets(Dictionary<string, Dictionary<string, double>> presets)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PresetsPath) ?? ".");
                File.WriteAllText(PresetsPath, System.Text.Json.JsonSerializer.Serialize(presets, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                presetsCache = new Dictionary<string, Dictionary<string, double>>(presets);
            }
            catch (Exception e) { PluginLog.Error("Couldn't save the settings presets", e); }
        }

        private void FlashTimer(HudColor colour)
        {
            flashColour = colour;
            flashUntil = game.TimeSeconds + 1.2;
            GlowEdge(colour, 1.4);
        }

        // A soft glow round the edge of the screen for a moment (a checkpoint passed, a penalty).
        private HudColor? edgeGlowColour;
        private double edgeGlowUntil;
        private void GlowEdge(HudColor colour, double seconds)
        {
            edgeGlowColour = colour;
            edgeGlowUntil = game.TimeSeconds + seconds;
        }

        // ---------------------------------------------------------------- course records

        /// <summary>A course = this world, its checkpoint count and where the start gate is (to the nearest 10 m).</summary>
        private string CourseKey()
        {
            if (timer.Gate == null) return null;
            var c = timer.Gate.Center;
            string key = $"{game.WorldId}|{timer.Checkpoints.Count}|{Math.Round(c.X / 10)},{Math.Round(c.Y / 10)},{Math.Round(c.Z / 10)}";
            if (timer.FinishGate != null)
            {
                var f = timer.FinishGate.Center;
                key += $"|F{Math.Round(f.X / 10)},{Math.Round(f.Y / 10)},{Math.Round(f.Z / 10)}";
            }
            if (CourseReversed) key += "|R";   // the other way round has its own records
            return key;
        }

        private GateSettings.CourseRecord CourseRecord(bool create)
        {
            string key = CourseKey();
            if (key == null) return null;
            if (settings.Courses.TryGetValue(key, out var r)) return r;
            if (!create) return null;
            r = new GateSettings.CourseRecord();
            settings.Courses[key] = r;
            return r;
        }

        // ---------------------------------------------------------------- countdown

        private void UpdateCountdown(Vec3D? pos, Vec3D? vel, double now)
        {
            bool canStart = timer.Gate != null && (timer.State == TimerState.Armed || timer.State == TimerState.Finished);
            if (!settings.CountdownEnabled || settings.CountdownHoldSeconds <= 0 || !canStart || !pos.HasValue || !vel.HasValue)
            {
                stillSince = null;
                countdownStart = null;
                return;
            }

            double speed = vel.Value.Length;
            bool nearGate = timer.Gate.DistanceTo(pos.Value) <= settings.CountdownDistance;
            if (!nearGate)
            {
                countdownAllowed = true;
                stillSince = null;
                countdownStart = null;
                return;
            }

            if (countdownStart.HasValue)
            {
                int beat = (int)Math.Floor(now - countdownStart.Value); // 0, 1, 2 = "3", "2", "1"
                if (beat != lastCountdownBeep && beat < 3) { lastCountdownBeep = beat; Sound(settings.SoundCountdown); }
                if (speed > 2.0)
                {
                    countdownStart = null;
                    stillSince = null;
                    RaceNotify("Countdown cancelled - moved too early", HudColor.Red);
                }
                else if (now - countdownStart.Value >= 3.0)
                {
                    // GO: launch! The timer itself starts when you fly through the gate.
                    Sound(settings.SoundGo);
                    Sound(settings.SoundGo, 120);
                    lastCountdownBeep = -1;
                    countdownStart = null;
                    countdownAllowed = false;
                    goUntil = now + 2.0;
                    lastGoShown = now;
                    timer.PrimeStart(pos);
                }
                return;
            }

            if (!countdownAllowed) return;
            if (speed < 1.0)
            {
                stillSince ??= now;
                if (now - stillSince.Value >= settings.CountdownHoldSeconds)
                {
                    lastCountdownBeep = -1;
                    countdownStart = now;
                    stillSince = null;
                }
            }
            else stillSince = null;
        }

        // ---- Undo / redo of course edits (gates and checkpoints), for this world while the game runs.
        private sealed class CourseSnap
        {
            public GateSettings.SavedGate Gate, Finish;
            public List<GateSettings.SavedGate> Checkpoints;
            public bool Reversed;
            public string Sig;
        }
        private readonly List<CourseSnap> undoStack = new List<CourseSnap>(), redoStack = new List<CourseSnap>();

        private CourseSnap SnapCourse()
        {
            string world = game.WorldId;
            var snap = new CourseSnap
            {
                Gate = timer.Gate == null ? null : GateSettings.SavedGate.From(timer.Gate, world),
                Finish = timer.FinishGate == null ? null : GateSettings.SavedGate.From(timer.FinishGate, world),
                Checkpoints = timer.Checkpoints.Select(c => GateSettings.SavedGate.From(c, world)).ToList(),
                Reversed = CourseReversed,
            };
            var sb = new System.Text.StringBuilder();
            void Add(GateSettings.SavedGate g)
            {
                if (g == null) { sb.Append("-|"); return; }
                sb.Append(Math.Round(g.Center[0], 2)).Append(',').Append(Math.Round(g.Center[1], 2)).Append(',').Append(Math.Round(g.Center[2], 2))
                  .Append(':').Append(Math.Round(g.Width, 2)).Append('x').Append(Math.Round(g.Height, 2)).Append('x').Append(Math.Round(g.Depth, 2))
                  .Append('/').Append(Math.Round(g.Forward[0], 3)).Append(',').Append(Math.Round(g.Forward[1], 3)).Append(',').Append(Math.Round(g.Forward[2], 3)).Append('|');
            }
            Add(snap.Gate); Add(snap.Finish);
            foreach (var c in snap.Checkpoints) Add(c);
            sb.Append(snap.Reversed ? "R" : "");
            snap.Sig = sb.ToString();
            return snap;
        }

        private bool CourseReversed => settings.ReversedWorlds.Contains(game.WorldId);

        private void SetCourseReversed(bool on)
        {
            if (on == CourseReversed) return;
            if (on) settings.ReversedWorlds.Add(game.WorldId); else settings.ReversedWorlds.Remove(game.WorldId);
            settings.Save(settingsPath);
            results = shipResults = null;
            coursesVersion++;
            recordsVersion++;
        }

        /// <summary>
        /// Flies the course the other way round: checkpoints in the opposite order, each gate turned round, and on a
        /// point-to-point course the start and finish swap. The reversed course keeps its own best times (reversing
        /// again brings the original's back).
        /// </summary>
        private void ReverseCourse(Vec3D? position)
        {
            if (timer.Gate == null) { Notify("Set up a course first", HudColor.Yellow); return; }
            TimingGate Flip(TimingGate g) => g.Turned();
            var start = timer.Gate;
            var finish = timer.FinishGate;
            var cps = timer.Checkpoints.Reverse().Select(Flip).ToList();
            if (finish != null)
            {
                SetGate(Flip(finish), position);
                SetFinishGate(Flip(start), position);
            }
            else SetGate(Flip(start), position);
            SaveCheckpoints(cps.Select(c => GateSettings.SavedGate.From(c, game.WorldId)).ToList(), position);
            SetCourseReversed(!CourseReversed);
            // Named sectors run the other way too: the same stretch, counted from the other end.
            int lastPoint = CourseRoute().Count - 1;
            foreach (var sec in settings.CourseSectors) { int f = sec.From; sec.From = lastPoint - sec.To; sec.To = lastPoint - f; }
            settings.CourseStartSpot = null;   // the start is at the other end now
            timer.Reset();
            countdownStart = null;
            Sound(settings.SoundCheckpoint);
            Notify(finish != null
                ? $"Course reversed: start and finish swapped, {cps.Count} checkpoints in the opposite order"
                : $"Course reversed: {cps.Count} checkpoints in the opposite order (fly the start / finish gate the other way)", HudColor.Cyan);
        }

        private void UndoRedo(bool undo, Vec3D? position)
        {
            var from = undo ? undoStack : redoStack;
            var to = undo ? redoStack : undoStack;
            if (from.Count == 0) { Notify(undo ? "Nothing to undo" : "Nothing to redo", HudColor.Yellow); return; }
            var target = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            to.Add(SnapCourse());
            SetGate(target.Gate?.ToGate(), position);
            SetFinishGate(target.Finish?.ToGate(), position);
            SaveCheckpoints(target.Checkpoints.Select(c => GateSettings.SavedGate.From(c.ToGate(), game.WorldId)).ToList(), position);
            SetCourseReversed(target.Reversed);
            Sound(settings.SoundCheckpoint);
            Notify(undo ? $"Undone ({undoStack.Count} more to undo)" : $"Redone ({redoStack.Count} more to redo)", HudColor.Cyan);
        }

        private void OnWorldLoaded(Vec3D? position)
        {
            undoStack.Clear();
            redoStack.Clear();
            loadedWorld = game.WorldId;
            worldLoadedAt = game.TimeSeconds;
            // Courses live in each world's save (RaceDisplayCourses.json), not in RaceDisplay.json: start with none
            // and load this world's own from its save folder (CheckWorldCourses), so nothing carries over from
            // another world, even one with the same name.
            settings.Gate = null;
            settings.FinishGate = null;
            settings.Checkpoints.Clear();
            settings.SavedCourses.Clear();
            settings.CurrentCourseName.Clear();
            settings.ReversedWorlds.Clear();
            settings.CourseSectors.Clear();
            settings.CourseStartSpot = null;
            settings.CourseGridGlow = false;
            timer.SetGate(null, position);
            timer.SetFinishGate(null, position);
            timer.SetCheckpoints(Enumerable.Empty<TimingGate>(), position);
            worldCoursesChecked = false;
            worldCoursesTriedAt = double.NegativeInfinity;
            lastWorldSaves = game.WorldSaveCount;
            worldWriteDueAt = 0;
            writtenJson = writtenFolder = null;
            coursesVersion++;
            recordsVersion++;
        }

        private bool worldCoursesChecked;
        private double worldCoursesTriedAt;
        private object loadedHandle;

        /// <summary>The world was saved under a new name: the course goes with it (it's written into the new save).</summary>
        private void OnWorldRenamed(string from, string to)
        {
            loadedWorld = to;
            if (settings.Gate != null) settings.Gate.World = to;
            if (settings.FinishGate != null) settings.FinishGate.World = to;
            foreach (var c in settings.Checkpoints) if (c.World == from) c.World = to;
            foreach (var c in settings.SavedCourses) if (c.World == from) c.World = to;
            if (settings.CurrentCourseName.TryGetValue(from, out var n)) { settings.CurrentCourseName.Remove(from); settings.CurrentCourseName[to] = n; }
            if (settings.ReversedWorlds.Remove(from)) settings.ReversedWorlds.Add(to);
            writtenJson = writtenFolder = null;
            coursesVersion++;
            recordsVersion++;
            PluginLog.Info($"World renamed '{from}' -> '{to}' (saved as): the race course goes with it");
        }

        // Writing the course into the world's save when the game saves.
        private int lastWorldSaves;
        private long worldWriteDueAt;          // Environment.TickCount64 (real time: the game is paused while saving from the menu)
        private long keepWrittenUntil;
        private long worldFileCheckedAt;
        private string writtenJson, writtenFolder;

        /// <summary>
        /// The game saved the world: a moment later (when it has finished writing the save folder) write the course and
        /// this world's saved courses into it. For a minute after that, put the file back if the save removed it.
        /// </summary>
        private void KeepWorldCourseFile()
        {
            long now = Environment.TickCount64;
            int saves = game.WorldSaveCount;
            if (saves != lastWorldSaves)
            {
                lastWorldSaves = saves;
                worldWriteDueAt = now + 2000;   // after the last save signal
            }
            if (worldWriteDueAt != 0 && now >= worldWriteDueAt)
            {
                worldWriteDueAt = 0;
                if (worldCoursesChecked) lock (worldFileLock) SaveCoursesToWorld(true);   // not before this world's own courses were read
                keepWrittenUntil = now + 60000;
            }
            if (writtenJson == null || now > keepWrittenUntil || now - worldFileCheckedAt < 2000) return;
            worldFileCheckedAt = now;
            try
            {
                string path = Path.Combine(writtenFolder, GateSettings.WorldCourses.FileName);
                if (!File.Exists(path) || File.ReadAllText(path) != writtenJson)
                {
                    File.WriteAllText(path, writtenJson);
                    PluginLog.Info("World save: wrote the race course again (the game's save had replaced it)");
                }
            }
            catch (Exception e) { PluginLog.Info("World save: couldn't check the course file: " + e.Message); }
        }

        /// <summary>
        /// A little after the world loads: set up the course saved in this world (RaceDisplayCourses.json in its save
        /// folder) and its saved courses.
        /// </summary>
        private void CheckWorldCourses()
        {
            double now = game.TimeSeconds;
            if (worldCoursesChecked || now - worldLoadedAt < 3 || now - worldCoursesTriedAt < 5) return;
            worldCoursesTriedAt = now;
            string folder = game.WorldSaveFolder;
            bool haveCourse = timer.Gate != null || timer.Checkpoints.Count > 0;   // made one already while we looked
            if (folder == null)
            {
                // A new world that hasn't been saved yet has no folder (and no courses of its own). Started from the
                // Workshop: its download's course can still be set up now (it goes into the save when the world is
                // saved). Otherwise stop looking after a while.
                if (game.WorkshopStartIds == null && now - worldLoadedAt <= 120) return;
                if (game.WorkshopStartIds == null) { worldCoursesChecked = true; return; }
            }
            worldCoursesChecked = true;
            var wc = folder == null ? null : GateSettings.WorldCourses.Load(folder);
            bool fromWorkshop = false;
            if (wc == null && !haveCourse)
            {
                // No course in this world, and it was just started from a Workshop download: that download's course.
                wc = FindWorkshopCourse(folder);
                fromWorkshop = wc != null;
            }
            game.ClearWorkshopStart();   // only ever for the first world loaded after starting it
            if (wc != null)
            {
                MergeWorldCourses(wc);
                if (!haveCourse && wc.Current?.Gate != null)
                {
                    ApplyCourse(wc.Current);
                    RaceNotify(fromWorkshop
                        ? $"Race course '{wc.Current.Name ?? "course"}' loaded from this world's Workshop download ({timer.Checkpoints.Count} checkpoints)"
                        : $"Race course '{wc.Current.Name ?? "course"}' loaded from this world ({timer.Checkpoints.Count} checkpoints)", HudColor.Cyan);
                }
                // Came from the Workshop download: keep it in this world's own save from now on.
                if (fromWorkshop) SaveCoursesToWorld(true);
            }
        }

        /// <summary>
        /// A world made from a Workshop download has no RaceDisplayCourses.json (the game doesn't copy it into the new
        /// save), but the download does. Look through the downloads for the course whose gates and checkpoints sit on
        /// this world's grids.
        /// </summary>
        private GateSettings.WorldCourses FindWorkshopCourse(string worldFolder)
        {
            try
            {
                // Only for a world just started from the Workshop screen, and only that download: the Workshop item
                // folder whose id is the one the game started. Any other world never looks at Workshop courses.
                string[] ids = game.WorkshopStartIds;
                if (ids == null || ids.Length == 0) return null;
                var files = WorkshopCourses.FindFiles();
                foreach (var f in files)
                {
                    string itemId = WorkshopCourses.ItemId(f);
                    if (itemId == null || !ids.Any(x => x.IndexOf(itemId, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var wc = GateSettings.WorldCourses.Load(Path.GetDirectoryName(f));
                    if (wc?.Current == null && (wc?.Courses == null || wc.Courses.Count == 0)) continue;
                    PluginLog.Info($"Workshop courses: this world was started from Workshop item {itemId}: using {f}");
                    return wc;
                }
                PluginLog.Info($"Workshop courses: started from the Workshop ({string.Join(" | ", ids)}), but that download has no race course");
                return null;
            }
            catch (Exception e) { PluginLog.Error("Workshop courses: search failed", e); return null; }
        }

        /// <summary>Adds the world file's named courses to your saved courses (keeps yours when the name is taken).</summary>
        private int MergeWorldCourses(GateSettings.WorldCourses wc)
        {
            string world = game.WorldId;
            int added = 0;
            foreach (var c in wc.Courses.Where(x => x?.Gate != null && !string.IsNullOrWhiteSpace(x.Name)))
            {
                if (settings.SavedCourses.Any(x => x.World == world && string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                settings.SavedCourses.Add(new GateSettings.SavedCourse
                {
                    Name = c.Name, World = world,
                    Gate = Copy(c.Gate, world),
                    Finish = c.Finish == null ? null : Copy(c.Finish, world),
                    Checkpoints = (c.Checkpoints ?? new List<GateSettings.SavedGate>()).Select(x => Copy(x, world)).ToList(),
                    Reversed = c.Reversed,
                    Sectors = c.Sectors ?? new List<GateSettings.NamedSector>(),
                    StartSpot = c.StartSpot == null ? null : CopySpot(c.StartSpot, world),
                    GridGlow = c.GridGlow,
                });
                added++;
            }
            if (added > 0) coursesVersion++;
            return added;
        }

        private void ApplyCourse(GateSettings.SavedCourse course)
        {
            string world = game.WorldId;
            SetGate(course.Gate?.ToGate(), playerPosition);
            SetFinishGate(course.Finish?.ToGate(), playerPosition);
            SaveCheckpoints((course.Checkpoints ?? new List<GateSettings.SavedGate>()).Select(x => Copy(x, world)).ToList(), playerPosition);
            SetCourseReversed(course.Reversed);
            settings.CourseSectors = (course.Sectors ?? new List<GateSettings.NamedSector>()).Select(x => new GateSettings.NamedSector { Name = x.Name, From = x.From, To = x.To }).ToList();
            settings.CourseStartSpot = course.StartSpot == null ? null : CopySpot(course.StartSpot, world);
            settings.CourseGridGlow = course.GridGlow;
            if (!string.IsNullOrWhiteSpace(course.Name)) settings.CurrentCourseName[world] = course.Name;
            else settings.CurrentCourseName.Remove(world);
            settings.Save(settingsPath);
            coursesVersion++;
        }

        /// <summary>Writes the current course and this world's saved courses into the world's save folder.</summary>
        private void SaveCoursesToWorld(bool quiet)
        {
            string folder = game.WorldSaveFolder;
            if (folder == null)
            {
                if (!quiet) controlsNote = "Couldn't find this world's save folder (save the world once, then try again; the log lists what was checked)";
                return;
            }
            string world = game.WorldId;
            var wc = new GateSettings.WorldCourses
            {
                Current = timer.Gate == null ? null : new GateSettings.SavedCourse
                {
                    Name = CurrentCourseName(), World = world,
                    Gate = GateSettings.SavedGate.From(timer.Gate, world),
                    Finish = timer.FinishGate == null ? null : GateSettings.SavedGate.From(timer.FinishGate, world),
                    Checkpoints = WorldCheckpoints().Select(x => Copy(x, world)).ToList(),
                    Reversed = CourseReversed,
                    Sectors = settings.CourseSectors.Select(x => new GateSettings.NamedSector { Name = x.Name, From = x.From, To = x.To }).ToList(),
                    StartSpot = settings.CourseStartSpot == null ? null : CopySpot(settings.CourseStartSpot, world),
                    GridGlow = settings.CourseGridGlow,
                },
                Courses = settings.SavedCourses.Where(x => x.World == world).ToList(),
            };
            try
            {
                string path = Path.Combine(folder, GateSettings.WorldCourses.FileName);
                // Nothing to keep and nothing there before: don't create an empty file.
                if (wc.Current == null && wc.Courses.Count == 0 && !File.Exists(path)) return;
                string json = wc.ToJson();
                File.WriteAllText(path, json);
                writtenJson = json; writtenFolder = folder;
                keepWrittenUntil = Environment.TickCount64 + 60000;
                coursesVersion++;
                PluginLog.Info($"World save: race course written to {path}");
                if (!quiet) controlsNote = $"Course saved into the world ({GateSettings.WorldCourses.FileName}). It's also written every time the game saves the world.";
            }
            catch (Exception e) { if (!quiet) controlsNote = "Couldn't write to the world folder: " + e.Message; }
        }

        private List<GateSettings.SavedGate> WorldCheckpoints() =>
            settings.Checkpoints.Where(c => c.World == game.WorldId).ToList();

        private void SaveCheckpoints(List<GateSettings.SavedGate> forThisWorld, Vec3D? position)
        {
            settings.Checkpoints.RemoveAll(c => c.World == game.WorldId);
            settings.Checkpoints.AddRange(forThisWorld);
            settings.Save(settingsPath);
            timer.SetCheckpoints(forThisWorld.Select(c => c.ToGate()), position);
            coursesVersion++;
            recordsVersion++;
        }

        private void HandleKeys(bool hasPose, Vec3D pos, Vec3D fwd, Vec3D up, Vec3D? position)
        {
            // Placing / removing gates and checkpoints is done in course edit mode (aim and click, tool wheel).
            // The resize keys only work there too.
            if (editMode && settings.EditAirPlace && (keys.WasPressed(GateKey.AirFurther) || keys.WasPressed(GateKey.AirNearer)))
            {
                // (From where it's really being placed: a big gate is kept further off than the setting.)
                double ahead = AirPlaceDistance();
                double step = ahead >= 100 ? 20 : ahead >= 40 ? 10 : 5;
                ahead = keys.WasPressed(GateKey.AirFurther) ? Math.Floor(ahead / step) * step + step : Math.Ceiling(ahead / step) * step - step;
                settings.AirCheckpointDistance = Math.Clamp(ahead, 5, 500);
                settings.Save(settingsPath);
                double placed = AirPlaceDistance();
                Notify(placed > settings.AirCheckpointDistance + 0.5
                    ? $"Air gate: {placed:0} m ahead (no closer at this size, so the whole gate stays in view)"
                    : $"Air gate: {placed:0} m ahead", HudColor.Cyan);
            }
            if (editMode && settings.EditAirPlace && keys.WasPressed(GateKey.AirShape))
            {
                settings.AirGateRound = !settings.AirGateRound;
                settings.Save(settingsPath);
                Notify(settings.AirGateRound ? "Air gates: rings" : "Air gates: squares", HudColor.Cyan);
            }
            if (editMode && settings.EditAirPlace && (keys.WasPressed(GateKey.AirTaller) || keys.WasPressed(GateKey.AirShorter)))
            {
                // (From the same as the width; back at the width it's "the same" again.)
                double tallNow = settings.AirGateHeight > 0 ? settings.AirGateHeight : settings.AirCheckpointSize;
                tallNow = AirSizeStep(tallNow, keys.WasPressed(GateKey.AirTaller));
                settings.AirGateHeight = Math.Abs(tallNow - settings.AirCheckpointSize) < 0.5 ? 0 : tallNow;
                settings.Save(settingsPath);
                Notify(settings.AirGateHeight > 0 ? $"Air gate: {settings.AirCheckpointSize:0} m wide, {settings.AirGateHeight:0} m tall" : $"Air gate height: the same as its width ({settings.AirCheckpointSize:0} m)", HudColor.Cyan);
            }
            if ((keys.WasPressed(GateKey.Grow) || keys.WasPressed(GateKey.Shrink)) && !editMode)
            {
                Notify($"Gate size is changed in course edit mode ({settings.GetBinding(GateKey.EditMode)})", HudColor.Yellow);
                return;
            }
            if (editMode && settings.EditAirPlace && (keys.WasPressed(GateKey.Grow) || keys.WasPressed(GateKey.Shrink)))
            {
                bool bigger = keys.WasPressed(GateKey.Grow);
                settings.AirCheckpointSize = AirSizeStep(settings.AirCheckpointSize, bigger);
                if (settings.AirGateHeight > 0) settings.AirGateHeight = AirSizeStep(settings.AirGateHeight, bigger);   // (keeps its shape, roughly)
                settings.Save(settingsPath);
                Notify(settings.AirGateHeight > 0 ? $"Air gate size: {settings.AirCheckpointSize:0} m wide, {settings.AirGateHeight:0} m tall" : $"Air gate size: {settings.AirCheckpointSize:0} m", HudColor.Cyan);
            }
            else if (keys.WasPressed(GateKey.Grow) && timer.Gate != null)
            {
                Resize(GrowFactor, position);
            }
            else if (keys.WasPressed(GateKey.Shrink) && timer.Gate != null)
            {
                Resize(1 / GrowFactor, position);
            }
            else if (keys.WasPressed(GateKey.ResetTimer) && timer.Gate != null && !editMode)
            {
                RememberBurnedRace();
                timer.Reset();
                results = shipResults = null;
                gapText = null;
                countdownStart = null;
                countdownAllowed = true;
                if (!settings.ResetTeleports) Notify("Race reset");
                else if (TeleportToStart()) Notify("Race reset: back to the start");
            }
            else if (keys.WasPressed(GateKey.FlipShip) && !editMode)
            {
                FlipShipUpright();
            }
            else if (keys.WasPressed(GateKey.SetStartSpot))
            {
                string note = SetStartSpotHere();
                Notify(settings.ResetTeleports || !note.StartsWith("Start spot set") ? note
                    : note + ". Turn on Settings > Race > Reset race teleports to the start to use it", note.StartsWith("Start spot set") ? HudColor.Cyan : HudColor.Yellow);
            }
            else if (keys.WasPressed(GateKey.ToggleMarkers))
            {
                settings.ShowMarkers = !settings.ShowMarkers;
                settings.Save(settingsPath);
                Notify(settings.ShowMarkers ? "Markers on" : "Markers off");
            }
            else if (keys.WasPressed(GateKey.LapCount))
            {
                // Race setup: laps, practice laps and which lap the ghost / racing line follow.
                if (!game.OpenRaceMenu())
                    Notify("Race setup: press G > Racing Display > Race (after that, this key opens it)");
            }
        }

        /// <summary>
        /// Box for a grid, in the grid's own orientation. Whole grid: padded on every side.
        /// Ring opening: padded only along the way you fly through, so the box stays inside the ring.
        /// </summary>
        private TimingGate GateFromGrid(GridBox g)
        {
            double pad = settings.GridPadding * 2;
            double w = g.Width, h = g.Height, d = g.Depth;
            if (!g.IsOpening) { w += pad; h += pad; d += pad; }
            else if (g.ThroughAxis == 0) w += pad;
            else if (g.ThroughAxis == 1) h += pad;
            else d += pad;
            return new TimingGate(g.Center, g.Forward, g.Up, w, h, d);
        }

        private void Resize(double factor, Vec3D? position)
        {
            var resized = timer.Gate.Resized(factor);
            settings.Width = resized.Width;
            settings.Height = resized.Height;
            settings.Depth = resized.Depth;
            SetGate(resized, position);
            Notify($"Gate {resized.Width:F0} x {resized.Height:F0} x {resized.Depth:F0} m");
        }

        /// <summary>Sets (or with null removes) the separate finish gate. Without one the start gate is also the finish.</summary>
        private void SetFinishGate(TimingGate gate, Vec3D? position)
        {
            timer.SetFinishGate(gate, position);
            results = shipResults = null;
            gapText = null;
            settings.FinishGate = gate == null ? null : GateSettings.SavedGate.From(gate, game.WorldId);
            settings.Save(settingsPath);
            coursesVersion++;
            recordsVersion++;
        }

        private void SetGate(TimingGate gate, Vec3D? position)
        {
            timer.SetGate(gate, position);
            results = shipResults = null;
            gapText = null;
            countdownAllowed = true;
            settings.Gate = gate == null ? null : GateSettings.SavedGate.From(gate, game.WorldId);
            settings.Save(settingsPath);
            coursesVersion++;
            recordsVersion++;
        }

        private void OnTriggered(LapEventKind kind, double seconds)
        {
            switch (kind)
            {
                case LapEventKind.PracticeStarted:
                    currentShip = game.GetShipShape(250);
                    ResetRaceStats();
                    results = shipResults = null;
                    gapText = null;
                    lapSpeeds.Clear();
                    raceSpeedTraces.Clear();
                    raceLapTraces.Clear();
                    raceCrashSpots.Clear();
                    resultsLap = -1;
                    raceTopSpeed = 0;
                    StartLapTracking();
                    RaceNotify($"Practice lap 1 of {timer.PracticeLaps}", HudColor.Yellow);
                    if (game.TimeSeconds - lastGoShown > 15)
                    {
                        goUntil = game.TimeSeconds + 2.0;
                        lastGoShown = game.TimeSeconds;
                        Sound(settings.SoundGo);
                    }
                    break;

                case LapEventKind.PracticeLapCompleted:
                {
                    int n = timer.PracticeLapsDone.Count;
                    var lap = timer.PracticeLapsDone[n - 1];
                    var rec = CourseRecord(false);
                    string gap = "";
                    if (rec != null && rec.BestLap.HasValue)
                    {
                        (gapText, gapColour) = HudRenderer.Gap(lap.Time - rec.BestLap.Value);
                        gap = "  " + gapText;
                    }
                    StartLapTracking();
                    string next = n < timer.PracticeLaps ? $"  - practice lap {n + 1} of {timer.PracticeLaps}" : "";
                    RaceNotify($"Practice {n}: {LapTimer.Format(seconds)}{gap}{next}", HudColor.Yellow);
                    Sound(settings.SoundLap);
                    break;
                }

                case LapEventKind.RaceStarted:
                    FlashAirGate(timer.Gate);
                    lastCrashPenaltyAt = double.NegativeInfinity;
                    crashSlowLock = false;
                    PluginLog.StartTrace(30, "race start");   // frame times while the race gets going
                    if (timer.PracticeLapsDone.Count == 0 || currentShip == null) currentShip = game.GetShipShape(250);
                    ResetRaceStats();
                    results = shipResults = null;
                    gapText = null;
                    lapSpeeds.Clear();
                    raceSpeedTraces.Clear();
                    raceLapTraces.Clear();
                    raceCrashSpots.Clear();
                    resultsLap = -1;
                    raceTopSpeed = 0;
                    StartLapTracking();
                    if (timer.PracticeLapsDone.Count > 0)
                    {
                        // Straight from practice: GO! and the race is on.
                        RaceNotify("Practice over - RACE ON!", HudColor.Green);
                        goUntil = game.TimeSeconds + 2.0;
                        lastGoShown = game.TimeSeconds;
                        FlashTimer(HudColor.Green);
                        Sound(settings.SoundGo);
                        Sound(settings.SoundGo, 150);
                        break;
                    }
                    // "GO!" under the timer (unless a countdown just showed it).
                    if (game.TimeSeconds - lastGoShown > 15)
                    {
                        goUntil = game.TimeSeconds + 2.0;
                        lastGoShown = game.TimeSeconds;
                        Sound(settings.SoundGo);
                    }
                    break;

                case LapEventKind.CheckpointHit:
                {
                    int i = (int)seconds;
                    if (i >= 0 && i < timer.Checkpoints.Count) FlashAirGate(timer.Checkpoints[i]);
                    lapCheckpointMarks.Add(new[] { lapDistance, currentSpeed, i + 1 });
                    var rec = CourseRecord(false);
                    double? split = timer.SplitThisLap(i);
                    double? best = rec != null && i < rec.BestLapSplits.Count ? rec.BestLapSplits[i] : null;
                    string gap = "";
                    if (split.HasValue && best.HasValue)
                    {
                        (gapText, gapColour) = HudRenderer.Gap(split.Value - best.Value);
                        gap = "  " + gapText;
                    }

                    // Flew past one or more checkpoints? Say so straight away, not at the end of the lap.
                    var missed = new List<int>();
                    for (int j = 0; j < i; j++)
                        if (!timer.IsCheckpointHit(j) && warnedMissed.Add(j)) missed.Add(j);
                    if (missed.Count > 0)
                    {
                        string which = string.Join(", ", missed.ConvertAll(j => (j + 1).ToString()));
                        RaceNotify($"MISSED CP {which}!  +{missed.Count * settings.PenaltySeconds:0.#}s", HudColor.Red);
                        FlashTimer(HudColor.Red);
                        PenaltySound(150);
                        break;
                    }

                    bool purple = split.HasValue && rec != null && i < rec.BestSplits.Count && rec.BestSplits[i].HasValue && split.Value < rec.BestSplits[i].Value;
                    // (Under the timer, with no gap on it: the gap is beside the time and in the splits.)
                    RaceNotify($"Checkpoint {i + 1} / {timer.Checkpoints.Count}", purple ? HudColor.Purple : HudColor.Green);
                    GlowEdge(purple ? HudColor.Purple : HudColor.Green, 1.0);
                    if (settings.CheckpointSound) Sound(settings.SoundCheckpoint);
                    break;
                }

                case LapEventKind.LapCompleted:
                {
                    FlashAirGate(timer.FinishGate ?? timer.Gate);
                    var lap = timer.Laps[timer.Laps.Count - 1];
                    // Checkpoints missed at the end of the lap (no later one hit, so no warning yet) still get the penalty sound.
                    bool unwarnedPenalty = lap.MissedCheckpoints > warnedMissed.Count;
                    var speeds = FinishedLapSpeeds(lap.Flown);
                    lapSpeeds.Add(speeds);
                    lapStats.Add(FinishedLapStats());
                    var speedTrace = NormalisedSpeedTrace();
                    raceSpeedTraces.Add(speedTrace);
                    raceLapTraces.Add(lapTrace);
                    raceCrashSpots.Add(lapCrashSpots);
                    raceTopSpeed = Math.Max(raceTopSpeed, speeds.Top);

                    var rec = CourseRecord(true);
                    string gap = "";
                    if (rec.BestLap.HasValue)
                    {
                        (gapText, gapColour) = HudRenderer.Gap(lap.Time - rec.BestLap.Value);
                        gap = "  " + gapText;
                    }

                    bool courseBest = !rec.BestLap.HasValue || lap.Time < rec.BestLap.Value;
                    if (courseBest)
                    {
                        rec.BestLap = lap.Time;
                        rec.BestLapSplits = new List<double?>(lap.Splits);
                        if (lapTrace.Count >= 8) rec.BestLapTrace = lapTrace;
                        if (speedTrace.Count >= 8) rec.BestLapSpeed = speedTrace;
                        rec.BestLapShip = currentShip != null && currentShip.Count > 0 ? currentShip : null;
                    }
                    UpdateBestSplitsAndSectors(rec, lap);
                    // This ship's best lap, with its path, for its replay and export.
                    if (!string.IsNullOrEmpty(raceShipName))
                    {
                        if (rec.Ships == null) rec.Ships = new Dictionary<string, GateSettings.ShipRecord>();
                        if (!rec.Ships.TryGetValue(raceShipName, out var shipRec)) rec.Ships[raceShipName] = shipRec = new GateSettings.ShipRecord();
                        // Its last lap, kept whatever the time (Records tab: race or watch it again).
                        if (lapTrace.Count >= 8)
                        {
                            shipRec.LastLap = lap.Time;
                            shipRec.LastLapTrace = lapTrace;
                            shipRec.LastLapSpeed = speedTrace.Count >= 8 ? speedTrace : null;
                            shipRec.LastLapSplits = new List<double?>(lap.Splits);
                            shipRec.LastLapShip = currentShip != null && currentShip.Count > 0 ? currentShip : null;
                            shipRec.LastLapDate = DateTime.Now;
                            recordsVersion++;
                        }
                        if (!shipRec.BestLap.HasValue || lap.Time < shipRec.BestLap.Value)
                        {
                            shipRec.BestLap = lap.Time;
                            shipRec.BestLapSplits = new List<double?>(lap.Splits);
                            if (lapTrace.Count >= 8) shipRec.BestLapTrace = lapTrace;
                            if (speedTrace.Count >= 8) shipRec.BestLapSpeed = speedTrace;
                            shipRec.BestLapShip = currentShip != null && currentShip.Count > 0 ? currentShip : null;
                            recordsVersion++;
                        }
                    }
                    if (!settings.BestSeconds.HasValue || lap.Time < settings.BestSeconds.Value)
                        settings.BestSeconds = lap.Time;
                    settings.Save(settingsPath);
                    StartLapTracking();

                    string speed = $"  ({GameOptions.Speed(speeds.Top)} top)";
                    if (lap.Penalty > 0)
                    {
                        RaceNotify($"Lap {timer.Laps.Count}: {LapTimer.Format(seconds)}{gap}  ({HudRenderer.PenaltyText(lap)})", HudColor.Red);
                        FlashTimer(courseBest ? HudColor.Green : HudColor.Red);
                    }
                    else
                    {
                        RaceNotify($"Lap {timer.Laps.Count}: {LapTimer.Format(seconds)}{gap}{(courseBest ? "  NEW BEST LAP!" : speed)}",
                            courseBest ? HudColor.Purple : HudColor.White);
                        if (courseBest) FlashTimer(HudColor.Purple);
                    }
                    if (unwarnedPenalty) PenaltySound(150);
                    else if (courseBest)
                    {
                        Sound(settings.SoundBest);
                        Sound(settings.SoundBest, 120);
                        Sound(settings.SoundBest, 240);
                    }
                    else Sound(settings.SoundLap);
                    break;
                }

                case LapEventKind.RaceFinished:
                    RaceNotify($"Finished! Total {LapTimer.Format(seconds)}", HudColor.Green);
                    shipResults = null;
                    resultLaps = timer.Laps.ToList();
                    results = BuildResults(seconds);
                    resultsClosed = false;
                    RememberRace(seconds);
                    resultsUntil = game.TimeSeconds + settings.ResultsSeconds;
                    if (results.Headline != null && results.Headline.StartsWith("NEW COURSE RECORD"))
                        for (int k = 0; k < 4; k++) Sound(settings.SoundBest, 300 + k * 110);
                    countdownAllowed = false; // leave the gate area before a countdown can start again
                    break;
            }
        }

        /// <summary>Race stats for under the splits after the finish.</summary>
        private List<(string Label, string Value, HudColor Colour)> BuildRaceStats(int laps, double total, double? shipBestBefore)
        {
            var list = new List<(string, string, HudColor)>();
            if (!string.IsNullOrEmpty(raceShipName)) list.Add(("SHIP", raceShipName, HudColor.Cyan));
            if (shipBestBefore.HasValue)
            {
                double d = total - shipBestBefore.Value;
                list.Add(("VS SHIP BEST RACE", FasterSlower(d), d < 0 ? HudColor.Purple : HudColor.Yellow));
            }
            else if (!string.IsNullOrEmpty(raceShipName)) list.Add(("VS SHIP BEST RACE", "first race in this ship", HudColor.Grey));

            if (lapStats.Count > 0)
            {
                double dist = 0, maxG = 0;
                foreach (var st in lapStats) { dist += st.Distance; maxG = Math.Max(maxG, st.MaxG); }
                // (Turn G and braking per lap are in the sectors table, so only race-wide figures here.)
                list.Add(("DISTANCE", Distance(dist), HudColor.White));
                list.Add(("MAX G", $"{maxG:0.0} g", HudColor.White));
            }
            if (raceSpeedSamples.Count > 4 && raceTopSpeed > 1)
            {
                int fast = 0;
                foreach (var v in raceSpeedSamples) if (v >= raceTopSpeed * 0.9) fast++;
                list.Add(("AT TOP SPEED", $"{100.0 * fast / raceSpeedSamples.Count:0}% of race", HudColor.White));
            }
            if (timer.Laps.Count >= 2)
            {
                double best = double.MaxValue, worst = 0, sum = 0;
                foreach (var l in timer.Laps) { best = Math.Min(best, l.Time); worst = Math.Max(worst, l.Time); sum += l.Time; }
                list.Add(("AVG LAP", LapTimer.Format(sum / timer.Laps.Count), HudColor.White));
                double spread = worst - best;
                list.Add(("CONSISTENCY", $"{spread:0.000}s spread", spread < 1 ? HudColor.Green : spread < 3 ? HudColor.Yellow : HudColor.Orange));
            }
            return list;
        }

        private static void AddGraphLine(HudModel.ResultsPanel panel, string label, HudColor colour, List<double[]> trace, double? time = null)
        {
            if (trace == null || trace.Count < 2) return;
            if (time.HasValue) label += "  " + LapTimer.Format(time.Value);
            var line = new HudModel.GraphLine { Label = label, Colour = colour };
            foreach (var p in trace)
            {
                line.Points.Add((p[0], p[1]));
                if (p.Length >= 3) line.Checkpoints.Add((p[0], p[1], (int)p[2]));
            }
            panel.Graph.Add(line);
            foreach (var p in trace) panel.GraphMaxSpeed = Math.Max(panel.GraphMaxSpeed, p[1]);
        }


        /// <summary>
        /// Lap time at each share of the lap (0..1), from a speed trace: the time between two known points (the
        /// start, each checkpoint with a split, the finish) is shared out by how slow the ship was over each bit.
        /// </summary>
        private static Func<double, double> LapClock(List<double[]> trace, double lapTime, IList<double?> splits)
        {
            // Cumulative "slowness" (sum of 1 / speed) along the lap.
            var xs = new List<double> { 0 }; var cum = new List<double> { 0 };
            var anchors = new List<(double X, double T)> { (0, 0) };
            double c = 0, lastX = 0;
            foreach (var p in trace)
            {
                double x = Math.Max(lastX, Math.Min(1, p[0]));
                c += (x - lastX) / Math.Max(5, p[1]);
                xs.Add(x); cum.Add(c); lastX = x;
                if (p.Length >= 3 && splits != null)
                {
                    int i = (int)p[2] - 1;
                    if (i >= 0 && i < splits.Count && splits[i].HasValue && splits[i].Value > anchors[anchors.Count - 1].T && splits[i].Value < lapTime)
                        anchors.Add((x, splits[i].Value));
                }
            }
            c += (1 - lastX) / Math.Max(5, trace[trace.Count - 1][1]);
            xs.Add(1); cum.Add(c);
            anchors.Add((1, lapTime));
            double Cum(double x)
            {
                int k = xs.BinarySearch(x);
                if (k >= 0) return cum[k];
                k = ~k;
                if (k <= 0) return 0;
                if (k >= xs.Count) return cum[cum.Count - 1];
                double span = xs[k] - xs[k - 1];
                return span > 1e-9 ? cum[k - 1] + (cum[k] - cum[k - 1]) * (x - xs[k - 1]) / span : cum[k];
            }
            return x =>
            {
                int a = 0;
                while (a < anchors.Count - 2 && anchors[a + 1].X <= x) a++;
                var (x0, t0) = anchors[a]; var (x1, t1) = anchors[a + 1];
                double c0 = Cum(x0), c1 = Cum(x1);
                double u = c1 - c0 > 1e-12 ? (Cum(x) - c0) / (c1 - c0) : (x1 - x0 > 1e-9 ? (x - x0) / (x1 - x0) : 1);
                return t0 + (t1 - t0) * Math.Max(0, Math.Min(1, u));
            };
        }

        /// <summary>Time gap graph: how far ahead (negative) or behind (positive) lap A was of lap B round the lap.</summary>
        private static void AddGapGraph(HudModel.ResultsPanel panel, string against,
            List<double[]> a, double aTime, IList<double?> aSplits, List<double[]> b, double bTime, IList<double?> bSplits)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2 || aTime <= 0 || bTime <= 0) return;
            var ca = LapClock(a, aTime, aSplits);
            var cb = LapClock(b, bTime, bSplits);
            const int n = 150;
            for (int i = 0; i <= n; i++)
            {
                double x = (double)i / n;
                panel.Gap.Add((x, ca(x) - cb(x)));
            }
            panel.GapAgainst = against;
        }

        /// <summary>Checkpoints missed on one lap (1 = CP 1), for the red lines on the graph.</summary>
        private void MarkMissed(HudModel.ResultsPanel panel, int lap)
        {
            var sp = resultLaps[lap].Splits;
            for (int i = 0; i < timer.Checkpoints.Count; i++) if (i >= sp.Length || !sp[i].HasValue) panel.MissedCheckpoints.Add(i + 1);
        }

        /// <summary>
        /// After a ship's times are deleted: if it held the course best lap (or a best race), the next best ship's
        /// takes its place (lap, splits, racing line, ghost shape, speed graph), and the best checkpoint and sector
        /// times are worked out again from the ships left. Returns the ship that now has the best lap, or null.
        /// </summary>
        private static string PromoteNextBestShip(GateSettings.CourseRecord r, GateSettings.ShipRecord gone)
        {
            string promoted = null;
            bool Same(double? a, double? b) => a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) < 1e-6;
            if (Same(r.BestLap, gone.BestLap))
            {
                var next = r.Ships.Where(kv => kv.Value.BestLap.HasValue).OrderBy(kv => kv.Value.BestLap.Value).FirstOrDefault();
                if (next.Value != null)
                {
                    var sh = next.Value;
                    r.BestLap = sh.BestLap;
                    r.BestLapSplits = sh.BestLapSplits != null ? new List<double?>(sh.BestLapSplits) : new List<double?>();
                    r.BestLapTrace = sh.BestLapTrace;
                    r.BestLapShip = sh.BestLapShip;
                    r.BestLapSpeed = sh.BestLapSpeed;
                    promoted = next.Key;
                }
                else
                {
                    r.BestLap = null; r.BestLapSplits = new List<double?>(); r.BestLapTrace = null; r.BestLapShip = null; r.BestLapSpeed = null;
                }
                // Best ever time at each checkpoint and for each sector: the best of the ships left.
                List<double?> MinOf(Func<GateSettings.ShipRecord, List<double?>> pick)
                {
                    var res = new List<double?>();
                    foreach (var sh in r.Ships.Values)
                    {
                        var l = pick(sh);
                        if (l == null) continue;
                        for (int i = 0; i < l.Count; i++)
                        {
                            while (res.Count <= i) res.Add(null);
                            if (l[i].HasValue && (!res[i].HasValue || l[i].Value < res[i].Value)) res[i] = l[i];
                        }
                    }
                    return res;
                }
                r.BestSplits = MinOf(sh => sh.BestLapSplits);
                r.BestSectors = MinOf(sh => sh.BestSectors);
                r.BestSectorTraces = null;   // they may have been the deleted ship's
            }
            foreach (var laps in r.BestRace.Keys.ToList())
            {
                if (gone.BestRace == null || !gone.BestRace.TryGetValue(laps, out double t) || Math.Abs(r.BestRace[laps] - t) > 1e-6) continue;
                double? next = null;
                foreach (var sh in r.Ships.Values)
                    if (sh.BestRace != null && sh.BestRace.TryGetValue(laps, out double v) && (!next.HasValue || v < next.Value)) next = v;
                if (next.HasValue) r.BestRace[laps] = next.Value; else r.BestRace.Remove(laps);
            }
            return promoted;
        }

        // ---- Results: which lap the graphs and the map show (the race's best lap, or one picked with the keys).
        private int resultsLap = -1;   // -1 = the best lap of the race
        private List<LapRecord> resultLaps = new List<LapRecord>();   // the finished race's laps (kept if the timer re-arms)
        private (string Name, List<double[]> Speed, double? Time, IList<double?> Splits) courseGraphRef, shipGraphRef;
        // Per race lap: where you crashed and when (lap time).
        private readonly List<List<(Vec3D At, double T)>> raceCrashSpots = new List<List<(Vec3D At, double T)>>();
        private List<(Vec3D At, double T)> lapCrashSpots = new List<(Vec3D At, double T)>();
        private List<(Vec3D At, double T)> resultsCrashSpots;      // the shown lap's crashes, for the map
        private List<(int Checkpoint, double? Hit, double? MissedAt)> resultsCpTimes;   // the shown lap's checkpoints, for the map's replay
        private double resultsLapTime;
        private HashSet<int> resultsMissed;         // the shown lap's missed checkpoints (0 = CP 1), for the map

        private int RaceBestLapIndex()
        {
            int bestIdx = 0;
            for (int l = 1; l < resultLaps.Count; l++) if (resultLaps[l].Time < resultLaps[bestIdx].Time) bestIdx = l;
            return bestIdx;
        }

        private int ResultsLapIndex() => resultsLap >= 0 && resultsLap < resultLaps.Count ? resultsLap : RaceBestLapIndex();

        /// <summary>
        /// The results graphs for the shown lap: its speed against the reference lap (the record / ship best from
        /// before the race, or another lap of the race when there's none), and the time gap between them.
        /// </summary>
        private void FillGraphs(HudModel.ResultsPanel panel, (string Name, List<double[]> Speed, double? Time, IList<double?> Splits) reference)
        {
            panel.Graph.Clear(); panel.Gap.Clear(); panel.MissedCheckpoints.Clear();
            panel.GraphMaxSpeed = 0; panel.GapAgainst = null; panel.GraphLap = null;
            panel.GraphVersion++;
            int laps = resultLaps.Count;
            if (laps == 0 || raceSpeedTraces.Count != laps) return;
            int bestIdx = RaceBestLapIndex(), idx = ResultsLapIndex();
            var lap = resultLaps[idx];
            bool beat = !reference.Time.HasValue || lap.Time < reference.Time.Value - 1e-6;
            string Cap(string x) => string.IsNullOrEmpty(x) ? x : char.ToUpper(x[0]) + x.Substring(1);
            MarkMissed(panel, idx);
            AddGraphLine(panel, resultsLap < 0 ? $"This race (L{idx + 1})" : $"Lap {idx + 1}", beat ? HudColor.Purple : HudColor.Green, raceSpeedTraces[idx], lap.Time);
            if (reference.Speed != null && reference.Time.HasValue)
            {
                string name = beat ? "old " + reference.Name : reference.Name;
                AddGraphLine(panel, Cap(name), beat ? HudColor.Grey : HudColor.Purple, reference.Speed, reference.Time);
                AddGapGraph(panel, name, raceSpeedTraces[idx], lap.Flown, lap.Splits, reference.Speed, reference.Time.Value, reference.Splits);
            }
            else
            {
                int other = idx != bestIdx ? bestIdx : laps - 1;
                if (other != idx)
                {
                    var o = resultLaps[other];
                    string name = other == bestIdx ? $"best lap (L{other + 1})" : $"last lap (L{other + 1})";
                    AddGraphLine(panel, Cap(name), HudColor.Grey, raceSpeedTraces[other], o.Time);
                    AddGapGraph(panel, name, raceSpeedTraces[idx], lap.Flown, lap.Splits, raceSpeedTraces[other], o.Flown, o.Splits);
                }
            }
            if (laps > 1)
                panel.GraphLap = (resultsLap < 0 ? $"best lap (L{idx + 1})" : $"lap {idx + 1} of {laps}")
                    + $"   {settings.GetBinding(GateKey.ResultsPrevLap)} / {settings.GetBinding(GateKey.ResultsNextLap)}: other laps";
        }

        /// <summary>The map after the race: the shown lap's path (over the record's), its crashes and missed checkpoints.</summary>
        private void SetResultsMap()
        {
            compareMine = compareRef = null; compareMineLabel = compareRefLabel = null; resultsCrashSpots = null; resultsMissed = null; resultsCpTimes = null;
            int laps = resultLaps.Count;
            if (laps == 0) return;
            int idx = ResultsLapIndex();
            var lap = resultLaps[idx];
            resultsMissed = new HashSet<int>();
            for (int i = 0; i < timer.Checkpoints.Count; i++) if (i >= lap.Splits.Length || !lap.Splits[i].HasValue) resultsMissed.Add(i);
            resultsCrashSpots = idx < raceCrashSpots.Count ? raceCrashSpots[idx] : null;
            // When each checkpoint was flown, or (missed) when a later one was flown and so it counted as missed.
            resultsLapTime = lap.Flown;
            resultsCpTimes = new List<(int, double?, double?)>();
            for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                double? hit = i < lap.Splits.Length ? lap.Splits[i] : null;
                double? missedAt = null;
                if (!hit.HasValue)
                {
                    for (int j = i + 1; j < lap.Splits.Length && !missedAt.HasValue; j++) missedAt = lap.Splits[j];
                    missedAt ??= lap.Flown;
                }
                resultsCpTimes.Add((i, hit, missedAt));
            }
            if (raceLapTraces.Count != laps || raceLapTraces[idx] == null || raceLapTraces[idx].Count < 8) return;
            compareMine = raceLapTraces[idx];
            compareMineLabel = resultsLap < 0 ? $"Your best lap (L{idx + 1})  {LapTimer.Format(lap.Time)}" : $"Lap {idx + 1}  {LapTimer.Format(lap.Time)}";
            compareRefLabel = null;
            if (recordTraceAtStart != null && recordTraceAtStart.Count >= 8)
            {
                compareRef = recordTraceAtStart;
                bool beat = !recordLapAtStart.HasValue || lap.Time < recordLapAtStart.Value - 1e-6;
                compareRefLabel = $"{(beat ? "Old course record" : "Course record")}  {LapTimer.Format(recordLapAtStart ?? 0)}";
            }
        }

        /// <summary>Results lap keys: step through best lap, L1, L2, ... and back.</summary>
        private List<double[]> mapReplayFor;
        private double mapReplayStart, mapReplayHoldUntil;

        private void StepResultsLap(int dir, bool quiet = false)
        {
            int laps = resultLaps.Count;
            if (results == null) return;
            if (laps < 2) { if (!quiet) Notify("Only one lap in this race: nothing to switch to", HudColor.Yellow); return; }
            // -1 (best), 0, 1, ..., laps - 1, round again.
            int pos = resultsLap + 1;                 // 0 = best
            pos = ((pos + dir) % (laps + 1) + laps + 1) % (laps + 1);
            resultsLap = pos - 1;
            FillGraphs(results, courseGraphRef);
            if (shipResults != null) FillGraphs(shipResults, shipGraphRef);
            SetResultsMap();
            int idx = ResultsLapIndex();
            if (quiet) return;
            mapReplayHoldUntil = game.TimeSeconds + 60;   // you picked this lap: the minimap stays on it for a while
            Notify(resultsLap < 0 ? $"Results: best lap (L{idx + 1})" : $"Results: lap {idx + 1} of {laps}", HudColor.Cyan);
            PluginLog.Info($"Results lap: {(resultsLap < 0 ? "best" : (resultsLap + 1).ToString())}");
        }

        private double speedoSessionTop, speedoMax;
        private double groundAskedAt = double.NegativeInfinity;
        private string groundShown;
        private Vec3D? rollUpWas;
        private double rollRateAt, rollRate;
        private string speedoUnit;

        /// <summary>A round number for the end of the speedometer dial (100, 150, 200, 300, 400, 500, 750, 1000, ...).</summary>
        private static double NiceDialMax(double v)
        {
            double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, v))));
            foreach (double m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 7.5, 10 }) if (m * mag >= v) return m * mag;
            return 10 * mag;
        }

        // Virtual key codes of the flight keys shown, in the order of HudModel.KeysDown's bits.
        private static readonly int[] FlightKeyCodes = { 0x51 /* Q */, 0x57 /* W */, 0x45 /* E */, 0x41 /* A */, 0x53 /* S */, 0x44 /* D */, 0x43 /* C */, 0x20 /* Space */ };

        /// <summary>Speedometer, flight keys and the speed vector marker (while flying the course, not editing it).</summary>
        private void AddFlightAids(HudModel model)
        {
            bool flying = lastVelocity.HasValue && playerPosition.HasValue;
            if (flying && (settings.Speedometer == 2 || settings.Speedometer == 1 && settings.RaceHudOnly))
            {
                double speed = lastVelocity.Value.Length;
                speedoSessionTop = Math.Max(speedoSessionTop, speed);
                // The dial goes up to a round number a bit above the fastest this ship has gone (here or on its
                // best laps), so the needle uses most of the dial; it only ever grows.
                double shipTop = 0;
                var rec = CourseRecord(false);
                if (rec?.Ships != null && game.ShipName != null && rec.Ships.TryGetValue(game.ShipName, out var sr)) shipTop = sr.TopSpeed;
                double top = GameOptions.ToUnit(Math.Max(Math.Max(speedoSessionTop, shipTop) * 1.1, 100));
                if (speedoUnit != GameOptions.SpeedUnitLabel) { speedoUnit = GameOptions.SpeedUnitLabel; speedoMax = 0; }   // unit changed
                if (top > speedoMax) speedoMax = NiceDialMax(top);
                model.Speedo = GameOptions.SpeedNumber(speed);
                model.SpeedoUnit = GameOptions.SpeedUnitLabel;
                model.SpeedoOther = GameOptions.SpeedUnit == 0 ? $"{speed * 3.6:0}" : $"{speed:0}";
                model.SpeedoOtherUnit = GameOptions.SpeedUnit == 0 ? "KM/H" : "M/S";
                model.SpeedoValue = GameOptions.ToUnit(speed);
                model.SpeedMs = speed;
                model.LightsOn = game.ShipLightsOn == true;
                model.SpeedoMax = settings.SpeedometerMax > 0 ? settings.SpeedometerMax : speedoMax;
                model.SpeedoParts = (settings.SpeedoResourceBars ? 1 : 0) | (settings.SpeedoPowerTile ? 2 : 0) | (settings.SpeedoDampenerTile ? 4 : 0)
                    | (settings.SpeedoParkingTile ? 8 : 0) | (settings.SpeedoAntennaTile ? 16 : 0) | (settings.SpeedoSafeSpeed ? 32 : 0);
                if (settings.GroundHeight && playerPosition.HasValue)
                {
                    // Asked ten times a second (the number doesn't need more, and the lookup isn't free).
                    if (game.TimeSeconds - groundAskedAt >= 0.1)
                    {
                        groundAskedAt = game.TimeSeconds;
                        groundShown = game.TryGetGroundDistance(playerPosition.Value, out double above)
                            ? (above >= 10000 ? $"{above / 1000:0.0} km" : $"{Math.Max(0, above):0} m") : null;
                        seaShown = groundShown != null && game.TryGetSeaAltitude(playerPosition.Value, out double sea)
                            ? (Math.Abs(sea) >= 10000 ? $"{sea / 1000:0.0} km" : $"{sea:0} m") : null;
                    }
                    model.GroundHeight = groundShown;
                    model.SeaHeight = seaShown;
                }
            }
            // Race HUD only: a toolbar number key (1..9, 0; not with Ctrl / Shift / Alt) shows that slot for a moment.
            if (settings.RaceHudOnly && settings.ToolbarPeekSeconds > 0 && game.HasFocus && !controlsOpen && !game.IsPaused)
            {
                bool peekMods = game.IsKeyDown(0x11) || game.IsKeyDown(0x10) || game.IsKeyDown(0x12);
                for (int pk = 0; pk < 10; pk++)
                {
                    bool numberDown = game.IsKeyDown(pk == 9 ? 0x30 : 0x31 + pk);
                    if (numberDown && !peekKeyDown[pk] && !peekMods) { peekSlot = pk; peekUntil = game.TimeSeconds + settings.ToolbarPeekSeconds; }
                    peekKeyDown[pk] = numberDown;
                }
            }
            if (peekSlot >= 0 && (game.TimeSeconds >= peekUntil || !settings.RaceHudOnly)) peekSlot = -1;
            model.ToolbarPeek = peekSlot;
            if (settings.FlightKeys > 0 && flying)
            {
                model.KeysCorner = 2;   // always bottom right
                int mask = 0;
                if (game.HasFocus)
                    for (int i = 0; i < FlightKeyCodes.Length; i++) if (game.IsKeyDown(FlightKeyCodes[i])) mask |= 1 << i;
                model.KeysDown = mask;
            }
            if ((settings.RollIndicator || settings.PitchIndicator) && flying && game.TryGetGravity(out Vec3D down) && game.TryGetSeatPose(out _, out Vec3D seatFwd, out Vec3D seatUp))
            {
                // Pitch: how far the nose is above (+) or below the horizon.
                if (settings.PitchIndicator)
                    model.Pitch = Math.Asin(Math.Clamp(Vec3D.Dot(seatFwd.Normalized(), down * -1), -1, 1)) * 180 / Math.PI;
                // The ship's up against straight up, both seen along the nose (so pitch doesn't count as roll).
                Vec3D f = seatFwd.Normalized(), skyUp = down * -1;
                Vec3D level = skyUp - f * Vec3D.Dot(skyUp, f);
                if (settings.RollIndicator && level.Length > 0.05)   // (pointing straight up or down: no roll to speak of)
                {
                    level = level.Normalized();
                    Vec3D shipUp = (seatUp - f * Vec3D.Dot(seatUp, f)).Normalized();
                    model.Roll = Math.Atan2(Vec3D.Dot(Vec3D.Cross(level, shipUp), f), Vec3D.Dot(level, shipUp)) * 180 / Math.PI;
                    // Roll speed: how far the ship's up has turned about its nose since the last frame (smoothed a little).
                    double rollNow = game.TimeSeconds, rollDt = rollNow - rollRateAt;
                    if (rollUpWas.HasValue && rollDt > 1e-4 && rollDt < 0.5)
                    {
                        Vec3D was = rollUpWas.Value - f * Vec3D.Dot(rollUpWas.Value, f);
                        if (was.Length > 0.05)
                        {
                            was = was.Normalized();
                            double turned = Math.Atan2(Vec3D.Dot(Vec3D.Cross(was, shipUp), f), Vec3D.Dot(was, shipUp)) * 180 / Math.PI;
                            rollRate += (turned / rollDt - rollRate) * Math.Min(1, rollDt * 8);
                        }
                    }
                    else if (rollDt >= 0.5) rollRate = 0;
                    if (rollDt > 1e-4) { rollUpWas = shipUp; rollRateAt = rollNow; }
                    model.RollRate = rollRate;
                }
            }
            if (settings.VelocityMarker && flying && lastVelocity.Value.Length > 5)
            {
                // Ahead of the ship along its velocity: a ring with wings and a fin, about the same size on screen.
                Vec3D v = lastVelocity.Value.Normalized();
                Vec3D upHint = game.TryGetShipAxes(out _, out Vec3D ay, out _) ? ay : timer.Gate?.Up ?? new Vec3D(0, 1, 0);
                Vec3D right = Vec3D.Cross(v, upHint);
                if (right.Length < 0.1) right = Vec3D.Cross(v, new Vec3D(1, 0, 0));
                right = right.Normalized();
                Vec3D up = Vec3D.Cross(right, v).Normalized();
                const double dist = 250;
                Vec3D c = playerPosition.Value + v * dist;
                // As a HUD symbol when the game UI can draw it (thick, outlined, easy to see); else lines in the world.
                if (settings.UseGameUi && game.HudVisible)
                {
                    // Far ahead, so the symbol shows the direction of travel itself. A point only 250 m ahead sits low
                    // on screen from a chase camera above the ship, away from the game's own crosshair, even when
                    // flying dead straight.
                    Vec3D far = playerPosition.Value + v * 20000;
                    if (game.TryProjectToScreen(far, out double sx, out double sy, out bool behind) && !behind && sx > 0.01 && sx < 0.99 && sy > 0.01 && sy < 0.99)
                    {
                        model.VectorX = sx;
                        model.VectorY = sy;
                    }
                    return;
                }
                double r = dist * 0.012;
                var col = new HudColor(170, 255, 60, 255);
                Vec3D prev = c + right * r;
                for (int k = 1; k <= 12; k++)
                {
                    double a = k * Math.PI * 2 / 12;
                    Vec3D p = c + right * (Math.Cos(a) * r) + up * (Math.Sin(a) * r);
                    game.DrawMarkerLine(prev, p, col);
                    prev = p;
                }
                game.DrawMarkerLine(c + right * r, c + right * (r * 2.4), col);
                game.DrawMarkerLine(c - right * r, c - right * (r * 2.4), col);
                game.DrawMarkerLine(c + up * r, c + up * (r * 1.9), col);
            }
        }

        // ---------------------------------------------------------------- ship analyser

        private ShipStats anaLook, anaA, anaB;
        private double anaLookAt, anaPinnedAt;
        private bool anaLeftDown, anaRightDown;
        private bool analyserMode;
        private object anaLoggedGrid;
        private readonly LapTimer blankTimer = new LapTimer();   // no course: a HUD model with nothing of the race on it

        private void SetAnalyserMode(bool on, Vec3D? position)
        {
            if (on == analyserMode) return;
            if (on && editMode) SetEditMode(false, position);
            analyserMode = on;
            recordsVersion++;   // the Race tab's button says Open / Close
            anaLook = anaA = anaB = null;
            anaLookAt = anaPinnedAt = 0;
            anaLeftDown = anaRightDown = true;   // ignore the click that pressed the menu button
            if (on)
            {
                timer.Reset();
                results = shipResults = null;
                countdownStart = null;
                KeyHint(spotlight != null
                    ? $"Race spotlight: {spotlight.Ship}, {spotlight.Course}. Look at the ship and left click. Esc: exit"
                    : "Ship analyser: look at a ship. Left click keeps its stats, right click another to compare. Esc: exit", HudColor.Cyan);
            }
            else
            {
                timer.Resync(position);
                // (On a tour the cinematic's camera is always let go of: the ship can only be put back from its seat.)
                if (orbitCam && (ocFromSpotlight || spotTour != null || spotTourStepping)) StopOrbitCam(null);
                Notify(spotlight != null ? "Race spotlight off" : "Ship analyser off", HudColor.Cyan);
                spotlight = null; spotlightPath = null; spotExport = null; spotGrid = null; spotStats = null;
                if (!spotTourStepping)
                {
                    // (Closed by hand: the tour ends too, and its ship goes back to where it was parked.)
                    if (spotTour != null && spotTourHome.HasValue && spotTourHomeGrid != null)
                    {
                        spotTourReturn = spotTourHome; spotTourReturnGrid = spotTourHomeGrid;
                        spotTourReturnAt = game.TimeSeconds + 0.6; spotTourCameraDropped = false;
                    }
                    spotTourHome = null; spotTourHomeGrid = null;
                    spotTour = null; spotTourNext = false; spotTourPhase = 0;
                }
                spotAutoRaceAt = spotAutoCineAt = spotTeleportedAt = null; spotCameraAfterRace = false;
                if (spotStartedAutopilot) { spotStartedAutopilot = false; StopAutopilot("Autopilot off"); }
                if (spotLineBefore.HasValue)
                {
                    // The racing line (and ghost) back to what it was before the spotlight raced its race.
                    var b = spotLineBefore.Value;
                    spotLineBefore = null;
                    raceGhost = b.Ghost; racePossible = b.Possible; raceBestRace = b.BestRace; spotRaceTrace = b.Trace;
                    recordsVersion++;
                }
            }
        }

        private (GhostFile Ghost, bool Possible, bool BestRace, List<double[]> Trace)? spotLineBefore;

        /// <summary>Ship analyser mode's whole HUD: the analyser panel (and messages); no timer, markers, racing line or map.</summary>
        /// <summary>
        /// While a cinematic (or the race spotlight's camera) films a ship: a name tag that follows it, and, while the
        /// autopilot flies a race, a live timing strip along the bottom (lap, race clock, speed). Times and speeds
        /// are the race's own, so a clip filmed at a lower recording speed reads right once sped up.
        /// </summary>
        private void AddFilmOverlay(HudModel model)
        {
            if (cinePlay != null && settings.CineHideHud)
            {
                // A clean picture: nothing of the race HUD, and the game's own HUD hidden too (as with Race HUD only).
                model.Timer = null; model.Laps = null; model.Splits = null; model.Results = null; model.Minimap = null;
                model.RaceStats = null; model.RaceMessage = null; model.CentreMessage = null; model.Countdown = null; model.Flash = null; model.EdgeGlow = null;
                model.Markers.Clear(); model.Arrow = null;
                model.Speedo = null; model.GroundHeight = null; model.SpeedoOther = null; model.KeysCorner = 0;
                model.Roll = null; model.Pitch = null;
                model.HideGameHud = true; model.KeepGyro = false;
                model.CleanPicture = true;
            }
            if (!orbitCam || (cinePlay == null && spotlight == null)) return;
            object grid = cineGrid ?? ocGrid ?? spotGrid;
            if (grid == null) return;
            double scale = RecordingScale;
            double speed = game.TryGetGridVelocity(grid, out Vec3D v) ? v.Length / Math.Max(0.01, scale) : 0;
            if (settings.FilmStrip && autopilot && ReferenceEquals(grid, apGrid))
            {
                var line = AutopilotLine();
                if (line != null && line.Count >= 2)
                {
                    double clock = Math.Max(0, apTime - line[0][3]);
                    string lap = null;
                    if (spotRaceTrace != null && ReferenceEquals(line, spotRaceTrace) && spotRaceLapStarts.Count > 1)
                    {
                        int at = spotRaceLapStarts.Count(t => t <= apTime + 1e-6);
                        lap = $"LAP {Math.Clamp(at, 1, spotRaceLapStarts.Count)} / {spotRaceLapStarts.Count}";
                    }
                    model.FilmStrip = new HudModel.FilmStripPanel { Lap = lap, Clock = LapTimer.Format(clock), Speed = GameOptions.Speed(speed) };
                }
            }
            if (settings.FilmNameTag && game.TryGetGridPose(grid, out Vec3D centre, out _, out _, out _, out double radius, out _)
                && game.TryGetSpectatorView(out _, out _, out Vec3D camUp))
            {
                string name = (ReferenceEquals(grid, spotGrid) ? spotStats?.Name : null) ?? game.GridName(grid) ?? "Ship";
                model.Markers.Add(new HudModel.ScreenMarker
                {
                    Text = name, Sub = settings.FilmStrip && model.FilmStrip != null ? null : GameOptions.Speed(speed),
                    Colour = HudColor.White, World = centre + camUp.Normalized() * Math.Clamp(radius * 0.9, 3, 200),
                    X = 0.5, Y = 0.5,
                });
                if (game.TryProjectToScreen(model.Markers[model.Markers.Count - 1].World.Value, out double sx, out double sy, out bool behind))
                {
                    var tag = model.Markers[model.Markers.Count - 1];
                    tag.X = sx; tag.Y = sy;
                    if (behind) model.Markers.RemoveAt(model.Markers.Count - 1);
                }
            }
        }

        private void DrawAnalyserMode()
        {
            double now = game.TimeSeconds;
            HudModel.Message centre = message != null && now < messageUntil
                ? new HudModel.Message { Text = message, Colour = messageColour } : null;
            var model = hud.Build(blankTimer, now, null, centre, null);
            if (spotlight != null) UpdateSpotlight(model); else UpdateAnalyser(model);
            model.CineDebug = CineDebugNow();
            PluginLog.Step("hud");
            model.CineFade = cinePlay != null && orbitCam ? cineFade : 0;
            model.CineBars = cinePlay != null && orbitCam ? cineBars : 0;
            AddFilmOverlay(model);
            resultsOnScreen = false;
            bool shownByGameUi = settings.UseGameUi && game.ShowHud(model.IsEmpty ? null : model);
            if (!shownByGameUi)
            {
                if (!settings.UseGameUi) game.ShowHud(null);
                hud.DrawDebug(model);
            }
        }

        /// <summary>
        /// Ship analyser mode: the stats of the grid you look at (or the ship you're in when you look at nothing).
        /// Left click keeps a grid's stats (A); right click another to compare it (B); clicking empty space lets go.
        /// </summary>
        private void UpdateAnalyser(HudModel model)
        {
            if (!analyserMode) return;
            if (!game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out _)) return;
            double now = game.TimeSeconds;
            object Looked() => game.LookedAtGrid(eye, dir, 3000, game.OwnGrid == null);

            // Clicks (only on the frame the button goes down, and not while a menu is open).
            bool focus = game.HasFocus && !controlsOpen && !game.IsPaused;
            bool left = focus && game.IsKeyDown(0x01), right = focus && game.IsKeyDown(0x02);
            if (left && !anaLeftDown)
            {
                var g = Looked();
                anaA = g != null ? game.AnalyseGrid(g) : null;
                if (anaA == null) anaB = null;
                anaPinnedAt = now;
                Notify(anaA != null ? $"Analyser: kept {anaA.Name ?? "this grid"} (right click another to compare)" : "Analyser: let go", HudColor.Cyan);
            }
            if (right && !anaRightDown)
            {
                var g = Looked();
                anaB = g != null ? game.AnalyseGrid(g) : null;
                anaPinnedAt = now;
                Notify(anaB != null ? $"Analyser: comparing with {anaB.Name ?? "this grid"}" : "Analyser: stopped comparing", HudColor.Cyan);
            }
            anaLeftDown = left; anaRightDown = right;

            // The looked-at grid: a new one straight away, the same one again every 2 seconds (big grids take a moment).
            if (now - anaLookAt > 0.25)
            {
                var g = Looked() ?? game.OwnGrid;
                if (g == null) anaLook = null;
                else if (!ReferenceEquals(g, anaLook?.Grid) || now - anaLookAt > 2)
                {
                    anaLook = game.AnalyseGrid(g); anaLookAt = now;
                    if (!ReferenceEquals(g, anaLoggedGrid))
                    {
                        anaLoggedGrid = g;
                        PluginLog.Info(anaLook == null ? "Analyser: looking at a grid but couldn't read it"
                            : $"Analyser: {anaLook.Name}: {anaLook.Blocks} blocks, {anaLook.Mass:0} kg, {anaLook.Thrusters} thrusters, {anaLook.Gyros} gyros, {anaLook.Inventories} inventories, stores [{string.Join(", ", anaLook.StoredMax.Keys)}], makes [{string.Join(", ", anaLook.Output.Keys)}], parts from the {(anaLook.FromServer ? "server" : "client")}");
                    }
                }
            }
            if (now - anaPinnedAt > 2)
            {
                anaPinnedAt = now;
                if (anaA != null) anaA = game.AnalyseGrid(anaA.Grid) ?? anaA;
                if (anaB != null) anaB = game.AnalyseGrid(anaB.Grid) ?? anaB;
            }

            var a = anaA ?? anaLook;
            if (a != null) rpiLastShip = a;
            var b = anaB != null && !ReferenceEquals(anaB.Grid, a?.Grid) ? anaB : null;
            if (a == null)
            {
                model.Analyser = new HudModel.AnalyserPanel { TitleA = "SHIP ANALYSER", Hint = "Look at a ship or any grid to see its stats   Esc: exit" };
                return;
            }
            var p = new HudModel.AnalyserPanel
            {
                TitleA = (a.Name ?? "Grid") + (anaA != null ? "  (kept)" : ""),
                TitleB = b?.Name ?? (b != null ? "Grid" : null),
                PinnedA = anaA != null,
                Hint = anaA == null ? "Left click: keep these stats   Right click another grid: compare" : b == null ? "Right click another grid to compare   Left click empty space: let go" : "Right click empty space: stop comparing",
            };
            p.Hint += "   Esc: exit";
            // Arrow keys: left / right change what's drawn on the ship, down opens or folds the long lists.
            bool kl = focus && game.IsKeyDown(0x25), kr = focus && game.IsKeyDown(0x27), kd = focus && game.IsKeyDown(0x28);
            if (kr && !anaKeyRight) anaOverlay = (anaOverlay + 1) % AnaOverlays.Length;
            if (kl && !anaKeyLeft) anaOverlay = (anaOverlay + AnaOverlays.Length - 1) % AnaOverlays.Length;
            if (kd && !anaKeyDown) anaDetails = !anaDetails;
            anaKeyLeft = kl; anaKeyRight = kr; anaKeyDown = kd;
            p.Hint += $"\nLeft / right arrow: drawn on the ship ({AnaOverlays[anaOverlay]})   Down arrow: {(anaDetails ? "fold" : "open")} the lists";
            if (b == null) AddShipRowsRich(p, a); else AddShipRows(p, a, b, true);
            model.Analyser = p;

            DrawShipOverlay(a, anaOverlay, model, new HudColor(255, 210, 60, 255));
            if (b != null) DrawAnalysis(b, new HudColor(80, 200, 255, 255));
        }

        private static string Place(int n) => n + (n % 100 >= 11 && n % 100 <= 13 ? "TH" : n % 10 == 1 ? "ST" : n % 10 == 2 ? "ND" : n % 10 == 3 ? "RD" : "TH");

        private static readonly string[] AnaOverlays = { "thrust arrows", "balance", "thrusters", "weight", "trim tip", "RPI cost", "size", "gyros and batteries", "nothing" };
        private int anaOverlay;
        private bool anaDetails, anaKeyLeft, anaKeyRight, anaKeyDown;
        private string anaTipKey;   // the block type the weight trim tip says to take off (lit by the "trim tip" drawing)

        /// <summary>
        /// One grid's stats laid out to read at a glance: headline tiles, then sections (ship, acceleration as bars,
        /// thrust as a diagram, power and cargo, RPI with a bar, weight trim), the long lists folded until opened.
        /// </summary>
        private void AddShipRowsRich(HudModel.AnalyserPanel p, ShipStats a, bool forShow = false)
        {
            bool details = !forShow && anaDetails;   // (the race spotlight's ship page: the short form, nothing to open)
            static string Mass(double kg) => kg >= 1e6 ? $"{kg / 1e6:0.##} kt" : kg >= 1000 ? $"{kg / 1000:0.#} t" : $"{kg:0} kg";
            static string Force(double n) => n >= 1e6 ? $"{n / 1e6:0.##} MN" : n >= 1000 ? $"{n / 1000:0.#} kN" : $"{n:0} N";
            void Row(string label, string value, HudColor? colour = null) => p.Rows.Add((label, value, null, HudColor.White, colour ?? HudColor.White));
            void Head(string title, string summary = "", HudColor? colour = null) => p.Rows.Add(("#" + title, summary, null, HudColor.White, colour ?? HudColor.Grey));

            // RPI numbers (for the tile, the bar and the folded summary).
            var rc = RpiClassInUse;
            double rpi = 0, budget = 0;
            if (rc != null)
            {
                budget = GateSettings.RpiBudgetFor(a.Mass, rc.MinWeight, rc.Budget, rc.Steps.Select(x => (x.Weight, x.Budget)).ToList());
                foreach (var kv in a.BlockCounts) if (rc.Values.TryGetValue(kv.Key, out double cost)) rpi += cost * kv.Value;
            }
            bool rpiOk = budget <= 0 || rpi <= budget + 1e-9;
            double accel = a.Mass > 0 ? a.Thrust[0] / a.Mass : 0;
            double best = BestTopSpeed(a.Name);

            // Headline numbers.
            p.CardSmall = true;
            p.Card = new List<(string, string, HudColor)>
            {
                ("MASS", Mass(a.Mass), HudColor.White),
                ("TOP SPEED", best > 1 ? GameOptions.Speed(best) : "-", HudColor.White),
                ($"0 TO {GameOptions.Speed(100)}", accel > 0 ? $"{100 / accel:0.0} s" : "-", HudColor.White),
                ("RPI", rc == null ? "no class" : budget > 0 ? $"{rpi:0.##} / {budget:0.##}" : $"{rpi:0.##}", rc == null ? HudColor.Grey : rpiOk ? HudColor.Green : HudColor.Red),
            };

            Head("SHIP");
            Row("SIZE (W x H x L)", $"{a.SizeX:0} x {a.SizeY:0} x {a.SizeZ:0} m");
            Row("BLOCKS / PCU", $"{a.Blocks} / {a.Pcu}");
            Row("DIRECTIONS FROM", a.CockpitAxes ? "the cockpit" : "the grid (no cockpit found)");
            Row("CENTRE OF MASS", $"{a.ComOffset:0.0} m from middle");

            Head("ACCELERATION", "flat out forward, no drag");
            AddSpeedRows(p, a, null, true);

            Head("THRUST");
            p.ThrustG = new double[6];
            for (int k = 0; k < 6; k++) p.ThrustG[k] = a.Mass > 0 ? a.Thrust[k] / a.Mass / 9.81 : 0;
            Row("@THRUST", "");
            Row("THRUSTERS", a.Thrusters.ToString());
            Row("GYROSCOPES", a.Gyros == 0 ? (a.FromServer ? "none" : "not known here") : $"{a.Gyros}  ({Force(a.GyroTorque)} m)");

            Head("POWER AND CARGO");
            static string Amount(double v, string unit)
            {
                if (unit == "kW" && v >= 1000) return $"{v / 1000:0.##} MW";
                if (unit == "kWh" && v >= 1000) return $"{v / 1000:0.##} MWh";
                if (unit == "L" && v >= 1000) return $"{v / 1000:0.#} kL";
                return $"{v:0.##}" + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            }
            foreach (var kv in a.Output.OrderBy(x => x.Key))
                Row(kv.Key.ToUpperInvariant() + " GENERATED (MAX)", Amount(kv.Value, a.OutputUnits.TryGetValue(kv.Key, out string u) ? u : ""));
            foreach (var kv in a.StoredMax.OrderBy(x => x.Key))
            {
                string unit = a.StoreUnits.TryGetValue(kv.Key, out string u) ? u : "";
                double have = a.StoredNow.TryGetValue(kv.Key, out double n) ? n : 0;
                p.RowBars[p.Rows.Count] = (kv.Value > 0 ? have / kv.Value : 0, HudColor.Green);
                Row(kv.Key.ToUpperInvariant() + " STORED", $"{Amount(have, unit)} / {Amount(kv.Value, unit)}");
            }
            if (a.Output.Count == 0 && a.StoredMax.Count == 0) Row("POWER / HYDROGEN", a.FromServer ? "none" : "not known here");
            if (a.Inventories > 0) p.RowBars[p.Rows.Count] = (a.StorageMax > 0 ? a.StorageUsed / a.StorageMax : 0, HudColor.Cyan);
            Row("CARGO", a.Inventories == 0 ? (a.FromServer ? "none" : "not known here") : $"{Mass(a.StorageUsed)} / {Mass(a.StorageMax)}");

            // RPI: the class rows, then the blocks that cost RPI (folded to the summary until opened), each with a bar.
            Head("RPI", rc == null ? "" : rc.Name);
            int rpiFrom = p.Rows.Count;
            AddRpiRows(p, a, null);
            double mostCost = 0;
            double CostOf(string text)
            {
                int eq = text == null ? -1 : text.LastIndexOf("= ", StringComparison.Ordinal);
                return eq >= 0 && double.TryParse(text.Substring(eq + 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double v) ? v : 0;
            }
            for (int i = rpiFrom; i < p.Rows.Count; i++) if (p.Rows[i].Label.StartsWith("  ")) mostCost = Math.Max(mostCost, CostOf(p.Rows[i].A));
            var rpiRows = p.Rows.GetRange(rpiFrom, p.Rows.Count - rpiFrom);
            p.Rows.RemoveRange(rpiFrom, rpiRows.Count);
            foreach (var r in rpiRows)
            {
                if (r.Label == "RPI CLASS") continue;   // (in the heading)
                bool block = r.Label.StartsWith("  ");
                if (block && !details) continue;
                if (r.Label == "RPI" && budget > 0) p.RowBars[p.Rows.Count] = (Math.Min(1, rpi / budget), rpiOk ? HudColor.Green : HudColor.Red);
                double c = block ? CostOf(r.A) : 0;
                if (mostCost > 0 && c > 0) p.RowBars[p.Rows.Count] = (c / mostCost, HudColor.Yellow);
                p.Rows.Add(r);
            }
            if (!details && !forShow && rpiRows.Any(r => r.Label.StartsWith("  "))) Row("  blocks", "down arrow to list them", HudColor.Grey);

            if (!forShow) AddTrimRows(p, a, !details);
        }

        /// <summary>A grid's stats as rows of the panel (with a second grid beside it when comparing), then its RPI.</summary>
        private void AddShipRows(HudModel.AnalyserPanel p, ShipStats a, ShipStats b, bool trimTips = false)
        {
            static string Mass(double kg) => kg >= 1e6 ? $"{kg / 1e6:0.##} kt" : kg >= 1000 ? $"{kg / 1000:0.#} t" : $"{kg:0} kg";
            static string Force(double n) => n >= 1e6 ? $"{n / 1e6:0.##} MN" : n >= 1000 ? $"{n / 1000:0.#} kN" : $"{n:0} N";
            static string Accel(double n, double kg) => kg > 0 && n > 0 ? $"{n / kg:0.0} m/s/s  {n / kg / 9.81:0.00} g" : "-";
            void Row(string label, Func<ShipStats, string> text, Func<ShipStats, double> value = null, bool lowerBetter = false)
            {
                string av = text(a), bv = b != null ? text(b) : null;
                var col = HudColor.White;
                if (b != null && value != null)
                {
                    double va = value(a), vb = value(b);
                    if (Math.Abs(va - vb) > 1e-6 * Math.Max(1, Math.Abs(va))) col = (vb > va) != lowerBetter ? HudColor.Green : HudColor.Red;
                }
                p.Rows.Add((label, av, bv, col, HudColor.White));
            }
            Row("MASS", s => Mass(s.Mass), s => s.Mass, true);
            Row("SIZE (W x H x L)", s => $"{s.SizeX:0} x {s.SizeY:0} x {s.SizeZ:0} m");
            Row("BLOCKS / PCU", s => $"{s.Blocks} / {s.Pcu}", s => s.Pcu, true);
            Row("DIRECTIONS FROM", s => s.CockpitAxes ? "the cockpit" : "the grid (no cockpit found)");
            AddSpeedRows(p, a, b);
            string[] dirs = { "FORWARD", "BACKWARD", "LEFT", "RIGHT", "UP", "DOWN" };
            for (int k = 0; k < 6; k++)
            {
                int kk = k;
                Row("THRUST " + dirs[k], s => s.Thrust[kk] > 0 ? Force(s.Thrust[kk]) : "none", s => s.Thrust[kk]);
                Row("  acceleration", s => Accel(s.Thrust[kk], s.Mass), s => s.Mass > 0 ? s.Thrust[kk] / s.Mass : 0);
            }
            Row("THRUSTERS", s => s.Thrusters.ToString(), s => s.Thrusters);
            Row("GYROSCOPES", s => s.Gyros == 0 ? (s.FromServer ? "none" : "not known here") : $"{s.Gyros}  ({Force(s.GyroTorque)} m)", s => s.GyroTorque);
            static string Amount(double v, string unit)
            {
                string u = string.IsNullOrEmpty(unit) ? "" : " " + unit;
                if (unit == "kW" && v >= 1000) return $"{v / 1000:0.##} MW";
                if (unit == "kWh" && v >= 1000) return $"{v / 1000:0.##} MWh";
                if (unit == "L" && v >= 1000) return $"{v / 1000:0.#} kL";
                return $"{v:0.##}{u}";
            }
            // What it can generate (power, hydrogen...), then what its batteries and tanks hold.
            var outputs = new SortedSet<string>(a.Output.Keys);
            if (b != null) outputs.UnionWith(b.Output.Keys);
            foreach (var key in outputs)
            {
                string kk = key;
                Row(kk.ToUpperInvariant() + " GENERATED (MAX)",
                    s => s.Output.TryGetValue(kk, out double v) ? Amount(v, s.OutputUnits.TryGetValue(kk, out string u) ? u : "") : "none",
                    s => s.Output.TryGetValue(kk, out double v) ? v : 0);
            }
            var stores = new SortedSet<string>(a.StoredMax.Keys);
            if (b != null) stores.UnionWith(b.StoredMax.Keys);
            foreach (var key in stores)
            {
                string kk = key;
                Row(kk.ToUpperInvariant() + " STORED",
                    s => s.StoredMax.TryGetValue(kk, out double max)
                        ? $"{Amount(s.StoredNow.TryGetValue(kk, out double now2) ? now2 : 0, s.StoreUnits.TryGetValue(kk, out string u) ? u : "")} / {Amount(max, s.StoreUnits.TryGetValue(kk, out string u2) ? u2 : "")}"
                        : "none",
                    s => s.StoredMax.TryGetValue(kk, out double max) ? max : 0);
            }
            if (outputs.Count == 0 && stores.Count == 0)
                Row("POWER / HYDROGEN", s => s.FromServer ? "none" : "not known here");
            Row("CARGO", s => s.Inventories == 0 ? (s.FromServer ? "none" : "not known here") : $"{Mass(s.StorageUsed)} / {Mass(s.StorageMax)}", s => s.StorageMax);
            Row("CENTRE OF MASS", s => $"{s.ComOffset:0.0} m from middle");
            AddRpiRows(p, a, b);
            if (trimTips && b == null) AddTrimRows(p, a);
        }

        /// <summary>
        /// How quickly it gets up to speed: from a standstill to a few speeds (and to its own best top speed from the
        /// session history), flat out forward, with no drag; and whether its up thrust can hold it up here.
        /// </summary>
        private void AddSpeedRows(HudModel.AnalyserPanel p, ShipStats a, ShipStats b, bool bars = false)
        {
            double barTop = Math.Max(500, BestTopSpeed(a.Name));   // the speed a full bar stands for
            double AccelOf(ShipStats st) => st != null && st.Mass > 0 ? st.Thrust[0] / st.Mass : 0;
            string To(ShipStats st, double v)
            {
                double acc = AccelOf(st);
                if (acc <= 0) return "-";
                double t = v / acc, d = v * v / (2 * acc);
                return $"{t:0.0} s  ({(d >= 1000 ? $"{d / 1000:0.0} km" : $"{d:0} m")})";
            }
            void Line(string label, Func<ShipStats, string> text, Func<ShipStats, double> value)
            {
                string av = text(a), bv = b != null ? text(b) : null;
                var col = HudColor.White;
                if (b != null)
                {
                    double va = value(a), vb = value(b);
                    if (va > 0 && vb > 0 && Math.Abs(va - vb) > 1e-6 * va) col = vb < va ? HudColor.Green : HudColor.Red;   // quicker is better
                }
                p.Rows.Add((label, av, bv, col, HudColor.White));
            }
            foreach (double v in new[] { 100.0, 250.0, 500.0 })
            {
                double vv = v;
                if (bars && AccelOf(a) > 0) p.RowBars[p.Rows.Count] = (vv / barTop, HudColor.Cyan);
                Line($"0 TO {GameOptions.Speed(vv)}", st => To(st, vv), st => AccelOf(st) > 0 ? vv / AccelOf(st) : 0);
            }
            double best = BestTopSpeed(a.Name);
            if (best > 1 && bars && AccelOf(a) > 0) p.RowBars[p.Rows.Count] = (best / barTop, HudColor.Purple);
            if (best > 1)
                Line($"0 TO ITS TOP SPEED ({GameOptions.Speed(best)})", st => To(st, best), st => AccelOf(st) > 0 ? best / AccelOf(st) : 0);
            // Lift: up thrust against its weight here.
            if (a.Gravity > 0.05 || (b != null && b.Gravity > 0.05))
            {
                string Hover(ShipStats st)
                {
                    if (st.Gravity <= 0.05 || st.Mass <= 0) return "no gravity";
                    double ratio = st.Thrust[4] / (st.Mass * st.Gravity);
                    return ratio >= 1 ? $"{ratio:0.00} x its weight" : $"CAN'T HOVER ({ratio:0.00} x its weight)";
                }
                double R(ShipStats st) => st.Gravity > 0.05 && st.Mass > 0 ? st.Thrust[4] / (st.Mass * st.Gravity) : 0;
                var colA = R(a) >= 1 || a.Gravity <= 0.05 ? HudColor.White : HudColor.Red;
                var colB = b == null ? HudColor.White : R(b) >= R(a) ? HudColor.Green : HudColor.Red;
                p.Rows.Add(("UP THRUST HERE", Hover(a), b != null ? Hover(b) : null, colB, colA));
            }
        }

        /// <summary>
        /// Weight trimming: how much it could lose (or must gain) for its RPI class, its heaviest block types, and a
        /// tip or two: which blocks to drop to get under the RPI budget, or that fit in the spare weight.
        /// </summary>
        private void AddTrimRows(HudModel.AnalyserPanel p, ShipStats a, bool folded = false)
        {
            anaTipKey = null;
            if (a.BlockMass.Count == 0 || a.Mass <= 0) return;
            var rc = RpiClassInUse;
            double minWeight = 0, budget = 0, rpi = 0;
            if (rc != null)
            {
                var steps = rc.Steps.Select(x => (x.Weight, x.Budget)).ToList();
                minWeight = GateSettings.RpiLowestWeight(rc.MinWeight, steps);
                budget = GateSettings.RpiBudgetFor(a.Mass, rc.MinWeight, rc.Budget, steps);
                foreach (var kv in a.BlockCounts) if (rc.Values.TryGetValue(kv.Key, out double cost)) rpi += cost * kv.Value;
            }
            double Cost(string key) => rc != null && rc.Values.TryGetValue(key, out double c) ? c : 0;
            int Count(string key) => a.BlockCounts.TryGetValue(key, out int n) ? n : 0;
            string Name(string key) => a.BlockInfo.TryGetValue(key, out var info) && !string.IsNullOrWhiteSpace(info.Name) ? info.Name : key.Replace('_', ' ');
            string Kg(double kg) => kg >= 1000 ? $"{kg / 1000:0.0} t" : $"{kg:0} kg";

            double spare = minWeight > 0 ? Math.Round(a.Mass) - minWeight : 0;
            p.Rows.Add(("#WEIGHT TRIM", minWeight > 0 ? (spare >= 0 ? $"{Kg(spare)} spare" : $"{Kg(-spare)} under") : "", null, HudColor.White,
                minWeight > 0 ? (spare >= 0 ? HudColor.Green : HudColor.Red) : HudColor.Grey));
            if (minWeight > 0 && !folded)
                p.Rows.Add(("  SPARE WEIGHT", spare >= 0 ? $"{Kg(spare)} it can lose (min {Kg(minWeight)})" : $"needs {Kg(-spare)} more (min {Kg(minWeight)})",
                    null, HudColor.White, spare >= 0 ? HudColor.Green : HudColor.Red));
            // The heaviest kinds of block.
            var heavy = a.BlockMass.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(6).ToList();
            if (folded && heavy.Count > 0) p.Rows.Add(("  heaviest blocks", "down arrow to list them", null, HudColor.White, HudColor.Grey));
            foreach (var kv in heavy)
            {
                if (folded) break;
                int n = Math.Max(1, Count(kv.Key));
                double each = kv.Value / n, c = Cost(kv.Key);
                bool fits = minWeight > 0 && spare >= each;
                p.RowBars[p.Rows.Count] = (kv.Value / heavy[0].Value, fits ? HudColor.Yellow : HudColor.Grey);
                p.Rows.Add(($"  {Name(kv.Key)}", $"{n} x {Kg(each)} = {Kg(kv.Value)} ({kv.Value / a.Mass * 100:0}%)" + (c != 0 ? $"   RPI {c:0.##} each" : ""),
                    null, HudColor.White, fits ? HudColor.Yellow : HudColor.White));
            }
            // Over the RPI budget: the fewest blocks that get it back under.
            if (rc != null && budget > 0 && rpi > budget + 1e-9)
            {
                double over = rpi - budget;
                var pick = a.BlockCounts.Keys.Where(k => Cost(k) > 0)
                    .Select(k => (Key: k, Need: (int)Math.Ceiling(over / Cost(k) - 1e-9)))
                    .Where(x => x.Need <= Count(x.Key))
                    .OrderBy(x => x.Need).ThenBy(x => a.BlockMass.TryGetValue(x.Key, out double m) ? m / Math.Max(1, Count(x.Key)) : 0)
                    .FirstOrDefault();
                anaTipKey = pick.Key;
                p.Rows.Add(("!TIP", pick.Key != null
                    ? $"{over:0.##} RPI over: take off {pick.Need} x {Name(pick.Key)} (RPI {Cost(pick.Key):0.##} each)"
                    : $"{over:0.##} RPI over: no single block type covers it", null, HudColor.White, HudColor.Yellow));
            }
            // Spare weight: the block that saves the most RPI and still fits in it.
            if (minWeight > 0 && spare > 0)
            {
                var save = a.BlockCounts.Keys.Where(k => Cost(k) > 0 && a.BlockMass.TryGetValue(k, out double m) && m / Math.Max(1, Count(k)) <= spare)
                    .OrderByDescending(Cost).FirstOrDefault();
                if (save != null) anaTipKey ??= save;
                if (save != null)
                    p.Rows.Add(("!TIP", $"Taking off 1 x {Name(save)} keeps it over the minimum and saves {Cost(save):0.##} RPI", null, HudColor.White, HudColor.Yellow));
                else if (heavy.Count > 0)
                    p.Rows.Add(("!TIP", $"Swap heavy blocks for lighter ones: {Kg(spare)} of spare weight to use", null, HudColor.White, HudColor.Yellow));
            }
            else if (minWeight > 0 && spare < 0)
                p.Rows.Add(("!TIP", $"Add {Kg(-spare)}: armour is the cheapest way in RPI", null, HudColor.White, HudColor.Yellow));
        }

        // ---------------------------------------------------------------- autopilot

        private bool autopilot;
        private object apGrid;                 // the ship it flies (kept, so it carries on with you in spectator)
        private Vec3D apSeat;                  // where the seat is on that ship, in its own axes from its middle (the line is the seat's path)
        private List<double[]> apTrace;
        private double apTime, apLastNow, apNoteAt;

        /// <summary>The line the autopilot can fly: the racing line (the ghost's lap), if it has the ship's attitude in it.</summary>
        private List<double[]> AutopilotLine()
        {
            if (timer.Gate == null) return null;
            // A spotlight race being raced: the whole race, lap after lap.
            if (RacingSpotlight != null && spotRaceTrace != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count)) return spotRaceTrace;
            var trace = GhostTrace;
            return trace != null && trace.Count >= 10 && trace[0].Length >= 13 && trace[trace.Count - 1][3] > 1 ? trace : null;
        }

        private void ToggleAutopilot(bool fromStart = false)
        {
            if (autopilot) { StopAutopilot("Autopilot off: the ship has stopped, you have control"); return; }
            if (replaying || replayPending) { Notify("Stop the replay first", HudColor.Yellow); return; }
            // Your ship; or, already in spectator, the ship the orbit camera is on.
            object grid = game.OwnGrid ?? (orbitCam ? ocGrid : null);
            if (grid == null) { Notify("Autopilot: get in your ship's cockpit first (or put the orbit camera on it)", HudColor.Yellow); return; }
            if (!game.TryGetGridPose(grid, out Vec3D gc, out Vec3D gx, out Vec3D gy, out Vec3D gz, out _, out _)) { Notify("Autopilot: couldn't read the ship", HudColor.Yellow); return; }
            // The seat's place on the ship (the line was recorded at the seat): known when you're in it.
            Vec3D seatNow = game.OwnGrid != null && playerPosition.HasValue ? playerPosition.Value : gc;
            Vec3D fromMiddle = seatNow - gc;
            apSeat = new Vec3D(Vec3D.Dot(fromMiddle, gx), Vec3D.Dot(fromMiddle, gy), Vec3D.Dot(fromMiddle, gz));
            var trace = AutopilotLine();
            if (trace == null) { Notify("Autopilot: no racing line on this course yet. Fly a lap first (it follows your best lap, or the ghost you race)", HudColor.Yellow); return; }
            // Join the line where it's nearest.
            int nearest = 0;
            double best = double.MaxValue;
            for (int i = 0; i < trace.Count; i++)
            {
                double d = (new Vec3D(trace[i][0], trace[i][1], trace[i][2]) - seatNow).Length;
                if (d < best) { best = d; nearest = i; }
            }
            // From the start of the line (the ship has just been put there), not the nearest bit of a later lap.
            if (fromStart) { nearest = 0; best = (new Vec3D(trace[0][0], trace[0][1], trace[0][2]) - seatNow).Length; }
            if (best > 500) { Notify($"Autopilot: the racing line is {best / 1000:0.0} km away. Fly closer to it first", HudColor.Yellow); return; }
            apTrace = trace;
            apGrid = grid;
            apTime = trace[nearest][3];
            apLastNow = game.TimeSeconds;
            apNoteAt = 0;
            autopilot = true;
            // Not timed: put the race back to ready, and stop target based gyro fighting the turns.
            timer.Reset();
            results = shipResults = null;
            countdownStart = null;
            game.TargetGyroOff();
            recordsVersion++;
            PluginLog.Info($"Autopilot on, joining the line {best:0} m away at {apTime:0.0} s");
        }

        private void StopAutopilot(string note)
        {
            if (!autopilot) return;
            // The ship stops where it is: left at race speed with nobody flying it, it would fly on into the ground.
            if (apGrid != null) game.SetShipMotion(apGrid, Vec3D.Zero, Vec3D.Zero);
            autopilot = false;
            apTrace = null;
            apGrid = null;
            game.TargetGyroRestore();
            apSettlePending = true;
            timer.Reset();
            recordsVersion++;
            PluginLog.Info("Autopilot off");
            if (note != null) Notify(note, HudColor.Cyan);
        }

        /// <summary>
        /// Every frame: where the line is now (it loops round), and the velocity and spin that take the ship
        /// there: the line's own speed and turn, plus a pull back onto it.
        /// </summary>
        private void UpdateAutopilot()
        {
            // The ship, wherever you are (in its seat, in spectator, filming it with a cinematic). Measured on the
            // server's copy, the one the autopilot moves: the copy you see trails it by a frame or two, and steering by
            // that late picture made the ship wobble.
            if (apGrid == null || !game.TryGetGridPose(apGrid, out Vec3D shipMiddle, out Vec3D cx, out Vec3D cy, out Vec3D cz, out _, out _))
            { StopAutopilot("Autopilot off: the ship is gone"); return; }
            object apServer = game.ServerCopy(apGrid);
            if (apServer != null && game.TryGetGridPose(apServer, out Vec3D sMiddle, out Vec3D sx, out Vec3D sy, out Vec3D sz, out _, out _))
            { shipMiddle = sMiddle; cx = sx; cy = sy; cz = sz; }
            Vec3D seat = shipMiddle + cx * apSeat.X + cy * apSeat.Y + cz * apSeat.Z;
            if (timer.Gate == null) { StopAutopilot("Autopilot off: no course"); return; }
            if (game.IsPaused)
            {
                // Paused (the game's menu): the ship is held still. A save made now keeps it parked rather than
                // flying at race speed, which, loaded later with no autopilot, carried it into the ground (and,
                // fast enough, through the planet: the game then flags the save as broken).
                apLastNow = game.TimeSeconds;
                game.SetShipMotion(apGrid, Vec3D.Zero, Vec3D.Zero);
                return;
            }
            var trace = apTrace;
            double end = trace[trace.Count - 1][3];
            double now = game.TimeSeconds;
            double dt = Math.Clamp(now - apLastNow, 0, 0.1);
            apLastNow = now;
            double rate = Math.Clamp(settings.AutopilotSpeed, 25, 150) / 100 * RecordingScale;   // (slower still at a recording speed under 100%)
            apTime += dt * rate;
            if (apTime >= end) apTime -= end;   // round again
            const double h = 0.1;
            double later = apTime + h;
            if (later >= end) later -= end;
            if (!GhostPose(trace, apTime, out Vec3D p0, out Vec3D[] a0) || a0 == null) return;
            if (!GhostPose(trace, later, out Vec3D p1, out Vec3D[] a1) || a1 == null) return;
            // Through the start of the lap the two points are a whole lap apart: use the line's speed from before.
            Vec3D ahead = later < apTime ? Vec3D.Zero : (p1 - p0) * (rate / h);
            Vec3D error = p0 - seat;
            if (error.Length > 600) { StopAutopilot("Autopilot off: the ship got too far from the line"); return; }
            Vec3D pull = error * 2.5;
            if (pull.Length > 150) pull = pull.Normalized() * 150;
            Vec3D velocity = ahead + pull;

            // Turning: the line's own turn plus a pull round to its attitude.
            Vec3D Twist(Vec3D[] from, Vec3D[] to) =>
                (Vec3D.Cross(from[0], to[0]) + Vec3D.Cross(from[1], to[1]) + Vec3D.Cross(from[2], to[2])) * 0.5;
            Vec3D spin = later < apTime ? Vec3D.Zero : Twist(a0, a1) * (rate / h);
            spin = spin + Twist(new[] { cx, cy, cz }, a0) * 6;
            if (spin.Length > 6) spin = spin.Normalized() * 6;
            if (!game.SetShipMotion(apGrid, velocity, spin)) { StopAutopilot("Autopilot off: it only works in single player or when hosting"); return; }
            // The reminder only while you're in the ship: not over a spectator or cinematic recording.
            if (now >= apNoteAt && !game.InSpectator && !orbitCam)
            {
                apNoteAt = now + 4;
                KeyHint($"AUTOPILOT  {Math.Clamp(settings.AutopilotSpeed, 25, 150):0}%   {settings.GetBinding(GateKey.Autopilot)}: take over", HudColor.Cyan);
            }
        }

        // ---------------------------------------------------------------- session history

        /// <summary>One finished race, as the history keeps it (a line each in History.jsonl).</summary>
        public sealed class HistoryEntry
        {
            public DateTime Date { get; set; }
            public string Course { get; set; }
            public string World { get; set; }
            public string Ship { get; set; }
            public string Pilot { get; set; }
            public int Laps { get; set; }
            public double Total { get; set; }
            public double BestLap { get; set; }
            public double Penalty { get; set; }
            public int Missed { get; set; }
            public int Crashes { get; set; }
            public double TopSpeed { get; set; }
            public List<double> LapTimes { get; set; } = new List<double>();
        }

        private static readonly DateTime sessionStart = DateTime.Now;
        private List<HistoryEntry> history;
        private int historyVersion;
        private string HistoryPath => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "History.jsonl");

        private List<HistoryEntry> History()
        {
            if (history != null) return history;
            history = new List<HistoryEntry>();
            try
            {
                if (File.Exists(HistoryPath) && new FileInfo(HistoryPath).Length > 50L * 1024 * 1024)
                    PluginLog.Info("History.jsonl is over 50 MB: not read (Clear history starts a new one)");
                else if (File.Exists(HistoryPath))
                    foreach (string line in File.ReadLines(HistoryPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { var e = System.Text.Json.JsonSerializer.Deserialize<HistoryEntry>(line); if (e != null) history.Add(e); }
                        catch { /* a broken line: skip it */ }
                    }
            }
            catch (Exception e) { PluginLog.Info("History couldn't be read: " + e.Message); }
            return history;
        }

        private void AddHistory(SpotlightFile race)
        {
            var e = new HistoryEntry
            {
                Date = DateTime.Now, Course = race.Course, World = race.World, Ship = race.Ship, Pilot = race.Pilot,
                Laps = race.Laps.Count, Total = Math.Round(race.Total, 3), TopSpeed = Math.Round(raceTopSpeed, 1),
                BestLap = race.Laps.Count > 0 ? race.Laps.Min(l => l.Time) : 0,
                Penalty = race.Laps.Sum(l => l.Penalty), Missed = race.Laps.Sum(l => l.Missed), Crashes = race.Laps.Sum(l => l.Crashes),
                LapTimes = race.Laps.Select(l => Math.Round(l.Time, 3)).ToList(),
            };
            History().Add(e);
            historyVersion++;
            string line = System.Text.Json.JsonSerializer.Serialize(e) + Environment.NewLine, path = HistoryPath;
            // Written on another thread: a slow disk mustn't hold the game up.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { lock (historyWriteLock) File.AppendAllText(path, line); }
                catch (Exception ex) { PluginLog.Info("History couldn't be saved: " + ex.Message); }
            });
        }
        private static readonly object historyWriteLock = new object();

        /// <summary>The fastest this ship has gone in any race in the history (0 = none).</summary>
        private double BestTopSpeed(string ship)
        {
            if (string.IsNullOrEmpty(ship)) return 0;
            double best = 0;
            foreach (var e in History()) if (string.Equals(e.Ship, ship, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, e.TopSpeed);
            return best;
        }

        private bool historyThisCourse = true, historySessionOnly;

        // Each ship's average race time on a course, for a lap count (from the race history; worked out again only when it changes).
        private string shipAveragesFor;
        private Dictionary<string, (double Average, int Count)> shipAverages = new Dictionary<string, (double, int)>();

        private Dictionary<string, (double Average, int Count)> ShipAverages(string course, int laps)
        {
            string key = $"{historyVersion}|{course}|{laps}";
            if (key == shipAveragesFor) return shipAverages;
            shipAveragesFor = key;
            shipAverages = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
            if (course == null) return shipAverages;
            foreach (var g in History().Where(e => e.Course == course && e.Laps == laps && e.Total > 0 && !string.IsNullOrEmpty(e.Ship)).GroupBy(e => e.Ship, StringComparer.OrdinalIgnoreCase))
                shipAverages[g.Key] = (g.Average(e => e.Total), g.Count());
            return shipAverages;
        }

        private void FillHistoryPanel(HudModel.ControlsPanel p)
        {
            p.HistoryVersion = historyShownVersion;
            p.HistoryThisCourse = historyThisCourse;
            p.HistorySessionOnly = historySessionOnly;
            string course = CurrentCourseName();
            var all = History();
            if (course == null && all.Count > 0) course = all[all.Count - 1].Course;
            p.HistoryCourse = course;
            p.HistoryCount = all.Count;
            // Worked out again only when the history, the filters or the course change (this runs every frame the menu is open).
            // (Also when the race files change: a row whose race has been saved can then be watched.)
            string files = spotFilesCache == null ? "" : spotFilesCache.Count + (spotFilesCache.Count > 0 ? spotFilesCache[0].Path : "");
            string key = $"{historyVersion}|{historyThisCourse}|{historySessionOnly}|{course}|{files}|{lastRace?.Total}";
            if (key != historyRowsFor)
            {
                historyRowsFor = key;
                historyShownVersion++;
                p.HistoryVersion = historyShownVersion;
                historyRows.Clear();
                // The race files there are (course and time), to find each race's file.
                var fileTimes = new List<(string Course, double Total, string Path)>();
                if (spotFilesCache != null)
                    foreach (var f in spotFilesCache)
                    {
                        var read = ReadSpotlightCached(f.Path);
                        if (read != null) fileTimes.Add((read.Course, read.Total, f.Path));
                    }
                // A personal best: quicker than every earlier race of this many laps on this course.
                var bestSoFar = new Dictionary<string, double>();
                foreach (var e in all.OrderBy(x => x.Date))
                {
                    string k = e.Course + "|" + e.Laps;
                    bool pb = !bestSoFar.TryGetValue(k, out double before) || e.Total < before;
                    if (pb) bestSoFar[k] = e.Total;
                    if (historyThisCourse && course != null && e.Course != course) continue;
                    if (historySessionOnly && e.Date < sessionStart) continue;
                    var row = new HudModel.RaceHistoryRow
                    {
                        Date = e.Date, Course = e.Course, Ship = e.Ship, Laps = e.Laps, Total = e.Total, BestLap = e.BestLap,
                        Penalty = e.Penalty, TopSpeed = e.TopSpeed, Best = pb,
                    };
                    row.File = fileTimes.FirstOrDefault(f => f.Course == e.Course && Math.Abs(f.Total - e.Total) < 0.005).Path;
                    row.CanSave = row.File == null && lastRace != null && lastRace.Course == e.Course && Math.Abs(lastRace.Total - e.Total) < 0.005;
                    historyRows.Add(row);
                }
            }
            p.History.AddRange(historyRows);
        }
        private string historyRowsFor;
        private readonly List<HudModel.RaceHistoryRow> historyRows = new List<HudModel.RaceHistoryRow>();
        private int historyShownVersion;

        // ---------------------------------------------------------------- race spotlight

        /// <summary>A finished race, saved to a file to show off the ship that flew it (in any world).</summary>
        public sealed class SpotlightFile
        {
            public string Course { get; set; }
            public string World { get; set; }
            public string Ship { get; set; }
            public string Pilot { get; set; }
            public string Date { get; set; }
            public string Headline { get; set; }
            public double Total { get; set; }
            /// <summary>The race was reset before the finish ("burned"): its last lap is the one it was reset on, as far as it got.</summary>
            public bool Burned { get; set; }
            /// <summary>The course from above for the lap replay page: gates [x, y, kind (0 start, 1 finish, 2 checkpoint)], in 0..1 (y down).</summary>
            public List<double[]> MapGates { get; set; }
            /// <summary>The results graphs (speed and time gap round the shown lap), for the race graphs page. Null in older files.</summary>
            public SpotlightGraph Graph { get; set; }
            /// <summary>The course it was flown on (start gate centre, number of checkpoints), so its race line can be raced and flown by the autopilot there. Null in older files.</summary>
            public double[] GateCenter { get; set; }
            public int Checkpoints { get; set; }
            /// <summary>The ship's shape, for the ghost hologram when the race is raced against. Null in older files.</summary>
            public List<double[]> ShipShape { get; set; }
            /// <summary>For the course card, speed trap and fun facts pages (0 in older files): the course's length round
            /// the fastest lap (m), the height between its lowest and highest points (m), the whole race's distance (m),
            /// the top speed (m/s), on which lap (0-based) and where on the map ([x, y], 0..1).</summary>
            public double CourseLength { get; set; }
            public double HeightRange { get; set; }
            public double Distance { get; set; }
            public double TopSpeed { get; set; }
            public int TopLap { get; set; }
            public double[] TopAt { get; set; }
            public List<SpotlightLap> Laps { get; set; } = new List<SpotlightLap>();
            public List<SpotlightRow> Stats { get; set; } = new List<SpotlightRow>();
        }
        public sealed class SpotlightLap
        {
            public double Time { get; set; }
            public double Flown { get; set; }
            public double Penalty { get; set; }
            public int Missed { get; set; }
            public int Crashes { get; set; }
            /// <summary>The lap's path from above for the lap replay page: [x, y, seconds into the lap], in 0..1 (y down). Null in older files.</summary>
            public List<double[]> Map { get; set; }
            /// <summary>The lap's race line: [x, y, z, seconds into the lap] and the ship's axes (as a ghost has). Null in older files.</summary>
            public List<double[]> Trace { get; set; }
            public List<double?> Splits { get; set; }
            /// <summary>Where the lap's crashes happened on the map ([x, y], 0..1), for the trouble spots page. Null in older files.</summary>
            public List<double[]> CrashSpots { get; set; }
        }
        public sealed class SpotlightGraph
        {
            public List<SpotlightGraphLine> Lines { get; set; } = new List<SpotlightGraphLine>();
            public double Max { get; set; }
            public List<double[]> Gap { get; set; } = new List<double[]>();   // [share of the lap, seconds behind (+) / ahead (-)]
            public string GapAgainst { get; set; }
            public List<int> Missed { get; set; } = new List<int>();
            public string Lap { get; set; }
        }
        public sealed class SpotlightGraphLine
        {
            public string Label { get; set; }
            public int R { get; set; }
            public int G { get; set; }
            public int B { get; set; }
            public List<double[]> Points { get; set; } = new List<double[]>();       // [share of the lap, speed]
            public List<double[]> Checkpoints { get; set; } = new List<double[]>();  // [share, speed, checkpoint number]
        }
        public sealed class SpotlightRow
        {
            public string Label { get; set; }
            public string Value { get; set; }
        }

        private SpotlightFile lastRace;          // the race just finished (for Export)
        private SpotlightFile spotlight;         // the race being shown in spotlight mode (null = plain analyser)
        private string spotlightPath;
        private object spotGrid;
        private ShipStats spotStats;
        private double spotStatsAt, spotPageAt;
        private int spotPage;
        private bool spotLeftDown;
        private List<(string Name, string Info, string Path)> spotFilesCache;
        private long spotFilesAt;
        private List<(string Ship, int Races, bool Hidden)> spotShipsCache;

        /// <summary>A ship's place in the running order set for the leaderboard (ships not in it: after all that are).</summary>
        private int SpotRunIndex(string ship)
        {
            int at = (settings.SpotBoardOrder ?? new List<string>()).FindIndex(x => string.Equals(x, ship, StringComparison.OrdinalIgnoreCase));
            return at < 0 ? int.MaxValue : at;
        }

        private double SpotRevealStep => Math.Clamp(settings.SpotBoardClimbSeconds, 0.3, 6) + 0.4;   // seconds between ships coming onto the leaderboard
        private long spotShipsAt = -1;
        private string SpotlightFolder => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "Spotlights");
        private const string SpotlightExtension = ".spotlight.json";

        /// <summary>
        /// Keeps a race that's about to be reset before its finish ("burned") so it can be exported too: the laps
        /// it finished, then the lap it was reset on as far as it got.
        /// </summary>
        private void RememberBurnedRace()
        {
            try
            {
                if (timer.State != TimerState.Running || timer.InPractice) return;
                double burnNow = game.TimeSeconds;
                double burnLapSeconds = timer.CurrentLapSeconds(burnNow);
                if (timer.Laps.Count == 0 && burnLapSeconds < 3) return;   // (reset on the line: nothing to show)
                var file = new SpotlightFile
                {
                    Course = CurrentCourseName() ?? "Unsaved course", World = game.WorldId, Ship = raceShipName ?? game.ShipName ?? "Ship",
                    Pilot = settings.PilotName, Date = DateTime.Now.ToString("d MMM yyyy  HH:mm"), Headline = "BURNED",
                    Total = timer.RaceSeconds(burnNow), Burned = true,
                };
                for (int li = 0; li < timer.Laps.Count; li++)
                {
                    var doneLap = timer.Laps[li];
                    var doneTrace = li < raceLapTraces.Count ? raceLapTraces[li] : null;
                    file.Laps.Add(new SpotlightLap
                    {
                        Time = doneLap.Time, Flown = doneLap.Flown, Penalty = doneLap.Penalty, Missed = doneLap.MissedCheckpoints, Crashes = doneLap.Crashes,
                        Trace = doneTrace != null && doneTrace.Count >= 2 ? doneTrace : null,
                        Splits = doneLap.Splits?.ToList(),
                    });
                }
                // The lap it was reset on.
                file.Laps.Add(new SpotlightLap
                {
                    Time = burnLapSeconds + timer.CurrentLapPenalty, Flown = burnLapSeconds, Penalty = timer.CurrentLapPenalty,
                    Missed = timer.MissedThisLap, Crashes = timer.CrashesThisLap,
                    Trace = lapTrace != null && lapTrace.Count >= 2 ? new List<double[]>(lapTrace) : null,
                });
                if (timer.Gate != null)
                {
                    file.GateCenter = new[] { timer.Gate.Center.X, timer.Gate.Center.Y, timer.Gate.Center.Z };
                    file.Checkpoints = timer.Checkpoints.Count;
                }
                file.ShipShape = currentShip != null && currentShip.Count > 0 ? currentShip : null;
                lastRace = file;
                recordsVersion++;
                PluginLog.Info($"Race spotlight: burned race kept for export ({file.Laps.Count - 1} lap(s) finished, {LapTimer.Format(file.Total)})");
            }
            catch (Exception e) { PluginLog.Info("Race spotlight: couldn't keep the burned race: " + e.Message); }
        }

        /// <summary>Keeps the race that just finished so it can be exported.</summary>
        private void RememberRace(double total)
        {
            try
            {
                var file = new SpotlightFile
                {
                    Course = CurrentCourseName() ?? "Unsaved course", World = game.WorldId, Ship = raceShipName ?? game.ShipName ?? "Ship",
                    Pilot = settings.PilotName, Date = DateTime.Now.ToString("d MMM yyyy  HH:mm"), Headline = results?.Headline, Total = total,
                };
                for (int li = 0; li < resultLaps.Count; li++)
                {
                    var lap = resultLaps[li];
                    var lapTraceKept = li < raceLapTraces.Count ? raceLapTraces[li] : null;
                    file.Laps.Add(new SpotlightLap
                    {
                        Time = lap.Time, Flown = lap.Flown, Penalty = lap.Penalty, Missed = lap.MissedCheckpoints, Crashes = lap.Crashes,
                        Trace = lapTraceKept != null && lapTraceKept.Count >= 2 ? lapTraceKept : null,
                        Splits = lap.Splits?.ToList(),
                    });
                }
                if (timer.Gate != null)
                {
                    file.GateCenter = new[] { timer.Gate.Center.X, timer.Gate.Center.Y, timer.Gate.Center.Z };
                    file.Checkpoints = timer.Checkpoints.Count;
                }
                file.ShipShape = currentShip != null && currentShip.Count > 0 ? currentShip : null;
                if (raceStats != null)
                    foreach (var row in raceStats) file.Stats.Add(new SpotlightRow { Label = row.Label, Value = row.Value });
                try { file.Graph = GraphOf(results); }
                catch (Exception graphError) { PluginLog.Info("Race spotlight: no graphs kept: " + graphError.Message); }
                try { AddRaceMap(file); }
                catch (Exception mapError) { PluginLog.Info("Race spotlight: no lap map kept: " + mapError.Message); }
                lastRace = file;
                recordsVersion++;
                AddHistory(file);
            }
            catch (Exception e) { PluginLog.Info("Race spotlight: couldn't keep the race: " + e.Message); }
        }

        /// <summary>
        /// Puts the course and each lap's path, seen from above, into the race file (for the lap replay page). The
        /// map's up is the start gate's forward, levelled to its up; everything is scaled into 0..1 with room round it.
        /// </summary>
        private void AddRaceMap(SpotlightFile file)
        {
            if (timer?.Gate == null) return;
            Vec3D up = timer.Gate.Up.Normalized();
            Vec3D e2 = timer.Gate.Forward - up * Vec3D.Dot(timer.Gate.Forward, up);
            if (e2.Length < 0.1) return;
            e2 = e2.Normalized();
            Vec3D e1 = Vec3D.Cross(e2, up).Normalized();
            if (Vec3D.Dot(e1, timer.Gate.Right) < 0) e1 = e1 * -1;   // right on the map = the gate's right
            Vec3D origin = timer.Gate.Center;
            (double U, double V) Flat(Vec3D p) { Vec3D d = p - origin; return (Vec3D.Dot(d, e1), Vec3D.Dot(d, e2)); }

            var gates = new List<(double U, double V, int Kind)> { (0, 0, 0) };
            if (timer.FinishGate != null) { var f = Flat(timer.FinishGate.Center); gates.Add((f.U, f.V, 1)); }
            foreach (var cp in timer.Checkpoints) { var c = Flat(cp.Center); gates.Add((c.U, c.V, 2)); }
            // Each lap's path, a point every quarter second.
            var laps = new List<List<(double U, double V, double T)>>();
            for (int i = 0; i < file.Laps.Count; i++)
            {
                var trace = i < raceLapTraces.Count ? raceLapTraces[i] : null;
                var pts = new List<(double, double, double)>();
                if (trace != null && trace.Count >= 2 && trace[0].Length >= 4)
                {
                    double t0 = trace[0][3], last = double.NegativeInfinity;
                    for (int k = 0; k < trace.Count; k++)
                    {
                        double t = trace[k][3] - t0;
                        if (t - last < 0.25 && k != trace.Count - 1) continue;
                        last = t;
                        var f = Flat(new Vec3D(trace[k][0], trace[k][1], trace[k][2]));
                        pts.Add((f.U, f.V, t));
                    }
                }
                laps.Add(pts);
            }
            if (!laps.Any(x => x.Count >= 2)) return;
            double minU = gates.Min(g => g.U), maxU = gates.Max(g => g.U), minV = gates.Min(g => g.V), maxV = gates.Max(g => g.V);
            foreach (var lap in laps)
                foreach (var pt in lap) { minU = Math.Min(minU, pt.U); maxU = Math.Max(maxU, pt.U); minV = Math.Min(minV, pt.V); maxV = Math.Max(maxV, pt.V); }
            double span = Math.Max(10, Math.Max(maxU - minU, maxV - minV)) * 1.08;
            double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
            double X(double u) => Math.Round((u - cu) / span + 0.5, 4);
            double Y(double v) => Math.Round(0.5 - (v - cv) / span, 4);
            file.MapGates = gates.Select(g => new[] { X(g.U), Y(g.V), g.Kind }).ToList();
            for (int i = 0; i < file.Laps.Count; i++)
                file.Laps[i].Map = laps[i].Count >= 2 ? laps[i].Select(pt => new[] { X(pt.U), Y(pt.V), Math.Round(pt.T, 2) }).ToList() : null;

            // Where each lap's crashes were.
            for (int i = 0; i < file.Laps.Count && i < raceCrashSpots.Count; i++)
                if (raceCrashSpots[i] != null && raceCrashSpots[i].Count > 0)
                    file.Laps[i].CrashSpots = raceCrashSpots[i].Select(c => { var f = Flat(c.At); return new[] { X(f.U), Y(f.V) }; }).ToList();

            // The course card's and speed trap's numbers, from the laps' paths.
            double distance = 0, fastestLapLength = 0, fastestLapTime = double.MaxValue, top = 0, lowest = double.MaxValue, highest = double.MinValue;
            int topLap = 0; Vec3D topAt = origin;
            for (int i = 0; i < file.Laps.Count && i < raceLapTraces.Count; i++)
            {
                var trace = raceLapTraces[i];
                if (trace == null || trace.Count < 2 || trace[0].Length < 4) continue;
                double length = 0;
                for (int k = 1; k < trace.Count; k++)
                {
                    var a = new Vec3D(trace[k - 1][0], trace[k - 1][1], trace[k - 1][2]);
                    var b = new Vec3D(trace[k][0], trace[k][1], trace[k][2]);
                    double step = (b - a).Length, dt = trace[k][3] - trace[k - 1][3];
                    length += step;
                    if (dt > 0.05 && step / dt > top && step / dt < 5000) { top = step / dt; topLap = i; topAt = b; }
                    double height = Vec3D.Dot(b - origin, up);
                    lowest = Math.Min(lowest, height); highest = Math.Max(highest, height);
                }
                distance += length;
                if (file.Laps[i].Time < fastestLapTime) { fastestLapTime = file.Laps[i].Time; fastestLapLength = length; }
            }
            file.Distance = Math.Round(distance);
            file.CourseLength = Math.Round(fastestLapLength);
            file.HeightRange = highest > lowest ? Math.Round(highest - lowest) : 0;
            if (top > 0)
            {
                file.TopSpeed = Math.Round(top, 1);
                file.TopLap = topLap;
                var t = Flat(topAt);
                file.TopAt = new[] { X(t.U), Y(t.V) };
            }
        }

        private static SpotlightGraph GraphOf(HudModel.ResultsPanel res)
        {
            if (res == null || res.Graph.Count == 0 || res.GraphMaxSpeed <= 0) return null;
            var g = new SpotlightGraph { Max = res.GraphMaxSpeed, GapAgainst = res.GapAgainst, Lap = res.GraphLap };
            foreach (var line in res.Graph)
            {
                var l = new SpotlightGraphLine { Label = line.Label, R = line.Colour.R, G = line.Colour.G, B = line.Colour.B };
                foreach (var (x, v) in line.Points) l.Points.Add(new[] { Math.Round(x, 4), Math.Round(v, 1) });
                foreach (var (x, v, n) in line.Checkpoints) l.Checkpoints.Add(new[] { Math.Round(x, 4), Math.Round(v, 1), n });
                g.Lines.Add(l);
            }
            foreach (var (x, gap) in res.Gap) g.Gap.Add(new[] { Math.Round(x, 4), Math.Round(gap, 3) });
            g.Missed.AddRange(res.MissedCheckpoints);
            return g;
        }

        // The race graphs page's panel, made once per race file (the HUD only redraws the graphs when it changes).
        private HudModel.ResultsPanel spotGraphPanel;
        private string spotGraphFor;

        private HudModel.ResultsPanel SpotGraphPanel()
        {
            if (spotlight?.Graph == null) return null;
            if (spotGraphPanel != null && spotGraphFor == spotlightPath) return spotGraphPanel;
            var g = spotlight.Graph;
            var res = new HudModel.ResultsPanel { GraphMaxSpeed = g.Max, GapAgainst = g.GapAgainst, GraphLap = g.Lap, GraphsShown = 2 };
            foreach (var l in g.Lines ?? new List<SpotlightGraphLine>())
            {
                var line = new HudModel.GraphLine { Label = l.Label, Colour = new HudColor((byte)l.R, (byte)l.G, (byte)l.B) };
                foreach (var pt in l.Points ?? new List<double[]>()) if (pt != null && pt.Length >= 2) line.Points.Add((pt[0], pt[1]));
                foreach (var c in l.Checkpoints ?? new List<double[]>()) if (c != null && c.Length >= 3) line.Checkpoints.Add((c[0], c[1], (int)c[2]));
                res.Graph.Add(line);
            }
            foreach (var gp in g.Gap ?? new List<double[]>()) if (gp != null && gp.Length >= 2) res.Gap.Add((gp[0], gp[1]));
            if (g.Missed != null) res.MissedCheckpoints.AddRange(g.Missed);
            spotGraphPanel = res.Graph.Count > 0 ? res : null;
            spotGraphFor = spotlightPath;
            return spotGraphPanel;
        }

        /// <summary>The laps the lap replay page can show (index into the file's laps), and how long each one plays for.</summary>
        private List<int> ReplayLaps(SpotlightFile file) =>
            Enumerable.Range(0, file.Laps.Count).Where(i => file.Laps[i].Map != null && file.Laps[i].Map.Count >= 2).ToList();
        private static double LapPlaySeconds(SpotlightLap lap) => Math.Clamp(lap.Time / 6, 5, 12);
        private const double LapHoldSeconds = 1.5;   // the finished lap stays up this long before the next one

        // The list on the Race tab is refreshed every few seconds: a file is only read again when it has changed.
        private readonly Dictionary<string, (DateTime Written, long Size, SpotlightFile File)> spotReadCache = new Dictionary<string, (DateTime, long, SpotlightFile)>();

        private SpotlightFile ReadSpotlightCached(string path)
        {
            var info = new FileInfo(path);
            if (spotReadCache.TryGetValue(path, out var had) && had.Written == info.LastWriteTimeUtc && had.Size == info.Length) return had.File;
            var file = ReadSpotlight(path);
            if (spotReadCache.Count > 100) spotReadCache.Clear();
            spotReadCache[path] = (info.LastWriteTimeUtc, info.Length, file);
            return file;
        }

        private static SpotlightFile ReadSpotlight(string path)
        {
            try
            {
                var file = System.Text.Json.JsonSerializer.Deserialize<SpotlightFile>(SafeRead.Text(path));
                if (file != null)
                {
                    // (Someone else's file: nothing absurd.)
                    file.Laps ??= new List<SpotlightLap>();
                    if (file.Laps.Count > 500) file.Laps.RemoveRange(500, file.Laps.Count - 500);
                    foreach (var lapCap in file.Laps) if (lapCap?.Map != null && lapCap.Map.Count > 20000) lapCap.Map = null;
                    foreach (var lapCap in file.Laps)
                        if (lapCap?.Trace != null && (lapCap.Trace.Count > 200000 || lapCap.Trace.Any(pt => pt == null || pt.Length < 4 || pt.Any(v => !double.IsFinite(v)))))
                            lapCap.Trace = null;
                    if (file.ShipShape != null && file.ShipShape.Count > 20000) file.ShipShape = null;
                    if (file.TopAt != null && (file.TopAt.Length < 2 || !double.IsFinite(file.TopAt[0]) || !double.IsFinite(file.TopAt[1]))) file.TopAt = null;
                    foreach (var lapCap in file.Laps)
                        if (lapCap?.CrashSpots != null && (lapCap.CrashSpots.Count > 500 || lapCap.CrashSpots.Any(c => c == null || c.Length < 2))) lapCap.CrashSpots = null;
                    if (file.GateCenter != null && (file.GateCenter.Length < 3 || file.GateCenter.Any(v => !double.IsFinite(v)))) file.GateCenter = null;
                    file.Laps.RemoveAll(x => x == null);
                    if (file.Stats != null && file.Stats.Count > 200) file.Stats.RemoveRange(200, file.Stats.Count - 200);
                    if (file.MapGates != null && file.MapGates.Count > 2000) file.MapGates = null;
                    if (file.Graph?.Lines != null && (file.Graph.Lines.Count > 10 || file.Graph.Lines.Any(x => x?.Points != null && x.Points.Count > 20000))) file.Graph = null;
                }
                if (file == null) return null;
                file.Laps ??= new List<SpotlightLap>();
                file.Stats ??= new List<SpotlightRow>();
                return file;
            }
            catch (Exception e) { PluginLog.Info("Race spotlight file couldn't be read (" + path + "): " + e.Message); return null; }
        }

        // ---- Comparing two races: a second race file shown head to head with the one in the spotlight.
        private string spotComparePath;
        private SpotlightFile spotCompare;
        private List<double[]> spotCompareTrace;   // its laps joined into one line (times carried on), like spotRaceTrace

        private void SetSpotCompare(string path, SpotlightFile file = null)
        {
            spotComparePath = file != null ? path : null;
            spotCompare = file;
            spotCompareTrace = null;
            if (file != null)
            {
                bool Flyable(SpotlightLap l) => l.Trace != null && l.Trace.Count >= 10 && l.Trace[0].Length >= 4;
                if (file.Laps.Count > 0 && file.Laps.All(Flyable))
                {
                    var whole = new List<double[]>();
                    double offset = 0;
                    foreach (var lap in file.Laps)
                    {
                        foreach (var pt in lap.Trace) { var copy = (double[])pt.Clone(); copy[3] += offset; whole.Add(copy); }
                        offset += Math.Max(lap.Flown, lap.Trace[lap.Trace.Count - 1][3]);
                    }
                    spotCompareTrace = whole;
                }
            }
            recordsVersion++;
        }

        /// <summary>The compared race, when it was flown on the same course as the one in the spotlight.</summary>
        private SpotlightFile SpotCompareNow() =>
            spotlight != null && spotCompare != null && !ReferenceEquals(spotlight, spotCompare)
            && string.Equals(spotlight.Course, spotCompare.Course, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(spotlightPath, spotComparePath, StringComparison.OrdinalIgnoreCase) ? spotCompare : null;

        private List<double[]> spotRaceTrace;
        private readonly List<double> spotRaceLapStarts = new List<double>();   // where each lap starts in spotRaceTrace (its times)   // a spotlight race's laps joined into one line, for the autopilot to fly the whole race

        /// <summary>
        /// Races an exported race: its best lap becomes the ghost and racing line, and the autopilot flies the
        /// whole race (every lap, one after another), so it can be filmed again with cinematics.
        /// </summary>
        private bool RaceSpotlight(string path, bool notify = true)
        {
            void Say(string text, bool ok = false) { if (notify) Notify(text, ok ? HudColor.Cyan : HudColor.Yellow); else controlsNote = text; }
            var file = path != null && InFolder(path, SpotlightFolder) ? ReadSpotlight(path) : null;
            if (file == null) { Say("Couldn't read that race file"); return false; }
            bool Flyable(SpotlightLap l) => l.Trace != null && l.Trace.Count >= 10 && l.Trace[0].Length >= 13;
            var flyable = file.Laps.Where(Flyable).ToList();
            if (flyable.Count == 0 || file.GateCenter == null) { Say("That race has no race line in it: races exported with this version have one"); return false; }
            var best = flyable.OrderBy(l => l.Time).First();
            var ghost = new GhostFile
            {
                Pilot = file.Pilot, Ship = file.Ship, Course = file.Course, Date = DateTime.Now, LapTime = best.Time,
                GateCenter = file.GateCenter, Checkpoints = file.Checkpoints, Splits = best.Splits ?? new List<double?>(),
                Trace = best.Trace, ShipShape = file.ShipShape, Path = "spot:" + path,
            };
            if (!ghost.Fits(timer.Gate, timer.Checkpoints.Count))
            { Say($"Load the course that race was flown on first ({file.Course}): its start gate and checkpoints have to match"); return false; }
            // The whole race as one line: each lap's times carried on from the end of the lap before.
            List<double[]> whole = null;
            spotRaceLapStarts.Clear();
            if (flyable.Count == file.Laps.Count && file.Laps.Count > 1)
            {
                whole = new List<double[]>();
                double offset = 0;
                foreach (var lap in file.Laps)
                {
                    spotRaceLapStarts.Add(lap.Trace[0][3] + offset);
                    foreach (var pt in lap.Trace) { var copy = (double[])pt.Clone(); copy[3] += offset; whole.Add(copy); }
                    offset += Math.Max(lap.Flown, lap.Trace[lap.Trace.Count - 1][3]);
                }
            }
            raceGhost = ghost; racePossible = false; raceBestRace = false;
            spotRaceTrace = whole;
            recordsVersion++;
            Say($"Racing {file.Ship}'s race ({file.Date}): the ghost and racing line show its best lap ({LapTimer.Format(best.Time)}); "
                + $"the autopilot ({settings.GetBinding(GateKey.Autopilot)}) flies {(whole != null ? $"the whole race, all {file.Laps.Count} laps" : "that lap")}, ready to film with cinematics", true);
            return true;
        }

        /// <summary>The spotlight race being raced (its file), or null.</summary>
        private string RacingSpotlight => raceGhost?.Path != null && raceGhost.Path.StartsWith("spot:") ? raceGhost.Path.Substring(5) : null;

        private void StartSpotlight(string path, bool forceRace = false)
        {
            var file = path != null && InFolder(path, SpotlightFolder) ? ReadSpotlight(path) : null;
            if (file == null) { controlsNote = "Couldn't read that race file"; return; }
            if (replaying) { controlsNote = "Stop the replay first"; return; }
            if (analyserMode) SetAnalyserMode(false, playerPosition);
            spotlight = file;
            spotlightPath = path;
            spotGrid = null; spotStats = null; spotPage = 0; spotLeftDown = true;
            SetAnalyserMode(true, playerPosition);
            game.CloseControlsMenu();
            // Straight onto your own ship (left click still moves it to another grid). With auto race on, the camera
            // waits: the ship is put at the start and the autopilot takes it while you're still in the seat (in
            // spectator the game no longer says which ship is yours, nor lets its gyro be switched off).
            var auto = SpotAutoFor(path);
            if (forceRace && !auto.Race) auto = new GateSettings.SpotAuto { Race = true, Cinematic = auto.Cinematic, Grid = auto.Grid };   // (the tour flies every race)
            object own = game.OwnGrid;
            bool cineComing = !string.IsNullOrEmpty(auto.Cinematic);
            if (own != null) SpotOn(own, game.TimeSeconds, cineComing || auto.Race);
            spotCameraAfterRace = auto.Race && !cineComing;
            // What this race starts by itself: the autopilot flying it from the start, and a cinematic.
            spotAutoRaceAt = null; spotAutoCineAt = null; spotAutoCine = null; spotTeleportedAt = null;
            if (auto.Race)
            {
                if (own == null) { Notify("Auto race: sit in the ship's cockpit first", HudColor.Yellow); spotCameraAfterRace = false; }
                else
                {
                    // (The racing line the race takes over, put back when the spotlight's closed.)
                    var before = (raceGhost, racePossible, raceBestRace, spotRaceTrace);
                    if (RaceSpotlight(path, true)) { spotAutoRaceAt = game.TimeSeconds + 0.2; spotLineBefore ??= before; }
                    else spotCameraAfterRace = false;
                }
                if (!spotCameraAfterRace && own != null && !cineComing) SpotCamera(own);
            }
            if (!string.IsNullOrEmpty(auto.Cinematic))
            {
                spotAutoCine = auto.Cinematic;
                // (After the ship's been put at the start and the autopilot has set off; with no race, after the same wait.)
                spotAutoCineAt = game.TimeSeconds + (spotAutoRaceAt.HasValue ? 1.2 : 0.3 + Math.Clamp(settings.SpotlightStartDelay, 0, 30));
            }
        }

        private double? spotAutoRaceAt, spotAutoCineAt, spotTeleportedAt;
        private string spotAutoCine;
        private bool spotStartedAutopilot, spotCameraAfterRace;

        private GateSettings.SpotAuto SpotAutoFor(string path)
        {
            string key = path == null ? null : Path.GetFileName(path);
            return key != null && settings.SpotlightAuto != null && settings.SpotlightAuto.TryGetValue(key, out var a) && a != null ? a : new GateSettings.SpotAuto();
        }

        /// <summary>Race spotlight's own timed steps: put the ship at the start of the race line, start the autopilot there, start the cinematic.</summary>
        private void UpdateSpotAuto()
        {
            UpdateSpotTour();
            double now = game.TimeSeconds;
            if (spotAutoRaceAt.HasValue && now >= spotAutoRaceAt.Value)
            {
                spotAutoRaceAt = null;
                if (spotTeleportedAt == null)
                {
                    // First the ship goes to where the race began, facing the way it did...
                    // (...where it waits a few seconds, Settings > Race spotlight...)
                    double wait = Math.Clamp(settings.SpotlightStartDelay, 0, 30);
                    if (PutShipAtLineStart())
                    {
                        spotTeleportedAt = now; spotAutoRaceAt = now + 0.4 + wait;
                        if (wait >= 1) KeyHint($"Race starts in {wait:0.#} s", HudColor.Cyan);
                    }
                    else if (spotCameraAfterRace && spotGrid != null) { spotCameraAfterRace = false; SpotCamera(spotGrid); }
                }
                else
                {
                    // ...then the autopilot takes it from there.
                    spotTeleportedAt = null;
                    if (!autopilot) { ToggleAutopilot(true); spotStartedAutopilot = autopilot; }
                    // Now the camera (the current cinematic, if the spotlight plays it), with the ship on its way.
                    if (spotCameraAfterRace && spotGrid != null) SpotCamera(spotGrid);
                    spotCameraAfterRace = false;
                }
            }
            if (spotAutoCineAt.HasValue && now >= spotAutoCineAt.Value && !spotAutoRaceAt.HasValue)
            {
                spotAutoCineAt = null;
                var cineFile = Cinematics().FirstOrDefault(x => x.Name == spotAutoCine);
                object subject = spotGrid ?? game.OwnGrid;
                if (cineFile == null) Notify($"Spotlight: no cinematic called {spotAutoCine} any more", HudColor.Yellow);
                else if (subject != null) StartCinematic(cineFile, subject, -1);
            }
        }

        /// <summary>Teleports your ship to the start of the autopilot's line, turned the way the ship was there.</summary>
        private bool PutShipAtLineStart()
        {
            var trace = AutopilotLine();
            if (trace == null || !GhostPose(trace, trace[0][3], out Vec3D p0, out Vec3D[] a0) || a0 == null) { Notify("Auto race: no race line to fly", HudColor.Yellow); return false; }
            if (!game.TryGetSeatPose(out _, out Vec3D seatFwd, out Vec3D seatUp) || !game.TryGetShipAxes(out Vec3D sx, out Vec3D sy, out Vec3D sz))
            { Notify("Auto race: sit in the ship's cockpit first", HudColor.Yellow); return false; }
            // The seat's facing on the ship, carried over to the ship's attitude at the start of the line.
            Vec3D Carry(Vec3D v) => a0[0] * Vec3D.Dot(v, sx) + a0[1] * Vec3D.Dot(v, sy) + a0[2] * Vec3D.Dot(v, sz);
            if (!game.TeleportShip(p0, Carry(seatFwd), Carry(seatUp))) { Notify("Auto race: couldn't move the ship to the start", HudColor.Yellow); return false; }
            return true;
        }

        // ---------------------------------------------------------------- spectator orbit camera

        private bool orbitCam, ocOwnsSpectator, ocReady, ocFromSpotlight, ocLeftDown, ocRightDown, ocHasAxes, escapeOrbit;
        private object ocGrid;                 // the grid it circles (null = flying free, pick one)
        private long ocWaitMs;
        private double ocDist, ocInputAt, ocLastTime, ocSaveAt = -1;
        private Vec3D ocDir, ocUp, ocWorldUp;  // camera-to-ship direction and up, in the world
        private Vec3D? ocLastCam, ocLastDir, ocFollow;
        private Vec3D ocAx, ocAy, ocAz;        // the ship's axes when the camera was last placed

        private void ToggleOrbitCam()
        {
            if (orbitCam) { StopOrbitCam("Orbit camera off"); return; }
            // Starts on the grid you're looking at, or the ship you're flying.
            object g = game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out _) ? game.LookedAtGrid(eye, dir, 5000, true) : null;
            StartOrbitCam(g ?? game.OwnGrid, false, false);
        }

        /// <summary>Switches to the game's spectator camera (unless you're in it already) and circles this grid (null = pick one).</summary>
        private bool StartOrbitCam(object grid, bool fromSpotlight, bool quiet)
        {
            if (replaying || replayPending) { Notify("Stop the replay first", HudColor.Yellow); return false; }
            if (orbitCam)
            {
                if (grid != null) OrbitTarget(grid, quiet);
                return true;
            }
            if (editMode) SetEditMode(false, playerPosition);
            ocOwnsSpectator = !game.InSpectator;
            if (ocOwnsSpectator)
            {
                if (!game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out Vec3D eyeUp) || !game.StartSpectator(eye, dir, eyeUp))
                {
                    Notify("Couldn't switch to the spectator camera", HudColor.Red);
                    return false;
                }
            }
            orbitCam = true;
            ocReady = !ocOwnsSpectator;
            ocWaitMs = Environment.TickCount64;
            ocFromSpotlight = fromSpotlight;
            ocGrid = null;
            ocLeftDown = ocRightDown = true;   // ignore the click that started it
            game.WatchWheel(settings.OrbitWheel);
            game.TakeWheel();
            recordsVersion++;                  // the Race tab's button says Open / Close
            cineVersion++;   // (the orbit camera's button is on the Cinematics tab)
            PluginLog.Info($"Orbit camera on ({(ocOwnsSpectator ? "switched to spectator" : "already in spectator")})");
            if (grid != null) OrbitTarget(grid, quiet);
            else if (!quiet) KeyHint($"Orbit camera: look at a ship and left click to lock on to it   {settings.GetBinding(GateKey.OrbitCamera)} / Esc: exit", HudColor.Cyan);
            return true;
        }

        private void OrbitTarget(object grid, bool quiet)
        {
            ocGrid = grid;
            ocLastCam = ocLastDir = ocFollow = null;
            ocHasAxes = false;
            if (!quiet)
                KeyHint($"Orbit camera on {game.AnalyseGrid(grid)?.Name ?? "this grid"}. Mouse: swing round   W / S: zoom   Right click: let go   {settings.GetBinding(GateKey.OrbitLock)}: turn with ship", HudColor.Cyan);
        }

        private void StopOrbitCam(string note)
        {
            if (!orbitCam) return;
            orbitCam = false;
            ocGrid = null;
            if (cinePlay != null) { cinePlay = null; cineVersion++; game.SetCameraFov(null); }
            game.ClearSpectatorFollow();   // (the game's lock let go of, whoever owns the spectator)
            if (ocOwnsSpectator) game.EndSpectator();
            game.WatchWheel(false);
            if (ocSaveAt >= 0) { ocSaveAt = -1; settings.Save(settingsPath); }
            recordsVersion++;
            cineVersion++;   // (the orbit camera's button is on the Cinematics tab)
            PluginLog.Info("Orbit camera off");
            if (note != null) Notify(note, HudColor.Cyan);
        }

        private static Vec3D Turn(Vec3D v, Vec3D axis, double radians)
        {
            double c = Math.Cos(radians), sn = Math.Sin(radians);
            return v * c + Vec3D.Cross(axis, v) * sn + axis * (Vec3D.Dot(axis, v) * (1 - c));
        }

        /// <summary>
        /// The orbit camera, on the game's spectator. Like the replay's orbit camera, the player's own spectator
        /// controls drive it: each frame we read how the game moved and turned the spectator since we last placed
        /// it (mouse = swing round the ship, W / S = closer / further, A / D = swing sideways), then put it back at
        /// that direction and distance from where the ship is now, aiming at it.
        /// </summary>
        // Where the cameras think the filmed ship is: carried along at the ship's own speed, then eased onto where the
        // game says it is, so a fast ship (an autopilot lap) is kept up with and its small position corrections don't
        // shake the picture. (Not led ahead by a frame: the game's frame times vary, and a lead that varies with them
        // made the picture jolt.)
        private object trackGrid;
        private Vec3D? trackAt;
        private double trackTime;

        private Vec3D TrackedCentre(object grid, Vec3D centre)
        {
            double now = game.TimeSeconds;
            double dt = Math.Clamp(now - trackTime, 0, 0.1);
            trackTime = now;
            Vec3D vel = game.TryGetGridVelocity(grid, out Vec3D v) ? v : Vec3D.Zero;
            Vec3D target = centre;
            if (!ReferenceEquals(grid, trackGrid) || trackAt == null || dt <= 0 || (trackAt.Value - target).Length > 50 + vel.Length * 0.5)
            {
                trackGrid = grid;
                trackAt = target;
                return target;
            }
            Vec3D carried = trackAt.Value + vel * dt;
            Vec3D eased = carried + (target - carried) * (1 - Math.Exp(-dt * 12));
            trackAt = eased;
            return eased;
        }

        private void UpdateOrbitCam()
        {
            if (!ocReady)
            {
                // The spectator takes a moment to appear.
                if (game.InSpectator) ocReady = true;
                else
                {
                    if (Environment.TickCount64 - ocWaitMs > 6000) StopOrbitCam("Couldn't switch to the spectator camera (is spectator allowed in this world?)");
                    return;
                }
            }
            if (!game.InSpectator) { StopOrbitCam("Orbit camera off"); return; }   // left the spectator with the game's own key
            CheckCineTriggers();
            if (cinePlay != null)
            {
                // A cinematic is playing: its timeline places the camera, not the mouse.
                game.TakeWheel();
                ocLeftDown = ocRightDown = true;
                UpdateCinematic();
                return;
            }

            if (!controlsOpen && keys.WasPressed(GateKey.OrbitLock))
            {
                settings.OrbitLocked = !settings.OrbitLocked;
                settings.Save(settingsPath);
                Notify(settings.OrbitLocked ? "Orbit camera: turns with the ship" : "Orbit camera: keeps its angle", HudColor.Cyan);
            }

            // Clicks (only on the frame the button goes down). The plain analyser and the spotlight use left click
            // themselves; the plain analyser uses right click too.
            bool focus = game.HasFocus && !controlsOpen && !game.IsPaused;
            bool left = focus && game.IsKeyDown(0x01), right = focus && game.IsKeyDown(0x02);
            if (left && !ocLeftDown && !analyserMode && game.TryGetLookRay(out Vec3D eye, out Vec3D look, out _, out _))
            {
                object g = game.LookedAtGrid(eye, look, 5000, true);
                if (g != null && !ReferenceEquals(g, ocGrid)) OrbitTarget(g, false);
            }
            if (right && !ocRightDown && ocGrid != null && (!analyserMode || spotlight != null))
            {
                ocGrid = null;
                Notify("Orbit camera: let go. Fly where you like; look at a ship and left click to lock on again", HudColor.Cyan);
            }
            ocLeftDown = left; ocRightDown = right;

            // Ctrl + mouse wheel: the auto rotate speed, a degree a second per notch (below 0 it circles the other way).
            int wheel = game.TakeWheel();
            if (wheel != 0 && focus && game.IsKeyDown(0x11))
            {
                double rate = Math.Clamp(Math.Round(settings.OrbitAutoRotate + wheel / 120.0), -45, 45);
                if (rate != settings.OrbitAutoRotate)
                {
                    settings.OrbitAutoRotate = rate;
                    ocSaveAt = game.TimeSeconds + 1.5;      // saved once the wheel has stopped
                    ocInputAt = game.TimeSeconds - 10;      // circle straight away, to show the new speed
                }
                Notify(rate == 0 ? "Orbit auto rotate: off" : $"Orbit auto rotate: {Math.Abs(rate):0} degrees a second{(rate < 0 ? " (the other way)" : "")}", HudColor.Cyan);
            }
            if (ocSaveAt >= 0 && game.TimeSeconds >= ocSaveAt) { ocSaveAt = -1; settings.Save(settingsPath); }
            if (ocGrid == null) return;

            if (!game.TryGetGridPose(ocGrid, out Vec3D centre, out Vec3D ax, out Vec3D ay, out Vec3D az, out double radius, out Vec3D gravityDown))
            {
                ocGrid = null;
                Notify("Orbit camera: that grid is gone. Look at another and left click", HudColor.Yellow);
                return;
            }
            if (!game.TryGetSpectatorView(out Vec3D cam, out Vec3D fwd, out Vec3D camUp) || fwd.Length < 0.5) return;
            fwd = fwd.Normalized();
            Vec3D shipMoved = ocFollow.HasValue && !settings.CineGameLock ? TrackedCentre(ocGrid, centre) - centre : Vec3D.Zero;
            centre = centre + shipMoved;
            double now = game.TimeSeconds;
            double dt = Math.Clamp(now - ocLastTime, 0, 0.1);
            ocLastTime = now;
            double minDist = Math.Max(3, radius * 1.1);

            if (ocLastCam == null || ocLastDir == null)
            {
                // Start from where the camera is now, at a distance that shows the whole ship.
                Vec3D from = cam - centre;
                ocDist = Math.Clamp(from.Length, Math.Max(minDist, radius * 2.5), Math.Max(radius * 6, 80));
                ocDir = from.Length > 0.01 ? (from * -1).Normalized() : fwd;
                ocWorldUp = camUp.Length > 0.5 ? camUp.Normalized() : new Vec3D(0, 1, 0);
                ocUp = ocWorldUp;
                ocFollow = centre;
                ocInputAt = now;
            }
            else
            {
                Vec3D lastDir = ocLastDir.Value;
                Vec3D moved = cam - ocLastCam.Value;
                double along = Vec3D.Dot(moved, lastDir);
                Vec3D side = moved - lastDir * along;
                if (moved.Length > 0.01 || Vec3D.Dot(fwd, lastDir) < 0.999999) ocInputAt = now;
                ocDist = Math.Clamp(ocDist - along, minDist, Math.Max(3000, minDist * 3));         // W = closer, S = further
                Vec3D offset = fwd * -ocDist + side;                          // mouse turn, and sideways keys swing it round too
                ocDir = offset.Length > 0.01 ? (offset * -1).Normalized() : fwd;
                if (settings.OrbitLocked && ocHasAxes)
                {
                    // Turns with the ship: carry the direction and up along with how the ship turned since last frame.
                    Vec3D Carry(Vec3D v) => (ax * Vec3D.Dot(v, ocAx) + ay * Vec3D.Dot(v, ocAy) + az * Vec3D.Dot(v, ocAz)).Normalized();
                    ocDir = Carry(ocDir);
                    ocUp = Carry(ocUp);
                }
            }
            if (gravityDown.Length > 0.5) ocWorldUp = gravityDown * -1;
            if (!settings.OrbitLocked) ocUp = ocWorldUp;

            // Left alone for 3 seconds: circle the ship slowly.
            // (Not during a race spotlight: the camera holds still on the ship there, and the mouse still swings it round.)
            if (settings.OrbitAutoRotate != 0 && now - ocInputAt > 3 && !game.IsPaused && spotlight == null)
                ocDir = Turn(ocDir, ocUp, settings.OrbitAutoRotate * dt * RecordingScale * Math.PI / 180).Normalized();

            // Smooth follow: the point the camera hangs off eases after the ship, never further behind than a part of the distance.
            double loose = Math.Clamp(settings.OrbitSmooth, 0, 100) / 100;
            if (loose < 0.01 || ocFollow == null) ocFollow = centre;
            else
            {
                // (Carried along with the ship first, so only its changes of speed and direction are smoothed, not the speed itself.)
                Vec3D carriedFollow = ocFollow.Value + (game.TryGetGridVelocity(ocGrid, out Vec3D ocVel) ? ocVel * dt : Vec3D.Zero);
                Vec3D f = carriedFollow + (centre - carriedFollow) * Math.Min(1, dt * (12 - 9 * loose));
                Vec3D lag = f - centre;
                double maxLag = ocDist * 0.35 * loose;
                if (lag.Length > maxLag) f = centre + lag.Normalized() * maxLag;
                ocFollow = f;
            }

            Vec3D up = ocUp - ocDir * Vec3D.Dot(ocUp, ocDir);
            if (up.Length < 0.05) up = Vec3D.Cross(ocDir, Math.Abs(Vec3D.Dot(ocDir, ax)) < 0.9 ? ax : ay);
            up = up.Normalized();
            Vec3D newCam = ocFollow.Value - ocDir * ocDist;
            if (settings.CineGameLock && game.SetSpectatorFollow(ocGrid, newCam, ocDir, up)) { }
            else
            {
                game.ClearSpectatorFollow();
                if (!game.SetSpectatorView(newCam, ocDir, up, false)) { StopOrbitCam("Orbit camera off (the spectator camera went away)"); return; }
            }
            ocLastCam = newCam;
            ocLastDir = ocDir;
            ocAx = ax; ocAy = ay; ocAz = az;
            ocHasAxes = true;
        }

        // ---------------------------------------------------------------- cinematics (camera timelines)

        /// <summary>
        /// One shot of a cinematic. A view is 9 numbers: the camera's position, direction and up. "Ship" views are
        /// in the target ship's own axes, measured from its middle, so they fit any ship anywhere.
        /// </summary>
        public sealed class CineShot
        {
            public string Type { get; set; } = "Orbit";     // Orbit, Chase, Fixed, Move
            public double Seconds { get; set; } = 8;
            public string Target { get; set; }               // the ship's name (null = no ship: world positions only)
            public double[] View { get; set; }               // in the world
            public double[] ViewShip { get; set; }
            public double[] View2 { get; set; }              // Move: where it ends
            public double[] View2Ship { get; set; }
            public double Dist { get; set; }                 // Orbit: how far out
            public double RotateSpeed { get; set; } = 6;     // Orbit: degrees a second
            public bool Track { get; set; }                  // Fixed / Move: keep turning to look at the ship
            public bool Relative { get; set; }               // Fixed / Move / Path: placed from the ship (where it is when the shot starts), not in the world
            /// <summary>Path: the camera's keyframes, in order (world views, and the same from the ship when there was one).</summary>
            public List<double[]> Keys { get; set; } = new List<double[]>();
            public List<double[]> KeysShip { get; set; } = new List<double[]>();
            /// <summary>Seconds this shot blends in from where the camera was at the end of the shot before (0 = a cut).</summary>
            public double Blend { get; set; }
        }

        public sealed class CinematicFile
        {
            public string Name { get; set; }
            public bool Loop { get; set; } = true;
            /// <summary>Every shot films the ship picked when it's played, whatever ship it was made on.</summary>
            public bool FollowPicked { get; set; } = true;
            public List<CineShot> Shots { get; set; } = new List<CineShot>();
            /// <summary>The effects layer: changes laid over the shots at their own times.</summary>
            public List<CineEffect> Effects { get; set; } = new List<CineEffect>();
            /// <summary>How many effect layers it has (effects on different layers can run at the same time).</summary>
            public int Lanes { get; set; } = 1;
            /// <summary>How long the whole timeline runs (0 = as long as its shots). Shots short of it: the last one holds; past it: cut.</summary>
            public double TimelineLength { get; set; }
        }

        /// <summary>
        /// How long shot i plays for in the whole cinematic: its own length, except with a set timeline length the
        /// last shot keeps going to the end and anything past the end is cut (0 = it doesn't play at all).
        /// </summary>
        private static double CineShotLength(CinematicFile file, int i)
        {
            if (i < 0 || i >= file.Shots.Count) return 0;
            double seconds = file.Shots[i].Seconds;
            if (file.TimelineLength <= 0) return seconds;
            double before = 0;
            for (int k = 0; k < i; k++) before += file.Shots[k].Seconds;
            if (i == file.Shots.Count - 1 && file.TimelineLength > before + seconds) return file.TimelineLength - before;
            return Math.Clamp(file.TimelineLength - before, 0, seconds);
        }

        /// <summary>The playing shot's length: its own when previewing just that shot, else as the timeline has it.</summary>
        private double CinePlayLength(CinematicFile file, int i) => cineOnlyShot >= 0 && i >= 0 && i < file.Shots.Count ? file.Shots[i].Seconds : CineShotLength(file, i);

        /// <summary>
        /// Something laid over the shots from Start for Seconds (timeline time). Fov: the field of view goes From -> To
        /// degrees. Roll: the picture tilts From -> To degrees. Shake: a hand-held wobble of To degrees.
        /// </summary>
        public sealed class CineEffect
        {
            public string Type { get; set; } = "Fov";
            public double Start { get; set; }
            public double Seconds { get; set; } = 4;
            public double From { get; set; }
            public double To { get; set; }
            /// <summary>Which effect layer it's on (0 = the top one).</summary>
            public int Lane { get; set; }
        }

        /// <summary>
        /// An example cinematic fitted to a ship: a slow orbit reveal, a chase from behind, a low wing camera, a head-on
        /// shot, one from above and a wide orbit to finish, with a fade in, film bars, a lens push and a little shake.
        /// The views are placed from the ship (its cockpit's forward and up, and its size), so they suit any ship.
        /// </summary>
        private CinematicFile MakeShowcase(object grid, string baseName)
        {
            if (grid == null || !game.TryGetGridPose(grid, out _, out _, out _, out _, out _, out _)) return null;
            ShipStats st = null;
            try { st = game.AnalyseGrid(grid); } catch { }
            // The ship's forward and up in its own grid coordinates (the cockpit's, else the grid's -Z and +Y).
            Vec3D F = st?.CockpitForwardGrid ?? new Vec3D(0, 0, -1), U = st?.CockpitUpGrid ?? new Vec3D(0, 1, 0);
            F = F.Normalized(); U = (U - F * Vec3D.Dot(U, F)).Normalized();
            Vec3D R = Vec3D.Cross(F, U).Normalized();
            double size = st != null ? Math.Max(st.SizeX, Math.Max(st.SizeY, st.SizeZ)) : 20;
            double r = Math.Clamp(size, 8, 200);
            string target = game.GridName(grid) ?? "Ship";
            double[] View(Vec3D pos, Vec3D look, Vec3D up)
            {
                Vec3D d = look.Normalized();
                Vec3D u = (up - d * Vec3D.Dot(up, d)).Normalized();
                return Nine(pos, d, u);
            }
            CineShot Orbit(double secs, Vec3D pos, double rotate, double blend) => new CineShot
            {
                Type = "Orbit", Seconds = secs, Target = target, Dist = pos.Length, RotateSpeed = rotate, Blend = blend,
                ViewShip = View(pos, pos * -1, U),
            };
            CineShot Chase(double secs, Vec3D pos, Vec3D look, Vec3D up, double blend) => new CineShot
            {
                Type = "Chase", Seconds = secs, Target = target, Blend = blend, ViewShip = View(pos, look, up),
            };
            var shots = new List<CineShot>
            {
                // 1. Reveal: a slow orbit from the front quarter.
                Orbit(8, F * (1.6 * r) + R * (0.9 * r) + U * (0.35 * r), 12, 0),
                // 2. Chase from behind and above, looking past the ship down the course.
                Chase(6, F * (-2.2 * r) + U * (0.55 * r), F * (6 * r) - (F * (-2.2 * r) + U * (0.55 * r)), U, 1.5),
                // 3. Low wing camera: beside the ship, looking forward along it.
                Chase(5, R * (1.1 * r) + F * (-0.4 * r) + U * (0.05 * r), F * (3 * r) - R * (0.6 * r), U, 1),
                // 4. Head on: in front, looking back at the ship.
                Chase(5, F * (2.6 * r) + U * (0.25 * r), F * (-2.6 * r) - U * (0.25 * r), U, 1),
                // 5. From above, the nose to the top of the picture.
                Chase(4, U * (3 * r) + F * (-0.3 * r), F * (0.6 * r) - U * (3 * r), F, 1.5),
                // 6. A wide orbit from behind to finish.
                Orbit(6, F * (-2.6 * r) + U * (0.9 * r) + R * (0.5 * r), 20, 2),
            };
            double total = shots.Sum(x => x.Seconds);
            var effects = new List<CineEffect>
            {
                new CineEffect { Type = "Fade", Start = 0, Seconds = 1.5, From = 100, To = 0, Lane = 0 },
                new CineEffect { Type = "Fade", Start = total - 1.5, Seconds = 1.5, From = 0, To = 100, Lane = 0 },
                new CineEffect { Type = "Bars", Start = 0, Seconds = 2, From = 0, To = 11, Lane = 1 },
                new CineEffect { Type = "Bars", Start = 2, Seconds = total - 2, From = 11, To = 11, Lane = 1 },
                new CineEffect { Type = "Fov", Start = 8, Seconds = 6, From = 75, To = 55, Lane = 2 },   // push in on the chase
                new CineEffect { Type = "Shake", Start = 14, Seconds = 5, From = 0, To = 0.35, Lane = 2 }, // the wing camera
            };
            string name = baseName;
            int n = 2;
            while (Cinematics().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} {n++}";
            var file = new CinematicFile { Name = name, Loop = true, FollowPicked = true, Shots = shots, Effects = effects, Lanes = 3 };
            Cinematics().Add(file);
            settings.CinematicName = name;
            settings.DefaultShowcaseAdded = true;
            settings.Save(settingsPath);
            SaveCinematic(file);
            PluginLog.Info($"Showcase cinematic '{name}' made for {target} (size {size:0} m)");
            return file;
        }

        private const string CineExtension = ".cinematic.json";
        private string CineFolder => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "Cinematics");
        private string CinePath(CinematicFile file) => Path.Combine(CineFolder, SafeFileName(file.Name) + CineExtension);
        private List<CinematicFile> cinematics;
        private int cineVersion;
        private CinematicFile cinePlay;        // the one playing (null = none)
        private int cineShot = -1, cineOnlyShot = -1;
        private double cineShotAt;
        // The cinematic's own clock: runs at the recording speed (Settings > Cinematics), not always real time.
        private double cineClock, cineClockReal;
        private double CineNow => cineClock;
        private double RecordingScale => Math.Clamp(settings.RecordingSpeed, 25, 100) / 100;
        private object cineMain, cineGrid, cineLastGrid;
        private bool cineHasAnchor;
        // What the mouse has added to the shot that's playing: a turn of the view (an orbit swings round the ship), and zoom.
        private double cineYaw, cinePitch, cineZoom;
        private readonly List<Vec3D> cinePathP = new List<Vec3D>(), cinePathD = new List<Vec3D>(), cinePathU = new List<Vec3D>();
        // Smooth cuts: where the camera was when the last shot ended, to blend from.
        private bool cineBlending;
        private Vec3D cineBlendP, cineBlendD, cineBlendU;
        // Fade to black (0..1) and letterbox bars (0..0.5 of the screen's height each) at this moment.
        private double cineFade, cineBars;

        /// <summary>Catmull-Rom curve through the points, at f along it (0 = the first, Count - 1 = the last).</summary>
        private static Vec3D Curve(List<Vec3D> pts, double f)
        {
            int n = pts.Count;
            if (n == 1) return pts[0];
            f = Math.Clamp(f, 0, n - 1);
            int i = Math.Min((int)Math.Floor(f), n - 2);
            double u = f - i, u2 = u * u, u3 = u2 * u;
            Vec3D p0 = pts[Math.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(n - 1, i + 2)];
            return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u2 + (3 * p1 - p0 - 3 * p2 + p3) * u3);
        }
        private bool cineFovOn;
        private Vec3D? cineLastCam, cineLastDir, cineLastU;
        private Vec3D cineC, cineAx, cineAy, cineAz, cineUp, cineDir0, cineP1, cineD1, cineU1, cineP2, cineD2, cineU2;

        private CinematicFile CineCurrent => Cinematics().FirstOrDefault(x => x.Name == settings.CinematicName);

        private List<CinematicFile> Cinematics()
        {
            if (cinematics != null) return cinematics;
            var found = new List<CinematicFile>();
            try
            {
                if (Directory.Exists(CineFolder))
                    foreach (string path in Directory.GetFiles(CineFolder, "*" + CineExtension))
                    {
                        try
                        {
                            var file = System.Text.Json.JsonSerializer.Deserialize<CinematicFile>(SafeRead.Text(path));
                            if (file == null) continue;
                            if (string.IsNullOrWhiteSpace(file.Name)) file.Name = Path.GetFileName(path).Replace(CineExtension, "");
                            file.Shots ??= new List<CineShot>();
                            file.Shots.RemoveAll(x => x == null);
                            foreach (var shotRead in file.Shots)
                            {
                                shotRead.Keys ??= new List<double[]>();
                                shotRead.KeysShip ??= new List<double[]>();
                                while (shotRead.KeysShip.Count < shotRead.Keys.Count) shotRead.KeysShip.Add(null);
                            }
                            file.Effects ??= new List<CineEffect>();
                            file.Effects.RemoveAll(x => x == null);
                            file.TimelineLength = Math.Clamp(double.IsFinite(file.TimelineLength) ? file.TimelineLength : 0, 0, 3600);
                            // (Someone else's file: nothing absurd.)
                            if (file.Shots.Count > 500) file.Shots.RemoveRange(500, file.Shots.Count - 500);
                            if (file.Effects.Count > 500) file.Effects.RemoveRange(500, file.Effects.Count - 500);
                            foreach (var shotCap in file.Shots)
                            {
                                shotCap.Seconds = Math.Clamp(double.IsFinite(shotCap.Seconds) ? shotCap.Seconds : 8, 0.5, 600);
                                shotCap.Blend = Math.Clamp(double.IsFinite(shotCap.Blend) ? shotCap.Blend : 0, 0, 30);
                                if (shotCap.Keys != null && shotCap.Keys.Count > 1000) shotCap.Keys.RemoveRange(1000, shotCap.Keys.Count - 1000);
                                if (shotCap.KeysShip != null && shotCap.KeysShip.Count > 1000) shotCap.KeysShip.RemoveRange(1000, shotCap.KeysShip.Count - 1000);
                            }
                            foreach (var fxCap in file.Effects)
                            {
                                fxCap.Start = Math.Clamp(double.IsFinite(fxCap.Start) ? fxCap.Start : 0, 0, 3600);
                                fxCap.Seconds = Math.Clamp(double.IsFinite(fxCap.Seconds) ? fxCap.Seconds : 4, 0.5, 600);
                            }
                            foreach (var fxPlace in file.Effects.ToList()) PlaceEffect(file, fxPlace, false);   // (older files could have effects on top of each other)
                            file.Lanes = Math.Clamp(Math.Max(file.Lanes, file.Effects.Count == 0 ? 1 : file.Effects.Max(x => x.Lane) + 1), 1, 8);
                            foreach (var fxRead in file.Effects) fxRead.Lane = Math.Clamp(fxRead.Lane, 0, file.Lanes - 1);
                            if (!found.Any(x => string.Equals(x.Name, file.Name, StringComparison.OrdinalIgnoreCase))) found.Add(file);
                        }
                        catch (Exception e) { PluginLog.Info("Cinematic file couldn't be read (" + path + "): " + e.Message); }
                    }
            }
            catch (Exception e) { PluginLog.Info("Cinematics folder: " + e.Message); }
            cinematics = found.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var loaded in cinematics) cineLastSaved[loaded] = CineSnap(loaded);   // (the first edit can be undone)
            return cinematics;
        }

        // Undo / redo: each cinematic's earlier states (as saved), newest last. Kept while the game runs, not in the file.
        private readonly Dictionary<CinematicFile, List<string>> cineUndo = new Dictionary<CinematicFile, List<string>>();
        private readonly Dictionary<CinematicFile, List<string>> cineRedo = new Dictionary<CinematicFile, List<string>>();
        private readonly Dictionary<CinematicFile, string> cineLastSaved = new Dictionary<CinematicFile, string>();
        private bool cineRestoring;

        private static List<string> CineStack(Dictionary<CinematicFile, List<string>> stacks, CinematicFile file)
        {
            if (!stacks.TryGetValue(file, out var list)) stacks[file] = list = new List<string>();
            return list;
        }

        /// <summary>The cinematic as text, without its name (so a rename isn't an undo step).</summary>
        private static string CineSnap(CinematicFile file)
        {
            string name = file.Name;
            try { file.Name = null; return System.Text.Json.JsonSerializer.Serialize(file); }
            finally { file.Name = name; }
        }

        /// <summary>Puts an earlier state back into the same cinematic (so whatever refers to it still does).</summary>
        private void RestoreCinematic(CinematicFile file, string state)
        {
            var back = System.Text.Json.JsonSerializer.Deserialize<CinematicFile>(state);
            if (back == null) return;
            file.Loop = back.Loop;
            file.FollowPicked = back.FollowPicked;
            file.Shots = back.Shots ?? new List<CineShot>();
            file.Shots.RemoveAll(x => x == null);
            foreach (var shot in file.Shots)
            {
                shot.Keys ??= new List<double[]>();
                shot.KeysShip ??= new List<double[]>();
                while (shot.KeysShip.Count < shot.Keys.Count) shot.KeysShip.Add(null);
            }
            file.Effects = back.Effects ?? new List<CineEffect>();
            file.Effects.RemoveAll(x => x == null);
            file.Lanes = Math.Clamp(back.Lanes, 1, 8);
            file.TimelineLength = Math.Clamp(back.TimelineLength, 0, 3600);
            if (ReferenceEquals(cinePlay, file) && cineShot >= file.Shots.Count) cineShot = -1;
            cineRestoring = true;
            try { SaveCinematic(file); } finally { cineRestoring = false; }
        }

        private void SaveCinematic(CinematicFile file)
        {
            cineVersion++;
            string snap = CineSnap(file);
            if (!cineRestoring && cineLastSaved.TryGetValue(file, out string was) && was != snap)
            {
                var undo = CineStack(cineUndo, file);
                undo.Add(was);
                if (undo.Count > 100) undo.RemoveAt(0);
                CineStack(cineRedo, file).Clear();
            }
            cineLastSaved[file] = snap;
            if (cinePlay != null && cinePlay.Name == file.Name) cinePlay = file;   // edits show in the one that's playing
            try
            {
                Directory.CreateDirectory(CineFolder);
                File.WriteAllText(CinePath(file), System.Text.Json.JsonSerializer.Serialize(file, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { controlsNote = "Couldn't save the cinematic: " + e.Message; }
        }

        private static bool EffectOverlaps(CinematicFile file, CineEffect fx, int lane) =>
            file.Effects.Any(o => o != null && !ReferenceEquals(o, fx) && o.Lane == lane
                && fx.Start < o.Start + o.Seconds - 1e-6 && o.Start < fx.Start + fx.Seconds - 1e-6);

        /// <summary>
        /// Effects on one layer never overlap (one would hide the other). If this one now does: a stretched one
        /// stops where the next one on its layer starts; otherwise it goes to the nearest layer that's free at that
        /// time, or a new layer underneath. Returns a note for the player when it moved (null = it fitted).
        /// </summary>
        private static string PlaceEffect(CinematicFile file, CineEffect fx, bool stretched)
        {
            file.Lanes = Math.Max(1, file.Lanes);
            fx.Lane = Math.Clamp(fx.Lane, 0, file.Lanes - 1);
            if (!EffectOverlaps(file, fx, fx.Lane)) return null;
            if (stretched)
            {
                double next = file.Effects.Where(o => o != null && !ReferenceEquals(o, fx) && o.Lane == fx.Lane && o.Start >= fx.Start - 1e-6)
                    .Select(o => o.Start).DefaultIfEmpty(double.MaxValue).Min();
                if (next - fx.Start >= 0.5)
                {
                    fx.Seconds = Math.Min(fx.Seconds, next - fx.Start);
                    if (!EffectOverlaps(file, fx, fx.Lane)) return "Stopped where the next effect on this layer starts";
                }
            }
            int wanted = fx.Lane;
            for (int step = 1; step < file.Lanes; step++)
                foreach (int lane in new[] { wanted + step, wanted - step })
                    if (lane >= 0 && lane < file.Lanes && !EffectOverlaps(file, fx, lane)) { fx.Lane = lane; return $"That time was taken on layer {wanted + 1}: put on layer {lane + 1}"; }
            if (file.Lanes < 8)
            {
                file.Lanes++;
                fx.Lane = file.Lanes - 1;
                return $"That time was taken on every layer: put on a new layer {file.Lanes}";
            }
            return "No layer is free at that time (8 layers at most): it overlaps another effect";
        }

        private static string CineTypeName(string type) =>
            type == "Fixed" ? "Fixed camera" : type == "Move" ? "Moving camera" : type == "Path" ? "Camera path" : type == "Chase" ? "Chase" : "Orbit";

        private static double[] Nine(Vec3D p, Vec3D d, Vec3D u) => new[] { p.X, p.Y, p.Z, d.X, d.Y, d.Z, u.X, u.Y, u.Z };

        /// <summary>Puts the view the camera has now into a shot (its end view for a moving camera when end is set). Returns what to tell the player.</summary>
        /// <summary>mode: 0 the view (a moving camera's start), 1 a moving camera's end, 2 a new keyframe (after keyIndex, or at the end), 3 keyframe keyIndex replaced.</summary>
        private string CaptureShot(CineShot shot, int mode, int keyIndex = -1)
        {
            bool end = mode == 1, key = mode == 2 || mode == 3;
            if (!game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out Vec3D up)) return "Couldn't read the camera's view";
            // The ship: the one the orbit camera is on, else the one in the crosshair, else the last one used.
            object g = orbitCam && ocGrid != null ? ocGrid : game.LookedAtGrid(eye, dir, 5000, true) ?? spotGrid ?? cineLastGrid ?? game.OwnGrid;
            Vec3D centre = Vec3D.Zero, ax = Vec3D.Zero, ay = Vec3D.Zero, az = Vec3D.Zero;
            bool hasShip = g != null && game.TryGetGridPose(g, out centre, out ax, out ay, out az, out _, out _);
            if (!hasShip && (shot.Type == "Orbit" || shot.Type == "Chase"))
                return "No ship for this shot yet: look at the ship (or put the orbit camera on it), then press its Use current view";
            double[] world = Nine(eye, dir.Normalized(), up.Normalized());
            double[] ship = null;
            if (hasShip)
            {
                Vec3D Local(Vec3D v) => new Vec3D(Vec3D.Dot(v, ax), Vec3D.Dot(v, ay), Vec3D.Dot(v, az));
                Vec3D aim = dir.Normalized();
                if (shot.Type == "Orbit")
                {
                    // An orbit always aims at the middle of the ship.
                    Vec3D to = centre - eye;
                    shot.Dist = Math.Max(3, to.Length);
                    if (to.Length > 0.01) aim = to.Normalized();
                }
                ship = Nine(Local(eye - centre), Local(aim), Local(up.Normalized()));
                shot.Target = game.GridName(g) ?? "Ship";
                cineLastGrid = g;
            }
            else if (!end && !key) shot.Target = null;
            int keyNo = shot.Keys.Count;
            if (key)
            {
                while (shot.KeysShip.Count < shot.Keys.Count) shot.KeysShip.Add(null);
                if (mode == 3 && keyIndex >= 0 && keyIndex < shot.Keys.Count) { shot.Keys[keyIndex] = world; shot.KeysShip[keyIndex] = ship; keyNo = keyIndex; }
                else if (keyIndex >= 0 && keyIndex < shot.Keys.Count) { shot.Keys.Insert(keyIndex + 1, world); shot.KeysShip.Insert(keyIndex + 1, ship); keyNo = keyIndex + 1; }
                else { shot.Keys.Add(world); shot.KeysShip.Add(ship); }
                cineEditKey = keyNo;
            }
            else if (end) { shot.View2 = world; shot.View2Ship = ship; }
            else { shot.View = world; shot.ViewShip = ship; }
            string what = mode == 3 ? $"Keyframe {keyNo + 1} moved here" : key ? $"Keyframe {keyNo + 1} added" : shot.Type == "Move" ? (end ? "End view set" : "Start view set") : "View set";
            return hasShip ? $"{what} (ship: {shot.Target})" : $"{what} (no ship in view: a place in this world)";
        }

        // ---- seeing and moving a shot's views while editing
        private int cineEditShot = -1, cineEditKey = -1;
        private object cinePreviewGrid;
        private string cinePreviewFor;
        private double cinePreviewAt = double.NegativeInfinity;

        /// <summary>A shot's views, in order: a path's keyframes, a moving camera's start and end, or the one view.</summary>
        private static List<(double[] World, double[] Ship, string Name)> ShotViews(CineShot shot)
        {
            var list = new List<(double[], double[], string)>();
            if (shot.Type == "Path")
                for (int k = 0; k < shot.Keys.Count; k++) list.Add((shot.Keys[k], k < shot.KeysShip.Count ? shot.KeysShip[k] : null, $"K{k + 1}"));
            else if (shot.Type == "Move")
            {
                if (shot.View != null || shot.ViewShip != null) list.Add((shot.View, shot.ViewShip, "START"));
                if (shot.View2 != null || shot.View2Ship != null) list.Add((shot.View2, shot.View2Ship, "END"));
            }
            else if (shot.View != null || shot.ViewShip != null)
                list.Add((shot.Type == "Orbit" || shot.Type == "Chase" ? null : shot.View, shot.ViewShip, "VIEW"));
            return list;
        }

        /// <summary>The ship a shot was made on, looked for by name (every 2 seconds at most).</summary>
        private object PreviewGrid(CineShot shot)
        {
            if (shot.Target == null) return null;
            double now = game.TimeSeconds;
            if (shot.Target != cinePreviewFor || now - cinePreviewAt > 2 || now < cinePreviewAt)
            {
                cinePreviewFor = shot.Target;
                cinePreviewAt = now;
                object near = orbitCam && ocGrid != null ? ocGrid : null;
                cinePreviewGrid = near != null && game.GridName(near) == shot.Target ? near
                    : game.FindGrid(shot.Target, playerPosition ?? Vec3D.Zero) ?? cineLastGrid ?? spotGrid;
            }
            return cinePreviewGrid;
        }

        /// <summary>Where a view is now: from the ship (when it's placed from the ship, or has no world place) or in the world.</summary>
        private bool ViewNow(CineShot shot, double[] world, double[] ship, out Vec3D pos, out Vec3D dir, out Vec3D up)
        {
            pos = dir = up = Vec3D.Zero;
            bool fromShip = ship != null && ship.Length >= 9 && (world == null || shot.Relative);
            if (fromShip)
            {
                object g = PreviewGrid(shot);
                if (g != null && game.TryGetGridPose(g, out Vec3D c, out Vec3D ax, out Vec3D ay, out Vec3D az, out _, out _))
                {
                    ShipView(ship, c, ax, ay, az, out pos, out dir, out up);
                    if (shot.Type == "Orbit" && shot.Dist > 0) { dir = (c - pos).Normalized(); }
                    return true;
                }
                if (world == null) return false;
            }
            if (world == null || world.Length < 9) return false;
            pos = new Vec3D(world[0], world[1], world[2]);
            dir = new Vec3D(world[3], world[4], world[5]);
            up = new Vec3D(world[6], world[7], world[8]);
            return true;
        }

        /// <summary>Puts the spectator camera at one of a shot's views, free to fly from there (to fine-tune it, then Set).</summary>
        private string GoToView(CineShot shot, int index)
        {
            var views = ShotViews(shot);
            if (index < 0 || index >= views.Count) return "That view isn't set yet";
            if (!ViewNow(shot, views[index].World, views[index].Ship, out Vec3D pos, out Vec3D dir, out Vec3D up))
                return "Couldn't place it: the ship it was made on isn't here";
            if (cinePlay != null) EndCinematic(null);
            dir = dir.Normalized();
            up = (up - dir * Vec3D.Dot(up, dir)).Normalized();
            bool ok;
            if (game.InSpectator)
            {
                if (orbitCam) ocGrid = null;   // let the orbit camera go, so it doesn't pull the view back
                ok = game.SetSpectatorView(pos, dir, up, true);
            }
            else ok = game.StartSpectator(pos, dir, up);
            if (!ok) return "Couldn't move the spectator camera";
            game.CloseControlsMenu();
            Notify($"At {(shot.Type == "Path" ? "keyframe " + (index + 1) : views[index].Name.ToLowerInvariant())}. Adjust the view, then open the menu and press Set{(shot.Type == "Path" ? " on that keyframe" : "")}", HudColor.Cyan);
            return null;
        }

        /// <summary>The picked shot's views drawn in the world: a camera shape at each (the picked keyframe yellow), a path's curve, labels.</summary>
        private void DrawCinePreview(HudModel model)
        {
            if (!settings.CinePreview || cinePlay != null) return;
            var file = CineCurrent;
            if (file == null || cineEditShot < 0 || cineEditShot >= file.Shots.Count) return;
            var shot = file.Shots[cineEditShot];
            var views = ShotViews(shot);
            if (views.Count == 0) return;
            var pts = new List<Vec3D>();
            var placed = new List<(Vec3D Pos, Vec3D Dir, Vec3D Up, int Index)>();
            for (int k = 0; k < views.Count; k++)
                if (ViewNow(shot, views[k].World, views[k].Ship, out Vec3D vp, out Vec3D vd, out Vec3D vu)) placed.Add((vp, vd.Normalized(), vu.Normalized(), k));
            if (placed.Count == 0) return;
            Vec3D eye = playerPosition ?? placed[0].Pos;
            if (game.TryGetLookRay(out Vec3D camEye, out _, out _, out _)) eye = camEye;

            // The path the camera will take.
            var linePts = new List<Vec3D>();
            if (shot.Type == "Path" && placed.Count >= 2)
            {
                var ps = placed.Select(x => x.Pos).ToList();
                int pieces = 16 * (ps.Count - 1);
                for (int i = 0; i < pieces; i++)
                {
                    linePts.Add(Curve(ps, (double)i / pieces * (ps.Count - 1)));
                    linePts.Add(Curve(ps, (double)(i + 1) / pieces * (ps.Count - 1)));
                }
            }
            else if (shot.Type == "Move" && placed.Count >= 2) { linePts.Add(placed[0].Pos); linePts.Add(placed[1].Pos); }
            if (linePts.Count > 0) game.DrawMarkerLines(linePts, new HudColor(255, 120, 190, 230), false);

            // A camera shape at each view: a box at the eye, a frame in front, the way up.
            foreach (var v in placed)
            {
                bool picked = v.Index == cineEditKey || (shot.Type != "Path" && views.Count == 1);
                var colour = picked ? HudColor.Yellow : new HudColor(255, 255, 255, 220);
                double size = Math.Clamp((v.Pos - eye).Length * 0.04, 1.5, 60);
                Vec3D right = Vec3D.Cross(v.Dir, v.Up).Normalized();
                Vec3D up = Vec3D.Cross(right, v.Dir).Normalized();
                Vec3D far = v.Pos + v.Dir * size * 1.6;
                Vec3D c1 = far + right * size * 0.8 + up * size * 0.45, c2 = far - right * size * 0.8 + up * size * 0.45;
                Vec3D c3 = far - right * size * 0.8 - up * size * 0.45, c4 = far + right * size * 0.8 - up * size * 0.45;
                var lines = new List<Vec3D>
                {
                    v.Pos, c1, v.Pos, c2, v.Pos, c3, v.Pos, c4,
                    c1, c2, c2, c3, c3, c4, c4, c1,
                    far + up * size * 0.45, far + up * size * 0.9,   // up
                };
                game.DrawMarkerLines(lines, colour, false);
                if (game.TryProjectToScreen(v.Pos, out double x, out double y, out bool behind) && !behind && x > 0.01 && x < 0.99 && y > 0.01 && y < 0.99)
                    model.Markers.Add(new HudModel.ScreenMarker
                    {
                        X = x, Y = y, Text = views[v.Index].Name, Sub = picked ? "picked" : null,
                        Colour = picked ? HudColor.Yellow : HudColor.White, Emphasis = picked, World = v.Pos,
                    });
            }
        }

        private static void ShipView(double[] v, Vec3D c, Vec3D ax, Vec3D ay, Vec3D az, out Vec3D pos, out Vec3D dir, out Vec3D up)
        {
            pos = c + ax * v[0] + ay * v[1] + az * v[2];
            dir = ax * v[3] + ay * v[4] + az * v[5];
            up = ax * v[6] + ay * v[7] + az * v[8];
        }

        /// <summary>A Fixed / Move view in the world: from the ship as it was when the shot started, or the world place it was captured at.</summary>
        private bool AnchoredView(double[] world, double[] ship, bool relative, out Vec3D pos, out Vec3D dir, out Vec3D up)
        {
            pos = dir = up = Vec3D.Zero;
            if (ship != null && ship.Length >= 9 && cineHasAnchor && (relative || world == null))
            {
                ShipView(ship, cineC, cineAx, cineAy, cineAz, out pos, out dir, out up);
                return true;
            }
            if (world == null || world.Length < 9) return false;
            pos = new Vec3D(world[0], world[1], world[2]);
            dir = new Vec3D(world[3], world[4], world[5]);
            up = new Vec3D(world[6], world[7], world[8]);
            return true;
        }

        private double cineSkipInto;   // play from part-way: seconds into the first shot played

        private void StartCinematic(CinematicFile file, object subject, int onlyShot, double fromTime = 0)
        {
            if (file == null || file.Shots.Count == 0) { Notify("This cinematic has no shots yet", HudColor.Yellow); return; }
            if (!StartOrbitCam(subject, false, true)) return;
            cinePlay = file;
            cineMain = subject ?? ocGrid;
            cineShot = -1;
            cineLastCam = cineLastDir = cineLastU = null;
            cineOnlyShot = onlyShot >= 0 && onlyShot < file.Shots.Count ? onlyShot : -1;
            cineShotAt = 0;
            cineSkipInto = 0;
            if (cineOnlyShot < 0 && fromTime > 0)
            {
                // Start in the shot that's playing at that time, part-way through it.
                double before = 0;
                for (int k = 0; k < file.Shots.Count; k++)
                {
                    if (fromTime < before + CineShotLength(file, k)) { cineShot = k - 1; cineSkipInto = fromTime - before; break; }
                    before += file.Shots[k].Seconds;
                }
            }
            cineClockReal = game.TimeSeconds;
            if (RecordingScale < 0.999) Notify($"Recording speed {settings.RecordingSpeed:0}%: speed the video up {100 / settings.RecordingSpeed:0.##}x afterwards", HudColor.Purple);
            cineUp = ocWorldUp.Length > 0.5 ? ocWorldUp
                : game.TryGetLookRay(out _, out _, out _, out Vec3D camUp) && camUp.Length > 0.5 ? camUp.Normalized() : new Vec3D(0, 1, 0);
            cineVersion++;
            PluginLog.StartTrace(30, "cinematic");   // frame times while it plays, to find any judder
            PluginLog.Info($"Cinematic '{file.Name}' playing ({file.Shots.Count} shots{(cineOnlyShot >= 0 ? ", shot " + (cineOnlyShot + 1) + " only" : "")})");
            KeyHint((cineOnlyShot >= 0 ? $"Shot {cineOnlyShot + 1} of {file.Name}" : $"Cinematic: {file.Name}") + "   Mouse: look around   W / S: zoom (orbit shots)   Esc: stop", HudColor.Purple);
        }

        private static HudColor CineHudColour(string type) =>
            type == "Fixed" ? new HudColor(70, 110, 190) : type == "Move" ? new HudColor(150, 90, 190) : type == "Chase" ? new HudColor(60, 150, 110)
            : type == "Fov" ? new HudColor(40, 140, 150) : type == "Roll" ? new HudColor(170, 80, 110) : type == "Shake" ? new HudColor(125, 120, 60)
            : type == "Panel" ? new HudColor(90, 100, 120) : type == "Fade" ? new HudColor(60, 60, 70) : type == "Bars" ? new HudColor(110, 85, 60)
            : type == "Path" ? new HudColor(200, 90, 130)
            : new HudColor(190, 130, 50);

        /// <summary>Seconds into the cinematic's whole timeline (-1 = none playing).</summary>
        private double CineTimeNow()
        {
            var file = cinePlay;
            if (file == null || cineShot < 0 || cineShot >= file.Shots.Count) return -1;
            double at = Math.Clamp(CineNow - cineShotAt, 0, CinePlayLength(file, cineShot));
            for (int k = 0; k < cineShot; k++) at += file.Shots[k].Seconds;
            return at;
        }

        /// <summary>
        /// The race spotlight page a Panel effect asks for now: 0 results, 1 race stats, 2 the ship, 3 hidden;
        /// -1 = no Panel effect running (the pages change by themselves). The highest layer wins.
        /// </summary>
        private int CinePanelNow()
        {
            double at = CineTimeNow();
            if (at < 0) return -1;
            foreach (var fx in cinePlay.Effects.Where(x => x != null && x.Type == "Panel").OrderBy(x => x.Lane))
                if (at >= fx.Start && at < fx.Start + fx.Seconds) return Math.Clamp((int)Math.Round(fx.To), 0, 15);
            return -1;
        }

        /// <summary>The spotlight pages by id (0..9), as the cinematic Panel effect names them.</summary>
        internal static readonly string[] PanelNames = { "Race results", "Race stats", "The ship", "Hidden", "Lap replay", "Race graphs", "Ship card", "Lap by lap", "Intro", "Outro",
                                                         "Course card", "Speed trap", "Fun facts", "Trouble spots", "Leaderboard", "Head to head" };
        private static string FxTypeName(string type) => type == "Roll" ? "Tilt" : type == "Shake" ? "Shake" : type == "Panel" ? "Panel" : type == "Fade" ? "Fade" : type == "Bars" ? "Letterbox" : "FOV";

        /// <summary>The debug timeline for the HUD (null unless a cinematic is playing with the debug setting on).</summary>
        private HudModel.CineDebugPanel CineDebugNow()
        {
            var file = cinePlay;
            if (file == null || !settings.CineDebug || !orbitCam) return null;
            var panel = new HudModel.CineDebugPanel { Lanes = Math.Max(1, file.Lanes), Title = "CINEMATIC  " + file.Name + (cineOnlyShot >= 0 ? "  (one shot)" : file.Loop ? "  (loop)" : "") };
            double before = 0, into = 0;
            for (int i = 0; i < file.Shots.Count; i++)
            {
                var shot = file.Shots[i];
                bool playing = i == cineShot;
                if (i < cineShot) before += shot.Seconds;
                if (playing) into = Math.Clamp(CineNow - cineShotAt, 0, shot.Seconds);
                panel.Total += shot.Seconds;
                panel.Shots.Add(($"{i + 1} {CineTypeName(shot.Type)}", shot.Seconds, CineHudColour(shot.Type), playing));
            }
            panel.Time = before + into;
            string active = "";
            foreach (var fx in file.Effects)
            {
                if (fx == null) continue;
                bool running = panel.Time >= fx.Start && panel.Time < fx.Start + fx.Seconds;
                string name = fx.Type == "Panel" ? "Panel: " + PanelNames[Math.Clamp((int)Math.Round(fx.To), 0, 14)] : FxTypeName(fx.Type);
                if (running && fx.Type == "Panel") active += "   " + name;
                else if (running)
                {
                    double part = Math.Clamp((panel.Time - fx.Start) / Math.Max(0.01, fx.Seconds), 0, 1);
                    part = part * part * (3 - 2 * part);
                    double shownValue = fx.From + (fx.To - fx.From) * part;
                    active += fx.Type == "Shake" ? $"   Shake {fx.To:0.##}°" : fx.Type == "Fade" || fx.Type == "Bars" ? $"   {name} {shownValue:0}%" : $"   {name} {shownValue:0.#}°";
                }
                panel.Effects.Add((name, fx.Start, fx.Seconds, CineHudColour(fx.Type), running, fx.Lane));
            }
            var now = cineShot >= 0 && cineShot < file.Shots.Count ? file.Shots[cineShot] : null;
            panel.Now = $"{panel.Time:0.0}s of {panel.Total:0.#}s"
                + (now != null ? $"   shot {cineShot + 1}: {CineTypeName(now.Type)}, {into:0.0}s of {now.Seconds:0.#}s" : "")
                + (cineGrid != null ? $"   ship: {game.GridName(cineGrid) ?? "?"}" : now?.Target != null ? "   ship: not found" : "")
                + active
                + (cineYaw != 0 || cinePitch != 0 ? $"   mouse {cineYaw * 180 / Math.PI:0}° / {cinePitch * 180 / Math.PI:0}°" : "");
            return panel;
        }

        /// <summary>Cinematic debug's whole HUD: the timeline (and messages); no race HUD.</summary>
        private void DrawCineDebug()
        {
            double now = game.TimeSeconds;
            HudModel.Message centre = message != null && now < messageUntil
                ? new HudModel.Message { Text = message, Colour = messageColour } : null;
            var model = hud.Build(blankTimer, now, null, centre, null);
            model.CineDebug = CineDebugNow();
            model.CineFade = cinePlay != null && orbitCam ? cineFade : 0;
            model.CineBars = cinePlay != null && orbitCam ? cineBars : 0;
            AddFilmOverlay(model);
            resultsOnScreen = false;
            bool shownByGameUi = settings.UseGameUi && game.ShowHud(model.IsEmpty ? null : model);
            if (!shownByGameUi)
            {
                if (!settings.UseGameUi) game.ShowHud(null);
                hud.DrawDebug(model);
            }
        }

        /// <summary>The ship a cinematic should film: the orbit camera's, the one in the crosshair, the spotlight's, the last one used, or your own.</summary>
        private object CineSubject()
        {
            if (orbitCam && ocGrid != null) return ocGrid;
            object looked = game.TryGetLookRay(out Vec3D eye, out Vec3D look, out _, out _) ? game.LookedAtGrid(eye, look, 5000, true) : null;
            return looked ?? spotGrid ?? cineLastGrid ?? game.OwnGrid;
        }

        // ---- auto switch: a cinematic starts when the filmed ship flies through a gate
        private object ctGrid;
        private Vec3D? ctLast;
        private double ctLastTime;
        private readonly Dictionary<int, double> ctQuietUntil = new Dictionary<int, double>();
        private readonly List<(int Gate, double At)> ctPending = new List<(int, double)>();   // "seconds after" switches waiting to go

        private void CheckCineTriggers()
        {
            object g = cinePlay != null ? cineMain : ocGrid;
            if (!settings.CineAutoSwitch || timer.Gate == null || g == null) { ctLast = null; ctPending.Clear(); return; }
            if (!ReferenceEquals(g, ctGrid)) { ctGrid = g; ctLast = null; ctPending.Clear(); }
            if (!game.TryGetGridPose(g, out Vec3D at, out _, out _, out _, out _, out _)) { ctLast = null; return; }
            Vec3D? from = ctLast;
            double now = game.TimeSeconds, dt = now - ctLastTime;
            ctLast = at;
            ctLastTime = now;
            if (!from.HasValue || (at - from.Value).Length > 2000) return;   // (a teleport isn't flying through anything)
            string key = CourseKey();
            GateSettings.CineTrigger TriggerFor(int gate) => settings.CineTriggers.FirstOrDefault(x => x.Course == key && x.Gate == gate && !string.IsNullOrEmpty(x.Cinematic));
            // "Seconds after a gate" switches that are due.
            for (int k = ctPending.Count - 1; k >= 0; k--)
                if (now >= ctPending[k].At) { int due = ctPending[k].Gate; ctPending.RemoveAt(k); Fire(due, TriggerFor(due)); }
            // How the ship is moving (for "seconds before": when it will reach a gate).
            Vec3D vel = dt > 1e-3 && dt < 0.5 ? (at - from.Value) * (1 / dt) : Vec3D.Zero;
            double speed = vel.Length;
            void Check(int gate, TimingGate g2)
            {
                if (g2 == null) return;
                var trigger = TriggerFor(gate);
                if (trigger != null && trigger.Offset < 0)
                {
                    // On the approach: heading at the gate, and this many seconds or less from it.
                    if (speed < 5 || (ctQuietUntil.TryGetValue(gate, out double q0) && now < q0)) return;
                    Vec3D rel = g2.Center - at, dir = vel * (1 / speed);
                    double along = Vec3D.Dot(rel, dir);
                    if (along <= 0) return;
                    double miss = (rel - dir * along).Length;
                    if (miss > Math.Max(g2.HalfWidth, g2.HalfHeight) + 40) return;
                    if (along / speed > -trigger.Offset) return;
                    ctQuietUntil[gate] = now - trigger.Offset + 3;
                    Fire(gate, trigger);
                    return;
                }
                if (!g2.SegmentIntersects(from.Value, at)) return;
                if (ctQuietUntil.TryGetValue(gate, out double quiet) && now < quiet) return;
                ctQuietUntil[gate] = now + 2;
                if (trigger == null) return;
                if (trigger.Offset > 0) { ctPending.Add((gate, now + trigger.Offset)); return; }
                Fire(gate, trigger);
            }
            void Fire(int gate, GateSettings.CineTrigger trigger)
            {
                if (trigger == null || string.IsNullOrEmpty(trigger.Cinematic)) return;
                var list = Cinematics();
                CinematicFile file;
                if (trigger.Cinematic == "*next*")
                {
                    if (list.Count == 0) return;
                    int i = list.FindIndex(x => x.Name == settings.CinematicName);
                    file = list[(i + 1) % list.Count];
                }
                else file = list.FirstOrDefault(x => x.Name == trigger.Cinematic);
                if (file == null || file.Shots.Count == 0) return;
                settings.CinematicName = file.Name;
                cineVersion++;
                StartCinematic(file, g, -1);
                PluginLog.Info($"Cinematic auto switch: {(gate == 0 ? "start" : gate < 0 ? "finish" : "CP " + gate)}"
                    + (trigger.Offset < 0 ? $" ({-trigger.Offset:0.#}s before)" : trigger.Offset > 0 ? $" ({trigger.Offset:0.#}s after)" : "") + $" -> {file.Name}");
            }
            Check(0, timer.Gate);
            for (int i = 0; i < timer.Checkpoints.Count; i++) Check(i + 1, timer.Checkpoints[i]);
            if (timer.FinishGate != null) Check(-1, timer.FinishGate);
        }

        /// <summary>Key: the next / previous cinematic. One playing switches straight to the new one, on the same ship.</summary>
        private void StepCinematic(int way)
        {
            var list = Cinematics();
            if (list.Count == 0) { Notify("No cinematics yet: make one on the Cinematics tab", HudColor.Yellow); return; }
            int at = list.FindIndex(x => x.Name == settings.CinematicName);
            int next = at < 0 ? 0 : ((at + way) % list.Count + list.Count) % list.Count;
            var file = list[next];
            settings.CinematicName = file.Name;
            settings.Save(settingsPath);
            cineVersion++;
            cineEditShot = cineEditKey = -1;
            string where = $"({next + 1} of {list.Count})";
            if (cinePlay != null)
            {
                object subject = cineMain ?? CineSubject();
                if (file.Shots.Count == 0) { EndCinematic(null); Notify($"Cinematic: {file.Name} {where}  (no shots yet: stopped)", HudColor.Yellow); return; }
                StartCinematic(file, subject, -1);
                Notify($"Cinematic: {file.Name} {where}   {settings.GetBinding(GateKey.CinePrev)} / {settings.GetBinding(GateKey.CineNext)}: switch", HudColor.Purple);
            }
            else Notify($"Cinematic: {file.Name} {where}   {settings.GetBinding(GateKey.CinePlayStop)}: play", HudColor.Purple);
        }

        /// <summary>Key: play the cinematic picked (on the ship in view), or stop the one playing.</summary>
        private void PlayStopCinematic()
        {
            if (cinePlay != null) { EndCinematic("Cinematic stopped"); return; }
            if (replaying) { Notify("Stop the replay first", HudColor.Yellow); return; }
            var file = CineCurrent;
            if (file == null) { Notify("No cinematic picked: make one on the Cinematics tab", HudColor.Yellow); return; }
            StartCinematic(file, CineSubject(), -1);
        }

        /// <summary>Stops the cinematic; the orbit camera carries on from where the camera is.</summary>
        private void EndCinematic(string note)
        {
            if (cinePlay == null) return;
            cinePlay = null;
            cineVersion++;
            game.SetCameraFov(null);
            ocLastCam = ocLastDir = null;
            if (note != null) Notify(note, HudColor.Purple);
        }

        /// <summary>Gets a shot ready to play: finds its ship and works out where its views are. False if it can't be played.</summary>
        private bool PrepareShot(CineShot shot, double now)
        {
            cineGrid = null;
            cineHasAnchor = false;
            cineYaw = cinePitch = cineZoom = 0;   // each shot starts as it was made
            if (shot.Target != null)
            {
                object g = (cinePlay.FollowPicked ? cineMain : null) ?? game.FindGrid(shot.Target, playerPosition ?? Vec3D.Zero) ?? cineMain;
                if (g != null && game.TryGetGridPose(g, out Vec3D c, out Vec3D ax, out Vec3D ay, out Vec3D az, out _, out Vec3D gravityDown))
                {
                    cineGrid = g;
                    cineC = c; cineAx = ax; cineAy = ay; cineAz = az;
                    cineHasAnchor = true;
                    if (gravityDown.Length > 0.5) cineUp = gravityDown * -1;
                }
            }
            switch (shot.Type)
            {
                case "Orbit":
                    if (!cineHasAnchor || shot.ViewShip == null || shot.ViewShip.Length < 9 || shot.Dist <= 0) return false;
                    cineDir0 = (cineAx * shot.ViewShip[3] + cineAy * shot.ViewShip[4] + cineAz * shot.ViewShip[5]).Normalized();
                    break;
                case "Chase":
                    if (!cineHasAnchor || shot.ViewShip == null || shot.ViewShip.Length < 9) return false;
                    break;
                case "Move":
                    if (!AnchoredView(shot.View, shot.ViewShip, shot.Relative, out cineP1, out cineD1, out cineU1)) return false;
                    if (!AnchoredView(shot.View2, shot.View2Ship, shot.Relative, out cineP2, out cineD2, out cineU2)) return false;
                    break;
                case "Path":
                {
                    cinePathP.Clear(); cinePathD.Clear(); cinePathU.Clear();
                    for (int k = 0; k < shot.Keys.Count; k++)
                    {
                        var keyShip = k < shot.KeysShip.Count ? shot.KeysShip[k] : null;
                        if (!AnchoredView(shot.Keys[k], keyShip, shot.Relative, out Vec3D kp, out Vec3D kd, out Vec3D ku)) continue;
                        cinePathP.Add(kp); cinePathD.Add(kd.Normalized()); cinePathU.Add(ku.Normalized());
                    }
                    if (cinePathP.Count < 2) return false;
                    cineP1 = cinePathP[0]; cineD1 = cinePathD[0]; cineU1 = cinePathU[0];
                    break;
                }
                default:
                    if (!AnchoredView(shot.View, shot.ViewShip, shot.Relative, out cineP1, out cineD1, out cineU1)) return false;
                    break;
            }
            cineShotAt = now;
            return true;
        }

        /// <summary>On to the next shot that can be played (round to the first with Loop on). False when the cinematic has ended.</summary>
        private bool NextCineShot(double now)
        {
            var file = cinePlay;
            for (int tries = 0; tries <= file.Shots.Count; tries++)
            {
                int next = cineShot + 1;
                if (cineOnlyShot >= 0)
                {
                    if (cineShot == cineOnlyShot) { EndCinematic("Shot finished"); return false; }
                    next = cineOnlyShot;
                }
                else if (next >= file.Shots.Count || CineShotLength(file, next) < 0.01)   // (past a set timeline length: the end)
                {
                    if (!file.Loop) { EndCinematic("Cinematic finished"); return false; }
                    next = 0;
                }
                if (next >= file.Shots.Count) break;
                if (cineLastCam.HasValue && cineLastDir.HasValue && cineLastU.HasValue && cineShot >= 0)
                {
                    cineBlending = true;
                    cineBlendP = cineLastCam.Value; cineBlendD = cineLastDir.Value; cineBlendU = cineLastU.Value;
                    cineBlendRel = cineLastCentre.HasValue ? cineLastCam.Value - cineLastCentre.Value : (Vec3D?)null;
                    cineBlendGrid = cineLastFilmed;
                }
                else cineBlending = false;
                cineShot = next;
                if (PrepareShot(file.Shots[next], now))
                {
                    if (cineSkipInto > 0) { cineShotAt -= Math.Min(cineSkipInto, CinePlayLength(file, next) - 0.05); cineSkipInto = 0; }
                    cineVersion++;
                    return true;
                }
                cineSkipInto = 0;
                if (cineOnlyShot >= 0) { EndCinematic("That shot isn't ready: set its view first"); return false; }
            }
            EndCinematic("No shot in this cinematic can be played: set their views first (orbit and chase shots need a ship)");
            return false;
        }

        private void UpdateCinematic()
        {
            double realNow = game.TimeSeconds;
            double realStep = Math.Clamp(realNow - cineClockReal, 0, 0.1);
            cineClockReal = realNow;
            if (!game.IsPaused) cineClock += realStep * RecordingScale;
            double now = cineClock;
            var file = cinePlay;
            CineShot shot = cineShot >= 0 && cineShot < file.Shots.Count ? file.Shots[cineShot] : null;
            if (shot == null || now - cineShotAt >= CinePlayLength(file, cineShot) || now < cineShotAt)
            {
                if (!NextCineShot(now)) return;
                shot = file.Shots[cineShot];
            }
            double t = now - cineShotAt;

            // Where the shot's ship is now.
            Vec3D centre = cineC, ax = cineAx, ay = cineAy, az = cineAz, gravityDown = Vec3D.Zero;
            double gridRadius = 0;
            bool live = cineGrid != null && game.TryGetGridPose(cineGrid, out centre, out ax, out ay, out az, out gridRadius, out gravityDown);
            // (With the game's own lock the camera's place is given relative to the ship, so the ship's own pose is
            // used as it is; otherwise it's smoothed.)
            bool gameFollows = settings.CineGameLock && (shot.Type == "Orbit" || shot.Type == "Chase");
            if (live && !gameFollows) centre = TrackedCentre(cineGrid, centre);
            if (live && gravityDown.Length > 0.5) cineUp = gravityDown * -1;

            // The mouse (through the spectator's own controls): how far the game has turned and moved the camera
            // since we last placed it. Turning looks around; in an orbit it swings round the ship and W / S zoom.
            if (cineLastCam.HasValue && cineLastDir.HasValue && cineLastU.HasValue && !game.IsPaused && !controlsOpen
                && game.TryGetSpectatorView(out Vec3D camNow, out Vec3D fwdNow, out _) && fwdNow.Length > 0.5)
            {
                fwdNow = fwdNow.Normalized();
                Vec3D lastDir = cineLastDir.Value, lastU = cineLastU.Value;
                Vec3D lastRight = Vec3D.Cross(lastDir, lastU).Normalized();
                if (Vec3D.Dot(fwdNow, lastDir) < 0.9999995)
                {
                    cineYaw += Math.Atan2(Vec3D.Dot(fwdNow, lastRight), Vec3D.Dot(fwdNow, lastDir));
                    cinePitch = Math.Clamp(cinePitch + Math.Asin(Math.Clamp(Vec3D.Dot(fwdNow, lastU), -1, 1)), -1.4, 1.4);
                }
                double along = Vec3D.Dot(camNow - cineLastCam.Value, lastDir);
                if (shot.Type == "Orbit" && Math.Abs(along) > 0.01 && Math.Abs(along) < 200) cineZoom -= along;
            }

            Vec3D pos, dir, up;
            switch (shot.Type)
            {
                case "Orbit":
                    if (!live) { cineShotAt = now - CinePlayLength(file, cineShot); return; }   // its ship is gone: on to the next shot
                    dir = Turn(cineDir0, cineUp, shot.RotateSpeed * t * Math.PI / 180);
                    pos = centre;   // (placed below, once the mouse's swing is added)
                    up = cineUp;
                    break;
                case "Chase":
                    if (!live) { cineShotAt = now - CinePlayLength(file, cineShot); return; }
                    ShipView(shot.ViewShip, centre, ax, ay, az, out pos, out dir, out up);
                    break;
                case "Path":
                {
                    // Through every keyframe in turn on a smooth curve, easing away from the first and into the last.
                    double e = Math.Clamp(t / Math.Max(0.01, shot.Seconds), 0, 1);
                    e = e * e * (3 - 2 * e);
                    double f = e * (cinePathP.Count - 1);
                    pos = Curve(cinePathP, f);
                    dir = Curve(cinePathD, f);
                    up = Curve(cinePathU, f);
                    break;
                }
                case "Move":
                {
                    double e = Math.Clamp(t / Math.Max(0.01, shot.Seconds), 0, 1);
                    e = e * e * (3 - 2 * e);   // eases away from the start and into the end
                    pos = cineP1 + (cineP2 - cineP1) * e;
                    dir = cineD1 + (cineD2 - cineD1) * e;
                    up = cineU1 + (cineU2 - cineU1) * e;
                    break;
                }
                default:
                    pos = cineP1; dir = cineD1; up = cineU1;
                    break;
            }
            if (shot.Track && live && (shot.Type == "Fixed" || shot.Type == "Move" || shot.Type == "Path"))
            {
                Vec3D to = centre - pos;
                if (to.Length > 0.5) { dir = to; up = cineUp; }
            }
            if (dir.Length < 0.01) dir = cineD1.Length > 0.01 ? cineD1 : new Vec3D(0, 0, 1);
            dir = dir.Normalized();
            Vec3D u = up - dir * Vec3D.Dot(up, dir);
            if (u.Length < 0.05) u = Vec3D.Cross(dir, Math.Abs(dir.Y) < 0.9 ? new Vec3D(0, 1, 0) : new Vec3D(1, 0, 0));
            u = u.Normalized();
            if (cineYaw != 0 || cinePitch != 0)
            {
                // The mouse's turn, on top of the shot's own view.
                Vec3D right = Vec3D.Cross(dir, u).Normalized();
                double cp = Math.Cos(cinePitch);
                Vec3D turned = (dir * (cp * Math.Cos(cineYaw)) + right * (cp * Math.Sin(cineYaw)) + u * Math.Sin(cinePitch)).Normalized();
                Vec3D u2 = u - turned * Vec3D.Dot(u, turned);
                if (u2.Length > 0.05) { dir = turned; u = u2.Normalized(); }
            }
            // (Never inside the ship: at least a little more than its size away.)
            if (shot.Type == "Orbit") pos = centre - dir * Math.Clamp(shot.Dist + cineZoom, Math.Max(3, gridRadius * 1.1), 5000);

            // The effects layer, at this moment of the timeline.
            double at = t;
            for (int k = 0; k < cineShot && k < file.Shots.Count; k++) at += file.Shots[k].Seconds;
            double? fov = null;
            double fade = 0, bars = 0;
            // (Layer by layer: where two FOVs overlap the lower layer's wins; tilts and shakes add up.)
            foreach (var fx in file.Effects.Where(x => x != null).OrderBy(x => x.Lane))
            {
                if (at < fx.Start || at >= fx.Start + fx.Seconds) continue;
                double part = Math.Clamp((at - fx.Start) / Math.Max(0.01, fx.Seconds), 0, 1);
                part = part * part * (3 - 2 * part);
                double value = fx.From + (fx.To - fx.From) * part;
                if (fx.Type == "Fov") fov = value;
                else if (fx.Type == "Fade") fade = Math.Max(fade, Math.Clamp(value, 0, 100) / 100);
                else if (fx.Type == "Bars") bars = Math.Max(bars, Math.Clamp(value, 0, 45) / 100);
                else if (fx.Type == "Roll") u = Turn(u, dir, value * Math.PI / 180).Normalized();
                else if (fx.Type == "Shake")
                {
                    // A hand-held wobble: a few slow waves that never quite repeat.
                    double strength = Math.Clamp(fx.To, 0, 10) * Math.PI / 180;
                    double sway = (Math.Sin(at * 3.1) + 0.6 * Math.Sin(at * 7.7 + 1.3) + 0.3 * Math.Sin(at * 13.3 + 2.1)) / 1.9 * strength;
                    double nod = (Math.Sin(at * 2.3 + 0.7) + 0.6 * Math.Sin(at * 6.1 + 2.9) + 0.3 * Math.Sin(at * 11.9)) / 1.9 * strength;
                    Vec3D side = Vec3D.Cross(dir, u).Normalized();
                    Vec3D shaken = (dir + side * Math.Tan(sway) + u * Math.Tan(nod)).Normalized();
                    Vec3D shakenUp = u - shaken * Vec3D.Dot(u, shaken);
                    if (shakenUp.Length > 0.05) { dir = shaken; u = shakenUp.Normalized(); }
                }
            }
            cineFade = fade;
            cineBars = bars;
            // Smooth cut: ease from where the last shot left the camera into this one.
            if (cineBlending && shot.Blend > 0.01 && t < shot.Blend)
            {
                double b = t / shot.Blend;
                b = b * b * (3 - 2 * b);
                if (live && cineBlendRel.HasValue && ReferenceEquals(cineGrid, cineBlendGrid))
                {
                    // On the same ship: blend where the camera is relative to it, so it keeps up with the ship
                    // (blending from where it was in the world left it far behind, or swept it through the ship, at speed).
                    Vec3D from = centre + cineBlendRel.Value;
                    pos = from + (pos - from) * b;
                    if (gridRadius > 0 && (pos - centre).Length < gridRadius * 1.05 && (from - centre).Length >= gridRadius * 1.05 && (pos - centre).Length > 0.01)
                        pos = centre + (pos - centre).Normalized() * gridRadius * 1.05;   // (round the ship, not through it)
                }
                else pos = cineBlendP + (pos - cineBlendP) * b;
                Vec3D bd = (cineBlendD + (dir - cineBlendD) * b);
                Vec3D bu = (cineBlendU + (u - cineBlendU) * b);
                if (bd.Length > 0.05)
                {
                    dir = bd.Normalized();
                    bu = bu - dir * Vec3D.Dot(bu, dir);
                    if (bu.Length > 0.05) u = bu.Normalized();
                }
            }
            if (fov.HasValue) { game.SetCameraFov(fov.Value); cineFovOn = true; }
            else if (cineFovOn) { game.SetCameraFov(null); cineFovOn = false; }
            if (gameFollows && live && game.SetSpectatorFollow(cineGrid, pos, dir, u)) { }
            else
            {
                game.ClearSpectatorFollow();
                if (!game.SetSpectatorView(pos, dir, u, false)) { StopOrbitCam("Cinematic stopped (the spectator camera went away)"); return; }
            }
            cineLastCam = pos; cineLastDir = dir; cineLastU = u;
            cineLastCentre = live ? centre : (Vec3D?)null;
            cineLastFilmed = live ? cineGrid : null;
        }

        private Vec3D? cineLastCentre, cineBlendRel;
        private object cineLastFilmed, cineBlendGrid;

        /// <summary>Spotlight mode's panel: the saved race's results, its stats, and the picked ship's stats, one after another.</summary>
        /// <summary>Puts the race spotlight on a grid (and the current cinematic on it, if the spotlight plays it).</summary>
        private void SpotOn(object g, double now, bool ownCinematicComing)
        {
            spotGrid = g;
            spotStats = game.AnalyseGrid(g);
            spotStatsAt = now;
            spotPage = 0; spotPageAt = now;
            KeyHint($"Spotlight on {spotStats?.Name ?? "this grid"}   Esc: exit", HudColor.Cyan);
            if (ownCinematicComing) return;   // the race's own cinematic (or the auto race, then the camera) comes in a moment
            SpotCamera(g);
        }

        /// <summary>
        /// The spotlight's camera on a grid: the current cinematic, when the spotlight is set to play it. (The orbit
        /// camera isn't started by itself any more: its own key puts it on the ship.)
        /// </summary>
        private void SpotCamera(object g)
        {
            if (!settings.SpotlightCinematic || CineCurrent == null || CineCurrent.Shots.Count == 0) return;
            StartOrbitCam(g, !orbitCam || ocFromSpotlight, true);
            if (orbitCam) StartCinematic(CineCurrent, g, -1);
        }

        // ---- Spotlight tour: each ship's race in the running order (Race tab > Race spotlight > Leaderboard), one after
        // another: your character is put beside the ship, its spotlight plays through its pages once, then the next.
        private List<string> spotTour;
        private int spotTourAt;
        private bool spotTourNext, spotTourStepping;

        /// <summary>The grid a race file's spotlight goes on: the one picked for it (clicked in its spotlight), else the one named in the file.</summary>
        private object SpotGridFor(string path, SpotlightFile file)
        {
            string picked = SpotAutoFor(path).Grid;
            Vec3D near = playerPosition ?? new Vec3D(0, 0, 0);
            return (string.IsNullOrWhiteSpace(picked) ? null : game.FindGrid(picked, near)) ?? (string.IsNullOrWhiteSpace(file?.Ship) ? null : game.FindGrid(file.Ship, near));
        }

        private void StartSpotTour()
        {
            if (replaying) { controlsNote = "Stop the replay first"; return; }
            // One race a ship (its fastest), ships hidden from the leaderboard left out, in the running order;
            // only ships that are in this world.
            var hiddenShips = settings.SpotBoardHiddenShips ?? new List<string>();
            var found = new List<(string Path, string Ship, double Total, bool Burned)>();
            try
            {
                foreach (string file in Directory.GetFiles(SpotlightFolder, "*" + SpotlightExtension).Take(300))
                {
                    var f = ReadSpotlightCached(file);
                    if (f == null || string.IsNullOrWhiteSpace(f.Ship) || f.Total <= 0) continue;
                    if (hiddenShips.Any(h => string.Equals(h, f.Ship, StringComparison.OrdinalIgnoreCase))) continue;
                    if (f.Burned && settings.SpotTourSkipBurned) continue;   // (Settings > Race spotlight > Tour: skip burned races)
                    if (SpotGridFor(file, f) == null) continue;
                    found.Add((file, f.Ship, f.Total, f.Burned));
                }
            }
            catch (Exception e) { PluginLog.Info("Spotlight tour: race files not read: " + e.Message); }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (A ship's finished races come before its burned ones: a burned race's time is only how far it got.)
            var tour = found.OrderBy(x => x.Burned).ThenBy(x => x.Total).Where(x => seen.Add(x.Ship)).OrderBy(x => SpotRunIndex(x.Ship)).ThenBy(x => x.Ship, StringComparer.OrdinalIgnoreCase).Select(x => x.Path).ToList();
            if (tour.Count == 0) { controlsNote = "No race file's ship is in this world (a ship is found by its name; or open a race's spotlight and click its ship once)"; return; }
            spotTour = tour;
            spotTourAt = -1;
            PluginLog.Info($"Spotlight tour: {tour.Count} ship(s)");
            SpotTourStep();
        }

        private const double TourClimbWait = 1.5;   // the newcomer: seconds at the bottom before it starts to climb
        private double TourClimbStep => Math.Clamp(settings.SpotBoardClimbSeconds, 0.3, 6);   // then a place every this long

        /// <summary>
        /// On the tour, the leaderboard as it stands so far: only the ships that have had their turn, this one
        /// included (null when not on the tour). Others = the earlier ships, fastest first; Current = this ship's row.
        /// </summary>
        private (List<(string Path, string Ship, string Pilot, double Total)> Others, (string Path, string Ship, string Pilot, double Total)? Current)? TourBoard()
        {
            if (spotTour == null || spotlight == null || spotTourAt < 0 || spotTourAt >= spotTour.Count) return null;
            var ran = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= spotTourAt; i++)
            {
                string ship = ReadSpotlightCached(spotTour[i])?.Ship;
                if (!string.IsNullOrWhiteSpace(ship)) ran.Add(ship);
            }
            var board = SpotLeaderboard().Where(x => ran.Contains(x.Ship ?? "")).ToList();
            int at = board.FindIndex(x => x.Path == spotlightPath);
            if (at < 0) at = board.FindIndex(x => string.Equals(x.Ship, spotlight.Ship ?? "?", StringComparison.OrdinalIgnoreCase));
            if (at < 0) return (board, null);
            var current = board[at];
            board.RemoveAt(at);
            return (board, current);
        }

        private void EndSpotTour(string note)
        {
            spotTour = null;
            spotTourNext = false;
            spotTourPhase = 0;
            spotTourPodiumUntil = 0;
            if (orbitCam && spotTourHome.HasValue) StopOrbitCam(null);   // (back in the seat, so the ship can be put back)
            if (spotTourHome.HasValue && spotTourHomeGrid != null)
            {
                // (The last ship, or the one flying when the tour was stopped: back to where it was parked.)
                spotTourReturn = spotTourHome; spotTourReturnGrid = spotTourHomeGrid;
                spotTourReturnAt = game.TimeSeconds + 0.6; spotTourCameraDropped = false;
                spotTourHome = null; spotTourHomeGrid = null;
            }
            if (analyserMode && spotlight != null) SetAnalyserMode(false, playerPosition);
            if (note != null) Notify(note, HudColor.Cyan);
        }

        // The tour's step for a ship: 1 getting you into its seat, 2 seated (a moment to settle, then its spotlight
        // starts with the race flown by the autopilot and the cinematic), 3 playing.
        private int spotTourPhase;
        private double spotTourPhaseAt, spotTourRaceOverAt;
        private object spotTourGrid;
        private bool spotTourLeft, spotTourSat, spotTourRaceSeen;

        /// <summary>The tour's next ship: you're put in its seat, then its spotlight starts (UpdateSpotTour carries it on).</summary>
        private void SpotTourStep()
        {
            spotTourPodiumUntil = 0;
            // The ship that just had its turn goes back to where it was parked.
            if (spotTourHome.HasValue && spotTourHomeGrid != null)
            {
                spotTourReturn = spotTourHome; spotTourReturnGrid = spotTourHomeGrid;
                spotTourReturnAt = game.TimeSeconds + 0.6; spotTourCameraDropped = false;
                spotTourHome = null; spotTourHomeGrid = null;
            }
            // The spotlight that just played is closed first (its camera let go, you back in that ship's seat).
            if (orbitCam) StopOrbitCam(null);
            if (analyserMode && spotlight != null)
            {
                spotTourStepping = true;
                try { SetAnalyserMode(false, playerPosition); } finally { spotTourStepping = false; }
            }
            while (spotTour != null)
            {
                spotTourAt++;
                if (spotTourAt >= spotTour.Count) { EndSpotTour("Spotlight tour finished"); return; }
                string path = spotTour[spotTourAt];
                var file = ReadSpotlightCached(path);
                object grid = file == null ? null : SpotGridFor(path, file);
                if (grid == null) continue;   // (gone since the tour started)
                spotTourGrid = grid;
                spotTourPhase = 1;
                spotTourPhaseAt = game.TimeSeconds;
                spotTourLeft = spotTourSat = spotTourRaceSeen = false;
                spotTourNext = false;
                Notify($"Spotlight tour: {spotTourAt + 1} of {spotTour.Count}, {file.Ship}", HudColor.Cyan);
                return;
            }
        }

        // Each ship is put back where it was parked once its turn is over (asked for here, done by UpdateSpotTour
        // once you're back in its seat from the cinematic's camera).
        private (Vec3D Seat, Vec3D Forward, Vec3D Up)? spotTourHome, spotTourReturn;
        private object spotTourHomeGrid, spotTourReturnGrid;
        private double spotTourReturnAt;
        private bool spotTourCameraDropped;

        private void UpdateSpotTour()
        {
            double now = game.TimeSeconds;
            if (spotTourReturn.HasValue)
            {
                if (now - spotTourReturnAt > 10)
                {
                    // (Never got back to its seat: left where it is.)
                    PluginLog.Info($"Spotlight tour: ship not put back (seated {game.Seated}, spectator {game.InSpectator}, same ship {ReferenceEquals(game.OwnGrid, spotTourReturnGrid)}, autopilot {autopilot}, camera {orbitCam})");
                    spotTourReturn = null; spotTourPhaseAt = now;
                }
                else if (now >= spotTourReturnAt + 1 && !spotTourCameraDropped && (orbitCam || game.InSpectator))
                {
                    // Still on a camera a second later: let go of it, so you're back in the seat.
                    spotTourCameraDropped = true;
                    if (orbitCam) StopOrbitCam(null); else game.EndSpectator();
                }
                else if (now >= spotTourReturnAt && game.Seated && !game.InSpectator && ReferenceEquals(game.OwnGrid, spotTourReturnGrid) && !autopilot)
                {
                    var home = spotTourReturn.Value;
                    spotTourReturn = null;
                    bool putBack = game.TeleportShip(home.Seat, home.Forward, home.Up);
                    if (putBack) stopShipAt = now + 0.5;
                    PluginLog.Info(putBack ? "Spotlight tour: ship put back where it was parked" : "Spotlight tour: couldn't put the ship back");
                    spotTourPhaseAt = now;   // (a moment for it to land there before you're taken out of the seat)
                }
                else if (spotTour != null && spotTourPhase == 1) return;   // (the next ship waits for this)
            }
            if (spotTour == null || spotTourPhase == 0) return;
            void Skip(string why)
            {
                Notify("Spotlight tour: " + why + ", on to the next ship", HudColor.Yellow);
                SpotTourStep();
            }
            if (spotTourPhase == 1)
            {
                bool seated = game.Seated;
                if (seated && ReferenceEquals(game.OwnGrid, spotTourGrid)) { spotTourPhase = 2; spotTourPhaseAt = now; return; }
                if (seated)
                {
                    // In another ship's seat: out of it first.
                    if (!spotTourLeft && now - spotTourPhaseAt > 0.5) { spotTourLeft = true; spotTourPhaseAt = now; if (!game.LeaveSeat()) { Skip("couldn't leave the seat"); return; } }
                    else if (now - spotTourPhaseAt > 5) Skip(spotTourSat ? "ended up in another ship's seat" : "still in the old seat");
                    return;
                }
                // On foot: beside the ship (in case the game wants you near it), then into its seat.
                if (!spotTourSat && now - spotTourPhaseAt > 0.6)
                {
                    spotTourSat = true; spotTourPhaseAt = now;
                    if (game.TryGetGridPose(spotTourGrid, out Vec3D centre, out Vec3D gx, out _, out Vec3D gz, out double radius, out Vec3D gravityDown))
                    {
                        Vec3D upWay = gravityDown.Length > 0.1 ? (gravityDown * -1).Normalized() : new Vec3D(0, 1, 0);
                        Vec3D side = gz - upWay * Vec3D.Dot(gz, upWay);
                        if (side.Length < 0.2) side = gx - upWay * Vec3D.Dot(gx, upWay);
                        side = side.Length > 1e-6 ? side.Normalized() : new Vec3D(1, 0, 0);
                        Vec3D stand = centre + side * Math.Max(6, radius * 1.6) + upWay * Math.Max(2, radius * 0.5);
                        game.TeleportCharacter(stand, centre - stand, upWay);
                    }
                    if (!game.SitInGrid(spotTourGrid)) Skip("no free cockpit on this ship");
                }
                else if (spotTourSat && now - spotTourPhaseAt > 6) Skip("couldn't get into this ship's seat");
                return;
            }
            if (spotTourPhase == 2)
            {
                if (now - spotTourPhaseAt < 0.8) return;   // (a moment for the game to say this is your ship)
                string path = spotTour[spotTourAt];
                // Where it's parked now: it's put back here when its turn is over.
                if (game.TryGetSeatPose(out Vec3D homeSeat, out Vec3D homeForward, out Vec3D homeUp)) { spotTourHome = (homeSeat, homeForward, homeUp); spotTourHomeGrid = spotTourGrid; }
                else { spotTourHome = null; spotTourHomeGrid = null; }
                spotTourStepping = true;
                try { StartSpotlight(path, true); } finally { spotTourStepping = false; }
                if (spotlight == null) { Skip("its race file couldn't be read"); return; }
                // The tour's own cinematic (Race tab > Spotlight tour) films every ship, whatever each race has set.
                string tourCine = settings.SpotTourCinematic;
                if (!string.IsNullOrEmpty(tourCine) && Cinematics().Any(x => x.Name == tourCine))
                {
                    spotAutoCine = tourCine;
                    spotAutoCineAt = now + (spotAutoRaceAt.HasValue ? 1.2 : 0.3);
                    spotCameraAfterRace = false;
                }
                // No cinematic of its own: the current one (Cinematics tab), so every ship on the tour is filmed.
                else if (spotAutoCine == null && CineCurrent != null && CineCurrent.Shots.Count > 0)
                {
                    spotAutoCine = CineCurrent.Name;
                    spotAutoCineAt = now + (spotAutoRaceAt.HasValue ? 1.2 : 0.3);
                    spotCameraAfterRace = false;
                }
                spotTourPhase = 3; spotTourPhaseAt = now; spotTourRaceOverAt = 0;
                return;
            }
            // Playing. With the race flown: on to the next a few seconds after it finishes. No race (no line for it
            // here): once its pages have been through once.
            if (autopilot) { spotTourRaceSeen = true; spotTourRaceOverAt = 0; }
            else if (spotTourRaceSeen && !spotAutoRaceAt.HasValue)
            {
                if (spotTourRaceOverAt == 0) spotTourRaceOverAt = now;
                else if (now - spotTourRaceOverAt > 5) TourShipDone(now);
            }
            else if (!spotTourRaceSeen && !spotAutoRaceAt.HasValue && now - spotTourPhaseAt > 8 && spotTourNext) TourShipDone(now);
        }

        // The podium: after the tour's last ship, a card with the top three before the tour finishes.
        private double spotTourPodiumUntil;
        private bool SpotPodiumOn => spotTour != null && spotlight != null && spotTourPodiumUntil > 0 && game.TimeSeconds < spotTourPodiumUntil;

        /// <summary>A tour ship's turn is over: on to the next, or (after the last one) the podium first.</summary>
        private void TourShipDone(double now)
        {
            bool lastShip = spotTour != null && spotTourAt >= spotTour.Count - 1;
            if (lastShip && settings.SpotTourPodium && spotTourPodiumUntil == 0 && TourPodium().Count >= 2)
            {
                spotTourPodiumUntil = now + Math.Clamp(settings.SpotTourPodiumSeconds, 3, 60);
                return;
            }
            if (spotTourPodiumUntil > 0 && now < spotTourPodiumUntil) return;   // (the podium is up)
            SpotTourStep();
        }

        /// <summary>The tour's top three (fewer if fewer ships finished), fastest first.</summary>
        private List<(string Ship, string Pilot, double Total)> TourPodium()
        {
            var podium = new List<(string Ship, string Pilot, double Total)>();
            var tourBoard = TourBoard();
            if (tourBoard == null) return podium;
            var finishers = tourBoard.Value.Others.ToList();
            if (tourBoard.Value.Current.HasValue) finishers.Add(tourBoard.Value.Current.Value);
            foreach (var finisher in finishers.OrderBy(x => x.Total).Take(3)) podium.Add((finisher.Ship, finisher.Pilot, finisher.Total));
            return podium;
        }

        private void UpdateSpotlight(HudModel model)
        {
            double now = game.TimeSeconds;
            bool focus = game.HasFocus && !controlsOpen && !game.IsPaused;
            bool left = focus && game.IsKeyDown(0x01);
            if (left && !spotLeftDown && game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out _))
            {
                // Left click: the grid you're looking at (your own ship when you look at nothing from its seat).
                object g = game.LookedAtGrid(eye, dir, 3000, game.OwnGrid == null) ?? game.OwnGrid;
                if (g != null && !ReferenceEquals(g, spotGrid))
                {
                    SpotOn(g, now, false);
                    // Remembered as this race's ship (for next time, and for the spotlight tour).
                    string pickedName = game.GridName(g), fileKey = spotlightPath == null ? null : Path.GetFileName(spotlightPath);
                    if (fileKey != null && !string.IsNullOrWhiteSpace(pickedName) && spotTour == null)
                    {
                        settings.SpotlightAuto ??= new Dictionary<string, GateSettings.SpotAuto>();
                        if (!settings.SpotlightAuto.TryGetValue(fileKey, out var remembered) || remembered == null) settings.SpotlightAuto[fileKey] = remembered = new GateSettings.SpotAuto();
                        if (remembered.Grid != pickedName)
                        {
                            remembered.Grid = string.Equals(pickedName, spotlight.Ship, StringComparison.OrdinalIgnoreCase) ? null : pickedName;
                            settings.Save(settingsPath);
                            Notify($"This race's spotlight ship is now {pickedName}", HudColor.Cyan);
                        }
                    }
                }
                else if (g != null && orbitCam && ocGrid == null) OrbitTarget(g, true);   // let go with right click, picked again
            }
            spotLeftDown = left;
            string who = spotlight.Ship ?? "Ship";
            if (spotGrid == null)
            {
                model.Analyser = new HudModel.AnalyserPanel
                {
                    Heading = "RACE SPOTLIGHT", TitleA = $"{who}  -  {spotlight.Course}",
                    Hint = "Look at the ship that flew this race and left click to put the spotlight on it   Esc: exit",
                };
                return;
            }
            if (now - spotStatsAt > 2) { spotStats = game.AnalyseGrid(spotGrid) ?? spotStats; spotStatsAt = now; }

            // Pages, one after another: the race, its stats (when it has any), the lap replay (when the file has
            // the laps' paths), the ship. Page numbers: 0 results, 1 stats, 2 ship, 4 lap replay (3 = hidden).
            bool hasStats = spotlight.Stats.Count > 0;
            var replayLaps = spotlight.MapGates != null ? ReplayLaps(spotlight) : new List<int>();
            bool hasReplay = replayLaps.Count > 0;
            var graphs = SpotGraphPanel();
            bool hasGraphs = graphs != null;
            // The pages in the order (and with the times) set on the Race tab, leaving out any this race can't show.
            bool Available(int pg) => pg == 1 ? hasStats : pg == 4 ? hasReplay : pg == 5 ? hasGraphs : pg >= 10 ? SpotPageHasData(pg) : pg != 3;
            var pageSettings = SpotPages();
            var order = pageSettings.Where(x => x.On && Available(x.Page)).Select(x => x.Page).ToList();
            // Every page switched off: no panel at all (unless a cinematic's Panel effect or saving pictures asks for one).
            bool noPages = order.Count == 0;
            double PageLength(int pg)
            {
                var set = pageSettings.FirstOrDefault(x => x.Page == pg);
                if (set != null && set.Seconds > 0.1) return set.Seconds;
                // (The leaderboard bringing its ships on one at a time: long enough for them all, then a look at the result.)
                if (pg == 14 && TourBoard() is var climbBoard && climbBoard != null)
                    return Math.Max(Math.Max(2, settings.SpotlightPageSeconds), TourClimbWait + climbBoard.Value.Others.Count * TourClimbStep + 4);
                if (pg == 14 && settings.SpotBoardReveal) return Math.Max(Math.Max(2, settings.SpotlightPageSeconds), SpotLeaderboard().Count * SpotRevealStep + 4);
                return pg == 4 ? replayLaps.Sum(i => LapPlaySeconds(spotlight.Laps[i]) + LapHoldSeconds)
                     : pg == 8 || pg == 9 ? 4.5
                     : Math.Max(2, settings.SpotlightPageSeconds);
            }
            // A cinematic playing with Panel effects picks the page itself at those times (or hides the panel).
            // The pages wait with the auto race and the cinematic (Settings > Race spotlight > Wait before...): they
            // start, from the first page, as those set off.
            if (spotExport == null && (spotAutoRaceAt.HasValue || spotAutoCineAt.HasValue)) { spotPage = 0; spotPageAt = now; return; }
            bool podiumNow = SpotPodiumOn && spotExport == null;
            if (podiumNow) { order.Clear(); order.Add(16); spotPage = 0; }   // (the tour's podium has the panel to itself)
            int cinePage = podiumNow ? -1 : CinePanelNow();
            if (cinePage == 3) return;
            if (cinePage >= 0 && cinePage != 3 && !Available(cinePage)) cinePage = 2;
            if (noPages && spotExport == null && cinePage < 0 && !podiumNow) return;
            if (spotExport != null)
            {
                // Saving every page as a picture: each page in turn, a second each, the picture taken halfway.
                if (spotExportIndex >= spotExport.Count) { FinishSpotExport(); return; }
                cinePage = -1;
                int want = spotExport[spotExportIndex];
                int at = order.IndexOf(want);
                if (at < 0) { order.Add(want); at = order.Count - 1; }
                if (spotPage % order.Count != at) { spotPage = at; spotPageAt = now; }
            }
            if (spotExport != null) { /* the export picks the page */ }
            else if (cinePage >= 0)
            {
                int forced = order.IndexOf(cinePage);
                if (forced < 0) { order.Add(cinePage); forced = order.Count - 1; }
                if (forced >= 0 && forced != spotPage % order.Count) { spotPage = forced; spotPageAt = now; }
                else if (cinePage == 4 && now - spotPageAt >= PageLength(4)) spotPageAt = now;   // held on the replay: play it again
            }
            else if (now - spotPageAt >= PageLength(order[spotPage % order.Count]))
            {
                spotPage = (spotPage + 1) % order.Count; spotPageAt = now;
                if (spotPage == 0 && spotTour != null) spotTourNext = true;   // (through its pages once: the tour moves on)
            }
            int page = order[spotPage % order.Count];
            var p = new HudModel.AnalyserPanel { SlideSeconds = settings.SpotlightSlideSeconds };
            int bestLap = 0;
            // (A burned race's last lap wasn't finished: never its best.)
            for (int i = 1; i < spotlight.Laps.Count - (spotlight.Burned ? 1 : 0); i++) if (spotlight.Laps[i].Time < spotlight.Laps[bestLap].Time) bestLap = i;
            // (The spotlight is for people watching, not the racer: no dates, no "best lap" call-outs, and no colour
            // codes (purple best, green / red) they wouldn't know the meaning of. Every value is plain white.)
            void Add(string label, string value, HudColor? colour = null) => p.Rows.Add((label, value, null, HudColor.White, HudColor.White));
            if (page == 0)
            {
                p.Heading = "RACE RESULTS";
                p.TitleA = who;
                Add("COURSE", spotlight.Course ?? "-");
                if (!string.IsNullOrWhiteSpace(spotlight.Pilot)) Add("PILOT", spotlight.Pilot);
                int laps = spotlight.Laps.Count;
                if (spotlight.Burned)
                {
                    Add("RESULT", $"BURNED  (race reset on lap {laps})");
                    Add("TIME BEFORE THE RESET", LapTimer.Format(spotlight.Total));
                }
                else Add(laps == 1 ? "RACE TIME (1 LAP)" : $"RACE TIME ({laps} LAPS)", LapTimer.Format(spotlight.Total), HudColor.Green);
                if (laps > 0)
                {
                    int best = 0;
                    for (int i = 1; i < laps; i++) if (spotlight.Laps[i].Time < spotlight.Laps[best].Time) best = i;
                    int missed = spotlight.Laps.Sum(x => x.Missed), crashes = spotlight.Laps.Sum(x => x.Crashes);
                    double penalty = spotlight.Laps.Sum(x => x.Penalty);
                    Add("PENALTIES", missed + crashes == 0 ? "none" : $"{missed} missed, {crashes} crash{(crashes == 1 ? "" : "es")}  (+{penalty:0.#} s)",
                        missed + crashes == 0 ? HudColor.Green : HudColor.Red);
                    for (int i = 0; i < laps && i < 12; i++)
                    {
                        var lap = spotlight.Laps[i];
                        Add($"  LAP {i + 1}{(spotlight.Burned && i == laps - 1 ? " (BURNED)" : "")}", LapTimer.Format(lap.Time) + (lap.Penalty > 0 ? $"  (+{lap.Penalty:0.#} s)" : ""), i == best && laps > 1 ? HudColor.Purple : (HudColor?)null);
                    }
                    if (laps > 12) Add($"  ...and {laps - 12} more laps", "");
                }
            }
            else if (page == 5)
            {
                // Race graphs: speed round the lap and the time gap round the lap, as the results had them.
                p.Heading = "RACE GRAPHS";
                p.TitleA = who;
                p.Graphs = graphs;
            }
            else if (page == 4)
            {
                // Lap replay: each lap flown again on the course map, one after another.
                p.Heading = "LAP REPLAY";
                p.TitleA = who;
                double into = Math.Max(0, now - spotPageAt);
                int pick = replayLaps[replayLaps.Count - 1];
                double local = 0;
                foreach (int i in replayLaps)
                {
                    double len = LapPlaySeconds(spotlight.Laps[i]) + LapHoldSeconds;
                    if (into < len) { pick = i; local = into; break; }
                    into -= len;
                    local = len;
                }
                if (spotExport != null && replayLaps.Contains(bestLap)) { pick = bestLap; local = LapPlaySeconds(spotlight.Laps[bestLap]); }   // a picture: the best lap, all of it
                var lap = spotlight.Laps[pick];
                double share = Math.Clamp(local / LapPlaySeconds(lap), 0, 1);
                int laps = spotlight.Laps.Count, best = 0;
                for (int i = 1; i < laps; i++) if (spotlight.Laps[i].Time < spotlight.Laps[best].Time) best = i;
                var line = lap.Map;
                double endT = line[line.Count - 1][2], at = share * endT;
                int done = 1;
                while (done < line.Count && line[done][2] <= at) done++;
                double dx = line[line.Count - 1][0], dy = line[line.Count - 1][1];
                if (done < line.Count)
                {
                    var a = line[done - 1]; var b = line[done];
                    double f = b[2] > a[2] ? (at - a[2]) / (b[2] - a[2]) : 0;
                    dx = a[0] + (b[0] - a[0]) * f; dy = a[1] + (b[1] - a[1]) * f;
                }
                var map = new HudModel.AnalyserMap
                {
                    Key = $"{spotlightPath}|{pick}", Done = done, DotX = dx, DotY = dy,
                    Colour = HudColor.Cyan, ShowKey = true,
                };
                foreach (var pt in line) map.Line.Add((pt[0], pt[1]));
                foreach (var g in spotlight.MapGates) if (g != null && g.Length >= 3) map.Gates.Add((g[0], g[1], (int)g[2]));
                // Also on the replay (they used to be pages of their own): the course card's numbers, the speed trap
                // and the lap's trouble spots, marked on the map.
                var cpGatesHere = spotlight.MapGates.Where(gt => gt != null && gt.Length >= 3 && (int)gt[2] == 2).ToList();
                if (lap.CrashSpots != null) foreach (var c in lap.CrashSpots) map.Marks.Add((c[0], c[1], 0));
                if (lap.Splits != null)
                    for (int k = 0; k < lap.Splits.Count && k < cpGatesHere.Count; k++)
                        if (!lap.Splits[k].HasValue) map.Marks.Add((cpGatesHere[k][0], cpGatesHere[k][1], 1));
                bool topHere = spotlight.TopSpeed > 0 && spotlight.TopAt != null && spotlight.TopLap == pick;
                if (topHere) map.Marks.Add((spotlight.TopAt[0], spotlight.TopAt[1], 2));
                p.Map = map;
                int cpCount = spotlight.Checkpoints > 0 ? spotlight.Checkpoints : cpGatesHere.Count;
                if (spotlight.CourseLength > 0) Add("COURSE", $"{Distance(spotlight.CourseLength)} a lap, {cpCount} checkpoint{(cpCount == 1 ? "" : "s")}"
                    + (spotlight.HeightRange >= 5 ? $", {Distance(spotlight.HeightRange)} high to low" : ""));
                Add("LAP", $"{pick + 1} of {laps}");
                Add("LAP TIME", $"{LapTimer.Format(Math.Min(share * lap.Time, lap.Time))}  /  {LapTimer.Format(lap.Time)}", share >= 1 ? HudColor.Green : (HudColor?)null);
                if (lap.Crashes > 0) Add("CRASHES", $"{lap.Crashes}  (red crosses)");
                if (lap.Missed > 0) Add("MISSED CHECKPOINTS", $"{lap.Missed}  (orange rings)");
                if (lap.Penalty > 0) Add("TIME ADDED", $"+{lap.Penalty:0.#} s");
                if (spotlight.TopSpeed > 0)
                    Add("TOP SPEED", $"{spotlight.TopSpeed:0} m/s  ({spotlight.TopSpeed * 3.6:0} km/h)" + (topHere ? "  (white ring)" : laps > 1 ? $"  (lap {spotlight.TopLap + 1})" : ""));
            }
            else if (page == 6)
            {
                // Ship card: the ship's headline numbers as big tiles.
                p.Heading = "SHIP CARD";
                p.TitleA = spotStats?.Name ?? who;
                p.Card = new List<(string, string, HudColor)>();
                void Tile(string label, string value, HudColor? colour = null) { if (!string.IsNullOrWhiteSpace(value)) p.Card.Add((label, value, HudColor.White)); }
                Tile("PILOT", spotlight.Pilot);
                Tile("COURSE", spotlight.Course);
                int laps = spotlight.Laps.Count;
                Tile(laps == 1 ? "RACE (1 LAP)" : $"RACE ({laps} LAPS)", LapTimer.Format(spotlight.Total), HudColor.Green);
                var st = spotStats;
                if (st != null && st.Mass > 0)
                {
                    Tile("MASS", st.Mass >= 1000 ? $"{st.Mass / 1000:0.0} t" : $"{st.Mass:0} kg");
                    double twr = st.Thrust[0] / (st.Mass * 9.81);
                    if (twr > 0) Tile("THRUST / WEIGHT", $"{twr:0.00}");
                    double acc = st.Thrust[0] / st.Mass;
                    if (acc > 0) Tile($"0 TO {GameOptions.Speed(100)}", $"{100 / acc:0.0} s");
                    Tile("BLOCKS / PCU", $"{st.Blocks} / {st.Pcu}");
                    var rc = RpiClassInUse;
                    if (rc != null)
                    {
                        double rpi = 0;
                        foreach (var kv in st.BlockCounts) if (rc.Values.TryGetValue(kv.Key, out double cost)) rpi += cost * kv.Value;
                        var steps = rc.Steps.Select(x => (x.Weight, x.Budget)).ToList();
                        double budget = GateSettings.RpiBudgetFor(st.Mass, rc.MinWeight, rc.Budget, steps);
                        Tile("CLASS", rc.Name);
                        Tile("RPI", budget > 0 ? $"{rpi:0.##} / {budget:0.##}" : $"{rpi:0.##}", budget > 0 && rpi > budget + 1e-9 ? HudColor.Red : HudColor.White);
                    }
                }
                double top = Math.Max(BestTopSpeed(spotlight.Ship), BestTopSpeed(st?.Name));
                if (top > 1) Tile("TOP SPEED", GameOptions.Speed(top));
            }
            else if (page == 7)
            {
                // Lap by lap: a bar a lap, longest for the best, coloured by how close each came to it.
                p.Heading = "LAP BY LAP";
                p.TitleA = who;
                p.Bars = new List<(string, string, double, HudColor)>();
                int laps = spotlight.Laps.Count;
                if (laps == 0) Add("", "No laps in this race");
                else
                {
                    double lo = spotlight.Laps.Min(x => x.Time), hi = spotlight.Laps.Max(x => x.Time);
                    for (int i = 0; i < laps && i < 20; i++)
                    {
                        var lap = spotlight.Laps[i];
                        double frac = hi - lo > 1e-6 ? 1 - 0.7 * (lap.Time - lo) / (hi - lo) : 1;
                        var colour = HudColor.Cyan;
                        string value = LapTimer.Format(lap.Time)
                            + (lap.Missed > 0 ? $"  {lap.Missed} missed" : "") + (lap.Crashes > 0 ? $"  {lap.Crashes} crash{(lap.Crashes == 1 ? "" : "es")}" : "");
                        p.Bars.Add(($"LAP {i + 1}", value, frac, colour));
                    }
                    double avg = spotlight.Laps.Average(x => x.Time);
                    Add("AVERAGE LAP", LapTimer.Format(avg));
                    if (laps > 1) Add("SPREAD", $"{hi - lo:0.000} s between the fastest and slowest laps");
                }
            }
            else if (page == 15 && SpotCompareNow() is SpotlightFile other)
            {
                // Head to head: this race against the compared one, lap by lap, then the totals.
                string them = other.Ship ?? "Other";
                if (string.Equals(them, who, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(other.Date)) them += " (" + other.Date + ")";
                p.Heading = "HEAD TO HEAD";
                // (A burned race, reset before its finish, says so beside its name: its times are only how far it got.)
                p.TitleA = $"{who}{(spotlight.Burned ? " (BURNED)" : "")}  vs  {them}{(other.Burned ? " (BURNED)" : "")}";
                p.Bars = new List<(string, string, double, HudColor)>();
                int laps = Math.Min(Math.Min(spotlight.Laps.Count, other.Laps.Count), 20);
                double lapGapMax = 0.001;
                for (int i = 0; i < laps; i++) lapGapMax = Math.Max(lapGapMax, Math.Abs(spotlight.Laps[i].Time - other.Laps[i].Time));
                for (int i = 0; i < laps; i++)
                {
                    double mine = spotlight.Laps[i].Time, theirs = other.Laps[i].Time, d = mine - theirs;
                    // The bar shows by how much the lap was won (green: this race, orange: the other).
                    var colour = d <= 0 ? HudColor.Green : HudColor.Orange;
                    bool burnedLap = (spotlight.Burned && i == spotlight.Laps.Count - 1) || (other.Burned && i == other.Laps.Count - 1);
                    p.Bars.Add(($"LAP {i + 1}{(burnedLap ? " (BURNED)" : "")}", $"{LapTimer.Format(mine)}  vs  {LapTimer.Format(theirs)}   {(d <= 0 ? "-" : "+")}{Math.Abs(d):0.000}",
                        0.15 + 0.85 * Math.Abs(d) / lapGapMax, colour));
                }
                double gap = spotlight.Total - other.Total;
                Add("TOTAL", $"{LapTimer.Format(spotlight.Total)}  vs  {LapTimer.Format(other.Total)}   {(gap <= 0 ? "-" : "+")}{Math.Abs(gap):0.000}");
                double myBest = spotlight.Laps.Count > 0 ? spotlight.Laps.Min(x => x.Time) : 0, theirBest = other.Laps.Count > 0 ? other.Laps.Min(x => x.Time) : 0;
                if (myBest > 0 && theirBest > 0) Add("BEST LAP", $"{LapTimer.Format(myBest)}  vs  {LapTimer.Format(theirBest)}");
                double myPen = spotlight.Laps.Sum(x => x.Penalty), theirPen = other.Laps.Sum(x => x.Penalty);
                if (myPen > 0 || theirPen > 0) Add("PENALTIES", $"{myPen:0.#} s  vs  {theirPen:0.#} s");
                if (spotlight.TopSpeed > 0 && other.TopSpeed > 0) Add("TOP SPEED", $"{GameOptions.Speed(spotlight.TopSpeed)}  vs  {GameOptions.Speed(other.TopSpeed)}");
                int wins = 0;
                for (int i = 0; i < laps; i++) if (spotlight.Laps[i].Time < other.Laps[i].Time) wins++;
                if (laps > 1) Add("LAPS WON", $"{wins} of {laps}");
                if (spotlight.Burned || other.Burned)
                {
                    if (spotlight.Burned) Add(who.ToUpperInvariant(), $"BURNED  (race reset on lap {spotlight.Laps.Count})");
                    if (other.Burned) Add(them.ToUpperInvariant(), $"BURNED  (race reset on lap {other.Laps.Count})");
                    Add("WINNER", spotlight.Burned && other.Burned ? "Neither finished" : spotlight.Burned ? $"{them}  ({who} burned)" : $"{who}  ({them} burned)");
                }
                else Add("WINNER", gap < 0 ? $"{who}, by {-gap:0.000} s" : gap > 0 ? $"{them}, by {gap:0.000} s" : "A dead heat");
            }
            else if (page == 16)
            {
                // The tour's podium: a title card with the top three.
                var podiumCard = new HudModel.TitleCard { Kicker = "PODIUM", Subtitle = spotlight.Course };
                var podiumShips = TourPodium();
                string[] podiumPlaces = { "1ST", "2ND", "3RD" };
                for (int place = 0; place < podiumShips.Count; place++)
                {
                    var onPodium = podiumShips[place];
                    string podiumPilot = string.IsNullOrWhiteSpace(onPodium.Pilot) ? "" : "  (" + onPodium.Pilot + ")";
                    if (place == 0)
                    {
                        podiumCard.Title = onPodium.Ship ?? "?";
                        podiumCard.TitleColour = HudColor.Yellow;
                        podiumCard.Lines.Add($"1ST   {LapTimer.Format(onPodium.Total)}{podiumPilot}");
                    }
                    else
                        podiumCard.Lines.Add($"{podiumPlaces[place]}   {onPodium.Ship}   {LapTimer.Format(onPodium.Total)}   +{onPodium.Total - podiumShips[0].Total:0.000}{podiumPilot}");
                }
                p.Cover = podiumCard;
            }
            else if (page == 8 || page == 9)
            {
                // Intro / outro: a title card in the middle of the screen.
                var card = new HudModel.TitleCard();
                int laps = spotlight.Laps.Count;
                if (page == 8)
                {
                    card.Kicker = "RACE SPOTLIGHT";
                    card.Title = spotStats?.Name ?? who;
                    card.Subtitle = spotlight.Course;
                    if (!string.IsNullOrWhiteSpace(spotlight.Pilot)) card.Lines.Add("Pilot  " + spotlight.Pilot);
                }
                else
                {
                    card.Kicker = spotlight.Burned ? $"BURNED  ·  RESET ON LAP {laps}" : laps == 1 ? "FINAL TIME  ·  1 LAP" : $"FINAL TIME  ·  {laps} LAPS";
                    card.Title = LapTimer.Format(spotlight.Total);
                    card.Subtitle = $"{spotStats?.Name ?? who}  ·  {spotlight.Course}";
                    int missed = spotlight.Laps.Sum(x => x.Missed), crashes = spotlight.Laps.Sum(x => x.Crashes);
                    if (missed + crashes > 0) card.Lines.Add($"{missed} missed checkpoint{(missed == 1 ? "" : "s")}, {crashes} crash{(crashes == 1 ? "" : "es")}");
                }
                p.Cover = card;
            }
            else if (page == 10)
            {
                // Course card: the course from above, with its length, checkpoints and climb.
                p.Heading = "THE COURSE";
                p.TitleA = spotlight.Course ?? who;
                var line = FastestMapLap()?.Map;
                p.Map = StillMap("course", line);
                int cps = spotlight.Checkpoints > 0 ? spotlight.Checkpoints : spotlight.MapGates?.Count(g => g != null && g.Length >= 3 && (int)g[2] == 2) ?? 0;
                if (spotlight.CourseLength > 0) Add("LENGTH", Distance(spotlight.CourseLength) + " a lap");
                Add("CHECKPOINTS", cps.ToString());
                Add("THIS RACE", spotlight.Laps.Count == 1 ? "1 lap" : $"{spotlight.Laps.Count} laps");
                if (spotlight.HeightRange >= 5) Add("HIGHEST TO LOWEST", Distance(spotlight.HeightRange));
            }
            else if (page == 11)
            {
                // Speed trap: the top speed, and where on the course it was hit.
                p.Heading = "SPEED TRAP";
                p.TitleA = spotStats?.Name ?? who;
                var lapMap = spotlight.TopLap >= 0 && spotlight.TopLap < spotlight.Laps.Count ? spotlight.Laps[spotlight.TopLap].Map : null;
                p.Map = StillMap("speed", lapMap ?? FastestMapLap()?.Map);
                if (p.Map != null && spotlight.TopAt != null) p.Map.Marks.Add((spotlight.TopAt[0], spotlight.TopAt[1], 2));
                double v = spotlight.TopSpeed;
                Add("TOP SPEED", $"{v:0} m/s");
                Add("THAT'S", $"{v * 3.6:0} km/h  ({v * 2.237:0} mph)");
                if (spotlight.Laps.Count > 1) Add("ON LAP", $"{spotlight.TopLap + 1} of {spotlight.Laps.Count}");
                Add("", "The ring on the map is where it was hit.");
            }
            else if (page == 12)
            {
                // Fun facts: the race in everyday numbers.
                p.Heading = "FUN FACTS";
                p.TitleA = spotStats?.Name ?? who;
                double dist = spotlight.Distance;
                if (dist > 0)
                {
                    Add("DISTANCE FLOWN", $"{Distance(dist)} in {LapTimer.Format(spotlight.Total)}");
                    if (dist >= 300) Add("THAT'S", $"{dist / 105:0} football pitches end to end");
                    double avg = spotlight.Total > 0 ? dist / spotlight.Total : 0;
                    if (avg > 0) Add("AVERAGE SPEED", $"{avg:0} m/s  ({avg * 3.6:0} km/h)");
                }
                if (spotlight.TopSpeed > 0) Add("TOP SPEED", $"{spotlight.TopSpeed:0} m/s  ({spotlight.TopSpeed * 3.6:0} km/h)");
                string StatOf(string label) => spotlight.Stats.FirstOrDefault(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))?.Value;
                string g = StatOf("MAX G");
                if (!string.IsNullOrWhiteSpace(g))
                {
                    var num = new string(g.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
                    Add("HARDEST TURN", double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gv) && gv >= 1.5
                        ? $"{g}: {gv:0.#} times the pull of gravity" : g);
                }
                string atTop = StatOf("AT TOP SPEED");
                if (!string.IsNullOrWhiteSpace(atTop)) Add("FLAT OUT", atTop);
                if (dist <= 0 && string.IsNullOrWhiteSpace(g)) Add("", "This race file is too old for fun facts: export a new one.");
            }
            else if (page == 13)
            {
                // Trouble spots: where the crashes and missed checkpoints happened.
                p.Heading = "TROUBLE SPOTS";
                p.TitleA = who;
                p.Map = StillMap("trouble", FastestMapLap()?.Map);
                var cpGates = spotlight.MapGates?.Where(gt => gt != null && gt.Length >= 3 && (int)gt[2] == 2).ToList() ?? new List<double[]>();
                int crashes = 0, missed = 0;
                for (int i = 0; i < spotlight.Laps.Count; i++)
                {
                    var lap = spotlight.Laps[i];
                    if (lap.CrashSpots != null)
                        foreach (var c in lap.CrashSpots) { p.Map?.Marks.Add((c[0], c[1], 0)); crashes++; }
                    if (lap.Splits != null)
                        for (int k = 0; k < lap.Splits.Count && k < cpGates.Count; k++)
                            if (!lap.Splits[k].HasValue) { p.Map?.Marks.Add((cpGates[k][0], cpGates[k][1], 1)); missed++; }
                }
                crashes = Math.Max(crashes, spotlight.Laps.Sum(x => x.Crashes));
                missed = Math.Max(missed, spotlight.Laps.Sum(x => x.Missed));
                Add("CRASHES", crashes == 0 ? "none" : $"{crashes}  (red crosses)");
                Add("MISSED CHECKPOINTS", missed == 0 ? "none" : $"{missed}  (orange rings)");
                double penalty = spotlight.Laps.Sum(x => x.Penalty);
                if (penalty > 0) Add("TIME ADDED", $"+{penalty:0.#} s");
                if (p.Map != null) p.Map.Key += "|" + p.Map.Marks.Count;
            }
            else if (page == 14)
            {
                // Leaderboard: every saved race on this course (same number of laps), fastest first.
                p.Heading = "LEADERBOARD";
                p.RowSlideSeconds = Math.Clamp(settings.SpotBoardClimbSeconds, 0.3, 6) * 0.85;   // (each slide takes most of its step: one steady climb)
                p.TitleA = $"{spotlight.Course}  -  {(spotlight.Laps.Count == 1 ? "1 lap" : $"{spotlight.Laps.Count} laps")}";
                var board = SpotLeaderboard();
                // The reveal: the ships come on one at a time in the order they ran (set on the Race tab; ships with
                // no place in it: slowest first), each going straight to its place on the board, the newest lit.
                int newest = -1;
                var onTour = TourBoard();
                if (onTour != null && onTour.Value.Current != null)
                {
                    // On the tour: the ships that have run, and this one starting at the bottom and climbing a tourPlace
                    // at a time to where its time puts it.
                    var tourOthers = onTour.Value.Others;
                    var tourCurrent = onTour.Value.Current.Value;
                    int tourPlace = tourOthers.Count(x => x.Total < tourCurrent.Total);
                    double tourOnPage = game.TimeSeconds - spotPageAt;
                    int tourClimbed = tourOnPage < TourClimbWait ? 0 : 1 + (int)((tourOnPage - TourClimbWait) / TourClimbStep);
                    int tourRow = spotExport != null ? tourPlace : Math.Max(tourPlace, tourOthers.Count - tourClimbed);
                    board = new List<(string Path, string Ship, string Pilot, double Total)>(tourOthers);
                    board.Insert(tourRow, tourCurrent);
                    newest = tourRow;
                }
                else if (settings.SpotBoardReveal && spotExport == null && board.Count > 1)
                {
                    var running = board.OrderBy(x => SpotRunIndex(x.Ship)).ThenByDescending(x => x.Total).ToList();
                    int on = Math.Clamp(1 + (int)((game.TimeSeconds - spotPageAt) / SpotRevealStep), 1, running.Count);
                    if (on < running.Count)
                    {
                        string newPath = running[on - 1].Path;
                        board = running.Take(on).OrderBy(x => x.Total).ToList();
                        newest = board.FindIndex(x => x.Path == newPath);
                    }
                }
                int mine = newest >= 0 ? newest : board.FindIndex(x => x.Path == spotlightPath);
                // (One row a ship, and this race isn't the ship's fastest: the ship's row is the one lit.)
                if (mine < 0) mine = board.FindIndex(x => string.Equals(x.Ship, spotlight.Ship ?? "?", StringComparison.OrdinalIgnoreCase));
                if (board.Count > 0)
                {
                    // Headline tiles, then a row a race: place badge (gold, silver, bronze), who, the gap to the
                    // fastest, the time, and a bar for its pace against the fastest. This race's row is lit.
                    double lead = board.Min(x => x.Total), last = board.Max(x => x.Total);
                    p.CardSmall = true;
                    p.Card = new List<(string, string, HudColor)>
                    {
                        ("FASTEST", LapTimer.Format(lead), new HudColor(255, 205, 70, 255)),
                        ("RACES", board.Count.ToString(), HudColor.White),
                        (newest >= 0 ? "NEW ENTRY" : "THIS RACE", mine >= 0 ? Place(mine + 1) : "-", HudColor.Cyan),
                        ("FIRST TO LAST", board.Count > 1 ? $"+{last - lead:0.000}s" : "-", HudColor.White),
                    };
                    for (int i = 0; i < board.Count; i++)
                    {
                        if (i >= 10 && i != mine) continue;
                        if (i >= 10 && i == mine) p.Rows.Add(("", "...", null, HudColor.White, HudColor.Grey));
                        var e = board[i];
                        string name = string.IsNullOrWhiteSpace(e.Pilot) ? e.Ship : $"{e.Ship}  ({e.Pilot})";
                        p.RowBars[p.Rows.Count] = (e.Total > 0 ? lead / e.Total : 0, i == mine ? HudColor.Cyan : HudColor.Grey);
                        p.Rows.Add(($"%{i + 1}|{name}", LapTimer.Format(e.Total), e.Total <= lead ? "leader" : $"+{e.Total - lead:0.000}", HudColor.White,
                            i == mine ? HudColor.Cyan : HudColor.White));
                    }
                }
                else p.Rows.Add(("", "No saved races on this course yet", null, HudColor.White, HudColor.Grey));
            }
            else if (page == 1)
            {
                p.Heading = "RACE STATS";
                p.TitleA = who;
                foreach (var row in spotlight.Stats.Where(x => !ForRacerOnly(x.Label)).Take(22)) Add((row.Label ?? "").ToUpperInvariant(), row.Value ?? "");
            }
            else
            {
                p.Heading = "THE SHIP";
                if (spotStats == null) { p.TitleA = who; Add("", "This grid's stats couldn't be read"); }
                else
                {
                    p.TitleA = spotStats.Name ?? who;
                    // (The analyser's own layout: headline tiles, sections, the thrust diagram and bars; no trim tips.)
                    AddShipRowsRich(p, spotStats, true);
                    DrawAnalysis(spotStats, new HudColor(255, 210, 60, 255));
                }
            }
            if (spotExport != null)
            {
                p.Exporting = true;
                p.SlideSeconds = 0;
                p.Hint = "Saving the pages as pictures...";
                double shown = now - spotPageAt;
                if (shown >= 0.6 && !spotExportTaken)
                {
                    spotExportTaken = true;
                    p.ExportPath = Path.Combine(spotExportFolder, $"{spotExportIndex + 1:00} {SpotPageName(page)}.png");
                }
                if (shown >= 1.2) { spotExportIndex++; spotExportTaken = false; }
            }
            model.Analyser = p;
        }

        /// <summary>Whether the race in the spotlight has what one of the newer pages needs (older race files don't).</summary>
        private bool SpotPageHasData(int pg)
        {
            var f = spotlight;
            if (f == null) return false;
            switch (pg)
            {
                case 10: return f.MapGates != null && FastestMapLap() != null;
                case 11: return f.TopSpeed > 0 && f.TopAt != null && f.MapGates != null;
                case 12: return f.Distance > 0 || f.Stats.Any(x => string.Equals(x.Label, "MAX G", StringComparison.OrdinalIgnoreCase));
                case 13: return f.MapGates != null && FastestMapLap() != null && f.Laps.Any(x => x.Crashes > 0 || x.Missed > 0);
                case 14:
                {
                    // (On the tour: not for the first ship, then only the ships that have run so far.)
                    var tourBoard = TourBoard();
                    if (tourBoard != null) return tourBoard.Value.Current != null && tourBoard.Value.Others.Count >= 1;
                    return SpotLeaderboard().Count >= 2;
                }
                case 15: return SpotCompareNow() != null;
                default: return true;
            }
        }

        /// <summary>The fastest lap that has a path on the map (for the still map pages).</summary>
        private SpotlightLap FastestMapLap() =>
            spotlight?.Laps.Where(x => x.Map != null && x.Map.Count >= 2).OrderBy(x => x.Time).FirstOrDefault();

        /// <summary>A still course map (no moving dot): the lap's whole path, the gates, room for marks.</summary>
        private HudModel.AnalyserMap StillMap(string what, List<double[]> line)
        {
            if (spotlight?.MapGates == null || line == null || line.Count < 2) return null;
            var map = new HudModel.AnalyserMap { Key = $"{spotlightPath}|{what}", Done = line.Count, HideDot = true, Colour = HudColor.Cyan };
            foreach (var pt in line) map.Line.Add((pt[0], pt[1]));
            var last = line[line.Count - 1];
            map.DotX = last[0]; map.DotY = last[1];
            foreach (var g in spotlight.MapGates) if (g != null && g.Length >= 3) map.Gates.Add((g[0], g[1], (int)g[2]));
            return map;
        }

        // The leaderboard for the race in the spotlight: worked out once a spotlight.
        private List<(string Path, string Ship, string Pilot, double Total)> spotBoard;
        private string spotBoardFor;

        private List<(string Path, string Ship, string Pilot, double Total)> SpotLeaderboard()
        {
            if (spotlight == null) return new List<(string, string, string, double)>();
            if (spotBoard != null && spotBoardFor == spotlightPath) return spotBoard;
            var board = new List<(string Path, string Ship, string Pilot, double Total)>();
            try
            {
                foreach (string other in Directory.GetFiles(SpotlightFolder, "*" + SpotlightExtension).Take(300))
                {
                    var f = string.Equals(other, spotlightPath, StringComparison.OrdinalIgnoreCase) ? spotlight : ReadSpotlightCached(other);
                    if (f == null || f.Total <= 0 || f.Burned || f.Laps.Count != spotlight.Laps.Count) continue;   // (a burned race has no finishing time to place)
                    if (!string.Equals(f.Course ?? "", spotlight.Course ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.GateCenter != null && spotlight.GateCenter != null
                        && (new Vec3D(f.GateCenter[0], f.GateCenter[1], f.GateCenter[2]) - new Vec3D(spotlight.GateCenter[0], spotlight.GateCenter[1], spotlight.GateCenter[2])).Length > 100) continue;
                    board.Add((other, f.Ship ?? "?", f.Pilot, f.Total));
                }
            }
            catch (Exception e) { PluginLog.Info("Race spotlight: leaderboard not read: " + e.Message); }
            if (spotlightPath != null && !spotlight.Burned && !board.Any(x => string.Equals(x.Path, spotlightPath, StringComparison.OrdinalIgnoreCase)))
                board.Add((spotlightPath, spotlight.Ship ?? "?", spotlight.Pilot, spotlight.Total));
            // Ships hidden from the board (Race tab > Race spotlight > Leaderboard) are left off, except the race being shown.
            var hiddenShips = settings.SpotBoardHiddenShips ?? new List<string>();
            bool Shown(string path) => spotlightPath != null && string.Equals(path, spotlightPath, StringComparison.OrdinalIgnoreCase);
            board.RemoveAll(x => !Shown(x.Path) && hiddenShips.Any(h => string.Equals(h, x.Ship, StringComparison.OrdinalIgnoreCase)));
            board = board.OrderBy(x => x.Total).ToList();
            // One row a ship: its fastest race, so a ship that beats its own time moves up instead of being listed twice.
            if (settings.SpotBoardBestPerShip)
            {
                var seenShips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                board = board.Where(x => seenShips.Add(x.Ship ?? "?")).ToList();
            }
            spotBoard = board;
            spotBoardFor = spotlightPath;
            return spotBoard;
        }

        /// <summary>Race stats that only mean something to the racer (comparisons with their own bests, dates), left off the spotlight.</summary>
        private static bool ForRacerOnly(string label)
        {
            string l = (label ?? "").ToUpperInvariant();
            return l.Contains("BEST") || l.Contains("DATE") || l.StartsWith("VS ") || l.Contains(" PB") || l.StartsWith("PB");
        }

        // (Course card 10, speed trap 11 and trouble spots 13 are part of the lap replay now, not pages of their own.)
        private static readonly int[] SpotPageDefaultOrder = { 8, 0, 14, 15, 1, 12, 6, 7, 5, 4, 2, 9 };
        private static string SpotPageName(int page) => page switch
        {
            0 => "Race results", 1 => "Race stats", 2 => "The ship", 4 => "Lap replay", 5 => "Race graphs",
            6 => "Ship card", 7 => "Lap by lap", 8 => "Intro", 9 => "Outro",
            10 => "Course card", 11 => "Speed trap", 12 => "Fun facts", 13 => "Trouble spots", 14 => "Leaderboard", 15 => "Head to head", _ => "Page " + page,
        };

        /// <summary>The spotlight's page settings, with any missing page added (new pages arrive switched on, before the outro).</summary>
        private List<GateSettings.SpotPage> SpotPages()
        {
            var list = settings.SpotlightPages ??= new List<GateSettings.SpotPage>();
            list.RemoveAll(x => x == null || !SpotPageDefaultOrder.Contains(x.Page));
            foreach (int pg in SpotPageDefaultOrder)
            {
                if (list.Any(x => x.Page == pg)) continue;
                int outro = list.FindIndex(x => x.Page == 9);
                var added = new GateSettings.SpotPage { Page = pg, On = true };
                if (pg != 9 && outro >= 0) list.Insert(outro, added); else list.Add(added);
            }
            return list;
        }

        // Saving every page as a picture.
        private List<int> spotExport;
        private int spotExportIndex;
        private bool spotExportTaken;
        private string spotExportFolder;

        private string StartSpotExport()
        {
            if (spotlight == null) return "Start a race spotlight first (Race tab > Race spotlight)";
            if (spotGrid == null) return "Pick the ship first: look at it and left click, then save the pictures";
            bool hasStats = spotlight.Stats.Count > 0, hasReplay = spotlight.MapGates != null && ReplayLaps(spotlight).Count > 0, hasGraphs = SpotGraphPanel() != null;
            spotExport = SpotPages().Where(x => x.On && (x.Page == 1 ? hasStats : x.Page == 4 ? hasReplay : x.Page == 5 ? hasGraphs : x.Page >= 10 ? SpotPageHasData(x.Page) : true)).Select(x => x.Page).ToList();
            if (spotExport.Count == 0) spotExport = new List<int> { 0 };
            string name = SafeFileName($"{spotlight.Ship} - {spotlight.Course} - {DateTime.Now:yyyy-MM-dd HHmm}");
            spotExportFolder = Path.Combine(SpotlightFolder, "Pictures", name);
            try { Directory.CreateDirectory(spotExportFolder); }
            catch (Exception e) { spotExport = null; return "Couldn't make the folder: " + e.Message; }
            spotExportIndex = 0;
            spotExportTaken = false;
            spotPageAt = game.TimeSeconds;
            PluginLog.Info($"Race spotlight: saving {spotExport.Count} pages to {spotExportFolder}");
            return null;
        }

        private void FinishSpotExport()
        {
            string folder = spotExportFolder;
            int count = spotExport?.Count ?? 0;
            spotExport = null;
            spotPage = 0;
            spotPageAt = game.TimeSeconds;
            Notify($"Saved {count} picture{(count == 1 ? "" : "s")} of the spotlight (Race tab > Race spotlight > Open folder > Pictures)", HudColor.Cyan);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
            catch { /* the note says where they are */ }
        }

        // ---------------------------------------------------------------- RPI (race performance index)

        private int rpiVersion;
        private ShipStats rpiLastShip;   // the ship last shown by the analyser (the designer checks it as you edit)
        private double rpiSaveAt = -1;
        private string RpiFolder => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "RpiClasses");
        private const string RpiExtension = ".rpiclass.json";
        private List<(string Name, string Path)> rpiFilesCache;
        private long rpiFilesAt;

        /// <summary>The RPI class in use (null = none).</summary>
        /// <summary>
        /// For the build menu: a block's RPI in the class in use, one value per size given (its keys), e.g.
        /// "RPI: 12  (class Sprint)", or "RPI: 12 / 3" for two sizes; banned / limited blocks say so. Null with no class in use.
        /// </summary>
        public string RpiTextFor(IReadOnlyList<string> keys)
        {
            var rc = RpiClassInUse;
            if (rc == null || keys == null || keys.Count == 0) return null;
            var parts = new List<string>();
            var notes = new List<string>();
            foreach (string key in keys)
            {
                double cost = rc.Values != null && rc.Values.TryGetValue(key, out double v) ? v : 0;
                parts.Add(cost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                if (rc.Limits != null && rc.Limits.TryGetValue(key, out int limit))
                    notes.Add(limit <= 0 ? "banned" : $"at most {limit}");
            }
            return $"RPI: {string.Join(" / ", parts)}  ({rc.Name})" + (notes.Count > 0 ? "  " + string.Join(", ", notes.Distinct()) : "");
        }

        private string gridRpiName, gridRpiText;
        private double gridRpiAt = -1;

        /// <summary>
        /// The RPI total of the grid with this name (the nearest one), for the game's grid info panel:
        /// "RPI: 41.5 / 60  (class)". Null when no class is picked or the grid isn't found. Worked out every 2 s at most.
        /// Game thread only.
        /// </summary>
        public string GridRpiText(string gridName)
        {
            var rc = RpiClassInUse;
            if (rc == null || string.IsNullOrWhiteSpace(gridName)) return null;
            double now = game.TimeSeconds;
            if (gridName == gridRpiName && gridRpiAt >= 0 && now - gridRpiAt < 2) return gridRpiText;
            gridRpiName = gridName;
            gridRpiAt = now;
            gridRpiText = null;
            try
            {
                object g = game.FindGrid(gridName.Trim(), playerPosition ?? Vec3D.Zero);
                var st = g != null ? game.AnalyseGrid(g) : null;
                if (st == null) return null;
                double total = 0;
                foreach (var kv in st.BlockCounts)
                    if (rc.Values.TryGetValue(kv.Key, out double cost)) total += cost * kv.Value;
                var steps = rc.Steps.Select(x => (x.Weight, x.Budget)).ToList();
                double budget = GateSettings.RpiBudgetFor(st.Mass, rc.MinWeight, rc.Budget, steps);
                string inv(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                gridRpiText = budget > 0
                    ? $"RPI: {inv(total)} / {inv(budget)}" + (total > budget + 1e-9 ? "  OVER" : "")
                    : $"RPI: {inv(total)}";
                gridRpiText += $"  ({rc.Name})";
            }
            catch (Exception e) { PluginLog.Info("Grid info RPI: " + e.Message); }
            return gridRpiText;
        }

        private GateSettings.RpiClass RpiClassInUse =>
            settings.RpiClassName == null ? null : settings.RpiClasses.FirstOrDefault(x => x.Name == settings.RpiClassName);

        private GateSettings.RpiClass RpiClassNamed(string name) =>
            name == null ? null : settings.RpiClasses.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        private void RpiChanged()
        {
            rpiVersion++;
            rpiFilesCache = null;
            settings.Save(settingsPath);
        }

        private static GateSettings.RpiClass ReadRpiFile(string path)
        {
            try
            {
                var rc = System.Text.Json.JsonSerializer.Deserialize<GateSettings.RpiClass>(SafeRead.Text(path));
                if (rc == null) return null;
                if (string.IsNullOrWhiteSpace(rc.Name)) rc.Name = Path.GetFileName(path).Replace(RpiExtension, "");
                rc.Name = rc.Name.Trim();
                CleanRpiClass(rc);
                return rc;
            }
            catch (Exception e) { PluginLog.Info("RPI class file couldn't be read (" + path + "): " + e.Message); return null; }
        }

        private static void CleanRpiClass(GateSettings.RpiClass rc)
        {
            rc.Values ??= new Dictionary<string, double>();
            rc.Limits ??= new Dictionary<string, int>();
            rc.Steps ??= new List<GateSettings.RpiStep>();
            rc.Steps.RemoveAll(x => x == null || x.Weight <= 0);
        }

        /// <summary>Adds a class that came from a file or the clipboard (replacing one of the same name) and puts it in use.</summary>
        private void AddRpiClass(GateSettings.RpiClass incoming, string how)
        {
            var sameName = RpiClassNamed(incoming.Name);
            if (sameName != null) settings.RpiClasses.Remove(sameName);
            settings.RpiClasses.Add(incoming);
            settings.RpiClassName = incoming.Name;
            RpiChanged();
            controlsNote = (sameName != null ? $"RPI class '{incoming.Name}' replaced" : $"RPI class '{incoming.Name}' {how}") + " and in use";
        }

        /// <summary>The analyser's RPI rows: total against the class budget, the minimum weight, and what the total is made of.</summary>
        private void AddRpiRows(HudModel.AnalyserPanel p, ShipStats a, ShipStats b)
        {
            var rc = RpiClassInUse;
            if (rc == null)
            {
                p.Rows.Add(("RPI", "pick a class: menu > RPI tab", b != null ? "" : null, HudColor.White, HudColor.White));
                return;
            }
            var steps = rc.Steps.Select(x => (x.Weight, x.Budget)).ToList();
            double minWeight = GateSettings.RpiLowestWeight(rc.MinWeight, steps);
            double BudgetOf(ShipStats st) => GateSettings.RpiBudgetFor(st.Mass, rc.MinWeight, rc.Budget, steps);
            double RpiOf(ShipStats st)
            {
                double total = 0;
                foreach (var kv in st.BlockCounts)
                    if (rc.Values.TryGetValue(kv.Key, out double cost)) total += cost * kv.Value;
                return total;
            }
            string Total(ShipStats st)
            {
                double r = RpiOf(st), budget = BudgetOf(st);
                if (budget <= 0) return $"{r:0.##}";
                return r <= budget + 1e-9 ? $"{r:0.##} / {budget:0.##}  ({budget - r:0.##} left)" : $"{r:0.##} / {budget:0.##}  ({r - budget:0.##} OVER)";
            }
            HudColor TotalColour(ShipStats st) => BudgetOf(st) <= 0 ? HudColor.White : RpiOf(st) <= BudgetOf(st) + 1e-9 ? HudColor.Green : HudColor.Red;
            p.Rows.Add(("RPI CLASS", rc.Name, b != null ? rc.Name : null, HudColor.White, HudColor.White));
            p.Rows.Add(("RPI", Total(a), b != null ? Total(b) : null, b != null ? TotalColour(b) : HudColor.White, TotalColour(a)));
            if (minWeight > 0)
            {
                // (To the whole kilogram, as it's shown: a ship a few grams short of the minimum isn't under weight.)
                string Weight(ShipStats st) => Math.Round(st.Mass) >= minWeight ? $"OK  ({Math.Round(st.Mass) - minWeight:0} kg over {minWeight:0} kg)" : $"UNDER by {minWeight - Math.Round(st.Mass):0} kg  (min {minWeight:0} kg)";
                HudColor WeightColour(ShipStats st) => Math.Round(st.Mass) >= minWeight ? HudColor.Green : HudColor.Red;
                p.Rows.Add(("MIN WEIGHT", Weight(a), b != null ? Weight(b) : null, b != null ? WeightColour(b) : HudColor.White, WeightColour(a)));
            }
            int Count(ShipStats st, string key) => st != null && st.BlockCounts.TryGetValue(key, out int n) ? n : 0;
            // Block limits: banned blocks and more of a block than the class allows.
            if (rc.Limits.Count > 0)
            {
                string Broken(ShipStats st)
                {
                    int broken = rc.Limits.Count(l => Count(st, l.Key) > l.Value);
                    return broken == 0 ? "OK" : $"{broken} BROKEN";
                }
                HudColor BrokenColour(ShipStats st) => rc.Limits.Any(l => Count(st, l.Key) > l.Value) ? HudColor.Red : HudColor.Green;
                p.Rows.Add(("BLOCK LIMITS", Broken(a), b != null ? Broken(b) : null, b != null ? BrokenColour(b) : HudColor.White, BrokenColour(a)));
                foreach (var limit in rc.Limits.OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (Count(a, limit.Key) <= limit.Value && Count(b, limit.Key) <= limit.Value) continue;
                    string Over(ShipStats st) => Count(st, limit.Key) > limit.Value
                        ? (limit.Value == 0 ? $"BANNED  (has {Count(st, limit.Key)})" : $"{Count(st, limit.Key)}  (max {limit.Value})") : "ok";
                    HudColor OverColour(ShipStats st) => Count(st, limit.Key) > limit.Value ? HudColor.Red : HudColor.Green;
                    p.Rows.Add(("  " + NameOf(limit.Key), Over(a), b != null ? Over(b) : null, b != null ? OverColour(b) : HudColor.White, OverColour(a)));
                }
            }
            // What it's made of: every block type on the grid(s) that costs RPI, most expensive first.
            string NameOf(string key)
            {
                string shown = a.BlockInfo.TryGetValue(key, out var ia) ? ia.Name : b != null && b.BlockInfo.TryGetValue(key, out var ib) ? ib.Name : null;
                return string.IsNullOrWhiteSpace(shown) ? key.Replace('_', ' ') : shown;
            }
            var keys = new HashSet<string>(a.BlockCounts.Keys);
            if (b != null) keys.UnionWith(b.BlockCounts.Keys);
            var costly = keys.Where(k => rc.Values.TryGetValue(k, out double cost) && cost != 0)
                .OrderByDescending(k => rc.Values[k] * Math.Max(Count(a, k), Count(b, k)))
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            int shownRows = 0;
            foreach (string k in costly)
            {
                if (++shownRows > 14) { p.Rows.Add(($"  ...and {costly.Count - 14} more", "", b != null ? "" : null, HudColor.White, HudColor.White)); break; }
                double each = rc.Values[k];
                string Line(ShipStats st) => Count(st, k) > 0 ? $"{Count(st, k)} x {each:0.##} = {Count(st, k) * each:0.##}" : "-";
                p.Rows.Add(("  " + NameOf(k), Line(a), b != null ? Line(b) : null, HudColor.White, HudColor.White));
            }
        }

        /// <summary>A grid's centre of mass (a cross) and its thrust each way (arrows from it, longer = more acceleration).</summary>
        /// <summary>
        /// What the analyser draws on the ship (left / right arrow changes it): thrust arrows; balance (where each
        /// direction's thrust acts against the centre of mass); the thrusters by the way they push; the heaviest
        /// blocks; the blocks the trim tip names; the blocks that cost RPI; its size box; gyroscopes and batteries.
        /// </summary>
        private void DrawShipOverlay(ShipStats s, int mode, HudModel model, HudColor colour)
        {
            if (s == null || s.Mass <= 0 || mode == 8) return;
            if (mode == 0) { DrawAnalysis(s, colour); return; }
            HudColor[] cols = { new HudColor(80, 230, 120, 255), new HudColor(255, 90, 80, 255), new HudColor(90, 170, 255, 255),
                                new HudColor(90, 170, 255, 255), new HudColor(255, 210, 60, 255), new HudColor(255, 210, 60, 255) };
            Vec3D[] ways = { s.Forward, s.Forward * -1, s.Right * -1, s.Right, s.Up, s.Up * -1 };
            string[] names = { "FORWARD", "BACKWARD", "LEFT", "RIGHT", "UP", "DOWN" };
            void Box(Vec3D c, double hx, double hy, double hz, HudColor col)
            {
                Vec3D x = s.GridX * hx, y = s.GridY * hy, z = s.GridZ * hz;
                var k = new Vec3D[8];
                for (int i = 0; i < 8; i++) k[i] = c + x * ((i & 1) != 0 ? 1 : -1) + y * ((i & 2) != 0 ? 1 : -1) + z * ((i & 4) != 0 ? 1 : -1);
                int[,] e = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
                for (int i = 0; i < 12; i++) game.DrawMarkerLine(k[e[i, 0]], k[e[i, 1]], col);
            }
            void Block(BlockSpot b, HudColor col) { double h = b.Size > 0 ? b.Size / 2 : 0.25; Box(s.SpotWorld(b), h, h, h, col); }
            void Tag(Vec3D at, string text, HudColor col)
            {
                if (game.TryProjectToScreen(at, out double x, out double y, out bool behind) && !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98)
                    model.Markers.Add(new HudModel.ScreenMarker { X = x, Y = y, Text = text, Colour = col, Quiet = true, World = at });
            }
            int SlotOf(BlockSpot b)
            {
                if (!b.ThrustDir.HasValue) return -1;
                Vec3D d = s.DirWorld(b.ThrustDir.Value);
                int bestSlot = 0; double bestDot = -2;
                for (int k = 0; k < 6; k++) { double dot = Vec3D.Dot(d, ways[k]); if (dot > bestDot) { bestDot = dot; bestSlot = k; } }
                return bestSlot;
            }
            double size = Math.Max(4, Math.Max(s.SizeX, Math.Max(s.SizeY, s.SizeZ)));
            Vec3D com = s.CenterOfMass;
            void Cross(Vec3D c, double r, HudColor col)
            {
                game.DrawMarkerLine(c - s.Right * r, c + s.Right * r, col);
                game.DrawMarkerLine(c - s.Up * r, c + s.Up * r, col);
                game.DrawMarkerLine(c - s.Forward * r, c + s.Forward * r, col);
            }
            switch (mode)
            {
                case 1:
                {
                    // Balance: the centre of mass, and for each direction where its thrusters' push acts. The further
                    // that is from the centre of mass (sideways to the push), the more the ship turns as it thrusts.
                    double r = Math.Clamp(size * 0.05, 0.5, 10);
                    Cross(com, r * 1.6, HudColor.White);
                    Tag(com + s.Up * r * 2, "CENTRE OF MASS", HudColor.White);
                    for (int k = 0; k < 6; k++)
                    {
                        Vec3D sum = Vec3D.Zero; double power = 0;
                        foreach (var b in s.Spots) if (b.ThrustPower > 0 && SlotOf(b) == k) { sum = sum + s.SpotWorld(b) * b.ThrustPower; power += b.ThrustPower; }
                        if (power <= 0) continue;
                        Vec3D at = sum * (1 / power);
                        Vec3D off = at - com;
                        off = off - ways[k] * Vec3D.Dot(off, ways[k]);   // sideways to the push
                        Cross(at, r, cols[k]);
                        game.DrawMarkerLine(com, at, cols[k]);
                        Tag(at, $"{names[k]}  {off.Length:0.0} m off centre", cols[k]);
                    }
                    break;
                }
                case 2:
                    // Thrusters, outlined in the colour of the way they push (as the thrust arrows), with a line that way.
                    foreach (var b in s.Spots)
                    {
                        int slot = SlotOf(b);
                        if (slot < 0) continue;
                        Block(b, cols[slot]);
                        Vec3D at = s.SpotWorld(b);
                        ThickLine(at, at + ways[slot] * Math.Max(1, b.Size * 1.5), Math.Clamp(size * 0.006, 0.03, 0.3), cols[slot]);
                    }
                    FlushThickLines();
                    break;
                case 3:
                {
                    // Weight: the heaviest blocks, green (light) to red (the heaviest).
                    var heavy = s.Spots.Where(b => b.Mass > 0).OrderByDescending(b => b.Mass).Take(60).ToList();
                    if (heavy.Count == 0) break;
                    double most = heavy[0].Mass;
                    foreach (var b in heavy)
                    {
                        double f = Math.Sqrt(b.Mass / most);
                        Block(b, new HudColor((byte)(80 + 175 * f), (byte)(230 - 150 * f), 80, 255));
                    }
                    Cross(com, Math.Clamp(size * 0.05, 0.5, 10), HudColor.White);
                    break;
                }
                case 4:
                    // The blocks the weight trim tip says to take off.
                    if (anaTipKey == null) { Tag(com, "no trim tip for this ship", HudColor.Grey); break; }
                    foreach (var b in s.Spots) if (b.Key == anaTipKey) Block(b, HudColor.Yellow);
                    break;
                case 5:
                {
                    // The blocks that cost RPI: yellow (cheap) to red (the dearest).
                    var rc = RpiClassInUse;
                    if (rc == null) { Tag(com, "pick an RPI class first", HudColor.Grey); break; }
                    double Cost(BlockSpot b) => rc.Values.TryGetValue(b.Key, out double c) ? c : 0;
                    var costly = s.Spots.Where(b => Cost(b) > 0).OrderByDescending(Cost).Take(80).ToList();
                    if (costly.Count == 0) break;
                    double most = Cost(costly[0]);
                    foreach (var b in costly)
                    {
                        double f = Cost(b) / most;
                        Block(b, new HudColor(255, (byte)(220 - 150 * f), (byte)(70 - 20 * f), 255));
                    }
                    break;
                }
                case 6:
                {
                    // Its size: the box round it, with its three lengths.
                    Vec3D c = s.BoxCenter;
                    double hx = s.SizeX / 2, hy = s.SizeY / 2, hz = s.SizeZ / 2;
                    Box(c, hx, hy, hz, HudColor.Cyan);
                    Tag(c - s.GridY * hy - s.GridZ * hz, $"{s.SizeX:0.#} m", HudColor.Cyan);
                    Tag(c - s.GridX * hx - s.GridZ * hz, $"{s.SizeY:0.#} m", HudColor.Cyan);
                    Tag(c - s.GridX * hx - s.GridY * hy, $"{s.SizeZ:0.#} m", HudColor.Cyan);
                    break;
                }
                case 7:
                    // Gyroscopes (cyan) and batteries / tanks (green): usually buried inside.
                    foreach (var b in s.Spots)
                    {
                        if (b.Kind == "Gyroscopes") Block(b, HudColor.Cyan);
                        else if (b.Kind == "Batteries and tanks") Block(b, HudColor.Green);
                    }
                    break;
            }
        }

        // Thick lines on the ship (the thrust arrows): each a ribbon of triangles, two flat strips crossed so it has
        // width from every side. Collected, then drawn in one go.
        private readonly List<Vec3D> anaTris = new List<Vec3D>();
        private readonly List<HudColor> anaTriColours = new List<HudColor>();

        private void ThickLine(Vec3D a, Vec3D b, double half, HudColor col)
        {
            Vec3D d = b - a;
            if (d.Length < 1e-6) return;
            d = d.Normalized();
            Vec3D any = Math.Abs(d.Y) < 0.9 ? new Vec3D(0, 1, 0) : new Vec3D(1, 0, 0);
            Vec3D s1 = Vec3D.Cross(d, any).Normalized() * half, s2 = Vec3D.Cross(d, s1).Normalized() * half;
            foreach (Vec3D side in new[] { s1, s2 })
            {
                Vec3D c0 = a - side, c1 = a + side, c2 = b + side, c3 = b - side;
                // (Both windings, so it shows from either side.)
                anaTris.Add(c0); anaTris.Add(c1); anaTris.Add(c2); anaTriColours.Add(col);
                anaTris.Add(c0); anaTris.Add(c2); anaTris.Add(c3); anaTriColours.Add(col);
                anaTris.Add(c0); anaTris.Add(c2); anaTris.Add(c1); anaTriColours.Add(col);
                anaTris.Add(c0); anaTris.Add(c3); anaTris.Add(c2); anaTriColours.Add(col);
            }
        }

        private void FlushThickLines()
        {
            if (anaTris.Count > 0) game.DrawMarkerTriangles(anaTris, anaTriColours, false);
            anaTris.Clear(); anaTriColours.Clear();
        }

        private void DrawAnalysis(ShipStats s, HudColor colour)
        {
            if (s.Mass <= 0) return;
            double size = Math.Max(4, Math.Max(s.SizeX, Math.Max(s.SizeY, s.SizeZ)));
            Vec3D c = s.CenterOfMass;
            double r = Math.Clamp(size * 0.06, 0.8, 15);
            game.DrawMarkerLine(c - s.Right * r, c + s.Right * r, colour);
            game.DrawMarkerLine(c - s.Up * r, c + s.Up * r, colour);
            game.DrawMarkerLine(c - s.Forward * r, c + s.Forward * r, colour);
            Vec3D[] ways = { s.Forward, s.Forward * -1, s.Right * -1, s.Right, s.Up, s.Up * -1 };
            HudColor[] cols = { new HudColor(80, 230, 120, 255), new HudColor(255, 90, 80, 255), new HudColor(90, 170, 255, 255),
                                new HudColor(90, 170, 255, 255), new HudColor(255, 210, 60, 255), new HudColor(255, 210, 60, 255) };
            double maxA = 0;
            for (int k = 0; k < 6; k++) maxA = Math.Max(maxA, s.Thrust[k] / s.Mass);
            if (maxA <= 0) return;
            for (int k = 0; k < 6; k++)
            {
                double acc = s.Thrust[k] / s.Mass;
                if (acc <= 0) continue;
                double len = size * 0.6 * acc / maxA;
                Vec3D tip = c + ways[k] * len;
                double thick = Math.Clamp(size * 0.008, 0.04, 0.5);
                ThickLine(c, tip, thick, cols[k]);
                // Arrow head.
                Vec3D side = Math.Abs(Vec3D.Dot(ways[k], s.Up)) > 0.9 ? s.Right : s.Up;
                double h = Math.Max(0.4, len * 0.12);
                ThickLine(tip, tip - ways[k] * h + side * h * 0.6, thick, cols[k]);
                ThickLine(tip, tip - ways[k] * h - side * h * 0.6, thick, cols[k]);
            }
            FlushThickLines();
        }

        /// <summary>"7.381s faster" / "3.204s slower".</summary>
        private static string FasterSlower(double d) => d < 0 ? $"{-d:0.000}s faster" : $"{d:0.000}s slower";

        /// <summary>
        /// Best ever time at each checkpoint, and best ever time for each sector (start to CP 1, CP 1 to CP 2, ...,
        /// last CP to the finish). Sectors only count when the checkpoints either side were flown in order.
        /// </summary>
        private void UpdateBestSplitsAndSectors(GateSettings.CourseRecord rec, LapRecord lap)
        {
            int n = timer.Checkpoints.Count;
            while (rec.BestSplits.Count < n) rec.BestSplits.Add(null);

            for (int i = 0; i < n && i < lap.Splits.Length; i++)
            {
                double? v = lap.Splits[i];
                if (v.HasValue && (!rec.BestSplits[i].HasValue || v.Value < rec.BestSplits[i].Value)) rec.BestSplits[i] = v;
            }
            var before = new List<double?>(rec.BestSectors);
            UpdateBestSectors(rec.BestSectors, lap, n);
            // Keep the path of each sector that just became the best (for the possible lap line).
            if (lapTrace == null || lapTrace.Count < 8) return;
            rec.BestSectorTraces ??= new List<List<double[]>>();
            while (rec.BestSectorTraces.Count < rec.BestSectors.Count) rec.BestSectorTraces.Add(null);
            for (int k = 0; k <= n && k < rec.BestSectors.Count; k++)
            {
                bool improved = rec.BestSectors[k].HasValue && (k >= before.Count || !before[k].HasValue || rec.BestSectors[k].Value < before[k].Value - 1e-9);
                if (!improved) continue;
                double start = k == 0 ? 0 : lap.Splits[k - 1].Value;
                double end = start + rec.BestSectors[k].Value;
                var part = new List<double[]>();
                foreach (var pt in lapTrace)
                {
                    if (pt.Length < 4 || pt[3] < start - 1e-6 || pt[3] > end + 1e-6) continue;
                    var c = (double[])pt.Clone();
                    c[3] = Math.Round(pt[3] - start, 3);
                    part.Add(c);
                }
                rec.BestSectorTraces[k] = part.Count >= 2 ? part : null;
            }
            possibleFor = null; possibleTrace = null;
        }

        // ---- The possible lap: every best sector's path joined up, with their times added up.
        private bool racePossible;
        private List<List<double[]>> possibleFor;
        private List<double[]> possibleTrace;

        private List<double[]> possibleBestFor;
        private double possibleSmoothFor = -1;

        private static Vec3D Pt(double[] p, int i = 0) => new Vec3D(p[i], p[i + 1], p[i + 2]);
        private static void SetPt(double[] p, Vec3D v, int i = 0) { p[i] = v.X; p[i + 1] = v.Y; p[i + 2] = v.Z; }
        private static double Ease(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

        /// <summary>
        /// Smooths one join of the possible line (two laps' paths meeting at a checkpoint, at line time "at"), over
        /// "w" seconds either side:
        /// 1. Where the two laps went through the gate differs: the end of the one before is eased forward and the
        ///    start of the one after back, so they meet halfway (inside the gate, as both were).
        /// 2. The ship's attitude is eased the same way, to halfway between the two.
        /// 3. The speed: the points in the window are given new times so the speed changes steadily from what it
        ///    was going in to what it is coming out, over the same time (the sector times don't change).
        /// </summary>
        /// <summary>
        /// The possible lap's end (the last sector into the finish) and its start (out of the start), smoothed into
        /// each other the same way as the joins between its sectors, so the line loops round without a step. Only
        /// the places are moved, not the times (the lap's timing stays as it is).
        /// </summary>
        private static void SmoothLoop(List<double[]> line, double w)
        {
            int n = line.Count;
            if (n < 8) return;
            double start = line[0][3], end = line[n - 1][3];
            if (end - start < w * 4) return;   // a lap too short to have room either side
            if ((Pt(line[n - 1]) - Pt(line[0])).Length > 400) return;   // not a loop (point to point)
            double step = Math.Max(1e-3, line[1][3] - line[0][3]);
            // The lap's tail, then its head carried on in time as if it were the next lap: one join in the middle.
            var tail = new List<int>();
            for (int i = 0; i < n; i++) if (end - line[i][3] <= w * 1.5) tail.Add(i);
            var head = new List<int>();
            for (int i = 0; i < n; i++) if (line[i][3] - start <= w * 1.5) head.Add(i);
            if (tail.Count < 3 || head.Count < 3 || head[head.Count - 1] >= tail[0]) return;
            var temp = new List<double[]>();
            foreach (int i in tail) temp.Add((double[])line[i].Clone());
            foreach (int i in head) { var c = (double[])line[i].Clone(); c[3] = c[3] - start + end + step; temp.Add(c); }
            SmoothJoin(temp, end, w);
            // Back into the line: the places and the ship's attitude, not the times.
            for (int k = 0; k < temp.Count; k++)
            {
                int i = k < tail.Count ? tail[k] : head[k - tail.Count];
                for (int j = 0; j < line[i].Length && j < temp[k].Length; j++) if (j != 3) line[i][j] = temp[k][j];
            }
        }

        private static void SmoothJoin(List<double[]> line, double at, double w)
        {
            int a = -1;
            for (int i = 0; i < line.Count; i++) if (line[i][3] <= at + 1e-6) a = i; else break;
            int b = a + 1;
            if (a < 1 || b + 1 >= line.Count) return;
            // The jump, less the normal move between the two points.
            double dtA = line[a][3] - line[a - 1][3], dtB = line[b + 1][3] - line[b][3], gapT = line[b][3] - line[a][3];
            if (dtA <= 1e-6 || dtB <= 1e-6) return;
            Vec3D vIn = (Pt(line[a]) - Pt(line[a - 1])) * (1 / dtA), vOut = (Pt(line[b + 1]) - Pt(line[b])) * (1 / dtB);
            Vec3D jump = Pt(line[b]) - Pt(line[a]) - (vIn + vOut) * (0.5 * gapT);
            bool axes = line[a].Length >= 13 && line[b].Length >= 13;
            Vec3D[] endA = null, startB = null, mid = null;
            if (axes)
            {
                endA = new[] { Pt(line[a], 4), Pt(line[a], 7), Pt(line[a], 10) };
                startB = new[] { Pt(line[b], 4), Pt(line[b], 7), Pt(line[b], 10) };
                mid = new Vec3D[3];
                for (int k = 0; k < 3; k++) mid[k] = (endA[k] + startB[k]).Length > 1e-6 ? (endA[k] + startB[k]).Normalized() : endA[k];
            }
            for (int i = 0; i < line.Count; i++)
            {
                double t = line[i][3];
                bool before = i <= a;
                double near = before ? at - t : t - at;
                if (near < 0 || near >= w) continue;
                double f = 1 - Ease(near / w);   // 1 at the join, 0 at the edge of the window
                SetPt(line[i], Pt(line[i]) + jump * (before ? 0.5 * f : -0.5 * f));
                if (axes && line[i].Length >= 13)
                {
                    var from = before ? endA : startB;
                    Vec3D x = Pt(line[i], 4) + (mid[0] - from[0]) * f;
                    Vec3D y = Pt(line[i], 7) + (mid[1] - from[1]) * f;
                    if (x.Length < 1e-6 || y.Length < 1e-6) continue;
                    x = x.Normalized();
                    y = (y - x * Vec3D.Dot(y, x));
                    if (y.Length < 1e-6) continue;
                    y = y.Normalized();
                    Vec3D z = Vec3D.Cross(x, y);
                    if (Vec3D.Dot(z, Pt(line[i], 10)) < 0) z = z * -1;   // keep the way round the axes were
                    SetPt(line[i], x, 4); SetPt(line[i], y, 7); SetPt(line[i], z, 10);
                }
            }
            // New times across the window: speed changing steadily from going in to coming out.
            int i0 = a, i1 = b;
            while (i0 > 0 && at - line[i0 - 1][3] < w) i0--;
            while (i1 + 1 < line.Count && line[i1 + 1][3] - at < w) i1++;
            if (i1 - i0 < 3) return;
            double t0 = line[i0][3], tw = line[i1][3] - t0;
            if (tw <= 0.2) return;
            var dist = new double[i1 - i0 + 1];
            for (int i = i0 + 1; i <= i1; i++) dist[i - i0] = dist[i - i0 - 1] + (Pt(line[i]) - Pt(line[i - 1])).Length;
            double length = dist[dist.Length - 1];
            double v0 = (Pt(line[i0 + 1]) - Pt(line[i0])).Length / Math.Max(1e-6, line[i0 + 1][3] - line[i0][3]);
            double v1 = (Pt(line[i1]) - Pt(line[i1 - 1])).Length / Math.Max(1e-6, line[i1][3] - line[i1 - 1][3]);
            if (v0 < 1 || v1 < 1 || length < 1) return;
            double k2 = length / ((v0 + v1) / 2 * tw);   // so the window still takes the same time
            v0 *= k2; v1 *= k2;
            double acc = (v1 - v0) / tw;
            double last = t0;
            for (int i = i0 + 1; i < i1; i++)
            {
                double sd = dist[i - i0], tau;
                if (Math.Abs(acc) < 1e-6) tau = sd / v0;
                else
                {
                    double disc = v0 * v0 + 2 * acc * sd;
                    tau = disc <= 0 ? tw : (-v0 + Math.Sqrt(disc)) / acc;
                }
                double nt = t0 + Math.Clamp(tau, 0, tw);
                nt = Math.Max(nt, last + 0.001);
                if (nt >= t0 + tw) nt = t0 + tw - 0.001 * (i1 - i);
                line[i][3] = Math.Round(nt, 3);
                last = line[i][3];
            }
        }

        private List<double[]> PossibleTrace()
        {
            var rec = CourseRecord(false);
            if (rec == null || timer.Gate == null) return null;
            var parts = rec.BestSectorTraces;
            int n = timer.Checkpoints.Count;
            if (ReferenceEquals(parts, possibleFor) && ReferenceEquals(rec.BestLapTrace, possibleBestFor) && possibleTrace != null
                && possibleSmoothFor == settings.PossibleSmoothing) return possibleTrace;
            // A sector with no path of its own yet (its best was set before paths were kept) takes the best lap's path for it.
            List<double[]> FromBestLap(int k, out double length)
            {
                length = 0;
                var t = rec.BestLapTrace;
                var sp = rec.BestLapSplits;
                if (t == null || t.Count < 2 || t[0].Length < 4) return null;
                double? start = k == 0 ? 0 : (sp != null && k - 1 < sp.Count ? sp[k - 1] : null);
                double? end = k == n ? t[t.Count - 1][3] : (sp != null && k < sp.Count ? sp[k] : null);
                if (!start.HasValue || !end.HasValue || end.Value <= start.Value) return null;
                var part = new List<double[]>();
                foreach (var pt in t)
                {
                    if (pt[3] < start.Value - 1e-6 || pt[3] > end.Value + 1e-6) continue;
                    var c = (double[])pt.Clone();
                    c[3] = pt[3] - start.Value;
                    part.Add(c);
                }
                length = end.Value - start.Value;
                return part.Count >= 2 ? part : null;
            }
            var line = new List<double[]>();
            var joins = new List<double>();
            double offset = 0;
            for (int k = 0; k <= n; k++)
            {
                if (k > 0) joins.Add(offset);
                List<double[]> part = parts != null && k < parts.Count ? parts[k] : null;
                double length;
                if (part != null && rec.BestSectors != null && k < rec.BestSectors.Count && rec.BestSectors[k].HasValue) length = rec.BestSectors[k].Value;
                else part = FromBestLap(k, out length);
                if (part == null) return null;
                foreach (var pt in part)
                {
                    var c = (double[])pt.Clone();
                    c[3] = Math.Round(offset + pt[3], 3);
                    if (line.Count == 0 || c[3] > line[line.Count - 1][3]) line.Add(c);
                }
                offset += length;
            }
            if (settings.PossibleSmoothing > 0.01 && line.Count >= 8)
            {
                try
                {
                    foreach (double join in joins) SmoothJoin(line, join, settings.PossibleSmoothing);
                    SmoothLoop(line, settings.PossibleSmoothing);
                }
                catch (Exception e) { PluginLog.Info("Possible line not smoothed: " + e.Message); }
            }
            possibleFor = parts;
            possibleBestFor = rec.BestLapTrace;
            possibleSmoothFor = settings.PossibleSmoothing;
            possibleTrace = line.Count >= 8 ? line : null;
            return possibleTrace;
        }

        /// <summary>Keeps the best time for each sector (start to CP 1, ..., last CP to finish) in <paramref name="best"/>.</summary>
        private static void UpdateBestSectors(List<double?> best, LapRecord lap, int n)
        {
            while (best.Count < n + 1) best.Add(null);
            for (int k = 0; k <= n; k++)
            {
                double? start = k == 0 ? 0 : (k - 1 < lap.Splits.Length ? lap.Splits[k - 1] : null);
                double? end = k == n ? lap.Flown : (k < lap.Splits.Length ? lap.Splits[k] : null);
                if (!start.HasValue || !end.HasValue || end.Value <= start.Value) continue;
                double sector = end.Value - start.Value;
                if (!best[k].HasValue || sector < best[k].Value) best[k] = sector;
            }
        }

        /// <summary>Best sectors added up: the lap you'd do if every sector went as well as your best. Null until all are known.</summary>
        private static double? PossibleLap(GateSettings.CourseRecord rec, int checkpoints) => PossibleLap(rec?.BestSectors, checkpoints);

        private static double? PossibleLap(List<double?> sectors, int checkpoints)
        {
            if (sectors == null || sectors.Count < checkpoints + 1) return null;
            double sum = 0;
            for (int k = 0; k <= checkpoints; k++)
            {
                if (!sectors[k].HasValue) return null;
                sum += sectors[k].Value;
            }
            return sum;
        }

        /// <summary>The results panel to show (course best or ship best), with the switch shortcut at the bottom.</summary>
        private HudModel.ResultsPanel ShownResults()
        {
            var shown = settings.ResultsForShip && shipResults != null ? shipResults : results;
            if (shown != null) shown.GraphsShown = settings.ResultsGraphs;
            if (shown != null)
                shown.Footer = (shipResults != null ? $"{settings.GetBinding(GateKey.ResultsMode)}: {(shown == shipResults ? "course best" : "this ship's best")}   " : "") + "Esc: close";
            return shown;
        }

        private GhostFile FindGhost(string path)
        {
            if (path == null) return null;
            if (ghostFiles == null) ghostFiles = GhostFile.LoadAll(GhostFolder);
            return ghostFiles.FirstOrDefault(g => g.Path == path) ?? GhostFile.Load(path);
        }

        /// <summary>Writes the course best (ship = null) or a ship's best lap to a ghost file others can load.</summary>
        /// <summary>The ship whose best lap the main ghost follows (Race tab), or null.</summary>
        private string racingShip => raceGhost?.Path != null && raceGhost.Path.StartsWith("ship:") ? raceGhost.Path.Substring(5) : null;

        private void ExportGhost(string ship)
        {
            var file = MakeGhost(ship);
            if (file == null)
            {
                controlsNote = "No recorded best lap to export yet (set one with this version first)";
                return;
            }
            try
            {
                string path = file.Save(GhostFolder);
                ghostFiles = null; recordsVersion++;
                controlsNote = $"Saved {Path.GetFileName(path)} - share it from the ghosts folder";
            }
            catch (Exception e) { controlsNote = "Couldn't save the ghost: " + e.Message; }
        }

        /// <summary>A ghost of a ship's best lap on this course (null ship = the course best); null if there isn't one.</summary>
        private GhostFile MakeGhost(string ship, bool last = false)
        {
            var rec = CourseRecord(false);
            List<double[]> trace, shape, speed; List<double?> splits; double? time; string shipName = ship;
            if (ship == null)
            {
                trace = rec?.BestLapTrace; shape = rec?.BestLapShip; speed = rec?.BestLapSpeed; splits = rec?.BestLapSplits; time = rec?.BestLap;
                // Which ship set the course best: the ship whose best lap is the same time.
                shipName = rec?.Ships?.FirstOrDefault(x => x.Value.BestLap.HasValue && time.HasValue && Math.Abs(x.Value.BestLap.Value - time.Value) < 1e-6).Key;
            }
            else
            {
                GateSettings.ShipRecord sr = null;
                rec?.Ships?.TryGetValue(ship, out sr);
                if (last) { trace = sr?.LastLapTrace; shape = sr?.LastLapShip; speed = sr?.LastLapSpeed; splits = sr?.LastLapSplits; time = sr?.LastLap; }
                else { trace = sr?.BestLapTrace; shape = sr?.BestLapShip; speed = sr?.BestLapSpeed; splits = sr?.BestLapSplits; time = sr?.BestLap; }
            }
            if (trace == null || trace.Count < 2 || !time.HasValue || timer.Gate == null) return null;
            var c = timer.Gate.Center;
            return new GhostFile
            {
                Pilot = settings.PilotName,
                Ship = shipName,
                Course = CurrentCourseName() ?? "Course",
                Date = DateTime.Now,
                LapTime = time.Value,
                GateCenter = new[] { Math.Round(c.X, 2), Math.Round(c.Y, 2), Math.Round(c.Z, 2) },
                Checkpoints = timer.Checkpoints.Count,
                Splits = splits,
                Trace = trace,
                ShipShape = shape,
                Speed = speed,
            };
        }

        private Vec3D? playerForward;
        private Vec3D? lastVelocity;   // the ship's velocity this frame (minimap arrow)
        private void ToggleResultsMode()
        {
            settings.ResultsForShip = !settings.ResultsForShip;
            settings.Save(settingsPath);
            if (settings.ResultsForShip && results != null && shipResults == null)
                Notify("No ship results for this race (it wasn't flown in a named ship)", HudColor.Yellow);
            else
                Notify(settings.ResultsForShip ? "Results: against this ship's best" : "Results: against the course best", HudColor.Cyan);
        }

        /// <summary>
        /// The results again, but against the best times in the ship you flew instead of the course best.
        /// <paramref name="lapBefore"/> / <paramref name="raceBefore"/> are the ship's bests before this race.
        /// </summary>
        private HudModel.ResultsPanel BuildShipResults(HudModel.ResultsPanel overall, GateSettings.ShipRecord ship, double total,
            double? lapBefore, double? raceBefore, List<double[]> speedBefore)
        {
            int laps = timer.Laps.Count;
            var panel = new HudModel.ResultsPanel { Title = $"SHIP RESULTS - {laps} LAP{(laps == 1 ? "" : "S")}", Subtitle = raceShipName };

            if (!raceBefore.HasValue)
            {
                panel.Headline = "FIRST RACE IN THIS SHIP";
                panel.HeadlineColour = HudColor.Cyan;
            }
            else if (total < raceBefore.Value)
            {
                panel.Headline = $"NEW SHIP BEST RACE: {FasterSlower(total - raceBefore.Value)}";
                panel.HeadlineColour = HudColor.Yellow;
            }
            else
            {
                panel.Headline = $"{FasterSlower(total - raceBefore.Value)} than this ship's best race";
                panel.HeadlineColour = HudColor.Grey;
            }

            // Same race time, penalty-free time, speeds and penalties as the course results.
            foreach (var line in overall.Lines)
                if (line.Label == "THIS RACE" || line.Label == "WITHOUT PENALTIES") panel.Lines.Add(line);
            if (raceBefore.HasValue && total >= raceBefore.Value)
                panel.Lines.Add(("SHIP BEST RACE", LapTimer.Format(raceBefore.Value), HudColor.Yellow));
            if (timer.RaceBestLap.HasValue)
            {
                bool shipBest = !lapBefore.HasValue || timer.RaceBestLap.Value < lapBefore.Value;
                panel.Lines.Add(("BEST LAP THIS RACE", LapTimer.Format(timer.RaceBestLap.Value), shipBest ? HudColor.Purple : HudColor.Green));
            }
            if (lapBefore.HasValue)
                panel.Lines.Add(("SHIP BEST LAP", LapTimer.Format(Math.Min(lapBefore.Value, timer.RaceBestLap ?? double.MaxValue)), HudColor.Yellow));
            double? possible = PossibleLap(ship.BestSectors, timer.Checkpoints.Count);
            if (possible.HasValue)
            {
                panel.Lines.Add(("POSSIBLE LAP (BEST SECTORS)", LapTimer.Format(possible.Value), HudColor.Purple));
                if (laps > 1) panel.Lines.Add(("POSSIBLE RACE", LapTimer.Format(possible.Value * laps), HudColor.Purple));
            }
            foreach (var line in overall.Lines)
                if (line.Label == "TOP / AVG SPEED" || line.Label == "PENALTIES") panel.Lines.Add(line);

            // Graphs: the best lap of this race (or the lap picked with the results lap keys) against this
            // ship's best lap from before the race (or another lap of this race).
            shipGraphRef = ("ship best", speedBefore, lapBefore, shipSplitsAtStart);
            FillGraphs(panel, shipGraphRef);
            return panel;
        }

        private HudModel.ResultsPanel BuildResults(double total)
        {
            var rec = CourseRecord(true);
            int laps = timer.Laps.Count;
            var panel = new HudModel.ResultsPanel { Title = $"RESULTS - {laps} LAP{(laps == 1 ? "" : "S")}" };

            int missed = 0, crashes = 0;
            double penalty = 0;
            foreach (var l in timer.Laps) { missed += l.MissedCheckpoints; crashes += l.Crashes; penalty += l.Penalty; }

            panel.Lines.Add(("THIS RACE", LapTimer.Format(total), HudColor.White));
            if (penalty > 0) panel.Lines.Add(("WITHOUT PENALTIES", LapTimer.Format(total - penalty), HudColor.Grey));
            int recordLine = panel.Lines.Count;   // the course record race goes here when it wasn't beaten
            if (timer.RaceBestLap.HasValue)
            {
                bool record = rec.BestLap.HasValue && Math.Abs(timer.RaceBestLap.Value - rec.BestLap.Value) < 1e-6;
                panel.Lines.Add(("BEST LAP THIS RACE", LapTimer.Format(timer.RaceBestLap.Value), record ? HudColor.Purple : HudColor.Green));
            }
            if (rec.BestLap.HasValue && timer.RaceBestLap.HasValue && rec.BestLap.Value < timer.RaceBestLap.Value - 1e-6)
                panel.Lines.Add(("COURSE RECORD LAP", LapTimer.Format(rec.BestLap.Value), HudColor.Yellow));
            double? possible = PossibleLap(rec, timer.Checkpoints.Count);
            if (possible.HasValue)
            {
                panel.Lines.Add(("POSSIBLE LAP (BEST SECTORS)", LapTimer.Format(possible.Value), HudColor.Purple));
                if (laps > 1) panel.Lines.Add(("POSSIBLE RACE", LapTimer.Format(possible.Value * laps), HudColor.Purple));
            }
            if (lapSpeeds.Count > 0)
            {
                double avg = 0;
                foreach (var sp in lapSpeeds) avg += sp.Avg;
                avg /= lapSpeeds.Count;
                panel.Lines.Add(("TOP / AVG SPEED", $"{GameOptions.SpeedNumber(raceTopSpeed)} / {GameOptions.Speed(avg)}", HudColor.White));
            }
            if (penalty > 0)
            {
                var parts = new List<string>();
                if (missed > 0) parts.Add($"{missed} missed");
                if (crashes > 0) parts.Add($"{crashes} crash{(crashes == 1 ? "" : "es")}");
                panel.Lines.Add(("PENALTIES", $"+{penalty:0.#}s: {string.Join(", ", parts)}", HudColor.Red));
            }
            else panel.Lines.Add(("PENALTIES", "none - clean race!", HudColor.Green));

            if (rec.BestRace.TryGetValue(laps, out double bestRace))
            {
                if (total < bestRace)
                {
                    panel.Headline = $"NEW COURSE RECORD: {FasterSlower(total - bestRace)}";
                    panel.HeadlineColour = HudColor.Yellow;
                    rec.BestRace[laps] = total;
                    KeepBestRaceTraces(rec, laps);
                    FlashTimer(HudColor.Green);
                }
                else
                {
                    panel.Headline = $"{FasterSlower(total - bestRace)} than the course record";
                    panel.HeadlineColour = HudColor.Grey;
                    panel.Lines.Insert(recordLine, ("COURSE RECORD RACE", LapTimer.Format(bestRace), HudColor.Yellow));
                }
            }
            else
            {
                panel.Headline = "NEW COURSE RECORD (first race of this length)";
                panel.HeadlineColour = HudColor.Yellow;
                rec.BestRace[laps] = total;
                KeepBestRaceTraces(rec, laps);
            }

            // Graphs: the best lap of this race (or the lap picked) against the course record from before the race.
            courseGraphRef = ("course record", recordSpeedAtStart, recordLapAtStart, recordSplitsAtStart);
            FillGraphs(panel, courseGraphRef);
            SetResultsMap();

            // Race history: keep the best 5 per lap count.
            // This ship's best times on the course (Records tab compares ships).
            double? shipBestBefore = null;
            if (!string.IsNullOrEmpty(raceShipName))
            {
                if (rec.Ships == null) rec.Ships = new Dictionary<string, GateSettings.ShipRecord>();
                if (!rec.Ships.TryGetValue(raceShipName, out var ship)) rec.Ships[raceShipName] = ship = new GateSettings.ShipRecord();
                if (ship.BestRace.TryGetValue(laps, out double before)) shipBestBefore = before;
                double? shipLapBefore = shipLapAtStart;
                var shipSpeedBefore = shipSpeedAtStart;
                if (ship.BestSectors == null) ship.BestSectors = new List<double?>();
                foreach (var lapDone in timer.Laps) UpdateBestSectors(ship.BestSectors, lapDone, timer.Checkpoints.Count);
                shipResults = BuildShipResults(panel, ship, total, shipLapBefore, shipBestBefore, shipSpeedBefore);
                ship.Races++;
                ship.LastRace = DateTime.Now;
                if (!shipBestBefore.HasValue || total < shipBestBefore.Value) ship.BestRace[laps] = total;
                ship.TopSpeed = Math.Max(ship.TopSpeed, raceTopSpeed);
                foreach (var st in lapStats) ship.MaxTurnG = Math.Max(ship.MaxTurnG, st.MaxTurnG);
            }
            raceStats = BuildRaceStats(laps, total, shipBestBefore);

            rec.History.Add(new GateSettings.RaceEntry
            {
                Ship = raceShipName,
                Date = DateTime.Now,
                Laps = laps,
                Total = total,
                Penalty = penalty,
                BestLap = timer.RaceBestLap ?? 0,
                Missed = missed,
                Crashes = crashes,
                TopSpeed = raceTopSpeed,
            });
            rec.History = rec.History
                .GroupBy(h => h.Laps)
                .SelectMany(g => g.OrderBy(h => h.Total).Take(5))
                .OrderBy(h => h.Laps).ThenBy(h => h.Total)
                .ToList();
            recordsVersion++;

            settings.Save(settingsPath);
            return panel;
        }

        /// <summary>
        /// Diamond at the gate's centre, scaled with distance so it stays the same size on screen, with a small
        /// label underneath. <paramref name="emphasis"/> above 1 makes it bigger (the next checkpoint).
        /// </summary>
        private static readonly HudColor GhostColour = HudColor.Purple;
        private static readonly HudColor SecondGhostColour = HudColor.Orange;
        private string secondGhostCourse;   // the course it was picked on (cleared when the course changes)

        private void SetSecondGhost(string key, List<double[]> trace, List<double[]> ship, string label)
        {
            secondGhostKey = key; secondGhostTrace = trace; secondGhostShip = ship;
            secondGhostLabel = label?.ToUpperInvariant();
            secondGhostCourse = CourseKey();
            recordsVersion++;
        }

        private void ClearSecondGhost()
        {
            secondGhostKey = null; secondGhostTrace = secondGhostShip = null; secondGhostLabel = null;
            recordsVersion++;
        }
        // The 2nd ghost (Records tab: a ship's best or a ghost file), raced or watched alongside the main one.
        private List<double[]> secondGhostTrace, secondGhostShip;
        private string secondGhostLabel, secondGhostKey;   // key: "ship:<name>" or the ghost file's path
        private static readonly HudColor RacingLineColour = new HudColor(255, 70, 255, 255); // bright magenta

        /// <summary>
        /// Racing line: the best lap's path drawn through the world (near you only). Ghost: a marker where the best
        /// lap was at this moment of the lap, so you can see whether you're ahead of it or behind it.
        /// </summary>
        private void DrawRacingLineAndGhost(List<double[]> trace, List<double[]> shipShape, string ghostLabel, HudModel model, bool hudLabels)
        {
            if (trace == null || trace.Count < 2 || timer.Gate == null) return;
            Vec3D? me = playerPosition;

            if (settings.ShowRacingLine && me.HasValue && (!editMode || settings.EditRacingLine))
            {
                bool twoLines = secondGhostTrace != null && !ReferenceEquals(secondGhostTrace, trace) && !editMode;
                // With a 2nd ghost each line takes its ghost's colour (main: pale blue, 2nd: orange) so you can tell
                // them apart; on its own the line is coloured by speed (or magenta).
                DrawRacingLine(trace, twoLines ? MainLineColour : (HudColor?)null, twoLines ? 0 : settings.RacingLineColourMode, me.Value, 0);
                if (twoLines) DrawRacingLine(secondGhostTrace, SecondGhostColour.WithAlpha(255), 0, me.Value, 1);
                if (settings.BrakeMarkers && !editMode) DrawBrakeMarks(trace, me.Value, model, hudLabels);
            }

            // A spotlight race flown by the autopilot, compared with another race: that race's ghost flies
            // alongside, at the same race time (orange, like the 2nd ghost).
            if (autopilot && spotRaceTrace != null && spotCompareTrace != null && SpotCompareNow() is SpotlightFile vs
                && ReferenceEquals(AutopilotLine(), spotRaceTrace))
            {
                double into = apTime - spotRaceTrace[0][3] + spotCompareTrace[0][3];
                if (into <= spotCompareTrace[spotCompareTrace.Count - 1][3])
                    DrawGhost(spotCompareTrace, vs.ShipShape, (vs.Ship ?? "OTHER RACE").ToUpperInvariant(), null, into, model, hudLabels, true);
            }

            double ghostTime;
            if (replaying) ghostTime = Math.Min(ReplayTime, replayLength);
            else if (!settings.ShowGhost || timer.State != TimerState.Running) return;
            else ghostTime = timer.CurrentLapSeconds(game.TimeSeconds);
            DrawGhost(trace, shipShape, ghostLabel ?? "BEST LAP", liveGapText, ghostTime, model, hudLabels, false);

            // A second ghost to race at the same time (Records tab > 2nd ghost), in orange.
            if (secondGhostTrace != null && !ReferenceEquals(secondGhostTrace, trace))
                DrawGhost(secondGhostTrace, secondGhostShip, secondGhostLabel ?? "2ND GHOST", null, ghostTime, model, hudLabels, true);
        }

        /// <summary>One ghost at this lap time: ship-shaped (or a diamond) with its label. second = the orange 2nd ghost.</summary>
        private void DrawGhost(List<double[]> trace, List<double[]> shipShape, string label, string gap, double ghostTime, HudModel model, bool hudLabels, bool second)
        {
            Vec3D? me = playerPosition;
            if (!GhostPose(trace, ghostTime, out Vec3D g, out Vec3D[] axes)) return;
            double dist = me.HasValue ? (g - me.Value).Length : 100;
            Vec3D up = timer.Gate.Up;
            double size = Math.Max(1.0, dist * settings.MarkerSize * 0.6);
            Vec3D bottom = g - up * size;

            var ship = shipShape;
            if ((settings.GhostProjection || replaying) && axes != null && ship != null && ship.Count > 0)
            {
                DrawGhostShip(ship, g, axes, dist, second);
                bottom = g - up * Math.Max(size, 5);
            }
            else
            {
                // A small diamond, sized like the checkpoint markers so it stays visible far away.
                var c = (second ? SecondGhostColour : GhostColour).WithAlpha(230);
                // Turns with the ship that set the lap (its recorded axes: X right, Y up, Z along), so you can
                // see the ghost pitch, roll and turn. Old recordings without axes spin slowly instead.
                Vec3D right, fwd, dUp;
                if (axes != null)
                {
                    right = axes[0]; dUp = axes[1]; fwd = axes[2];
                }
                else
                {
                    double ang = game.TimeSeconds * 1.5;
                    Vec3D gr = timer.Gate.Right, gf = timer.Gate.Forward;
                    right = gr * Math.Cos(ang) + gf * Math.Sin(ang);
                    fwd = gf * Math.Cos(ang) - gr * Math.Sin(ang);
                    dUp = up;
                }
                Vec3D dBottom = g - dUp * size;
                Vec3D top = g + dUp * size;
                Vec3D[] ring = { g + right * (size * 0.6), g + fwd * (size * 0.6), g - right * (size * 0.6), g - fwd * (size * 0.6) };
                for (int i = 0; i < 4; i++)
                {
                    game.DrawMarkerLine(top, ring[i], c);
                    game.DrawMarkerLine(dBottom, ring[i], c);
                    game.DrawMarkerLine(ring[i], ring[(i + 1) % 4], c);
                }
            }
            var labelColour = (second ? SecondGhostColour : GhostColour).WithAlpha(230);
            if (hudLabels)
            {
                if (game.TryProjectToScreen(g, out double x, out double y, out bool behind) && !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98)
                    model.Markers.Add(new HudModel.ScreenMarker
                    {
                        X = x, Y = y, Text = label, Sub = gap, Colour = labelColour, Emphasis = false, World = g,
                        // One slim line, the gap green (ahead of it) or red (behind).
                        SubInline = true,
                        SubColour = gap == null ? (HudColor?)null : gap.StartsWith("-") ? HudColor.Green : gap.StartsWith("+") ? HudColor.Red : (HudColor?)null,
                    });
            }
            else game.DrawMarkerText(bottom - up * (size * 0.5), gap != null ? $"{label}  {gap}" : label, labelColour, 0.5);
        }

        // ---------------------------------------------------------------- best-lap replay

        private void ToggleReplay()
        {
            if (replaying || replayPending) { StopReplay("Replay stopped"); return; }
            if (CourseRecord(false)?.BestLapTrace == null) { Notify("No best lap recorded on this course yet", HudColor.Red); return; }
            replayPending = true;
        }

        private void BeginReplay()
        {
            replayPending = false;
            if (orbitCam) { Notify("Close the orbit camera first", HudColor.Yellow); return; }
            var rec = CourseRecord(false);
            if (pendingTrace != null)
            {
                replayTrace = pendingTrace; replayShip = pendingShip;
                replayLabel = pendingLabel ?? "BEST LAP"; replayTitle = pendingTitle;
            }
            else
            {
                replayTrace = rec?.BestLapTrace; replayShip = rec?.BestLapShip;
                replayLabel = "BEST LAP"; replayTitle = null;
            }
            pendingTrace = pendingShip = null; pendingLabel = pendingTitle = null;
            if (replayTrace == null || replayTrace.Count < 2 || timer.Gate == null) { Notify("No best lap recorded on this course yet", HudColor.Red); return; }
            replayLength = replayTrace[replayTrace.Count - 1][3];
            replayCam = null;
            orbitLastCam = orbitLastDir = null;
            orbitInputSeen = orbitLogged = false;
            replayCinematicTried = false;
            ChaseView(0, out Vec3D cam, out Vec3D look, out Vec3D up);
            if (!game.StartSpectator(cam, look, up)) { Notify("Couldn't switch to the spectator camera", HudColor.Red); return; }
            replaying = true;
            replayCameraReady = false;
            replayWaitMs = Environment.TickCount64;
            KeyHint((replayTitle != null ? $"Replay: {replayTitle}" : $"Replay of your best lap ({LapTimer.Format(rec?.BestLap ?? replayLength)})") + "   Esc: stop", HudColor.Purple);
        }

        private void StopReplay(string message)
        {
            bool wasReplaying = replaying;
            replaying = false;
            replayPending = false;
            replayCameraReady = false;
            replayCam = null;
            if (wasReplaying) game.EndSpectator();
            Notify(message);
        }

        private void UpdateReplay()
        {
            if (!replayCameraReady)
            {
                // The spectator takes a moment to appear; keep trying to put it in place.
                ChaseView(0, out Vec3D c0, out Vec3D l0, out Vec3D u0);
                if (game.SetSpectatorView(c0, l0, u0)) { replayCameraReady = true; replayStartMs = Environment.TickCount64; }
                else if (Environment.TickCount64 - replayWaitMs > 6000)
                    StopReplay("Couldn't switch to the spectator camera (is spectator allowed in this world?)");
                return;
            }

            double t = ReplayTime;
            if (t > replayLength + 1.5) { StopReplay("Replay finished"); return; }
            if (keys.WasPressed(GateKey.ReplayCamera))
            {
                settings.ReplayOrbit = !settings.ReplayOrbit;
                settings.Save(settingsPath);
                orbitLastCam = orbitLastDir = null;
                replayCam = null;
                KeyHint(settings.ReplayOrbit ? "Replay camera: orbit (mouse to swing round the ghost, W / S to zoom)" : "Replay camera: chase", HudColor.Purple);
            }
            if (!replayCinematicTried)
            {
                replayCinematicTried = true;
                if (settings.ReplayCinematic && !game.SetSpectatorCinematic(true))
                    Notify("This game version has no cinematic spectator; using the normal one", HudColor.Grey);
            }
            double tt = Math.Min(t, replayLength);
            if (settings.ReplayOrbit && OrbitView(tt)) return;
            ChaseView(tt, out Vec3D cam, out Vec3D look, out Vec3D up);
            if (!game.SetSpectatorView(cam, look, up)) StopReplay("Replay stopped (the spectator camera went away)");
        }

        /// <summary>
        /// Orbit camera round the ghost, driven by the normal spectator controls. Each frame we read what the game did
        /// to the spectator since we last placed it: the mouse turned it (that turn swings the camera round the ghost),
        /// W / S moved it forward or back (zoom in / out), A / D and up / down moved it sideways (also swings it round).
        /// Then the camera is put back at that distance and direction from where the ghost is now, aiming at it.
        /// False if the spectator can't be read (the chase camera is used instead).
        /// </summary>
        private bool OrbitView(double t)
        {
            if (!GhostPose(replayTrace, t, out Vec3D ghost, out _)) return false;
            if (!game.TryGetSpectatorView(out Vec3D cam, out Vec3D fwd, out _)) return false;
            if (fwd.Length < 0.5) return false;
            fwd = fwd.Normalized();

            if (orbitLastCam == null || orbitLastDir == null)
            {
                // Start from wherever the camera is now (the chase view), aiming at the ghost.
                Vec3D from = cam - ghost;
                orbitDist = Math.Clamp(from.Length, 3, 500);
                orbitDir = from.Length > 0.01 ? (from * -1).Normalized() : timer.Gate.Forward;
            }
            else
            {
                Vec3D lastDir = orbitLastDir.Value;
                Vec3D moved = cam - orbitLastCam.Value;
                double along = Vec3D.Dot(moved, lastDir);
                Vec3D side = moved - lastDir * along;
                if (moved.Length > 0.001 || Vec3D.Dot(fwd, lastDir) < 0.99999) orbitInputSeen = true;

                orbitDist = Math.Clamp(orbitDist - along, 3, 500);       // W = closer, S = further
                // Mouse turn: the game rotated the spectator from lastDir to fwd. Sideways movement swings it round too.
                Vec3D offset = fwd * -orbitDist + side;
                orbitDir = offset.Length > 0.01 ? (offset * -1).Normalized() : fwd;
            }

            Vec3D look = orbitDir;
            Vec3D worldUp = timer.Gate.Up;
            Vec3D up = worldUp - look * Vec3D.Dot(worldUp, look);
            up = up.Length > 0.05 ? up.Normalized() : timer.Gate.Forward;
            Vec3D newCam = ghost - look * orbitDist;

            if (!orbitLogged && orbitInputSeen) { orbitLogged = true; PluginLog.Info("Replay orbit: spectator input is reaching the camera"); }
            if (!orbitLogged && ReplayTime > 10) { orbitLogged = true; PluginLog.Info("Replay orbit: no spectator movement seen in 10 s (mouse / WASD may not be reaching the spectator)"); }

            orbitLastCam = newCam;
            orbitLastDir = look;
            replayCam = newCam;
            return game.SetSpectatorView(newCam, look, up, false);
        }

        /// <summary>
        /// Chase camera behind and above the ghost at time t, looking a little ahead of it. Follows the direction of
        /// travel (smoothed), with the gate's "up" as up so it doesn't roll with the ship.
        /// </summary>
        private void ChaseView(double t, out Vec3D cam, out Vec3D look, out Vec3D up)
        {
            GhostPose(replayTrace, t, out Vec3D pos, out _);
            if (GhostPose(replayTrace, Math.Min(t + 0.5, replayLength), out Vec3D ahead, out _) && (ahead - pos).Length > 0.5)
                replayDir = (replayDir * 0.85 + (ahead - pos).Normalized() * 0.15).Normalized();

            double size = 20;
            if (replayShip != null && replayShip.Count > 0)
            {
                if (!ReferenceEquals(replayShip, hullFor)) BuildHull(replayShip);
                size = Math.Sqrt(Math.Pow(hullBounds[3] - hullBounds[0], 2) + Math.Pow(hullBounds[4] - hullBounds[1], 2) + Math.Pow(hullBounds[5] - hullBounds[2], 2));
            }
            double back = Math.Max(15, size * 1.8), height = back * 0.3;

            Vec3D worldUp = timer.Gate.Up;
            Vec3D desired = pos - replayDir * back + worldUp * height;
            replayCam = replayCam.HasValue ? replayCam.Value + (desired - replayCam.Value) * 0.2 : desired;
            cam = replayCam.Value;
            look = (pos + replayDir * (size * 0.5) - cam).Normalized();
            up = (worldUp - look * Vec3D.Dot(worldUp, look)).Normalized();
        }

        // ---------------------------------------------------------------- ghost ship

        private List<double[]> hullFor;   // the ship shape the faces below were made from
        private List<double[]> hullFaces; // outer faces: 4 corners (12 numbers, grid coordinates) + side (0..5: -X, +X, -Y, +Y, -Z, +Z)
        private double[] hullBounds;      // whole-ship box [min x, y, z, max x, y, z]
        private List<double[]> hullEdges; // outline edges: [x0, y0, z0, x1, y1, z1] where the hull bends (not across flat surfaces)
        private readonly List<Vec3D> ghostLines = new List<Vec3D>();
        private readonly List<Vec3D> ghostCorners = new List<Vec3D>();
        private readonly List<HudColor> ghostColours = new List<HudColor>();

        /// <summary>
        /// Outer faces of the ship from its block boxes: a face shared by two touching blocks is inside the ship,
        /// so it's dropped. Made once per ship shape.
        /// </summary>
        private void BuildHull(List<double[]> ship)
        {
            hullFor = ship;
            hullFaces = new List<double[]>();
            hullBounds = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            var count = new Dictionary<string, int>();
            var faces = new List<(string Key, double[] Face)>();

            foreach (var k in ship)
            {
                for (int i = 0; i < 3; i++) { hullBounds[i] = Math.Min(hullBounds[i], k[i]); hullBounds[i + 3] = Math.Max(hullBounds[i + 3], k[i + 3]); }
                for (int side = 0; side < 6; side++)
                {
                    var f = BoxFace(k, side);
                    int axis = side / 2;
                    double plane = side % 2 == 0 ? k[axis] : k[axis + 3];
                    int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                    string key = $"{axis}|{plane:F2}|{k[a1]:F2}|{k[a2]:F2}|{k[a1 + 3]:F2}|{k[a2 + 3]:F2}";
                    count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                    faces.Add((key, f));
                }
            }
            foreach (var (key, f) in faces) if (count[key] == 1) hullFaces.Add(f);

            // Outline: edges of the outer faces, except where two faces of the same flat surface meet.
            var edges = new Dictionary<string, (double[] Edge, int Sides, int Count)>();
            foreach (var f in hullFaces)
            {
                int side = (int)f[12];
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    var e = new[] { f[i * 3], f[i * 3 + 1], f[i * 3 + 2], f[j * 3], f[j * 3 + 1], f[j * 3 + 2] };
                    string ka = $"{e[0]:F2},{e[1]:F2},{e[2]:F2}", kb = $"{e[3]:F2},{e[4]:F2},{e[5]:F2}";
                    string key = string.CompareOrdinal(ka, kb) < 0 ? ka + "|" + kb : kb + "|" + ka;
                    edges[key] = edges.TryGetValue(key, out var x) ? (x.Edge, x.Sides | (1 << side), x.Count + 1) : (e, 1 << side, 1);
                }
            }
            hullEdges = new List<double[]>();
            foreach (var x in edges.Values)
            {
                bool flat = x.Count >= 2 && (x.Sides & (x.Sides - 1)) == 0; // only one side direction = same flat surface
                if (!flat) hullEdges.Add(x.Edge);
            }
        }

        /// <summary>One side of a box as 4 corners in order, plus the side number.</summary>
        private static double[] BoxFace(double[] k, int side)
        {
            double x0 = k[0], y0 = k[1], z0 = k[2], x1 = k[3], y1 = k[4], z1 = k[5];
            double[] c;
            switch (side)
            {
                case 0: c = new[] { x0, y0, z0, x0, y1, z0, x0, y1, z1, x0, y0, z1 }; break;
                case 1: c = new[] { x1, y0, z0, x1, y0, z1, x1, y1, z1, x1, y1, z0 }; break;
                case 2: c = new[] { x0, y0, z0, x0, y0, z1, x1, y0, z1, x1, y0, z0 }; break;
                case 3: c = new[] { x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1 }; break;
                case 4: c = new[] { x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0 }; break;
                default: c = new[] { x0, y0, z1, x0, y1, z1, x1, y1, z1, x1, y0, z1 }; break;
            }
            var f = new double[13];
            Array.Copy(c, f, 12);
            f[12] = side;
            return f;
        }

        /// <summary>
        /// The ghost: a see-through, softly shaded copy of the ship that set the best lap, at the recorded
        /// position and orientation. Far away it's just the ship's overall box (still filled).
        /// </summary>
        // ---- Minimap: the course from above, the start gate's forward pointing up the map.
        private int minimapKey, minimapVersion;
        private double lastCrashPenaltyAt = double.NegativeInfinity;   // for the crash immunity time
        private bool crashSlowLock;   // a crash penalty was given and you haven't been back above the immunity speed since
        private Vec3D mmOrigin, mmE1, mmE2;          // map axes (right, up the map) on the course's ground plane
        private double mmMinU, mmMinV, mmSpan;
        private HudModel.MinimapPanel mmStatic;      // route / lines in map coordinates, rebuilt when they change
        private double mmViewX = 0.5, mmViewY = 0.5, mmViewSpan = 1, mmViewAt = double.NegativeInfinity;

        private (double X, double Y) MapPoint(Vec3D p)
        {
            Vec3D d = p - mmOrigin;
            double u = Vec3D.Dot(d, mmE1), v = Vec3D.Dot(d, mmE2);
            return ((u - mmMinU) / mmSpan, 1 - (v - mmMinV) / mmSpan);
        }

        /// <summary>
        /// The Race Map's extras, made with the route and lines: sector bands along the racing line, gate bars,
        /// the scale and north, the height profile, hover details, and after the race your lap coloured by where
        /// it gained or lost time on the record with the stretches well away from its line.
        /// </summary>
        private void BuildMapExtras(List<double[]> line, List<double[]> line2, List<(string Name, TimingGate Gate)> route, bool comparing)
        {
            var mm = mmStatic;
            mm.MetresAcross = mmSpan;
            // North: the world's +Y axis (the planet's pole in most worlds), as a direction on the map.
            Vec3D north = new Vec3D(0, 1, 0);
            double nu = Vec3D.Dot(north, mmE1), nv = Vec3D.Dot(north, mmE2), nl = Math.Sqrt(nu * nu + nv * nv);
            if (nl > 0.2) { mm.NorthX = nu / nl; mm.NorthY = -nv / nl; mm.HasNorth = true; }

            // Gates as short bars across the course, the width of the gate; start / finish chequered.
            for (int k = 0; k < route.Count; k++)
            {
                var g = route[k].Gate;
                if (k == route.Count - 1 && ReferenceEquals(g, route[0].Gate)) continue;   // the start again at the end of a loop
                var a = MapPoint(g.Center - g.Right * g.HalfWidth);
                var b = MapPoint(g.Center + g.Right * g.HalfWidth);
                bool sf = ReferenceEquals(g, timer.Gate) || ReferenceEquals(g, timer.FinishGate);
                mm.GateBars.Add((a.X, a.Y, b.X, b.Y, sf));
            }

            // The line that matters most: your lap after a race, else the best lap / ghost.
            var main = line2 ?? line;
            if (main == null || main.Count < 4) return;
            int step = Math.Max(1, main.Count / 300);
            var idx = new List<int>();
            for (int i = 0; i < main.Count; i += step) idx.Add(i);
            if (idx[idx.Count - 1] != main.Count - 1) idx.Add(main.Count - 1);
            Vec3D At(List<double[]> t, int i) => new Vec3D(t[i][0], t[i][1], t[i][2]);

            // Where each gate falls on it (in order, so the start and the finish of a loop don't get mixed up).
            var gateAt = new int[route.Count];
            int from = 0;
            for (int k = 0; k < route.Count; k++)
            {
                if (k == route.Count - 1 && ReferenceEquals(route[k].Gate, route[0].Gate)) { gateAt[k] = idx.Count - 1; continue; }
                double best = double.MaxValue; int bi = from;
                for (int j = from; j < idx.Count; j++)
                {
                    double d = (At(main, idx[j]) - route[k].Gate.Center).Length;
                    if (d < best) { best = d; bi = j; }
                }
                gateAt[k] = bi; from = bi;
            }
            // Sector bands along the line rather than straight from gate to gate.
            if (mm.Sectors.Count > 0)
            {
                var along = new List<(List<(double X, double Y)> Points, string Name, HudColor Colour)>();
                for (int k = 0; k < settings.CourseSectors.Count; k++)
                {
                    var sec = settings.CourseSectors[k];
                    int a = Math.Clamp(sec.From, 0, route.Count - 1), b = Math.Clamp(sec.To, 0, route.Count - 1);
                    if (b <= a) continue;
                    var pts = new List<(double X, double Y)>();
                    for (int j = gateAt[a]; j <= gateAt[b] && j < idx.Count; j++) pts.Add(MapPoint(At(main, idx[j])));
                    if (pts.Count >= 2) along.Add((pts, sec.Name, SectorColours[k % SectorColours.Length]));
                }
                if (along.Count > 0) mm.Sectors = along;
            }

            // Height profile round the lap and the gates' places on it: the height above the ground below, the same as
            // under the speedometer (or, off a planet / where the game can't say, above the start gate).
            Vec3D up = timer.Gate.Up.Normalized();
            double total = 0;
            var dist = new double[idx.Count];
            for (int j = 1; j < idx.Count; j++)
            {
                double seg = (At(main, idx[j]) - At(main, idx[j - 1])).Length;
                total += seg < 400 * step ? seg : 0;
                dist[j] = total;
            }
            if (total > 1)
            {
                var ground = new double[idx.Count];
                bool aboveGround = settings.GroundHeight;
                for (int j = 0; j < idx.Count && aboveGround; j++)
                    if (!game.TryGetGroundDistance(At(main, idx[j]), out ground[j])) aboveGround = false;
                mm.ProfileAboveGround = aboveGround;
                for (int j = 0; j < idx.Count; j++)
                    mm.Profile.Add((dist[j] / total, aboveGround ? Math.Max(0, ground[j]) : Vec3D.Dot(At(main, idx[j]) - timer.Gate.Center, up)));
                for (int k = 1; k < route.Count; k++)
                {
                    bool missed = comparing && resultsMissed != null && k - 1 < timer.Checkpoints.Count && resultsMissed.Contains(k - 1);
                    string name = k - 1 < timer.Checkpoints.Count && route[k].Name.StartsWith("CP") ? (k).ToString() : "FIN";
                    mm.ProfileMarks.Add((dist[gateAt[k]] / total, name, missed));
                }
            }

            // Hover details and, after a race, the gap to the record along your lap.
            bool timed = main[0].Length >= 4;
            Func<Vec3D, double> refTime = null;
            if (comparing && line2 != null && line != null && line[0].Length >= 4)
            {
                var refPts = new List<(Vec3D P, double T)>();
                int rs = Math.Max(1, line.Count / 600);
                for (int i = 0; i < line.Count; i += rs) refPts.Add((At(line, i), line[i][3]));
                int last = 0;
                refTime = p =>
                {
                    // Nearest point of the record's line, searching forward from the last match (the lap goes one way).
                    double best = double.MaxValue; int bi = last;
                    int hi = Math.Min(refPts.Count, last + refPts.Count / 3 + 5);
                    for (int i = last; i < hi; i++) { double d = (refPts[i].P - p).Length; if (d < best) { best = d; bi = i; } }
                    if (best > 400) for (int i = 0; i < refPts.Count; i++) { double d = (refPts[i].P - p).Length; if (d < best) { best = d; bi = i; } }
                    last = bi;
                    return refPts[bi].T;
                };
            }
            var gaps = new double?[idx.Count];
            for (int j = 0; j < idx.Count; j++)
            {
                int i = idx[j];
                var p = At(main, i);
                double speed = 0;
                if (timed && i + 1 < main.Count)
                {
                    double dt = main[i + 1][3] - main[i][3];
                    if (dt > 1e-3) speed = (At(main, i + 1) - p).Length / dt;
                }
                else if (timed && i > 0)
                {
                    double dt = main[i][3] - main[i - 1][3];
                    if (dt > 1e-3) speed = (p - At(main, i - 1)).Length / dt;
                }
                if (refTime != null && timed) gaps[j] = main[i][3] - refTime(p);
                var mp = MapPoint(p);
                mm.HoverPoints.Add((mp.X, mp.Y, speed, timed ? main[i][3] : -1, gaps[j]));
            }
            if (refTime == null) return;

            // Your lap coloured by the gap's trend: green where you gained on the record, red where you lost.
            var change = new double[idx.Count - 1];
            for (int j = 0; j + 1 < idx.Count; j++)
            {
                // Over a few samples either side, so it isn't jittery.
                int a = Math.Max(0, j - 3), b = Math.Min(idx.Count - 1, j + 4);
                double ta = main[idx[b]][3] - main[idx[a]][3];
                change[j] = ta > 1e-3 && gaps[a].HasValue && gaps[b].HasValue ? (gaps[b].Value - gaps[a].Value) / ta : 0;
            }
            foreach (double c in change)
            {
                double t = Math.Clamp(Math.Abs(c) / 0.25, 0, 1);   // 0.25 s lost or gained per second = full colour
                mm.Line2Colours.Add(c < 0
                    ? new HudColor((byte)(235 - 175 * t), (byte)(235 - 5 * t), (byte)(235 - 145 * t), 255)    // white -> green
                    : new HudColor((byte)(235 + 20 * t), (byte)(235 - 175 * t), (byte)(235 - 185 * t), 255)); // white -> red
            }
            // Stretches where your line was well away from the record's: 2% of the map's width (at least 25 m),
            // and only when it stays away for a while.
            double far = Math.Max(25, mmSpan * 0.02);
            var refAll = new List<Vec3D>();
            for (int i = 0; i < line.Count; i += Math.Max(1, line.Count / 600)) refAll.Add(At(line, i));
            List<(double X, double Y)> run = null;
            for (int j = 0; j < idx.Count; j++)
            {
                var p = At(main, idx[j]);
                double best = double.MaxValue;
                foreach (var q in refAll) { double d = (q - p).Length; if (d < best) best = d; }
                if (best > far)
                {
                    if (run == null) { run = new List<(double X, double Y)>(); mm.Diverge.Add(run); }
                    run.Add(MapPoint(p));
                }
                else run = null;
            }
            mm.Diverge.RemoveAll(r => r.Count < 4);
        }

        private static readonly HudColor[] SectorColours =
        {
            new HudColor(120, 200, 255, 255), new HudColor(255, 150, 220, 255), new HudColor(180, 255, 140, 255),
            new HudColor(255, 210, 120, 255), new HudColor(190, 160, 255, 255),
        };

        private HudModel.MinimapPanel BuildMinimap()
        {
            if (timer.Gate == null) return null;
            var line = replaying ? replayTrace : GhostTrace;
            var line2 = secondGhostTrace != null && !ReferenceEquals(secondGhostTrace, line) ? secondGhostTrace : null;
            // After the race: the best lap from before it (purple) against your best lap of the race (orange).
            bool comparing = !replaying && timer.State != TimerState.Running && results != null && compareMine != null;
            if (comparing) { line = compareRef ?? compareMine; line2 = compareRef != null ? compareMine : null; }
            // What the map's route and lines are made from, as a number (no text built every frame).
            var hk = new HashCode();
            hk.Add(game.WorldId);
            void AddGate(TimingGate g) { if (g == null) { hk.Add(0); return; } hk.Add(Math.Round(g.Center.X)); hk.Add(Math.Round(g.Center.Y)); hk.Add(Math.Round(g.Center.Z)); }
            AddGate(timer.Gate); AddGate(timer.FinishGate);
            hk.Add(timer.Checkpoints.Count);
            foreach (var cp in timer.Checkpoints) AddGate(cp);
            hk.Add(line == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(line)); hk.Add(line?.Count ?? 0);
            hk.Add(line2 == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(line2)); hk.Add(line2?.Count ?? 0);
            hk.Add(settings.MinimapSize); hk.Add(settings.RacingLineColourMode); hk.Add(comparing);
            foreach (var sec in settings.CourseSectors) { hk.Add(sec.Name); hk.Add(sec.From); hk.Add(sec.To); }
            int key = hk.ToHashCode();
            if (key != minimapKey || mmStatic == null)
            {
                minimapKey = key;
                var route = CourseRoute();
                // Axes: the start gate's right and forward, flattened onto the ground (its up).
                Vec3D up = timer.Gate.Up.Normalized();
                Vec3D fwd = timer.Gate.Forward - up * Vec3D.Dot(timer.Gate.Forward, up);
                if (fwd.Length < 0.1) fwd = timer.Gate.Right - up * Vec3D.Dot(timer.Gate.Right, up);
                mmE2 = fwd.Normalized();
                mmE1 = Vec3D.Cross(mmE2, up).Normalized();
                if (Vec3D.Dot(mmE1, timer.Gate.Right) < 0) mmE1 = mmE1 * -1;   // keep the gate's right on the right
                mmOrigin = timer.Gate.Center;

                var all = route.Select(r => r.Gate.Center).ToList();
                if (line != null) all.AddRange(line.Select(t => new Vec3D(t[0], t[1], t[2])));
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (var p in all.Count > 0 ? all : new List<Vec3D> { mmOrigin })
                {
                    Vec3D d = p - mmOrigin;
                    double u = Vec3D.Dot(d, mmE1), v = Vec3D.Dot(d, mmE2);
                    minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
                }
                // The map is round: fit everything inside the circle (not just the square round it), with a
                // margin, so the course and line never touch or cross the edge.
                double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2, radius = 0;
                foreach (var p in all)
                {
                    Vec3D d = p - mmOrigin;
                    double du = Vec3D.Dot(d, mmE1) - cu, dv = Vec3D.Dot(d, mmE2) - cv;
                    radius = Math.Max(radius, Math.Sqrt(du * du + dv * dv));
                }
                double span = Math.Max(radius * 2, 50) * 1.3;   // room round the edge for the labels
                mmSpan = span;
                mmMinU = cu - span / 2;
                mmMinV = cv - span / 2;

                mmStatic = new HudModel.MinimapPanel { Size = settings.MinimapSize, StaticSig = "mm" + (++minimapVersion) };
                foreach (var r in route) mmStatic.Route.Add(MapPoint(r.Gate.Center));
                // Named sectors: the route from one point to another, each in its own colour.
                for (int k = 0; k < settings.CourseSectors.Count; k++)
                {
                    var sec = settings.CourseSectors[k];
                    int a = Math.Clamp(sec.From, 0, mmStatic.Route.Count - 1), b = Math.Clamp(sec.To, 0, mmStatic.Route.Count - 1);
                    if (b <= a) continue;
                    mmStatic.Sectors.Add((mmStatic.Route.GetRange(a, b - a + 1), sec.Name, SectorColours[k % SectorColours.Length]));
                }
                void AddLine(List<double[]> tr, List<(double X, double Y)> to)
                {
                    if (tr == null) return;
                    int step = Math.Max(1, tr.Count / 300);
                    for (int i = 0; i < tr.Count; i += step) to.Add(MapPoint(new Vec3D(tr[i][0], tr[i][1], tr[i][2])));
                }
                AddLine(line, mmStatic.Line);
                AddLine(line2, mmStatic.Line2);
                if (comparing) { mmStatic.LegendMine = compareMineLabel; mmStatic.LegendRef = compareRefLabel; }
                BuildMapExtras(line, line2, route, comparing);
                // The map's line takes the racing line colours (speed / braking), one colour per drawn segment.
                if (line != null && line2 == null && settings.RacingLineColourMode > 0)
                {
                    var cols = LineSpeedColours(line, settings.RacingLineColourMode);
                    int step = Math.Max(1, line.Count / 300);
                    for (int i = 0; i + step < line.Count; i += step) mmStatic.LineColours.Add(cols[Math.Min(cols.Length - 1, i)]);
                }
            }

            var m = new HudModel.MinimapPanel { Size = mmStatic.Size, StaticSig = mmStatic.StaticSig, LineColour = line2 != null ? HudColor.Purple : RacingLineColour };
            // The same (unchanging) lists every frame, not copies.
            m.Route = mmStatic.Route;
            m.Line = mmStatic.Line;
            m.Line2 = mmStatic.Line2;
            m.LineColours = mmStatic.LineColours;
            m.Sectors = mmStatic.Sectors;
            m.Diverge = mmStatic.Diverge;
            m.Line2Colours = mmStatic.Line2Colours;
            m.GateBars = mmStatic.GateBars;
            m.Profile = mmStatic.Profile;
            m.ProfileAboveGround = mmStatic.ProfileAboveGround;
            m.ProfileMarks = mmStatic.ProfileMarks;
            m.HoverPoints = mmStatic.HoverPoints;
            m.MetresAcross = mmStatic.MetresAcross;
            m.NorthX = mmStatic.NorthX; m.NorthY = mmStatic.NorthY; m.HasNorth = mmStatic.HasNorth;
            m.LegendMine = mmStatic.LegendMine; m.LegendRef = mmStatic.LegendRef;
            if (comparing && resultsCrashSpots != null)
                foreach (var c in resultsCrashSpots) { var cp = MapPoint(c.At); m.Crashes.Add((cp.X, cp.Y)); m.CrashTimes.Add(c.T); }
            if (comparing && resultsCpTimes != null)
            {
                // For the Race Map's replay of the lap: when each checkpoint went green (or red, when missed).
                int cpBase = timer.FinishGate != null ? 2 : 1;
                foreach (var (cp, hit, missedAt) in resultsCpTimes) m.AnimCheckpoints.Add((cpBase + cp, hit, missedAt));
                m.AnimLapTime = resultsLapTime;
                m.Laps = resultLaps.Count;
            }

            // Gates and checkpoints: the next one orange, flown ones green, missed ones red, the rest grey; gates cyan / purple.
            bool running = timer.State == TimerState.Running;
            int next = running ? timer.NextCheckpoint : -2;
            var start = MapPoint(timer.Gate.Center);
            m.Points.Add((start.X, start.Y, HudColor.Cyan, true, timer.FinishGate != null ? "START" : "S/F"));
            if (timer.FinishGate != null) { var f = MapPoint(timer.FinishGate.Center); m.Points.Add((f.X, f.Y, HudColor.Purple, true, "FINISH")); }
            for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                var c = MapPoint(timer.Checkpoints[i].Center);
                var col = comparing && resultsMissed != null ? (resultsMissed.Contains(i) ? HudColor.Red : HudColor.Green)
                    : timer.IsCheckpointHit(i) ? HudColor.Green : timer.IsMissed(i) ? HudColor.Red : i == next ? HudColor.Orange : HudColor.Grey;
                m.Points.Add((c.X, c.Y, col, false, (i + 1).ToString()));
            }

            // Highlighted target: the next checkpoint, or the finish / start gate when that's next (or before a race).
            int firstCp = timer.FinishGate != null ? 2 : 1;
            m.NextIndex = next >= 0 ? firstCp + next : next == -1 && timer.FinishGate != null ? 1 : 0;

            // You, and the ghosts where they are now.
            Vec3D? me = replaying ? replayCam : playerPosition;
            if (me.HasValue && !replaying)
            {
                var p = MapPoint(me.Value);
                m.PlayerX = p.X; m.PlayerY = p.Y;
                // The arrow points the way you're moving once you're going (that's what matters on the map), else
                // the way the cockpit faces.
                Vec3D? heading = playerForward;
                if (lastVelocity.HasValue)
                {
                    Vec3D v = lastVelocity.Value;
                    double flat = Math.Sqrt(Math.Pow(Vec3D.Dot(v, mmE1), 2) + Math.Pow(Vec3D.Dot(v, mmE2), 2));
                    if (flat > 5) heading = v;
                }
                if (heading.HasValue)
                    m.PlayerAngle = Math.Atan2(Vec3D.Dot(heading.Value, mmE1), Vec3D.Dot(heading.Value, mmE2)) * 180 / Math.PI;
            }
            // View: zoomed in round you while you're moving, the whole course when you're still; eased between.
            // Fully zoomed in from 20 m/s; below that it zooms out gradually, to the whole course at 4 m/s.
            double speed = settings.MinimapFollow && !replaying && !editMode && m.PlayerX.HasValue && game.TryGetShipVelocity(out Vec3D vel) ? vel.Length : 0;
            double zoomIn = Math.Clamp((speed - 4) / (20 - 4), 0, 1);
            bool moving = zoomIn > 0;
            double tx = 0.5, ty = 0.5, tspan = 1;
            if (moving)
            {
                double zoomed = Math.Min(1, Math.Max(0.02, settings.MinimapZoom / mmSpan));
                tspan = 1 + (zoomed - 1) * zoomIn;
                tx = 0.5 + (m.PlayerX.Value - 0.5) * zoomIn;
                ty = 0.5 + (m.PlayerY.Value - 0.5) * zoomIn;
            }
            double now = game.TimeSeconds, dt = Math.Clamp(now - mmViewAt, 0, 0.5);
            mmViewAt = now;
            double ease = 1 - Math.Exp(-dt * 3);           // about a second to settle
            double follow = 1 - Math.Exp(-dt * 12);        // the centre keeps up with you closely
            mmViewSpan += (tspan - mmViewSpan) * ease;
            double cEase = moving && mmViewSpan < 0.95 ? follow : ease;
            mmViewX += (tx - mmViewX) * cEase;
            mmViewY += (ty - mmViewY) * cEase;
            m.ViewX = mmViewX; m.ViewY = mmViewY; m.ViewSpan = mmViewSpan;

            double? ghostTime = replaying ? Math.Min(ReplayTime, replayLength)
                : settings.ShowGhost && timer.State == TimerState.Running ? timer.CurrentLapSeconds(game.TimeSeconds) : (double?)null;
            if (ghostTime.HasValue)
            {
                if (GhostPose(line, ghostTime.Value, out Vec3D g1, out _)) { var p = MapPoint(g1); m.Ghosts.Add((p.X, p.Y, GhostColour)); }
                if (line2 != null && GhostPose(line2, ghostTime.Value, out Vec3D g2, out _)) { var p = MapPoint(g2); m.Ghosts.Add((p.X, p.Y, SecondGhostColour)); }
            }
            // After the race: the shown lap flown again as a white dot, then on to the next lap.
            // (Not while the terminal's Race Map is open: it plays the lap itself, and this one made a second dot on it.)
            if (comparing && settings.MinimapLapReplay && !controlsOpen && resultsLapTime > 0 && !game.RaceMapShown)
            {
                double nowS = game.TimeSeconds;
                if (!ReferenceEquals(mapReplayFor, compareMine)) { mapReplayFor = compareMine; mapReplayStart = nowS; }
                double duration = Math.Clamp(resultsLapTime / 8, 8, 15);   // seconds a lap's playback takes
                double share = (nowS - mapReplayStart) / duration;
                if (share > 1 + 1.5 / duration)
                {
                    if (resultLaps.Count > 1 && nowS >= mapReplayHoldUntil) StepResultsLap(1, true); else mapReplayStart = nowS;
                }
                else if (GhostPose(compareMine, Math.Min(share, 1) * resultsLapTime, out Vec3D rp, out _))
                {
                    var p = MapPoint(rp);
                    m.Ghosts.Add((p.X, p.Y, HudColor.White));
                }
            }
            return m;
        }

        private static readonly HudColor MainLineColour = HudColor.Purple.WithAlpha(255);   // purple, like the main ghost's label

        /// <summary>
        /// One racing line: a ribbon of lines (centre plus a few offset copies, wider further away so it stays a few
        /// pixels thick on screen). Colour: fixed, by speed, or magenta. offset nudges a 2nd line so two lines on the
        /// same path don't hide each other.
        /// </summary>
        private void DrawRacingLine(List<double[]> trace, HudColor? colour, int colourMode, Vec3D me, int offset)
        {
            if (trace == null || trace.Count < 2) return;
            byte alpha = (byte)Math.Clamp(settings.RacingLineOpacity * 2.55, 20, 255);
            var line = (colour ?? RacingLineColour).WithAlpha(alpha);
            Vec3D side = timer.Gate.Right, lift = timer.Gate.Up;
            var speedColours = colourMode > 0 ? LineSpeedColours(trace, colourMode) : null;
            double maxD = settings.RacingLineDistance, widthMul = settings.RacingLineWidth;
            bool strips = settings.GlowLines && game.GlowLayerReady;
            int drawn = 0;
            Vec3D At(int i) => new Vec3D(trace[i][0], trace[i][1], trace[i][2]);
            void Seg(Vec3D a, Vec3D b, HudColor c, double w)
            {
                if (offset != 0) { var o = lift * (w * 3 * offset); a += o; b += o; }
                // (One thick line through the game's 3D UI layer, when it's there.)
                if (strips) { game.DrawGlowRibbon(a, b, c, w * 2, false); return; }
                game.DrawMarkerLine(a, b, c);
                game.DrawMarkerLine(a + side * w, b + side * w, c);
                game.DrawMarkerLine(a - side * w, b - side * w, c);
                game.DrawMarkerLine(a + lift * w, b + lift * w, c);
                game.DrawMarkerLine(a - lift * w, b - lift * w, c);
            }
            // The possible lap's line loops: its end (coming up to the finish) joins back onto its start, smoothed like
            // its other joins, so it's one unbroken loop. (Only that one: other lines are drawn as they were flown.)
            int count = trace.Count;
            double gapLen = count > 8 ? (At(count - 1) - At(0)).Length : 0;
            bool loops = racePossible && ReferenceEquals(trace, possibleTrace) && gapLen > 0.01 && gapLen < 400;
            for (int i = 0; i + 1 < count + (loops ? 1 : 0) && drawn < 1500; i++)
            {
                int j = (i + 1) % count;
                Vec3D a = At(i), b = At(j);
                double len = (b - a).Length;
                if (len > 400) continue; // a jump in the recording (respawn / teleport), not flying
                double d = ((a + b) * 0.5 - me).Length;
                if (d > maxD) continue;
                var c = speedColours != null ? speedColours[Math.Min(i, speedColours.Length - 1)].WithAlpha(alpha) : line;
                double w = Math.Max(0.25, d * 0.0012) * widthMul; // ~2 px wide
                // Smooth corners: a curve through the points either side (Catmull-Rom), in more pieces up close.
                int pieces = len < 12 ? 1 : (int)Math.Min(d < 600 ? 6 : d < 1200 ? 3 : 1, Math.Ceiling(len / 12));
                // (As solid strips through the 3D UI layer each piece costs more than a thin line did: fewer of
                // them, and only close up, where a corner cut into straight bits would show.)
                if (strips && pieces > 1) pieces = d < 300 ? Math.Min(pieces, 3) : 1;
                if (pieces <= 1) { Seg(a, b, c, w); drawn++; continue; }
                Vec3D p0 = i > 0 && (a - At(i - 1)).Length < 400 ? At(i - 1) : a - (b - a);
                int k3 = loops ? (i + 2) % count : i + 2;
                Vec3D p3 = k3 < count && (At(k3) - b).Length < 400 ? At(k3) : b + (b - a);
                Vec3D prev = a;
                for (int k = 1; k <= pieces; k++)
                {
                    double t = (double)k / pieces, t2 = t * t, t3 = t2 * t;
                    Vec3D pt = k == pieces ? b : (a * 2 + (b - p0) * t + (p0 * 2 - a * 5 + b * 4 - p3) * t2 + (a * 3 - p0 - b * 3 + p3) * t3) * 0.5;
                    Seg(prev, pt, c, w);
                    prev = pt;
                    drawn++;
                }
            }
        }

        // ---- Braking markers: where the best lap started braking hard and where it was speeding up again.
        private List<double[]> brakeMarksFor;
        private List<(Vec3D At, bool Brake, double Speed)> brakeMarks;

        private List<(Vec3D At, bool Brake, double Speed)> BrakeMarks(List<double[]> trace)
        {
            if (ReferenceEquals(trace, brakeMarksFor) && brakeMarks != null) return brakeMarks;
            brakeMarksFor = trace;
            brakeMarks = new List<(Vec3D, bool, double)>();
            int n = trace.Count;
            if (n < 8 || trace[0].Length < 4) return brakeMarks;
            // Speed between each pair of points, smoothed a little.
            var raw = new double[n - 1];
            for (int i = 0; i + 1 < n; i++)
            {
                double dt = trace[i + 1][3] - trace[i][3];
                double dist = new Vec3D(trace[i + 1][0] - trace[i][0], trace[i + 1][1] - trace[i][1], trace[i + 1][2] - trace[i][2]).Length;
                raw[i] = dt > 1e-3 && dist < 400 ? dist / dt : (i > 0 ? raw[i - 1] : 0);
            }
            var v = new double[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                double sum = 0; int cnt = 0;
                for (int k = Math.Max(0, i - 1); k <= Math.Min(raw.Length - 1, i + 1); k++) { sum += raw[k]; cnt++; }
                v[i] = sum / cnt;
            }
            Vec3D P(int i) => new Vec3D(trace[i][0], trace[i][1], trace[i][2]);
            for (int i = 0; i + 1 < v.Length; i++)
            {
                double dt = Math.Max(0.05, trace[i + 1][3] - trace[i][3]);
                if ((v[i] - v[i + 1]) / dt < 12) continue;   // not braking hard (about 1.2 g)
                int start = i, end = i + 1;
                while (end + 1 < v.Length && (v[end] - v[end + 1]) / Math.Max(0.05, trace[end + 1][3] - trace[end][3]) > 2) end++;
                double from = v[start], low = v[end];
                if (from - low >= Math.Max(25, from * 0.12))
                {
                    brakeMarks.Add((P(start), true, from));
                    brakeMarks.Add((P(Math.Min(n - 1, end + 1)), false, low));
                }
                i = end;
            }
            return brakeMarks;
        }

        private void DrawBrakeMarks(List<double[]> trace, Vec3D me, HudModel model, bool hudLabels)
        {
            var marks = BrakeMarks(trace);
            if (marks.Count == 0) return;
            Vec3D side = timer.Gate.Right.Normalized(), lift = timer.Gate.Up.Normalized();
            double maxD = Math.Min(settings.RacingLineDistance, 2500);
            int labels = 0;
            foreach (var (at, brake, speed) in marks)
            {
                double d = (at - me).Length;
                if (d > maxD || d < 5) continue;
                var c = brake ? new HudColor(255, 60, 50, 255) : new HudColor(60, 230, 90, 255);
                double h = Math.Max(3, d * 0.02) * settings.RacingLineWidth;
                // A post across the line with a bar on top: easy to spot coming up.
                game.DrawMarkerLine(at - lift * h * 0.4, at + lift * h, c);
                game.DrawMarkerLine(at + lift * h - side * h * 0.5, at + lift * h + side * h * 0.5, c);
                game.DrawMarkerLine(at - side * h * 0.5, at + side * h * 0.5, c);
                // (Named for what to do, and only while a lap is being flown: waiting at the start, a lone "GO" tag
                // out on the course meant nothing.)
                if (d < 900 && labels < 4 && (timer.State == TimerState.Running || replaying))
                {
                    string text = brake ? "BRAKE" : "THROTTLE";
                    if (hudLabels)
                    {
                        if (game.TryProjectToScreen(at + lift * h, out double x, out double y, out bool behind) && !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98)
                        {
                            model.Markers.Add(new HudModel.ScreenMarker { X = x, Y = y, Text = text, Sub = GameOptions.Speed(speed), Colour = c, World = at + lift * h, SubInline = true });
                            labels++;
                        }
                    }
                    else { game.DrawMarkerText(at + lift * (h * 1.3), $"{text} {GameOptions.Speed(speed)}", c, 0.45); labels++; }
                }
            }
        }

        private List<double[]> speedColoursFor;
        private HudColor[] speedColours;

        /// <summary>
        /// A colour per racing line segment from the speed the lap was flown at there: red at the slowest parts,
        /// yellow in the middle, green at the fastest (scaled between the lap's slow and fast ends, so short spikes
        /// don't wash the colours out). Worked out once per recorded lap.
        /// </summary>
        private int speedColoursMode;

        private HudColor[] LineSpeedColours(List<double[]> trace, int mode)
        {
            if (ReferenceEquals(trace, speedColoursFor) && speedColours != null && speedColoursMode == mode) return speedColours;
            var speeds = new double[Math.Max(0, trace.Count - 1)];
            for (int i = 0; i + 1 < trace.Count; i++)
            {
                double dt = trace[i + 1][3] - trace[i][3];
                double dx = trace[i + 1][0] - trace[i][0], dy = trace[i + 1][1] - trace[i][1], dz = trace[i + 1][2] - trace[i][2];
                speeds[i] = dt > 1e-3 ? Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt : 0;
            }
            // Smooth a little (the recording is a point every quarter second).
            var smooth = new double[speeds.Length];
            for (int i = 0; i < speeds.Length; i++)
            {
                double sum = 0; int n = 0;
                for (int k = Math.Max(0, i - 2); k <= Math.Min(speeds.Length - 1, i + 2); k++) { sum += speeds[k]; n++; }
                smooth[i] = n > 0 ? sum / n : 0;
            }
            if (mode == 2)
            {
                // Braking / accelerating: change of speed per second along the lap. Red = slowing (harder = redder),
                // green = speeding up, white = holding speed. Scaled to the lap's hardest 10 %.
                var acc = new double[smooth.Length];
                for (int i = 0; i < smooth.Length; i++)
                {
                    int a0 = Math.Max(0, i - 1), a1 = Math.Min(smooth.Length - 1, i + 1);
                    // Segment k is flown around the middle of its two points' times.
                    double Mid(int k) => (trace[k][3] + trace[k + 1][3]) / 2;
                    double dt = Mid(a1) - Mid(a0);
                    acc[i] = dt > 1e-3 ? (smooth[a1] - smooth[a0]) / dt : 0;
                }
                var mags = acc.Select(Math.Abs).OrderBy(v => v).ToArray();
                double scale = mags.Length > 0 ? Math.Max(1, mags[(int)(mags.Length * 0.9)]) : 1;
                var bc = new HudColor[acc.Length];
                for (int i = 0; i < acc.Length; i++)
                {
                    double t = Math.Clamp(acc[i] / scale, -1, 1);
                    bc[i] = t < 0
                        ? new HudColor(255, (byte)(235 - 195 * -t), (byte)(235 - 195 * -t), 255)            // white -> red
                        : new HudColor((byte)(235 - 195 * t), 235, (byte)(235 - 155 * t), 255);              // white -> green
                }
                speedColoursFor = trace; speedColours = bc; speedColoursMode = mode;
                return bc;
            }
            var sorted = smooth.Where(v => v > 0.5).OrderBy(v => v).ToArray();
            double lo = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.05)] : 0;
            double hi = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))] : 1;
            if (hi - lo < 1) hi = lo + 1;
            var colours = new HudColor[smooth.Length];
            for (int i = 0; i < smooth.Length; i++)
            {
                double t = Math.Clamp((smooth[i] - lo) / (hi - lo), 0, 1);
                // red (255, 50, 40) -> yellow (255, 215, 0) -> green (40, 230, 80)
                colours[i] = t < 0.5
                    ? new HudColor(255, (byte)(50 + (215 - 50) * t * 2), (byte)(40 - 40 * t * 2), 255)
                    : new HudColor((byte)(255 - (255 - 40) * (t - 0.5) * 2), (byte)(215 + (230 - 215) * (t - 0.5) * 2), (byte)(80 * (t - 0.5) * 2), 255);
            }
            speedColoursFor = trace;
            speedColours = colours;
            speedColoursMode = mode;
            return colours;
        }

        // The 2nd ghost's hull is kept separately so the two ghosts don't rebuild each other's every frame.
        private (List<double[]> For, List<double[]> Faces, double[] Bounds, List<double[]> Edges) hull2;
        private void SwapHull()
        {
            var mine = (hullFor, hullFaces, hullBounds, hullEdges);
            (hullFor, hullFaces, hullBounds, hullEdges) = hull2;
            hull2 = mine;
        }

        private void DrawGhostShip(List<double[]> ship, Vec3D g, Vec3D[] axes, double dist, bool second = false)
        {
            if (second) SwapHull();
            try { DrawGhostShipInner(ship, g, axes, dist, second); }
            finally { if (second) SwapHull(); }
        }

        private List<double[]> hullBox, hullBoxFor;

        private void DrawGhostShipInner(List<double[]> ship, Vec3D g, Vec3D[] axes, double dist, bool second)
        {
            if (!ReferenceEquals(ship, hullFor))
            {
                BuildHull(ship);
                PluginLog.Info($"Ghost ship: hull of {hullFaces?.Count ?? 0} faces, {hullEdges?.Count ?? 0} outline edges (detail setting {settings.GhostDetail})");
            }
            Vec3D X = axes[0], Y = axes[1], Z = axes[2];
            Vec3D[] normals = { X * -1, X, Y * -1, Y, Z * -1, Z };
            Vec3D light = (timer.Gate.Up * 0.8 + timer.Gate.Right * 0.35 + timer.Gate.Forward * 0.2).Normalized();

            IEnumerable<double[]> faces = hullFaces;
            IEnumerable<double[]> outline = hullEdges;
            // How much of it is drawn. It always keeps the ship's shape: the box is only for the Box setting, or a
            // ghost so far off it's a speck. Its see-through faces are the costly part (every face is drawn from both
            // sides, and right beside you they cover the whole screen in layers), so those are left off when it's
            // right on top of you, and far away, where the outline alone still shows the ship.
            // (Settings > Ghost and racing line > Ghost ship detail.)
            int ghostDetail = settings.GhostDetail;
            bool asBox = ghostDetail == 2 || dist > 600;
            bool bigHull = hullFaces.Count > 3000;
            bool facesOn = ghostDetail == 0 && dist >= 20 && dist <= (bigHull ? 200 : 350);
            if (asBox)
            {
                // Far away: just its bounding box (made once per hull).
                if (!ReferenceEquals(hullBoxFor, hullFaces))
                {
                    hullBoxFor = hullFaces;
                    hullBox = new List<double[]>();
                    for (int side = 0; side < 6; side++) hullBox.Add(BoxFace(hullBounds, side));
                }
                faces = hullBox;
                outline = null;
            }

            ghostCorners.Clear();
            ghostColours.Clear();
            Vec3D P(double[] f, int i) => g + X * f[i] + Y * f[i + 1] + Z * f[i + 2];
            if (asBox || facesOn)
            foreach (var f in faces)
            {
                // Pale blue-white, lighter on faces turned to the "light", and see-through.
                double shade = 0.7 + 0.3 * Math.Max(0, Vec3D.Dot(normals[(int)f[12]], light));
                var col = second
                    ? new HudColor((byte)(255 * shade), (byte)(165 * shade), (byte)(60 * shade), 110)   // the 2nd ghost: orange
                    : new HudColor((byte)(150 * shade), (byte)(225 * shade), (byte)(255 * shade), 110);
                Vec3D c0 = P(f, 0), c1 = P(f, 3), c2 = P(f, 6), c3 = P(f, 9);
                // Both windings, so it's visible from inside and outside.
                ghostCorners.Add(c0); ghostCorners.Add(c1); ghostCorners.Add(c2); ghostColours.Add(col);
                ghostCorners.Add(c0); ghostCorners.Add(c2); ghostCorners.Add(c3); ghostColours.Add(col);
                ghostCorners.Add(c0); ghostCorners.Add(c2); ghostCorners.Add(c1); ghostColours.Add(col);
                ghostCorners.Add(c0); ghostCorners.Add(c3); ghostCorners.Add(c2); ghostColours.Add(col);
            }
            if (ghostCorners.Count > 0) game.DrawMarkerTriangles(ghostCorners, ghostColours, true);

            // Glowing outline along the hull's edges: the crisp "ghost" look.
            if (outline != null)
            {
                ghostLines.Clear();
                foreach (var e in outline)
                {
                    ghostLines.Add(g + X * e[0] + Y * e[1] + Z * e[2]);
                    ghostLines.Add(g + X * e[3] + Y * e[4] + Z * e[5]);
                }
                HudColor ghostEdge = second ? new HudColor(255, 190, 90, 235) : new HudColor(130, 235, 255, 235);
                // (Only a simple hull's outline goes through the 3D UI layer: a detailed ship has well over a thousand
                // edges, and that many thick lines a frame cost several milliseconds.)
                if (settings.GlowLines && game.GlowLayerReady && ghostLines.Count <= 600)
                {
                    double edgeThick = Math.Max(0.05, (playerPosition.HasValue ? (g - playerPosition.Value).Length : 100) * 0.0011);
                    for (int edgeAt = 0; edgeAt + 1 < ghostLines.Count; edgeAt += 2) game.DrawGlowLine(ghostLines[edgeAt], ghostEdge, ghostLines[edgeAt + 1], ghostEdge, edgeThick, true);
                }
                else game.DrawMarkerLines(ghostLines, ghostEdge, true);
            }
        }

        private static string Distance(double m) => m >= 1000 ? $"{m / 1000:0.0} km" : $"{m:F0} m";

        /// <summary>
        /// Adds HUD labels for the checkpoints and the gate at their place on screen, and an arrow at the screen
        /// edge pointing to where you're heading when it's off screen or behind you. False if the camera isn't
        /// available (the debug-draw labels are used instead).
        /// </summary>
        private bool AddScreenMarkers(HudModel model)
        {
            if (timer.Gate == null) return true;
            if (!game.TryProjectToScreen(timer.Gate.Center, out _, out _, out _)) return false;

            bool racing = timer.State == TimerState.Running;
            int next = racing ? timer.NextCheckpoint : -1;
            Vec3D? me = playerPosition;

            void Add(TimingGate g, string text, HudColor colour, bool target, bool farAhead = false)
            {
                if (!game.TryProjectToScreen(g.Center, out double x, out double y, out bool behind)) return;
                double dist = me.HasValue ? (g.Center - me.Value).Length : 0;
                bool onScreen = !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98;
                if (onScreen)
                    model.Markers.Add(new HudModel.ScreenMarker
                    {
                        X = x, Y = y, Text = text, Colour = colour, Emphasis = target, World = g.Center,
                        Quiet = farAhead && !target,
                        Sub = target && me.HasValue ? Distance(dist) : null,
                    });
                else if (target)
                {
                    // Direction from the screen centre (in pixels, so the angle is right on a wide screen).
                    double w = Math.Max(1, game.ScreenWidth), h = Math.Max(1, game.ScreenHeight);
                    double dx = (x - 0.5) * w, dy = (y - 0.5) * h;
                    if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) dy = 1; // straight behind: point down
                    double k = Math.Min(0.44 * w / Math.Max(1e-6, Math.Abs(dx)), 0.40 * h / Math.Max(1e-6, Math.Abs(dy)));
                    model.Arrow = new HudModel.EdgeArrow
                    {
                        X = 0.5 + dx * k / w,
                        Y = 0.5 + dy * k / h,
                        AngleDegrees = Math.Atan2(dy, dx) * 180 / Math.PI,
                        Text = me.HasValue ? $"{text}  {Distance(dist)}" : text,
                        Colour = colour,
                        World = g.Center,
                    };
                }
            }

            for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                if (!MarkerFor(i, next, out string name)) continue;   // a ring used twice gets one label
                if (!CheckpointAhead(i, next, racing)) continue;      // Only mark the next checkpoints
                bool hit = timer.IsCheckpointHit(i);
                if (hit && racing) continue;   // (already flown through this lap: its label would only be clutter)
                bool isNext = i == next;
                HudColor colour = hit ? HudColor.Green.WithAlpha(130)
                                : timer.IsMissed(i) ? HudColor.Red.WithAlpha(150)
                                : isNext ? HudColor.Orange.WithAlpha(255)
                                : racing ? HudColor.Orange.WithAlpha(150) : HudColor.Orange;
                // Further down the order: fainter and smaller, so the next one stands out.
                int ahead = racing && next >= 0 && !hit && !timer.IsMissed(i) ? i - next : 0;
                if (ahead >= 1) colour = HudColor.Orange.WithAlpha((byte)(ahead == 1 ? 170 : ahead == 2 ? 120 : 85));
                Add(timer.Checkpoints[i], name, colour, isNext, ahead >= 2);
            }
            bool gateIsTarget = next == -1;
            if (timer.FinishGate != null)
            {
                // Sprint: the start gate is only a target before the race; the finish gate is the last target.
                if (GateAhead(false, racing)) Add(timer.Gate, "START", HudColor.Cyan.WithAlpha((byte)(racing ? 140 : 255)), !racing);
                if (GateAhead(true, racing)) Add(timer.FinishGate, "FINISH", HudColor.Purple.WithAlpha(255), racing && gateIsTarget);
            }
            else if (GateAhead(true, racing) || GateAhead(false, racing)) Add(timer.Gate, "START / FINISH", HudColor.Cyan.WithAlpha(255), gateIsTarget);
            return true;
        }

        /// <summary>One line of a marker: thick through the game's 3D UI layer, else a plain marker line. Always on top.</summary>
        private void MarkerLine(Vec3D a, Vec3D b, HudColor colour, double thick)
        {
            if (settings.MarkerThick && game.GlowLayerReady) game.DrawGlowLine(a, colour, b, colour, thick, false);
            else game.DrawMarkerLine(a, b, colour);
        }

        private void MarkerCircle(Vec3D centre, Vec3D across, Vec3D tall, double radius, HudColor colour, double thick, int steps = 28)
        {
            Vec3D last = centre + across * radius;
            for (int i = 1; i <= steps; i++)
            {
                double turn = i * 2 * Math.PI / steps;
                Vec3D spot = centre + across * (Math.Cos(turn) * radius) + tall * (Math.Sin(turn) * radius);
                MarkerLine(last, spot, colour, thick);
                last = spot;
            }
        }

        /// <summary>
        /// The marker's icon (Marker icon setting): a ring facing you, the wire diamond, a chevron or a beacon. The
        /// one to fly to next (isNext) moves, so it stands out from the others.
        /// </summary>
        private void DrawMarkerIcon(TimingGate gate, Vec3D c, double size, double distance, HudColor colour, bool isNext)
        {
            // (About two pixels wide whatever the distance; the next one a little heavier.)
            double thick = Math.Max(0.03, distance * (isNext ? 0.0022 : 0.0014));
            double clock = game.TimeSeconds;
            // Across and up as you see them: at right angles to the line from you to the marker.
            Vec3D view = playerPosition.HasValue ? (c - playerPosition.Value).Normalized() : gate.Forward;
            if (view.Length < 0.5) view = gate.Forward;
            Vec3D tall = gate.Up - view * Vec3D.Dot(gate.Up, view);
            if (tall.Length < 0.1) tall = gate.Right - view * Vec3D.Dot(gate.Right, view);
            tall = tall.Normalized();
            Vec3D across = Vec3D.Cross(view, tall).Normalized();
            switch (settings.MarkerIcon)
            {
                case 1:
                {
                    // The wire diamond; the next one turns about its upright.
                    double spin = isNext ? clock * 1.6 : 0;
                    Vec3D top = c + gate.Up * size, bottom = c - gate.Up * size;
                    var ring = new Vec3D[4];
                    for (int i = 0; i < 4; i++)
                    {
                        double turn = spin + i * Math.PI / 2;
                        ring[i] = c + gate.Right * (Math.Cos(turn) * size * 0.6) + gate.Forward * (Math.Sin(turn) * size * 0.6);
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        MarkerLine(top, ring[i], colour, thick);
                        MarkerLine(bottom, ring[i], colour, thick);
                        MarkerLine(ring[i], ring[(i + 1) % 4], colour, thick);
                    }
                    break;
                }
                case 2:
                {
                    // A chevron pointing down at the spot; the next one is two, bobbing.
                    double bob = isNext ? (0.5 + 0.5 * Math.Sin(clock * 5)) * size * 0.3 : 0;
                    for (int n = 0; n < (isNext ? 2 : 1); n++)
                    {
                        Vec3D tip = c + tall * (bob + n * size * 0.55);
                        MarkerLine(tip + across * (size * 0.7) + tall * (size * 0.8), tip, colour, thick);
                        MarkerLine(tip - across * (size * 0.7) + tall * (size * 0.8), tip, colour, thick);
                    }
                    break;
                }
                case 3:
                {
                    // A beam standing on the spot (away from gravity when there is some), a small ring at its foot;
                    // the next one's is taller with a ring climbing it.
                    Vec3D upright = game.TryGetGravity(out Vec3D down) ? down * -1 : gate.Up;
                    double height = size * (isNext ? 9 : 6);
                    MarkerLine(c, c + upright * height, colour, thick * 1.3);
                    Vec3D flatA = Vec3D.Cross(upright, view).Normalized();
                    if (flatA.Length < 0.5) flatA = across;
                    Vec3D flatB = Vec3D.Cross(upright, flatA).Normalized();
                    MarkerCircle(c, flatA, flatB, size * 0.6, colour, thick, 16);
                    if (isNext)
                    {
                        double climb = clock * 0.6 % 1;
                        MarkerCircle(c + upright * (height * climb), flatA, flatB, size * 0.45 * (1 - 0.5 * climb), colour.WithAlpha((byte)(colour.A * (1 - 0.7 * climb))), thick, 16);
                    }
                    break;
                }
                default:
                {
                    // A ring facing you; the next one has a second ring pulsing out from it and four marks pointing in.
                    // (Kept small and plain: a screenful of big rings was too much.)
                    MarkerCircle(c, across, tall, size * 0.5, colour, thick, isNext ? 28 : 20);
                    if (isNext)
                    {
                        double beat = clock * 0.8 % 1;
                        MarkerCircle(c, across, tall, size * (0.5 + 0.3 * beat), colour.WithAlpha((byte)(colour.A * (1 - beat))), thick * 0.7);
                        for (int i = 0; i < 4; i++)
                        {
                            Vec3D way = i == 0 ? across : i == 1 ? tall : i == 2 ? across * -1 : tall * -1;
                            MarkerLine(c + way * (size * 0.5), c + way * (size * 0.3), colour, thick);
                        }
                    }
                    break;
                }
            }
        }

        private void DrawMarker(TimingGate gate, string label, HudColor colour, bool showDistance, double emphasis, bool drawText = true)
        {
            Vec3D c = gate.Center;
            double distance = playerPosition.HasValue ? (c - playerPosition.Value).Length : 500;
            double size = Math.Max(1.5, distance * settings.MarkerSize) * emphasis;

            Vec3D bottom = c - gate.Up * size;
            DrawMarkerIcon(gate, c, size, distance, colour, emphasis > 1);

            if (!drawText) return; // the HUD shows the label instead

            // Small label UNDER the icon, so it doesn't sit on the game's own GPS markers.
            string text = label;
            if (showDistance)
                text += distance >= 1000 ? $"  {distance / 1000:0.0} km" : $"  {distance:F0} m";
            game.DrawMarkerText(bottom - gate.Up * (size * 0.5), text, colour, emphasis > 1 ? 0.65 : 0.5);
        }

        /// <summary>
        /// The next air gate size up or down: a quarter bigger or smaller, to the nearest 5 m, and always at least
        /// 5 m different (small sizes rounded back to where they were, so 10 m could never change).
        /// </summary>
        private static double AirSizeStep(double size, bool bigger)
        {
            double next = Math.Round(size * (bigger ? GrowFactor : 1 / GrowFactor) / 5, MidpointRounding.AwayFromZero) * 5;
            if (bigger && next < size + 5) next = Math.Floor(size / 5) * 5 + 5;
            if (!bigger && next > size - 5) next = Math.Ceiling(size / 5) * 5 - 5;
            return Math.Clamp(next, 5, 300);
        }

        private TimingGate airFlashGate;   // the air gate just flown through: a ring bursts out of it
        private double airFlashAt;

        private void FlashAirGate(TimingGate g)
        {
            if (g == null || !g.Air) return;
            airFlashGate = g;
            airFlashAt = game.TimeSeconds;
        }

        /// <summary>Arrows on an air gate's rim (top, bottom and both sides) pointing the way through it.</summary>
        /// <param name="clock">The time, to have the arrows run through the gate over and over; under 0 = standing still.</param>
        /// <param name="strength">If given: how bright each line is (0 to 1), one per line: faint as an arrow sets off and as it arrives.</param>
        private void AddRimArrows(TimingGate g, List<Vec3D> into, double clock = -1, List<double> strength = null)
        {
            double len = Math.Clamp(Math.Min(g.HalfWidth, g.HalfHeight) * 0.22, 1, 14);
            // (Seen from far off they'd be specks: they grow with the distance, up to nearly the size of the gate.)
            if (playerPosition.HasValue && !editMode)
                len = Math.Max(len, Math.Min((g.Center - playerPosition.Value).Length * 0.02, Math.Max(g.HalfWidth, g.HalfHeight) * 0.9));
            for (int side = 0; side < 4; side++)
            {
                Vec3D outward = side == 0 ? g.Right * g.HalfWidth : side == 1 ? g.Up * g.HalfHeight : side == 2 ? g.Right * -g.HalfWidth : g.Up * -g.HalfHeight;
                Vec3D along = side % 2 == 0 ? g.Up : g.Right;   // (along the rim there)
                Vec3D at = g.Center + outward;
                for (int n = 0; n < 2; n++)   // (two, one behind the other: >>)
                {
                    // (Running: each sets off a little before the gate, passes through it and fades out beyond,
                    // the second half a beat behind the first.)
                    double flow = clock < 0 ? -1 : (clock * 0.7 + n * 0.5) % 1;
                    Vec3D tip = at + g.Forward * (len * (flow < 0 ? 0.6 + n * 0.7 : flow * 3.4 - 1.0));
                    Vec3D tail = tip - g.Forward * len;
                    into.Add(tail + along * (len * 0.55)); into.Add(tip);
                    into.Add(tail - along * (len * 0.55)); into.Add(tip);
                    if (strength != null) { double s = flow < 0 ? 1 : Math.Sin(Math.PI * flow); strength.Add(s); strength.Add(s); }
                }
            }
        }

        private readonly List<Vec3D> airLines = new List<Vec3D>();
        private readonly List<Vec3D> airFill = new List<Vec3D>();
        private readonly List<HudColor> airFillInk = new List<HudColor>();

        private static void AddLoop(List<Vec3D> rim, List<Vec3D> into)
        {
            for (int i = 0; i < rim.Count; i++) { into.Add(rim[i]); into.Add(rim[(i + 1) % rim.Count]); }
        }

        /// <summary>
        /// A gate placed in open air, drawn so there's something to fly through: its frame (a ring or a square, front
        /// and back). state 1 = the one to fly through next: brighter, a faint film across it, a ring closing in on
        /// its middle, and a red cross when you're on the far side of it. 0 = later, 2 = already flown.
        /// always = drawn over everything (edit mode); otherwise hidden behind terrain and grids.
        /// </summary>
        private bool airUi;   // air gate glow goes through the game's 3D UI layer (else the plain debug drawing)
        private bool airDepth = true;   // hidden behind terrain and grids (not in edit mode)

        private static HudColor Hot(HudColor c, double toWhite) =>
            new HudColor((byte)(c.R + (255 - c.R) * toWhite), (byte)(c.G + (255 - c.G) * toWhite), (byte)(c.B + (255 - c.B) * toWhite), c.A);

        private static HudColor Faded(HudColor c, double alpha) => c.WithAlpha((byte)Math.Clamp(alpha, 0, 255));

        /// <summary>
        /// A band between two loops of an air gate (the same number of points each), one colour at the inner loop
        /// blending to another at the outer. The 3D UI layer blends it itself; the plain drawing gets it in three
        /// steps (kept in airFill, drawn by the caller).
        /// </summary>
        private void GlowBand(List<Vec3D> inner, List<Vec3D> outer, HudColor atInner, HudColor atOuter)
        {
            int n = Math.Min(inner.Count, outer.Count);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (airUi) { game.DrawGlowQuad(inner[i], atInner, inner[j], atInner, outer[j], atOuter, outer[i], atOuter, airDepth); continue; }
                const int stepsAcross = 3;
                for (int k = 0; k < stepsAcross; k++)
                {
                    double from = (double)k / stepsAcross, to = (double)(k + 1) / stepsAcross, mid = (from + to) / 2;
                    Vec3D a = inner[i] + (outer[i] - inner[i]) * from, b = inner[j] + (outer[j] - inner[j]) * from;
                    Vec3D c2 = inner[j] + (outer[j] - inner[j]) * to, d = inner[i] + (outer[i] - inner[i]) * to;
                    var ink = new HudColor((byte)(atInner.R + (atOuter.R - atInner.R) * mid), (byte)(atInner.G + (atOuter.G - atInner.G) * mid),
                        (byte)(atInner.B + (atOuter.B - atInner.B) * mid), (byte)(atInner.A + (atOuter.A - atInner.A) * mid));
                    if (ink.A < 3) continue;
                    airFill.Add(a); airFill.Add(b); airFill.Add(c2); airFillInk.Add(ink);
                    airFill.Add(a); airFill.Add(c2); airFill.Add(d); airFillInk.Add(ink);
                }
            }
        }

        /// <summary>A loop of line: thick, through the 3D UI layer; else kept in airLines (drawn by the caller in one colour).</summary>
        private void GlowLoop(List<Vec3D> rim, HudColor ink, double thick)
        {
            if (!airUi) { AddLoop(rim, airLines); return; }
            for (int i = 0; i < rim.Count; i++) game.DrawGlowLine(rim[i], ink, rim[(i + 1) % rim.Count], ink, thick, airDepth);
        }

        /// <summary>
        /// An air gate with a glow (outside edit mode): a bright rim, a soft halo fading out either side of it, the
        /// frame's front and back, and for the next gate a film across the opening that's clear in the middle, arrows
        /// and the ring closing in. state as for DrawAirGate.
        /// </summary>
        private bool airChequer;   // the gate being drawn is the start or the finish: a chequered rim
        private bool airArrowsOnly;   // ... is on a grid whose course doesn't want the glowing frame: just the arrows through it

        /// <summary>
        /// A gate on a grid turned for drawing: face on (FaceOn), and pointing the way the course is flown through it
        /// (travel: from the point of the course before it towards it, or from it towards the one after).
        /// </summary>
        private static TimingGate Through(TimingGate g, Vec3D travel)
        {
            TimingGate face = FaceOn(g);
            return Vec3D.Dot(travel, face.Forward) < 0 ? face.Turned() : face;
        }

        /// <summary>One flat patch of an air gate in one colour (the 3D UI layer, or kept in airFill).</summary>
        private void GlowPatch(Vec3D a, Vec3D b, Vec3D c2, Vec3D d, HudColor ink)
        {
            if (airUi) { game.DrawGlowQuad(a, ink, b, ink, c2, ink, d, ink, airDepth); return; }
            airFill.Add(a); airFill.Add(b); airFill.Add(c2); airFillInk.Add(ink);
            airFill.Add(a); airFill.Add(c2); airFill.Add(d); airFillInk.Add(ink);
        }

        private static List<Vec3D> Loop(List<Vec3D> rim)
        {
            var pairs = new List<Vec3D>(rim.Count * 2);
            AddLoop(rim, pairs);
            return pairs;
        }

        /// <summary>A spot on a gate's rim: turn 0 to 1 is once round it (a ring, or the four sides of a square one).</summary>
        private static Vec3D RimSpot(TimingGate g, double scale, double turn)
        {
            turn -= Math.Floor(turn);
            if (g.Round)
            {
                double angle = turn * 2 * Math.PI;
                return g.Center + g.Right * (Math.Cos(angle) * g.HalfWidth * scale) + g.Up * (Math.Sin(angle) * g.HalfHeight * scale);
            }
            Vec3D across = g.Right * (g.HalfWidth * scale), tall = g.Up * (g.HalfHeight * scale);
            double side = turn * 4;
            int k = Math.Min(3, (int)side);
            double along = side - k;
            Vec3D from = g.Center + (k == 0 || k == 3 ? across : across * -1) + (k < 2 ? tall : tall * -1);
            int k2 = (k + 1) % 4;
            Vec3D to = g.Center + (k2 == 0 || k2 == 3 ? across : across * -1) + (k2 < 2 ? tall : tall * -1);
            return from + (to - from) * along;
        }

        /// <summary>Part of a gate's rim as a thick line through the 3D UI layer, one colour blending to another along it.</summary>
        private void GlowArc(TimingGate g, double scale, double from, double to, HudColor atStart, HudColor atEnd, double thick)
        {
            int pieces = Math.Max(1, (int)Math.Ceiling((to - from) * 64));
            Vec3D last = RimSpot(g, scale, from);
            HudColor lastInk = atStart;
            for (int i = 1; i <= pieces; i++)
            {
                double t = (double)i / pieces;
                Vec3D spot = RimSpot(g, scale, from + (to - from) * t);
                var ink = new HudColor((byte)(atStart.R + (atEnd.R - atStart.R) * t), (byte)(atStart.G + (atEnd.G - atStart.G) * t),
                    (byte)(atStart.B + (atEnd.B - atStart.B) * t), (byte)(atStart.A + (atEnd.A - atStart.A) * t));
                game.DrawGlowLine(last, lastInk, spot, ink, thick, airDepth);
                last = spot; lastInk = ink;
            }
        }

        /// <summary>
        /// A gate on a grid, for drawing its frame: its box turned so its thinnest side is the way through (a ring's
        /// box is thin along the way you fly through it, whichever of the box's sides that happens to be).
        /// </summary>
        private static TimingGate FaceOn(TimingGate g)
        {
            if (g.HalfDepth <= g.HalfWidth && g.HalfDepth <= g.HalfHeight) return g;
            if (g.HalfWidth <= g.HalfHeight) return new TimingGate(g.Center, g.Right, g.Up, g.Depth, g.Height, g.Width);
            return new TimingGate(g.Center, g.Up, g.Forward, g.Width, g.Depth, g.Height);
        }

        private void DrawAirGateGlow(TimingGate g, HudColor colour, int state, double away, bool always = false)
        {
            airDepth = !always;
            // (Edit mode: drawn over everything; through the 3D UI layer too when that look is chosen.)
            if (always) airUi = settings.AirGateLook >= 2 && game.GlowLayerReady;
            int steps = away > 1500 ? 24 : 48;
            double half = Math.Min(g.HalfWidth, g.HalfHeight);
            // (The rim keeps a width you can see from far off: thicker in metres the further away.)
            double thick = Math.Max(half * 0.03, away * (always ? 0.007 : 0.005));
            double w = Math.Min(0.2, thick / half);          // the rim's half width, as a share of the gate
            double strength = colour.A / 255.0;
            HudColor core = Hot(colour, state == 1 ? 0.55 : 0.25);
            airLines.Clear(); airFill.Clear(); airFillInk.Clear();
            if (airArrowsOnly)
            {
                // A built gate or ring speaks for itself: only the arrows running through it, the way to fly it.
                if (state == 2) return;
                HudColor arrowsInk = state == 1 ? core : Faded(Hot(colour, 0.35), Math.Min(255, 150 + colour.A * 0.5));
                if (airUi)
                {
                    var through = new List<Vec3D>();
                    var throughGlow = new List<double>();
                    AddRimArrows(g, through, game.TimeSeconds, throughGlow);
                    for (int i = 0; i + 1 < through.Count; i += 2)
                    {
                        HudColor ink = Faded(arrowsInk, arrowsInk.A * throughGlow[i / 2]);
                        game.DrawGlowLine(through[i], ink, through[i + 1], ink, thick * (state == 1 ? 0.9 : 0.7), airDepth);
                    }
                }
                else
                {
                    AddRimArrows(g, airLines, game.TimeSeconds);
                    game.DrawMarkerLines(airLines, arrowsInk, airDepth);
                }
                return;
            }

            var rimIn = g.Rim(0, 1 - w, steps);
            var rimOut = g.Rim(0, 1 + w, steps);
            if (airChequer)
            {
                // Start / finish: a chequered rim, two rows of white and dark squares.
                var rimMid = g.Rim(0, 1, steps);
                HudColor light = Faded(HudColor.White, 255 * strength), dark = Faded(new HudColor(18, 20, 26), 235 * strength);
                int run = g.Round ? 2 : Math.Max(1, steps / 8);   // (points per square; a square gate's rim is its four corners)
                for (int i = 0; i < rimMid.Count; i++)
                {
                    int j = (i + 1) % rimMid.Count;
                    bool first = g.Round ? i / run % 2 == 0 : i % 2 == 0;
                    GlowPatch(rimIn[i], rimIn[j], rimMid[j], rimMid[i], first ? light : dark);
                    GlowPatch(rimMid[i], rimMid[j], rimOut[j], rimOut[i], first ? dark : light);
                }
            }
            else GlowBand(rimIn, rimOut, core, core);
            // The halo: out from the rim and in from it, fading to nothing.
            double spread = state == 1 ? 7 : 4;
            HudColor haloNear = Faded(colour, (state == 1 ? 150 : 90) * strength), haloFar = Faded(colour, 0);
            GlowBand(rimOut, g.Rim(0, 1 + w * spread, steps), haloNear, haloFar);
            GlowBand(g.Rim(0, Math.Max(0.05, 1 - w * spread), steps), rimIn, haloFar, haloNear);
            // The frame's front and back edges, so it has some depth from the side.
            HudColor edge = Faded(colour, 150 * strength);
            GlowLoop(g.Rim(-1, 1, steps), edge, thick * 0.35);
            GlowLoop(g.Rim(1, 1, steps), edge, thick * 0.35);
            // Arrows on the rim, the way through: on every gate still to fly while flying, not only the next one
            // (which gets its own, brighter, below) and not only in edit mode.
            if (state == 0 && !always)
            {
                if (airUi)
                {
                    var wayIn = new List<Vec3D>();
                    var wayInGlow = new List<double>();
                    AddRimArrows(g, wayIn, game.TimeSeconds, wayInGlow);
                    for (int i = 0; i + 1 < wayIn.Count; i += 2)
                    {
                        HudColor ink = Faded(Hot(colour, 0.35), Math.Min(255, 150 + colour.A * 0.5) * wayInGlow[i / 2]);
                        game.DrawGlowLine(wayIn[i], ink, wayIn[i + 1], ink, thick * 0.7, airDepth);
                    }
                }
                else AddRimArrows(g, airLines, game.TimeSeconds);
            }
            // (Hidden by a hill: the next gate's rim still shows faintly through it.)
            if (state == 1 && airUi && !always)
            {
                var through = g.Rim(0, 1, steps);
                HudColor faint = Faded(colour, 75);
                for (int i = 0; i < through.Count; i++) game.DrawGlowLineHidden(through[i], faint, through[(i + 1) % through.Count], faint, thick);
            }
            if (state == 1)
            {
                bool wrongSide = !always && playerPosition.HasValue && Vec3D.Dot(playerPosition.Value - g.Center, g.Forward) > g.HalfDepth + 2;
                if (wrongSide)
                {
                    // (Flown from this side it wouldn't count: a red cross.)
                    double reach = g.Round ? 0.7071 : 1;
                    Vec3D across = g.Right * (g.HalfWidth * reach), tall = g.Up * (g.HalfHeight * reach);
                    HudColor no = HudColor.Red.WithAlpha(230);
                    if (airUi)
                    {
                        game.DrawGlowLine(g.Center + across + tall, no, g.Center - across - tall, no, thick, airDepth);
                        game.DrawGlowLine(g.Center - across + tall, no, g.Center + across - tall, no, thick, airDepth);
                    }
                    else
                    {
                        var cross = new List<Vec3D> { g.Center + across + tall, g.Center - across - tall, g.Center - across + tall, g.Center + across - tall };
                        game.DrawMarkerLines(cross, no, airDepth);
                    }
                }
                else
                {
                    // The film: nothing in the middle (so you can see through to the course), building towards the rim.
                    GlowBand(g.Rim(0, 0.45, steps), rimIn, Faded(colour, 0), Faded(colour, 46));
                    // The ring closing in on the middle, over and over: fly here. (While placing, it stays still.)
                    double beat = always ? 0.35 : game.TimeSeconds % 1.2 / 1.2;
                    double at = 0.9 - 0.55 * beat;
                    HudColor pulse = Faded(core, 200 * (1 - beat) + 15);
                    GlowBand(g.Rim(0, at - w * 0.6, steps), g.Rim(0, at + w * 0.6, steps), pulse, pulse);
                    GlowBand(g.Rim(0, at + w * 0.6, steps), g.Rim(0, at + w * 3, steps), Faded(colour, 70 * (1 - beat)), Faded(colour, 0));
                    if (!always)
                    {
                        double clock = game.TimeSeconds;
                        // Rings leading in: three of them drifting up to the gate along the way through it, fading
                        // in from further out.
                        double gap = Math.Max(g.HalfWidth, g.HalfHeight) * 1.1;
                        double drift = clock * 0.7 % 1;
                        for (int n = 1; n <= 3; n++)
                        {
                            double back = (n - drift) * gap, share = back / (3 * gap);
                            if (back < g.HalfDepth + 1) continue;
                            var lead = g.Rim(0, 1 - 0.3 * share, away > 1500 ? 16 : 32);
                            Vec3D shift = g.Forward * -back;
                            for (int i = 0; i < lead.Count; i++) lead[i] = lead[i] + shift;
                            HudColor leadInk = Faded(colour, 150 * (1 - share) * Math.Min(1, share * 6));
                            if (airUi) for (int i = 0; i < lead.Count; i++) game.DrawGlowLine(lead[i], leadInk, lead[(i + 1) % lead.Count], leadInk, thick * 0.3, airDepth);
                            else game.DrawMarkerLines(Loop(lead), leadInk, airDepth);
                        }
                        // Guide rails: a line out from each side of the gate, back along the way in and spreading a
                        // little (a funnel), bright at the gate and fading out, with dashes running in along them.
                        double railLong = Math.Max(g.HalfWidth, g.HalfHeight) * 5;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            Vec3D atGate = g.Center + g.Right * (side * g.HalfWidth);
                            Vec3D farEnd = atGate + g.Right * (side * railLong * 0.22) - g.Forward * railLong;
                            Vec3D inward = (atGate - farEnd).Normalized();
                            double head = Math.Max(g.HalfWidth, g.HalfHeight) * 0.22;   // an arrow head's size
                            var plain = airUi ? null : new List<Vec3D> { atGate, farEnd };
                            if (airUi)
                            {
                                const int railPieces = 6;
                                for (int n = 0; n < railPieces; n++)
                                {
                                    double t0 = (double)n / railPieces, t1 = (double)(n + 1) / railPieces;
                                    game.DrawGlowLine(atGate + (farEnd - atGate) * t0, Faded(colour, 150 * (1 - t0)), atGate + (farEnd - atGate) * t1, Faded(colour, 150 * (1 - t1)), thick * 0.35, airDepth);
                                }
                            }
                            // Arrow heads running in along the rail, pointing at the gate: bigger and brighter the
                            // nearer they get (their arms stand up and down from the rail, so you see them side on).
                            for (int n = 0; n < 5; n++)
                            {
                                double out0 = 1 - (n + clock * 0.8 % 1) / 5;   // 1 = the far end, 0 = at the gate
                                if (out0 <= 0.04) continue;
                                double grown = head * (1 - 0.5 * out0);
                                Vec3D tip = atGate + (farEnd - atGate) * out0;
                                Vec3D tail = tip - inward * grown;
                                Vec3D armA = tail + g.Up * (grown * 0.7), armB = tail - g.Up * (grown * 0.7);
                                if (airUi)
                                {
                                    HudColor ink = Faded(core, 255 * (1 - out0) * Math.Min(1, out0 * 8));
                                    game.DrawGlowLine(armA, ink, tip, ink, thick * 0.8, airDepth);
                                    game.DrawGlowLine(armB, ink, tip, ink, thick * 0.8, airDepth);
                                }
                                else { plain.Add(armA); plain.Add(tip); plain.Add(armB); plain.Add(tip); }
                            }
                            if (!airUi) game.DrawMarkerLines(plain, Faded(colour, 170), airDepth);
                        }
                        if (airUi)
                        {
                            // Dashes turning slowly round the outside of the rim.
                            for (int k = 0; k < 12; k++)
                            {
                                double from = k / 12.0 + clock * 0.03;
                                GlowArc(g, 1 + w * 4.5, from, from + 1 / 30.0, edge, edge, thick * 0.4);
                            }
                            // A bright spot running round the rim, with a tail fading behind it.
                            double spot = clock * 0.28;
                            HudColor white = HudColor.White.WithAlpha(255);
                            GlowArc(g, 1, spot - 0.09, spot, Faded(HudColor.White, 0), white, thick * 1.5);
                            GlowArc(g, 1, spot, spot + 0.015, white, Faded(HudColor.White, 0), thick * 1.5);
                        }
                    }
                    // Arrows on the rim, the way through.
                    if (airUi)
                    {
                        var arrows = new List<Vec3D>();
                        var arrowGlow = new List<double>();
                        AddRimArrows(g, arrows, game.TimeSeconds, arrowGlow);
                        for (int i = 0; i + 1 < arrows.Count; i += 2)
                        {
                            HudColor arrowInk = Faded(core, 255 * arrowGlow[i / 2]);
                            game.DrawGlowLine(arrows[i], arrowInk, arrows[i + 1], arrowInk, thick * 0.9, airDepth);
                        }
                    }
                    else AddRimArrows(g, airLines, game.TimeSeconds);
                }
            }
            if (!airUi)
            {
                if (airFill.Count > 0) game.DrawMarkerTriangles(airFill, airFillInk, airDepth);
                if (airLines.Count > 0) game.DrawMarkerLines(airLines, state == 1 ? core : edge, airDepth);
            }
        }

        private void DrawAirGate(TimingGate g, HudColor colour, int state, bool always)
        {
            double away = playerPosition.HasValue ? (g.Center - playerPosition.Value).Length : 0;
            if (away > 4000) return;
            // (In edit mode always the glow, drawn over everything: thin lines were too hard to see while placing.)
            if (always || settings.AirGateLook > 0 || airArrowsOnly) { DrawAirGateGlow(g, colour.WithAlpha(always ? (byte)255 : colour.A), always ? 1 : state, away, always); return; }
            bool depth = !always;
            int steps = away > 1500 ? 24 : 48;
            airLines.Clear();
            var front = g.Rim(-1, 1, steps);
            var back = g.Rim(1, 1, steps);
            AddLoop(front, airLines);
            AddLoop(back, airLines);
            // (Front and back joined: at the corners of a square one, eight times round a ring.)
            int joinEvery = g.Round ? steps / 8 : 1;
            for (int i = 0; i < front.Count; i += joinEvery) { airLines.Add(front[i]); airLines.Add(back[i]); }
            if (state == 1 || always) AddLoop(g.Rim(0, 0.94, steps), airLines);   // (a second line inside it: a thicker frame)
            if (state == 1 || always) AddRimArrows(g, airLines);
            game.DrawMarkerLines(airLines, colour, depth);
            if (state != 1) return;

            // Which side you're on: flown from the far side it wouldn't count.
            bool wrongSide = !always && playerPosition.HasValue && Vec3D.Dot(playerPosition.Value - g.Center, g.Forward) > g.HalfDepth + 2;
            var middle = g.Rim(0, 1, steps);
            if (wrongSide)
            {
                airLines.Clear();
                double reach = g.Round ? 0.7071 : 1;
                Vec3D across = g.Right * (g.HalfWidth * reach), tall = g.Up * (g.HalfHeight * reach);
                airLines.Add(g.Center + across + tall); airLines.Add(g.Center - across - tall);
                airLines.Add(g.Center - across + tall); airLines.Add(g.Center + across - tall);
                game.DrawMarkerLines(airLines, HudColor.Red.WithAlpha(220), depth);
                return;
            }
            // A faint film across the opening, so it reads as something to fly through.
            airFill.Clear(); airFillInk.Clear();
            HudColor film = colour.WithAlpha(22);
            for (int i = 0; i < middle.Count; i++)
            {
                airFill.Add(g.Center); airFill.Add(middle[i]); airFill.Add(middle[(i + 1) % middle.Count]);
                airFillInk.Add(film);
            }
            game.DrawMarkerTriangles(airFill, airFillInk, depth);
            if (always) return;
            // A ring closing in on the middle, over and over: fly here.
            double beat = game.TimeSeconds % 1.2 / 1.2;
            airLines.Clear();
            AddLoop(g.Rim(0, 0.9 - 0.55 * beat, steps), airLines);
            game.DrawMarkerLines(airLines, colour.WithAlpha((byte)(210 * (1 - beat) + 20)), depth);
        }

        /// <summary>The air gates of the course, each in its colour for where the race is.</summary>
        private void DrawAirGates()
        {
            bool racing = timer.State == TimerState.Running;
            int next = racing ? timer.NextCheckpoint : -1;
            airUi = settings.AirGateLook >= 2 && game.GlowLayerReady && !editMode;
            // (Gates on grids get the glowing frame too, with a 3D UI look: round the face of their box.)
            bool gridGlow = settings.CourseGridGlow && settings.AirGateLook >= 2 && game.GlowLayerReady && !editMode;
            airChequer = false;
            airDepth = true;
            // Just flown through: rings burst out of it and fade.
            if (airFlashGate != null && !editMode)
            {
                double burst = (game.TimeSeconds - airFlashAt) / 0.6;
                if (burst < 0 || burst >= 1) airFlashGate = null;
                else if (settings.AirGateLook > 0)
                {
                    // (Glow: a bright ring racing outwards with a fading tail behind it, and the opening lit up.)
                    airLines.Clear(); airFill.Clear(); airFillInk.Clear();
                    HudColor lit = Hot(HudColor.Green, 0.5);
                    double grown = 1 + 0.6 * burst, left = 1 - burst;
                    GlowBand(airFlashGate.Rim(0, grown - 0.03), airFlashGate.Rim(0, grown + 0.03), Faded(lit, 255 * left), Faded(lit, 255 * left));
                    GlowBand(airFlashGate.Rim(0, 1), airFlashGate.Rim(0, grown - 0.03), Faded(HudColor.Green, 0), Faded(HudColor.Green, 140 * left));
                    GlowBand(airFlashGate.Rim(0, grown + 0.03), airFlashGate.Rim(0, grown + 0.2), Faded(HudColor.Green, 110 * left), Faded(HudColor.Green, 0));
                    GlowBand(airFlashGate.Rim(0, 0.02), airFlashGate.Rim(0, 1), Faded(HudColor.Green, 20 * left), Faded(HudColor.Green, 90 * left));
                    if (!airUi && airFill.Count > 0) game.DrawMarkerTriangles(airFill, airFillInk, true);
                }
                else
                {
                    airLines.Clear();
                    AddLoop(airFlashGate.Rim(0, 1 + 0.5 * burst), airLines);
                    AddLoop(airFlashGate.Rim(0, 1 + 0.25 * burst), airLines);
                    AddLoop(airFlashGate.Rim(0, 1), airLines);
                    game.DrawMarkerLines(airLines, HudColor.Green.WithAlpha((byte)(255 * (1 - burst))), true);
                    airFill.Clear(); airFillInk.Clear();
                    var burstRim = airFlashGate.Rim(0, 1);
                    HudColor burstFilm = HudColor.Green.WithAlpha((byte)(70 * (1 - burst)));
                    for (int i = 0; i < burstRim.Count; i++)
                    {
                        airFill.Add(airFlashGate.Center); airFill.Add(burstRim[i]); airFill.Add(burstRim[(i + 1) % burstRim.Count]);
                        airFillInk.Add(burstFilm);
                    }
                    game.DrawMarkerTriangles(airFill, airFillInk, true);
                }
            }
            for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                TimingGate cp = timer.Checkpoints[i];
                if (!cp.Air && editMode) continue;
                airArrowsOnly = false;
                if (!cp.Air)
                {
                    // (On a grid: turned to the way the course goes through it; the glowing frame only if the
                    // course asks for it, else just the arrows.)
                    Vec3D before = i > 0 ? timer.Checkpoints[i - 1].Center : timer.Gate != null ? timer.Gate.Center : cp.Center;
                    cp = Through(cp, cp.Center - before);
                    airArrowsOnly = !gridGlow;
                }
                if (editMode) { DrawAirGate(cp, HudColor.Orange, 0, true); continue; }
                bool hit = timer.IsCheckpointHit(i);
                if (racing && hit) DrawAirGate(cp, HudColor.Green.WithAlpha(90), 2, false);
                else if (racing && i == next) DrawAirGate(cp, HudColor.Orange.WithAlpha(255), 1, false);
                else DrawAirGate(cp, HudColor.Orange.WithAlpha((byte)(!racing ? 215 : i == next + 1 ? 180 : 120)), 0, false);
            }
            bool gateIsTarget = next == -1;
            airChequer = !editMode;   // (the start and the finish: a chequered rim)
            airArrowsOnly = false;
            int cps = timer.Checkpoints.Count;
            if (timer.Gate != null && (timer.Gate.Air || !editMode))
            {
                bool target = timer.FinishGate != null ? !racing : gateIsTarget;
                TimingGate shown = timer.Gate;
                if (!shown.Air)
                {
                    // (The way off the start: towards the first checkpoint, or the finish.)
                    Vec3D after = cps > 0 ? timer.Checkpoints[0].Center : timer.FinishGate != null ? timer.FinishGate.Center : shown.Center + shown.Forward;
                    shown = Through(shown, after - shown.Center);
                    airArrowsOnly = !gridGlow;
                }
                if (editMode) DrawAirGate(shown, HudColor.Cyan, 0, true);
                else DrawAirGate(shown, HudColor.Cyan.WithAlpha((byte)(target ? 255 : racing ? 120 : 215)), target ? 1 : 0, false);
            }
            airArrowsOnly = false;
            if (timer.FinishGate != null && (timer.FinishGate.Air || !editMode))
            {
                bool target = racing && gateIsTarget;
                TimingGate shown = timer.FinishGate;
                if (!shown.Air)
                {
                    Vec3D before = cps > 0 ? timer.Checkpoints[cps - 1].Center : timer.Gate != null ? timer.Gate.Center : shown.Center - shown.Forward;
                    shown = Through(shown, shown.Center - before);
                    airArrowsOnly = !gridGlow;
                }
                if (editMode) DrawAirGate(shown, HudColor.Purple, 0, true);
                else DrawAirGate(shown, HudColor.Purple.WithAlpha((byte)(target ? 255 : racing ? 120 : 215)), target ? 1 : 0, false);
            }
            airArrowsOnly = false;
            airChequer = false;
        }

        private void DrawBox(TimingGate gate, HudColor colour)
        {
            if (gate.Air && (editMode || settings.AirGateDraw)) return;   // (drawn as an air gate instead)
            var c = gate.GetCorners();
            for (int i = 0; i < TimingGate.Edges.GetLength(0); i++)
            {
                // In edit mode always shown (not only with the game's debug draw on).
                if (editMode) game.DrawMarkerLine(c[TimingGate.Edges[i, 0]], c[TimingGate.Edges[i, 1]], colour);
                else game.DrawWorldLine(c[TimingGate.Edges[i, 0]], c[TimingGate.Edges[i, 1]], colour);
            }
        }

        private void Draw()
        {
            double now = game.TimeSeconds;

            // ---- HUD (timer, laps, messages)
            HudModel.Message centre = null;
            if (message != null && now < messageUntil)
                centre = new HudModel.Message { Text = message, Colour = messageColour };
            else if (timer.Gate == null && !editMode && now - worldLoadedAt < 30)
                // Shown for 30 s after loading a world: confirms the plugin is running.
                centre = new HudModel.Message { Text = $"Race Display: {settings.GetBinding(GateKey.EditMode)} for course edit mode to set up a course.  G > Racing Display (or {settings.GetBinding(GateKey.LapCount)}) for the menu" };

            HudModel.Message race = raceMessage != null && now < raceMessageUntil
                ? new HudModel.Message { Text = raceMessage, Colour = raceMessageColour }
                : null;

            var record = CourseRecord(false);
            var view = new RaceView
            {
                Record = record,
                // Live gap to the best lap when we have its path; otherwise the gap at the last checkpoint.
                Delta = settings.TimerGap ? liveGapText ?? gapText : null,
                DeltaColour = liveGapText != null ? liveGapColour : gapColour,
                LapSpeeds = lapSpeeds,
                LapStats = lapStats,
                RaceStats = resultsClosed && timer.State == TimerState.Finished ? null : raceStats,
                SectorsBefore = recordSectorsAtStart,
                SplitsBefore = recordBestSplitsAtStart,
                LapBefore = recordLapAtStart,
                PossibleLap = PossibleLap(record, timer.Checkpoints.Count),
                Results = !resultsClosed && (settings.ResultsSeconds <= 0 || now < resultsUntil) ? ShownResults() : null, // 0 = until the next race
                Flash = flashColour.HasValue && now < flashUntil ? flashColour : null,
                ShowGo = now < goUntil,
            };
            if (countdownStart.HasValue)
            {
                int n = 3 - (int)Math.Floor(now - countdownStart.Value);
                view.Countdown = Math.Max(1, n).ToString();
                view.CountdownColour = HudColor.Yellow;
            }


            var model = hud.Build(timer, now, race, centre, view);
            model.Opacity = Math.Clamp(settings.HudOpacity, 20, 100) / 100.0;
            // (For the toolbar bar's right corner: gravity here, in g.)
            if (settings.GravityPanel)
            {
                double gravityInG = game.GravityStrength / 9.81;
                model.GravityG = gravityInG;
                // Which way it pulls, in the ship's own frame (for the panel's arrow).
                if (gravityInG > 0.005 && game.TryGetGravity(out Vec3D pullDown) && game.TryGetControlledPose(out _, out Vec3D shipAhead, out Vec3D shipUpward))
                {
                    Vec3D shipRightward = Vec3D.Cross(shipAhead, shipUpward).Normalized();
                    model.GravityX = Vec3D.Dot(pullDown, shipRightward);
                    model.GravityY = Vec3D.Dot(pullDown, shipUpward.Normalized());
                    // Natural and artificial gravity apart, for an arrow each.
                    if (game.TryGetGravityParts(out Vec3D pullNatural, out Vec3D pullArtificial)
                        && (pullNatural - pullArtificial).Length > 0.3)   // (the same pull twice = one kind only)
                    {
                        model.GravityParts = true;
                        model.GravityNatG = pullNatural.Length / 9.81;
                        model.GravityArtG = pullArtificial.Length / 9.81;
                        Vec3D towardNatural = pullNatural.Normalized(), towardArtificial = pullArtificial.Normalized();
                        model.GravityNatX = Vec3D.Dot(towardNatural, shipRightward);
                        model.GravityNatY = Vec3D.Dot(towardNatural, shipUpward.Normalized());
                        model.GravityArtX = Vec3D.Dot(towardArtificial, shipRightward);
                        model.GravityArtY = Vec3D.Dot(towardArtificial, shipUpward.Normalized());
                        // The overall pull is the two together (the ship's own figure can leave the artificial part out).
                        Vec3D pullBoth = new Vec3D(pullNatural.X + pullArtificial.X, pullNatural.Y + pullArtificial.Y, pullNatural.Z + pullArtificial.Z);
                        model.GravityG = pullBoth.Length / 9.81;
                        Vec3D towardBoth = pullBoth.Normalized();
                        model.GravityX = Vec3D.Dot(towardBoth, shipRightward);
                        model.GravityY = Vec3D.Dot(towardBoth, shipUpward.Normalized());
                    }
                }
            }
            if (settings.EdgeGlow && edgeGlowColour.HasValue && game.TimeSeconds < edgeGlowUntil && !replaying && !editMode) model.EdgeGlow = edgeGlowColour;
            // Waiting at the start: the course's name and the time to beat in the timer box, and a bar that fills
            // as you close on the gate.
            if (model.Timer != null && timer.State == TimerState.Armed && timer.Gate != null && !replaying && !editMode)
            {
                var readyParts = new List<string>();
                string readyCourse = CurrentCourseName();
                if (!string.IsNullOrWhiteSpace(readyCourse)) readyParts.Add(readyCourse.Trim().ToUpperInvariant());
                if (model.Timer.BestRace != null) readyParts.Add("TO BEAT " + model.Timer.BestRace);
                if (readyParts.Count > 0) model.Timer.Footer = string.Join("   ", readyParts);
                if (game.TryGetShipCentre(out Vec3D readyAt))
                {
                    double readyGap = (readyAt - timer.Gate.Center).Length;
                    if (readyGap < 300)
                    {
                        model.Timer.StartBar = 1 - readyGap / 300;
                        model.Timer.Sub = $"Fly through the gate to start  \u00B7  {readyGap:0} m";
                    }
                }
            }
            if (replaying && model.Timer != null)
            {
                double rt = Math.Min(ReplayTime, replayLength);
                model.Timer.Label = "REPLAY - " + replayLabel;
                model.Timer.Big = LapTimer.Format(rt);
                model.Timer.BigColour = HudColor.Purple;
                model.Timer.Footer = !replayCameraReady ? "Switching camera..."
                    : $"Esc / {settings.GetBinding(GateKey.Replay)}: stop   {settings.GetBinding(GateKey.ReplayCamera)}: {(settings.ReplayOrbit ? "chase camera   Mouse: orbit   W/S: zoom" : "orbit camera")}";
                model.Timer.Delta = null;
                model.Timer.Dots = null;
                model.Timer.Hint = null;
                model.Splits = null;
                model.Results = null;
            }
            if (editMode) DrawEditMode(model);
            // Checkpoint labels and the off-screen arrow as HUD elements (needs the game UI and its camera).
            bool hudLabels = settings.ShowMarkers && settings.ShowCheckpointArrow && settings.UseGameUi
                             && game.HudVisible && AddScreenMarkers(model);
            // Racing line and ghost (from the best lap's recorded path); the ghost's HUD label goes in the model.
            // The minimap, and the big Race Map page in the terminal (G > Map > Race Map) while it's open.
            bool raceMap = game.RaceMapShown;
            if (settings.ShowMinimap || raceMap)
            {
                var mmp = BuildMinimap();
                if (settings.ShowMinimap) model.Minimap = mmp;
                if (mmp != null) mmp.HeadingUp = settings.MinimapHeadingUp && !editMode;
                if (mmp != null) mmp.LabelsAhead = editMode || replaying ? 0 : 3;
                if (editMode && mmp != null) mmp.NextIndex = editAimedPoint;
                if (raceMap) game.ShowRaceMap(mmp);
            }
            PluginLog.Step("racing line / ghost");
            if (secondGhostKey != null && secondGhostCourse != CourseKey()) ClearSecondGhost();   // a different course now
            if (replaying) DrawRacingLineAndGhost(replayTrace, replayShip, replayTitle, model, hudLabels);
            else
            {
                string ghostLabel = raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count) ? raceGhost.Label
                    : racePossible ? "POSSIBLE LAP"
                    : raceBestRace && BestRaceLapTrace() != null ? $"RECORD RACE LAP {Math.Min(timer.Laps.Count + 1, timer.LapTarget)}" : null;
                DrawRacingLineAndGhost(GhostTrace, GhostShip, ghostLabel, model, hudLabels);
            }
            if (!replaying && !editMode) AddFlightAids(model);
            if (settings.MinimalHud && !replaying && !editMode && model.Results == null && timer.State == TimerState.Running)
            {
                // Minimal HUD: the timer (with the gap), the speedometer and the next gate only.
                model.Laps = null; model.Splits = null; model.Minimap = null;
                model.Roll = null; model.Pitch = null; model.KeysCorner = 0;
                if (model.Timer != null) { model.Timer.BestLap = null; model.Timer.BestLapNote = null; model.Timer.BestRace = null; model.Timer.BestRaceNote = null; }
                model.Markers.RemoveAll(quietMarker => !quietMarker.Emphasis);
            }
            if (!replaying) DrawCinePreview(model);
            PluginLog.Step("hud");
            model.CineFade = cinePlay != null && orbitCam ? cineFade : 0;
            model.CineBars = cinePlay != null && orbitCam ? cineBars : 0;
            AddFilmOverlay(model);
            resultsOnScreen = model.Results != null;
            bool shownByGameUi = settings.UseGameUi && game.ShowHud(model.IsEmpty ? null : model);
            if (!shownByGameUi)
            {
                if (!settings.UseGameUi) game.ShowHud(null);
                hud.DrawDebug(model);
            }

            // ---- Markers (always visible, even with the HUD hidden)
            PluginLog.Step("markers");
            if (settings.ShowMarkers)
            {
                // During a race the NEXT checkpoint (lowest number not yet flown through) is big, bright and shows
                // its distance; the others are dimmed. Before a race, or once every checkpoint is done, the
                // start/finish gate is where you're heading, so it shows the distance.
                bool racing = timer.State == TimerState.Running;
                int next = racing ? timer.NextCheckpoint : -1;

                for (int i = 0; i < timer.Checkpoints.Count; i++)
                {
                    if (!MarkerFor(i, next, out string name)) continue;   // a ring used twice gets one marker
                    if (!CheckpointAhead(i, next, racing)) continue;      // Only mark the next checkpoints
                    bool hit = timer.IsCheckpointHit(i);
                    bool isNext = i == next;
                    HudColor colour = hit ? HudColor.Green.WithAlpha(110)
                                    : isNext ? HudColor.Orange.WithAlpha(255)
                                    : racing ? HudColor.Orange.WithAlpha(110) : HudColor.Orange;
                    DrawMarker(timer.Checkpoints[i], name, colour, isNext, isNext ? 1.6 : 1.0, !hudLabels);
                }
                if (timer.Gate != null)
                {
                    bool gateIsTarget = next == -1;
                    if (timer.FinishGate != null)
                    {
                        if (GateAhead(false, racing)) DrawMarker(timer.Gate, "START", HudColor.Cyan, !racing, 1.0, !hudLabels);
                        if (GateAhead(true, racing)) DrawMarker(timer.FinishGate, "FINISH", HudColor.Purple, racing && gateIsTarget, gateIsTarget && racing ? 1.4 : 1.0, !hudLabels);
                    }
                    else if (GateAhead(true, racing) || GateAhead(false, racing)) DrawMarker(timer.Gate, "START / FINISH", HudColor.Cyan, gateIsTarget, gateIsTarget && racing ? 1.4 : 1.0, !hudLabels);
                }
            }

            // ---- Air gates: nothing there to see, so they're drawn (always, like the markers)
            if (settings.AirGateDraw || editMode) DrawAirGates();

            // ---- World outlines (only visible with the game's debug draw on)
            if (settings.ShowGateOutline || editMode)
            {
                if (timer.Gate != null)
                    DrawBox(timer.Gate, timer.State == TimerState.Running ? HudColor.Green : HudColor.Cyan);
                if (timer.FinishGate != null)
                    DrawBox(timer.FinishGate, HudColor.Purple);
                // Checkpoints: orange until flown through this lap, then green.
                for (int i = 0; i < timer.Checkpoints.Count; i++)
                    DrawBox(timer.Checkpoints[i], timer.IsCheckpointHit(i) ? HudColor.Green : HudColor.Orange);
            }
        }
    }
}
