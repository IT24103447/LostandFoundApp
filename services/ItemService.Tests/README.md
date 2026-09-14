# ItemService.Tests

This project contains the xUnit coverage for the Item Service epic. Test names and the short comments above each test section identify the user-story behaviour being verified.

## Before running

- Controller, DTO, model, and service tests run without any locally running application.
- API/MySQL integration tests start a disposable MySQL Testcontainers database. Start Docker Desktop before those tests or before the full suite.
- Do not start the frontend, Auth Service, or Item Service for these xUnit tests. `WebApplicationFactory` starts the API in-process.

Run all commands from the repository root.

## Run one story at a time

```powershell
# Story 1 - Report a Lost Item
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~LostItemsControllerTests|FullyQualifiedName~ReportLostItemRequestValidationTests|FullyQualifiedName~LostItemsApiIntegrationTests|FullyQualifiedName~ItemPhotoModelsTests|FullyQualifiedName~ImageSignatureValidatorTests|FullyQualifiedName~AzureBlobPhotoStorageServiceTests|FullyQualifiedName~KafkaEventPublisherTests" --no-restore

# Story 2 - Report a Found Item
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~FoundItemsControllerTests|FullyQualifiedName~ReportFoundItemRequestValidationTests|FullyQualifiedName~FoundItemsApiIntegrationTests|FullyQualifiedName~ItemPhotoModelsTests|FullyQualifiedName~ImageSignatureValidatorTests|FullyQualifiedName~AzureBlobPhotoStorageServiceTests|FullyQualifiedName~KafkaEventPublisherTests" --no-restore

# Story 3 - Edit an Item Report
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~EditItemReportStoryTests|FullyQualifiedName~UpdateItemRequestValidationTests" --no-restore

# Story 4 - Search and Filter Items
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~SearchAndFilterItemsStoryTests" --no-restore

# Story 5 - Mark an Item as Resolved
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~MarkItemResolvedStoryTests|FullyQualifiedName~MarkItemResolvedApiIntegrationTests" --no-restore

# Story 6 - View Item Details
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~ViewItemDetailsStoryTests|FullyQualifiedName~ViewItemDetailsApiIntegrationTests" --no-restore

# Story 7 - Delete an Item Report
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~DeleteItemReportStoryTests|FullyQualifiedName~DeleteItemReportApiIntegrationTests" --no-restore

# Story 8 - View My Reports
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --filter "FullyQualifiedName~MyReportsStoryTests|FullyQualifiedName~MyReportsApiIntegrationTests" --no-restore
```

## Run the complete epic

```powershell
dotnet test .\services\ItemService.Tests\ItemService.Tests.csproj --no-restore
```

The full command runs every xUnit test in this project, including API/MySQL integration tests. A test failure remains a failure; this project does not retry or suppress failing tests.
