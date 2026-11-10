// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AdApp;

/// <summary>The user guide: Help/index.html and its images, embedded in the program (so the portable exe has it too).
/// Written out to the settings folder, one folder per version, and opened in the default browser.</summary>
static class HelpDoc
{
    const string Prefix = "Help/";

    /// <summary>Writes the guide out (again: a deleted or older copy is replaced) and returns index.html.</summary>
    public static string Extract()
    {
        var asm = typeof(HelpDoc).Assembly;
        var ver = asm.GetName().Version?.ToString(3) ?? "0";
        var root = Path.Combine(Settings.Dir, "help");
        var dir = Path.Combine(root, ver);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix)))
        {
            var rel = name[Prefix.Length..].Replace('\\', '/');
            var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var s = asm.GetManifestResourceStream(name)!;
            if (rel == "index.html") File.WriteAllText(path, new StreamReader(s, Encoding.UTF8).ReadToEnd().Replace("{{VERSION}}", ver), new UTF8Encoding(false));
            else { using var f = File.Create(path); s.CopyTo(f); }
        }
        // guides of earlier versions are left over after an update
        foreach (var old in Directory.GetDirectories(root).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
            try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Path.Combine(dir, "index.html");
    }

    public static void Open() => Process.Start(new ProcessStartInfo(Extract()) { UseShellExecute = true });

    /// <summary>A text file written out with the guide, opened in the default editor: THIRD-PARTY-NOTICES.txt (the
    /// licences of the third-party components), NOTICE.txt (the program's licence notice) or LICENSE.txt (GNU GPL v3).</summary>
    public static void OpenText(string name) => Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Extract())!, name)) { UseShellExecute = true });
}
