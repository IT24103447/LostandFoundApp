using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// Browse, search and filter journeys (test plan section 6.2). Each test creates its own uniquely
/// titled report through the API (arrange), then verifies what a signed-in user sees in the browser.
/// </summary>
public class BrowseTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public BrowseTests(BrowserFixture fixture) : base(fixture) { }

    private HomePage SignInAndOpenBrowse()
    {
        new LoginPage(Driver).LoginAs(TestConfig.User1);
        return new HomePage(Driver).WaitUntilLoaded();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Demo")]
    [Trait("Description", "A report created by another user can be found by searching for its title.")]
    public void BRW_01_KeywordSearch_FindsReportCreatedByAnotherUser() => Run(nameof(BRW_01_KeywordSearch_FindsReportCreatedByAnotherUser), () =>
    {
        var title = ItemApi.CreateLostItem(TestConfig.PickUser(), ItemApi.UniqueTitle("Wallet"));
        var home = SignInAndOpenBrowse();

        home.Search(title);

        home.WaitForResult(title);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Searching for a term that matches nothing shows the empty state, not an error.")]
    public void BRW_02_SearchWithNoMatches_ShowsEmptyState() => Run(nameof(BRW_02_SearchWithNoMatches_ShowsEmptyState), () =>
    {
        var home = SignInAndOpenBrowse();

        home.Search($"nothing-{Guid.NewGuid():N}");

        home.WaitForNoResults();
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "The category filter hides a report from a different category and shows it again for the right one.")]
    public void BRW_03_CategoryFilter_NarrowsResultsToMatchingCategory() => Run(nameof(BRW_03_CategoryFilter_NarrowsResultsToMatchingCategory), () =>
    {
        var title = ItemApi.CreateLostItem(TestConfig.PickUser(), ItemApi.UniqueTitle("Watch"), "Accessories");
        var home = SignInAndOpenBrowse();
        home.Search(title);
        home.WaitForResult(title);

        home.ApplyCategory("Electronics");
        home.WaitForNoResults();

        home.ApplyCategory("Accessories");
        home.WaitForResult(title);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Choosing the Found type hides a lost report; removing the filter chip brings it back.")]
    public void BRW_04_TypeFilterChip_CanBeAppliedAndRemoved() => Run(nameof(BRW_04_TypeFilterChip_CanBeAppliedAndRemoved), () =>
    {
        var title = ItemApi.CreateLostItem(TestConfig.PickUser(), ItemApi.UniqueTitle("Scarf"), "Clothing");
        var home = SignInAndOpenBrowse();
        home.Search(title);
        home.WaitForResult(title);

        home.ChooseType("Found");
        home.WaitForNoResults();

        home.RemoveFirstFilterChip();
        home.WaitForResult(title);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Keyword, category and type filters combine: only reports matching all of them stay in the results.")]
    public void BRW_05_CombinedFilters_ShowOnlyTheIntersection() => Run(nameof(BRW_05_CombinedFilters_ShowOnlyTheIntersection), () =>
    {
        var owner = TestConfig.PickUser();
        var keyword = ItemApi.UniqueTitle("Gizmo");
        var accessory = ItemApi.CreateLostItem(owner, $"{keyword} charger", "Accessories");
        var bag = ItemApi.CreateLostItem(owner, $"{keyword} backpack", "Bags");

        var home = SignInAndOpenBrowse();
        home.Search(keyword);
        home.WaitForResult(accessory);
        home.WaitForResult(bag);

        home.ApplyCategory("Accessories");              // keyword + category
        home.WaitForResultGone(bag);
        home.WaitForResult(accessory);

        home.ChooseType("Found");                       // keyword + category + type: both lost reports excluded
        home.WaitForNoResults();

        home.ChooseType("Lost");                        // back to the intersection
        home.WaitForResult(accessory);
        home.WaitForResultGone(bag);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Clear Filters on an empty result resets the search and brings the results back.")]
    public void BRW_06_ClearFilters_FromEmptyResult_RestoresResults() => Run(nameof(BRW_06_ClearFilters_FromEmptyResult_RestoresResults), () =>
    {
        ItemApi.CreateLostItem(TestConfig.PickUser(), ItemApi.UniqueTitle("Bottle"));   // guarantees something to show
        var home = SignInAndOpenBrowse();

        home.Search($"nothing-{Guid.NewGuid():N}");
        home.WaitForNoResults();

        home.ClearFiltersFromEmptyState();

        home.WaitForAnyResult();
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Clicking a search result opens its details page showing the title, description and location.")]
    public void BRW_07_ClickingAResult_OpensItsDetails() => Run(nameof(BRW_07_ClickingAResult_OpensItsDetails), () =>
    {
        var title = ItemApi.CreateLostItem(TestConfig.PickUser(), ItemApi.UniqueTitle("Calculator"));
        var home = SignInAndOpenBrowse();
        home.Search(title);

        home.OpenResult(title);

        var details = new ItemDetailsPage(Driver).WaitUntilLoaded(title);
        details.WaitForDescription($"E2E test report: {title}");
        details.WaitForLocation("Main campus library");
    });
}
