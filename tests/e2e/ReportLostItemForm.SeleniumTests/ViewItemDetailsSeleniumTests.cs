using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>Story 6 browser coverage for the public details page.</summary>
[Trait("Story", "6")]
[Trait("Requires", "ItemDetailsTestData")]
public sealed class ViewItemDetailsSeleniumTests : IClassFixture<ReportLostItemFixture>
{
    // Story 6 browser flow: render public data and keep unavailable/private data hidden.
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public ViewItemDetailsSeleniumTests(ReportLostItemFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
    }

    // Verifies active reports show configured public fields but never the private marker.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public void Details_ActiveConfiguredItemShowsPublicFieldsAndNeverPrivateInformation(string kind)
    {
        var prefix = $"SELENIUM_DETAILS_{kind.ToUpperInvariant()}";
        var id = Required($"{prefix}_ID");
        var title = Required($"{prefix}_TITLE");
        var privateMarker = Required($"{prefix}_HIDDEN_INFORMATION");
        var description = Required($"{prefix}_DESCRIPTION");
        var category = Required($"{prefix}_CATEGORY");
        var location = Required($"{prefix}_LOCATION");
        var dateDisplay = Required($"{prefix}_DATE_DISPLAY");
        var expectsPhoto = bool.Parse(Required($"{prefix}_EXPECTS_PHOTO"));

        Open(id);

        _wait.Until(d => d.FindElement(By.XPath($"//h1[normalize-space()={XPathLiteral(title)}]")));
        Assert.Contains(title, _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains(kind.ToUpperInvariant(), _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("ACTIVE", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(description, _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains(category, _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains(location, _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains(dateDisplay, _driver.PageSource, StringComparison.Ordinal);
        if (expectsPhoto)
            Assert.NotEmpty(_driver.FindElements(By.CssSelector($"img[alt='{title}']")));
        else
            Assert.Contains("No photo added", _driver.PageSource, StringComparison.Ordinal);
        Assert.DoesNotContain(privateMarker, _driver.PageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenInformation", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    // Verifies invalid IDs show a friendly not-found state.
    [Fact]
    public void Details_InvalidItemIdShowsFriendlyNotFoundState()
    {
        Open("00000000-0000-0000-0000-000000000001");

        _wait.Until(d => d.PageSource.Contains("This item couldn't be found", StringComparison.Ordinal));
        Assert.Contains("removed or resolved", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(_driver.FindElements(By.XPath("//button[normalize-space()='Back to Results']")));
    }

    // Verifies another user's resolved report shows a friendly not-found state.
    [Fact]
    public void Details_ResolvedItemOwnedByAnotherUserShowsFriendlyNotFoundState()
    {
        Open(Required("SELENIUM_DETAILS_RESOLVED_NONOWNER_ID"));

        _wait.Until(d => d.PageSource.Contains("This item couldn't be found", StringComparison.Ordinal));
        Assert.DoesNotContain("HiddenInformation", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    private void Open(string id) => _driver.Navigate().GoToUrl($"{ReportLostItemFixture.BaseUrl}/items/{id}");

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim()
        ?? throw new InvalidOperationException($"Set {name} before running Story 6 Selenium tests.");

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "', \"'\", '") + "')";
    }
}
