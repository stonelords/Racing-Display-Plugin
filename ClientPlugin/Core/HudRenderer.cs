using System;

namespace RaceDisplay.Core
{
    /// <summary>
    /// Builds the <see cref="HudModel"/> (what to show) and, as a fallback when the game's real UI isn't
    /// available, draws it with debug draw (dark panels drawn by the game over the text, so text is dimmed).
    /// </summary>
    public sealed class HudRenderer
    {
        private readonly IGameBridge game;
        private readonly GateSettings settings;

        /// <summary>Lap list penalty note: "+10s" with a ring per missed checkpoint and a cross per crash, or in words.</summary>
        private void SetPenalty(HudModel.LapRow row, double penalty, int missed, int crashes)
        {
            row.Note = $"+{penalty:0.#}s";
            if (settings.PenaltyIcons)
            {
                row.MissedIcons = missed;
                row.CrashIcons = crashes;
                return;
            }
            var parts = new System.Collections.Generic.List<string>();
            if (missed > 0) parts.Add($"{missed} missed");
            if (crashes > 0) parts.Add($"{crashes} crash{(crashes == 1 ? "" : "es")}");
            if (parts.Count > 0) row.Note += "  " + string.Join(", ", parts);
        }

        public HudRenderer(IGameBridge game, GateSettings settings)
        {
            this.game = game;
            this.settings = settings;
        }

        /// <summary>"+10s: 1 missed, 1 crash"</summary>
        public static string PenaltyText(LapRecord lap)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (lap.MissedCheckpoints > 0) parts.Add($"{lap.MissedCheckpoints} missed");
            if (lap.Crashes > 0) parts.Add($"{lap.Crashes} crash{(lap.Crashes == 1 ? "" : "es")}");
            return $"+{lap.Penalty:0.#}s: {string.Join(", ", parts)}";
        }

        // =====================================================================================
        // Model
        // =====================================================================================

        public HudModel Build(LapTimer timer, double now, HudModel.Message raceMessage, HudModel.Message centreMessage, RaceView view)
        {
            var model = new HudModel
            {
                Scale = settings.HudScale,
                BackgroundOpacity = settings.UseGameUiOpacity && GameOptions.UiBackgroundOpacity >= 0
                    ? (byte)Math.Round(Math.Clamp(GameOptions.UiBackgroundOpacity, 0, 1) * 255)
                    : settings.BackgroundOpacity,
                RaceMessage = raceMessage,
                CentreMessage = centreMessage,
                Countdown = view?.Countdown,
                CountdownColour = view?.CountdownColour ?? HudColor.White,
                Flash = view?.Flash,
                SwapSides = settings.SwapSides,
                TimerInCorner = settings.TimerInCorner,
                HideGameHud = settings.RaceHudOnly,
                KeepGyro = settings.KeepGyro,
                GyroTargetBased = settings.GyroTargetBased,
            };

            if (timer.Gate == null) return model;

            double? bestLap = view?.Record?.BestLap ?? settings.BestSeconds;
            model.Timer = BuildTimer(timer, now, bestLap);

            // Right of the timer: best whole race on this course at the current lap count.
            var bestRaces = view?.Record?.BestRace;
            if (timer.LapTarget > 0 && bestRaces != null && bestRaces.TryGetValue(timer.LapTarget, out double bestRace))
            {
                model.Timer.BestRace = LapTimer.Format(bestRace);
                model.Timer.BestRaceLabel = $"{timer.LapTarget} LAP{(timer.LapTarget == 1 ? "" : "S")}";
            }
            if (view?.PossibleLap != null && model.Timer.BestLap != null)
                model.Timer.BestLapNote = "POSSIBLE " + LapTimer.Format(view.PossibleLap.Value);
            // Possible race: a whole race of possible laps.
            if (view?.PossibleLap != null && model.Timer.BestRace != null && timer.LapTarget > 0)
                model.Timer.BestRaceNote = "POSSIBLE " + LapTimer.Format(view.PossibleLap.Value * timer.LapTarget);
            if (view != null && view.ShowGo)
            {
                model.Timer.Hint = timer.State == TimerState.Running ? "GO!" : "GO!  Fly through the gate";
                model.Timer.HintColour = HudColor.Green;
            }
            if (timer.State == TimerState.Running)
            {
                model.Timer.Delta = view?.Delta;
                model.Timer.DeltaColour = view?.DeltaColour ?? HudColor.White;
                model.Timer.Dots = BuildDots(timer);
                f1Colours = settings.SectorColours;
                if (settings.SectorColours && model.Timer.Dots != null)
                {
                    model.Timer.DotColours = new HudColor?[model.Timer.Dots.Length];
                    for (int i = 0; i < model.Timer.Dots.Length; i++)
                        if (model.Timer.Dots[i] == HudModel.DotState.Hit) model.Timer.DotColours[i] = LiveSectorColour(timer, view?.Record, i);
                }
                model.Splits = BuildSplits(timer, view?.Record);
            }
            model.Laps = BuildLaps(timer, now);
            if (timer.State == TimerState.Finished)
            {
                model.Results = view?.Results;
                model.Splits = BuildRaceSplits(timer, view?.Record, view?.LapSpeeds, view?.LapStats, null, view);
                if (settings.ShowRaceStats && view?.RaceStats != null && view.RaceStats.Count > 0)
                    model.RaceStats = new System.Collections.Generic.List<(string, string, HudColor)>(view.RaceStats);
            }

            // Panels the player has turned off (Settings tab > Display).
            if (!settings.ShowLapList) model.Laps = null;
            if (!settings.ShowSplits) model.Splits = null;
            if (!settings.ShowBestBoxes) { model.Timer.BestLap = null; model.Timer.BestLapNote = null; model.Timer.BestRace = null; model.Timer.BestRaceNote = null; }
            return model;
        }

        private static HudModel.DotState[] BuildDots(LapTimer timer)
        {
            if (timer.Checkpoints.Count == 0) return null;
            int next = timer.NextCheckpoint;
            var dots = new HudModel.DotState[timer.Checkpoints.Count];
            for (int i = 0; i < dots.Length; i++)
                dots[i] = timer.IsCheckpointHit(i) ? HudModel.DotState.Hit
                        : timer.IsMissed(i) ? HudModel.DotState.Missed
                        : i == next ? HudModel.DotState.Next : HudModel.DotState.Pending;
            return dots;
        }

        /// <summary>
        /// Sector times of a lap: sector k is start to CP 1 (k = 0), CP k to CP k+1, ..., last CP to the finish (k = n).
        /// Null where the checkpoints either side weren't both flown in order. Pass flown = null for a lap still going.
        /// </summary>
        public static double?[] Sectors(double?[] splits, double? flown, int checkpoints)
        {
            var result = new double?[checkpoints + 1];
            for (int k = 0; k <= checkpoints; k++)
            {
                double? start = k == 0 ? 0 : (k - 1 < splits.Length ? splits[k - 1] : null);
                double? end = k == checkpoints ? flown : (k < splits.Length ? splits[k] : null);
                if (start.HasValue && end.HasValue && end.Value > start.Value) result[k] = end.Value - start.Value;
            }
            return result;
        }

        /// <summary>"12.345" under a minute, "01:12.345" otherwise.</summary>
        public static string ShortTime(double seconds) =>
            seconds < 60 ? Math.Max(0, seconds).ToString("0.000") : LapTimer.Format(seconds);

        /// <summary>Fastest time for sector k over the completed race laps, or null.</summary>
        private static double? RaceBestSector(LapTimer timer, int k)
        {
            double? best = null;
            foreach (var lap in timer.Laps)
            {
                var s = Sectors(lap.Splits, lap.Flown, timer.Checkpoints.Count);
                if (k < s.Length && s[k].HasValue && (!best.HasValue || s[k].Value < best.Value)) best = s[k];
            }
            return best;
        }

        /// <summary>Purple = best ever, green = best this race (or better), yellow = slower (white with sector colours off).</summary>
        private static bool f1Colours = true;
        private static HudColor SectorColour(double value, double? ever, double? race)
        {
            if (ever.HasValue && value <= ever.Value + 1e-6) return HudColor.Purple;
            if (!race.HasValue || value <= race.Value + 1e-6) return HudColor.Green;
            return f1Colours ? HudColor.Yellow : HudColor.White;
        }

        /// <summary>Sector colour of checkpoint i's sector this lap (start or previous checkpoint to it), or null.</summary>
        private static HudColor? LiveSectorColour(LapTimer timer, GateSettings.CourseRecord record, int i)
        {
            double? split = timer.SplitThisLap(i);
            double? prev = i == 0 ? 0 : timer.SplitThisLap(i - 1);
            if (!split.HasValue || !prev.HasValue || split.Value <= prev.Value) return null;
            double sector = split.Value - prev.Value;
            double? everSector = record != null && i < record.BestSectors.Count ? record.BestSectors[i] : null;
            return everSector.HasValue && sector < everSector.Value ? HudColor.Purple : SectorColour(sector, null, RaceBestSector(timer, i));
        }

        /// <summary>"-0.412" green (ahead) / "+0.380" red (behind).</summary>
        public static (string Text, HudColor Colour) Gap(double gap) =>
            (LapTimer.FormatDelta(gap), gap <= 0 ? HudColor.Green : HudColor.Red);

        /// <summary>During a race: this lap's time at each checkpoint and the gap to the best lap's split.</summary>
        private static HudModel.SplitsPanel BuildSplits(LapTimer timer, GateSettings.CourseRecord record)
        {
            if (timer.Checkpoints.Count == 0) return null;
            var panel = new HudModel.SplitsPanel();
            panel.Headers.Add("GAP");
            panel.Headers.Add("SECTOR");
            panel.Headers.Add("TIME");
            int next = timer.NextCheckpoint;
            for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                double? split = timer.SplitThisLap(i);
                bool missed = timer.IsMissed(i);
                var colour = split.HasValue ? HudColor.White : missed ? HudColor.Red : i == next ? HudColor.Orange : HudColor.Grey;
                var row = new HudModel.SplitRow { Name = $"CP {i + 1}", NameColour = colour };

                double? best = record != null && i < record.BestLapSplits.Count ? record.BestLapSplits[i] : null;
                if (split.HasValue && best.HasValue)
                    row.Cells.Add(Gap(split.Value - best.Value));
                else
                    row.Cells.Add((missed ? "MISSED" : "", HudColor.Red));

                // Sector: time from the previous checkpoint (or the start) to this one.
                double? prev = i == 0 ? 0 : timer.SplitThisLap(i - 1);
                if (split.HasValue && prev.HasValue && split.Value > prev.Value)
                {
                    double sector = split.Value - prev.Value;
                    var sc = LiveSectorColour(timer, record, i) ?? HudColor.White;
                    row.Cells.Add((ShortTime(sector), sc));
                }
                else row.Cells.Add(("", HudColor.White));
                // Purple: faster than anyone has ever been at this checkpoint on this course.
                double? ever = record != null && i < record.BestSplits.Count ? record.BestSplits[i] : null;
                var timeColour = split.HasValue && ever.HasValue && split.Value < ever.Value ? HudColor.Purple : colour;
                row.Cells.Add((split.HasValue ? LapTimer.Format(split.Value) : "--", timeColour));
                panel.Rows.Add(row);
            }
            return panel;
        }

        public const int MaxSplitColumns = 5;

        /// <summary>
        /// After the finish: every lap's time at each checkpoint (last <see cref="MaxSplitColumns"/> laps), coloured
        /// like F1 timing: purple = best ever on this course, green = best in this race, white = slower.
        /// Missed checkpoints are a red "--". Then a LAP row, and TOP / AVG speed rows.
        /// </summary>
        private HudModel.SplitsPanel BuildRaceSplits(LapTimer timer, GateSettings.CourseRecord record,
            System.Collections.Generic.IReadOnlyList<(double Top, double Avg)> speeds,
            System.Collections.Generic.IReadOnlyList<LapStats> lapStats = null,
            System.Collections.Generic.IReadOnlyList<(string Label, string Value, HudColor Colour)> raceStats = null,
            RaceView view = null)
        {
            var laps = timer.Laps;
            if (laps.Count == 0) return null;
            // Last column: this race's best for the row against the course best before the race (- = gained).
            bool vsBest = view != null && (view.SectorsBefore != null || view.SplitsBefore != null || view.LapBefore.HasValue);
            void AddVs(HudModel.SplitRow row, double? raceBest, double? before)
            {
                if (!vsBest) return;
                if (!raceBest.HasValue || !before.HasValue) { row.Cells.Add(("", HudColor.Grey)); return; }
                double d = raceBest.Value - before.Value;
                row.Cells.Add((Math.Abs(d) < 0.0005 ? "0.000" : LapTimer.FormatDelta(d), d < -0.0005 ? HudColor.Purple : d > 0.0005 ? HudColor.Red : HudColor.White));
            }
            void AddBlank(HudModel.SplitRow row) { if (vsBest) row.Cells.Add(("", HudColor.Grey)); }

            int first = Math.Max(0, laps.Count - MaxSplitColumns);
            bool sectors = settings.SectorTable && timer.Checkpoints.Count > 0;
            string kind = sectors ? "SECTORS" : "SPLITS";
            var panel = new HudModel.SplitsPanel { Title = first > 0 ? $"{kind} (last {MaxSplitColumns} laps)" : kind };
            for (int l = first; l < laps.Count; l++) panel.Headers.Add($"L{l + 1}");
            if (vsBest) panel.Headers.Add("VS BEST");

            if (sectors)
            {
                // Sector times: CP 1 = start to CP 1, ..., FINISH = last checkpoint to the finish.
                int n = timer.Checkpoints.Count;
                var lapSectors = new double?[laps.Count][];
                for (int l = 0; l < laps.Count; l++) lapSectors[l] = Sectors(laps[l].Splits, laps[l].Flown, n);
                for (int k = 0; k <= n; k++)
                {
                    double? raceBest = null;
                    for (int l = first; l < laps.Count; l++)
                        if (lapSectors[l][k].HasValue && (!raceBest.HasValue || lapSectors[l][k].Value < raceBest.Value)) raceBest = lapSectors[l][k];
                    double? ever = record != null && k < record.BestSectors.Count ? record.BestSectors[k] : null;
                    var row = new HudModel.SplitRow { Name = k < n ? $"CP {k + 1}" : "FIN" };
                    for (int l = first; l < laps.Count; l++)
                    {
                        double? v = lapSectors[l][k];
                        if (!v.HasValue) { row.Cells.Add(("--", HudColor.Red)); continue; }
                        var c = ever.HasValue && Math.Abs(v.Value - ever.Value) < 1e-6 ? HudColor.Purple
                              : Math.Abs(v.Value - raceBest.Value) < 1e-9 ? HudColor.Green : HudColor.White;
                        row.Cells.Add((ShortTime(v.Value), c));
                    }
                    double? allBest = null;
                    for (int l = 0; l < laps.Count; l++)
                        if (lapSectors[l][k].HasValue && (!allBest.HasValue || lapSectors[l][k].Value < allBest.Value)) allBest = lapSectors[l][k];
                    AddVs(row, allBest, view?.SectorsBefore != null && k < view.SectorsBefore.Count ? view.SectorsBefore[k] : null);
                    panel.Rows.Add(row);
                }
            }
            else for (int i = 0; i < timer.Checkpoints.Count; i++)
            {
                double? fastest = null;
                for (int l = first; l < laps.Count; l++)
                {
                    double? v = i < laps[l].Splits.Length ? laps[l].Splits[i] : null;
                    if (v.HasValue && (!fastest.HasValue || v.Value < fastest.Value)) fastest = v;
                }
                double? ever = record != null && i < record.BestSplits.Count ? record.BestSplits[i] : null;

                var row = new HudModel.SplitRow { Name = $"CP {i + 1}" };
                for (int l = first; l < laps.Count; l++)
                {
                    double? v = i < laps[l].Splits.Length ? laps[l].Splits[i] : null;
                    if (!v.HasValue) { row.Cells.Add(("--", HudColor.Red)); continue; }
                    var c = ever.HasValue && Math.Abs(v.Value - ever.Value) < 1e-6 ? HudColor.Purple
                          : Math.Abs(v.Value - fastest.Value) < 1e-9 ? HudColor.Green : HudColor.White;
                    row.Cells.Add((LapTimer.Format(v.Value), c));
                }
                double? allBest = null;
                foreach (var lp in laps)
                {
                    double? v = i < lp.Splits.Length ? lp.Splits[i] : null;
                    if (v.HasValue && (!allBest.HasValue || v.Value < allBest.Value)) allBest = v;
                }
                AddVs(row, allBest, view?.SplitsBefore != null && i < view.SplitsBefore.Count ? view.SplitsBefore[i] : null);
                panel.Rows.Add(row);
            }

            // Each lap's time (with penalties): purple = course record lap, green = best this race.
            double bestLap = double.MaxValue;
            for (int l = first; l < laps.Count; l++) bestLap = Math.Min(bestLap, laps[l].Time);
            var lapRow = new HudModel.SplitRow { Name = "LAP", NameColour = HudColor.Yellow };
            for (int l = first; l < laps.Count; l++)
            {
                double t = laps[l].Time;
                var c = record?.BestLap != null && Math.Abs(t - record.BestLap.Value) < 1e-6 ? HudColor.Purple
                      : Math.Abs(t - bestLap) < 1e-9 ? HudColor.Green : HudColor.Yellow;
                lapRow.Cells.Add((LapTimer.Format(t), c));
            }
            double raceBestLap = double.MaxValue;
            foreach (var lp in laps) raceBestLap = Math.Min(raceBestLap, lp.Time);
            AddVs(lapRow, raceBestLap, view?.LapBefore);
            panel.Rows.Add(lapRow);

            // Top and average speed per lap.
            if (speeds != null && speeds.Count >= laps.Count)
            {
                var top = new HudModel.SplitRow { Name = "TOP", NameColour = HudColor.Grey };
                var avg = new HudModel.SplitRow { Name = "AVG", NameColour = HudColor.Grey };
                double bestTop = 0, bestAvg = 0;
                for (int l = first; l < laps.Count; l++) { bestTop = Math.Max(bestTop, speeds[l].Top); bestAvg = Math.Max(bestAvg, speeds[l].Avg); }
                for (int l = first; l < laps.Count; l++)
                {
                    top.Cells.Add((GameOptions.Speed(speeds[l].Top), Math.Abs(speeds[l].Top - bestTop) < 1e-9 ? HudColor.Green : HudColor.White));
                    avg.Cells.Add((GameOptions.Speed(speeds[l].Avg), Math.Abs(speeds[l].Avg - bestAvg) < 1e-9 ? HudColor.Green : HudColor.White));
                }
                AddBlank(top); AddBlank(avg);
                panel.Rows.Add(top);
                panel.Rows.Add(avg);
            }

            // G-forces and distance per lap.
            if (lapStats != null && lapStats.Count >= laps.Count)
            {
                var gMax = new HudModel.SplitRow { Name = "G MAX", NameColour = HudColor.Grey };
                var gTurn = new HudModel.SplitRow { Name = "G AVG", NameColour = HudColor.Grey };
                var brake = new HudModel.SplitRow { Name = "BRAKE", NameColour = HudColor.Grey };
                var dist = new HudModel.SplitRow { Name = "DIST", NameColour = HudColor.Grey };
                double shortest = double.MaxValue;
                for (int l = first; l < laps.Count; l++) if (lapStats[l].Distance > 0) shortest = Math.Min(shortest, lapStats[l].Distance);
                for (int l = first; l < laps.Count; l++)
                {
                    var st = lapStats[l];
                    gMax.Cells.Add(($"{st.MaxTurnG:0.0} g", HudColor.White));
                    gTurn.Cells.Add(($"{st.AvgTurnG:0.0} g", HudColor.White));
                    brake.Cells.Add(($"{st.MaxBrakeG:0.0} g", HudColor.White));
                    dist.Cells.Add((st.Distance >= 1000 ? $"{st.Distance / 1000:0.00} km" : $"{st.Distance:0} m",
                        laps.Count - first > 1 && Math.Abs(st.Distance - shortest) < 1e-6 ? HudColor.Green : HudColor.White));
                }
                AddBlank(gMax); AddBlank(gTurn); AddBlank(brake); AddBlank(dist);
                panel.Rows.Add(gMax);
                panel.Rows.Add(gTurn);
                panel.Rows.Add(brake);
                panel.Rows.Add(dist);
            }

            return panel;
        }

        private static HudModel.TimerPanel BuildTimer(LapTimer timer, double now, double? bestLapEver)
        {
            var p = new HudModel.TimerPanel();
            string laps = timer.LapTarget > 0 ? $" / {timer.LapTarget}" : "";

            switch (timer.State)
            {
                case TimerState.Running when timer.InPractice:
                {
                    int n = timer.CurrentPracticeLap;
                    p.Label = $"PRACTICE {n} / {timer.PracticeLaps}";
                    if (timer.Checkpoints.Count > 0)
                        p.Label += $"   CP {timer.CheckpointsHitThisLap}/{timer.Checkpoints.Count}";
                    p.Big = LapTimer.Format(timer.CurrentLapSeconds(now));
                    p.BigColour = HudColor.Yellow;
                    p.Footer = (n >= timer.PracticeLaps ? "RACE STARTS AT THE GATE" : $"{timer.PracticeLaps - n + 1} PRACTICE LAPS LEFT")
                        + (timer.CurrentLapPenalty > 0 ? $"   PENALTY +{timer.CurrentLapPenalty:0.#}s" : "");
                    break;
                }
                case TimerState.Running:
                    p.Label = $"LAP {timer.CurrentLapNumber}{laps}";
                    if (timer.Checkpoints.Count > 0)
                        p.Label += $"   CP {timer.CheckpointsHitThisLap}/{timer.Checkpoints.Count}";
                    p.Big = LapTimer.Format(timer.CurrentLapSeconds(now));
                    p.Footer = "TOTAL  " + LapTimer.Format(timer.RaceSeconds(now))
                        + (timer.CurrentLapPenalty > 0 ? $"   PENALTY +{timer.CurrentLapPenalty:0.#}s" : "");
                    break;
                case TimerState.Finished:
                    p.Label = "FINISHED";
                    p.Big = LapTimer.Format(timer.RaceSeconds(now));
                    p.BigColour = HudColor.Green;
                    p.Footer = timer.RaceBestLap.HasValue ? "BEST  " + LapTimer.Format(timer.RaceBestLap.Value) : null;
                    p.Hint = "Fly through the gate to race again";
                    break;
                default: // Armed
                    p.Label = timer.LapTarget > 0 ? $"READY - {timer.LapTarget} LAP{(timer.LapTarget == 1 ? "" : "S")}" : "READY - UNLIMITED LAPS";
                    if (timer.PracticeLaps > 0) p.Label += $" + {timer.PracticeLaps} PRACTICE";
                    p.Big = LapTimer.Format(0);
                    p.BigColour = HudColor.Grey;
                    p.Hint = timer.Checkpoints.Count > 0
                        ? $"Fly through the gate to start - {timer.Checkpoints.Count} checkpoint{(timer.Checkpoints.Count == 1 ? "" : "s")}"
                        : "Fly through the gate to start";
                    break;
            }

            if (bestLapEver.HasValue)
                p.BestLap = LapTimer.Format(bestLapEver.Value);
            return p;
        }

        private HudModel.LapsPanel BuildLaps(LapTimer timer, double now)
        {
            // Race laps only: practice laps aren't listed (the timer panel shows practice progress).
            var laps = timer.Laps;
            bool running = timer.State == TimerState.Running && !timer.InPractice;
            if (laps.Count == 0 && !running) return null;

            int maxRows = Math.Max(1, settings.MaxLapRows);
            int totalRows = laps.Count + (running ? 1 : 0);
            int first = Math.Max(0, totalRows - maxRows); // scroll: keep the newest laps

            var panel = new HudModel.LapsPanel { Header = first > 0 ? $"LAPS  ({first} more above)" : "LAPS" };
            double? best = timer.RaceBestLap;

            for (int i = first; i < totalRows; i++)
            {
                bool current = i == laps.Count; // the lap being flown now
                double t = current ? timer.CurrentLapSeconds(now) : laps[i].Time;
                bool penalised = !current && laps[i].Penalty > 0;
                bool isBest = !current && best.HasValue && Math.Abs(t - best.Value) < 1e-9;

                var row = new HudModel.LapRow
                {
                    Name = $"L{i + 1}",
                    Time = LapTimer.Format(t),
                    Colour = current ? HudColor.Grey : isBest ? HudColor.Green : HudColor.White,
                };
                if (penalised)
                {
                    SetPenalty(row, laps[i].Penalty, laps[i].MissedCheckpoints, laps[i].Crashes);
                }
                else if (current && timer.CurrentLapPenalty > 0)
                {
                    // Penalties so far this lap: skipped checkpoints count as soon as they show MISSED in the splits.
                    SetPenalty(row, timer.CurrentLapPenalty, timer.MissedThisLap, timer.CrashesThisLap);
                }
                else if (!current && !isBest && best.HasValue && laps.Count > 1)
                {
                    // Comparison only (not a penalty): grey, and labelled so it can't be mistaken for one.
                    row.Note = LapTimer.FormatDelta(t - best.Value) + " vs best";
                    row.NoteColour = HudColor.Grey;
                }
                panel.Rows.Add(row);
            }
            return panel;
        }

        // =====================================================================================
        // Debug-draw fallback (used only when the game's UI can't be reached)
        // =====================================================================================

        private double S => Math.Max(0.3, (game.ScreenHeight > 0 ? game.ScreenHeight / 1080.0 : 1.0) * settings.HudScale);

        /// <summary>Approximate height in pixels of a line of text drawn at <paramref name="textScale"/>.</summary>
        private double LineHeight(double textScale) => settings.FontHeightPx * textScale * S;

        private void Text(string text, double x, double y, double textScale, HudColor color, TextAlign align) =>
            game.DrawText(text, x, y, textScale * S, color, align);

        private void Panel(double x, double y, double w, double h, byte opacity)
        {
            game.DrawRect(x, y, w, h, HudColor.PanelFill.WithAlpha(opacity));
            game.DrawRectOutline(x, y, w, h, HudColor.PanelBorder);
        }

        private double LapsX => 30 * S;
        private double LapsY => game.ScreenHeight * 0.25;

        public void DrawDebug(HudModel m)
        {
            if (game.ScreenWidth <= 0) return;
            // Debug draw puts the panel OVER the text, so cap the darkness to keep text readable.
            byte opacity = Math.Min(m.BackgroundOpacity, (byte)110);

            if (m.Timer != null) DrawTimerDebug(m.Timer, opacity);
            if (m.Laps != null) DrawLapsDebug(m.Laps, opacity);
            if (m.RaceMessage != null) DrawRaceMessageDebug(m.RaceMessage, opacity);
            if (m.CentreMessage != null) DrawCentreMessageDebug(m.CentreMessage, opacity);
            if (m.Countdown != null)
                Text(m.Countdown, game.ScreenWidth / 2.0, game.ScreenHeight * 0.4, 5.0, m.CountdownColour, TextAlign.Center);
            // (Splits, results, dots and fades are only drawn by the game-UI HUD.)
        }

        private void DrawTimerDebug(HudModel.TimerPanel p, byte opacity)
        {
            double s = S, pad = 14 * s;
            const double labelScale = 0.9, bigScale = 2.2, footScale = 0.9, bestScale = 1.15, hintScale = 1.05;

            double w = 340 * s;
            double h = pad + LineHeight(labelScale) + 4 * s + LineHeight(bigScale) + pad
                       + (p.Footer != null ? 6 * s + LineHeight(footScale) : 0);
            double x = (game.ScreenWidth - w) / 2;
            double y = 30 * s;
            double mid = x + w / 2;
            Panel(x, y, w, h, opacity);

            double ty = y + pad;
            Text(p.Label, mid, ty, labelScale, HudColor.White, TextAlign.Center);
            ty += LineHeight(labelScale) + 4 * s;
            Text(p.Big, mid, ty, bigScale, p.BigColour, TextAlign.Center);
            ty += LineHeight(bigScale) + 6 * s;
            if (p.Footer != null) Text(p.Footer, mid, ty, footScale, HudColor.White, TextAlign.Center);
            if (p.Delta != null) Text(p.Delta, x + w - pad, y + pad, labelScale, p.DeltaColour, TextAlign.Right);

            double below = y + h + 8 * s;
            if (p.BestLap != null)
            {
                Text("BEST LAP", x - 16 * s, y + pad, labelScale, HudColor.Grey, TextAlign.Right);
                Text(p.BestLap, x - 16 * s, y + pad + LineHeight(labelScale) + 4 * s, bestScale, HudColor.Yellow, TextAlign.Right);
            }
            if (p.BestRace != null)
            {
                Text("BEST RACE  " + p.BestRaceLabel, x + w + 16 * s, y + pad, labelScale, HudColor.Grey, TextAlign.Left);
                Text(p.BestRace, x + w + 16 * s, y + pad + LineHeight(labelScale) + 4 * s, bestScale, HudColor.Yellow, TextAlign.Left);
            }
            if (p.Hint != null) Text(p.Hint, mid, below, hintScale, p.HintColour, TextAlign.Center);
        }

        private void DrawLapsDebug(HudModel.LapsPanel p, byte opacity)
        {
            double s = S, pad = 14 * s;
            const double headScale = 0.9, rowScale = 0.85;
            double row = LineHeight(rowScale) + 4 * s;

            double w = 420 * s;
            double h = pad + LineHeight(headScale) + 8 * s + p.Rows.Count * row + pad - 4 * s;
            double x = LapsX, y = LapsY;
            double left = x + pad, right = x + w - pad, noteX = x + w * 0.17;

            Panel(x, y, w, h, opacity);
            Text(p.Header, right, y + pad, headScale, HudColor.White, TextAlign.Right);

            double ty = y + pad + LineHeight(headScale) + 8 * s;
            foreach (var r in p.Rows)
            {
                Text(r.Name, left, ty, rowScale, r.Colour, TextAlign.Left);
                Text(r.Time, right, ty, rowScale, r.Colour, TextAlign.Right);
                if (r.Note != null) Text(r.Note, noteX, ty, rowScale * 0.8, r.NoteColour, TextAlign.Left);
                ty += row;
            }
        }

        private void DrawRaceMessageDebug(HudModel.Message msg, byte opacity)
        {
            double s = S, pad = 10 * s;
            const double scale = 0.8;
            double w = Math.Min(game.ScreenWidth * 0.5, msg.Text.Length * LineHeight(scale) * 0.52 + 2 * pad);
            double h = LineHeight(scale) + 2 * pad;
            double x = LapsX, y = LapsY - h - 8 * s;
            Panel(x, y, w, h, opacity);
            Text(msg.Text, x + pad, y + pad, scale, msg.Colour, TextAlign.Left);
        }

        private void DrawCentreMessageDebug(HudModel.Message msg, byte opacity)
        {
            double s = S, pad = 10 * s;
            const double scale = 0.8;
            double w = Math.Min(game.ScreenWidth * 0.9, msg.Text.Length * LineHeight(scale) * 0.52 + 2 * pad);
            double h = LineHeight(scale) + 2 * pad;
            double x = (game.ScreenWidth - w) / 2, y = game.ScreenHeight * 0.30;
            Panel(x, y, w, h, opacity);
            Text(msg.Text, x + w / 2, y + pad, scale, msg.Colour, TextAlign.Center);
        }
    }
}
