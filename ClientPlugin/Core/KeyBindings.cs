using System;
using System.Collections.Generic;

namespace RaceDisplay.Core
{
    /// <summary>One shortcut: a key (Windows virtual-key code) plus the exact modifiers held with it.</summary>
    public sealed class KeyBinding
    {
        public int Key { get; set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }

        public KeyBinding() { }
        public KeyBinding(int key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            Key = key; Ctrl = ctrl; Shift = shift; Alt = alt;
        }

        public KeyBinding Clone() => new KeyBinding(Key, Ctrl, Shift, Alt);

        public bool SameAs(KeyBinding o) => o != null && o.Key == Key && o.Ctrl == Ctrl && o.Shift == Shift && o.Alt == Alt;

        public override string ToString()
        {
            if (Key == 0) return "(none)";
            string s = "";
            if (Ctrl) s += "Ctrl+";
            if (Shift) s += "Shift+";
            if (Alt) s += "Alt+";
            return s + KeyNames.Name(Key);
        }
    }

    /// <summary>The plugin's actions: names for the controls screen and default shortcuts.</summary>
    public static class KeyBindings
    {
        // Windows virtual-key codes used by the defaults and the controls screen.
        public const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_INSERT = 0x2D, VK_DELETE = 0x2E;
        public const int VK_B = 0x42, VK_J = 0x4A, VK_K = 0x4B, VK_L = 0x4C, VK_N = 0x4E, VK_R = 0x52, VK_U = 0x55;
        public const int VK_OEM_4 = 0xDB /* [ */, VK_OEM_6 = 0xDD /* ] */;

        /// <summary>Order and names shown in the Racing Display controls menu.</summary>
        public static readonly (GateKey Key, string Name)[] Actions =
        {
            // Racing
            (GateKey.ResetTimer, "Reset race"),
            (GateKey.LapCount, "Race setup (laps, ghost)"),
            (GateKey.ResultsMode, "Results: overall / this ship"),
            (GateKey.ToggleMarkers, "Markers on/off"),
            // Replay
            (GateKey.Replay, "Watch best-lap replay"),
            (GateKey.ReplayCamera, "Replay camera: orbit / chase"),
            // Course edit mode (placing and removing gates and checkpoints is aim + click there)
            (GateKey.EditMode, "Course edit mode on/off"),
            (GateKey.EditWheel, "Edit tool wheel (hold)"),
            (GateKey.Undo, "Undo course edit"),
            (GateKey.Redo, "Redo course edit"),
            (GateKey.ReverseCourse, "Reverse course"),
            (GateKey.Grow, "Make gate bigger"),
            (GateKey.Shrink, "Make gate smaller"),
        };

        /// <summary>Which group an action is listed under in the Controls tab.</summary>
        public static string CategoryOf(GateKey key)
        {
            switch (key)
            {
                case GateKey.Replay:
                case GateKey.ReplayCamera:
                    return "Replay";
                case GateKey.EditMode:
                case GateKey.EditWheel:
                case GateKey.Undo:
                case GateKey.Redo:
                case GateKey.ReverseCourse:
                case GateKey.Grow:
                case GateKey.Shrink:
                    return "Course edit mode";
                default:
                    return "Racing";
            }
        }

        public static KeyBinding Default(GateKey key)
        {
            switch (key)
            {
                case GateKey.GateOnGrid: return new KeyBinding(VK_J, ctrl: true);
                case GateKey.RemoveGate: return new KeyBinding(VK_J, ctrl: true, shift: true);
                case GateKey.Grow: return new KeyBinding(VK_OEM_6, ctrl: true);
                case GateKey.Shrink: return new KeyBinding(VK_OEM_4, ctrl: true);
                case GateKey.AddCheckpointOnGrid: return new KeyBinding(VK_K, ctrl: true);
                case GateKey.RemoveLastCheckpoint: return new KeyBinding(VK_U, ctrl: true);
                case GateKey.ClearCheckpoints: return new KeyBinding(VK_U, ctrl: true, shift: true);
                case GateKey.LapCount: return new KeyBinding(VK_J);
                case GateKey.ResetTimer: return new KeyBinding(VK_R, ctrl: true);
                case GateKey.ClearBest: return new KeyBinding(VK_R, ctrl: true, shift: true);
                case GateKey.ToggleMarkers: return new KeyBinding(VK_N, ctrl: true);
                case GateKey.Replay: return new KeyBinding(VK_L, ctrl: true, shift: true);
                case GateKey.ResultsMode: return new KeyBinding(VK_B, ctrl: true);
                case GateKey.ReplayCamera: return new KeyBinding(0x43 /* C */, ctrl: true, shift: true);
                case GateKey.EditMode: return new KeyBinding(0x46 /* F */, ctrl: true);
                case GateKey.EditWheel: return new KeyBinding(VK_R); // R (held); only does anything in course edit mode
                // Not Ctrl+Z / Ctrl+Y: the game uses those to undo / redo building.
                case GateKey.Undo: return new KeyBinding(VK_U, ctrl: true);
                case GateKey.Redo: return new KeyBinding(VK_U, ctrl: true, shift: true);
                case GateKey.ReverseCourse: return new KeyBinding(0x49 /* I */, ctrl: true, shift: true);
                default: return new KeyBinding();
            }
        }

    }

    /// <summary>Readable names for Windows virtual-key codes.</summary>
    public static class KeyNames
    {
        private static readonly Dictionary<int, string> Special = new Dictionary<int, string>
        {
            { 0x08, "Backspace" }, { 0x09, "Tab" }, { 0x0D, "Enter" }, { 0x13, "Pause" }, { 0x14, "Caps Lock" },
            { 0x1B, "Esc" }, { 0x20, "Space" }, { 0x21, "Page Up" }, { 0x22, "Page Down" }, { 0x23, "End" },
            { 0x24, "Home" }, { 0x25, "Left" }, { 0x26, "Up" }, { 0x27, "Right" }, { 0x28, "Down" },
            { 0x2C, "Print Screen" }, { 0x2D, "Insert" }, { 0x2E, "Delete" },
            { 0x6A, "Num *" }, { 0x6B, "Num +" }, { 0x6D, "Num -" }, { 0x6E, "Num ." }, { 0x6F, "Num /" },
            { 0x90, "Num Lock" }, { 0x91, "Scroll Lock" },
            { 0xBA, ";" }, { 0xBB, "=" }, { 0xBC, "," }, { 0xBD, "-" }, { 0xBE, "." }, { 0xBF, "/" }, { 0xC0, "`" },
            { 0xDB, "[" }, { 0xDC, "\\" }, { 0xDD, "]" }, { 0xDE, "'" }, { 0xE2, "\\" },
        };

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

        /// <summary>The character a punctuation key types on this keyboard layout (so ' and ` show right on UK keyboards).</summary>
        private static string LayoutChar(int vk)
        {
            try
            {
                uint ch = MapVirtualKeyW((uint)vk, 2) & 0x7FFF; // MAPVK_VK_TO_CHAR (high bit = dead key)
                if (ch > 32 && ch < 0xFFFF) return ((char)ch).ToString();
            }
            catch { }
            return null;
        }

        public static string Name(int vk)
        {
            if ((vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF) || vk == 0xE2)
            {
                string c = LayoutChar(vk);
                if (c != null) return c;
            }
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();              // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();              // A-Z
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);               // numpad 0-9
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);              // F1-F24
            return Special.TryGetValue(vk, out var s) ? s : $"Key {vk:X2}";
        }

        /// <summary>Keys that can't be a shortcut's main key (modifiers, mouse buttons, Windows keys).</summary>
        public static bool IsAssignable(int vk) =>
            vk >= 0x08 && !(vk >= 0x10 && vk <= 0x12) && !(vk >= 0xA0 && vk <= 0xA5) && vk != 0x5B && vk != 0x5C && vk != 0x5D;
    }

    /// <summary>
    /// Turns raw key states into "this action was just pressed" using the player's bindings, and runs the
    /// "press a new key" capture for the controls screen. Modifiers must match exactly, so Ctrl+J and
    /// Ctrl+Shift+J are different shortcuts.
    /// </summary>
    public sealed class KeyInput
    {
        private readonly Dictionary<GateKey, bool> wasDown = new Dictionary<GateKey, bool>();
        private readonly HashSet<GateKey> pressed = new HashSet<GateKey>();
        private readonly bool[] keyWasDown = new bool[256];

        public bool Ctrl { get; private set; }
        public bool Shift { get; private set; }
        public bool Alt { get; private set; }

        /// <summary>Main keys (not modifiers) that went down this frame.</summary>
        public readonly List<int> NewKeys = new List<int>();

        public void Update(IGameBridge game, Func<GateKey, KeyBinding> binding)
        {
            pressed.Clear();
            NewKeys.Clear();
            bool focus = game.HasFocus;
            Ctrl = focus && game.IsKeyDown(0x11);
            Shift = focus && game.IsKeyDown(0x10);
            Alt = focus && game.IsKeyDown(0x12);

            for (int vk = 0x08; vk < 256; vk++)
            {
                bool down = focus && game.IsKeyDown(vk);
                if (down && !keyWasDown[vk] && KeyNames.IsAssignable(vk)) NewKeys.Add(vk);
                keyWasDown[vk] = down;
            }

            foreach (GateKey key in Enum.GetValues(typeof(GateKey)))
            {
                var b = binding(key);
                bool down = b != null && b.Key != 0 && keyWasDown[b.Key & 0xFF]
                            && Ctrl == b.Ctrl && Shift == b.Shift && Alt == b.Alt;
                wasDown.TryGetValue(key, out bool before);
                if (down && !before) pressed.Add(key);
                wasDown[key] = down;
            }
        }

        public bool WasPressed(GateKey key) => pressed.Contains(key);

        /// <summary>True while the key combination is held down.</summary>
        public bool IsDown(GateKey key) => wasDown.TryGetValue(key, out bool d) && d;

    }
}
