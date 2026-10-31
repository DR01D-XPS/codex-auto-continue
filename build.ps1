param(
    [string]$DotnetPath,
    [switch]$SkipTests,
    [switch]$Integration,
    [string]$CliPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $script:dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ".NET command failed: $($Arguments -join ' ')" }
}
if ($DotnetPath) {
    $script:dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
} else {
    $candidates = @((Join-Path $root '.sdk\dotnet.exe'), (Join-Path (Split-Path $root -Parent) '.dotnet-sdk\dotnet.exe'))
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            $sdks = & $candidate --list-sdks
            if ($sdks -match '^10\.0\.40\d') { $script:dotnet = $candidate; break }
        }
    }
    if (-not $script:dotnet) {
        $installer = Join-Path $root 'tools\dotnet-install.ps1'
        if (-not (Test-Path -LiteralPath $installer)) { Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer }
        & $installer -Version '10.0.401' -InstallDir (Join-Path $root '.sdk') -NoPath
        $script:dotnet = Join-Path $root '.sdk\dotnet.exe'
        if (-not (Test-Path -LiteralPath $script:dotnet)) { throw 'SDK .NET 10 installation failed.' }
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path $script:dotnet -Parent
$checks = Join-Path $root 'artifacts\checks'
$release = Join-Path $root 'artifacts\release'
if ($OutputDirectory) { $release = [System.IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $checks,$release -Force | Out-Null
Push-Location $root
try {
    Invoke-Dotnet -Arguments @('build','src\CodexAutoContinue.App\CodexAutoContinue.App.csproj','-c','Release','--nologo')
    if (-not $SkipTests) {
        & $script:dotnet run --project 'tests\CodexAutoContinue.Tests\CodexAutoContinue.Tests.csproj' -c Release --no-launch-profile 2>&1 | Tee-Object -FilePath (Join-Path $checks 'tests.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Automatic tests failed.' }
    }
    if ($Integration) {
        if (-not $CliPath) { $CliPath = (Get-Command codex -ErrorAction Stop).Source }
        & $script:dotnet run --project 'tests\CodexAutoContinue.Tests\CodexAutoContinue.Tests.csproj' -c Release --no-build -- --integration $CliPath 2>&1 | Tee-Object -FilePath (Join-Path $checks 'integration.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Read-only CLI integration failed.' }
    }
    Invoke-Dotnet -Arguments @('publish','src\CodexAutoContinue.App\CodexAutoContinue.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-o',$release,'--nologo')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $release -Force
    $exe = Join-Path $release 'CodexAutoContinue.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Published EXE missing.' }
    $env:CODEX_AUTOCONTINUE_SMOKE_DIR = $checks
    $smokeResult = Join-Path $checks 'ui-smoke.txt'
    if (Test-Path -LiteralPath $smokeResult) { Remove-Item -LiteralPath $smokeResult }
    $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'UI check timed out.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $checks 'ui-smoke.txt'))) { throw 'UI check failed.' }
    Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Format-List | Out-File -LiteralPath (Join-Path $checks 'sha256.txt') -Encoding utf8
    Write-Output "Ready: $exe"
} finally {
    Pop-Location
}
