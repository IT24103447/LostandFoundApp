[CmdletBinding()]
param(
    [switch]$SkipIntegration,
    [switch]$SkipSelenium
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$resultsDir = Join-Path $PSScriptRoot "TestResults"
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

function Get-TestPurpose([string]$name) {
    switch -Regex ($name) {
        "ValidRequest|CreatesActive|HappyPath" { return "Create a valid ACTIVE item for the authenticated user" }
        "ResponseDto|ResponseNever|NeverReturns|NeverRenders" { return "Verify hidden information is absent from public output" }
        "Nonexistent|MissingItem|NotFound" { return "Return not found for missing records" }
        "InvalidSession|NoAuth|ExpiredJwt|TamperedJwt" { return "Reject missing, expired, or tampered authentication" }
        "PublishesEvent|Complete.*Event|Event" { return "Verify the create event contains matching data" }
        "NoPhotos|OptionalPhoto|WithOnePhoto|AcceptsFivePhotos" { return "Validate optional photos and photo association/limits" }
        "TooManyPhotos|InvalidPhoto|DisallowedContent|ZeroLength" { return "Reject invalid, empty, oversized, or disallowed photos" }
        "WhitespaceOnly|Whitespace" { return "Reject whitespace-only required values" }
        "UnsupportedCategory|Category" { return "Select or validate item category" }
        "Description" { return "Validate description required/length/counter behavior" }
        "Future.*Date|Date.*Future|DateField" { return "Validate future-date rejection and date boundary" }
        "MalformedDate|DateFound|DateLost" { return "Validate date parsing and persistence" }
        "Missing.*Field|Required" { return "Reject missing required fields" }
        "MaxLength|OverMax|Boundary" { return "Validate field length boundaries" }
        "Photo|Photos|Jpeg|Image" { return "Validate optional photo upload and limits" }
        "Hidden|Privacy|Never.*Render|Never.*Return" { return "Protect hidden information from frontend responses" }
        "Kafka|Publish" { return "Build, enqueue, or verify Kafka create events" }
        "Auth|Jwt|Session|Cookie" { return "Validate authentication and JWT behavior" }
        "StorageFailure|PartialFailure" { return "Expose behavior when storage fails after database write" }
        "FullWizard|HappyPath|Completes" { return "Complete the end-to-end reporting workflow" }
        "Back|Preserv" { return "Preserve wizard data during navigation" }
        "CategorySelection" { return "Select category and close the dropdown" }
        default { return "Execute the test case" }
    }
}

function Invoke-QaPhase([string]$label, [string]$project, [string]$filter, [string]$trxName) {
    $trxPath = Join-Path $resultsDir $trxName
    Write-Host "`n=== $label ===" -ForegroundColor Cyan
    Write-Host "Project: $project"
    Write-Host "Filter:  $filter"

    & dotnet test $project --no-restore --filter $filter `
        --logger "console;verbosity=minimal" `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $resultsDir
    $exitCode = $LASTEXITCODE

    if (-not (Test-Path $trxPath)) {
        $trxPath = Get-ChildItem -Path $resultsDir -Filter "*.trx" |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if (Test-Path $trxPath) {
        $xml = [System.Xml.Linq.XDocument]::Load($trxPath)
        $ns = $xml.Root.GetDefaultNamespace()
        foreach ($result in $xml.Descendants($ns + "UnitTestResult")) {
            $name = [string]$result.Attribute("testName")
            $outcome = [string]$result.Attribute("outcome")
            $purpose = Get-TestPurpose $name
            $color = if ($outcome -eq "Passed") { "Green" } elseif ($outcome -eq "NotExecuted") { "Yellow" } else { "Red" }
            Write-Host ("[{0,-11}] {1}" -f $outcome, $name) -ForegroundColor $color
            Write-Host ("              Tests: {0}" -f $purpose)

            $message = $result.Descendants($ns + "ErrorInfo") | ForEach-Object {
                $text = $_.Element($ns + "Message")
                if ($null -ne $text) { $text.Value }
            } | Select-Object -First 1
            if ($outcome -ne "Passed" -and $message) {
                Write-Host ("              Why:   {0}" -f ($message -replace "\s+", " ").Trim()) -ForegroundColor Red
            }
            if ($outcome -eq "NotExecuted") {
                Write-Host "              Why:   Skipped, usually because this is a known defect or requires an unavailable environment." -ForegroundColor Yellow
            }
        }
    }

    if ($exitCode -ne 0) {
        Write-Host "Phase result: FAILED or environment-blocked (exit code $exitCode)" -ForegroundColor Red
    } else {
        Write-Host "Phase result: PASSED" -ForegroundColor Green
    }
    return $exitCode
}

$unitProject = Join-Path $repo "services\ItemService.Tests\ItemService.Tests.csproj"
$seleniumProject = Join-Path $repo "tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj"
$failed = $false

$unitExit = Invoke-QaPhase "xUnit unit and DTO tests (lost + found)" $unitProject "FullyQualifiedName!~Integration" "xunit-unit.trx"
$failed = $failed -or ($unitExit -ne 0)

if (-not $SkipIntegration) {
    $integrationExit = Invoke-QaPhase "xUnit API integration tests (lost + found)" $unitProject "FullyQualifiedName~LostItemsApiIntegrationTests|FullyQualifiedName~FoundItemsApiIntegrationTests" "xunit-integration.trx"
    $failed = $failed -or ($integrationExit -ne 0)
} else {
    Write-Host "`n=== xUnit API integration tests ===`nSKIPPED by parameter" -ForegroundColor Yellow
}

if (-not $SkipSelenium) {
    if ([string]::IsNullOrWhiteSpace($env:SELENIUM_TEST_EMAIL) -or [string]::IsNullOrWhiteSpace($env:SELENIUM_TEST_PASSWORD)) {
        Write-Host "`nSelenium skipped: set SELENIUM_TEST_EMAIL and SELENIUM_TEST_PASSWORD first." -ForegroundColor Yellow
    } else {
        $seleniumExit = Invoke-QaPhase "Selenium UI tests (lost + found)" $seleniumProject "FullyQualifiedName~ReportLostItemWizardTests|FullyQualifiedName~ReportFoundItemWizardTests" "selenium.trx"
        $failed = $failed -or ($seleniumExit -ne 0)
    }
} else {
    Write-Host "`n=== Selenium UI tests ===`nSKIPPED by parameter" -ForegroundColor Yellow
}

Write-Host "`nTRX files: $resultsDir"
if ($failed) {
    Write-Host "Overall result: ATTENTION REQUIRED" -ForegroundColor Red
    exit 1
}
Write-Host "Overall result: PASSED" -ForegroundColor Green
exit 0
