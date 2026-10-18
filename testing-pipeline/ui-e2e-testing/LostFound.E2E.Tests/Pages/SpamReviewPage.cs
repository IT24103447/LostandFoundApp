using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /admin/spam-review (SpamReviewSection.tsx), Active tab.</summary>
public sealed class SpamReviewPage
{
    private readonly IWebDriver _driver;

    public SpamReviewPage(IWebDriver driver) => _driver = driver;

    /// <summary>(Re)loads the page and waits until it shows either the queue table or the empty state.</summary>
    public SpamReviewPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/admin/spam-review");
        _driver.WaitForPath("/admin/spam-review");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='Spam Review']"));
        _driver.Wait().Until(d =>
            d.FindElements(By.Id("spam-review-table")).Count > 0 ||
            d.FindElements(By.Id("spam-review-empty")).Count > 0);
        return this;
    }

    /// <summary>
    /// Sum of the "No. of listings" scores of every active record whose flagged user has this e-mail
    /// (0 when there is none). The e-mail is fetched after the list, so wait for the "Loading…" cells first.
    /// </summary>
    public int TotalScoreFor(string email)
    {
        try
        {
            _driver.Wait(10).Until(d => d.FindElements(By.XPath("//tr[@data-status]//td[1][contains(.,'Loading')]")).Count == 0);
        }
        catch (WebDriverTimeoutException)
        {
            // Contacts that never load simply cannot be matched; the caller's poll will try again.
        }

        var rows = _driver.FindElements(By.XPath($"//tr[@data-status][td[1][contains(.,'{email}')]]"));
        return rows.Sum(r => int.TryParse(r.FindElement(By.XPath("./td[2]")).Text.Trim(), out var n) ? n : 0);
    }

    /// <summary>
    /// Reloads the page until the user's total score is higher than <paramref name="before"/>.
    /// Detection runs off Kafka, so the case appears a little after the reports were created.
    /// Returns the new total, or null if it never rose.
    /// </summary>
    public int? WaitForScoreAbove(string email, int before, int seconds = 90)
    {
        var latest = before;
        try
        {
            // Until() only accepts a bool or an object result; false means "not yet, poll again".
            _driver.Wait(seconds).Until(_ =>
            {
                Open();
                latest = TotalScoreFor(email);
                return latest > before;
            });
            return latest;
        }
        catch (WebDriverTimeoutException)
        {
            return null;
        }
    }
}
