# Run all assessment unit tests, one service, or one service's story from PowerShell.
# Examples: .\test-unit.ps1
#           .\test-unit.ps1 -Service AuthService
#           .\test-unit.ps1 -Service AuthService -Story Registration
#           .\test-unit.ps1 -Prepare
#           .\test-unit.ps1 -NoBuild
#           .\test-unit.ps1 -NoBuild -Demo
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$Service,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$Story,
    [switch]$NoBuild,
    [switch]$Demo,
    [switch]$Prepare,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
$unitRoot = Join-Path $repositoryRoot 'testing-pipeline/unit-testing'

# Validate selections before moving the previous reports.
if ($Story -and -not $Service) {
    throw 'Specify -Service when selecting -Story.'
}
if ($Prepare -and $NoBuild) {
    throw 'Use -Prepare before the demo, then -NoBuild when running tests.'
}
if ($Prepare -and $Demo) {
    throw 'Prepare first, then use -Demo when running tests.'
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

# Locate an installed SDK or the temporary SDK used to validate this assessment.
# An explicit path takes priority; the helper never downloads or installs tools.
if ($DotNetPath) {
    if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw "dotnet.exe was not found at '$DotNetPath'." }
    $dotNetExecutable = [System.IO.Path]::GetFullPath($DotNetPath)
} else {
    $dotNetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    $dotNetExecutable = if ($dotNetCommand) { $dotNetCommand.Source } else { $null }
    if (-not $dotNetExecutable) {
        $sdkVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
        $sdkCandidates = @()
        if ($env:ProgramFiles) { $sdkCandidates += Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
        if ($env:LOCALAPPDATA) { $sdkCandidates += Join-Path $env:LOCALAPPDATA "Temp/ase-unit-dotnet-$sdkVersion/dotnet.exe" }
        $dotNetExecutable = $sdkCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $dotNetExecutable) { throw 'Install the .NET 8 SDK, or supply -DotNetPath with the full path to dotnet.exe.' }
if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)) { throw 'Node.js is required for the unit reports.' }

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
$buildRoot = Join-Path $resultsRoot 'build'
[System.IO.Directory]::CreateDirectory($resultsRoot) | Out-Null

# Prevent another helper run from moving reports while this one is writing them.
try {
    $runLock = [System.IO.File]::Open((Join-Path $resultsRoot 'runner.lock'),
        [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
} catch {
    throw 'Another local unit run is active, or the results folder cannot be opened.'
}

$previousReportRoot = $env:ASE_REPORT_ROOT
$previousPath = $env:Path
$previousDotNetRoot = $env:DOTNET_ROOT
$result = 1
try {
    $sdkDirectory = Split-Path $dotNetExecutable -Parent
    $env:Path = $sdkDirectory + [System.IO.Path]::PathSeparator + $previousPath
    $env:DOTNET_ROOT = $sdkDirectory
    if ($Prepare) {
        $projects = if ($Service) { @(Get-Item -LiteralPath $projectPath) }
            else { @(Get-ChildItem -LiteralPath $unitRoot -Directory | ForEach-Object {
                Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File
            } | Sort-Object FullName) }
        if ($projects.Count -eq 0) { throw 'No assessment unit-test projects were found.' }
        foreach ($project in $projects) {
            & $dotNetExecutable restore $project.FullName --artifacts-path $buildRoot
            if ($LASTEXITCODE -ne 0) { throw "Restore failed for $($project.Name)." }
            & $dotNetExecutable build $project.FullName -c Release --no-restore --artifacts-path $buildRoot --disable-build-servers -p:UseSharedCompilation=false
            if ($LASTEXITCODE -ne 0) { throw "Build failed for $($project.Name)." }
        }
        Write-Host "`nPrepared $($projects.Count) unit-test project(s). Use .\test-unit.ps1 -NoBuild for the demo."
        $result = 0
        exit $result
    }
    if (Test-Path -LiteralPath $latestRoot) {
        $latestItem = Get-Item -LiteralPath $latestRoot
        # OneDrive cloud folders also have ReparsePoint set, but no link target.
        # Reject actual symbolic links/junctions rather than normal synced folders.
        if ($latestItem.LinkType -or $latestItem.Target) {
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
    # Keep generated outputs for the tests AND referenced service projects in unit-testing.
    $runnerArguments += @('--artifacts-path', (ConvertTo-GitBashPath $buildRoot))
    if ($Service) { $runnerArguments += @('--service', $Service) }
    if ($Story) { $runnerArguments += @('--story', $Story) }
    if ($Demo) { $runnerArguments += '--demo' }
    if ($NoBuild) { $runnerArguments += @('--no-build', '--no-restore') }
    else { $runnerArguments += @('--disable-build-servers', '-p:UseSharedCompilation=false') }
    & $gitBashPath @runnerArguments
    $result = $LASTEXITCODE
} finally {
    $env:ASE_REPORT_ROOT = $previousReportRoot
    $env:Path = $previousPath
    $env:DOTNET_ROOT = $previousDotNetRoot
    $runLock.Dispose()
    if (-not $Prepare) {
        $reportPath = 'testing-pipeline/unit-testing/TestResults/latest/unit-testing/summary.md'
        if ((Get-Location).Path -ne $repositoryRoot) {
            $reportPath = Join-Path $latestRoot 'unit-testing/summary.md'
        }
        Write-Host "`nReport: $reportPath"
    }
}
exit $result
