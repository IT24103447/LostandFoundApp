using System.Globalization;
using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

public enum PreviewOutcome
{
    /// <summary>The dialog compared the two reports and shows a similarity score.</summary>
    Score,
    /// <summary>A photo has no completed image description yet, so the Matching service asks the user to wait.</summary>
    StillAnalysing,
    /// <summary>
    /// The Matching service refused the photo for comparison (for example the pipeline's local photo storage
    /// has no URL it can build a matching key from, or the analysis failed). A defined 409 response, not a crash.
    /// </summary>
    PhotoNotReady,
    /// <summary>The dialog showed some other error alert.</summary>
    Error,
    /// <summary>Neither a score nor an alert appeared in time.</summary>
    TimedOut,
}

public sealed record PreviewResult(PreviewOutcome Outcome, string Detail);

/// <summary>
/// Page object for the claim popup (ClaimDialog.tsx) opened from /items/:id with the
/// "This Is My Lost Item" button. The candidate list and the preview both come from the Matching service.
/// </summary>
public sealed class ClaimDialogPage
{
    private readonly IWebDriver _driver;

    public ClaimDialogPage(IWebDriver driver) => _driver = driver;

    private const string DialogXPath = "//dialog[@aria-labelledby='claim-dialog-title']";
    private static readonly By Dialog = By.XPath(DialogXPath);

    private string DialogText => _driver.FindElement(Dialog).Text;

    /// <summary>
    /// Opens the item page, clicks the claim button and waits for the candidate list to load.
    /// Returns true when <paramref name="candidateTitle"/> is listed. The Matching service learns about
    /// new reports through Kafka, so the list can be briefly empty: the caller retries instead of sleeping.
    /// </summary>
    private bool TryOpenOnce(string itemId, string itemTitle, string candidateTitle, string buttonText)
    {
        _driver.Navigate().GoToUrl($"{TestConfig.BaseUrl}/items/{itemId}");
        new ItemDetailsPage(_driver).WaitUntilLoaded(itemTitle);
        _driver.WaitVisible(By.XPath($"//button[normalize-space()='{buttonText}']")).Click();
        _driver.WaitVisible(Dialog);
        _driver.Wait().Until(_ => !DialogText.Contains("Loading your reports", StringComparison.Ordinal));

        return CandidateTitles().Contains(candidateTitle);
    }

    /// <summary>Re-opens the dialog until the Matching service lists the candidate (or fails after the timeout).</summary>
    public ClaimDialogPage OpenUntilCandidateListed(
        string itemId, string itemTitle, string candidateTitle,
        string buttonText = "This Is My Lost Item", int seconds = 90)
    {
        try
        {
            // Until() only accepts a bool or an object result; false means "not yet, poll again".
            _driver.Wait(seconds).Until(_ => TryOpenOnce(itemId, itemTitle, candidateTitle, buttonText));
        }
        catch (WebDriverTimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"'{candidateTitle}' never appeared in the claim candidate list. Dialog text: {DialogText}");
        }
        return this;
    }

    /// <summary>Titles of the reports currently offered in the picker.</summary>
    public IReadOnlyList<string> CandidateTitles() =>
        _driver.FindElements(By.XPath($"{DialogXPath}//button[@type='button'][span]/span[1]"))
            .Select(e => e.Text.Trim()).ToList();

    public ClaimDialogPage SelectCandidate(string title)
    {
        _driver.WaitVisible(By.XPath($"{DialogXPath}//button[.//span[normalize-space()='{title}']]")).Click();
        return this;
    }

    /// <summary>Waits for the dialog to reach a terminal state after a candidate was selected.</summary>
    public PreviewResult WaitForPreviewOutcome(int seconds = 60)
    {
        try
        {
            return _driver.Wait(seconds).Until(d =>
            {
                var text = DialogText;
                if (text.Contains("Similarity score", StringComparison.Ordinal))
                    return new PreviewResult(PreviewOutcome.Score, text);

                var alerts = d.FindElements(By.XPath($"{DialogXPath}//*[@role='alert']"));
                if (alerts.Count > 0 && !string.IsNullOrWhiteSpace(alerts[0].Text))
                {
                    var alert = alerts[0].Text;
                    return new PreviewResult(Classify(alert), alert);
                }
                return null;
            })!;
        }
        catch (WebDriverTimeoutException)
        {
            return new PreviewResult(PreviewOutcome.TimedOut, DialogText);
        }
    }

    private static PreviewOutcome Classify(string alert)
    {
        if (alert.Contains("still being analysed", StringComparison.OrdinalIgnoreCase))
            return PreviewOutcome.StillAnalysing;

        if (alert.Contains("not ready for matching", StringComparison.OrdinalIgnoreCase) ||
            alert.Contains("could not be analysed", StringComparison.OrdinalIgnoreCase))
            return PreviewOutcome.PhotoNotReady;

        return PreviewOutcome.Error;
    }

    /// <summary>The similarity score shown in the preview, as a percentage.</summary>
    public double ScorePercent()
    {
        var text = _driver
            .FindElement(By.XPath($"{DialogXPath}//p[normalize-space()='Similarity score']/following-sibling::p[1]"))
            .Text.Trim().TrimEnd('%');
        return double.Parse(text, CultureInfo.InvariantCulture);
    }
}
