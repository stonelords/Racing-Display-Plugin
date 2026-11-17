using System;
using System.Collections.Generic;

namespace RaceDisplay.Core
{
    /// <summary>
    /// Finds the hole in a ring/arch/frame grid, so a checkpoint covers the opening you fly through instead
    /// of the whole structure (legs, supports and all).
    ///
    /// Works from the blocks' cell boxes (one box per block), so it's fast even for huge grids:
    /// 1. The grid's thinnest direction is taken as the way you fly through it.
    /// 2. Every block is drawn as a filled rectangle on a small 2D map of the other two directions
    ///    (at most <see cref="MaxMapSize"/> pixels across; one pixel may cover several cells).
    /// 3. Empty pixels reachable from outside are flood-filled (4-way, so diagonal block edges don't leak).
    /// 4. Empty pixels that can't be reached are enclosed: the opening. The biggest enclosed area wins.
    /// Legs or posts enclose nothing, so they're ignored. Returns null if nothing is enclosed.
    /// </summary>
    public static class OpeningFinder
    {
        public const int MaxMapSize = 400;

        /// <summary>A block's cells: Min inclusive, Max exclusive, on all three axes.</summary>
        public readonly struct CellBox
        {
            public readonly int[] Min, Max;
            public CellBox(int[] min, int[] maxExclusive) { Min = min; Max = maxExclusive; }
        }

        public sealed class Result
        {
            /// <summary>The opening in cells: Min inclusive, Max exclusive. Thin axis = the ring's full depth.</summary>
            public int[] Min = new int[3];
            public int[] Max = new int[3];
            public int ThinAxis;
            public int OpeningPixels;
            public int MapWidth, MapHeight, CellsPerPixel;
        }

        public static Result Find(IReadOnlyList<CellBox> blocks)
        {
            if (blocks == null || blocks.Count == 0) return null;

            int[] lo = { int.MaxValue, int.MaxValue, int.MaxValue };
            int[] hi = { int.MinValue, int.MinValue, int.MinValue }; // exclusive
            foreach (var b in blocks)
                for (int a = 0; a < 3; a++)
                {
                    lo[a] = Math.Min(lo[a], b.Min[a]);
                    hi[a] = Math.Max(hi[a], Math.Max(b.Max[a], b.Min[a] + 1));
                }

            int[] ext = { hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2] };
            int thin = 0;
            for (int a = 1; a < 3; a++) if (ext[a] < ext[thin]) thin = a;
            int u = thin == 0 ? 1 : 0;
            int v = thin == 2 ? 1 : 2;

            int res = Math.Max(1, (int)Math.Ceiling(Math.Max(ext[u], ext[v]) / (double)MaxMapSize));
            // +2: a one-pixel empty border all round, so "outside" is connected.
            int w = (ext[u] + res - 1) / res + 2;
            int h = (ext[v] + res - 1) / res + 2;

            var filled = new bool[w, h];
            foreach (var b in blocks)
            {
                int bMaxU = Math.Max(b.Max[u], b.Min[u] + 1), bMaxV = Math.Max(b.Max[v], b.Min[v] + 1);
                int i0 = (b.Min[u] - lo[u]) / res + 1, i1 = (bMaxU - 1 - lo[u]) / res + 1;
                int j0 = (b.Min[v] - lo[v]) / res + 1, j1 = (bMaxV - 1 - lo[v]) / res + 1;
                for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    filled[i, j] = true;
            }

            var outside = new bool[w, h];
            var queue = new Queue<(int, int)>();
            outside[0, 0] = true;
            queue.Enqueue((0, 0));
            while (queue.Count > 0)
            {
                var (a, b) = queue.Dequeue();
                foreach (var (da, db) in Steps)
                {
                    int c = a + da, d = b + db;
                    if (c < 0 || d < 0 || c >= w || d >= h || filled[c, d] || outside[c, d]) continue;
                    outside[c, d] = true;
                    queue.Enqueue((c, d));
                }
            }

            // Largest enclosed empty region.
            var seen = new bool[w, h];
            int bestCount = 0, bMinI = 0, bMaxI = 0, bMinJ = 0, bMaxJ = 0;
            for (int i = 0; i < w; i++)
            for (int j = 0; j < h; j++)
            {
                if (filled[i, j] || outside[i, j] || seen[i, j]) continue;
                int count = 0, minI = i, maxI = i, minJ = j, maxJ = j;
                seen[i, j] = true;
                queue.Enqueue((i, j));
                while (queue.Count > 0)
                {
                    var (a, b) = queue.Dequeue();
                    count++;
                    if (a < minI) minI = a; if (a > maxI) maxI = a;
                    if (b < minJ) minJ = b; if (b > maxJ) maxJ = b;
                    foreach (var (da, db) in Steps)
                    {
                        int c = a + da, d = b + db;
                        if (c < 0 || d < 0 || c >= w || d >= h || filled[c, d] || outside[c, d] || seen[c, d]) continue;
                        seen[c, d] = true;
                        queue.Enqueue((c, d));
                    }
                }
                if (count > bestCount) { bestCount = count; bMinI = minI; bMaxI = maxI; bMinJ = minJ; bMaxJ = maxJ; }
            }
            if (bestCount == 0) return null;

            var r = new Result { ThinAxis = thin, OpeningPixels = bestCount, MapWidth = w, MapHeight = h, CellsPerPixel = res };
            // Pixel p covers cells [lo + (p-1)*res, lo + p*res).
            r.Min[u] = lo[u] + (bMinI - 1) * res; r.Max[u] = Math.Min(hi[u], lo[u] + bMaxI * res);
            r.Min[v] = lo[v] + (bMinJ - 1) * res; r.Max[v] = Math.Min(hi[v], lo[v] + bMaxJ * res);
            r.Min[thin] = lo[thin]; r.Max[thin] = hi[thin];
            return r;
        }

        private static readonly (int, int)[] Steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    }
}
