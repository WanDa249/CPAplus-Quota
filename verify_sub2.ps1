$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& "$root\build.ps1"
if($LASTEXITCODE -ne 0){throw 'Client build failed'}
New-Item -ItemType Directory -Path "$root\evidence" -Force | Out-Null
$sources=@(Get-ChildItem -LiteralPath "$root\src" -Filter '*.cs' | ForEach-Object FullName)+@("$root\Sub2QuotaProbe.cs")
$runner=Join-Path $root 'bin\Sub2QuotaProbe.exe'
$resources=@("/resource:$root\assets\fonts\SourceHanSansCN-Regular.otf,BundledFonts.SourceHanSansCN-Regular.otf","/resource:$root\assets\fonts\SourceHanSansCN-Medium.otf,BundledFonts.SourceHanSansCN-Medium.otf")
& "$framework\csc.exe" /nologo /target:exe /main:Sub2QuotaProbe /platform:x64 /codepage:65001 /out:"$runner" "/resource:$root\src\QuotaPopup.xaml,QuotaPopup.xaml" $resources /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xaml.dll /reference:"$framework\System.Net.Http.dll" /reference:"$framework\WPF\WindowsBase.dll" /reference:"$framework\WPF\PresentationFramework.dll" /reference:"$framework\WPF\PresentationCore.dll" /reference:"$framework\WPF\WindowsFormsIntegration.dll" /reference:"$framework\WPF\UIAutomationClient.dll" /reference:"$framework\WPF\UIAutomationTypes.dll" $sources
if($LASTEXITCODE -ne 0){throw 'Sub2 quota probe compilation failed'}
$log = "$root\evidence\sub2-tests.txt"
& $runner 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { throw 'Sub2 quota probe failed' }
