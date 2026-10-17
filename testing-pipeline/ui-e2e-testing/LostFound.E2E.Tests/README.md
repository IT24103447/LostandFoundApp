# LostFound.E2E.Tests

Selenium + xUnit browser tests for the Lost & Found app (SE3112 UI / E2E role).

## Run (app already started with testing-pipeline/shared/start-environment.sh)

Visible browser (demo):
    dotnet test

Headless (CI):
    E2E_HEADLESS=true dotnet test

One group only:
    dotnet test --filter "Category=Smoke"
    dotnet test --filter "Category=Demo"

## Settings (environment variables)
E2E_BASE_URL (default http://localhost:5173), E2E_HEADLESS (default false),
E2E_SCREENSHOT_DIR, E2E_USER1_EMAIL / E2E_USER1_PASSWORD, E2E_ADMIN_EMAIL / E2E_ADMIN_PASSWORD.

## Layout
Support/  fixture, config, explicit waits, base class
Pages/    page objects (one per screen)
Tests/    test classes, one per area
