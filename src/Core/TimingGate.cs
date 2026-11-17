using System;

namespace RaceDisplay.Core
{
    /// <summary>
    /// An oriented box in world space. The box is placed facing the way the ship was
    /// pointing, so its Forward axis is the direction you fly through it.
    /// </summary>
    public sealed class TimingGate
    {
        public Vec3D Center { get; }
        public Vec3D Right { get; }
        public Vec3D Up { get; }
        public Vec3D Forward { get; }

        /// <summary>Half sizes along Right (width), Up (height) and Forward (depth), in metres.</summary>
        public double HalfWidth { get; }
        public double HalfHeight { get; }
        public double HalfDepth { get; }

        /// <summary>Placed in open air (no grid round it), so the plugin draws it for you to see.</summary>
        public bool Air { get; }
        /// <summary>A ring (round or oval, Width across and Height tall) instead of a box: only the inside of the ring counts.</summary>
        public bool Round { get; }

        public TimingGate(Vec3D center, Vec3D forward, Vec3D up, double width, double height, double depth, bool air = false, bool round = false)
        {
            Air = air;
            Round = round;
            Center = center;
            Forward = forward.Normalized();
            // Re-orthogonalise so the axes are exactly perpendicular even if the game's up vector is slightly off.
            Right = Vec3D.Cross(Forward, up).Normalized();
            if (Right.Length < 0.5)
                Right = Vec3D.Cross(Forward, Math.Abs(Forward.Y) < 0.9 ? new Vec3D(0, 1, 0) : new Vec3D(1, 0, 0)).Normalized();
            Up = Vec3D.Cross(Right, Forward).Normalized();

            HalfWidth = Math.Max(0.5, width / 2);
            HalfHeight = Math.Max(0.5, height / 2);
            HalfDepth = Math.Max(0.5, depth / 2);
        }

        public double Width => HalfWidth * 2;
        public double Height => HalfHeight * 2;
        public double Depth => HalfDepth * 2;

        public TimingGate Resized(double factor) =>
            new TimingGate(Center, Forward, Up, Width * factor, Height * factor, Depth * factor, Air, Round);

        /// <summary>The same gate, flown through the other way.</summary>
        public TimingGate Turned() => new TimingGate(Center, Forward * -1, Up, Width, Height, Depth, Air, Round);

        /// <summary>
        /// The gate's rim, as a closed loop of points: a ring for a round gate (steps points), the four corners for a
        /// square one. along = where on the way through (-1 = the side you fly in, 1 = the side you leave); scale
        /// shrinks it towards the middle.
        /// </summary>
        public System.Collections.Generic.List<Vec3D> Rim(double along, double scale, int steps = 48)
        {
            var points = new System.Collections.Generic.List<Vec3D>();
            Vec3D mid = Center + Forward * (along * HalfDepth);
            if (Round)
            {
                for (int i = 0; i < steps; i++)
                {
                    double turn = i * 2 * Math.PI / steps;
                    points.Add(mid + Right * (Math.Cos(turn) * HalfWidth * scale) + Up * (Math.Sin(turn) * HalfHeight * scale));
                }
            }
            else
            {
                points.Add(mid + Right * (HalfWidth * scale) + Up * (HalfHeight * scale));
                points.Add(mid - Right * (HalfWidth * scale) + Up * (HalfHeight * scale));
                points.Add(mid - Right * (HalfWidth * scale) - Up * (HalfHeight * scale));
                points.Add(mid + Right * (HalfWidth * scale) - Up * (HalfHeight * scale));
            }
            return points;
        }

        /// <summary>Converts a world point into the box's local frame (x = right, y = up, z = forward).</summary>
        private Vec3D ToLocal(Vec3D world)
        {
            Vec3D d = world - Center;
            return new Vec3D(Vec3D.Dot(d, Right), Vec3D.Dot(d, Up), Vec3D.Dot(d, Forward));
        }

        /// <summary>Distance in metres from a point to the box's surface (0 when inside).</summary>
        public double DistanceTo(Vec3D world)
        {
            Vec3D p = ToLocal(world);
            double dx = Math.Max(0, Math.Abs(p.X) - HalfWidth);
            double dy = Math.Max(0, Math.Abs(p.Y) - HalfHeight);
            double dz = Math.Max(0, Math.Abs(p.Z) - HalfDepth);
            if (Round)
            {
                // (How far outside the ring, measured out from its middle.)
                double out1 = Math.Sqrt(p.X * p.X / (HalfWidth * HalfWidth) + p.Y * p.Y / (HalfHeight * HalfHeight));
                double dr = out1 > 1 ? Math.Sqrt(p.X * p.X + p.Y * p.Y) * (1 - 1 / out1) : 0;
                return Math.Sqrt(dr * dr + dz * dz);
            }
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public bool Contains(Vec3D world)
        {
            Vec3D p = ToLocal(world);
            if (Round && p.X * p.X / (HalfWidth * HalfWidth) + p.Y * p.Y / (HalfHeight * HalfHeight) > 1) return false;
            return Math.Abs(p.X) <= HalfWidth && Math.Abs(p.Y) <= HalfHeight && Math.Abs(p.Z) <= HalfDepth;
        }

        /// <summary>
        /// True if the straight line from <paramref name="from"/> to <paramref name="to"/> touches the box.
        /// Testing the path between frames (not just the current position) means a fast ship that
        /// jumps clean over a thin box in a single tick is still detected.
        /// </summary>
        public bool SegmentIntersects(Vec3D from, Vec3D to)
        {
            Vec3D a = ToLocal(from);
            Vec3D b = ToLocal(to);
            Vec3D d = b - a;

            double tMin = 0.0, tMax = 1.0;
            if (Round)
            {
                // A ring: the part of the path between its two faces has to pass inside the ring.
                if (!Slab(a.Z, d.Z, HalfDepth, ref tMin, ref tMax)) return false;
                double ax = a.X / HalfWidth, ay = a.Y / HalfHeight, mx = d.X / HalfWidth, my = d.Y / HalfHeight;
                double qa = mx * mx + my * my, qb = 2 * (ax * mx + ay * my), qc = ax * ax + ay * ay - 1;
                if (qa < 1e-12) return qc <= 0;   // (not moving across it: inside the ring already, or not)
                double under = qb * qb - 4 * qa * qc;
                if (under < 0) return false;
                double root = Math.Sqrt(under);
                double inAt = (-qb - root) / (2 * qa), outAt = (-qb + root) / (2 * qa);
                return Math.Max(inAt, tMin) <= Math.Min(outAt, tMax);
            }
            return Slab(a.X, d.X, HalfWidth, ref tMin, ref tMax)
                && Slab(a.Y, d.Y, HalfHeight, ref tMin, ref tMax)
                && Slab(a.Z, d.Z, HalfDepth, ref tMin, ref tMax);
        }

        private static bool Slab(double start, double dir, double half, ref double tMin, ref double tMax)
        {
            if (Math.Abs(dir) < 1e-12)
                return Math.Abs(start) <= half; // moving parallel to this slab: must already be within it

            double t1 = (-half - start) / dir;
            double t2 = (half - start) / dir;
            if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }

            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            return tMin <= tMax;
        }

        /// <summary>The 8 corners, for drawing the outline.</summary>
        public Vec3D[] GetCorners()
        {
            var corners = new Vec3D[8];
            int i = 0;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
                corners[i++] = Center + Right * (x * HalfWidth) + Up * (y * HalfHeight) + Forward * (z * HalfDepth);
            return corners;
        }

        /// <summary>Pairs of corner indexes forming the 12 edges (indexes into <see cref="GetCorners"/>).</summary>
        public static readonly int[,] Edges =
        {
            { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, // along Forward
            { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, // along Up
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }, // along Right
        };
    }
}
