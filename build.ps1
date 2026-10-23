$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $root 'bin\CPAplusQuota.exe'
New-Item -ItemType Directory -Path "$root\bin" -Force | Out-Null
$notice = Join-Path $root 'bin\THIRD_PARTY_NOTICES-v0.1.3-beta.txt'
$documents = @('LICENSE.txt','THIRD_PARTY_NOTICES.md','licenses\echo-watcher-MIT.txt','licenses\lyricbar-MIT.txt','licenses\echo-native-MIT.txt') | ForEach-Object {Get-Content -LiteralPath (Join-Path $root $_) -Raw -Encoding UTF8}
[System.IO.File]::WriteAllText($notice,($documents -join "`r`n`r`n"),(New-Object System.Text.UTF8Encoding($false)))
$sources = @(Get-ChildItem -LiteralPath "$root\src" -Filter '*.cs' | ForEach-Object FullName)
$resources = @("/resource:$root\assets\fonts\SourceHanSansCN-Regular.otf,BundledFonts.SourceHanSansCN-Regular.otf", "/resource:$root\assets\fonts\SourceHanSansCN-Medium.otf,BundledFonts.SourceHanSansCN-Medium.otf", "/resource:$root\assets\fonts\LICENSE.txt,BundledFonts.LICENSE.txt", "/resource:$notice,BundledFonts.THIRD_PARTY_NOTICES-v0.1.3-beta.txt")
& "$framework\csc.exe" /nologo /target:winexe /main:Program /platform:x64 /optimize+ /codepage:65001 /out:"$output" /win32manifest:"$root\src\app.manifest" "/win32icon:$root\assets\quota.ico" "/resource:$root\assets\quota.ico,QuotaIcon.ico" "/resource:$root\src\QuotaPopup.xaml,QuotaPopup.xaml" $resources /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:"$framework\WPF\UIAutomationClient.dll" /reference:"$framework\WPF\UIAutomationTypes.dll" /reference:"$framework\WPF\WindowsBase.dll" /reference:"$framework\WPF\PresentationFramework.dll" /reference:"$framework\WPF\PresentationCore.dll" /reference:"$framework\WPF\WindowsFormsIntegration.dll" /reference:"$framework\System.Net.Http.dll" /reference:System.Xaml.dll $sources
if ($LASTEXITCODE -ne 0) {throw 'Windows x64 build failed'}
$stream=[System.IO.File]::OpenRead($output)
try {
    $digest=[System.Security.Cryptography.SHA256]::Create()
    try {$hash=([System.BitConverter]::ToString($digest.ComputeHash($stream))).Replace('-','').ToLowerInvariant()} finally {$digest.Dispose()}
} finally {$stream.Dispose()}
Write-Output "Built: $output"
Write-Output "SHA256: $hash"
