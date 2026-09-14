using System.ComponentModel.DataAnnotations;
using ItemService.Models.Dtos;
using Xunit;

namespace ItemService.Tests.Dtos;

public class ReportLostItemRequestValidationTests
{
    // Story 1 DTO contract: required fields and field-length limits are enforced before creation.
    // Verifies a request missing all required lost-item fields is invalid.
    [Fact]
    public void MissingRequiredFields_AreRejected()
    {
        var request = new ReportLostItemRequest();
        var results = Validate(request);

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.Title)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.Category)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.Description)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.DateLost)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.LastKnownLocation)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.HiddenInformation)));
    }

    // Verifies each lost-item field accepts its exact maximum length.
    [Theory]
    [InlineData(nameof(ReportLostItemRequest.Title), 150)]
    [InlineData(nameof(ReportLostItemRequest.Category), 50)]
    [InlineData(nameof(ReportLostItemRequest.Description), 2000)]
    [InlineData(nameof(ReportLostItemRequest.LastKnownLocation), 255)]
    [InlineData(nameof(ReportLostItemRequest.HiddenInformation), 500)]
    public void MaximumLength_IsAcceptedAtBoundary(string propertyName, int maxLength)
    {
        var request = ValidRequest();
        typeof(ReportLostItemRequest).GetProperty(propertyName)!.SetValue(request, new string('x', maxLength));

        Assert.Empty(Validate(request));
    }

    // Verifies each lost-item field rejects one character over its maximum length.
    [Theory]
    [InlineData(nameof(ReportLostItemRequest.Title), 151)]
    [InlineData(nameof(ReportLostItemRequest.Category), 51)]
    [InlineData(nameof(ReportLostItemRequest.Description), 2001)]
    [InlineData(nameof(ReportLostItemRequest.LastKnownLocation), 256)]
    [InlineData(nameof(ReportLostItemRequest.HiddenInformation), 501)]
    public void MaximumLength_IsRejectedAboveBoundary(string propertyName, int length)
    {
        var request = ValidRequest();
        typeof(ReportLostItemRequest).GetProperty(propertyName)!.SetValue(request, new string('x', length));

        Assert.Contains(Validate(request), result => result.MemberNames.Contains(propertyName));
    }

    private static List<ValidationResult> Validate(ReportLostItemRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, true);
        return results;
    }

    private static ReportLostItemRequest ValidRequest() => new()
    {
        Title = "Wallet",
        Category = "Accessories",
        Description = "Black leather wallet",
        DateLost = "2026-08-28",
        LastKnownLocation = "Library",
        HiddenInformation = "A torn inner pocket"
    };
}
