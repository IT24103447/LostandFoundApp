# Run all assessment unit tests, one service, or one service's story from PowerShell.
# Examples: .\test-unit.ps1
#           .\test-unit.ps1 -Service AuthService
#           .\test-unit.ps1 -Service AuthService -Story Registration
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$Service,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$Story,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
$unitRoot = Join-Path $repositoryRoot 'testing-pipeline/unit-testing'

# Validate selections before moving the previous reports.
if ($Story -and -not $Service) {
    throw 'Specify -Service when selecting -Story.'
}
if ($Service) {
    $projectPath = Join-Path $unitRoot "$Service.UnitTests/$Service.UnitTests.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "No assessment unit-test project exists for service '$Service'."
    }
    if ($Story -and -not (Test-Path -LiteralPath (Join-Path $unitRoot "$Service.UnitTests/$Story") -PathType Container)) {
        throw "No story folder '$Story' exists in $Service.UnitTests."
    }
}

# Use Git Bash rather than the WindowsApps bash alias that launches WSL.
$gitBashCandidates = @()
$gitCommand = Get-Command git -ErrorAction SilentlyContinue
if ($gitCommand) {
    $gitInstallRoot = Split-Path (Split-Path $gitCommand.Source -Parent) -Parent
    $gitBashCandidates += Join-Path $gitInstallRoot 'bin/bash.exe'
}
if ($env:ProgramFiles) { $gitBashCandidates += Join-Path $env:ProgramFiles 'Git/bin/bash.exe' }
if ($env:LOCALAPPDATA) { $gitBashCandidates += Join-Path $env:LOCALAPPDATA 'Programs/Git/bin/bash.exe' }
$gitBashPath = $gitBashCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $gitBashPath) { throw 'Git Bash was not found. Install Git for Windows to use this helper.' }

function ConvertTo-GitBashPath([string]$NativePath) {
    $fullPath = [System.IO.Path]::GetFullPath($NativePath).Replace('\', '/')
    if ($fullPath -notmatch '^([A-Za-z]):/(.*)$') { throw "Expected a local Windows drive path: $NativePath" }
    return '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2]
}

$resultsRoot = Join-Path $unitRoot 'TestResults'
$latestRoot = Join-Path $resultsRoot 'latest'
$historyRoot = Join-Path $resultsRoot 'history'
[System.IO.Directory]::CreateDirectory($resultsRoot) | Out-Null

# Prevent another helper run from moving reports while this one is writing them.
try {
    $runLock = [System.IO.File]::Open((Join-Path $resultsRoot 'runner.lock'),
        [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
} catch {
    throw 'Another local unit run is active, or the results folder cannot be opened.'
}

$previousReportRoot = $env:ASE_REPORT_ROOT
$result = 1
try {
    if (Test-Path -LiteralPath $latestRoot) {
        if ((Get-Item -LiteralPath $latestRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to move a linked latest-results folder.'
        }
        [System.IO.Directory]::CreateDirectory($historyRoot) | Out-Null
        $archiveName = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
        $archivePath = [System.IO.Path]::GetFullPath((Join-Path $historyRoot $archiveName))
        # Both move targets must remain inside this repository's unit TestResults.
        $expectedPrefix = [System.IO.Path]::GetFullPath($resultsRoot).TrimEnd('\') + '\'
        foreach ($movePath in @([System.IO.Path]::GetFullPath($latestRoot), $archivePath)) {
            if (-not $movePath.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'Results archive path is outside the intended TestResults directory.'
            }
        }
        Move-Item -LiteralPath $latestRoot -Destination $archivePath
    }
    [System.IO.Directory]::CreateDirectory($latestRoot) | Out-Null
    $env:ASE_REPORT_ROOT = ConvertTo-GitBashPath $latestRoot
    $runnerArguments = @('--noprofile', '--norc', (ConvertTo-GitBashPath (Join-Path $unitRoot 'run.sh')))
    if ($Service) { $runnerArguments += @('--service', $Service) }
    if ($Story) { $runnerArguments += @('--story', $Story) }
    if ($NoBuild) { $runnerArguments += @('--no-build', '--no-restore') }
    & $gitBashPath @runnerArguments
    $result = $LASTEXITCODE
} finally {
    $env:ASE_REPORT_ROOT = $previousReportRoot
    $runLock.Dispose()
    $reportPath = 'testing-pipeline/unit-testing/TestResults/latest/unit-testing/summary.md'
    if ((Get-Location).Path -ne $repositoryRoot) {
        $reportPath = Join-Path $latestRoot 'unit-testing/summary.md'
    }
    Write-Host "`nReport: $reportPath"
}
exit $result
