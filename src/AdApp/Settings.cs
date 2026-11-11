// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.IO;
using System.Text.Json;

namespace AdApp;

public sealed class Settings
{
    public List<string> Recent { get; set; } = new();
    public List<string> Session { get; set; } = new();
    /// <summary>User groups and files added to projects in the last session.</summary>
    public List<AdCore.Project.SavedGroup> Groups { get; set; } = new();
    /// <summary>Document tabs of the last session in tab order (null: not recorded yet — open everything), the tabs
    /// in front in each pane, the active one, and whether the view was split.</summary>
    public List<string> OpenDocs { get; set; }
    public List<string> ShownDocs { get; set; } = new();
    public string ActiveDocPath { get; set; }
    public bool SessionSplit { get; set; }
    public bool CrossProbeJump { get; set; } = true;
    public bool AutoSplit { get; set; }
    /// <summary>Settings layout version (2: split view off by default; 3: point finger 75 % by default).</summary>
    public int SchemaVersion { get; set; }
    public bool UnitsMm { get; set; } = true;
    public string SchTheme { get; set; } = "Altium";
    public double FontScale { get; set; } = 1.0;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1600;
    public double Height { get; set; } = 980;
    public bool Maximized { get; set; }

    // 檔案 → 設定
    public double PanelFontSize { get; set; } = 12.5;
    public double PropTitleSize { get; set; } = 24;
    public double PropBodySize { get; set; } = 16;
    public bool AltNetSchToPcb { get; set; } = true;
    public bool AltNetPcbToSch { get; set; } = true;
    public bool SchShowParams { get; set; } = true;
    public double MeasFontSize { get; set; } = 13;
    public string MeasColor { get; set; } = "#FFE27A";
    /// <summary>Cross Probe from the schematic: also light the part(s) up on the PCB, the rest dimmed (not the other way).</summary>
    public bool ProbeLightPart { get; set; }
    /// <summary>匯出 PDF: open the file when it is written.</summary>
    public bool PdfOpenAfter { get; set; } = true;
    /// <summary>指示 (point finger): size of the hands and the cursor, 0.1–3 (10–300 %); 75 % unless changed (2.9.4).</summary>
    public double FingerScale { get; set; } = 0.75;
    /// <summary>PCB selection filter (class names); null = everything.</summary>
    public List<string> PickFilter { get; set; }

    /// <summary>Docking panels as the user left them: auto-hide on/off and sizes.</summary>
    public List<PanelState> Panels { get; set; } = new();

    // 3D lighting
    public double LightIntensity { get; set; } = 1.0;
    public double LightAmbient { get; set; } = 0.35;
    public double LightAzimuth { get; set; } = 225;
    public double LightElevation { get; set; } = 55;

    // kept from the Altium Reader 2 days so settings survive the rename
    public static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltiumReader2");
    /// <summary>Tests: --settings &lt;folder&gt; keeps them away from the real settings.</summary>
    public static string DirOverride;
    static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>The settings the running window uses (views read display options from here).</summary>
    public static Settings Current { get; private set; } = new();

    // read and written by code generated at build time (SettingsJson below), not by reflection: no JSON setup or JIT
    // at start-up, where settings.json is read before the window is built
    public static Settings Load()
    {
        try { Current = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new(); } catch { Current = new(); }
        if (Current.SchemaVersion < 2) { Current.AutoSplit = false; Current.SchemaVersion = 2; }   // 2.2: split view starts off
        // 2.9.4: the hands start at 75 %; 2.9.2–2.9.3 wrote their 100 % default into every settings.json, so that one
        // value is taken as "not chosen" (once)
        if (Current.SchemaVersion < 3) { if (Current.FingerScale == 1.0) Current.FingerScale = 0.75; Current.SchemaVersion = 3; }
        return Current;
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.Settings)); } catch { }
    }

    public void AddRecent(IEnumerable<string> paths)
    {
        foreach (var p in paths) { Recent.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)); Recent.Insert(0, p); }
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
    }
}

/// <summary>settings.json as before (property names as they are, indented); a window position not set yet (NaN) is
/// written as "NaN" instead of failing the whole save.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : System.Text.Json.Serialization.JsonSerializerContext { }

/// <summary>One docking panel (ContentId): docked width / height (0 = not set by the user) and the size of its
/// auto-hide flyout.</summary>
public sealed class PanelState
{
    public string Id { get; set; } = "";
    public bool AutoHidden { get; set; }
    public double DockWidth { get; set; }
    public double DockHeight { get; set; }
    public double AutoHideWidth { get; set; }
    public double AutoHideHeight { get; set; }
    /// <summary>Closed by the user (檢視 → 面板 opens it again); null = not recorded (before 2.7.1): the default,
    /// which closes Navigator and Messages. Properties is left out: it steps aside by itself for a PCB alone.</summary>
    public bool? Closed { get; set; }
}
