$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Path "$root\evidence" -Force | Out-Null
$sources=@(Get-ChildItem -LiteralPath "$root\src" -Filter '*.cs' | ForEach-Object FullName)+@("$root\InteractionProbe.cs")
$runner=Join-Path $root 'bin\InteractionProbe.exe'
$resources=@("/resource:$root\assets\fonts\SourceHanSansCN-Regular.otf,BundledFonts.SourceHanSansCN-Regular.otf","/resource:$root\assets\fonts\SourceHanSansCN-Medium.otf,BundledFonts.SourceHanSansCN-Medium.otf","/resource:$root\assets\fonts\LICENSE.txt,BundledFonts.LICENSE.txt","/resource:$root\bin\THIRD_PARTY_NOTICES-v0.1.3-beta.txt,BundledFonts.THIRD_PARTY_NOTICES-v0.1.3-beta.txt")
& "$framework\csc.exe" /nologo /target:exe /main:InteractionProbe /platform:x64 /codepage:65001 /out:"$runner" "/resource:$root\src\QuotaPopup.xaml,QuotaPopup.xaml" $resources /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xaml.dll /reference:"$framework\System.Net.Http.dll" /reference:"$framework\WPF\WindowsBase.dll" /reference:"$framework\WPF\PresentationFramework.dll" /reference:"$framework\WPF\PresentationCore.dll" /reference:"$framework\WPF\WindowsFormsIntegration.dll" /reference:"$framework\WPF\UIAutomationClient.dll" /reference:"$framework\WPF\UIAutomationTypes.dll" $sources
if($LASTEXITCODE -ne 0){throw 'Interaction probe compilation failed'}
& $runner 2>&1 | Tee-Object -FilePath "$root\evidence\interaction-tests.txt"
if($LASTEXITCODE -ne 0){throw 'Interaction probe failed'}
