param([Parameter(Mandatory=$true)][string]$Backend)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& "$root\build.ps1"
if($LASTEXITCODE -ne 0){throw 'Client build failed'}
$sources=@(Get-ChildItem -LiteralPath "$root\src" -Filter '*.cs' | ForEach-Object FullName)+@("$root\RoutingProbe.cs")
$runner=Join-Path $root 'bin\RoutingProbe.exe'
$resources=@("/resource:$root\assets\fonts\SourceHanSansCN-Regular.otf,BundledFonts.SourceHanSansCN-Regular.otf","/resource:$root\assets\fonts\SourceHanSansCN-Medium.otf,BundledFonts.SourceHanSansCN-Medium.otf")
& "$framework\csc.exe" /nologo /target:exe /main:RoutingProbe /platform:x64 /codepage:65001 /out:"$runner" "/resource:$root\src\QuotaPopup.xaml,QuotaPopup.xaml" $resources /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xaml.dll /reference:"$framework\System.Net.Http.dll" /reference:"$framework\WPF\WindowsBase.dll" /reference:"$framework\WPF\PresentationFramework.dll" /reference:"$framework\WPF\PresentationCore.dll" /reference:"$framework\WPF\WindowsFormsIntegration.dll" /reference:"$framework\WPF\UIAutomationClient.dll" /reference:"$framework\WPF\UIAutomationTypes.dll" $sources
if($LASTEXITCODE -ne 0){throw 'Routing probe compilation failed'}
& $runner $Backend 2>&1 | Tee-Object -FilePath "$root\evidence\routing-tests.txt"
if($LASTEXITCODE -ne 0){throw 'Routing probe failed'}
