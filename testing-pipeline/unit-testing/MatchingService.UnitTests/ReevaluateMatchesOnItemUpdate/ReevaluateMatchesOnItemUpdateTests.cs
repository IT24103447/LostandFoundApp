using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Reevaluation;

namespace MatchingService.UnitTests.ReevaluateMatchesOnItemUpdate;

public sealed class ReevaluateMatchesOnItemUpdateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdatedReportThatNoLongerMatches_DropsBelowTheClaimThreshold(bool updateLost)
    {
        var (lost, found) = MatchingPair();
        var scorer = Scorer();
        var before = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        if (updateLost)
        {
            lost.Title = "Blue plastic umbrella";
            lost.Category = "Other";
            lost.Description = "Large golf umbrella with broken ribs";
        }
        else
        {
            found.Title = "Blue plastic umbrella";
            found.Category = "Other";
            found.Description = "Large golf umbrella with broken ribs";
        }

        var after = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.True(before.CanClaim);
        Assert.True(before.Score >= ClaimService.Threshold);
        Assert.False(after.CanClaim);
        Assert.True(after.Score < ClaimService.Threshold);
        Assert.NotEqual(before.PreviewVersion, after.PreviewVersion);
    }

    [Fact]
    public async Task CorrectedReport_UsesTheSameScoringEngineAndCanClearTheThreshold()
    {
        var (lost, found) = MatchingPair();
        lost.Title = "Blue plastic umbrella";
        lost.Category = "Other";
        lost.Description = "Large golf umbrella with broken ribs";
        var scorer = Scorer();
        var before = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        lost.Title = found.Title;
        lost.Category = found.Category;
        lost.Description = found.Description;
        var after = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.False(before.CanClaim);
        Assert.True(after.CanClaim);
        Assert.True(after.Score >= ClaimService.Threshold);
        Assert.Equal(lost.Title, after.Lost.Title);
        Assert.Equal(lost.Description, after.Lost.Description);
    }

    [Fact]
    public async Task DateAndLocationEdits_ChangeTheSnapshotButNotTheSimilarityScore()
    {
        var (lost, found) = MatchingPair();
        var scorer = Scorer();
        var before = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        lost.DateLost = "2026-10-12";
        lost.LastKnownLocation = "Railway station";
        var after = await scorer.ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.Equal(before.Score, after.Score);
        Assert.NotEqual(before.PreviewVersion, after.PreviewVersion);
        Assert.Equal("Railway station", after.Lost.Location);
        Assert.Equal("2026-10-12", after.Lost.Date);
    }

    [Fact]
    public async Task OnlyOneReportHasAPhoto_TextScoringCompletesWithoutImageEvidence()
    {
        var (lost, found) = MatchingPair();
        lost.PhotoUrls = ["https://example.test/new-photo.jpg"];

        // No repository is supplied: image evidence cannot be requested for a single-sided photo.
        var preview = await Scorer().ScoreExistingAsync(lost, found, CancellationToken.None);

        Assert.True(preview.CanClaim);
        Assert.Equal(100m, preview.Score);
        Assert.Equal(0m, preview.Breakdown.ImageDescription);
        Assert.Equal(0m, preview.Breakdown.ImageAttributes);
        Assert.Equal("https://example.test/new-photo.jpg", preview.Lost.PhotoUrl);
    }

    [Theory]
    [InlineData("items.user.updated")]
    [InlineData("items.lost_item.resolved")]
    [InlineData("items.found_item.deleted")]
    public async Task UnsupportedItemEvents_AreRejectedBeforeAReevaluationJobIsRecorded(string topic)
    {
        var handler = new MatchReevaluationEventHandler(null!);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            handler.HandleAsync(topic, ValidEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateEventMissingRequiredPublicFields_IsRejectedBeforePersistence()
    {
        var handler = new MatchReevaluationEventHandler(null!);
        var json = ValidEvent(title: " ");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            handler.HandleAsync("items.lost_item.updated", json, CancellationToken.None));
    }

    [Fact]
    public async Task ExistingPairWithAnEmptyItemId_CannotBeScored()
    {
        var (lost, found) = MatchingPair();
        lost.Id = Guid.Empty;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scorer().ScoreExistingAsync(lost, found, CancellationToken.None));
    }

    private static ClaimService Scorer() => new(null!, null!);

    private static (ItemReport Lost, ItemReport Found) MatchingPair() =>
    (
        new ItemReport
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = "ACTIVE",
            Title = "Brown leather wallet", Category = "Accessories",
            Description = "Worn brown leather wallet with gold clasp",
            DateLost = "2026-10-01", LastKnownLocation = "Library"
        },
        new ItemReport
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = "ACTIVE",
            Title = "Brown leather wallet", Category = "Accessories",
            Description = "Worn brown leather wallet with gold clasp",
            DateFound = "2026-10-02", LocationFound = "Bus station"
        }
    );

    private static string ValidEvent(string title = "Brown leather wallet") =>
        JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow,
            userId = Guid.NewGuid(), lostItemId = Guid.NewGuid(),
            title, category = "Accessories", description = "A leather wallet",
            status = "ACTIVE", dateLost = "2026-10-01",
            lastKnownLocation = "Library", photoUrls = Array.Empty<string>()
        });
}
