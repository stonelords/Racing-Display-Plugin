using System.Collections.Generic;
using Avalonia.Media;
using RaceDisplay.Core;

namespace RaceDisplay
{
    /// <summary>
    /// One brush per colour, made once and reused (UI thread only). Giving a control a new, if identical, brush
    /// makes it redraw, so the HUD and maps take their brushes from here.
    /// </summary>
    internal static class UiBrushes
    {
        private static readonly Dictionary<uint, IBrush> cache = new Dictionary<uint, IBrush>();

        public static IBrush Get(byte a, byte r, byte g, byte b)
        {
            uint key = (uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b;
            if (!cache.TryGetValue(key, out var brush)) cache[key] = brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            return brush;
        }

        public static IBrush Of(HudColor c) => Get(c.A, c.R, c.G, c.B);
    }
}
