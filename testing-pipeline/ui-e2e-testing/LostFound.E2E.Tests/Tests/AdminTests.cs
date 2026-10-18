using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// Admin area (test plan section 6.6). The only account these tests ever change is the reserved
/// kick target (TestConfig.KickTarget); every test that kicks it restores it in a finally block.
/// </summary>
public class AdminTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public AdminTests(BrowserFixture fixture) : base(fixture) { }

    private AdminUsersPage SignInAsAdmin()
    {
        new LoginPage(Driver).LoginAs(TestConfig.Admin);
        new AdminHomePage(Driver).WaitUntilLoaded();
        return new AdminUsersPage(Driver);
    }

    /// <summary>Makes sure the reserved account is active again, whatever state a test left it in.</summary>
    private void RestoreKickTarget()
    {
        var target = TestConfig.KickTarget;
        ClearBrowserState();
        var users = SignInAsAdmin().Find(target.Email);
        if (users.IsKicked(target.Email))
            users.Unkick(target.Email).WaitForStatus(target.Email, "Active");
    }

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "The admin's User Management page lists accounts, including a seeded user.")]
    public void ADM_01_UserManagement_ListsAccounts() => Run(nameof(ADM_01_UserManagement_ListsAccounts), () =>
    {
        var users = SignInAsAdmin();

        Assert.True(users.WaitForRows() > 0);
        users.Find(TestConfig.User1.Email);   // a known seeded account can be found in the list
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Searching by e-mail and then by name narrows the list to that account; a search with no match shows the empty state.")]
    public void ADM_02_Search_ByEmailAndName_NarrowsList_AndNoMatchShowsEmptyState() => Run(nameof(ADM_02_Search_ByEmailAndName_NarrowsList_AndNoMatchShowsEmptyState), () =>
    {
        var email = TestConfig.User1.Email;
        var users = SignInAsAdmin();

        users.Find(email).WaitForOnlyRow(email);

        var name = users.ReadName(email);
        users.Search(name);
        users.WaitForOnlyRow(email);          // the name is unique to this account

        users.Search($"no-such-user-{Guid.NewGuid():N}");
        users.WaitForNoUsers();
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Clicking Kick shows a warning dialog; choosing Cancel leaves the user active.")]
    public void ADM_03_KickThenCancel_LeavesUserActive() => Run(nameof(ADM_03_KickThenCancel_LeavesUserActive), () =>
    {
        var target = TestConfig.KickTarget;
        var users = SignInAsAdmin().Find(target.Email);

        users.OpenKickDialog(target.Email);
        Assert.Contains("kick", users.ReadDialogMessage(), StringComparison.OrdinalIgnoreCase);
        users.CancelDialog();

        users.WaitForStatus(target.Email, "Active");
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Confirming Kick marks the user as Kicked in the list.")]
    public void ADM_04_KickConfirmed_MarksUserKicked() => Run(nameof(ADM_04_KickConfirmed_MarksUserKicked), () =>
    {
        var target = TestConfig.KickTarget;
        try
        {
            SignInAsAdmin().Find(target.Email).Kick(target.Email).WaitForStatus(target.Email, "Kicked");
        }
        finally
        {
            RestoreKickTarget();
        }
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "The Match Appeals and Spam Review pages load for an admin without an error.")]
    public void ADM_05_MatchAppealsAndSpamReview_Load() => Run(nameof(ADM_05_MatchAppealsAndSpamReview_Load), () =>
    {
        SignInAsAdmin();
        var sections = new AdminSectionPage(Driver);

        Assert.False(sections.OpenMatchAppeals().HasErrorBanner());
        Assert.False(sections.OpenSpamReview().HasErrorBanner());
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "After an admin kicks and then unkicks a user, that user can sign in again.")]
    public void ADM_06_UnkickedUser_CanLogInAgain() => Run(nameof(ADM_06_UnkickedUser_CanLogInAgain), () =>
    {
        var target = TestConfig.KickTarget;
        try
        {
            var users = SignInAsAdmin().Find(target.Email);
            users.Kick(target.Email).WaitForStatus(target.Email, "Kicked");
            users.Unkick(target.Email).WaitForStatus(target.Email, "Active");

            ClearBrowserState();
            new LoginPage(Driver).LoginAs(target);
            new HomePage(Driver).WaitUntilLoaded();
        }
        finally
        {
            RestoreKickTarget();
        }
    });
}
