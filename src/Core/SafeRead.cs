using System;

namespace RaceDisplay.Core
{
    /// <summary>Reading files other people may have made (shared races, ghosts, cinematics, courses, classes): too big = refused.</summary>
    internal static class SafeRead
    {
        public const long MaxBytes = 20L * 1024 * 1024;

        public static string Text(string path, long maxBytes = MaxBytes)
        {
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
                throw new System.IO.InvalidDataException($"{info.Name} is too big to read ({info.Length / 1048576.0:0.#} MB; the most is {maxBytes / 1048576} MB)");
            return System.IO.File.ReadAllText(path);
        }
    }
}
