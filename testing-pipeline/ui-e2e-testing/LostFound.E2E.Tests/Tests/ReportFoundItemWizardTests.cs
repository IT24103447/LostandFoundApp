using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// "Report a found item" wizard (test plan section 6.4). Reports are created by different seeded
/// users (PickUser) so none of them trips the spam rule.
/// </summary>
public class ReportFoundItemWizardTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    private readonly ITestOutputHelper _output;

    public ReportFoundItemWizardTests(BrowserFixture fixture, ITestOutputHelper output) : base(fixture) => _output = output;

    private ReportFoundWizardPage SignInAndOpenWizard()
    {
        new LoginPage(Driver).LoginAs(TestConfig.PickUser());
        new HomePage(Driver).WaitUntilLoaded();
        return new ReportFoundWizardPage(Driver).Open();
    }

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "A user completes all three found-item steps, reaches the success page, and the report is listed in My Reports as FOUND.")]
    public void FOUND_01_CompleteWizard_ReachesSuccessPage_AndIsListedAsFound() => Run(nameof(FOUND_01_CompleteWizard_ReachesSuccessPage_AndIsListedAsFound), () =>
    {
        var wizard = SignInAndOpenWizard();

        var (title, _) = wizard.CompleteWithValidData(ItemApi.UniqueTitle("Keys"));
        wizard.WaitForSuccessPage();

        var reports = new MyReportsPage(Driver).Open();
        reports.WaitForReport(title);
        reports.WaitForTypeLabel(title, "FOUND");
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "On each of the three steps, continuing with the required fields empty is blocked and every required-field message can be shown; the test records which messages only appear after a field is visited.")]
    public void FOUND_02_RequiredFields_BlockEachStep() => Run(nameof(FOUND_02_RequiredFields_BlockEachStep), () =>
    {
        var wizard = SignInAndOpenWizard();
        var lateMessages = new List<string>();   // messages that did not appear on Continue

        void Expect(string field, string message)
        {
            if (!wizard.WaitForRequiredMessage(field, message)) lateMessages.Add(field);
        }

        // step 1: nothing entered. The category message is shown on Continue.
        wizard.ContinueExpectingToStay();
        wizard.WaitForValidationMessage("Please select a category.");
        wizard.WaitForStep(1);
        Expect("title", "Item title is required.");
        Expect("description", "Description is required.");

        // step 2: date and location empty
        wizard.EnterTitle(ItemApi.UniqueTitle("Gloves")).ChooseCategory("Accessories")
            .EnterDescription("Found near the cafeteria").Continue(2);
        wizard.ContinueExpectingToStay();
        wizard.WaitForStep(2);
        Expect("dateFound", "Date found is required.");
        Expect("locationFound", "Location found is required.");

        // step 3: hidden information empty (shown when the report is submitted)
        wizard.EnterDateFound(DateOnly.FromDateTime(DateTime.Today.AddDays(-1))).EnterLocationFound("Cafeteria")
            .Continue(3);
        wizard.Submit();
        wizard.WaitForValidationMessage("Hidden information is required.");
        wizard.WaitForStep(3);

        _output.WriteLine(lateMessages.Count == 0
            ? "Observed: every required-field message appeared as soon as Continue was clicked."
            : $"Observed: Continue blocks the step, but the messages for [{string.Join(", ", lateMessages)}] only appeared after the field was visited (defect: no feedback on Continue).");
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Going back from a later step keeps what the user already typed, and moving forward again still has it.")]
    public void FOUND_03_GoingBack_PreservesEnteredData() => Run(nameof(FOUND_03_GoingBack_PreservesEnteredData), () =>
    {
        var wizard = SignInAndOpenWizard();
        var title = ItemApi.UniqueTitle("Scarf");

        wizard.EnterTitle(title).ChooseCategory("Accessories").EnterDescription("Found on a bench").Continue(2);
        wizard.EnterDateFound(DateOnly.FromDateTime(DateTime.Today.AddDays(-1))).EnterLocationFound("Main gate");

        wizard.Back(1);
        Assert.Equal(title, wizard.TitleValue());

        wizard.Continue(2);
        Assert.Equal("Main gate", wizard.LocationValue());
    });
}
