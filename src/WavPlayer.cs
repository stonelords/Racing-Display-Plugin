using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RaceDisplay
{
    /// <summary>
    /// Plays a short .wav (built into the plugin, or a replacement dropped in the plugin's data folder) through Windows,
    /// at a set volume. Used for sounds the game doesn't have, like the penalty melody.
    /// Only one of these plays at a time (a new one cuts the last one off); the game's own sounds are not affected.
    /// </summary>
    internal static class WavPlayer
    {
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;
        private const int MaxBytes = 8 * 1024 * 1024;   // a replacement file bigger than this is ignored

        private sealed class Clip { public byte[] Original; public int DataStart, DataLength; public bool Pcm16; }
        private static readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);

        // The buffer Windows is playing from. Unmanaged, so the garbage collector can't move it mid-play.
        private static IntPtr playing;
        private static string playingKey;

        /// <summary>Plays the named sound (e.g. "Penalty") at volume 0..1. Never throws.</summary>
        public static void Play(string name, double volume)
        {
            try
            {
                volume = Math.Clamp(volume, 0, 1);
                if (volume <= 0.001) return;
                var clip = Load(name);
                if (clip == null) return;

                string key = name + "|" + Math.Round(volume * 100);
                if (key != playingKey)
                {
                    // Stop whatever is playing before freeing the buffer it plays from.
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                    if (playing != IntPtr.Zero) { Marshal.FreeHGlobal(playing); playing = IntPtr.Zero; }

                    byte[] bytes = (byte[])clip.Original.Clone();
                    if (clip.Pcm16 && volume < 0.999)
                    {
                        int end = Math.Min(bytes.Length - 1, clip.DataStart + clip.DataLength - 1);
                        for (int i = clip.DataStart; i < end; i += 2)
                        {
                            int v = (short)(bytes[i] | (bytes[i + 1] << 8));
                            v = (int)Math.Round(v * volume);
                            bytes[i] = (byte)v; bytes[i + 1] = (byte)(v >> 8);
                        }
                    }
                    playing = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, playing, bytes.Length);
                    playingKey = key;
                }
                PlaySound(playing, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
            catch (Exception e) { PluginLog.Info("Couldn't play " + name + ".wav: " + e.Message); }
        }

        /// <summary>Forgets loaded sounds, so a replaced file in the data folder is picked up next time.</summary>
        public static void Reload() { clips.Clear(); playingKey = null; }

        private static Clip Load(string name)
        {
            if (clips.TryGetValue(name, out var have)) return have;
            byte[] bytes = null;

            // 1. A replacement in the plugin's data folder (e.g. %AppData%\SpaceEngineers2\AppData\RaceDisplay\Penalty.wav).
            try
            {
                string file = Path.Combine(PluginPaths.Folder, name + ".wav");
                if (File.Exists(file))
                {
                    if (new FileInfo(file).Length <= MaxBytes) bytes = File.ReadAllBytes(file);
                    else PluginLog.Info($"{file} is over {MaxBytes / 1024 / 1024} MB: using the built-in sound");
                }
            }
            catch (Exception e) { PluginLog.Info("Couldn't read " + name + ".wav: " + e.Message); }

            // 2. The one built into the plugin.
            if (bytes == null)
            {
                using var s = typeof(WavPlayer).Assembly.GetManifestResourceStream("RaceDisplay.Sounds." + name + ".wav");
                if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            }

            Clip clip = bytes == null ? null : Parse(bytes);
            if (bytes != null && clip == null) PluginLog.Info(name + ".wav isn't a .wav file Windows can play");
            clips[name] = clip;
            return clip;
        }

        /// <summary>Finds the format and the sample data in a RIFF/WAVE file. Null if it isn't one.</summary>
        private static Clip Parse(byte[] b)
        {
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return null;
            var clip = new Clip { Original = b };
            int format = 0, bits = 0;
            int at = 12;
            while (at + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
                int size = BitConverter.ToInt32(b, at + 4);
                if (size < 0) return null;
                int body = at + 8;
                if (id == "fmt " && body + 16 <= b.Length)
                {
                    format = BitConverter.ToUInt16(b, body);
                    bits = BitConverter.ToUInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    clip.DataStart = body;
                    clip.DataLength = Math.Min(size, b.Length - body);
                    break;
                }
                at = body + size + (size & 1);
            }
            if (clip.DataLength <= 0) return null;
            clip.Pcm16 = format == 1 && bits == 16;   // other formats play, just always at full volume
            return clip;
        }
    }
}
