using System;
using System.Collections.Generic;
using System.IO;

namespace TapRelay
{
    internal static class SuyanAppLocator
    {
        internal const string RootOverrideVariable = "TAPRELAY_SUYAN_ROOT";

        internal static bool IsInstalled()
        {
            foreach (var root in FindRoots())
            {
                if (File.Exists(Path.Combine(root, "release", "win-unpacked", "素言.exe")) ||
                    File.Exists(Path.Combine(root, "release", "win-unpacked", "suyan.exe")) ||
                    File.Exists(Path.Combine(root, "素言.exe")) ||
                    File.Exists(Path.Combine(root, "suyan.exe")))
                {
                    return true;
                }
            }

            return false;
        }

        internal static IEnumerable<string> FindRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(roots, Environment.GetEnvironmentVariable(RootOverrideVariable));
            // Keep the current development checkout discoverable while the app is not installed.
            Add(roots, @"W:\提示词");
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "suyan"));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "suyan"));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "suyan"));

            foreach (var root in roots)
            {
                if (!string.IsNullOrWhiteSpace(root)) yield return root;
            }
        }

        private static void Add(HashSet<string> roots, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { roots.Add(Path.GetFullPath(path.Trim().Trim('\"'))); }
            catch { }
        }
    }
}
