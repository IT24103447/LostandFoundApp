using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatchingService.Claims;

public sealed class ClaimService
{
    public const string ScoringVersion = "text-v1";
    public const decimal Threshold = 60m;

    private static readonly Regex WordPattern = new(
        @"[\p{L}\p{N}]+",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> StopWords =
        new(StringComparer.Ordinal)
        {
            "a", "an", "the", "and", "or", "of", "to",
            "in", "on", "at", "is", "it", "its", "with",
            "for", "from", "this", "that", "my", "i",
            "has", "have", "was", "were", "item", "lost",
            "found", "unknown", "uncertain", "appears",
            "visible", "image", "photo"
        };

    private static readonly string[] AttributeNames =
    [
        "objectType",
        "colours",
        "materials",
        "visibleBrand",
        "patterns",
        "condition",
        "distinctiveFeatures"
    ];

    private readonly ClaimItemClient _items;
    private readonly ClaimRepository _repository;

    public ClaimService(
        ClaimItemClient items,
        ClaimRepository repository)
    {
        _items = items;
        _repository = repository;
    }

    public async Task<ClaimPreview> PreviewAsync(
        PairRequest request,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var pair = await VerifyPairAsync(
            request,
            userId,
            cancellationToken);

        return await CalculateAsync(
            pair,
            cancellationToken);
    }

    public async Task<MatchView> SubmitAsync(
        SubmitClaimRequest request,
        Guid userId,
        CancellationToken cancellationToken,
        string? claimantEmail = null,
        string? claimantPhone = null)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewVersion))
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Preview the reports before submitting a claim.");
        }

        var pair = await VerifyPairAsync(
            new PairRequest(
                request.LostItemId,
                request.FoundItemId),
            userId,
            cancellationToken);

        var preview = await CalculateAsync(
            pair,
            cancellationToken);

        if (!string.Equals(
                request.PreviewVersion,
                preview.PreviewVersion,
                StringComparison.Ordinal))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "The report information changed. Preview it again.");
        }

        if (!preview.CanClaim)
        {
            throw new ClaimException(
                StatusCodes.Status422UnprocessableEntity,
                "These reports do not meet the 60% threshold.");
        }

        if (string.IsNullOrWhiteSpace(claimantEmail) ||
            string.IsNullOrWhiteSpace(claimantPhone))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "Your contact details are unavailable. Please sign in again before claiming.");
        }

        return await _repository.CreateAsync(
            pair,
            preview,
            userId,
            cancellationToken,
            pair.ClaimantRole == "FOUND" ? claimantEmail : null,
            pair.ClaimantRole == "FOUND" ? claimantPhone : null,
            pair.ClaimantRole == "LOST" ? claimantEmail : null,
            pair.ClaimantRole == "LOST" ? claimantPhone : null);
    }

    private async Task<VerifiedPair> VerifyPairAsync(
        PairRequest request,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (request.LostItemId == Guid.Empty ||
            request.FoundItemId == Guid.Empty ||
            request.LostItemId == request.FoundItemId)
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Select one lost report and one found report.");
        }

        var lost = await _items.GetAsync(
            "LOST",
            request.LostItemId,
            cancellationToken);

        var found = await _items.GetAsync(
            "FOUND",
            request.FoundItemId,
            cancellationToken);

        if (lost.UserId != userId &&
            found.UserId != userId)
        {
            throw new ClaimException(
                StatusCodes.Status403Forbidden,
                "You must own one selected report.");
        }

        if (lost.UserId == found.UserId)
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "You cannot match your own lost and found reports.");
        }

        if (!string.Equals(
                lost.Status,
                "ACTIVE",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                found.Status,
                "ACTIVE",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "Both reports must still be active.");
        }

        if (await _repository.PairExistsAsync(
                lost.Id,
                found.Id,
                cancellationToken))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "This pair already has a match, or one of these reports already has a confirmed match.");
        }

        return new VerifiedPair(
            lost,
            found,
            lost.UserId == userId
                ? "LOST"
                : "FOUND");
    }

    private async Task<ClaimPreview> CalculateAsync(
        VerifiedPair pair,
        CancellationToken cancellationToken)
    {
        var title = Similarity(
            pair.Lost.Title,
            pair.Found.Title);

        var description = Similarity(
            pair.Lost.Description,
            pair.Found.Description);

        var category = string.Equals(
            pair.Lost.Category.Trim(),
            pair.Found.Category.Trim(),
            StringComparison.OrdinalIgnoreCase)
                ? 100m
                : 0m;

        var bothReportsHavePhotos =
            HasCurrentPhoto(pair.Lost) &&
            HasCurrentPhoto(pair.Found);

        var imageDescription = 0m;
        var attributes = 0m;

        ImageEvidence? lostImage = null;
        ImageEvidence? foundImage = null;

        var weightedScores = new List<(
            decimal Score,
            decimal Weight)>
        {
            (title, 20m),
            (category, 15m),
            (description, 20m)
        };

        if (bothReportsHavePhotos)
        {
            lostImage = await _repository.GetImageEvidenceAsync(
                pair.Lost,
                "LOST",
                cancellationToken);

            foundImage = await _repository.GetImageEvidenceAsync(
                pair.Found,
                "FOUND",
                cancellationToken);

            imageDescription = Similarity(
                lostImage.Description,
                foundImage.Description);

            attributes = Similarity(
                ReadAttributes(lostImage.AttributesJson),
                ReadAttributes(foundImage.AttributesJson));

            weightedScores.Add((imageDescription, 30m));
            weightedScores.Add((attributes, 15m));
        }

        var totalWeight = weightedScores.Sum(
            component => component.Weight);

        var score = Math.Round(
            weightedScores.Sum(
                component => component.Score *
                    component.Weight) / totalWeight,
            2,
            MidpointRounding.AwayFromZero);

        var lostView = pair.Lost.ToView("LOST");
        var foundView = pair.Found.ToView("FOUND");

        var fingerprint = JsonSerializer.Serialize(new
        {
            ScoringVersion,
            Lost = lostView,
            Found = foundView,
            LostOwner = pair.Lost.UserId,
            FoundOwner = pair.Found.UserId,
            LostPhotos = pair.Lost.PhotoUrls,
            FoundPhotos = pair.Found.PhotoUrls,
            LostEvidence = lostImage,
            FoundEvidence = foundImage
        });

        var previewVersion = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(fingerprint)));

        return new ClaimPreview(
            lostView,
            foundView,
            score,
            Threshold,
            score >= Threshold,
            pair.ClaimantRole,
            previewVersion,
            new ScoreBreakdown(
                title,
                category,
                description,
                imageDescription,
                attributes));
    }

    private static bool HasCurrentPhoto(ItemReport report)
    {
        return report.PhotoUrls?.Any(
            url => !string.IsNullOrWhiteSpace(url)) == true;
    }

    private static decimal Similarity(
        string left,
        string right)
    {
        var leftWords = Tokenize(left);
        var rightWords = Tokenize(right);

        if (leftWords.Count == 0 ||
            rightWords.Count == 0)
        {
            return 0m;
        }

        var common =
            leftWords.Count(rightWords.Contains);

        return Math.Round(
            200m * common /
            (leftWords.Count + rightWords.Count),
            2,
            MidpointRounding.AwayFromZero);
    }

    private static HashSet<string> Tokenize(string text)
    {
        return WordPattern
            .Matches(text.ToLowerInvariant())
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => !StopWords.Contains(word))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ReadAttributes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var values = new List<string>();

            foreach (var name in AttributeNames)
            {
                if (!document.RootElement.TryGetProperty(
                        name,
                        out var value))
                {
                    continue;
                }

                if (value.ValueKind ==
                    JsonValueKind.String)
                {
                    values.Add(value.GetString() ?? "");
                }
                else if (value.ValueKind ==
                         JsonValueKind.Array)
                {
                    foreach (var entry in value.EnumerateArray())
                    {
                        if (entry.ValueKind ==
                            JsonValueKind.String)
                        {
                            values.Add(
                                entry.GetString() ?? "");
                        }
                    }
                }
            }

            return string.Join(" ", values);
        }
        catch (JsonException)
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "An image description is not ready for comparison.");
        }
    }
}
