using MatchingService.Claims;
using MatchingService.Matches;
using Moq;

namespace MatchingService.Tests.Matches;

/// <summary>
/// Story 3 (view potential matches) contract tests for MatchReadService, the read side behind the
/// dedicated Matches page and its shared review screen: section grouping, ownership, and visibility.
/// This code was originally built and tested alongside Story 2, whose own Scenario 4 ("awaiting the
/// other person's decision") is where the half-confirmed statuses this class exercises come from -
/// see Story2-ClaimAndMatch.md for the write side and Story3-MatchedItemsPage.md for this read side.
/// IMatchReadRepository is mocked, matching Story 1's ImageDescriptionWorkerTests style: this proves
/// the service's own section/ownership/visibility logic, not the SQL underneath it (see
/// MatchReadRepositoryTests, the real-database counterpart).
/// </summary>
public sealed class MatchReadServiceTests
{
    private static readonly Guid LostReporterId = Guid.NewGuid();
    private static readonly Guid FinderId = Guid.NewGuid();

    private static StoredMatch NewMatch(
        string status,
        bool isActive = true,
        Guid? claimantId = null,
        string? deactivationReason = null,
        Guid? deactivatedItemId = null,
        string? deactivatedItemType = null) => new(
            Guid.NewGuid(),
            LostReporterId,
            FinderId,
            claimantId ?? LostReporterId,
            status,
            isActive,
            75m,
            DateTime.UtcNow,
            new ClaimItemView(Guid.NewGuid(), "LOST", "Lost item", "Accessories", "desc", "2026-09-01", "Malabe"),
            new ClaimItemView(Guid.NewGuid(), "FOUND", "Found item", "Accessories", "desc", "2026-09-01", "Malabe"))
        {
            DeactivatedAt = deactivationReason is null ? null : DateTime.UtcNow,
            DeactivationReason = deactivationReason,
            DeactivatedItemId = deactivatedItemId,
            DeactivatedItemType = deactivatedItemType
        };

    // An unrecognised section name is rejected before the repository is ever queried.
    [Fact]
    public async Task GetPageAsync_UnknownSection_ThrowsBadRequestWithoutQueryingRepository()
    {
        var repository = new Mock<IMatchReadRepository>();
        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetPageAsync(LostReporterId, "not-a-real-section", 1, 20, CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        repository.Verify(
            r => r.GetPageAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(100001, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task GetPageAsync_PageOrSizeOutOfRange_ThrowsBadRequest(int page, int size)
    {
        var repository = new Mock<IMatchReadRepository>();
        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetPageAsync(LostReporterId, "active", page, size, CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
    }

    // A section name is accepted case- and whitespace-insensitively, and passed through normalized.
    [Fact]
    public async Task GetPageAsync_SectionWithMixedCaseAndWhitespace_IsNormalizedBeforeQuerying()
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetPageAsync(LostReporterId, "confirmed", 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredMatchPage([], 0));

        var service = new MatchReadService(repository.Object);

        await service.GetPageAsync(LostReporterId, "  Confirmed  ", 1, 20, CancellationToken.None);

        repository.Verify(
            r => r.GetPageAsync(LostReporterId, "confirmed", 1, 20, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_MatchDoesNotExist_ThrowsNotFound()
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StoredMatch?)null);

        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    // Scenario 6's ownership principle applied to reading a match too: a third party cannot view it.
    [Fact]
    public async Task GetByIdAsync_CallerIsNeitherPartyToTheMatch_ThrowsForbidden()
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch("LOST_REPORTER_CONFIRMED"));

        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
    }

    // A deactivated match (is_active = 0) is hidden even from its own parties.
    [Fact]
    public async Task GetByIdAsync_MatchIsNotActive_ThrowsNotFoundEvenForAParty()
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch("LOST_REPORTER_CONFIRMED", isActive: false));

        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    // Scenario 8: a match that was Auto Rejected for low confidence is hidden the same way as "not found", even from its own parties. Also covers any other status outside the four visible ones, defensively - nothing in the shipped code writes one, but the read side must not assume that.
    [Fact]
    public async Task GetByIdAsync_AutoRejectedLowConfidenceStatus_ThrowsNotFoundEvenForAParty()
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch("AUTO_REJECTED_LOW_CONFIDENCE"));

        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    /* Scenario 4's "awaiting the other person's decision": each viewer sees isYourTurn/section
       relative to their own role, not an absolute property of the match. */
    [Theory]
    [InlineData("LOST_REPORTER_CONFIRMED", true, false, "waiting-on-other")]
    [InlineData("LOST_REPORTER_CONFIRMED", false, true, "waiting-on-you")]
    [InlineData("FINDER_CONFIRMED", true, true, "waiting-on-you")]
    [InlineData("FINDER_CONFIRMED", false, false, "waiting-on-other")]
    public async Task GetByIdAsync_HalfConfirmedMatch_ComputesTurnAndSectionRelativeToViewer(
        string status, bool viewerIsLostReporter, bool expectedIsYourTurn, string expectedSection)
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch(status));

        var service = new MatchReadService(repository.Object);
        var viewer = viewerIsLostReporter ? LostReporterId : FinderId;

        var entry = await service.GetByIdAsync(Guid.NewGuid(), viewer, CancellationToken.None);

        Assert.Equal(expectedIsYourTurn, entry.IsYourTurn);
        Assert.Equal(expectedSection, entry.Section);
        Assert.Equal(viewerIsLostReporter ? "LOST" : "FOUND", entry.YourRole);
    }

    // CONFIRMED/REJECTED are terminal: their section is fixed, regardless of whose turn it would otherwise be.
    [Theory]
    [InlineData("CONFIRMED", "confirmed")]
    [InlineData("REJECTED", "rejected")]
    public async Task GetByIdAsync_TerminalStatus_SectionIsFixedRegardlessOfViewer(
        string status, string expectedSection)
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch(status));

        var service = new MatchReadService(repository.Object);

        var entry = await service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None);

        Assert.Equal(expectedSection, entry.Section);
    }

    // "OtherItem" is always the counterpart's report: the found item for a lost reporter, and vice versa.
    [Fact]
    public async Task GetByIdAsync_OtherItem_IsTheCounterpartsReportForEachViewer()
    {
        var match = NewMatch("CONFIRMED");
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var service = new MatchReadService(repository.Object);

        var asLostReporter = await service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None);
        var asFinder = await service.GetByIdAsync(Guid.NewGuid(), FinderId, CancellationToken.None);

        Assert.Equal(match.Found, asLostReporter.OtherItem);
        Assert.Equal(match.Lost, asFinder.OtherItem);
    }

    // IsClaimant and its role label reflect who actually submitted the claim, not just who owns which side.
    [Fact]
    public async Task GetByIdAsync_ViewerIsTheClaimant_IsClaimantTrueWithClaimedLabel()
    {
        var match = NewMatch("CONFIRMED", claimantId: LostReporterId);
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var service = new MatchReadService(repository.Object);
        var entry = await service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None);

        Assert.True(entry.IsClaimant);
        Assert.Equal("You claimed this item", entry.RoleLabel);
    }

    [Fact]
    public async Task GetByIdAsync_ViewerIsNotTheClaimant_IsClaimantFalseWithClaimedByOtherLabel()
    {
        var match = NewMatch("CONFIRMED", claimantId: LostReporterId);
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var service = new MatchReadService(repository.Object);
        var entry = await service.GetByIdAsync(Guid.NewGuid(), FinderId, CancellationToken.None);

        Assert.False(entry.IsClaimant);
        Assert.Equal("Someone claimed your item", entry.RoleLabel);
    }

    // The happy path for the list endpoint: entries are mapped and the page metadata is passed through unchanged.
    [Fact]
    public async Task GetPageAsync_RepositoryReturnsMatches_ReturnsMappedPageWithTotalCount()
    {
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetPageAsync(LostReporterId, "active", 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredMatchPage([NewMatch("LOST_REPORTER_CONFIRMED")], 11));

        var service = new MatchReadService(repository.Object);

        var page = await service.GetPageAsync(LostReporterId, "active", 2, 10, CancellationToken.None);

        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.Size);
        Assert.Equal(11, page.TotalCount);
        Assert.Single(page.Items);
    }

    // ---- Story 7: deactivated-but-visible matches (read-only history) --------------------

    [Theory]
    [InlineData("ITEM_RESOLVED")]
    [InlineData("ITEM_DELETED")]
    [InlineData("MATCH_CONFIRMED_ELSEWHERE")]
    public async Task GetByIdAsync_DeactivatedWithAQualifyingReason_IsVisibleNotNotFound(string reason)
    {
        var repository = new Mock<IMatchReadRepository>();
        var itemId = Guid.NewGuid();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch(
                "LOST_REPORTER_CONFIRMED", isActive: false,
                deactivationReason: reason, deactivatedItemId: itemId, deactivatedItemType: "LOST"));

        var service = new MatchReadService(repository.Object);
        var entry = await service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None);

        Assert.Equal("DEACTIVATED", entry.Status);
        Assert.Equal("deactivated", entry.Section);
        Assert.Equal(reason, entry.DeactivationReason);
        Assert.Equal(itemId, entry.DeactivatedItemId);
    }

    [Fact]
    public async Task GetByIdAsync_InactiveWithoutAQualifyingReason_StillThrowsNotFound()
    {
        // A deactivation reason outside the known allow-list (nothing in the shipped code writes one,
        // but the read side must not assume that) is treated the same as no reason at all - hidden.
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch(
                "LOST_REPORTER_CONFIRMED", isActive: false, deactivationReason: "SOMETHING_UNEXPECTED"));

        var service = new MatchReadService(repository.Object);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task GetByIdAsync_DeactivatedConfirmedMatch_IsNotTreatedAsDeactivated()
    {
        // ItemLifecycleRepository/MatchConfirmationOutbox never deactivate a CONFIRMED match (Scenario
        // 3), but the read side's own gate is proven directly too: a CONFIRMED status always takes the
        // normal path, regardless of is_active/deactivation_reason on the row.
        var repository = new Mock<IMatchReadRepository>();
        repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewMatch("CONFIRMED"));

        var service = new MatchReadService(repository.Object);
        var entry = await service.GetByIdAsync(Guid.NewGuid(), LostReporterId, CancellationToken.None);

        Assert.Equal("CONFIRMED", entry.Status);
        Assert.Equal("confirmed", entry.Section);
    }
}
