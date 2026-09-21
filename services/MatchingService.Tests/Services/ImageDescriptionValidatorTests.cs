using MatchingService.Services;

namespace MatchingService.Tests.Services;

/// <summary>
/// Story 1, Scenario 4 contract tests: the Gemini JSON response must be well-formed and must never
/// contain private/unverifiable content. Not itself in the acceptance criteria list, but real
/// enforcement logic behind Scenario 4 that needs its own coverage.
/// </summary>
public sealed class ImageDescriptionValidatorTests
{
    private const string ValidJson = """
        {
          "description": "A dark blue backpack with a front zip pocket.",
          "objectType": "backpack",
          "colours": ["blue", "black"],
          "materials": ["canvas"],
          "visibleBrand": null,
          "patterns": [],
          "condition": "lightly worn",
          "distinctiveFeatures": ["reflective strip"],
          "uncertainty": []
        }
        """;

    // The happy path: a well-formed, fully-shaped response maps cleanly to GeneratedImageDescription.
    [Fact]
    public void Validate_WellFormedJson_ReturnsMappedDescription()
    {
        var result = new ImageDescriptionValidator().Validate(ValidJson);

        Assert.Equal("A dark blue backpack with a front zip pocket.", result.Description);
        Assert.Equal("backpack", result.ObjectType);
        Assert.Equal(["blue", "black"], result.Colours);
        Assert.Null(result.VisibleBrand);
    }

    /* Scenario 4: Gemini is instructed never to reproduce contact details, but the validator enforces it independently of the prompt. 
       A URL anywhere in the normalized output is rejected. */
    [Theory]
    [InlineData("Contact the owner at https://example.com/owner")]
    [InlineData("Email seen on tag: someone@example.com")]
    [InlineData("Phone number visible: +1 555 123 4567")]
    public void Validate_PrivateContentInDescription_ThrowsPrivacyRejected(string description)
    {
        var json = $$"""
            {
              "description": "{{description}}",
              "objectType": null, "colours": [], "materials": [], "visibleBrand": null,
              "patterns": [], "condition": null, "distinctiveFeatures": [], "uncertainty": []
            }
            """;

        var exception = Assert.Throws<ImageProcessingException>(
            () => new ImageDescriptionValidator().Validate(json));

        Assert.Equal("AI_OUTPUT_PRIVACY_REJECTED", exception.Code);
        Assert.True(exception.Retryable);
    }

    // A missing required property means Gemini didn't follow the required JSON shape at all.
    [Fact]
    public void Validate_MissingRequiredProperty_ThrowsInvalidOutput()
    {
        const string json = """
            {
              "description": "A dark blue backpack with a front zip pocket.",
              "objectType": null, "colours": [], "materials": [], "visibleBrand": null,
              "patterns": [], "condition": null, "distinctiveFeatures": []
            }
            """;

        var exception = Assert.Throws<ImageProcessingException>(
            () => new ImageDescriptionValidator().Validate(json));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
    }

    // An unexpected extra property is just as invalid as a missing one: the shape must match exactly.
    [Fact]
    public void Validate_UnexpectedExtraProperty_ThrowsInvalidOutput()
    {
        const string json = """
            {
              "description": "A dark blue backpack with a front zip pocket.",
              "objectType": null, "colours": [], "materials": [], "visibleBrand": null,
              "patterns": [], "condition": null, "distinctiveFeatures": [], "uncertainty": [],
              "confidence": 0.9
            }
            """;

        Assert.Throws<ImageProcessingException>(() => new ImageDescriptionValidator().Validate(json));
    }

    // A description under 10 characters is treated as too thin to be a real description.
    [Fact]
    public void Validate_DescriptionTooShort_ThrowsInvalidOutput()
    {
        const string json = """
            {
              "description": "short", "objectType": null, "colours": [], "materials": [],
              "visibleBrand": null, "patterns": [], "condition": null,
              "distinctiveFeatures": [], "uncertainty": []
            }
            """;

        Assert.Throws<ImageProcessingException>(() => new ImageDescriptionValidator().Validate(json));
    }

    // description is the one required, non-nullable field per the prompt's schema.
    [Fact]
    public void Validate_NullDescription_ThrowsInvalidOutput()
    {
        const string json = """
            {
              "description": null, "objectType": null, "colours": [], "materials": [],
              "visibleBrand": null, "patterns": [], "condition": null,
              "distinctiveFeatures": [], "uncertainty": []
            }
            """;

        Assert.Throws<ImageProcessingException>(() => new ImageDescriptionValidator().Validate(json));
    }

    // An array with more than 12 entries is rejected rather than silently truncated.
    [Fact]
    public void Validate_ArrayExceedsMaxLength_ThrowsInvalidOutput()
    {
        var colours = string.Join(",", Enumerable.Range(0, 13).Select(i => $"\"c{i}\""));
        var json = $$"""
            {
              "description": "A dark blue backpack with a front zip pocket.",
              "objectType": null, "colours": [{{colours}}], "materials": [], "visibleBrand": null,
              "patterns": [], "condition": null, "distinctiveFeatures": [], "uncertainty": []
            }
            """;

        Assert.Throws<ImageProcessingException>(() => new ImageDescriptionValidator().Validate(json));
    }

    /* Syntactically broken JSON from the model must be caught and normalized to the same
       AI_OUTPUT_INVALID error code as any other malformed response, not an unhandled JsonException. */
    [Fact]
    public void Validate_MalformedJson_ThrowsInvalidOutput()
    {
        var exception = Assert.Throws<ImageProcessingException>(
            () => new ImageDescriptionValidator().Validate("{ not json"));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
    }

    // A JSON array at the root (not an object) is rejected outright.
    [Fact]
    public void Validate_NonObjectRoot_ThrowsInvalidOutput()
    {
        Assert.Throws<ImageProcessingException>(() => new ImageDescriptionValidator().Validate("[1,2,3]"));
    }

    // Empty/blank input and an oversized payload (>16,000 chars) are both rejected before parsing.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrBlankInput_ThrowsInvalidOutput(string json)
    {
        Assert.Throws<ImageProcessingException>(() => new ImageDescriptionValidator().Validate(json));
    }
}
