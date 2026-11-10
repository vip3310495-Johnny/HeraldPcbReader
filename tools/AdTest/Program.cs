// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

// AdTest <exe> <outdir> <scenario> <path>
// Drives Herald PCB Reader with real mouse / keyboard input (SendInput) and checks state through the automation port.
var exe = args[0]; var outDir = args[1]; var scenario = args[2]; var path = args[3];
Directory.CreateDirectory(outDir);
const int Port = 8765;
// the automation port only answers requests that carry the token it was started with (a new random one each run)
var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.Add("X-Automation-Token", token);
var p = Process.Start(new ProcessStartInfo(exe, $"--automation-port {Port} --automation-token {token} \"{path}\"") { UseShellExecute = false });
int fails = 0;
void Log(string s) { Console.WriteLine(s); File.AppendAllText(Path.Combine(outDir, "log.txt"), s + "\n"); }
async Task<JsonNode> Get(string q) { for (int i = 0; ; i++) { try { return JsonNode.Parse(await http.GetStringAsync(q)); } catch when (i < 60) { await Task.Delay(500); } } }
async Task Shot(string name) { await Get("shot?path=" + Uri.EscapeDataString(Path.Combine(outDir, name + ".png"))); }
void Check(bool ok, string what) { Log((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

async Task Click(double x, double y, bool alt = false, bool ctrl = false, bool dbl = false)
{
    Native.SetForegroundWindow(p.MainWindowHandle);
    await Task.Delay(150);
    Native.SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
    await Task.Delay(120);
    if (alt) Native.Key(0x12, true);
    if (ctrl) Native.Key(0x11, true);
    await Task.Delay(60);
    for (int i = 0; i < (dbl ? 2 : 1); i++) { Native.Mouse(0x0002); await Task.Delay(40); Native.Mouse(0x0004); await Task.Delay(60); }
    await Task.Delay(60);
    if (ctrl) Native.Key(0x11, false);
    if (alt) Native.Key(0x12, false);
    await Task.Delay(700);
}

JsonNode OpenDoc(JsonNode st, string name) => st["open"].AsArray().FirstOrDefault(d => (string)d["name"] == name);

try
{
    JsonNode st = null;
    for (int i = 0; i < 120; i++) { st = await Get("state"); if (st["open"].AsArray().Count > 0) break; await Task.Delay(500); }
    await Task.Delay(1500);
    if (scenario != "smoke") { await Get("do?cmd=split"); await Task.Delay(800); }   // split view is off by default since 2.2
    st = await Get("state");
    Log("STATE " + st.ToJsonString());
    await Shot("01-open");
    var sch = st["open"].AsArray().Where(d => d["sch"] != null && (bool)d["selected"]).Select(d => (string)d["name"]).FirstOrDefault();
    var pcb = st["open"].AsArray().Where(d => d["pcb"] != null && (bool)d["selected"]).Select(d => (string)d["name"]).FirstOrDefault();
    if (scenario == "smoke") { Check(st["open"].AsArray().Count > 0 && st["groups"].AsArray().SelectMany(g => g["docs"].AsArray()).All(d => d["Error"] == null), "開啟成功、沒有解析錯誤"); }
    else { Check((bool)st["split"], "分割檢視");
    Check(sch != null && pcb != null, $"左右同時看得到電路圖 {sch} 與 PCB {pcb}"); }
    if (scenario == "basic" && sch != null && pcb != null)
    {
        var refName = args.Length > 4 ? args[4] : "U1";
        var netName = args.Length > 5 ? args[5] : null;
        // 1. SCH component → PCB
        var pt = await Get($"point?doc={Uri.EscapeDataString(sch)}&ref={refName}");
        Log("sch point " + pt.ToJsonString());
        if (pt["x"] != null)
        {
            await Click((double)pt["x"], (double)pt["y"]);
            st = await Get("state");
            Check((string)OpenDoc(st, sch)["sch"]["sel"]?["Ref"] == refName, $"電路圖點 {refName} 被選取");
            Check((string)OpenDoc(st, pcb)["pcb"]["sel"] == "comp:" + refName, $"PCB 同步選取 {refName}");
            await Shot("02-sch-comp");
        }
        // 2. Alt+click net → PCB highlight
        if (netName != null)
        {
            pt = await Get($"point?doc={Uri.EscapeDataString(sch)}&net={Uri.EscapeDataString(netName)}");
            Log("net point " + pt.ToJsonString());
            if (pt["x"] != null)
            {
                await Click((double)pt["x"], (double)pt["y"], alt: true);
                st = await Get("state");
                var sel = OpenDoc(st, sch)["sch"]["sel"];
                Check(sel != null && (bool)sel["IsNet"] && (bool)sel["Mask"], $"Alt+點選 網路 {netName} → 只看此網路（{sel?["Net"]}）");
                var hl = OpenDoc(st, pcb)["pcb"]["hlNets"].AsArray().Select(x => (string)x).ToList();
                Check(hl.Count == 1, $"PCB 同步點亮 NET：{string.Join(",", hl)}");
                Log("probe: " + st["probe"]);
                await Shot("03-alt-net");
            }
        }
        // 3. PCB component → SCH
        var other = args.Length > 6 ? args[6] : null;
        if (other != null)
        {
            await Get($"do?cmd=zoomto&doc={Uri.EscapeDataString(pcb)}&ref={other}"); await Task.Delay(400);
            pt = await Get($"point?doc={Uri.EscapeDataString(pcb)}&ref={other}");
            Log("pcb point " + pt.ToJsonString());
            if (pt["x"] != null)
            {
                await Click((double)pt["x"], (double)pt["y"]);
                st = await Get("state");
                Log("pcb sel: " + OpenDoc(st, pcb)["pcb"]["sel"]);
                Check(((string)OpenDoc(st, pcb)["pcb"]["sel"] ?? "") != "", $"PCB 點 {other}（元件或其焊墊）被選取");
                var schWith = st["open"].AsArray().FirstOrDefault(d => d["sch"] != null && (bool)d["selected"]);
                Check((string)schWith?["sch"]["sel"]?["Ref"] == other, $"電路圖同步跳到 {other}（{schWith?["name"]}）");
                Log("probe: " + st["probe"]);
                await Shot("04-pcb-comp");
            }
        }
        // 4. 3D
        await Get($"do?cmd=pcb3d&doc={Uri.EscapeDataString(pcb)}&on=1");
        await Task.Delay(2500);
        await Shot("05-3d");
        await Get($"do?cmd=pcb3d&doc={Uri.EscapeDataString(pcb)}&on=0");
    }
}
catch (Exception e) { Log("ERROR " + e); fails++; }
finally { try { p.Kill(); } catch { } }
Log(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
    public static void Mouse(uint flags) => mouse_event(flags, 0, 0, 0, IntPtr.Zero);
    public static void Key(byte vk, bool down) => keybd_event(vk, 0, down ? 0u : 2u, IntPtr.Zero);
}
