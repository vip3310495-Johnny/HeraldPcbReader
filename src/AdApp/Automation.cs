// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdApp.Views;
using AdCore.Project;
using AvalonDock.Layout;

namespace AdApp;

/// <summary>Test hook (only with --automation-port N --automation-token T): read state, get on-screen points, take
/// screenshots. Real mouse input is sent by the test driver, so clicks travel through the whole WPF input stack.
/// Only on 127.0.0.1, and every request must carry the token of this run in an X-Automation-Token header: a web page
/// cannot send that header to another site, so a browser on this PC cannot drive the program (cross-site requests,
/// DNS rebinding). Without a token the hook does not start.</summary>
static class Automation
{
    public static void Start(MainWindow win, int port, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 16) return;
        var expect = Encoding.UTF8.GetBytes(token);
        var l = new HttpListener();
        l.Prefixes.Add($"http://127.0.0.1:{port}/");
        l.Start();
        new Thread(() =>
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); } catch { break; }
                string body;
                var got = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Automation-Token"] ?? "");
                bool hostOk = ctx.Request.Url is { } u && u.Host == "127.0.0.1" && u.Port == port;
                if (!hostOk || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(got, expect))
                {
                    try { ctx.Response.StatusCode = 403; ctx.Response.Close(); } catch { }
                    continue;
                }
                try { body = win.Dispatcher.Invoke(() => Handle(win, ctx.Request)); }
                catch (Exception e) { body = JsonSerializer.Serialize(new { error = e.Message }); }
                var bytes = Encoding.UTF8.GetBytes(body);
                try
                {
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                }
                catch { }   // the driver went away, or the window is closing (/do?cmd=close)
            }
        }) { IsBackground = true }.Start();
    }

    static string _fps;   // the last frame-rate test

    static string Handle(MainWindow w, HttpListenerRequest r)
    {
        var q = r.QueryString;
        switch (r.Url.AbsolutePath)
        {
            case "/state":
                return JsonSerializer.Serialize(new
                {
                    status = w.LastStatus, probe = w.LastProbe, split = w.Split, mm = w.UnitsMm, active = w.ActiveDoc?.Name,
                    openTiming = w.LastOpenTiming, marks = string.Join(" · ", App.Marks), cpu = string.Join(" · ", App.CpuMarks), asms = q["asms"] == "1" ? string.Join(",", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Order()) : null, jitAll = $"{System.Runtime.JitInfo.GetCompiledMethodCount()}/{System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:0}",
                    flyoutFx = w.Flyout == null ? null : new { sliding = w.Flyout.Sliding, closeMs = w.Flyout.LastCloseMs, trace = w.Flyout.Trace, shown = w.Flyout.WindowShown },
                    panels = w.Dock.Layout.Descendents().OfType<LayoutAnchorable>().Select(a => new
                    {
                        id = a.ContentId, a.IsVisible, a.IsAutoHidden, a.AutoHideWidth, a.AutoHideHeight,
                        width = (a.Content as FrameworkElement)?.ActualWidth, height = (a.Content as FrameworkElement)?.ActualHeight,
                        flyout = w.Dock.AutoHideWindow?.Model == a && w.Dock.AutoHideWindow.Visibility == Visibility.Visible,
                    }),
                    groups = w.Ws.AllGroups.Select(g => new { g.Name, kind = g.Kind.ToString(), docs = g.Docs.Select(d => new { d.Name, kind = d.Kind.ToString(), d.Missing, d.Error }), pnets = g.Compiled?.PNets.Count }),
                    open = w.DocsOpen.Select(kv => new
                    {
                        name = kv.Key.Name, selected = kv.Value.IsSelected, pane = kv.Value.Parent is AvalonDock.Layout.LayoutDocumentPane p ? w.Dock.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocumentPane>().ToList().IndexOf(p) : -1,
                        sch = kv.Value.Content is SchView sv ? new { sheet = sv.SheetIndex, view = sv.ViewKey, sel = sv.Sel == null ? null : new { sv.Sel.IsNet, sv.Sel.Ref, sv.Sel.Net, sv.Sel.Mask, pnet = sv.Sel.PNet?.Name } } : null,
                        pcb = kv.Value.Content is PcbView pv ? new
                        {
                            sel = pv.St.Sel == null ? null : pv.St.Sel.IsComp ? "comp:" + pv.Doc.Pcb.Comps[pv.St.Sel.Index].Des : pv.St.Sel.Items != null ? "multi:" + pv.St.Sel.Items.Count : $"obj:{pv.Scene.Objs[pv.St.Sel.Index].Kind}:{pv.Doc.Pcb.LayerName(pv.Scene.Objs[pv.St.Sel.Index].L)}:{pv.Doc.Pcb.NetName(pv.Scene.Objs[pv.St.Sel.Index].Net)}:comp{pv.Scene.Objs[pv.St.Sel.Index].Comp}",
                            hlNets = pv.St.HlNets.Select(pv.Doc.Pcb.NetName).ToList(), hlComps = pv.St.HlComps.Select(i => pv.Doc.Pcb.Comps[i].Des).ToList(), mode3d = pv.Is3D, view = $"{pv.St.Cx:0.#},{pv.St.Cy:0.#},{pv.St.S:0.####}",
                            paint = new { pv.Paints, pv.MaxPaintMs, pv.SyncRenders, pv.AsyncRenders, pv.FramePaints },
                            meas = new { a = pv.St.Meas.A?.ToString(), b = (pv.St.Meas.B ?? pv.St.Meas.Live)?.ToString(), snap = pv.St.Meas.SnapKind, info = pv.St.Meas.SnapInfo, pv.St.Meas.AInfo, pv.St.Meas.BInfo, ring = pv.CenterCursorShown, card = pv.Card.Text(), framePaints = pv.FramePaints, pv.Paints },
                        } : null,
                    }),
                });
            case "/point":
                {
                    var doc = w.DocsOpen.Keys.FirstOrDefault(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                    if (doc == null) return "{\"error\":\"doc not open\"}";
                    var v = w.ViewOf(doc);
                    Point? p = v switch
                    {
                        SchView sv when q["ref"] != null => sv.ScreenPointOfComp(q["ref"]),
                        SchView sv when q["net"] != null => sv.ScreenPointOfNet(q["net"]),
                        PcbView pv when q["ref"] != null => pv.ScreenPointOfComp(q["ref"]),
                        PcbView pv when q["net"] != null => pv.ScreenPointOfNet(q["net"]),
                        _ => null,
                    };
                    if (p == null || !v.IsVisible) return "{\"error\":\"not found or not visible\"}";
                    var s = v.PointToScreen(p.Value);
                    return JsonSerializer.Serialize(new { x = s.X, y = s.Y });
                }
            case "/click":
                {
                    var doc = w.DocsOpen.Keys.FirstOrDefault(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                    var v = w.ViewOf(doc); var mods = q["mods"] ?? "";
                    Point? p = v switch
                    {
                        SchView sv when q["ref"] != null => sv.ScreenPointOfComp(q["ref"]),
                        SchView sv when q["net"] != null => sv.ScreenPointOfNet(q["net"]),
                        PcbView pv when q["ref"] != null => pv.ScreenPointOfComp(q["ref"]),
                        PcbView pv when q["net"] != null => pv.ScreenPointOfNet(q["net"]),
                        _ => null,
                    };
                    if (p == null) return "{\"error\":\"not found\"}";
                    // a click starts the double-click time as the button going down would (a plain click's jump to the PCB waits it out)
                    if (v is SchView s2 && q["dbl"] == "1") { s2.Focus(); s2.NoteDown(); s2.ClickAt(p.Value, false); s2.DoubleClickAt(p.Value); }
                    else if (v is SchView s2b) { s2b.NoteDown(); s2b.ClickAt(p.Value, mods.Contains("alt") || mods.Contains("ctrl")); }
                    else if (v is PcbView p2) p2.ClickAtView(p.Value, (mods.Contains("alt") ? System.Windows.Input.ModifierKeys.Alt : 0) | (mods.Contains("ctrl") ? System.Windows.Input.ModifierKeys.Control : 0));
                    return "{\"ok\":true}";
                }
            case "/meas":
                {
                    var doc = w.DocsOpen.Keys.FirstOrDefault(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                    if (w.ViewOf(doc) is not PcbView pv) return "{\"error\":\"not a pcb\"}";
                    var mods = q["mods"] ?? "";
                    Point? p = q["pad"] != null ? pv.ScreenPointOfPad(q["ref"], q["pad"]) : q["ref"] != null ? pv.ScreenPointOfComp(q["ref"]) : q["net"] != null ? pv.ScreenPointOfNet(q["net"]) : null;
                    if (p == null) return "{\"error\":\"not found\"}";
                    var pt = new Point(p.Value.X + double.Parse(q["dx"] ?? "0"), p.Value.Y + double.Parse(q["dy"] ?? "0"));
                    pv.MeasureAtView(pt, (mods.Contains("shift") ? System.Windows.Input.ModifierKeys.Shift : 0) | (mods.Contains("ctrl") ? System.Windows.Input.ModifierKeys.Control : 0), q["click"] == "1");
                    return "{\"ok\":true}";
                }
            case "/box":
                {
                    var doc = w.DocsOpen.Keys.FirstOrDefault(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                    if (w.ViewOf(doc) is SchView sv) sv.DragSelect(new Point(double.Parse(q["x0"]), double.Parse(q["y0"])), new Point(double.Parse(q["x1"]), double.Parse(q["y1"])));
                    return "{\"ok\":true}";
                }
            case "/cfg":
                {
                    var prop = typeof(Settings).GetProperty(q["key"]);
                    if (prop == null) return "{\"error\":\"no such setting\"}";
                    prop.SetValue(Settings.Current, Convert.ChangeType(q["val"], prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture)); w.ApplyDisplaySettings();
                    return "{\"ok\":true}";
                }
            case "/parts":
                {
                    var doc = w.DocsOpen.Keys.FirstOrDefault(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                    return w.ViewOf(doc) is SchView sv ? JsonSerializer.Serialize(sv.Doc.Sch.PartMap.Where(kv => kv.Value.Sheets.Contains(sv.SheetIndex)).Select(kv => kv.Key).Take(40)) : "[]";
                }
            case "/open": _ = w.Open(new[] { q["path"] }, q["expand"] == "1"); return "{\"ok\":true}";
            case "/do":
                switch (q["cmd"])
                {
                    case "split": w.SetSplit(true, w.ActiveDoc?.Group); break;
                    case "unsplit": w.SetSplit(false); break;
                    case "activate": w.ActivateDoc(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))); break;
                    case "fit": { var fv = w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))); if (fv is PcbView fp) fp.Fit(); else if (fv is SchView fs) fs.Fit(); break; }
                    case "zoomto": { if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView zp) { int i = zp.Doc.Pcb.FindComp(q["ref"]); if (i >= 0 && zp.Scene.CompBox(i) is { } b) { zp.St.Fit((float)zp.ActualWidth, (float)zp.ActualHeight - 28, new SkiaSharp.SKRect(b.Left - 80, b.Top - 80, b.Right + 80, b.Bottom + 80)); zp.Invalidate(); } } break; }
                    case "pcb3d": if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView pv) pv.Set3D(q["on"] != "0"); break;
                    case "pcbsel": if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView ps) ps.SelectByDes(q["ref"], true); break;
                    case "pcbnet": if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView pn) pn.HighlightNet(pn.Doc.Pcb.Nets.IndexOf(q["net"])); break;
                    case "wheel": if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView pw) pw.WheelAt(pw.CanvasCenter, int.Parse(q["delta"])); break;
                    case "paintreset": foreach (var x in w.DocsOpen.Values.Select(l => l.Content).OfType<PcbView>()) { x.MaxPaintMs = 0; x.Paints = x.SyncRenders = x.AsyncRenders = 0; } break;
                    case "settings": w.Dispatcher.BeginInvoke(new Action(w.ShowSettings)); break;
                    case "closetop": w.OwnedWindows.Cast<Window>().LastOrDefault()?.Close(); break;
                    case "search": w.Dispatcher.BeginInvoke(new Action(() => w.OpenSearchWindowWith(q["text"]))); break;
                    case "unitclick": w.ClickUnitSwitch(q["mm"] != "0"); break;
                    case "size":w.WindowState = WindowState.Normal; w.Width = double.Parse(q["w"]); w.Height = double.Parse(q["h"]); break;
                    case "autohide": { var a = w.Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(x => x.ContentId == q["panel"]); if (a.IsAutoHidden != (q["on"] != "0")) a.ToggleAutoHide(); break; }
                    case "close": w.Dispatcher.BeginInvoke(new Action(w.Close)); break;
                    case "group": w.GroupDocs(q["docs"].Split(','), q["name"]); break;
                    case "panelwidth": w.SetPanelWidth(q["panel"], double.Parse(q["w"])); break;
                    case "flyout": w.Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(x => x.ContentId == q["panel"]).IsActive = true; break;
                    case "focus": w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase)))?.Focus(); break;   // what a click on the canvas does first
                    case "fps":   // pan the view of doc a few pixels every frame for ms; frame times go to fpsresult
                        {
                            var fv = w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase)));
                            int ms = int.Parse(q["ms"] ?? "3000"); var times = new List<double>(); var t0 = DateTime.Now; var last = TimeSpan.Zero; int k = 0;
                            _fps = null;
                            EventHandler tick = null;
                            tick = (_, a) =>
                            {
                                var rt = ((System.Windows.Media.RenderingEventArgs)a).RenderingTime;
                                if (rt == last) return; if (last != TimeSpan.Zero) times.Add((rt - last).TotalMilliseconds); last = rt;
                                double dx = Math.Sin(k * 0.05) * 12, dy = Math.Cos(k * 0.037) * 8; k++;
                                if (fv is PcbView fp) { fp.St.PanBy(dx, dy); fp.Invalidate(); } else if (fv is SchView fs) fs.PanBy(dx, dy);
                                if ((DateTime.Now - t0).TotalMilliseconds > ms)
                                {
                                    System.Windows.Media.CompositionTarget.Rendering -= tick;
                                    var s = times.OrderBy(x => x).ToList();
                                    _fps = s.Count == 0 ? "no frames" : $"frames {s.Count} in {ms} ms = {1000.0 * s.Count / ms:0.0} fps, avg {s.Average():0.0} ms, p95 {s[(int)(s.Count * 0.95)]:0.0} ms, max {s[^1]:0.0} ms, over 20 ms: {s.Count(x => x > 20)}"
                                        + (fv is PcbView pp ? $", paint max {pp.MaxPaintMs:0.0} ms" : "");
                                }
                            };
                            if (fv is PcbView p0) { p0.MaxPaintMs = 0; }
                            System.Windows.Media.CompositionTarget.Rendering += tick;
                            break;
                        }
                    case "measfps":   // measure tool: first point at the view centre, then the cursor circles around it every frame for ms (mods=ctrl,shift)
                        {
                            var mv = (PcbView)w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase)));
                            int ms = int.Parse(q["ms"] ?? "3000"); var mm = q["mods"] ?? ""; bool still = q["still"] == "1";   // still: the cursor stays put (a baseline: no card or measure updates)
                            var mods = (mm.Contains("shift") ? System.Windows.Input.ModifierKeys.Shift : 0) | (mm.Contains("ctrl") ? System.Windows.Input.ModifierKeys.Control : 0);
                            mv.Card.Collapsed = q["fold"] == "1";
                            if (q["selbig"] == "1" && mv.Doc.Pcb.Pads.Where(p => p.Comp >= 0).GroupBy(p => p.Comp).MaxBy(g => g.Count()) is { } big)
                                mv.SelectByDes(mv.Doc.Pcb.Comps[big.Key].Des, false);   // the part with the most pads: the longest info card
                            var c0 = new Point(mv.ActualWidth / 2, mv.ActualHeight / 2); double rad = Math.Min(mv.ActualWidth, mv.ActualHeight) * 0.3;
                            mv.MeasureAtView(c0, 0, false); mv.MeasureAtView(c0, 0, true);
                            var times = new List<double>(); var hover = new List<double>(); var layout = new List<double>(); var t0 = DateTime.Now; var last = TimeSpan.Zero; int k = 0;
                            _fps = null; mv.MaxPaintMs = 0; mv.Paints = mv.FramePaints = 0; mv.MaxFramePaintMs = 0; int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                            EventHandler tick = null;
                            tick = (_, a) =>
                            {
                                var rt = ((System.Windows.Media.RenderingEventArgs)a).RenderingTime;
                                if (rt == last) return; if (last != TimeSpan.Zero) times.Add((rt - last).TotalMilliseconds); last = rt;
                                double ang = still ? 0 : k * 0.021, r = rad * (still ? 0.5 : 0.35 + 0.65 * Math.Abs(Math.Sin(k * 0.013))); k++;
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                mv.MeasureAtView(new Point(c0.X + Math.Cos(ang) * r, c0.Y + Math.Sin(ang) * r), mods, false);
                                hover.Add(sw.Elapsed.TotalMilliseconds); sw.Restart();
                                w.UpdateLayout(); layout.Add(sw.Elapsed.TotalMilliseconds);
                                if ((DateTime.Now - t0).TotalMilliseconds > ms)
                                {
                                    System.Windows.Media.CompositionTarget.Rendering -= tick;
                                    string St(List<double> l) { var s = l.OrderBy(x => x).ToList(); return s.Count == 0 ? "-" : $"avg {s.Average():0.00} p95 {s[(int)(s.Count * 0.95)]:0.00} max {s[^1]:0.00} (worst at step {string.Join(",", l.Select((x, i) => (x, i)).OrderByDescending(t => t.x).Take(3).Select(t => $"{t.i}:{t.x:0}"))})"; }
                                    _fps = $"frames {times.Count} in {ms} ms = {1000.0 * times.Count / ms:0.0} fps; frame {St(times)}; hover {St(hover)}; layout {St(layout)}; paints {mv.Paints} (frame kept {mv.FramePaints}, max {mv.MaxFramePaintMs:0.0} ms), paint max {mv.MaxPaintMs:0.0} ms; GC gen0/1/2 {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}";
                                }
                            };
                            System.Windows.Media.CompositionTarget.Rendering += tick;
                            break;
                        }
                    case "fpsresult": return JsonSerializer.Serialize(new { result = _fps, renderTier = System.Windows.Media.RenderCapability.Tier >> 16 });
                    case "showpanel": { var sp = w.Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(x => x.ContentId == q["panel"]); sp.Show(); sp.IsActive = true; break; }   // 檢視 → 面板
                    case "hidepanel": w.Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(x => x.ContentId == q["panel"]).Hide(); break;
                    case "styletabs": foreach (var st in Descend(w.Dock).OfType<System.Windows.Controls.TabItem>().ToList()) MainWindow.StyleDocTab(st); break;
                    case "tabtree":   // the visual tree of the first document tab (TabItem down), to style the tabs
                        {
                            var tab = Descend(w.Dock).OfType<System.Windows.Controls.TabItem>().FirstOrDefault(t => Descend(t).OfType<AvalonDock.Controls.LayoutDocumentTabItem>().Any());
                            if (tab == null) return "{\"error\":\"no tab\"}";
                            var sb = new StringBuilder();
                            void Dump(DependencyObject d, int depth)
                            {
                                var fe = d as FrameworkElement;
                                sb.Append(new string(' ', depth * 2)).Append(d.GetType().Name);
                                if (fe != null)
                                {
                                    if (!string.IsNullOrEmpty(fe.Name)) sb.Append(" #").Append(fe.Name);
                                    sb.Append($" {fe.ActualWidth:0.#}x{fe.ActualHeight:0.#} m={fe.Margin} vis={fe.Visibility} col={System.Windows.Controls.Grid.GetColumn(fe)}/{System.Windows.Controls.Grid.GetColumnSpan(fe)}");
                                    if (fe is System.Windows.Controls.Control c) sb.Append($" pad={c.Padding} bt={c.BorderThickness} bg={c.Background}");
                                    if (fe is System.Windows.Controls.Border b) sb.Append($" pad={b.Padding} bt={b.BorderThickness} bg={b.Background}");
                                    if (fe is System.Windows.Controls.Grid g && g.ColumnDefinitions.Count > 0) sb.Append(" cols=" + string.Join(",", g.ColumnDefinitions.Select(x => x.Width.ToString())));
                                }
                                sb.Append('\n');
                                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Dump(VisualTreeHelper.GetChild(d, i), depth + 1);
                            }
                            var panel = VisualTreeHelper.GetParent(tab) as FrameworkElement;
                            sb.Append($"panel {panel?.GetType().Name} {panel?.ActualWidth:0}x{panel?.ActualHeight:0}\n");
                            Dump(tab, 0);
                            return JsonSerializer.Serialize(new { tree = sb.ToString() });
                        }
                    case "helpextract": return JsonSerializer.Serialize(new { path = HelpDoc.Extract() });   // the guide written out, not opened
                    case "pdfpages":   // the page order 匯出 PDF would use (scope = sheet / doc / group), optionally written to path
                        {
                            var psv = (SchView)w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase)));
                            var pages = w.PdfPages(psv, q["scope"] ?? "group");
                            if (q["path"] != null) AdCore.Render.PdfExport.ExportSchematics(pages, q["path"], AdCore.Render.PdfPaper.Auto, false, SchView.FontScale, SchView.ShowParams);
                            return JsonSerializer.Serialize(pages.Select(p => $"{p.sheet.Number?.ToString() ?? "-"} {p.file}"));
                        }
                    case "pdfdialog": w.Dispatcher.BeginInvoke(new Action(w.ExportPdf)); break;
                    case "groupmenu":   // the right-click menu of a group row in Projects: its items
                        {
                            var gi = Descend((DependencyObject)w.ProjectsAnch.Content).OfType<System.Windows.Controls.TreeViewItem>().FirstOrDefault(i => i.Tag is ProjectGroup g && g.Name == q["name"]);
                            if (gi?.ContextMenu == null) return "{\"error\":\"no group\"}";
                            return JsonSerializer.Serialize(gi.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header?.ToString() + (string.IsNullOrEmpty(m.InputGestureText) ? "" : " [" + m.InputGestureText + "]")));
                        }
                    case "renamegroup": w.RenameGroup(w.Ws.Groups.First(g => g.Name == q["name"]), q["to"]); break;
                    case "menushot":   // the context menu of a Projects row (doc=… a file, else the first group) saved as a picture (path=…); sub=1 opens its submenu too
                        {
                            var rows = Descend((DependencyObject)w.ProjectsAnch.Content).OfType<System.Windows.Controls.TreeViewItem>().ToList();
                            var row = q["doc"] != null ? rows.FirstOrDefault(i => i.Tag is DocItem d && d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase)) : rows.FirstOrDefault(i => i.Tag is ProjectGroup g && (q["group"] == null || g.Name == q["group"]));
                            if (row?.ContextMenu is not { } cm) return "{\"error\":\"no menu\"}";
                            cm.PlacementTarget = row; cm.IsOpen = true; cm.UpdateLayout();
                            FrameworkElement shot = cm;
                            if (q["sub"] == "1" && cm.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(i => i.HasItems) is { } mi)
                            {
                                mi.IsSubmenuOpen = true; mi.UpdateLayout();
                                if (mi.Template.FindName("PART_Popup", mi) is System.Windows.Controls.Primitives.Popup subPop && subPop.Child is FrameworkElement pc) { pc.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); pc.Arrange(new Rect(pc.DesiredSize)); pc.UpdateLayout(); shot = pc; }
                            }
                            var dpi = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                            var bmp = new RenderTargetBitmap((int)(shot.ActualWidth * dpi), (int)(shot.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                            bmp.Render(shot);
                            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                            using (var f = File.Create(q["path"])) enc.Save(f);
                            cm.IsOpen = false;
                            return JsonSerializer.Serialize(new { w = shot.ActualWidth, h = shot.ActualHeight });
                        }
                    case "treeclick":   // a click (count=2: double-click) on a file row of the Projects panel, routed like a real one
                        {
                            var row = Descend((DependencyObject)w.ProjectsAnch.Content).OfType<System.Windows.Controls.TreeViewItem>().FirstOrDefault(i => i.Tag is DocItem d && d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                            if (row?.Header is not UIElement head) return "{\"error\":\"no row\"}";
                            int count = q["count"] == "2" ? 2 : 1;
                            var setCount = typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.GetSetMethod(true)!;
                            for (int k = 1; k <= count; k++)
                                foreach (var ev in new[] { System.Windows.Input.Mouse.PreviewMouseDownEvent, System.Windows.Input.Mouse.MouseDownEvent, System.Windows.Input.Mouse.PreviewMouseUpEvent, System.Windows.Input.Mouse.MouseUpEvent })
                                {
                                    var a = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = ev };
                                    setCount.Invoke(a, new object[] { k });
                                    head.RaiseEvent(a);
                                }
                            break;
                        }
                    case "btnclick":   // a button by its label, inside a schematic view (in=<doc>) or the Properties panel (in=props)
                        {
                            DependencyObject root = q["in"] == "props" ? w.PropAnch.Content as DependencyObject : w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["in"], StringComparison.OrdinalIgnoreCase)));
                            var btn = root == null ? null : Descend(root).OfType<System.Windows.Controls.Button>().FirstOrDefault(b => (b.Content as string) == q["label"]);
                            if (btn == null) return "{\"error\":\"no button\"}";
                            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            break;
                        }
                    case "tabclick":   // a left click on a panel's side bar tab, routed like a real one
                        {
                            var ta = w.Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(x => x.ContentId == q["panel"]);
                            var tab = Descend(w.Dock).OfType<AvalonDock.Controls.LayoutAnchorControl>().FirstOrDefault(c => (c.GetType().GetProperty("Model")?.GetValue(c) ?? c.DataContext) == ta);
                            if (tab == null) return "{\"error\":\"no tab\"}";
                            foreach (var ev in new[] { System.Windows.Input.Mouse.PreviewMouseDownEvent, System.Windows.Input.Mouse.MouseDownEvent, System.Windows.Input.Mouse.PreviewMouseUpEvent, System.Windows.Input.Mouse.MouseUpEvent })
                                tab.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = ev });
                            break;
                        }
                    case "canvasclick":   // a left click on the view as WPF routes it (AvalonDock's handlers included); the position is wherever the mouse is
                        if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is { } cv)
                            foreach (var ev in new[] { System.Windows.Input.Mouse.PreviewMouseDownEvent, System.Windows.Input.Mouse.MouseDownEvent, System.Windows.Input.Mouse.PreviewMouseUpEvent, System.Windows.Input.Mouse.MouseUpEvent })
                                cv.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = ev });
                        break;
                    case "schclear": if (w.ActiveView is SchView sc) sc.ClickAt(new Point(2, 2), false); break;
                    case "closetab": w.DocsOpen.First(kv => kv.Key.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase)).Value.Close(); break;
                    case "view3d":   // v=top|bottom|iso|side|home, focus=<designator>, dolly=<factor>
                        if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView v3 && v3.View3D is { } p3)
                        {
                            switch (q["v"]) { case "top": p3.TopView(); break; case "bottom": p3.BottomView(); break; case "side": p3.SideView(); break; case "home": p3.Home(); break; case "iso": p3.IsoView(); break; }
                            if (q["focus"] is string fd && v3.Doc.Pcb.FindComp(fd) is int fi && fi >= 0 && v3.Scene.CompBox(fi) is { } fb) p3.FocusOn(fb);
                        }
                        break;
                    case "finger":   // doc, op=on|off|add|rot|clear|scale|png, x/y (view DIP) for add, mods=ctrl, v for scale, path/angle/size for png
                        {
                            if (q["op"] == "png") { Views.PcbView.SaveFingerCursorPng(q["path"], double.Parse(q["angle"] ?? "-45", System.Globalization.CultureInfo.InvariantCulture), double.Parse(q["size"] ?? "46", System.Globalization.CultureInfo.InvariantCulture)); break; }
                            var fv = w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) as PcbView;
                            if (fv == null) break;
                            switch (q["op"])
                            {
                                case "on": fv.SetTool("finger"); break;
                                case "off": fv.SetTool("select"); break;
                                case "add": fv.FingerAtView(new Point(double.Parse(q["x"], System.Globalization.CultureInfo.InvariantCulture), double.Parse(q["y"], System.Globalization.CultureInfo.InvariantCulture)), q["mods"] == "ctrl" ? System.Windows.Input.ModifierKeys.Control : System.Windows.Input.ModifierKeys.None); break;
                                case "rot": fv.FingerRotateForTest(); break;
                                case "key": fv.KeyForTest(Enum.Parse<System.Windows.Input.Key>(q["k"])); break;
                                case "clear": fv.ClearFingers(); break;
                                case "scale": fv.SetFingerScale(double.Parse(q["v"], System.Globalization.CultureInfo.InvariantCulture)); break;
                            }
                            return JsonSerializer.Serialize(new { count = fv.FingerCount, angle = fv.FingerAngle, tool = fv.Tool, scale = Settings.Current.FingerScale });
                        }
                    case "usestep": Views.Pcb3DOptions.UseStepModels = q["on"] != "0"; break;
                    case "step3d":   // the STEP models of doc: read time, counts, and the memory the process uses
                        {
                            var sd = w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase));
                            if (q["gc"] == "1") { System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true); }
                            var pr = System.Diagnostics.Process.GetCurrentProcess();
                            return JsonSerializer.Serialize(new { step = Views.Pcb3DView.StepInfo(sd.Pcb), log = AdCore.Step.StepModel.LastLog, privateMB = pr.PrivateMemorySize64 >> 20, workingMB = pr.WorkingSet64 >> 20, gcMB = GC.GetTotalMemory(false) >> 20, committedMB = GC.GetGCMemoryInfo().TotalCommittedBytes >> 20 });
                        }
                    case "padnums": if (w.ViewOf(w.DocsOpen.Keys.First(d => d.Name.Equals(q["doc"], StringComparison.OrdinalIgnoreCase))) is PcbView pp) { pp.St.ShowPadNumbers = q["on"] != "0"; if (q["nets"] != null) pp.St.ShowNetNames = q["nets"] != "0"; pp.Invalidate(); } break;
                }
                return "{\"ok\":true}";
            case "/shot":
                {
                    var src = PresentationSource.FromVisual(w); double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                    var top = q["win"] != null ? w.OwnedWindows.Cast<Window>().LastOrDefault() : null;
                    var root = (FrameworkElement)(top ?? w).Content;
                    var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi), (int)(root.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var f = File.Create(q["path"])) enc.Save(f);
                    return "{\"ok\":true}";
                }
        }
        return "{\"error\":\"unknown\"}";
    }

    static IEnumerable<DependencyObject> Descend(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i); yield return c;
            foreach (var x in Descend(c)) yield return x;
        }
    }
}
