using System;
using System.Collections.Generic;

namespace RaceDisplay.Core
{
    public enum TimerState
    {
        /// <summary>No gate placed.</summary>
        NoGate,
        /// <summary>Gate placed, waiting for the first pass.</summary>
        Armed,
        /// <summary>Race running; each pass through the gate completes a lap.</summary>
        Running,
        /// <summary>Lap target reached; showing the result. Next pass starts a new race.</summary>
        Finished,
    }

    public enum LapEventKind { RaceStarted, LapCompleted, RaceFinished, CheckpointHit, PracticeStarted, PracticeLapCompleted }

    /// <summary>One completed lap.</summary>
    public readonly struct LapRecord
    {
        /// <summary>Lap time including penalties.</summary>
        public readonly double Time;
        /// <summary>Time actually flown, without penalties.</summary>
        public readonly double Flown;
        public readonly int MissedCheckpoints;
        public readonly int Crashes;
        /// <summary>Total penalty seconds (missed checkpoints + crashes).</summary>
        public readonly double Penalty;
        /// <summary>Lap time (flown, no penalties) when each checkpoint was hit; null = missed.</summary>
        public readonly double?[] Splits;

        public LapRecord(double flown, int missed, int crashes, double penalty, double?[] splits)
        {
            Flown = flown;
            MissedCheckpoints = missed;
            Crashes = crashes;
            Penalty = penalty;
            Time = flown + penalty;
            Splits = splits ?? new double?[0];
        }
    }

    /// <summary>
    /// Race logic. The first pass through the start/finish gate starts the race; every pass after that
    /// completes a lap. Checkpoints along the course must each be flown through once per lap (any order);
    /// every checkpoint missed adds a time penalty to that lap.
    /// With a lap target (e.g. 3) the race finishes after that many laps; with 0 laps it keeps lapping.
    /// Game-independent: feed it positions and a clock, read the state back.
    /// </summary>
    public sealed class LapTimer
    {
        /// <summary>Ignore a second start/finish pass within this many seconds.</summary>
        public double MinLapSeconds { get; set; } = 2.0;

        /// <summary>After a start/finish pass, the ship must get this far from the gate before the next one counts.</summary>
        public double RearmDistance { get; set; } = 30.0;

        /// <summary>Moves bigger than this in one tick are treated as a teleport/respawn, not flight.</summary>
        public double TeleportDistance { get; set; } = 5000.0;

        /// <summary>A race only starts going through the start gate the right way (not backwards).</summary>
        public bool ForwardStartOnly { get; set; } = true;

        /// <summary>Laps per race. 0 = unlimited (keeps lapping until reset). Always 1 with a separate finish gate.</summary>
        public int LapTarget { get => FinishGate != null ? 1 : lapTarget; set => lapTarget = value; }
        private int lapTarget;

        /// <summary>
        /// A separate finish gate (point-to-point / sprint course): the race starts at Gate and ends at FinishGate.
        /// Null = Gate is both start and finish (a circuit).
        /// </summary>
        public TimingGate FinishGate { get; private set; }
        private bool wasInsideFinish;

        public void SetFinishGate(TimingGate gate, Vec3D? currentShipPosition)
        {
            FinishGate = gate;
            if (Gate != null) { State = TimerState.Armed; ClearRace(); }
            wasInsideFinish = gate != null && currentShipPosition.HasValue && gate.Contains(currentShipPosition.Value);
        }

        /// <summary>Seconds added to a lap for each checkpoint missed.</summary>
        public double PenaltySeconds { get; set; } = 5.0;

        /// <summary>
        /// Practice laps before the race. The first gate pass starts practice; each pass completes a practice lap,
        /// and the pass that completes the last one starts the race itself. Practice laps are timed (with
        /// penalties) but don't count towards the race.
        /// </summary>
        public int PracticeLaps { get; set; }

        /// <summary>True while flying practice laps (State is Running).</summary>
        public bool InPractice { get; private set; }

        /// <summary>Completed practice laps of the current (or last) race.</summary>
        public IReadOnlyList<LapRecord> PracticeLapsDone => practice;

        /// <summary>Number of the practice lap being flown (1-based), or 0 if not in practice.</summary>
        public int CurrentPracticeLap => InPractice ? practice.Count + 1 : 0;

        public TimingGate Gate { get; private set; }
        public TimerState State { get; private set; } = TimerState.NoGate;

        public IReadOnlyList<TimingGate> Checkpoints => checkpoints;

        /// <summary>Has checkpoint i been flown through this lap?</summary>
        public bool IsCheckpointHit(int i) => i >= 0 && i < cpHit.Count && cpHit[i];

        public int CheckpointsHitThisLap
        {
            get { int n = 0; foreach (bool h in cpHit) if (h) n++; return n; }
        }

        /// <summary>Completed laps of the current (or last) race, in order.</summary>
        public IReadOnlyList<LapRecord> Laps => laps;

        /// <summary>Fastest lap (including penalties) in the current (or last) race.</summary>
        public double? RaceBestLap { get; private set; }

        /// <summary>Total time of the last finished race, including penalties.</summary>
        public double? LastRaceTotal { get; private set; }

        /// <summary>Raised on race start, each lap, race finish and checkpoint hit. Second argument: lap time, total, or checkpoint index.</summary>
        public event Action<LapEventKind, double> Triggered;

        /// <summary>Seconds added for each crash.</summary>
        public double CrashPenaltySeconds { get; set; } = 5.0;

        /// <summary>Crashes so far in the lap being flown.</summary>
        public int CrashesThisLap { get; private set; }

        /// <summary>Penalty already earned this lap from crashes (missed checkpoints are added at the finish).</summary>
        public double CurrentLapPenalty => CrashesThisLap * CrashPenaltySeconds + MissedThisLap * PenaltySeconds;

        /// <summary>Checkpoints skipped so far this lap (a later one has been flown through).</summary>
        public int MissedThisLap
        {
            get { int n = 0; for (int i = 0; i < cpHit.Count; i++) if (IsMissed(i)) n++; return n; }
        }

        /// <summary>Records a crash. Only counts while a race is running. Returns true if it counted.</summary>
        public bool AddCrash()
        {
            if (State != TimerState.Running) return false;
            CrashesThisLap++;
            return true;
        }

        private readonly List<LapRecord> laps = new List<LapRecord>();
        private readonly List<LapRecord> practice = new List<LapRecord>();
        private readonly List<TimingGate> checkpoints = new List<TimingGate>();
        private readonly List<bool> cpHit = new List<bool>();
        private readonly List<double?> cpSplit = new List<double?>();
        private readonly List<bool> cpInside = new List<bool>();
        private double lapStart;
        private double lastTriggerTime = double.NegativeInfinity;
        private Vec3D? lastPosition;
        private bool wasInside;
        private bool rearmed = true;

        public void SetGate(TimingGate gate, Vec3D? currentShipPosition)
        {
            Gate = gate;
            State = gate == null ? TimerState.NoGate : TimerState.Armed;
            ClearRace();
            lastPosition = currentShipPosition;
            // If the gate is placed around the ship, don't trigger until it has flown out and back in.
            wasInside = gate != null && currentShipPosition.HasValue && gate.Contains(currentShipPosition.Value);
            rearmed = !wasInside;
        }

        /// <summary>Replaces all checkpoints (re-arms the race).</summary>
        public void SetCheckpoints(IEnumerable<TimingGate> gates, Vec3D? currentShipPosition)
        {
            checkpoints.Clear();
            cpHit.Clear();
            cpSplit.Clear();
            cpInside.Clear();
            foreach (var g in gates)
            {
                checkpoints.Add(g);
                cpHit.Add(false);
                cpSplit.Add(null);
                cpInside.Add(currentShipPosition.HasValue && g.Contains(currentShipPosition.Value));
            }
            Reset();
        }

        /// <summary>Stops the race and re-arms the gate (lap times are cleared).</summary>
        /// <summary>Takes the current position as the starting point without testing the path to it (after course editing).</summary>
        public void Resync(Vec3D? position)
        {
            lastPosition = position;
            wasInside = Gate != null && position.HasValue && Gate.Contains(position.Value);
            wasInsideFinish = FinishGate != null && position.HasValue && FinishGate.Contains(position.Value);
            rearmed = !wasInside;
            for (int i = 0; i < checkpoints.Count; i++) cpInside[i] = position.HasValue && checkpoints[i].Contains(position.Value);
        }

        public void Reset()
        {
            if (Gate == null) return;
            State = TimerState.Armed;
            ClearRace();
        }

        private void ClearRace()
        {
            laps.Clear();
            practice.Clear();
            InPractice = false;
            RaceBestLap = null;
            LastRaceTotal = null;
            ClearCheckpointHits();
        }

        private void ClearCheckpointHits()
        {
            for (int i = 0; i < cpHit.Count; i++) { cpHit[i] = false; cpSplit[i] = null; }
            CrashesThisLap = 0;
        }

        /// <summary>Lap time when checkpoint i was hit this lap, or null.</summary>
        public double? SplitThisLap(int i) => i >= 0 && i < cpSplit.Count ? cpSplit[i] : null;

        /// <summary>Highest-numbered checkpoint flown through this lap (-1 if none yet).</summary>
        public int HighestHitThisLap
        {
            get
            {
                for (int i = cpHit.Count - 1; i >= 0; i--) if (cpHit[i]) return i;
                return -1;
            }
        }

        /// <summary>Checkpoint i was skipped: a later one has been flown through but not this one.</summary>
        public bool IsMissed(int i) => i >= 0 && i < cpHit.Count && !cpHit[i] && i < HighestHitThisLap;

        /// <summary>
        /// The checkpoint to fly to next: the first one after the furthest one reached this lap
        /// (-1 = none left, head for the start/finish gate). Skipped ones are left behind.
        /// </summary>
        public int NextCheckpoint
        {
            get
            {
                for (int i = HighestHitThisLap + 1; i < cpHit.Count; i++) if (!cpHit[i]) return i;
                return -1;
            }
        }

        /// <summary>
        /// After a countdown's GO: the race still starts when you fly through the gate. If you're already sitting
        /// inside the gate box, that counts as flying through it, so the race starts on the next update.
        /// </summary>
        public void PrimeStart(Vec3D? position)
        {
            if (Gate == null || State == TimerState.Running) return;
            rearmed = true;
            lastTriggerTime = double.NegativeInfinity;
            if (position.HasValue && Gate.Contains(position.Value)) wasInside = false;
        }

        /// <summary>Number of the race lap being flown now (1-based), or 0 if not racing (or still in practice).</summary>
        public int CurrentLapNumber => State == TimerState.Running && !InPractice ? laps.Count + 1 : 0;

        public double CurrentLapSeconds(double now) => State == TimerState.Running ? now - lapStart : 0;

        private double CompletedLapsTotal()
        {
            double t = 0;
            foreach (var l in laps) t += l.Time;
            return t;
        }

        /// <summary>Race time including penalties of completed laps.</summary>
        public double RaceSeconds(double now)
        {
            switch (State)
            {
                case TimerState.Running: return CompletedLapsTotal() + (now - lapStart) + CurrentLapPenalty;
                case TimerState.Finished: return LastRaceTotal ?? 0;
                default: return 0;
            }
        }

        /// <summary>
        /// Call once per game tick with the controlled ship/character position, or null when
        /// the player isn't controlling anything (e.g. in a menu or dead).
        /// </summary>
        public void Update(Vec3D? position, double now)
        {
            if (Gate == null) { lastPosition = position; return; }
            // No position this tick (it happens for a frame now and then): keep the last one, so the next
            // tick's path still runs from where the ship was and a gate passed in between isn't lost.
            if (!position.HasValue) return;

            Vec3D current = position.Value;
            Vec3D previous = lastPosition ?? current;
            lastPosition = current;

            if ((current - previous).Length > TeleportDistance)
            {
                wasInside = Gate.Contains(current);
                for (int i = 0; i < checkpoints.Count; i++) cpInside[i] = checkpoints[i].Contains(current);
                return;
            }

            // Checkpoints: count once per lap, on entry.
            for (int i = 0; i < checkpoints.Count; i++)
            {
                var cp = checkpoints[i];
                bool hit = cp.SegmentIntersects(previous, current);
                if (hit && !cpInside[i] && State == TimerState.Running && !cpHit[i])
                {
                    cpHit[i] = true;
                    cpSplit[i] = now - lapStart;
                    Triggered?.Invoke(LapEventKind.CheckpointHit, i);
                }
                cpInside[i] = cp.Contains(current);
            }

            // Start/finish gate.
            bool gateHit = Gate.SegmentIntersects(previous, current);
            bool insideNow = Gate.Contains(current);

            if (!rearmed && Gate.DistanceTo(current) >= RearmDistance)
                rearmed = true;

            // Trigger on entry: the path touched the box, we weren't already inside it,
            // and we've been well clear of the gate since the last pass.
            // With a separate finish gate, the start gate only starts a race (passing it mid-race does nothing).
            // A race only starts going through the gate the right way (towards the first checkpoint), not backwards.
            if (gateHit && !wasInside && rearmed && now - lastTriggerTime >= MinLapSeconds
                && (FinishGate == null || State != TimerState.Running)
                && (State == TimerState.Running || !ForwardStartOnly || GoingForward(previous, current)))
                Pass(now);

            wasInside = insideNow;

            // Separate finish gate: flying through it ends the race.
            if (FinishGate != null)
            {
                bool finishHit = FinishGate.SegmentIntersects(previous, current);
                if (finishHit && !wasInsideFinish && State == TimerState.Running && now - lastTriggerTime >= MinLapSeconds)
                {
                    lastTriggerTime = now;
                    CompleteLap(now);
                }
                wasInsideFinish = FinishGate.Contains(current);
            }
        }

        /// <summary>
        /// Moving the way the course goes through the start gate: towards the first checkpoint (or the finish gate on
        /// a course without checkpoints), or along the gate's forward if there's neither. Gates fitted to a ring can face
        /// either way, so the course itself decides.
        /// </summary>
        private bool GoingForward(Vec3D previous, Vec3D current)
        {
            Vec3D move = current - previous;
            if (move.Length < 1e-6) return true;
            Vec3D way = Gate.Forward;
            Vec3D? next = checkpoints.Count > 0 ? checkpoints[0].Center : FinishGate?.Center;
            if (next.HasValue)
            {
                Vec3D toNext = next.Value - Gate.Center;
                // Only the part along the gate's axis counts (the next checkpoint may be off to the side).
                double along = Vec3D.Dot(toNext, Gate.Forward);
                if (Math.Abs(along) > 1e-3) way = Gate.Forward * Math.Sign(along);
            }
            return Vec3D.Dot(move, way) > 0;
        }

        private void Pass(double now)
        {
            lastTriggerTime = now;
            rearmed = false;

            if (State != TimerState.Running)
            {
                ClearRace();
                lapStart = now;
                State = TimerState.Running;
                if (PracticeLaps > 0 && FinishGate == null)
                {
                    InPractice = true;
                    Triggered?.Invoke(LapEventKind.PracticeStarted, 0);
                }
                else Triggered?.Invoke(LapEventKind.RaceStarted, 0);
                return;
            }

            int missed = checkpoints.Count - CheckpointsHitThisLap;
            var lap = new LapRecord(now - lapStart, missed, CrashesThisLap,
                missed * PenaltySeconds + CrashesThisLap * CrashPenaltySeconds, cpSplit.ToArray());

            if (InPractice)
            {
                practice.Add(lap);
                lapStart = now;
                ClearCheckpointHits();
                Triggered?.Invoke(LapEventKind.PracticeLapCompleted, lap.Time);
                if (practice.Count >= PracticeLaps)
                {
                    // Practice done: this same pass starts the race.
                    InPractice = false;
                    Triggered?.Invoke(LapEventKind.RaceStarted, 0);
                }
                return;
            }
            AddLap(lap, now);
        }

        /// <summary>Ends the lap being flown now (at the finish gate of a sprint course).</summary>
        private void CompleteLap(double now)
        {
            int missed = checkpoints.Count - CheckpointsHitThisLap;
            var lap = new LapRecord(now - lapStart, missed, CrashesThisLap,
                missed * PenaltySeconds + CrashesThisLap * CrashPenaltySeconds, cpSplit.ToArray());
            AddLap(lap, now);
        }

        private void AddLap(LapRecord lap, double now)
        {
            laps.Add(lap);
            lapStart = now;
            ClearCheckpointHits();
            if (!RaceBestLap.HasValue || lap.Time < RaceBestLap.Value) RaceBestLap = lap.Time;
            Triggered?.Invoke(LapEventKind.LapCompleted, lap.Time);

            if (LapTarget > 0 && laps.Count >= LapTarget)
            {
                LastRaceTotal = CompletedLapsTotal();
                State = TimerState.Finished;
                Triggered?.Invoke(LapEventKind.RaceFinished, LastRaceTotal.Value);
            }
        }

        public static string Format(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        /// <summary>"+1.234" / "-0.456" style difference.</summary>
        public static string FormatDelta(double seconds) =>
            (seconds >= 0 ? "+" : "-") + Math.Abs(seconds).ToString("0.000");
    }
}
