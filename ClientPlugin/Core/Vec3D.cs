using System;

namespace RaceDisplay.Core
{
    /// <summary>
    /// Minimal double-precision vector. World coordinates in Space Engineers can be
    /// millions of metres from the origin, so float (System.Numerics) is not precise enough.
    /// Kept independent of game types so the core logic has no game dependency.
    /// </summary>
    public readonly struct Vec3D
    {
        public readonly double X, Y, Z;

        public Vec3D(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3D Zero = new Vec3D(0, 0, 0);

        public static Vec3D operator +(Vec3D a, Vec3D b) => new Vec3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3D operator -(Vec3D a, Vec3D b) => new Vec3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3D operator *(Vec3D a, double s) => new Vec3D(a.X * s, a.Y * s, a.Z * s);
        public static Vec3D operator *(double s, Vec3D a) => a * s;

        public static double Dot(Vec3D a, Vec3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3D Cross(Vec3D a, Vec3D b) => new Vec3D(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vec3D Normalized()
        {
            double len = Length;
            return len < 1e-9 ? Zero : this * (1.0 / len);
        }

        public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
    }
}
