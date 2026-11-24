<#
 Game Translator cho Windows: test -> publish x64/arm64 -> NSIS -> zip portable -> SHA256SUMS.
 Chạy từ thư mục windows/:   ./build_release.ps1 [-Arch x64,arm64] [-SkipTests]
 Ký số (tùy chọn): đặt biến môi trường SIGN_CERT (đường dẫn .pfx) và SIGN_PASSWORD.
#>
param(
    [string[]]$Arch = @('x64', 'arm64'),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = (Get-Content ..\VERSION -Raw).Trim()
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null

function Find-Tool($name, $fallbacks) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($f in $fallbacks) { if (Test-Path $f) { return $f } }
    throw "Không tìm thấy $name"
}
$makensis = Find-Tool 'makensis' @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe")

function Sign-File($path) {
    if (-not $env:SIGN_CERT) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { Write-Warning 'Không có signtool, bỏ qua ký số.'; return }
    & $signtool.FullName sign /f $env:SIGN_CERT /p $env:SIGN_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Ký số lỗi: $path" }
}

if (-not $SkipTests) {
    Write-Host '== Test' -ForegroundColor Cyan
    dotnet test tests/GameTranslator.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Test Core thất bại' }
    dotnet test tests/GameTranslator.Windows.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Test Windows thất bại' }
}

foreach ($a in $Arch) {
    Write-Host "== Publish win-$a" -ForegroundColor Cyan
    $pub = Join-Path $artifacts "publish\win-$a"
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
    dotnet publish src/GameTranslator.App -c Release -r "win-$a" --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $pub
    if ($LASTEXITCODE -ne 0) { throw "Publish win-$a thất bại" }
    Sign-File (Join-Path $pub 'GameTranslator.exe')

    Write-Host "== NSIS win-$a" -ForegroundColor Cyan
    $setup = Join-Path $artifacts "GameTranslator-$version-$a-setup.exe"
    & $makensis /V2 "/DARCH=$a" "/DVERSION=$version" "/DSOURCE=$pub" "/DOUTFILE=$setup" installer\gametranslator.nsi
    if ($LASTEXITCODE -ne 0) { throw "makensis win-$a thất bại" }
    Sign-File $setup

    Write-Host "== Zip portable win-$a" -ForegroundColor Cyan
    $stage = Join-Path $artifacts "stage-$a\GameTranslator"
    if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item "$pub\*" $stage -Recurse
    Set-Content (Join-Path $stage 'portable.txt') 'Có file này cạnh exe thì dữ liệu (hồ sơ, cache, log) nằm trong thư mục data\ thay vì AppData.' -Encoding UTF8
    $zip = Join-Path $artifacts "GameTranslator-$version-$a-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip
    Remove-Item (Split-Path $stage) -Recurse -Force
}

Write-Host '== SHA256SUMS' -ForegroundColor Cyan
$sums = Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in '.exe', '.zip' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
Set-Content (Join-Path $artifacts 'SHA256SUMS') $sums -Encoding ASCII
Get-ChildItem $artifacts -File | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host 'Xong.' -ForegroundColor Green
