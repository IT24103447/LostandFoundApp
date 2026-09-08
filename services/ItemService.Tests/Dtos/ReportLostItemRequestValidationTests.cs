using System.ComponentModel.DataAnnotations;
using ItemService.Models.Dtos;
using Xunit;

namespace ItemService.Tests.Dtos;

public class ReportLostItemRequestValidationTests
{
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
