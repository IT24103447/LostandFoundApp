using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace LostFound.E2E.Tests.Support;

/// <summary>
/// One Chrome instance per test class (xUnit IClassFixture). No implicit wait is set on purpose:
/// all waiting is explicit (see WaitExtensions) so timing is predictable.
///
/// Before Chrome is ever started, <see cref="RunPreflight"/> checks over plain HTTP that the frontend
/// answers and warms up Vite (which compiles pages on first request). This turns "16 tests each
/// hang for 60 s" into one clear, immediate failure message.
/// </summary>
public sealed class BrowserFixture : IDisposable
{
    private static readonly Lazy<string?> PreflightResult = new(RunPreflight, LazyThreadSafetyMode.ExecutionAndPublication);

    private IWebDriver _driver;
    public IWebDriver Driver => _driver;

    public BrowserFixture()
    {
        if (PreflightResult.Value is { } problem)
            throw new InvalidOperationException(problem);

        _driver = CreateDriver();
    }

    private static IWebDriver CreateDriver()
    {
        var options = new ChromeOptions
        {
            // Return once the DOM is parsed; explicit waits decide when a page is ready.
            PageLoadStrategy = PageLoadStrategy.Eager
        };

        if (TestConfig.Headless)
        {
            options.AddArgument("--headless=new");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
        }
        options.AddArgument("--window-size=1280,900");
        options.AddArgument("--disable-extensions");
        options.AddArgument("--disable-background-networking");
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");

        options.AddUserProfilePreference("credentials_enable_service", false);
        options.AddUserProfilePreference("profile.password_manager_enabled", false);
        options.AddUserProfilePreference("profile.password_manager_leak_detection", false);

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;

        var driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(150));
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(90);
        return driver;
    }

    /// <summary>Replaces a wedged browser (a hung navigation blocks every later command).</summary>
    public void Restart()
    {
        try { _driver.Quit(); } catch { }
        try { _driver.Dispose(); } catch { }
        _driver = CreateDriver();
    }

    /// <summary>Plain-HTTP readiness check + Vite warm-up. Null when healthy, else an explanation.</summary>
    private static string? RunPreflight()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var deadline = DateTime.UtcNow.AddMinutes(3);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = http.GetAsync(TestConfig.BaseUrl + "/login").GetAwaiter().GetResult();
                if ((int)response.StatusCode < 500) { last = null; break; }
                last = new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(2000);
        }
        if (last is not null)
        {
            return $"The frontend at {TestConfig.BaseUrl} did not answer ({last.GetType().Name}: {last.Message}). " +
                   "Start the stack first (testing-pipeline/shared/start-environment.sh start) and check that the " +
                   "same URL opens in a normal browser on THIS machine. If the stack runs in WSL2, make sure " +
                   "localhost:5173 is reachable from Windows, or set E2E_BASE_URL.";
        }

        // Warm up Vite: it compiles on first request. Best effort.
        foreach (var path in new[] { "/src/main.tsx", "/src/App.tsx" })
        {
            try
            {
                using var warm = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                warm.GetAsync(TestConfig.BaseUrl + path).GetAwaiter().GetResult().Dispose();
            }
            catch { }
        }
        return null;
    }

    public void Capture(string testName)
    {
        try
        {
            Directory.CreateDirectory(TestConfig.ScreenshotDir);
            var path = Path.Combine(TestConfig.ScreenshotDir, $"{testName}-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
            ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(path);
        }
        catch
        {
            // A failed screenshot must never hide the real test failure.
        }
    }

    public void Dispose()
    {
        try { _driver.Quit(); } catch { }
        try { _driver.Dispose(); } catch { }
    }
}
