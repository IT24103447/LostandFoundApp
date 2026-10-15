using MatchingService.Claims;
using MatchingService.Matches;
using Moq;

namespace MatchingService.UnitTests.ViewPotentialMatches;

public class ViewPotentialMatchesTests
{
    private static readonly Guid LostReporter = Guid.NewGuid();
    private static readonly Guid Finder = Guid.NewGuid();

    [Theory]
    [InlineData("LOST_REPORTER_CONFIRMED", true, "waiting-on-other", false)]
    [InlineData("LOST_REPORTER_CONFIRMED", false, "waiting-on-you", true)]
    [InlineData("FINDER_CONFIRMED", true, "waiting-on-you", true)]
    [InlineData("FINDER_CONFIRMED", false, "waiting-on-other", false)]
    [InlineData("CONFIRMED", true, "confirmed", false)]
    [InlineData("REJECTED", false, "rejected", false)]
    public async Task MatchRow_MapsStatusToTheCorrectViewerSectionAndActionState(
        string status, bool asLostReporter, string section, bool canAct)
    {
        var match = Stored(status);
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);
        var viewer = asLostReporter ? LostReporter : Finder;

        var row = await new MatchReadService(repository.Object).GetByIdAsync(match.Id, viewer, CancellationToken.None);

        Assert.Equal(section, row.Section);
        Assert.Equal(canAct, row.IsYourTurn);
        Assert.Equal(asLostReporter ? "LOST" : "FOUND", row.YourRole);
        Assert.Equal(asLostReporter ? match.Found : match.Lost, row.OtherItem);
        Assert.Equal(match.Score, row.Score);
        Assert.Equal(match.Status, row.Status);
        repository.VerifyAll();
    }

    [Fact]
    public async Task MatchRow_ShowsCounterpartsPhotoTitleCategoryDateAndLocation()
    {
        var match = Stored("FINDER_CONFIRMED");
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>())).ReturnsAsync(match);

        var row = await new MatchReadService(repository.Object).GetByIdAsync(match.Id, LostReporter, CancellationToken.None);

        Assert.Equal("https://photos.example.com/found.jpg", row.OtherItem.PhotoUrl);
        Assert.Equal("Found backpack", row.OtherItem.Title);
        Assert.Equal("Bags", row.OtherItem.Category);
        Assert.Equal("2026-09-02", row.OtherItem.Date);
        Assert.Equal("Bus stop", row.OtherItem.Location);
        Assert.Equal(78m, row.Score);
    }

    [Theory]
    [InlineData(true, "You claimed this item")]
    [InlineData(false, "Someone claimed your item")]
    public async Task MatchRow_IdentifiesWhoSubmittedTheClaim(bool viewerIsClaimant, string expectedLabel)
    {
        var match = Stored("LOST_REPORTER_CONFIRMED");
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>())).ReturnsAsync(match);
        var viewer = viewerIsClaimant ? LostReporter : Finder;

        var row = await new MatchReadService(repository.Object).GetByIdAsync(match.Id, viewer, CancellationToken.None);

        Assert.Equal(viewerIsClaimant, row.IsClaimant);
        Assert.Equal(expectedLabel, row.RoleLabel);
    }

    [Fact]
    public async Task List_PreservesPaginationAndScopesRepositoryQueryToCaller()
    {
        var match = Stored("FINDER_CONFIRMED");
        var ct = new CancellationTokenSource().Token;
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetPageAsync(LostReporter, "waiting-on-you", 2, 10, ct))
            .ReturnsAsync(new StoredMatchPage([match], 11));

        var page = await new MatchReadService(repository.Object).GetPageAsync(
            LostReporter, "  WAITING-ON-YOU  ", 2, 10, ct);

        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.Size);
        Assert.Equal(11, page.TotalCount);
        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
        repository.VerifyAll();
    }

    [Fact]
    public async Task EmptyMatchPage_ReturnsAnEmptyResultAndZeroTotal()
    {
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetPageAsync(LostReporter, "active", 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredMatchPage([], 0));

        var page = await new MatchReadService(repository.Object).GetPageAsync(
            LostReporter, "active", 1, 20, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Theory]
    [InlineData("unknown", 1, 20)]
    [InlineData("active", 0, 20)]
    [InlineData("active", 1, 101)]
    public async Task InvalidSectionOrPage_IsRejectedBeforeRepositoryAccess(string section, int page, int size)
    {
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        var error = await Assert.ThrowsAsync<ClaimException>(() => new MatchReadService(repository.Object)
            .GetPageAsync(LostReporter, section, page, size, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownMatch_Returns404()
    {
        var id = Guid.NewGuid();
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StoredMatch?)null);

        var error = await Assert.ThrowsAsync<ClaimException>(() => new MatchReadService(repository.Object)
            .GetByIdAsync(id, LostReporter, CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task ThirdPartyCannotOpenMatchDetails()
    {
        var match = Stored("FINDER_CONFIRMED");
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>())).ReturnsAsync(match);

        var error = await Assert.ThrowsAsync<ClaimException>(() => new MatchReadService(repository.Object)
            .GetByIdAsync(match.Id, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
    }

    [Theory]
    [InlineData("AUTO_REJECTED_LOW_CONFIDENCE", true)]
    [InlineData("FINDER_CONFIRMED", false)]
    public async Task NonVisibleMatchCannotBeOpenedByOwner(string status, bool active)
    {
        var match = Stored(status, active);
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>())).ReturnsAsync(match);

        var error = await Assert.ThrowsAsync<ClaimException>(() => new MatchReadService(repository.Object)
            .GetByIdAsync(match.Id, LostReporter, CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task CurrentApi_ExposesCertainDeactivatedMatchesInDedicatedSection()
    {
        var match = Stored("FINDER_CONFIRMED", active: false) with
        {
            DeactivationReason = "ITEM_RESOLVED",
            DeactivatedAt = DateTime.UtcNow,
            DeactivatedItemId = Guid.NewGuid(),
            DeactivatedItemType = "FOUND"
        };
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>())).ReturnsAsync(match);

        var row = await new MatchReadService(repository.Object).GetByIdAsync(match.Id, LostReporter, CancellationToken.None);

        Assert.Equal("deactivated", row.Section);
        Assert.Equal("DEACTIVATED", row.Status);
        Assert.False(row.IsYourTurn);
        Assert.Equal(match.DeactivatedAt, row.DeactivatedAt);
    }

    private static StoredMatch Stored(string status, bool active = true) => new(
        Guid.NewGuid(), LostReporter, Finder, LostReporter, status, active, 78m,
        new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc),
        new ClaimItemView(Guid.NewGuid(), "LOST", "Lost backpack", "Bags", "Blue canvas bag",
            "2026-09-01", "Library", "https://photos.example.com/lost.jpg"),
        new ClaimItemView(Guid.NewGuid(), "FOUND", "Found backpack", "Bags", "Blue canvas bag",
            "2026-09-02", "Bus stop", "https://photos.example.com/found.jpg"));
}
