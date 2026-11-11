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
            if (editMode && !controlsOpen && keys.WasPressed(GateKey.Undo)) { UndoRedo(true, position); courseBefore = null; }
            else if (editMode && !controlsOpen && keys.WasPressed(GateKey.Redo)) { UndoRedo(false, position); courseBefore = null; }
            else if (editMode && !controlsOpen && keys.WasPressed(GateKey.ReverseCourse)) ReverseCourse(position);
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
            // A second stop request just after a teleport, in case the first one ran before the ship moved.
            if (stopShipAt.HasValue && game.TimeSeconds >= stopShipAt.Value) { stopShipAt = null; game.StopShip(); }
            if (launchAt.HasValue && game.TimeSeconds >= launchAt.Value) { launchAt = null; game.SetShipVelocity(launchVelocity); }
            if (gyroRestoreAt.HasValue && game.TimeSeconds >= gyroRestoreAt.Value) { gyroRestoreAt = null; game.TargetGyroRestore(); }
            timer.Update(detectAt, game.TimeSeconds);
            TrackLap(position, velocity);
            UpdateLiveGap(position);

            // A crash during the immunity time after the last crash penalty doesn't count.
            // ...nor, with that setting on, before you're back above the crash immunity speed.
            if (crashSlowLock && velocity.HasValue && velocity.Value.Length > settings.CrashRearmSpeed) crashSlowLock = false;
            // Crash: hull damage (the game's collision damage to your ship), a sudden speed drop, or either.
            bool speedCrash = crash.Update(velocity, game.TimeSeconds);
            RememberSpeed(velocity);
            // Hull damage only counts when you hit something going at least the crash minimum speed (as the speed drop does).
            bool damageCrash = DamageCrash(position, out double hitDamage) && RecentTopSpeed() >= settings.CrashMinSpeed;
            int mode = settings.CrashDetection;
            bool crashed = mode == 1 ? speedCrash
                : mode == 2 ? speedCrash || damageCrash
                : damageCrash || (speedCrash && !Patches.ImpactDamageSeen);   // speed drop until damage is seen to work here
            if (crashed && game.TimeSeconds - lastCrashPenaltyAt >= Math.Max(settings.CrashImmunitySeconds, 1.0)
                && !(settings.CrashImmuneUntilSpeed && crashSlowLock) && timer.AddCrash())
            {
                lastCrashPenaltyAt = game.TimeSeconds;
                if (position.HasValue) lapCrashSpots.Add((position.Value, timer.CurrentLapSeconds(game.TimeSeconds)));
                crashSlowLock = true;
                double impactSpeed = speedCrash ? crash.LastImpactSpeed : RecentTopSpeed();
                RaceNotify($"CRASH at {GameOptions.Speed(impactSpeed)}!  +{settings.CrashPenaltySeconds:0.#}s penalty", HudColor.Red);
                if (damageCrash) PluginLog.Info($"Crash: hull damage {hitDamage:0.##} at {GameOptions.Speed(impactSpeed)}");
                FlashTimer(HudColor.Red);
                Sound(settings.SoundMissed);
                Sound(settings.SoundMissed, 120);
            }

            UpdateCountdown(position, velocity, game.TimeSeconds);
            PlayQueuedSounds();

            PluginLog.Step("draw");
            Draw();
        }

        // ---- Crash by hull damage: collision damage the game dealt to a ship near you this frame.
        private readonly Queue<(double T, double Speed)> recentSpeeds = new Queue<(double, double)>();

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
                RaceNotify($"Course edit mode: left click a grid to set it, right click to remove. Hold {settings.GetBinding(GateKey.EditWheel)} for the tool wheel, {settings.GetBinding(GateKey.PlacementMode)} changes how it's placed (grid / blocks / air). {settings.GetBinding(GateKey.Undo)} / {settings.GetBinding(GateKey.Redo)}: undo / redo.", HudColor.Cyan);
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
                    game.OpenEditWheel(WheelLabels, editTool, settings.GetBinding(GateKey.EditWheel).Key & 0xFF, tint);
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
            // Placement mode (on a grid / pick blocks / in the air) and the racing line have their own keys.
            if (canAct && !game.EditWheelOpen && keys.WasPressed(GateKey.PlacementMode))
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
            if (canAct && !game.EditWheelOpen && keys.WasPressed(GateKey.MoveCheckpoint) && hasRay)
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
                    where = $"in the air ({settings.AirCheckpointSize:0} m, {settings.AirCheckpointDistance:0} m ahead)";
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

        /// <summary>The air checkpoint the Air checkpoint tool would place: ahead along the look direction.</summary>
        private TimingGate AirGate(Vec3D eye, Vec3D dir, Vec3D up)
        {
            Vec3D f = dir.Normalized();
            Vec3D u = up - f * Vec3D.Dot(up, f);
            u = u.Length > 0.05 ? u.Normalized() : (timer.Gate?.Up ?? new Vec3D(0, 1, 0));
            double size = Math.Clamp(settings.AirCheckpointSize, 5, 300);
            return new TimingGate(eye + f * Math.Clamp(settings.AirCheckpointDistance, 5, 500), f, u, size, size, Math.Max(4, size * 0.15));
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
        private void DrawCourseRoute()
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
                game.DrawMarkerText(mid + lift * ts * 1.4, Metres(len), colour, 0.45);
            }
        }

        /// <summary>Edit mode: the course panel (type, length, each leg, and things worth fixing).</summary>
        private List<(string Label, string Value, HudColor Colour)> CourseInfo()
        {
            var rows = new List<(string, string, HudColor)>();
            var route = CourseRoute();
            bool p2p = timer.FinishGate != null;
            rows.Add(("Type", timer.Gate == null ? "no start gate yet" : p2p ? "Point to point" : "Circuit", HudColor.White));
            rows.Add(("Checkpoints", timer.Checkpoints.Count.ToString(), HudColor.White));
            double total = 0;
            for (int i = 0; i + 1 < route.Count; i++) total += (route[i + 1].Gate.Center - route[i].Gate.Center).Length;
            rows.Add(("Length (straight)", route.Count >= 2 ? Metres(total) : "--", HudColor.White));
            if (!p2p && timer.Gate != null) rows.Add(("Race", settings.LapTarget > 0 ? $"{settings.LapTarget} laps, {Metres(total * settings.LapTarget)}" : "unlimited laps", HudColor.White));
            var rec = CourseRecord(false);
            if (rec?.BestLap != null) rows.Add(("Best lap", LapTimer.Format(rec.BestLap.Value), HudColor.Yellow));

            // Each leg (long courses: the first ones, then a count).
            const int maxLegs = 14;
            for (int i = 0; i + 1 < route.Count && i < maxLegs; i++)
                rows.Add(($"{route[i].Name} > {route[i + 1].Name}", Metres((route[i + 1].Gate.Center - route[i].Gate.Center).Length), HudColor.Grey));
            if (route.Count - 1 > maxLegs) rows.Add(("...", $"{route.Count - 1 - maxLegs} more legs", HudColor.Grey));

            // Warnings.
            var warn = HudColor.Orange;
            if (timer.Gate == null) rows.Add(("Fix", "set a start / finish gate", warn));
            else if (timer.Checkpoints.Count == 0) rows.Add(("Tip", "add checkpoints so the route can't be cut short", warn));
            for (int i = 0; i + 1 < route.Count; i++)
            {
                double len = (route[i + 1].Gate.Center - route[i].Gate.Center).Length;
                if (len < 25 && !(i + 1 == route.Count - 1 && route.Count == 2))
                    rows.Add(("Close together", $"{route[i].Name} and {route[i + 1].Name} ({len:0} m)", warn));
            }
            // Ring-shaped gates (thin) turned side-on to the way you arrive are hard or impossible to fly through.
            for (int i = 1; i < route.Count; i++)
            {
                var g = route[i].Gate;
                if (g.Depth > Math.Min(g.Width, g.Height) * 0.6) continue;
                Vec3D arrive = (g.Center - route[i - 1].Gate.Center).Normalized();
                if (Math.Abs(Vec3D.Dot(arrive, g.Forward)) < 0.35)
                    rows.Add(("Side-on", $"{route[i].Name} faces across the route", warn));
            }
            return rows;
        }

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
                model.Timer.Label = "COURSE EDIT MODE  ·  " + PlacementName;
                model.Timer.Big = ToolName;
                model.Timer.BigColour = ToolColour;
                model.Timer.Footer = settings.EditBlockPick && !settings.EditAirPlace
                    ? (blockFirst == null
                        ? $"Left click: first corner block   Right click: remove   Hold {settings.GetBinding(GateKey.EditWheel)}: tools   {settings.GetBinding(GateKey.PlacementMode)}: placement"
                        : $"Left click: opposite corner block   Right click: cancel   Hold {settings.GetBinding(GateKey.EditWheel)}: tools   {settings.GetBinding(GateKey.PlacementMode)}: placement")
                    : settings.EditAirPlace
                    ? $"Left click: place   Right click: remove   {settings.GetBinding(GateKey.Grow)} / {settings.GetBinding(GateKey.Shrink)}: size ({settings.AirCheckpointSize:0} m)   Hold {settings.GetBinding(GateKey.EditWheel)}: tools   {settings.GetBinding(GateKey.PlacementMode)}: placement"
                    : $"Left click: set   Right click: remove   Hold {settings.GetBinding(GateKey.EditWheel)}: tools   {settings.GetBinding(GateKey.PlacementMode)}: placement   {settings.GetBinding(GateKey.EditMode)}: done";
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
            model.RaceStats = CourseInfo();
            model.RaceStatsTitle = "COURSE";
            model.RaceStatsOneColumn = true;
            DrawCourseRoute();
            if (blockPreview != null && !game.EditWheelOpen)
            {
                var c = blockPreview.GetCorners();
                for (int i = 0; i < TimingGate.Edges.GetLength(0); i++)
                    game.DrawMarkerLine(c[TimingGate.Edges[i, 0]], c[TimingGate.Edges[i, 1]], blockFirst != null ? HudColor.Yellow : ToolColour);
            }
            if (airPreview != null && !game.EditWheelOpen)
            {
                var c = airPreview.GetCorners();
                for (int i = 0; i < TimingGate.Edges.GetLength(0); i++)
                    game.DrawMarkerLine(c[TimingGate.Edges[i, 0]], c[TimingGate.Edges[i, 1]], ToolColour);
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
            : (racePossible ? PossibleTrace() : null) ?? CourseRecord(false)?.BestLapTrace;
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
                        else { raceGhost = g; racePossible = false; recordsVersion++; controlsNote = $"Racing against {g.Label}: the ghost, racing line and live gap use it"; }
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
                        raceGhost = g; racePossible = false; recordsVersion++;
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
                        raceGhost = g; racePossible = false; recordsVersion++;
                        controlsNote = $"Racing against {c.Text}'s best lap: the ghost, racing line and live gap use it";
                        break;
                    }
                    case MenuAction.RacePossible:
                        raceGhost = null; racePossible = true; recordsVersion++;
                        controlsNote = PossibleTrace() != null
                            ? "Racing against your possible lap: every best sector joined up"
                            : "Racing against your possible lap once every sector has a recorded path (until then, your best lap)";
                        break;
                    case MenuAction.RaceCourseBest:
                        raceGhost = null; racePossible = false; recordsVersion++;
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
                    case MenuAction.SpotlightStart: StartSpotlight(c.Text); break;
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
                        try { pasted = string.IsNullOrWhiteSpace(c.Text) ? null : System.Text.Json.JsonSerializer.Deserialize<GateSettings.RpiClass>(c.Text); }
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
                    + (rec.BestLap.HasValue ? $"   -   best lap {LapTimer.Format(rec.BestLap.Value)}" : "")
                    + (possible.HasValue ? $"   -   possible lap {LapTimer.Format(possible.Value)}" : "")
                    + (possible.HasValue && timer.LapTarget > 0 ? $"   -   possible {timer.LapTarget}-lap race {LapTimer.Format(possible.Value * timer.LapTarget)}" : "");
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
            p.SpotlightOpen = analyserMode && spotlight != null ? spotlightPath : null;
            p.CanExportRace = lastRace != null;
            p.LastRaceLine = lastRace == null ? null : $"{lastRace.Ship}, {lastRace.Course}, {LapTimer.Format(lastRace.Total)}";
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
                            var read = ReadSpotlight(spotFile);
                            if (read == null) continue;
                            spotFound.Add(($"{read.Ship}  -  {read.Course}", $"{LapTimer.Format(read.Total)} over {read.Laps.Count} lap{(read.Laps.Count == 1 ? "" : "s")}, {read.Date}{(string.IsNullOrWhiteSpace(read.Pilot) ? "" : ", by " + read.Pilot)}", spotFile));
                        }
                }
                catch (Exception spotListError) { PluginLog.Info("Race spotlight folder: " + spotListError.Message); }
                if (spotFilesCache != null && !spotFilesCache.SequenceEqual(spotFound)) recordsVersion++;
                spotFilesCache = spotFound;
            }
            p.Spotlights.AddRange(spotFilesCache);
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
                p.RoutePoints.Add(i == routeNow.Count - 1 && i > 0 && timer.FinishGate == null ? "Start/Finish (lap end)" : routeNow[i].Name);
            var spot = settings.CourseStartSpot;
            p.StartSpot = spot?.Center == null || timer.Gate == null ? "Just behind the start gate"
                : $"Your spot, {Distance((new Vec3D(spot.Center[0], spot.Center[1], spot.Center[2]) - timer.Gate.Center).Length)} from the start gate";
            foreach (var sec in settings.CourseSectors)
            {
                string Pt(int k) => k >= 0 && k < p.RoutePoints.Count ? p.RoutePoints[k] : $"#{k}";
                p.Sectors.Add((sec.Name, $"{Pt(sec.From)} to {Pt(sec.To)}"));
            }
            foreach (var sc in settings.SavedCourses.Where(x => x.World == game.WorldId).OrderBy(x => x.Name))
                p.SavedCourses.Add((sc.Name, $"{sc.Checkpoints.Count} checkpoint{(sc.Checkpoints.Count == 1 ? "" : "s")}", sc.Name == p.CourseName));

            p.SettingsVersion = settingsVersion;
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
        private Dictionary<string, Dictionary<string, double>> LoadPresets()
        {
            if (presetsCache == null) presetsCache = ReadPresets();
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
                var f = System.Text.Json.JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path));
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
            TimingGate Flip(TimingGate g) => new TimingGate(g.Center, g.Forward * -1, g.Up, g.Width, g.Height, g.Depth);
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
            if ((keys.WasPressed(GateKey.Grow) || keys.WasPressed(GateKey.Shrink)) && !editMode)
            {
                Notify($"Gate size is changed in course edit mode ({settings.GetBinding(GateKey.EditMode)})", HudColor.Yellow);
                return;
            }
            if (editMode && settings.EditAirPlace && (keys.WasPressed(GateKey.Grow) || keys.WasPressed(GateKey.Shrink)))
            {
                double f = keys.WasPressed(GateKey.Grow) ? GrowFactor : 1 / GrowFactor;
                settings.AirCheckpointSize = Math.Clamp(Math.Round(settings.AirCheckpointSize * f / 5) * 5, 5, 300);
                settings.Save(settingsPath);
                Notify($"Air gate size: {settings.AirCheckpointSize:0} m", HudColor.Cyan);
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
                timer.Reset();
                results = shipResults = null;
                gapText = null;
                countdownStart = null;
                countdownAllowed = true;
                if (!settings.ResetTeleports) Notify("Race reset");
                else if (TeleportToStart()) Notify("Race reset: back to the start");
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
                        Sound(settings.SoundMissed);
                        Sound(settings.SoundMissed, 150);
                        break;
                    }

                    bool purple = split.HasValue && rec != null && i < rec.BestSplits.Count && rec.BestSplits[i].HasValue && split.Value < rec.BestSplits[i].Value;
                    RaceNotify($"Checkpoint {timer.CheckpointsHitThisLap} / {timer.Checkpoints.Count}{gap}", purple ? HudColor.Purple : HudColor.Green);
                    Sound(settings.SoundCheckpoint);
                    break;
                }

                case LapEventKind.LapCompleted:
                {
                    var lap = timer.Laps[timer.Laps.Count - 1];
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
                    if (courseBest)
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
                model.SpeedoValue = GameOptions.ToUnit(speed);
                model.SpeedoMax = settings.SpeedometerMax > 0 ? settings.SpeedometerMax : speedoMax;
            }
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
                    if (game.TryProjectToScreen(c, out double sx, out double sy, out bool behind) && !behind && sx > 0.01 && sx < 0.99 && sy > 0.01 && sy < 0.99)
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
                Notify(spotlight != null
                    ? $"Race spotlight: {spotlight.Ship}, {spotlight.Course}. Look at the ship and left click. Esc: exit"
                    : "Ship analyser: look at a ship. Left click keeps its stats, right click another to compare. Esc: exit", HudColor.Cyan);
            }
            else
            {
                timer.Resync(position);
                Notify(spotlight != null ? "Race spotlight off" : "Ship analyser off", HudColor.Cyan);
                spotlight = null; spotlightPath = null; spotGrid = null; spotStats = null;
            }
        }

        /// <summary>Ship analyser mode's whole HUD: the analyser panel (and messages); no timer, markers, racing line or map.</summary>
        private void DrawAnalyserMode()
        {
            double now = game.TimeSeconds;
            HudModel.Message centre = message != null && now < messageUntil
                ? new HudModel.Message { Text = message, Colour = messageColour } : null;
            var model = hud.Build(blankTimer, now, null, centre, null);
            if (spotlight != null) UpdateSpotlight(model); else UpdateAnalyser(model);
            PluginLog.Step("hud");
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
            AddShipRows(p, a, b);
            model.Analyser = p;

            DrawAnalysis(a, new HudColor(255, 210, 60, 255));
            if (b != null) DrawAnalysis(b, new HudColor(80, 200, 255, 255));
        }

        /// <summary>A grid's stats as rows of the panel (with a second grid beside it when comparing), then its RPI.</summary>
        private void AddShipRows(HudModel.AnalyserPanel p, ShipStats a, ShipStats b)
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
        }

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
        private string SpotlightFolder => Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "Spotlights");
        private const string SpotlightExtension = ".spotlight.json";
        private const double SpotPageSeconds = 8;

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
                foreach (var lap in resultLaps)
                    file.Laps.Add(new SpotlightLap { Time = lap.Time, Flown = lap.Flown, Penalty = lap.Penalty, Missed = lap.MissedCheckpoints, Crashes = lap.Crashes });
                if (raceStats != null)
                    foreach (var row in raceStats) file.Stats.Add(new SpotlightRow { Label = row.Label, Value = row.Value });
                lastRace = file;
                recordsVersion++;
            }
            catch (Exception e) { PluginLog.Info("Race spotlight: couldn't keep the race: " + e.Message); }
        }

        private static SpotlightFile ReadSpotlight(string path)
        {
            try
            {
                var file = System.Text.Json.JsonSerializer.Deserialize<SpotlightFile>(File.ReadAllText(path));
                if (file == null) return null;
                file.Laps ??= new List<SpotlightLap>();
                file.Stats ??= new List<SpotlightRow>();
                return file;
            }
            catch (Exception e) { PluginLog.Info("Race spotlight file couldn't be read (" + path + "): " + e.Message); return null; }
        }

        private void StartSpotlight(string path)
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
        }

        /// <summary>Spotlight mode's panel: the saved race's results, its stats, and the picked ship's stats, one after another.</summary>
        private void UpdateSpotlight(HudModel model)
        {
            double now = game.TimeSeconds;
            bool focus = game.HasFocus && !controlsOpen && !game.IsPaused;
            bool left = focus && game.IsKeyDown(0x01);
            if (left && !spotLeftDown && game.TryGetLookRay(out Vec3D eye, out Vec3D dir, out _, out _))
            {
                // Left click: the grid you're looking at (your own ship when you look at nothing from its seat).
                object g = game.LookedAtGrid(eye, dir, 3000, game.OwnGrid == null) ?? game.OwnGrid;
                if (g != null)
                {
                    spotGrid = g;
                    spotStats = game.AnalyseGrid(g);
                    spotStatsAt = now;
                    spotPage = 0; spotPageAt = now;
                    Notify($"Spotlight on {spotStats?.Name ?? "this grid"}", HudColor.Cyan);
                }
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

            // Pages, one after another: the race, its stats (when it has any), the ship.
            bool hasStats = spotlight.Stats.Count > 0;
            int pages = hasStats ? 3 : 2;
            if (now - spotPageAt >= SpotPageSeconds) { spotPage = (spotPage + 1) % pages; spotPageAt = now; }
            int page = spotPage % pages;
            if (!hasStats && page == 1) page = 2;
            var p = new HudModel.AnalyserPanel { Hint = "Left click another grid to move the spotlight   Esc: exit" };
            void Add(string label, string value, HudColor? colour = null) => p.Rows.Add((label, value, null, HudColor.White, colour ?? HudColor.White));
            if (page == 0)
            {
                p.Heading = "RACE RESULTS";
                p.TitleA = who;
                Add("COURSE", spotlight.Course ?? "-");
                if (!string.IsNullOrWhiteSpace(spotlight.Pilot)) Add("PILOT", spotlight.Pilot);
                if (!string.IsNullOrWhiteSpace(spotlight.Date)) Add("DATE", spotlight.Date);
                if (!string.IsNullOrWhiteSpace(spotlight.Headline)) Add("", spotlight.Headline, HudColor.Purple);
                int laps = spotlight.Laps.Count;
                Add(laps == 1 ? "RACE TIME (1 LAP)" : $"RACE TIME ({laps} LAPS)", LapTimer.Format(spotlight.Total), HudColor.Green);
                if (laps > 0)
                {
                    int best = 0;
                    for (int i = 1; i < laps; i++) if (spotlight.Laps[i].Time < spotlight.Laps[best].Time) best = i;
                    if (laps > 1) Add("BEST LAP", $"{LapTimer.Format(spotlight.Laps[best].Time)}  (lap {best + 1})", HudColor.Purple);
                    int missed = spotlight.Laps.Sum(x => x.Missed), crashes = spotlight.Laps.Sum(x => x.Crashes);
                    double penalty = spotlight.Laps.Sum(x => x.Penalty);
                    Add("PENALTIES", missed + crashes == 0 ? "none: a clean race" : $"{missed} missed, {crashes} crash{(crashes == 1 ? "" : "es")}  (+{penalty:0.#} s)",
                        missed + crashes == 0 ? HudColor.Green : HudColor.Red);
                    for (int i = 0; i < laps && i < 12; i++)
                    {
                        var lap = spotlight.Laps[i];
                        Add($"  LAP {i + 1}", LapTimer.Format(lap.Time) + (lap.Penalty > 0 ? $"  (+{lap.Penalty:0.#} s)" : ""), i == best && laps > 1 ? HudColor.Purple : (HudColor?)null);
                    }
                    if (laps > 12) Add($"  ...and {laps - 12} more laps", "");
                }
            }
            else if (page == 1)
            {
                p.Heading = "RACE STATS";
                p.TitleA = who;
                foreach (var row in spotlight.Stats.Take(22)) Add((row.Label ?? "").ToUpperInvariant(), row.Value ?? "");
            }
            else
            {
                p.Heading = "THE SHIP";
                if (spotStats == null) { p.TitleA = who; Add("", "This grid's stats couldn't be read"); }
                else
                {
                    p.TitleA = spotStats.Name ?? who;
                    AddShipRows(p, spotStats, null);
                    DrawAnalysis(spotStats, new HudColor(255, 210, 60, 255));
                }
            }
            // (Which page, as dots at the end of the hint.)
            p.Hint += "     " + string.Join(" ", Enumerable.Range(0, pages).Select(k => k == spotPage % pages ? "[" + (k + 1) + "]" : (k + 1).ToString()));
            model.Analyser = p;
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
                var rc = System.Text.Json.JsonSerializer.Deserialize<GateSettings.RpiClass>(File.ReadAllText(path));
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
                string Weight(ShipStats st) => st.Mass >= minWeight ? $"OK  ({st.Mass - minWeight:0} kg over {minWeight:0} kg)" : $"UNDER by {minWeight - st.Mass:0} kg  (min {minWeight:0} kg)";
                HudColor WeightColour(ShipStats st) => st.Mass >= minWeight ? HudColor.Green : HudColor.Red;
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
                game.DrawMarkerLine(c, tip, cols[k]);
                // Arrow head.
                Vec3D side = Math.Abs(Vec3D.Dot(ways[k], s.Up)) > 0.9 ? s.Right : s.Up;
                double h = Math.Max(0.4, len * 0.12);
                game.DrawMarkerLine(tip, tip - ways[k] * h + side * h * 0.6, cols[k]);
                game.DrawMarkerLine(tip, tip - ways[k] * h - side * h * 0.6, cols[k]);
            }
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

        private List<double[]> PossibleTrace()
        {
            var rec = CourseRecord(false);
            if (rec == null || timer.Gate == null) return null;
            var parts = rec.BestSectorTraces;
            int n = timer.Checkpoints.Count;
            if (ReferenceEquals(parts, possibleFor) && ReferenceEquals(rec.BestLapTrace, possibleBestFor) && possibleTrace != null) return possibleTrace;
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
            double offset = 0;
            for (int k = 0; k <= n; k++)
            {
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
            possibleFor = parts;
            possibleBestFor = rec.BestLapTrace;
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
            double cycle = settings.GraphCycleSeconds;
            if (shown != null) shown.GraphPage = cycle > 0 && shown.Gap.Count > 1 && ((long)Math.Floor(game.TimeSeconds / cycle)) % 2 == 1 ? 1 : 0;
            if (shown != null && shipResults != null)
                shown.Footer = $"{settings.GetBinding(GateKey.ResultsMode)}: {(shown == shipResults ? "course best" : "this ship's best")}";
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
                    model.Markers.Add(new HudModel.ScreenMarker { X = x, Y = y, Text = label, Sub = gap, Colour = labelColour, Emphasis = false, World = g });
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
            Notify((replayTitle != null ? $"Replay: {replayTitle}" : $"Replay of your best lap ({LapTimer.Format(rec?.BestLap ?? replayLength)})"), HudColor.Purple);
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
                Notify(settings.ReplayOrbit ? "Replay camera: orbit (mouse to swing round the ghost, W / S to zoom)" : "Replay camera: chase", HudColor.Purple);
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

            // Height profile round the lap (height above the start gate) and the gates' places on it.
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
                for (int j = 0; j < idx.Count; j++) mm.Profile.Add((dist[j] / total, Vec3D.Dot(At(main, idx[j]) - timer.Gate.Center, up)));
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
            if (comparing && settings.MinimapLapReplay && !controlsOpen && resultsLapTime > 0)
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
            int drawn = 0;
            Vec3D At(int i) => new Vec3D(trace[i][0], trace[i][1], trace[i][2]);
            void Seg(Vec3D a, Vec3D b, HudColor c, double w)
            {
                if (offset != 0) { var o = lift * (w * 3 * offset); a += o; b += o; }
                game.DrawMarkerLine(a, b, c);
                game.DrawMarkerLine(a + side * w, b + side * w, c);
                game.DrawMarkerLine(a - side * w, b - side * w, c);
                game.DrawMarkerLine(a + lift * w, b + lift * w, c);
                game.DrawMarkerLine(a - lift * w, b - lift * w, c);
            }
            for (int i = 0; i + 1 < trace.Count && drawn < 1500; i++)
            {
                Vec3D a = At(i), b = At(i + 1);
                double len = (b - a).Length;
                if (len > 400) continue; // a jump in the recording (respawn / teleport), not flying
                double d = ((a + b) * 0.5 - me).Length;
                if (d > maxD) continue;
                var c = speedColours != null ? speedColours[i].WithAlpha(alpha) : line;
                double w = Math.Max(0.25, d * 0.0012) * widthMul; // ~2 px wide
                // Smooth corners: a curve through the points either side (Catmull-Rom), in more pieces up close.
                int pieces = len < 12 ? 1 : (int)Math.Min(d < 600 ? 6 : d < 1200 ? 3 : 1, Math.Ceiling(len / 12));
                if (pieces <= 1) { Seg(a, b, c, w); drawn++; continue; }
                Vec3D p0 = i > 0 && (a - At(i - 1)).Length < 400 ? At(i - 1) : a - (b - a);
                Vec3D p3 = i + 2 < trace.Count && (At(i + 2) - b).Length < 400 ? At(i + 2) : b + (b - a);
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
                if (d < 900 && labels < 4)
                {
                    string text = brake ? "BRAKE" : "GO";
                    if (hudLabels)
                    {
                        if (game.TryProjectToScreen(at + lift * h, out double x, out double y, out bool behind) && !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98)
                        {
                            model.Markers.Add(new HudModel.ScreenMarker { X = x, Y = y, Text = text, Sub = GameOptions.Speed(speed), Colour = c, World = at + lift * h });
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
            if (!ReferenceEquals(ship, hullFor)) BuildHull(ship);
            Vec3D X = axes[0], Y = axes[1], Z = axes[2];
            Vec3D[] normals = { X * -1, X, Y * -1, Y, Z * -1, Z };
            Vec3D light = (timer.Gate.Up * 0.8 + timer.Gate.Right * 0.35 + timer.Gate.Forward * 0.2).Normalized();

            IEnumerable<double[]> faces = hullFaces;
            IEnumerable<double[]> outline = hullEdges;
            if (dist > 700 || hullFaces.Count > 3000)
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
            game.DrawMarkerTriangles(ghostCorners, ghostColours, true);

            // Glowing outline along the hull's edges: the crisp "ghost" look.
            if (outline != null)
            {
                ghostLines.Clear();
                foreach (var e in outline)
                {
                    ghostLines.Add(g + X * e[0] + Y * e[1] + Z * e[2]);
                    ghostLines.Add(g + X * e[3] + Y * e[4] + Z * e[5]);
                }
                game.DrawMarkerLines(ghostLines, second ? new HudColor(255, 190, 90, 235) : new HudColor(130, 235, 255, 235), true);
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

            void Add(TimingGate g, string text, HudColor colour, bool target)
            {
                if (!game.TryProjectToScreen(g.Center, out double x, out double y, out bool behind)) return;
                double dist = me.HasValue ? (g.Center - me.Value).Length : 0;
                bool onScreen = !behind && x > 0.02 && x < 0.98 && y > 0.02 && y < 0.98;
                if (onScreen)
                    model.Markers.Add(new HudModel.ScreenMarker
                    {
                        X = x, Y = y, Text = text, Colour = colour, Emphasis = target, World = g.Center,
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
                bool isNext = i == next;
                HudColor colour = hit ? HudColor.Green.WithAlpha(130)
                                : timer.IsMissed(i) ? HudColor.Red.WithAlpha(150)
                                : isNext ? HudColor.Orange.WithAlpha(255)
                                : racing ? HudColor.Orange.WithAlpha(150) : HudColor.Orange;
                Add(timer.Checkpoints[i], name, colour, isNext);
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

        private void DrawMarker(TimingGate gate, string label, HudColor colour, bool showDistance, double emphasis, bool drawText = true)
        {
            Vec3D c = gate.Center;
            double distance = playerPosition.HasValue ? (c - playerPosition.Value).Length : 500;
            double size = Math.Max(1.5, distance * settings.MarkerSize) * emphasis;

            Vec3D top = c + gate.Up * size, bottom = c - gate.Up * size;
            Vec3D[] ring =
            {
                c + gate.Right * (size * 0.6), c + gate.Forward * (size * 0.6),
                c - gate.Right * (size * 0.6), c - gate.Forward * (size * 0.6),
            };
            for (int i = 0; i < 4; i++)
            {
                game.DrawMarkerLine(top, ring[i], colour);
                game.DrawMarkerLine(bottom, ring[i], colour);
                game.DrawMarkerLine(ring[i], ring[(i + 1) % 4], colour);
            }

            if (!drawText) return; // the HUD shows the label instead

            // Small label UNDER the diamond, so it doesn't sit on the game's own GPS markers.
            string text = label;
            if (showDistance)
                text += distance >= 1000 ? $"  {distance / 1000:0.0} km" : $"  {distance:F0} m";
            game.DrawMarkerText(bottom - gate.Up * (size * 0.5), text, colour, emphasis > 1 ? 0.65 : 0.5);
        }

        private void DrawBox(TimingGate gate, HudColor colour)
        {
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
                Delta = liveGapText ?? gapText,
                DeltaColour = liveGapText != null ? liveGapColour : gapColour,
                LapSpeeds = lapSpeeds,
                LapStats = lapStats,
                RaceStats = raceStats,
                SectorsBefore = recordSectorsAtStart,
                SplitsBefore = recordBestSplitsAtStart,
                LapBefore = recordLapAtStart,
                PossibleLap = PossibleLap(record, timer.Checkpoints.Count),
                Results = settings.ResultsSeconds <= 0 || now < resultsUntil ? ShownResults() : null, // 0 = until the next race
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
                if (raceMap) game.ShowRaceMap(mmp);
            }
            PluginLog.Step("racing line / ghost");
            if (secondGhostKey != null && secondGhostCourse != CourseKey()) ClearSecondGhost();   // a different course now
            if (replaying) DrawRacingLineAndGhost(replayTrace, replayShip, replayTitle, model, hudLabels);
            else
            {
                string ghostLabel = raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count) ? raceGhost.Label
                    : racePossible ? "POSSIBLE LAP" : null;
                DrawRacingLineAndGhost(GhostTrace, GhostShip, ghostLabel, model, hudLabels);
            }
            if (!replaying && !editMode) AddFlightAids(model);
            PluginLog.Step("hud");
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
