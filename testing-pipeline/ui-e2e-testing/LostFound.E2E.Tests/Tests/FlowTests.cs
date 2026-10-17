using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// Cross-service end-to-end journeys (test plan section 6.7). Each test drives two real user
/// sessions through the browser, so it exercises the auth service (login), the item service
/// (create, search, details) and the Kafka publish between them.
/// </summary>
public class FlowTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public FlowTests(BrowserFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Demo")]
    [Trait("Category", "Full")]
    [Trait("Description", "User A reports a lost item through the wizard and signs out; user B signs in, finds it by search and opens its details. The private hidden information is never shown to B.")]
    public void FLOW_01_ReportByUserA_IsFoundByUserB() => Run(nameof(FLOW_01_ReportByUserA_IsFoundByUserB), () =>
    {
        var userA = TestConfig.PickUser();   // reporter: spread across user2..user19 (spam rule)
        var userB = TestConfig.User1;        // searcher
        var login = new LoginPage(Driver);

        // --- user A reports a lost item ---
        login.LoginAs(userA);
        new HomePage(Driver).WaitUntilLoaded();
        var wizard = new ReportLostWizardPage(Driver).Open();
        var (title, hidden) = wizard.CompleteWithValidData(ItemApi.UniqueTitle("Navy umbrella"));
        wizard.WaitForSuccessPage();

        // --- it is listed in A's own reports, then A signs out ---
        new MyReportsPage(Driver).Open().WaitForReport(title);
        new AccountMenuPage(Driver).SignOut();

        // --- user B finds it and opens the details ---
        login.LoginAs(userB);
        var home = new HomePage(Driver).WaitUntilLoaded();
        home.SearchUntilFound(title);
        home.OpenResult(title);

        var details = new ItemDetailsPage(Driver).WaitUntilLoaded(title);
        details.WaitForDescription($"E2E description for {title}");
        details.WaitForLocation("Main campus library");
        Assert.DoesNotContain(hidden, details.PageSource);
    });
}
