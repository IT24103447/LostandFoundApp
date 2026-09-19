using System.Text.Json;
using System.Text.RegularExpressions;
using MatchingService.Models;

namespace MatchingService.Services;

public sealed class ImageDescriptionValidator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly string[] ExpectedProperties =
    [
        "description",
        "objectType",
        "colours",
        "materials",
        "visibleBrand",
        "patterns",
        "condition",
        "distinctiveFeatures",
        "uncertainty"
    ];

    private static readonly Regex PrivateContentPattern = new(
        @"https?://|www\.|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|\b(?:\+?\d[\s().-]*){7,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public GeneratedImageDescription Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16_000)
        {
            throw InvalidOutput();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidOutput();
            }

            var properties = root.EnumerateObject().ToArray();

            if (properties.Length != ExpectedProperties.Length ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != ExpectedProperties.Length)
            {
                throw InvalidOutput();
            }

            foreach (var name in ExpectedProperties)
            {
                if (!root.TryGetProperty(name, out _))
                {
                    throw InvalidOutput();
                }
            }

            var description = ReadString(
                root.GetProperty("description"),
                maximumLength: 1500,
                allowNull: false);

            if (description is null || description.Length < 10)
            {
                throw InvalidOutput();
            }

            var result = new GeneratedImageDescription
            {
                Description = description,
                ObjectType = ReadString(
                    root.GetProperty("objectType"), 120, true),
                Colours = ReadArray(root.GetProperty("colours")),
                Materials = ReadArray(root.GetProperty("materials")),
                VisibleBrand = ReadString(
                    root.GetProperty("visibleBrand"), 120, true),
                Patterns = ReadArray(root.GetProperty("patterns")),
                Condition = ReadString(
                    root.GetProperty("condition"), 200, true),
                DistinctiveFeatures = ReadArray(
                    root.GetProperty("distinctiveFeatures")),
                Uncertainty = ReadArray(root.GetProperty("uncertainty"))
            };

            var normalizedJson = JsonSerializer.Serialize(
                result,
                JsonOptions);

            if (PrivateContentPattern.IsMatch(normalizedJson))
            {
                throw new ImageProcessingException(
                    "AI_OUTPUT_PRIVACY_REJECTED",
                    retryable: true);
            }

            return result;
        }
        catch (JsonException)
        {
            throw InvalidOutput();
        }
        catch (RegexMatchTimeoutException)
        {
            throw InvalidOutput();
        }
    }

    private static string? ReadString(
        JsonElement element,
        int maximumLength,
        bool allowNull)
    {
        if (allowNull && element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw InvalidOutput();
        }

        var value = element.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw InvalidOutput();
        }

        return value;
    }

    private static string[] ReadArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() > 12)
        {
            throw InvalidOutput();
        }

        return element.EnumerateArray()
            .Select(item => ReadString(item, 250, false)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ImageProcessingException InvalidOutput() =>
        new("AI_OUTPUT_INVALID", retryable: true);
}