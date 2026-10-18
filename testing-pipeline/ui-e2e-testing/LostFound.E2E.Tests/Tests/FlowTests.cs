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

    [Fact]
    [Trait("Category", "Demo")]
    [Trait("Category", "Full")]
    [Trait("Description", "An admin kicks a user in User Management; that user can then no longer sign in and stays on the login page. The user is restored afterwards.")]
    public void FLOW_05_AdminKicksUser_UserCannotLogIn() => Run(nameof(FLOW_05_AdminKicksUser_UserCannotLogIn), () =>
    {
        var target = TestConfig.KickTarget;   // reserved throwaway account (see TestConfig)
        var login = new LoginPage(Driver);

        try
        {
            // --- admin kicks the user ---
            login.LoginAs(TestConfig.Admin);
            new AdminHomePage(Driver).WaitUntilLoaded();
            new AdminUsersPage(Driver).Find(target.Email).Kick(target.Email).WaitForStatus(target.Email, "Kicked");

            // --- the kicked user tries to sign in with the correct password ---
            ClearBrowserState();
            login.LoginAs(target);
            var message = login.WaitForErrorMessage();

            Assert.Contains("suspended", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("/login", new Uri(Driver.Url).AbsolutePath);
        }
        finally
        {
            // Always restore the account so the test can be run again and other runs are unaffected.
            ClearBrowserState();
            login.LoginAs(TestConfig.Admin);
            new AdminHomePage(Driver).WaitUntilLoaded();
            new AdminUsersPage(Driver).Find(target.Email).Unkick(target.Email).WaitForStatus(target.Email, "Active");
        }
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "User A renames a report; user B can then find it under the new title and no longer under the old one.")]
    public void FLOW_02_EditByUserA_IsReflectedInUserBSearch() => Run(nameof(FLOW_02_EditByUserA_IsReflectedInUserBSearch), () =>
    {
        var userA = TestConfig.PickUser();
        // The description must not contain the title: search also matches descriptions, so the old
        // title would otherwise still be found after the rename.
        var oldTitle = ItemApi.CreateLostItem(userA, ItemApi.UniqueTitle("Scarf"),
            description: "E2E rename scenario: plain description without the title");
        var newTitle = ItemApi.UniqueTitle("Shawl");

        // --- user A renames the report ---
        new LoginPage(Driver).LoginAs(userA);
        new HomePage(Driver).WaitUntilLoaded();
        var reports = new MyReportsPage(Driver).Open();
        reports.ClickEdit(oldTitle);
        new EditLostItemPage(Driver).WaitUntilLoaded(oldTitle)
            .ReplaceTitle(newTitle)
            .ReEnterHiddenInformation("re-entered private detail")
            .Save()
            .WaitForSavedMessage();

        // --- user B searches ---
        ClearBrowserState();
        new LoginPage(Driver).LoginAs(TestConfig.User1);
        var home = new HomePage(Driver).WaitUntilLoaded();
        home.SearchUntilFound(newTitle);
        home.SearchUntilNoResults(oldTitle);
    });
}
