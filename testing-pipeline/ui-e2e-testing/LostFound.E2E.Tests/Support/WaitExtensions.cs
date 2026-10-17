using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace LostFound.E2E.Tests.Support;

/// <summary>Explicit-wait helpers. Vite compiles pages on first request, hence the generous default.</summary>
public static class WaitExtensions
{
    public static WebDriverWait Wait(this IWebDriver driver, int seconds = 30)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(seconds))
        {
            PollingInterval = TimeSpan.FromMilliseconds(250)
        };
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        return wait;
    }

    public static IWebElement WaitVisible(this IWebDriver driver, By by, int seconds = 30) =>
        driver.Wait(seconds).Until(d =>
        {
            var element = d.FindElement(by);
            return element.Displayed ? element : null;
        })!;

    /// <summary>
    /// Waits until the element exists in the page, visible or not. Needed for inputs that are
    /// deliberately invisible (the date field's native input has opacity 0 and sits on top of a
    /// custom display, so Selenium reports it as not displayed).
    /// </summary>
    public static IWebElement WaitPresent(this IWebDriver driver, By by, int seconds = 30) =>
        driver.Wait(seconds).Until(d => d.FindElement(by))!;

    public static void WaitForPath(this IWebDriver driver, string path, int seconds = 30)
    {
        try
        {
            driver.Wait(seconds).Until(d => new Uri(d.Url).AbsolutePath == path);
        }
        catch (WebDriverTimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected the browser to be at '{path}' but it stayed at '{new Uri(driver.Url).AbsolutePath}'.");
        }
    }
}
