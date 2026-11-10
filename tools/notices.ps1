# Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
# SPDX-License-Identifier: GPL-3.0-only
# Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
# additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
# Builds src\AdApp\Help\THIRD-PARTY-NOTICES.txt from the licence files of the exact component versions in the
# program (NuGet cache) and of NSIS (the installer). Run it again whenever a package or the .NET runtime is updated:
#   powershell -ExecutionPolicy Bypass -File tools\notices.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ng = Join-Path $env:USERPROFILE '.nuget\packages'
$nsis = Join-Path $env:LOCALAPPDATA 'electron-builder\Cache\nsis\nsis-3.0.4.1'
$out = Join-Path $root 'src\AdApp\Help\THIRD-PARTY-NOTICES.txt'

# name, version, licence, home page, licence / notice files
$parts = @(
  @('.NET Runtime', '10.0.14', 'MIT', 'https://github.com/dotnet/runtime', @("$ng\microsoft.netcore.app.runtime.win-x64\10.0.14\LICENSE.TXT", "$ng\microsoft.netcore.app.runtime.win-x64\10.0.14\THIRD-PARTY-NOTICES.TXT")),
  @('.NET Windows Desktop Runtime (WPF; includes System.Drawing.Common)', '10.0.14', 'MIT', 'https://github.com/dotnet/wpf', @("$ng\microsoft.windowsdesktop.app.runtime.win-x64\10.0.14\LICENSE")),
  @('AvalonDock (Dirkster.AvalonDock)', '4.72.1', 'Ms-PL', 'https://github.com/Dirkster99/AvalonDock', @("$ng\dirkster.avalondock\4.72.1\LICENSE")),
  @('AvalonDock VS2013 theme (Dirkster.AvalonDock.Themes.VS2013)', '4.72.1', 'Ms-PL', 'https://github.com/Dirkster99/AvalonDock', @("$ng\dirkster.avalondock.themes.vs2013\4.72.1\LICENSE")),
  @('Helix Toolkit (HelixToolkit.Wpf)', '2.25.0', 'MIT', 'https://github.com/helix-toolkit/helix-toolkit', @("$ng\helixtoolkit.wpf\2.25.0\LICENSE")),
  @('SkiaSharp', '2.88.9', 'MIT', 'https://github.com/mono/SkiaSharp', @("$ng\skiasharp\2.88.9\LICENSE.txt", "$ng\skiasharp\2.88.9\THIRD-PARTY-NOTICES.txt")),
  @('SkiaSharp native library for Windows (Skia and the libraries built into it)', '2.88.9', 'MIT; Skia: BSD-3-Clause; others as listed', 'https://github.com/mono/SkiaSharp', @("$ng\skiasharp.nativeassets.win32\2.88.9\LICENSE.txt", "$ng\skiasharp.nativeassets.win32\2.88.9\THIRD-PARTY-NOTICES.txt")),
  @('SkiaSharp.Views.WPF', '2.88.9', 'MIT', 'https://github.com/mono/SkiaSharp', @("$ng\skiasharp.views.wpf\2.88.9\LICENSE.txt")),
  @('SkiaSharp.Views.Desktop.Common', '2.88.9', 'MIT', 'https://github.com/mono/SkiaSharp', @("$ng\skiasharp.views.desktop.common\2.88.9\LICENSE.txt")),
  @('earcut (polygon triangulation algorithm, ported to C# in src/AdCore/Step/Earcut.cs)', '3.2.4', 'ISC', 'https://github.com/mapbox/earcut', @("$root\tools\licenses\earcut-LICENSE.txt")),
  @('NSIS (the installer, not part of the program)', '3.04', 'zlib/libpng and others (see COPYING)', 'https://nsis.sourceforge.io', @("$nsis\COPYING"))
)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('Herald PCB Reader - 第三方元件與授權 / Third-party components and licences')
[void]$sb.AppendLine('=========================================================================')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Herald PCB Reader 本身：Copyright (C) 2026 Johnny.ch.Tsai，依 GNU 通用公共授權條款第 3 版（GPL-3.0）發佈，另允許與 AvalonDock（Ms-PL）結合散布；見 NOTICE.txt 與 LICENSE.txt。')
[void]$sb.AppendLine('Herald PCB Reader itself: Copyright (C) 2026 Johnny.ch.Tsai, licensed under the GNU General Public License version 3, with an additional permission for AvalonDock (Ms-PL); see NOTICE.txt and LICENSE.txt.')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Herald PCB Reader 使用下列第三方元件。各元件依其授權條款提供，著作權屬於各自的作者；以下附上各授權與聲明的全文。')
[void]$sb.AppendLine('Herald PCB Reader uses the components below. Each is provided under its own licence and remains the property of its authors; the full licence and notice texts follow.')
[void]$sb.AppendLine('')
foreach ($p in $parts) { [void]$sb.AppendLine(('- {0} {1}  [{2}]  {3}' -f $p[0], $p[1], $p[2], $p[3])) }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Altium、Altium Designer、KiCad、EAGLE 等名稱為其各自所有者的商標；本程式為獨立開發的檢視工具，與這些公司及專案無關，也未經其認可。')
[void]$sb.AppendLine('Altium, Altium Designer, KiCad and EAGLE are trademarks of their respective owners. This program is developed independently and is not affiliated with or endorsed by them.')

$seen = @{}
foreach ($p in $parts) {
  [void]$sb.AppendLine(''); [void]$sb.AppendLine(''); [void]$sb.AppendLine(('#' * 77))
  [void]$sb.AppendLine(('{0} {1}  ({2})' -f $p[0], $p[1], $p[3])); [void]$sb.AppendLine(('#' * 77))
  foreach ($f in $p[4]) {
    if (-not (Test-Path $f)) { throw "missing licence file: $f" }
    $text = [IO.File]::ReadAllText($f).Trim()
    $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))
    [void]$sb.AppendLine(''); [void]$sb.AppendLine(('--- {0} ---' -f (Split-Path $f -Leaf)))
    if ($seen.ContainsKey($sha)) { [void]$sb.AppendLine(('(same text as for {0} above)' -f $seen[$sha])) }
    else { $seen[$sha] = $p[0]; [void]$sb.AppendLine($text) }
  }
}
[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding $true))
'wrote {0} ({1:N0} bytes)' -f $out, (Get-Item $out).Length
