using System;
using System.Collections.Generic;
using Avalonia;

namespace RaceDisplay
{
    /// <summary>
    /// Finds a spot for a map label that doesn't cover anything already placed (checkpoint dots and their labels,
    /// you, other labels): tries positions off to one side of the route, then the other, further and further out.
    /// </summary>
    internal static class LabelPlacer
    {
        /// <param name="at">The point on the route the label belongs to.</param>
        /// <param name="normal">Which side to try first (unit length; e.g. away from the middle of the map).</param>
        /// <param name="fits">Whether a spot is allowed at all (on the map).</param>
        public static Rect Place(Point at, Vector normal, double w, double h, List<Rect> taken, Func<Rect, bool> fits, double step)
        {
            var tangent = new Vector(-normal.Y, normal.X);
            Rect At(Vector offset) => new Rect(at.X + offset.X - w / 2, at.Y + offset.Y - h / 2, w, h);
            bool Free(Rect r)
            {
                if (!fits(r)) return false;
                foreach (var t in taken) if (t.Intersects(r)) return false;
                return true;
            }
            // Clear of the route itself: at least half the label's size plus a gap, out to the side.
            double clear = Math.Abs(normal.X) * w / 2 + Math.Abs(normal.Y) * h / 2 + step;
            for (int ring = 0; ring < 6; ring++)
            {
                double d = clear + ring * step * 1.5;
                foreach (var side in new[] { 1.0, -1.0 })
                    foreach (var along in new[] { 0.0, 1.0, -1.0, 2.0, -2.0 })
                    {
                        var r = At(normal * d * side + tangent * along * w * 0.35);
                        if (Free(r)) { taken.Add(r); return r; }
                    }
            }
            var fallback = At(normal * clear);
            taken.Add(fallback);
            return fallback;
        }

        /// <summary>The vector from b to a (Avalonia's Point minus Point gives a Point).</summary>
        public static Vector Sub(Point a, Point b) => new Vector(a.X - b.X, a.Y - b.Y);

        /// <summary>The point halfway along a polyline, and the direction across it there (unit length).</summary>
        public static (Point Mid, Vector Normal) Halfway(IList<Point> pts, Point centre)
        {
            double total = 0;
            for (int i = 1; i < pts.Count; i++) total += Sub(pts[i], pts[i - 1]).Length;
            double half = total / 2, run = 0;
            Point mid = pts[0];
            Vector dir = pts.Count > 1 ? Sub(pts[1], pts[0]) : new Vector(1, 0);
            for (int i = 1; i < pts.Count; i++)
            {
                var seg = Sub(pts[i], pts[i - 1]);
                double len = seg.Length;
                if (run + len >= half && len > 0)
                {
                    mid = pts[i - 1] + seg * ((half - run) / len);
                    dir = seg;
                    break;
                }
                run += len;
            }
            double dl = dir.Length;
            var n = dl > 1e-6 ? new Vector(-dir.Y / dl, dir.X / dl) : new Vector(0, -1);
            // Outwards, away from the middle of the map, first.
            if ((mid.X - centre.X) * n.X + (mid.Y - centre.Y) * n.Y < 0) n = -n;
            return (mid, n);
        }
    }
}
