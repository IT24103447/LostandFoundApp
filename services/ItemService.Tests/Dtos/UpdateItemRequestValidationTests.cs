using System.ComponentModel.DataAnnotations;
using ItemService.Models.Dtos;

namespace ItemService.Tests.Dtos;

public class UpdateItemRequestValidationTests
{
    [Fact]
    public void UpdateLostItemRequest_ExactMaximumLengthsAreValid()
    {
        var request = new UpdateLostItemRequest
        {
            Title = new string('t', 150),
            Category = new string('c', 50),
            Description = new string('d', 2000),
            DateLost = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            LastKnownLocation = new string('l', 255),
            HiddenInformation = new string('h', 500)
        };

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void UpdateFoundItemRequest_ExactMaximumLengthsAreValid()
    {
        var request = new UpdateFoundItemRequest
        {
            Title = new string('t', 150),
            Category = new string('c', 50),
            Description = new string('d', 2000),
            DateFound = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            LocationFound = new string('l', 255),
            HiddenInformation = new string('h', 500)
        };

        Assert.Empty(Validate(request));
    }

    [Theory]
    [InlineData("Title", 151)]
    [InlineData("Category", 51)]
    [InlineData("Description", 2001)]
    [InlineData("LastKnownLocation", 256)]
    [InlineData("HiddenInformation", 501)]
    public void UpdateLostItemRequest_OverMaximumLengthIsRejectedByTheApiModelContract(string property, int length)
    {
        var request = new UpdateLostItemRequest
        {
            Title = "Title", Category = "Accessories", Description = "Description", DateLost = "2026-09-10",
            LastKnownLocation = "Location", HiddenInformation = "Private"
        };
        typeof(UpdateLostItemRequest).GetProperty(property)!.SetValue(request, new string('x', length));

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(property));
    }

    [Theory]
    [InlineData("Title", 151)]
    [InlineData("Category", 51)]
    [InlineData("Description", 2001)]
    [InlineData("LocationFound", 256)]
    [InlineData("HiddenInformation", 501)]
    public void UpdateFoundItemRequest_OverMaximumLengthIsRejectedByTheApiModelContract(string property, int length)
    {
        var request = new UpdateFoundItemRequest
        {
            Title = "Title", Category = "Accessories", Description = "Description", DateFound = "2026-09-10",
            LocationFound = "Location", HiddenInformation = "Private"
        };
        typeof(UpdateFoundItemRequest).GetProperty(property)!.SetValue(request, new string('x', length));

        Assert.Contains(Validate(request), r => r.MemberNames.Contains(property));
    }

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }
}
