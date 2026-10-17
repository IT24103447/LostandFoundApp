using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// "Report a lost item" wizard (test plan section 6.3). Reports are created by different seeded
/// users (PickUser) so none of them trips the spam rule.
/// </summary>
public class ReportLostItemWizardTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public ReportLostItemWizardTests(BrowserFixture fixture) : base(fixture) { }

    private ReportLostWizardPage SignInAndOpenWizard()
    {
        new LoginPage(Driver).LoginAs(TestConfig.PickUser());
        new HomePage(Driver).WaitUntilLoaded();
        return new ReportLostWizardPage(Driver).Open();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Demo")]
    [Trait("Description", "A user completes all three wizard steps and reaches the success page showing the new report.")]
    public void LOST_01_CompleteWizard_ReachesSuccessPage() => Run(nameof(LOST_01_CompleteWizard_ReachesSuccessPage), () =>
    {
        var wizard = SignInAndOpenWizard();

        var (title, _) = wizard.CompleteWithValidData(ItemApi.UniqueTitle("Umbrella"));

        wizard.WaitForSuccessPage();
        Assert.Contains(title, wizard.PageSource);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Step 1 cannot be continued without a title; an inline error is shown and the user stays on step 1.")]
    public void LOST_02_Step1_EmptyTitle_BlocksContinue() => Run(nameof(LOST_02_Step1_EmptyTitle_BlocksContinue), () =>
    {
        var wizard = SignInAndOpenWizard();
        wizard.ChooseCategory("Bags").EnterDescription("A description but no title");

        wizard.ContinueExpectingToStay();

        wizard.WaitForValidationMessage("Item title is required.");
        wizard.WaitForStep(1);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Step 1 cannot be continued without a description; an inline error is shown and the user stays on step 1.")]
    public void LOST_03_Step1_EmptyDescription_BlocksContinue() => Run(nameof(LOST_03_Step1_EmptyDescription_BlocksContinue), () =>
    {
        var wizard = SignInAndOpenWizard();
        wizard.EnterTitle(ItemApi.UniqueTitle("Backpack")).ChooseCategory("Bags");

        wizard.ContinueExpectingToStay();

        wizard.WaitForValidationMessage("Description is required.");
        wizard.WaitForStep(1);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Going back from step 2 keeps what the user already typed in step 1.")]
    public void LOST_04_GoingBack_PreservesEnteredData() => Run(nameof(LOST_04_GoingBack_PreservesEnteredData), () =>
    {
        var wizard = SignInAndOpenWizard();
        var title = ItemApi.UniqueTitle("Keys");
        wizard.EnterTitle(title).ChooseCategory("Keys").EnterDescription("Set of keys").Continue(2);

        wizard.Back(1);

        Assert.Equal(title, wizard.TitleValue());
    });

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Description", "The private hidden-information text never appears on the success page, which is visible to others.")]
    public void LOST_08_HiddenInformation_IsNeverShownOnSuccessPage() => Run(nameof(LOST_08_HiddenInformation_IsNeverShownOnSuccessPage), () =>
    {
        var wizard = SignInAndOpenWizard();

        var (_, hidden) = wizard.CompleteWithValidData(ItemApi.UniqueTitle("Ring"));

        wizard.WaitForSuccessPage();
        Assert.DoesNotContain(hidden, wizard.PageSource);
    });
}
