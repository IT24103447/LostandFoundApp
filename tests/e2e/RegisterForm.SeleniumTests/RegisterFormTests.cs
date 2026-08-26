using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace RegisterForm.SeleniumTests;

// UI-level tests against the real registration form at http://localhost:5173/register.
// These test what the USER sees (inline errors, success messages, redirects) — as
// opposed to the xUnit API tests, which test what the SERVER returns.
//
// BEFORE RUNNING:
//   1. Backend must be running:  dotnet run   (in services/AuthService)
//   2. Frontend must be running: npm run dev  (in frontend/)
//   3. Google Chrome must be installed
public class RegisterFormTests : IClassFixture<BrowserFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly WebDriverWait _registerWait;

    public RegisterFormTests(BrowserFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(8));
        // Registration involves a real SMTP send (Mailtrap) before the API responds,
        // which can be slower than typical UI actions — give that specific wait more room.
        _registerWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(20));
    }

    private void GoToRegisterPage()
    {
        // Note: RegisterPage is mounted at the root path ("" in App.tsx's <Routes>),
        // not "/register" — confirmed against the actual App.tsx routing.
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/");
        _wait.Until(d => d.FindElement(By.Id("email")));
    }

    private void FillForm(string name, string email, string phone, string password, string? confirmPassword = null)
    {
        _driver.FindElement(By.Id("name")).Clear();
        _driver.FindElement(By.Id("name")).SendKeys(name);

        _driver.FindElement(By.Id("email")).Clear();
        _driver.FindElement(By.Id("email")).SendKeys(email);

        _driver.FindElement(By.Id("phoneNo")).Clear();
        _driver.FindElement(By.Id("phoneNo")).SendKeys(phone);

        _driver.FindElement(By.Id("password")).Clear();
        _driver.FindElement(By.Id("password")).SendKeys(password);

        _driver.FindElement(By.Id("confirmPassword")).Clear();
        _driver.FindElement(By.Id("confirmPassword")).SendKeys(confirmPassword ?? password);
    }

    private void Submit()
    {
        _driver.FindElement(By.CssSelector("button[type='submit']")).Click();
    }

    private static string UniqueEmail() => $"selenium.{Guid.NewGuid():N}@example.com";
    private static string UniquePhone()
    {
        // E.164 format, random 9-digit local number after the Sri Lanka country code.
        var digits = Random.Shared.Next(100000000, 999999999);
        return $"+94{digits}";
    }

    // ---------- Scenario 1: Successful Registration ----------

    [Fact]
    public void Register_ValidData_ShowsSuccessMessage_AndNavigatesToVerifyEmail()
    {
        GoToRegisterPage();
        FillForm("Selenium Test User", UniqueEmail(), UniquePhone(), "Str0ngPass1");
        Submit();

        // Note: RegisterForm.tsx calls setSuccess(...) immediately followed by
        // navigate("/verify-email", ...) in the same synchronous block, so the
        // success message unmounts almost instantly — too fast to reliably read
        // its text here. The redirect itself is the more stable, meaningful signal
        // that registration succeeded, so we assert on that instead.
        _registerWait.Until(d => d.Url.Contains("/verify-email"));
        Assert.Contains("/verify-email", _driver.Url);
    }

    // ---------- Scenario 2: Duplicate Email ----------

    [Fact]
    public void Register_DuplicateEmail_ShowsInlineErrorOnEmailField()
    {
        var email = UniqueEmail();

        // First registration — should succeed. Uses the longer wait since this
        // triggers a real SMTP send, same as the ValidData test.
        GoToRegisterPage();
        FillForm("First User", email, UniquePhone(), "Str0ngPass1");
        Submit();
        _registerWait.Until(d => d.Url.Contains("/verify-email"));

        // Second registration with the SAME email — should be rejected.
        GoToRegisterPage();
        FillForm("Second User", email, UniquePhone(), "Str0ngPass1");
        Submit();

        var emailError = _wait.Until(d => d.FindElement(By.Id("email-error")));
        Assert.Contains("already", emailError.Text, StringComparison.OrdinalIgnoreCase);

        // Must NOT have navigated away — account creation was blocked.
        Assert.DoesNotContain("/verify-email", _driver.Url);
    }

    // ---------- Scenario 3: Weak Password ----------

    [Fact]
    public void Register_WeakPassword_ShowsPasswordRequirementsError()
    {
        GoToRegisterPage();
        FillForm("Weak Pw User", UniqueEmail(), UniquePhone(), "weak");
        // Click elsewhere to trigger onTouched validation, then submit.
        _driver.FindElement(By.Id("confirmPassword")).SendKeys("weak");
        Submit();

        var passwordError = _wait.Until(d => d.FindElement(By.Id("password-error")));
        Assert.False(string.IsNullOrWhiteSpace(passwordError.Text));

        // Blocked — still on the register page.
        Assert.DoesNotContain("/verify-email", _driver.Url);
    }

    // ---------- Scenario 4: Invalid Phone Format ----------

    [Fact]
    public void Register_InvalidPhoneFormat_ShowsPhoneValidationError()
    {
        GoToRegisterPage();
        FillForm("Bad Phone User", UniqueEmail(), "0771234567", "Str0ngPass1"); // missing '+'
        Submit();

        var phoneError = _wait.Until(d => d.FindElement(By.Id("phoneNo-error")));
        Assert.False(string.IsNullOrWhiteSpace(phoneError.Text));

        Assert.DoesNotContain("/verify-email", _driver.Url);
    }

    // ---------- Scenario 5: Required Field Validation ----------

    [Fact]
    public void Register_EmptyRequiredFields_ShowsInlineErrors_AndBlocksSubmission()
    {
        GoToRegisterPage();
        // Submit with everything blank.
        Submit();

        // At minimum the name field (first in the form) should show a required error.
        var nameError = _wait.Until(d => d.FindElement(By.Id("name-error")));
        Assert.False(string.IsNullOrWhiteSpace(nameError.Text));

        // No success message should ever appear, and we should still be on /register —
        // i.e. no create-account API call actually went through.
        Assert.DoesNotContain("/verify-email", _driver.Url);
        Assert.Empty(_driver.FindElements(By.CssSelector("[role='status']")));
    }
}
