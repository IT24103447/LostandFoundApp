using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

// Boundary-value and input-sanitising tests for the two report wizards (test plan sections 6.3 / 6.4).
//
// Each [Theory] row is one field of the wizard. The expected messages are looked up from the tables
// below by field id (not passed through InlineData) so that the test names in the TRX stay short.
//
// No row submits a finished report, so these tests create no data and cannot trip the spam rule. The
// one boundary they therefore do not cover is "hidden information of exactly 500 characters is
// accepted", because proving that means submitting the wizard.

/// <summary>Report-lost wizard: field length limits and whitespace-only input.</summary>
public class ReportLostWizardBoundaryTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public ReportLostWizardBoundaryTests(BrowserFixture fixture) : base(fixture) { }

    // field id -> (maximum length, message when too long, message when blank)
    private static readonly Dictionary<string, (int Max, string TooLong, string Required)> Fields = new()
    {
        ["title"] = (150, "Title must be at most 150 characters.", "Item title is required."),
        ["description"] = (2000, "Description must be at most 2000 characters.", "Description is required."),
        ["lastKnownLocation"] = (255, "Last known location must be at most 255 characters.", "Last known location is required."),
        ["hiddenInformation"] = (500, "Hidden information must be at most 500 characters.", "Hidden information is required."),
    };

    private static int StepOf(string field) => field switch
    {
        "lastKnownLocation" => 2,
        "hiddenInformation" => 3,
        _ => 1,
    };

    /// <summary>
    /// Signs in as a seeded user and opens the wizard on the step that holds <paramref name="field"/>, with
    /// every other required field of that step (and of the earlier steps) already valid.
    /// </summary>
    private ReportLostWizardPage OpenOnStepOf(string field)
    {
        new LoginPage(Driver).LoginAs(TestConfig.PickUser());
        new HomePage(Driver).WaitUntilLoaded();
        var wizard = new ReportLostWizardPage(Driver).Open();
        var title = ItemApi.UniqueTitle("Boundary");

        switch (StepOf(field))
        {
            case 1:
                wizard.EnterTitle(title).ChooseCategory("Accessories").EnterDescription("Boundary check");
                break;
            case 2:
                wizard.EnterTitle(title).ChooseCategory("Accessories").EnterDescription("Boundary check").Continue(2);
                wizard.EnterDateLost(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
                break;
            default:
                wizard.GoToStepThree(title);
                break;
        }
        return wizard;
    }

    /// <summary>Tries to leave the step: Submit on the last step, Continue on the others.</summary>
    private static void TryToAdvance(ReportLostWizardPage wizard, string field)
    {
        if (field == "hiddenInformation") wizard.Submit();
        else wizard.ContinueExpectingToStay();
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("lastKnownLocation")]
    [InlineData("hiddenInformation")]
    [Trait("Category", "Full")]
    [Trait("Description", "A lost-item field that contains only spaces is treated as empty: the required-field error is shown and the wizard stays on that step.")]
    public void LOST_10_WhitespaceOnlyField_IsTreatedAsEmpty(string field) => Run($"{nameof(LOST_10_WhitespaceOnlyField_IsTreatedAsEmpty)}_{field}", () =>
    {
        var wizard = OpenOnStepOf(field);

        wizard.SetField(field, "   ");
        TryToAdvance(wizard, field);

        wizard.WaitForValidationMessage(Fields[field].Required);
        wizard.WaitForStep(StepOf(field));
    });

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("lastKnownLocation")]
    [InlineData("hiddenInformation")]
    [Trait("Category", "Full")]
    [Trait("Description", "A lost-item field one character over its limit (title 150, description 2000, location 255, hidden information 500) is rejected with a length error and the wizard stays on that step.")]
    public void LOST_11_OneCharacterOverTheLimit_IsRejected(string field) => Run($"{nameof(LOST_11_OneCharacterOverTheLimit_IsRejected)}_{field}", () =>
    {
        var wizard = OpenOnStepOf(field);

        wizard.SetField(field, new string('a', Fields[field].Max + 1));
        TryToAdvance(wizard, field);

        wizard.WaitForValidationMessage(Fields[field].TooLong);
        wizard.WaitForStep(StepOf(field));
    });

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("lastKnownLocation")]
    [Trait("Category", "Full")]
    [Trait("Description", "A lost-item field of exactly its maximum length (title 150, description 2000, location 255) is accepted and the wizard moves on to the next step.")]
    public void LOST_12_ExactlyAtTheLimit_IsAccepted(string field) => Run($"{nameof(LOST_12_ExactlyAtTheLimit_IsAccepted)}_{field}", () =>
    {
        var wizard = OpenOnStepOf(field);

        wizard.SetField(field, new string('a', Fields[field].Max));

        wizard.Continue(StepOf(field) + 1);
    });
}

/// <summary>Report-found wizard: field length limits and whitespace-only input.</summary>
public class ReportFoundWizardBoundaryTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public ReportFoundWizardBoundaryTests(BrowserFixture fixture) : base(fixture) { }

    private static readonly Dictionary<string, (int Max, string TooLong, string Required)> Fields = new()
    {
        ["title"] = (150, "Title must be at most 150 characters.", "Item title is required."),
        ["description"] = (2000, "Description must be at most 2000 characters.", "Description is required."),
        ["locationFound"] = (255, "Location found must be at most 255 characters.", "Location found is required."),
        ["hiddenInformation"] = (500, "Hidden information must be at most 500 characters.", "Hidden information is required."),
    };

    private static int StepOf(string field) => field switch
    {
        "locationFound" => 2,
        "hiddenInformation" => 3,
        _ => 1,
    };

    private ReportFoundWizardPage OpenOnStepOf(string field)
    {
        new LoginPage(Driver).LoginAs(TestConfig.PickUser());
        new HomePage(Driver).WaitUntilLoaded();
        var wizard = new ReportFoundWizardPage(Driver).Open();
        var title = ItemApi.UniqueTitle("Boundary");
        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));

        switch (StepOf(field))
        {
            case 1:
                wizard.EnterTitle(title).ChooseCategory("Accessories").EnterDescription("Boundary check");
                break;
            case 2:
                wizard.EnterTitle(title).ChooseCategory("Accessories").EnterDescription("Boundary check").Continue(2);
                wizard.EnterDateFound(yesterday);
                break;
            default:
                wizard.EnterTitle(title).ChooseCategory("Accessories").EnterDescription("Boundary check").Continue(2);
                wizard.EnterDateFound(yesterday).EnterLocationFound("Main campus library").Continue(3);
                break;
        }
        return wizard;
    }

    private static void TryToAdvance(ReportFoundWizardPage wizard, string field)
    {
        if (field == "hiddenInformation") wizard.Submit();
        else wizard.ContinueExpectingToStay();
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("locationFound")]
    [InlineData("hiddenInformation")]
    [Trait("Category", "Full")]
    [Trait("Description", "A found-item field that contains only spaces is treated as empty: the required-field error is shown and the wizard stays on that step.")]
    public void FOUND_04_WhitespaceOnlyField_IsTreatedAsEmpty(string field) => Run($"{nameof(FOUND_04_WhitespaceOnlyField_IsTreatedAsEmpty)}_{field}", () =>
    {
        var wizard = OpenOnStepOf(field);

        wizard.SetField(field, "   ");
        TryToAdvance(wizard, field);

        // This form sometimes raises a message only once the field was visited; the helper handles both.
        wizard.WaitForRequiredMessage(field, Fields[field].Required);
        wizard.WaitForStep(StepOf(field));
    });

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("locationFound")]
    [InlineData("hiddenInformation")]
    [Trait("Category", "Full")]
    [Trait("Description", "A found-item field one character over its limit (title 150, description 2000, location 255, hidden information 500) is rejected with a length error and the wizard stays on that step.")]
    public void FOUND_05_OneCharacterOverTheLimit_IsRejected(string field) => Run($"{nameof(FOUND_05_OneCharacterOverTheLimit_IsRejected)}_{field}", () =>
    {
        var wizard = OpenOnStepOf(field);

        wizard.SetField(field, new string('a', Fields[field].Max + 1));
        TryToAdvance(wizard, field);

        wizard.WaitForRequiredMessage(field, Fields[field].TooLong);
        wizard.WaitForStep(StepOf(field));
    });

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("locationFound")]
    [Trait("Category", "Full")]
    [Trait("Description", "A found-item field of exactly its maximum length (title 150, description 2000, location 255) is accepted and the wizard moves on to the next step.")]
    public void FOUND_06_ExactlyAtTheLimit_IsAccepted(string field) => Run($"{nameof(FOUND_06_ExactlyAtTheLimit_IsAccepted)}_{field}", () =>
    {
        var wizard = OpenOnStepOf(field);

        wizard.SetField(field, new string('a', Fields[field].Max));

        wizard.Continue(StepOf(field) + 1);
    });
}
