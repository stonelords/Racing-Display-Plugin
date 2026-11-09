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
        private volatile bool escapeStop;

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
                replaying = replayPending = replayCameraReady = false;
                game.ShowHud(null);
                return;
            }

            PluginLog.Step("frame");
            bool hasPose = game.TryGetControlledPose(out Vec3D pos, out Vec3D fwd, out Vec3D up);
            Vec3D? position = hasPose ? pos : (Vec3D?)null;
            playerPosition = position;
            playerForward = hasPose ? fwd : (Vec3D?)null;
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
            if (replayPending && !controlsOpen && !game.IsPaused) BeginReplay();
            if (escapeStop) { escapeStop = false; if (replaying) StopReplay("Replay stopped"); }
            if (replaying)
            {
                UpdateReplay();
                PlayQueuedSounds();
                Draw();
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
            // Gates and checkpoints are tested with the middle of the ship, not the grid's pivot (which can be
            // well off to one side, so passes near the edge of a gate were missed).
            Vec3D? detectAt = position;
            if (hasPose && game.TryGetShipCentre(out Vec3D centre)) detectAt = centre;
            // The spectator camera flying through a gate or checkpoint doesn't count: the timer keeps the ship's last
            // position until you're back in control.
            if (game.InSpectator) detectAt = null;
            timer.Update(detectAt, game.TimeSeconds);
            TrackLap(position, velocity);
            UpdateLiveGap(position);

            // A crash during the immunity time after the last crash penalty doesn't count.
            if (crash.Update(velocity, game.TimeSeconds) && game.TimeSeconds - lastCrashPenaltyAt >= settings.CrashImmunitySeconds && timer.AddCrash())
            {
                lastCrashPenaltyAt = game.TimeSeconds;
                RaceNotify($"CRASH at {GameOptions.Speed(crash.LastImpactSpeed)}!  +{settings.CrashPenaltySeconds:0.#}s penalty"
                    + (settings.CrashImmunitySeconds > 0 ? $" (no crash penalty for {settings.CrashImmunitySeconds:0.#}s)" : ""), HudColor.Red);
                FlashTimer(HudColor.Red);
                Sound(settings.SoundMissed);
                Sound(settings.SoundMissed, 120);
            }

            UpdateCountdown(position, velocity, game.TimeSeconds);
            PlayQueuedSounds();

            PluginLog.Step("draw");
            Draw();
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
        private BlockPick blockFirst;              // Pick blocks: the first corner, once clicked
        private TimingGate blockPreview;           // Pick blocks: the aimed block, or the box from the first corner to it
        private TimingGate airPreview;             // where a gate / checkpoint would go in the air (In the air switch)
        private bool lmbWas, rmbWas;
        private GridBox? preview;
        private double previewAt = double.NegativeInfinity;
        private const double EditReach = 3000;    // how far away a grid can be picked (m)

        private string PlacementName => settings.EditAirPlace ? "IN THE AIR" : settings.EditBlockPick ? "PICK BLOCKS" : "ON A GRID";

        private string ToolName => (settings.EditBlockPick && !settings.EditAirPlace && blockFirst != null ? "CORNER 2: " : "") + (
            editTool == ToolStartFinish ? "START / FINISH GATE"
            : editTool == ToolStart ? "START GATE"
            : editTool == ToolFinish ? "FINISH GATE"
            : $"CHECKPOINT {timer.Checkpoints.Count + 1}");

        private HudColor ToolColour => editTool == ToolCheckpoint ? HudColor.Orange : editTool == ToolFinish ? HudColor.Purple : HudColor.Cyan;

        private void SetEditMode(bool on, Vec3D? position)
        {
            if (on == editMode) return;
            editMode = on;
            game.SetDebugDraw(on); // so the gate and checkpoint boxes show while editing
            preview = null;
            blockFirst = null; blockPreview = null;
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
                if (editTool == ToolStartFinish)
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
                    if (already >= 0) { Notify($"That's already CP {already + 1}", HudColor.Yellow); return; }
                    if (timer.Gate != null && (timer.Gate.Center - gate.Center).Length < 2) { Notify("That's the start / finish gate", HudColor.Yellow); return; }
                    list.Add(GateSettings.SavedGate.From(gate, game.WorldId));
                    SaveCheckpoints(list, position);
                    Notify($"Checkpoint {list.Count} set {where}", HudColor.Cyan);
                }
                Sound(settings.SoundCheckpoint);
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
            if (model.Timer != null)
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

        // ---------------------------------------------------------------- lap tracking, live gap, sounds

        private void StartLapTracking()
        {
            lapTrace = new List<double[]>();
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
            lapStats.Clear();
            raceSpeedSamples.Clear();
            raceStats = null;
            raceShipName = game.ShipName;
            GateSettings.ShipRecord sr = null;
            var crec = CourseRecord(false);
            if (raceShipName != null && crec?.Ships != null) crec.Ships.TryGetValue(raceShipName, out sr);
            shipLapAtStart = sr?.BestLap;
            shipSpeedAtStart = sr?.BestLapSpeed;
        }

        /// <summary>The path and ship shape the ghost, racing line and live gap use: a raced-against ghost file, else your best.</summary>
        private List<double[]> GhostTrace => raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count) ? raceGhost.Trace : CourseRecord(false)?.BestLapTrace;
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
            while (game.TryTakeMenuCommand(out var c))
            {
                bool validRow = c.Row >= 0 && c.Row < actions.Length;
                switch (c.Action)
                {
                    case MenuAction.Opened:
                        capturing = false;
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
                        else { raceGhost = g; recordsVersion++; controlsNote = $"Racing against {g.Label}: the ghost, racing line and live gap use it"; }
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
                    case MenuAction.RaceShip:
                    {
                        var g = c.Text == null ? null : MakeGhost(c.Text);
                        if (g == null) { controlsNote = "No recorded best lap for that ship yet"; break; }
                        g.Path = "ship:" + c.Text;
                        raceGhost = g; recordsVersion++;
                        controlsNote = $"Racing against {c.Text}'s best lap: the ghost, racing line and live gap use it";
                        break;
                    }
                    case MenuAction.RaceCourseBest:
                        raceGhost = null; recordsVersion++;
                        controlsNote = "Racing against your own best again";
                        break;
                    case MenuAction.DeleteGhostFile:
                        try
                        {
                            if (c.Text != null && c.Text.EndsWith(GhostFile.Extension) && File.Exists(c.Text)) File.Delete(c.Text);
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
                    case MenuAction.ToggleEditMode:
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
                        if (r?.Ships != null && c.Text != null && r.Ships.Remove(c.Text))
                        {
                            settings.Save(settingsPath);
                            recordsVersion++;
                            controlsNote = $"Times for {c.Text} cleared";
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

            game.ShowControlsMenu(BuildControlsPanel());
        }

        // ---------------------------------------------------------------- courses

        private string CurrentCourseName() => settings.CurrentCourseName.TryGetValue(game.WorldId, out var n) ? n : null;

        private static GateSettings.SavedGate Copy(GateSettings.SavedGate g, string world) => GateSettings.SavedGate.From(g.ToGate(), world);

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
                    Selected = selected,
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
                    });
                }
            }

            // Records tab: ghost files.
            if (ghostFiles == null) ghostFiles = GhostFile.LoadAll(GhostFolder);
            p.GhostFolder = GhostFolder;
            p.PilotName = settings.PilotName;
            p.CourseHasBest = rec?.BestLapTrace != null;
            bool racingFile = raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count);
            if (!racingFile) foreach (var row in p.Ships) row.Racing = false;
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
            foreach (var sec in settings.CourseSectors)
            {
                string Pt(int k) => k >= 0 && k < p.RoutePoints.Count ? p.RoutePoints[k] : $"#{k}";
                p.Sectors.Add((sec.Name, $"{Pt(sec.From)} to {Pt(sec.To)}"));
            }
            foreach (var sc in settings.SavedCourses.Where(x => x.World == game.WorldId).OrderBy(x => x.Name))
                p.SavedCourses.Add((sc.Name, $"{sc.Checkpoints.Count} checkpoint{(sc.Checkpoints.Count == 1 ? "" : "s")}", sc.Name == p.CourseName));

            p.SettingsVersion = settingsVersion;
            for (int i = 0; i < SettingDefs.All.Length; i++)
            {
                var d = SettingDefs.All[i];
                p.Settings.Add(new HudModel.SettingRow
                {
                    Index = i, Section = d.Section, Name = d.Name, Description = d.Description, Format = d.Format, Choices = d.Choices,
                    Toggle = d.Toggle, Min = d.Min, Max = d.Max, Step = d.Step, Value = d.Clean(d.Get(settings)),
                });
            }
            return p;
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
            if (folder == null)
            {
                // A new world that hasn't been saved yet has no folder (and no courses); stop looking after a while.
                if (now - worldLoadedAt > 120) worldCoursesChecked = true;
                return;
            }
            worldCoursesChecked = true;
            bool haveCourse = timer.Gate != null || timer.Checkpoints.Count > 0;   // made one already while we looked
            var wc = GateSettings.WorldCourses.Load(folder);
            bool fromWorkshop = false;
            if (wc == null && !haveCourse)
            {
                wc = FindWorkshopCourse();
                fromWorkshop = wc != null;
            }
            if (wc != null)
            {
                MergeWorldCourses(wc);
                if (!haveCourse && wc.Current?.Gate != null)
                {
                    ApplyCourse(wc.Current);
                    RaceNotify($"Race course '{wc.Current.Name ?? "course"}' loaded from this world ({timer.Checkpoints.Count} checkpoints)", HudColor.Cyan);
                }
                // Came from the Workshop download: keep it in this world's own save from now on.
                if (fromWorkshop) SaveCoursesToWorld(true);
                return;
            }
        }

        /// <summary>
        /// A world made from a Workshop download has no RaceDisplayCourses.json (the game doesn't copy it into the new
        /// save), but the download does. Look through the downloads for the course whose gates and checkpoints sit on
        /// this world's grids.
        /// </summary>
        private GateSettings.WorldCourses FindWorkshopCourse()
        {
            try
            {
                var files = WorkshopCourses.FindFiles();
                if (files.Count == 0) return null;
                var grids = game.FindNamedGrids(_ => true, false);
                GateSettings.WorldCourses best = null;
                double bestFit = 0;
                string bestFile = null;
                foreach (var f in files)
                {
                    var wc = GateSettings.WorldCourses.Load(Path.GetDirectoryName(f));
                    if (wc?.Current == null) continue;
                    double fit = WorkshopCourses.Fit(wc.Current, grids);
                    if (fit > bestFit) { bestFit = fit; best = wc; bestFile = f; }
                }
                PluginLog.Info($"Workshop courses: {files.Count} file(s) found, best fit {bestFit:P0}{(bestFile != null ? " (" + bestFile + ")" : "")}");
                return bestFit >= 0.5 ? best : null;
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
                Notify("Race reset");
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
                    PluginLog.StartTrace(30, "race start");   // frame times while the race gets going
                    if (timer.PracticeLapsDone.Count == 0 || currentShip == null) currentShip = game.GetShipShape(250);
                    ResetRaceStats();
                    results = shipResults = null;
                    gapText = null;
                    lapSpeeds.Clear();
                    raceSpeedTraces.Clear();
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
                    results = BuildResults(seconds);
                    resultsUntil = game.TimeSeconds + settings.ResultsSeconds;
                    if (results.Headline != null && results.Headline.StartsWith("NEW RECORD"))
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
                list.Add(("VS SHIP BEST", d < 0 ? $"-{-d:0.000}s best" : $"+{d:0.000}s", d < 0 ? HudColor.Purple : HudColor.Yellow));
            }
            else if (!string.IsNullOrEmpty(raceShipName)) list.Add(("VS SHIP BEST", "first race", HudColor.Grey));

            if (lapStats.Count > 0)
            {
                double dist = 0, turnSum = 0, maxTurn = 0, maxG = 0, maxBrake = 0;
                int maxTurnLap = 1;
                for (int l = 0; l < lapStats.Count; l++)
                {
                    var st = lapStats[l];
                    dist += st.Distance;
                    turnSum += st.AvgTurnG;
                    if (st.MaxTurnG > maxTurn) { maxTurn = st.MaxTurnG; maxTurnLap = l + 1; }
                    maxG = Math.Max(maxG, st.MaxG);
                    maxBrake = Math.Max(maxBrake, st.MaxBrakeG);
                }
                list.Add(("DISTANCE", Distance(dist), HudColor.White));
                list.Add(("MAX TURN G", $"{maxTurn:0.0} g  L{maxTurnLap}", HudColor.White));
                list.Add(("AVG TURN G", $"{turnSum / lapStats.Count:0.0} g", HudColor.White));
                list.Add(("MAX BRAKING", $"{maxBrake:0.0} g", HudColor.White));
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

        private static void AddGraphLine(HudModel.ResultsPanel panel, string label, HudColor colour, List<double[]> trace)
        {
            if (trace == null || trace.Count < 2) return;
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
            UpdateBestSectors(rec.BestSectors, lap, n);
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
        private GhostFile MakeGhost(string ship)
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
                trace = sr?.BestLapTrace; shape = sr?.BestLapShip; speed = sr?.BestLapSpeed; splits = sr?.BestLapSplits; time = sr?.BestLap;
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
                panel.Headline = $"SHIP BEST!  {LapTimer.FormatDelta(total - raceBefore.Value)}";
                panel.HeadlineColour = HudColor.Yellow;
            }
            else
            {
                panel.Headline = $"{LapTimer.FormatDelta(total - raceBefore.Value)} vs ship best";
                panel.HeadlineColour = HudColor.Grey;
            }

            panel.Lines.Add(("TOTAL", LapTimer.Format(total), HudColor.White));
            if (timer.RaceBestLap.HasValue)
            {
                bool shipBest = !lapBefore.HasValue || timer.RaceBestLap.Value < lapBefore.Value;
                panel.Lines.Add(("BEST LAP", LapTimer.Format(timer.RaceBestLap.Value), shipBest ? HudColor.Purple : HudColor.Green));
            }
            if (lapBefore.HasValue)
                panel.Lines.Add(("SHIP BEST LAP", LapTimer.Format(Math.Min(lapBefore.Value, timer.RaceBestLap ?? double.MaxValue)), HudColor.Yellow));
            if (raceBefore.HasValue && total >= raceBefore.Value)
                panel.Lines.Add(("SHIP BEST RACE", LapTimer.Format(raceBefore.Value), HudColor.Yellow));
            double? possible = PossibleLap(ship.BestSectors, timer.Checkpoints.Count);
            if (possible.HasValue)
            {
                panel.Lines.Add(("POSSIBLE LAP", LapTimer.Format(possible.Value), HudColor.Purple));
                if (laps > 1) panel.Lines.Add(("POSSIBLE RACE", LapTimer.Format(possible.Value * laps), HudColor.Purple));
            }
            // Speeds and penalties are the same either way.
            foreach (var line in overall.Lines)
                if (line.Label == "TOP / AVG SPEED" || line.Label == "PENALTIES") panel.Lines.Add(line);

            // Graph: best lap of this race against this ship's best lap before it.
            if (laps > 0 && raceSpeedTraces.Count == laps)
            {
                int bestIdx = 0;
                for (int l = 1; l < laps; l++) if (timer.Laps[l].Time < timer.Laps[bestIdx].Time) bestIdx = l;
                bool beat = !lapBefore.HasValue || timer.Laps[bestIdx].Time < lapBefore.Value;
                AddGraphLine(panel, $"Best this race (L{bestIdx + 1})", beat ? HudColor.Purple : HudColor.Green, raceSpeedTraces[bestIdx]);
                if (speedBefore != null) AddGraphLine(panel, beat ? "Old ship best" : "Ship best", beat ? HudColor.Grey : HudColor.Purple, speedBefore);
                else if (bestIdx != laps - 1) AddGraphLine(panel, $"Last lap (L{laps})", HudColor.Grey, raceSpeedTraces[laps - 1]);
            }
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

            panel.Lines.Add(("TOTAL", LapTimer.Format(total), HudColor.White));
            if (timer.RaceBestLap.HasValue)
            {
                bool record = rec.BestLap.HasValue && Math.Abs(timer.RaceBestLap.Value - rec.BestLap.Value) < 1e-6;
                panel.Lines.Add(("BEST LAP", LapTimer.Format(timer.RaceBestLap.Value), record ? HudColor.Purple : HudColor.Green));
            }
            double? possible = PossibleLap(rec, timer.Checkpoints.Count);
            if (possible.HasValue)
            {
                panel.Lines.Add(("POSSIBLE LAP", LapTimer.Format(possible.Value), HudColor.Purple));
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
                panel.Lines.Add(("PENALTIES", $"+{penalty:0.#}s  ({string.Join(", ", parts)})", HudColor.Red));
            }
            else panel.Lines.Add(("PENALTIES", "none - clean race!", HudColor.Green));

            if (rec.BestRace.TryGetValue(laps, out double bestRace))
            {
                if (total < bestRace)
                {
                    panel.Headline = $"NEW RECORD!  {LapTimer.FormatDelta(total - bestRace)}";
                    panel.HeadlineColour = HudColor.Yellow;
                    rec.BestRace[laps] = total;
                    FlashTimer(HudColor.Green);
                }
                else
                {
                    panel.Headline = $"{LapTimer.FormatDelta(total - bestRace)} vs record";
                    panel.HeadlineColour = HudColor.Grey;
                    panel.Lines.Add(("RECORD", LapTimer.Format(bestRace), HudColor.Yellow));
                }
            }
            else
            {
                panel.Headline = "NEW RECORD!";
                panel.HeadlineColour = HudColor.Yellow;
                rec.BestRace[laps] = total;
            }

            // Speed graph: best lap of this race (green) against the course record lap (purple).
            if (laps > 0 && raceSpeedTraces.Count == laps)
            {
                int bestIdx = 0;
                for (int l = 1; l < laps; l++) if (timer.Laps[l].Time < timer.Laps[bestIdx].Time) bestIdx = l;
                bool raceBestIsRecord = rec.BestLap.HasValue && Math.Abs(timer.Laps[bestIdx].Time - rec.BestLap.Value) < 1e-6;
                AddGraphLine(panel, $"Best this race (L{bestIdx + 1})", raceBestIsRecord ? HudColor.Purple : HudColor.Green, raceSpeedTraces[bestIdx]);
                if (!raceBestIsRecord && rec.BestLapSpeed != null) AddGraphLine(panel, "Course record", HudColor.Purple, rec.BestLapSpeed);
                if (bestIdx != laps - 1) AddGraphLine(panel, $"Last lap (L{laps})", HudColor.Grey, raceSpeedTraces[laps - 1]);
            }

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
            // What the map's route and lines are made from, as a number (no text built every frame).
            var hk = new HashCode();
            hk.Add(game.WorldId);
            void AddGate(TimingGate g) { if (g == null) { hk.Add(0); return; } hk.Add(Math.Round(g.Center.X)); hk.Add(Math.Round(g.Center.Y)); hk.Add(Math.Round(g.Center.Z)); }
            AddGate(timer.Gate); AddGate(timer.FinishGate);
            hk.Add(timer.Checkpoints.Count);
            foreach (var cp in timer.Checkpoints) AddGate(cp);
            hk.Add(line == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(line)); hk.Add(line?.Count ?? 0);
            hk.Add(line2 == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(line2)); hk.Add(line2?.Count ?? 0);
            hk.Add(settings.MinimapSize); hk.Add(settings.RacingLineColourMode);
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

            // Gates and checkpoints: the next one orange, flown ones green, missed ones red, the rest grey; gates cyan / purple.
            bool running = timer.State == TimerState.Running;
            int next = running ? timer.NextCheckpoint : -2;
            var start = MapPoint(timer.Gate.Center);
            m.Points.Add((start.X, start.Y, HudColor.Cyan, true, timer.FinishGate != null ? "START" : "S/F"));
            if (timer.FinishGate != null) { var f = MapPoint(timer.FinishGate.Center); m.Points.Add((f.X, f.Y, HudColor.Purple, true, "FINISH")); }
            for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                var c = MapPoint(timer.Checkpoints[i].Center);
                var col = timer.IsCheckpointHit(i) ? HudColor.Green : timer.IsMissed(i) ? HudColor.Red : i == next ? HudColor.Orange : HudColor.Grey;
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
                if (playerForward.HasValue)
                    m.PlayerAngle = Math.Atan2(Vec3D.Dot(playerForward.Value, mmE1), Vec3D.Dot(playerForward.Value, mmE2)) * 180 / Math.PI;
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
            var line = colour ?? RacingLineColour;
            Vec3D side = timer.Gate.Right, lift = timer.Gate.Up;
            var speedColours = colourMode > 0 ? LineSpeedColours(trace, colourMode) : null;
            int drawn = 0;
            for (int i = 0; i + 1 < trace.Count && drawn < 500; i++)
            {
                var a = new Vec3D(trace[i][0], trace[i][1], trace[i][2]);
                var b = new Vec3D(trace[i + 1][0], trace[i + 1][1], trace[i + 1][2]);
                if ((b - a).Length > 400) continue; // a jump in the recording (respawn / teleport), not flying
                double d = ((a + b) * 0.5 - me).Length;
                if (d > 2000) continue;
                if (speedColours != null) line = speedColours[i];
                double w = Math.Max(0.25, d * 0.0012); // ~2 px wide
                if (offset != 0) { var o = lift * (w * 3 * offset); a += o; b += o; }
                game.DrawMarkerLine(a, b, line);
                game.DrawMarkerLine(a + side * w, b + side * w, line);
                game.DrawMarkerLine(a - side * w, b - side * w, line);
                game.DrawMarkerLine(a + lift * w, b + lift * w, line);
                game.DrawMarkerLine(a - lift * w, b - lift * w, line);
                drawn++;
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
                bool hit = timer.IsCheckpointHit(i);
                bool isNext = i == next;
                HudColor colour = hit ? HudColor.Green.WithAlpha(130)
                                : timer.IsMissed(i) ? HudColor.Red.WithAlpha(150)
                                : isNext ? HudColor.Orange.WithAlpha(255)
                                : racing ? HudColor.Orange.WithAlpha(150) : HudColor.Orange;
                Add(timer.Checkpoints[i], $"CP {i + 1}", colour, isNext);
            }
            bool gateIsTarget = next == -1;
            if (timer.FinishGate != null)
            {
                // Sprint: the start gate is only a target before the race; the finish gate is the last target.
                Add(timer.Gate, "START", HudColor.Cyan.WithAlpha((byte)(racing ? 140 : 255)), !racing);
                Add(timer.FinishGate, "FINISH", HudColor.Purple.WithAlpha(255), racing && gateIsTarget);
            }
            else Add(timer.Gate, "START / FINISH", HudColor.Cyan.WithAlpha(255), gateIsTarget);
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
            else if (timer.Gate == null && now - worldLoadedAt < 30)
                // Shown for 30 s after loading a world: confirms the plugin is running.
                centre = new HudModel.Message { Text = $"Race Display: {settings.GetBinding(GateKey.EditMode)} for course edit mode to set up a course.  Esc > Racing Display for settings" };

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
            else DrawRacingLineAndGhost(GhostTrace, GhostShip, raceGhost != null && raceGhost.Fits(timer.Gate, timer.Checkpoints.Count) ? raceGhost.Label : null, model, hudLabels);
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
                    bool hit = timer.IsCheckpointHit(i);
                    bool isNext = i == next;
                    HudColor colour = hit ? HudColor.Green.WithAlpha(110)
                                    : isNext ? HudColor.Orange.WithAlpha(255)
                                    : racing ? HudColor.Orange.WithAlpha(110) : HudColor.Orange;
                    DrawMarker(timer.Checkpoints[i], $"CP {i + 1}", colour, isNext, isNext ? 1.6 : 1.0, !hudLabels);
                }
                if (timer.Gate != null)
                {
                    bool gateIsTarget = next == -1;
                    if (timer.FinishGate != null)
                    {
                        DrawMarker(timer.Gate, "START", HudColor.Cyan, !racing, 1.0, !hudLabels);
                        DrawMarker(timer.FinishGate, "FINISH", HudColor.Purple, racing && gateIsTarget, gateIsTarget && racing ? 1.4 : 1.0, !hudLabels);
                    }
                    else DrawMarker(timer.Gate, "START / FINISH", HudColor.Cyan, gateIsTarget, gateIsTarget && racing ? 1.4 : 1.0, !hudLabels);
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
