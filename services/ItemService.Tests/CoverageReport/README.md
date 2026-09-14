# Item Service Code Coverage Report

This folder contains the published HTML code-coverage evidence for the Item Service automated xUnit test suite.

## Current result

The report was generated on 14 September 2026 from Cobertura coverage data produced by Coverlet:

- Line coverage: 81.1%
- Branch coverage: 76.7%
- Method coverage: 94.6%

Open `index.html` in a browser to view the full class-by-class and line-by-line report.

## How it was generated

1. Run the Item Service xUnit test suite with the Coverlet collector:

   ```powershell
   dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --no-restore --collect:"XPlat Code Coverage" --results-directory .\services\ItemService.Tests\TestResults\Coverage
   ```

2. Coverlet writes the raw `coverage.cobertura.xml` file below `TestResults\Coverage`. Raw test results and XML are intentionally excluded from Git.

3. Generate this HTML report using ReportGenerator:

   ```powershell
   reportgenerator `
     "-reports:.\services\ItemService.Tests\TestResults\Coverage\**\coverage.cobertura.xml" `
     "-targetdir:.\services\ItemService.Tests\CoverageReport" `
     "-reporttypes:Html;TextSummary"
   ```

4. Open the report locally:

   ```powershell
   Start-Process .\services\ItemService.Tests\CoverageReport\index.html
   ```

`Summary.txt` contains the same high-level percentages in plain text.
