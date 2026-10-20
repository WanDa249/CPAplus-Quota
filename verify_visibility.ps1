$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$evidence=Join-Path $root 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$runner=Join-Path $root 'bin\VisibilityProbe.exe'
& "$framework\csc.exe" /nologo /target:exe /platform:x64 /codepage:65001 /out:"$runner" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xaml.dll /reference:"$framework\WPF\WindowsBase.dll" /reference:"$framework\WPF\PresentationCore.dll" /reference:"$framework\WPF\PresentationFramework.dll" "$root\VisibilityProbe.cs"
if($LASTEXITCODE -ne 0){throw 'Visibility probe compilation failed'}
& $runner 2>&1 | Tee-Object -FilePath "$evidence\visibility-tests.txt"
if($LASTEXITCODE -ne 0){throw 'Visibility probe failed'}
