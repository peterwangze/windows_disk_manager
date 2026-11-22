using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UsageTree
{
    // A user-specified data directory may already contain unrelated files. Reclaim only
    // tagged run directories and the scanner's known SQLite filenames. Never traverse links.
    internal static class ScanStaging
    {
        private const string Prefix = ".wdm-run-";
        private const string Marker = "owner.wdm";
        private static readonly Regex TempName = new Regex(@"^\.scan-[a-z0-9_]{8}$", RegexOptions.CultureInvariant);
        private static readonly Regex Shard = new Regex(@"^(report|volume-[0-9]+|branch-[0-9]+-[0-9]+)\.sqlite(-journal|-wal|-shm)?$", RegexOptions.CultureInvariant);

        private static string Signature(string id) { return "WindowsDiskManager staging v1 " + id; }
        internal static string Create(string parent)
        {
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(parent, Prefix + id);
            Directory.CreateDirectory(directory);
            using (var file = new StreamWriter(new FileStream(Path.Combine(directory, Marker), FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                file.Write(Signature(id));
            return directory;
        }

        internal static void CleanAbandoned(string parent)
        {
            try
            {
                foreach (string directory in Directory.GetDirectories(parent, Prefix + "*")) Clean(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        internal static void Clean(string directory)
        {
            try
            {
                string name = Path.GetFileName(directory);
                Guid guid;
                if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(Prefix.Length), "N", out guid)) return;
                if (!Directory.Exists(directory) || Link(directory)) return;
                string marker = Path.Combine(directory, Marker);
                if (!File.Exists(marker) || Link(marker) || new FileInfo(marker).Length > 128 ||
                    File.ReadAllText(marker) != Signature(guid.ToString("N"))) return;
                var files = new List<string>();
                var subdirs = new List<string>();
                foreach (string entry in Directory.GetFileSystemEntries(directory))
                {
                    if (Link(entry)) return;
                    string leaf = Path.GetFileName(entry);
                    if (leaf == Marker) continue;
                    if (!Directory.Exists(entry))
                    {
                        if (leaf != "cancel" && leaf != "snapshot.sqlite" && leaf != "snapshot.sqlite.previous") return;
                        if (leaf == "snapshot.sqlite.previous" && !File.Exists(Path.Combine(Path.GetDirectoryName(directory), "disk-usage.sqlite"))) return;
                        files.Add(entry);
                        continue;
                    }
                    if (!TempName.IsMatch(leaf)) return;
                    subdirs.Add(entry);
                    foreach (string shard in Directory.GetFileSystemEntries(entry))
                    {
                        if (Link(shard) || Directory.Exists(shard) || !Shard.IsMatch(Path.GetFileName(shard))) return;
                        files.Add(shard);
                    }
                }
                foreach (string file in files) File.Delete(file);
                foreach (string subdir in subdirs) Directory.Delete(subdir, false);
                File.Delete(marker);
                Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static bool Link(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
    }
}
