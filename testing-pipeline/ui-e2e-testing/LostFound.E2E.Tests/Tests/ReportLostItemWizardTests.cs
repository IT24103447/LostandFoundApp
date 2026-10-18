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

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "The date field's latest allowed date is today, and a future date is rejected with an inline error on step 2.")]
    public void LOST_05_FutureDate_IsNotAccepted() => Run(nameof(LOST_05_FutureDate_IsNotAccepted), () =>
    {
        var wizard = SignInAndOpenWizard();
        wizard.EnterTitle(ItemApi.UniqueTitle("Hat")).ChooseCategory("Accessories")
            .EnterDescription("A description").Continue(2);

        // the app derives the limit from the UTC date (toISOString)
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), wizard.DateMaxAttribute());

        wizard.EnterDateLost(DateOnly.FromDateTime(DateTime.Today.AddDays(2))).EnterLocation("Main campus library");
        wizard.ContinueExpectingToStay();

        wizard.WaitForValidationMessage("Date lost cannot be in the future.");
        wizard.WaitForStep(2);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "A text file chosen in the photo dropzone is ignored (no preview), while an image chosen afterwards is previewed.")]
    public void LOST_06_NonImageFile_IsIgnored() => Run(nameof(LOST_06_NonImageFile_IsIgnored), () =>
    {
        var wizard = SignInAndOpenWizard().GoToStepThree(ItemApi.UniqueTitle("Purse"));
        var notes = TestFiles.TextFile();
        var photo = TestFiles.ValidImage();

        wizard.UploadPhoto(notes);
        wizard.UploadPhoto(photo);   // once this preview shows, the text file has certainly been processed

        wizard.WaitForPhotoPreview(Path.GetFileName(photo));
        Assert.False(wizard.HasRemoveButtonFor(Path.GetFileName(notes)));
        Assert.Equal(1, wizard.PhotoPreviewCount());
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "A valid image is previewed, and once it is added no further photo can be added (one-photo limit).")]
    public void LOST_07_ValidImage_IsPreviewed_AndOnePhotoLimitApplies() => Run(nameof(LOST_07_ValidImage_IsPreviewed_AndOnePhotoLimitApplies), () =>
    {
        var wizard = SignInAndOpenWizard().GoToStepThree(ItemApi.UniqueTitle("Mug"));
        var photo = TestFiles.ValidImage();

        wizard.UploadPhoto(photo);

        wizard.WaitForPhotoPreview(Path.GetFileName(photo));
        Assert.Equal(1, wizard.PhotoPreviewCount());
        Assert.False(wizard.CanAddAnotherPhoto());
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "A photo just over the 5 MB limit is refused with an inline error when the report is submitted.")]
    public void LOST_09_OversizedPhoto_IsRejectedOnSubmit() => Run(nameof(LOST_09_OversizedPhoto_IsRejectedOnSubmit), () =>
    {
        var wizard = SignInAndOpenWizard().GoToStepThree(ItemApi.UniqueTitle("Tripod"));

        wizard.UploadPhoto(TestFiles.OversizedImage());
        wizard.EnterHiddenInformation("a private detail").Submit();

        wizard.WaitForValidationMessage("Each photo must be at most 5 MB.");
    });
}
