# Selenium browser tests

These tests cover the Item Service epic through the real frontend in Chrome. Test names and comments identify the visible user behaviour each case verifies.

## Before running

Start these local services first:

1. Frontend at `http://localhost:5173`
2. Auth Service at `http://localhost:5261`
3. Item Service at `http://localhost:5001`
4. Local MySQL with the selected test reports and accounts

The test fixture uses the three localhost URLs above; there is no Selenium base-URL environment variable.

When starting the frontend in the same PowerShell session, use:

```powershell
$env:VITE_AUTH_API_BASE_URL = "http://localhost:5261"
$env:VITE_ITEM_API_BASE_URL = "http://localhost:5001"
$env:VITE_MATCHING_API_BASE_URL = "http://localhost:5002"
$env:VITE_ADMIN_API_BASE_URL = "http://localhost:5003"
```

## Required account variables

Set these for every Selenium run. Do not commit real passwords.

```powershell
$env:SELENIUM_TEST_EMAIL = "<verified-owner-email>"
$env:SELENIUM_TEST_PASSWORD = "<owner-password>"
```

Story 3 non-owner authorization also needs:

```powershell
$env:SELENIUM_NONOWNER_EMAIL = "<verified-non-owner-email>"
$env:SELENIUM_NONOWNER_PASSWORD = "<non-owner-password>"
```

## Story-specific report variables

Only set the groups required by the story you are running. Full-suite execution requires every group below.

```powershell
# Story 3 - Edit Item Report
$env:SELENIUM_EDIT_REPORT_ID = "<active-owner-report-id>"
$env:SELENIUM_EDIT_REPORT_TYPE = "lost" # lost or found; defaults to lost when omitted
$env:SELENIUM_EDIT_HIDDEN_INFORMATION = "<current-private-value>"

# Story 6 - View Item Details: active lost report
$env:SELENIUM_DETAILS_LOST_ID = "<active-lost-id>"
$env:SELENIUM_DETAILS_LOST_TITLE = "<exact-lost-title>"
$env:SELENIUM_DETAILS_LOST_HIDDEN_INFORMATION = "<lost-private-value>"
$env:SELENIUM_DETAILS_LOST_DESCRIPTION = "<exact-description>"
$env:SELENIUM_DETAILS_LOST_CATEGORY = "<exact-category>"
$env:SELENIUM_DETAILS_LOST_LOCATION = "<exact-location>"
$env:SELENIUM_DETAILS_LOST_DATE_DISPLAY = "<date-as-shown-in-browser>"
$env:SELENIUM_DETAILS_LOST_EXPECTS_PHOTO = "true" # or false

# Story 6 - View Item Details: active found report
$env:SELENIUM_DETAILS_FOUND_ID = "<active-found-id>"
$env:SELENIUM_DETAILS_FOUND_TITLE = "<exact-found-title>"
$env:SELENIUM_DETAILS_FOUND_HIDDEN_INFORMATION = "<found-private-value>"
$env:SELENIUM_DETAILS_FOUND_DESCRIPTION = "<exact-description>"
$env:SELENIUM_DETAILS_FOUND_CATEGORY = "<exact-category>"
$env:SELENIUM_DETAILS_FOUND_LOCATION = "<exact-location>"
$env:SELENIUM_DETAILS_FOUND_DATE_DISPLAY = "<date-as-shown-in-browser>"
$env:SELENIUM_DETAILS_FOUND_EXPECTS_PHOTO = "true" # or false
$env:SELENIUM_DETAILS_RESOLVED_NONOWNER_ID = "<resolved-report-owned-by-another-user>"

# Story 7 - Delete Item Report. These reports are consumed by the test.
$env:SELENIUM_DELETE_CANCEL_REPORT_ID = "<active-owner-report-id>"
$env:SELENIUM_DELETE_CANCEL_REPORT_TITLE = "<exact-title>"
$env:SELENIUM_DELETE_LOST_REPORT_ID = "<fresh-active-lost-id>"
$env:SELENIUM_DELETE_LOST_REPORT_TITLE = "<exact-title>"
$env:SELENIUM_DELETE_FOUND_REPORT_ID = "<fresh-active-found-id>"
$env:SELENIUM_DELETE_FOUND_REPORT_TITLE = "<exact-title>"

# Story 8 - View My Reports
$env:SELENIUM_MY_REPORTS_LOST_TITLE = "<owner-lost-title>"
$env:SELENIUM_MY_REPORTS_LOST_STATUS = "ACTIVE"
$env:SELENIUM_MY_REPORTS_FOUND_TITLE = "<owner-found-title>"
$env:SELENIUM_MY_REPORTS_FOUND_STATUS = "ACTIVE"
$env:SELENIUM_MY_REPORTS_RESOLVED_TITLE = "<owner-resolved-title>"
```

## Commands

```powershell
# Story 1 - Report Lost Item
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "FullyQualifiedName~ReportLostItemWizardTests" --no-restore

# Story 2 - Report Found Item
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "FullyQualifiedName~ReportFoundItemWizardTests" --no-restore

# Story 3 - Edit Item Report, including the non-owner check
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "FullyQualifiedName~EditItemReport" --no-restore

# Story 4 - Search and Filter Items
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "FullyQualifiedName~SearchAndFilterItemsSeleniumTests" --no-restore

# Story 6 - View Item Details
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "Story=6" --no-restore

# Story 7 - Delete Item Report
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "Story=7" --no-restore

# Story 8 - View My Reports
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --filter "Story=8" --no-restore

# Full Selenium suite
dotnet test .\tests\e2e\ReportLostItemForm.SeleniumTests\ReportLostItemForm.SeleniumTests.csproj --no-restore
```

Stories 5 and 7 have backend xUnit coverage; Story 5 does not currently have a Selenium class. Story 7 deletion tests permanently delete their configured lost/found reports, so create fresh disposable reports before every run.
