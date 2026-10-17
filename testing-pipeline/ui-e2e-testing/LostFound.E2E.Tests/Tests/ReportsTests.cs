using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// My Reports journeys: view, edit and delete (test plan section 6.5). Each test creates its own
/// uniquely titled report through the API (arrange) and then works with it in the browser.
/// </summary>
public class ReportsTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public ReportsTests(BrowserFixture fixture) : base(fixture) { }

    /// <summary>Creates a report for a random seeded user, signs that user in and opens My Reports.</summary>
    private (string Title, MyReportsPage Page) SignInWithNewReport(string prefix)
    {
        var owner = TestConfig.PickUser();
        var title = ItemApi.CreateLostItem(owner, ItemApi.UniqueTitle(prefix));
        new LoginPage(Driver).LoginAs(owner);
        new HomePage(Driver).WaitUntilLoaded();
        return (title, new MyReportsPage(Driver).Open());
    }

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "A lost report the user created is listed in My Reports with the LOST label.")]
    public void RPT_01_NewLostReport_IsListedWithTypeLabel() => Run(nameof(RPT_01_NewLostReport_IsListedWithTypeLabel), () =>
    {
        var (title, reports) = SignInWithNewReport("Backpack");

        reports.WaitForReport(title);
        reports.WaitForTypeLabel(title, "LOST");
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Editing a report's title shows 'Changes saved.' and the new title replaces the old one in My Reports.")]
    public void RPT_02_EditTitle_IsSavedAndShownInMyReports() => Run(nameof(RPT_02_EditTitle_IsSavedAndShownInMyReports), () =>
    {
        var (title, reports) = SignInWithNewReport("Satchel");
        var newTitle = ItemApi.UniqueTitle("Renamed satchel");

        reports.ClickEdit(title);
        new EditLostItemPage(Driver).WaitUntilLoaded(title)
            .ReplaceTitle(newTitle)
            .ReEnterHiddenInformation("re-entered private detail")   // never pre-filled by design
            .Save()
            .WaitForSavedMessage();

        reports.Open();
        reports.WaitForReport(newTitle);
        reports.WaitForReportGone(title);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Saving an edit with the title cleared shows a validation error and the old title is kept.")]
    public void RPT_03_EditWithEmptyTitle_ShowsErrorAndKeepsOldTitle() => Run(nameof(RPT_03_EditWithEmptyTitle_ShowsErrorAndKeepsOldTitle), () =>
    {
        var (title, reports) = SignInWithNewReport("Helmet");

        reports.ClickEdit(title);
        var edit = new EditLostItemPage(Driver).WaitUntilLoaded(title).ReplaceTitle("").Save();
        edit.WaitForValidationMessage("Item title is required.");

        reports.Open();
        reports.WaitForReport(title);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Choosing Delete and then Cancel in the confirmation dialog leaves the report in place.")]
    public void RPT_04_DeleteThenCancel_KeepsReport() => Run(nameof(RPT_04_DeleteThenCancel_KeepsReport), () =>
    {
        var (title, reports) = SignInWithNewReport("Flask");

        reports.ClickDelete(title).CancelDelete();

        reports.WaitForReport(title);
    });

    [Fact]
    [Trait("Category", "Demo")]
    [Trait("Category", "Full")]
    [Trait("Description", "Confirming Delete removes the report from My Reports, and another user can no longer find it by search.")]
    public void RPT_05_DeleteConfirmed_RemovesReportEverywhere() => Run(nameof(RPT_05_DeleteConfirmed_RemovesReportEverywhere), () =>
    {
        var (title, reports) = SignInWithNewReport("Umbrella");

        reports.ClickDelete(title).ConfirmDelete();
        reports.WaitForReportGone(title);

        // a different user must not find it any more
        ClearBrowserState();
        new LoginPage(Driver).LoginAs(TestConfig.User1);
        var home = new HomePage(Driver).WaitUntilLoaded();
        home.Search(title);
        home.WaitForNoResults();
    });
}
