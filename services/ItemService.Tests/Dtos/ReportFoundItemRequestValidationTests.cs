using System.ComponentModel.DataAnnotations;
using ItemService.Models.Dtos;
using Xunit;

namespace ItemService.Tests.Dtos;

public class ReportFoundItemRequestValidationTests
{
    [Fact]
    public void MissingRequiredFields_AreRejected()
    {
        var results = Validate(new ReportFoundItemRequest());

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ReportFoundItemRequest.Title)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ReportFoundItemRequest.Category)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ReportFoundItemRequest.Description)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ReportFoundItemRequest.DateFound)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ReportFoundItemRequest.LocationFound)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ReportFoundItemRequest.HiddenInformation)));
    }

    [Theory]
    [InlineData(nameof(ReportFoundItemRequest.Title), 150)]
    [InlineData(nameof(ReportFoundItemRequest.Category), 50)]
    [InlineData(nameof(ReportFoundItemRequest.Description), 2000)]
    [InlineData(nameof(ReportFoundItemRequest.LocationFound), 255)]
    [InlineData(nameof(ReportFoundItemRequest.HiddenInformation), 500)]
    public void MaximumLength_IsAcceptedAtBoundary(string propertyName, int length)
    {
        var request = ValidRequest();
        typeof(ReportFoundItemRequest).GetProperty(propertyName)!.SetValue(request, new string('x', length));

        Assert.Empty(Validate(request));
    }

    [Theory]
    [InlineData(nameof(ReportFoundItemRequest.Title), 151)]
    [InlineData(nameof(ReportFoundItemRequest.Category), 51)]
    [InlineData(nameof(ReportFoundItemRequest.Description), 2001)]
    [InlineData(nameof(ReportFoundItemRequest.LocationFound), 256)]
    [InlineData(nameof(ReportFoundItemRequest.HiddenInformation), 501)]
    public void MaximumLength_IsRejectedAboveBoundary(string propertyName, int length)
    {
        var request = ValidRequest();
        typeof(ReportFoundItemRequest).GetProperty(propertyName)!.SetValue(request, new string('x', length));

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(propertyName));
    }

    private static List<ValidationResult> Validate(ReportFoundItemRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, true);
        return results;
    }

    private static ReportFoundItemRequest ValidRequest() => new()
    {
        Title = "Black wallet",
        Category = "Accessories",
        Description = "Found near the library",
        DateFound = "2026-09-08",
        LocationFound = "Main library",
        HiddenInformation = "Scratch beside clasp"
    };
}
