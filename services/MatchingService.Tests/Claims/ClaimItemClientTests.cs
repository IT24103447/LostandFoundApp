using System.Net;
using MatchingService.Claims;
using MatchingService.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace MatchingService.Tests.Claims;

/// <summary>
/// Story 2 (LF-173) contract tests for ClaimItemClient, the only thing in Matching Service that calls
/// Item Service over HTTP. Covers the incoming-credential forwarding (Bearer header or auth_token
/// cookie, whichever the caller's own request carried), the HTTP status mapping ClaimsController and
/// ClaimService rely on, and GetMineAsync's own-report/ACTIVE filtering, which is what Scenario 1's
/// report-picker popup is actually built from.
/// Item Service itself is never called: FakeItemServiceHandler stands in for it.
/// </summary>
public sealed class ClaimItemClientTests
{
    private static readonly Guid ItemId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();

    private static ClaimItemClient BuildClient(
        FakeItemServiceHandler handler,
        string? authorizationHeader = "Bearer test-token",
        string? cookie = null,
        TimeSpan? timeout = null)
    {
        var context = new DefaultHttpContext();

        if (authorizationHeader is not null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        if (cookie is not null)
        {
            context.Request.Headers["Cookie"] = $"auth_token={cookie}";
        }

        var accessor = new HttpContextAccessor { HttpContext = context };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://item-service.test/"),
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };

        return new ClaimItemClient(httpClient, accessor);
    }

    // The caller's own Bearer header is forwarded to Item Service unchanged.
    [Fact]
    public async Task GetAsync_CallerSentBearerHeader_ForwardsItUnchanged()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithJson(
                $"api/items/lost/{ItemId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(ItemId, OwnerId));

        var client = BuildClient(handler, authorizationHeader: "Bearer caller-token");

        await client.GetAsync("LOST", ItemId, CancellationToken.None);

        Assert.Equal("Bearer caller-token", handler.ReceivedAuthorizationHeaders[0]);
    }

    /* No Authorization header, but the request carried the auth_token cookie (the browser flow):
       ClaimItemClient must fall back to it and forward it as a Bearer token. */
    [Fact]
    public async Task GetAsync_NoAuthorizationHeaderButCookiePresent_ForwardsCookieAsBearerToken()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithJson(
                $"api/items/lost/{ItemId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(ItemId, OwnerId));

        var client = BuildClient(handler, authorizationHeader: null, cookie: "cookie-token");

        await client.GetAsync("LOST", ItemId, CancellationToken.None);

        Assert.Equal("Bearer cookie-token", handler.ReceivedAuthorizationHeaders[0]);
    }

    // With neither a header nor a cookie, the client must refuse before ever calling Item Service.
    [Fact]
    public async Task GetAsync_NoCredentialAtAll_ThrowsUnauthorizedWithoutCallingItemService()
    {
        var handler = new FakeItemServiceHandler();
        var client = BuildClient(handler, authorizationHeader: null);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Empty(handler.ReceivedPaths);
    }

    // Item Service's own 401 (an expired/invalid forwarded token) maps to a re-authentication message.
    [Fact]
    public async Task GetAsync_ItemServiceReturns401_ThrowsUnauthorizedSignInAgain()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithStatus($"api/items/lost/{ItemId}", HttpStatusCode.Unauthorized);
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task GetAsync_ItemServiceReturns403_ThrowsForbidden()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithStatus($"api/items/lost/{ItemId}", HttpStatusCode.Forbidden);
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    // A deleted report Item Service no longer serves must not read as a generic failure.
    [Fact]
    public async Task GetAsync_ItemServiceReturns404_ThrowsNotFound()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithStatus($"api/items/lost/{ItemId}", HttpStatusCode.NotFound);
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
    }

    // Any other failure (Item Service down, a 500) is normalized to one 503, not leaked as-is.
    [Fact]
    public async Task GetAsync_ItemServiceReturns500_ThrowsServiceUnavailable()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithStatus($"api/items/lost/{ItemId}", HttpStatusCode.InternalServerError);
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
    }

    // The transport itself fails below the HTTP level (DNS, connection refused): normalized to the same 503 as a bad status code.
    [Fact]
    public async Task GetAsync_TransportThrowsHttpRequestException_ThrowsServiceUnavailable()
    {
        var handler = new FakeItemServiceHandler()
            .FailWithConnectionError($"api/items/lost/{ItemId}");
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
    }

    // A 200 whose body isn't valid JSON at all (not just the wrong shape) is caught explicitly, not left as an unhandled JsonException.
    [Fact]
    public async Task GetAsync_ResponseBodyIsNotValidJson_ThrowsServiceUnavailable()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithMalformedBody($"api/items/lost/{ItemId}");
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
    }

    /* Item Service never answers at all. ClaimItemClient's own HttpClient.Timeout cancels the request
       on a token the caller never touched, and that specific case (OperationCanceledException while the
       caller's own cancellationToken is still live) must map to 503, not be mistaken for the caller
       cancelling their own request. */
    [Fact]
    public async Task GetAsync_ItemServiceNeverResponds_ThrowsServiceUnavailableOnClientTimeout()
    {
        var handler = new FakeItemServiceHandler()
            .NeverRespond($"api/items/lost/{ItemId}");
        var client = BuildClient(handler, timeout: TimeSpan.FromMilliseconds(200));

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
    }

    /* A 200 response whose body doesn't actually describe the requested item (wrong id, or no owner)
       must not be trusted silently: it is treated the same as an unreachable Item Service. */
    [Fact]
    public async Task GetAsync_ResponseBodyIdDoesNotMatchRequestedId_ThrowsServiceUnavailable()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithJson(
                $"api/items/lost/{ItemId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(Guid.NewGuid(), OwnerId));
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetAsync("LOST", ItemId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
    }

    // The happy path: a genuine, matching report is returned as-is.
    [Fact]
    public async Task GetAsync_ValidResponse_ReturnsTheReport()
    {
        var handler = new FakeItemServiceHandler()
            .RespondWithJson(
                $"api/items/lost/{ItemId}",
                HttpStatusCode.OK,
                FakeItemServiceHandler.Report(
                    ItemId, OwnerId, title: "Blue umbrella", status: "ACTIVE"));
        var client = BuildClient(handler);

        var report = await client.GetAsync("LOST", ItemId, CancellationToken.None);

        Assert.Equal(ItemId, report.Id);
        Assert.Equal(OwnerId, report.UserId);
        Assert.Equal("Blue umbrella", report.Title);
        Assert.Equal("ACTIVE", report.Status);
    }

    /* Scenario 1: "a popup lists my active reports of the opposite type." GetMineAsync only returns
       the caller's own reports, and only the ACTIVE ones, even though Item Service's /mine endpoint
       returns every report regardless of owner or status (it is the caller's own "mine" list either
       way, but the filter is re-asserted defensively here rather than trusted blindly). */
    [Fact]
    public async Task GetMineAsync_MixOfOwnersAndStatuses_ReturnsOnlyOwnActiveReports()
    {
        var mine = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();

        var handler = new FakeItemServiceHandler()
            .RespondWithJson(
                "api/items/lost/mine",
                HttpStatusCode.OK,
                new[]
                {
                    FakeItemServiceHandler.Report(Guid.NewGuid(), mine, status: "ACTIVE", title: "Mine, active"),
                    FakeItemServiceHandler.Report(Guid.NewGuid(), mine, status: "RESOLVED", title: "Mine, resolved"),
                    FakeItemServiceHandler.Report(Guid.NewGuid(), someoneElse, status: "ACTIVE", title: "Not mine")
                });

        var client = BuildClient(handler);

        var reports = await client.GetMineAsync("lost", mine, CancellationToken.None);

        var report = Assert.Single(reports);
        Assert.Equal("Mine, active", report.Title);
    }

    // A report type other than LOST/FOUND is rejected locally, before any request is sent.
    [Fact]
    public async Task GetMineAsync_UnsupportedType_ThrowsBadRequestWithoutCallingItemService()
    {
        var handler = new FakeItemServiceHandler();
        var client = BuildClient(handler);

        var exception = await Assert.ThrowsAsync<ClaimException>(
            () => client.GetMineAsync("found_it", OwnerId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Empty(handler.ReceivedPaths);
    }
}
