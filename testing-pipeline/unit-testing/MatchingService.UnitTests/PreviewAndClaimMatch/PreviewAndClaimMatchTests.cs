using System.Net;
using System.Net.Http.Json;
using MatchingService.Claims;
using Microsoft.AspNetCore.Http;

namespace MatchingService.UnitTests.PreviewAndClaimMatch;

public class PreviewAndClaimMatchTests
{
    [Theory]
    [InlineData("LOST", "api/items/lost/mine")]
    [InlineData("FOUND", "api/items/found/mine")]
    public async Task CandidateLookup_ReturnsOnlyTheCallersActiveReports(string type, string expectedPath)
    {
        var caller = Guid.NewGuid();
        var owned = Report(caller);
        var resolved = Report(caller);
        resolved.Status = "RESOLVED";
        var strangers = Report(Guid.NewGuid());
        var handler = new ItemApiHandler();
        handler.Respond(expectedPath, new[] { owned, resolved, strangers });

        var candidates = await ItemClient(handler).GetMineAsync(type, caller, CancellationToken.None);

        Assert.Equal(owned.Id, Assert.Single(candidates).Id);
        Assert.Equal(type, candidates[0].Type);
        Assert.Equal(expectedPath, Assert.Single(handler.RequestedPaths));
        Assert.Equal("Bearer assessment-token", Assert.Single(handler.AuthorizationHeaders));
    }

    [Fact]
    public async Task PreviewWithNoPhotos_ReturnsBothReportsAndNormalizesTextScoreTo100()
    {
        var (lost, found) = ComparableReports();

        var preview = await Scorer().ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.Equal(lost.Id, preview.Lost.Id);
        Assert.Equal(found.Id, preview.Found.Id);
        Assert.Equal(100m, preview.Score);
        Assert.True(preview.CanClaim);
        Assert.Equal(60m, preview.Threshold);
        Assert.Equal(0m, preview.Breakdown.ImageDescription);
        Assert.Equal(0m, preview.Breakdown.ImageAttributes);
    }

    [Fact]
    public async Task OnePhotoMissing_DoesNotPenalizeTheTextComparison()
    {
        var (lost, found) = ComparableReports();
        lost.PhotoUrls = ["https://photos.example.com/blue-bag.jpg"];

        var preview = await Scorer().ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.Equal(100m, preview.Score);
        Assert.Equal(0m, preview.Breakdown.ImageDescription);
    }

    [Fact]
    public async Task DateAndLocationAreShownButDoNotChangeTheScore()
    {
        var (lost, found) = ComparableReports();
        lost.DateLost = "2026-01-01";
        lost.LastKnownLocation = "Colombo";
        found.DateFound = "2026-09-30";
        found.LocationFound = "Kandy";

        var preview = await Scorer().ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.Equal(100m, preview.Score);
        Assert.Equal("2026-01-01", preview.Lost.Date);
        Assert.Equal("Kandy", preview.Found.Location);
    }

    [Fact]
    public async Task LowSimilarity_IsBelowClaimThreshold()
    {
        var (lost, found) = ComparableReports();
        found.Title = "silver keys";
        found.Category = "Keys";
        found.Description = "three metal keys on a ring";

        var preview = await Scorer().ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.True(preview.Score < 60m);
        Assert.False(preview.CanClaim);
    }

    [Fact]
    public async Task RecalculatingAfterReportChange_ProducesNewPreviewVersion()
    {
        var (lost, found) = ComparableReports();
        var scorer = Scorer();
        var original = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);
        found.Description = "An orange suitcase instead of a backpack";

        var updated = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.NotEqual(original.PreviewVersion, updated.PreviewVersion);
        Assert.NotEqual(original.Score, updated.Score);
    }

    [Fact]
    public async Task PreviewRejectsCallerWhoOwnsNeitherReportBeforeDatabaseAccess()
    {
        var (lost, found) = ComparableReports();
        var handler = new ItemApiHandler();
        handler.Respond($"api/items/lost/{lost.Id}", lost);
        handler.Respond($"api/items/found/{found.Id}", found);
        var service = new ClaimService(ItemClient(handler), null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.PreviewAsync(
            new PairRequest(lost.Id, found.Id), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal(2, handler.RequestedPaths.Count);
    }

    [Fact]
    public async Task PreviewRejectsTwoReportsFromTheSameOwner()
    {
        var (lost, found) = ComparableReports();
        found.UserId = lost.UserId;
        var handler = new ItemApiHandler();
        handler.Respond($"api/items/lost/{lost.Id}", lost);
        handler.Respond($"api/items/found/{found.Id}", found);
        var service = new ClaimService(ItemClient(handler), null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.PreviewAsync(
            new PairRequest(lost.Id, found.Id), lost.UserId, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData("RESOLVED", "ACTIVE")]
    [InlineData("ACTIVE", "CLOSED")]
    public async Task PreviewRejectsAnInactiveReport(string lostStatus, string foundStatus)
    {
        var (lost, found) = ComparableReports();
        lost.Status = lostStatus;
        found.Status = foundStatus;
        var handler = new ItemApiHandler();
        handler.Respond($"api/items/lost/{lost.Id}", lost);
        handler.Respond($"api/items/found/{found.Id}", found);
        var service = new ClaimService(ItemClient(handler), null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.PreviewAsync(
            new PairRequest(lost.Id, found.Id), lost.UserId, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PreviewRejectsEmptyItemIdWithoutCallingItemService(bool lostValid, bool foundValid)
    {
        var handler = new ItemApiHandler();
        var service = new ClaimService(ItemClient(handler), null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.PreviewAsync(
            new PairRequest(lostValid ? Guid.NewGuid() : Guid.Empty,
                foundValid ? Guid.NewGuid() : Guid.Empty), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Empty(handler.RequestedPaths);
    }

    [Fact]
    public async Task ClaimWithoutPreviewVersion_IsRejectedBeforeCallingOtherServices()
    {
        var service = new ClaimService(null!, null!);
        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SubmitAsync(
            new SubmitClaimRequest(Guid.NewGuid(), Guid.NewGuid(), " "),
            Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task ClaimRechecksOwnershipAfterPreviewBeforeAnyDatabaseWrite()
    {
        var (lost, found) = ComparableReports();
        var handler = new ItemApiHandler();
        handler.Respond($"api/items/lost/{lost.Id}", lost);
        handler.Respond($"api/items/found/{found.Id}", found);
        var service = new ClaimService(ItemClient(handler), null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SubmitAsync(
            new SubmitClaimRequest(lost.Id, found.Id, "previous-preview-version"),
            Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal(2, handler.RequestedPaths.Count);
    }

    [Fact]
    public async Task ClaimRechecksActiveStatusAfterPreviewBeforeAnyDatabaseWrite()
    {
        var (lost, found) = ComparableReports();
        found.Status = "RESOLVED";
        var handler = new ItemApiHandler();
        handler.Respond($"api/items/lost/{lost.Id}", lost);
        handler.Respond($"api/items/found/{found.Id}", found);
        var service = new ClaimService(ItemClient(handler), null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SubmitAsync(
            new SubmitClaimRequest(lost.Id, found.Id, "previous-preview-version"),
            lost.UserId, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    private static ClaimService Scorer() => new(null!, null!);

    private static (ItemReport Lost, ItemReport Found) ComparableReports()
    {
        var lost = Report(Guid.NewGuid());
        var found = Report(Guid.NewGuid());
        return (lost, found);
    }

    private static ItemReport Report(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner,
        Title = "Blue backpack", Category = "Bags",
        Description = "Blue canvas backpack with front pocket",
        Status = "ACTIVE", DateLost = "2026-09-01", DateFound = "2026-09-02",
        LastKnownLocation = "Library", LocationFound = "Bus stop"
    };

    private static ClaimItemClient ItemClient(ItemApiHandler handler)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer assessment-token";
        return new ClaimItemClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://item-service.test/") },
            new HttpContextAccessor { HttpContext = context });
    }

    private sealed class ItemApiHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _responses = new(StringComparer.Ordinal);
        public List<string> RequestedPaths { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];

        public void Respond(string path, object value) => _responses.Add(path, value);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            RequestedPaths.Add(path);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(_responses.TryGetValue(path, out var value)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
