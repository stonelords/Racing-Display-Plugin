using System;
using System.Collections.Generic;

namespace RaceDisplay.Core
{
    /// <summary>
    /// Spots crashes from the ship's speed. Only a loss of SPEED counts (turning changes the velocity's
    /// direction but not its size, so steering at 500 m/s never counts). A crash needs all of:
    ///  - speed at least <see cref="MinSpeed"/> just before,
    ///  - speed falling by at least <see cref="MinSpeedLoss"/> m/s AND at least <see cref="MinSpeedLossFraction"/>
    ///    of the speed, at more than <see cref="MinDeceleration"/> m/s², within <see cref="WindowSeconds"/>,
    ///  - the speed still being that low <see cref="ConfirmSeconds"/> later (so one bad reading can't trigger it).
    /// </summary>
    public sealed class CrashDetector
    {
        public double MinSpeed { get; set; } = 20.0;            // m/s before the impact
        public double MinDeceleration { get; set; } = 60.0;     // m/s² (about 6 g)
        public double MinSpeedLoss { get; set; } = 8.0;         // m/s lost
        public double MinSpeedLossFraction { get; set; } = 0.25; // and at least this share of the speed
        public double CooldownSeconds { get; set; } = 2.0;       // one crash = one penalty
        public double WindowSeconds { get; set; } = 0.25;
        public double ConfirmSeconds { get; set; } = 0.1;

        /// <summary>Speed (m/s) just before the last crash, for the message.</summary>
        public double LastImpactSpeed { get; private set; }

        private readonly Queue<(double time, double speed)> history = new Queue<(double, double)>();
        private double lastCrash = double.NegativeInfinity;

        // A suspected impact waiting to be confirmed.
        private bool pending;
        private double pendingSince, pendingSpeedBefore;

        /// <summary>Call every frame. velocity = null when not flying a ship. Returns true on the frame a crash is confirmed.</summary>
        public bool Update(Vec3D? velocity, double now)
        {
            if (!velocity.HasValue)
            {
                history.Clear();
                pending = false;
                return false;
            }

            double speed = velocity.Value.Length;

            if (pending)
            {
                double required = RequiredLoss(pendingSpeedBefore);
                if (pendingSpeedBefore - speed < required)
                {
                    pending = false; // speed came back: it was a glitch, not a crash
                }
                else if (now - pendingSince >= ConfirmSeconds)
                {
                    pending = false;
                    lastCrash = now;
                    LastImpactSpeed = pendingSpeedBefore;
                    history.Clear();
                    history.Enqueue((now, speed));
                    return true;
                }
            }

            while (history.Count > 0 && now - history.Peek().time > WindowSeconds)
                history.Dequeue();

            if (!pending && now - lastCrash >= CooldownSeconds)
            {
                foreach (var (t, oldSpeed) in history)
                {
                    double dt = now - t;
                    if (dt <= 1e-4 || oldSpeed < MinSpeed) continue;
                    double loss = oldSpeed - speed; // only slowing down counts
                    if (loss >= RequiredLoss(oldSpeed) && loss / dt >= MinDeceleration)
                    {
                        pending = true;
                        pendingSince = now;
                        pendingSpeedBefore = oldSpeed;
                        break;
                    }
                }
            }

            history.Enqueue((now, speed));
            return false;
        }

        private double RequiredLoss(double speedBefore) => Math.Max(MinSpeedLoss, speedBefore * MinSpeedLossFraction);
    }
}
