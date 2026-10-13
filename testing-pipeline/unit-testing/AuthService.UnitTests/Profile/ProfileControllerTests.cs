using System.Security.Claims;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Models.Events;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.Profile;

public class ProfileControllerTests
{
    private readonly AuthControllerTestContext _context = new();

    [Theory]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    public async Task View_profile_uses_authenticated_identity_and_returns_contact_details(string claimType)
    {
        var user = AuthControllerTestContext.User();
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Jwt.Setup(x => x.IssueLoginToken(user)).Returns("refreshed.jwt");
        var controller = _context.Controller(subject: user.Id.ToString(), claimType: claimType);

        var result = await controller.GetMe(CancellationToken.None);

        var response = Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        AssertProfile(user, response);
        Assert.Equal("refreshed.jwt", response.Token);
        _context.Users.Verify(x => x.GetByIdAsync(user.Id, CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("NameOnly", "sub")]
    [InlineData("PhoneOnly", "sub")]
    [InlineData("Both", ClaimTypes.NameIdentifier)]
    [InlineData("Unchanged", "sub")]
    public async Task Profile_update_persists_callers_record_returns_reload_and_publishes_only_changed_fields(string scenario, string claimType)
    {
        var before = AuthControllerTestContext.User();
        var request = new UpdateProfileRequest
        {
            Name = scenario is "NameOnly" or "Both" ? "Updated User" : before.Name,
            PhoneNo = scenario is "PhoneOnly" or "Both" ? "+94779876543" : before.PhoneNo
        };
        var after = new User
        {
            Id = before.Id, Name = request.Name, PhoneNo = request.PhoneNo,
            Email = before.Email, IsEmailVerified = true, CreatedAt = before.CreatedAt
        };
        var changedFields = scenario switch
        {
            "NameOnly" => new[] { "name" }, "PhoneOnly" => new[] { "phoneNo" },
            "Both" => new[] { "name", "phoneNo" }, _ => Array.Empty<string>()
        };
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        var sequence = new MockSequence();
        _context.Users.InSequence(sequence).Setup(x => x.GetByIdAsync(before.Id, ct)).ReturnsAsync(before);
        _context.Users.InSequence(sequence).Setup(x => x.PhoneExistsForOtherUserAsync(before.Id, request.PhoneNo, ct)).ReturnsAsync(false);
        _context.Users.InSequence(sequence).Setup(x => x.UpdateProfileAsync(before.Id, request.Name, request.PhoneNo, ct)).Returns(Task.CompletedTask);
        _context.Users.InSequence(sequence).Setup(x => x.GetByIdAsync(before.Id, ct)).ReturnsAsync(after);
        if (changedFields.Length > 0)
            _context.Publisher.InSequence(sequence).Setup(x => x.PublishAsync($"{AuthControllerTestContext.TopicPrefix}.user.profile_updated",
                It.Is<UserProfileUpdatedEvent>(e => e.UserId == before.Id && e.UpdatedFields.SequenceEqual(changedFields)
                    && e.Email == after.Email && e.Name == after.Name && e.Phone == after.PhoneNo), ct)).Returns(ValueTask.CompletedTask);
        var controller = _context.Controller(subject: before.Id.ToString(), claimType: claimType);

        var result = await controller.UpdateMe(request, ct);

        var response = Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        AssertProfile(after, response);
        _context.Users.Verify(x => x.GetByIdAsync(before.Id, ct), Times.Exactly(2));
        _context.Users.Verify(x => x.PhoneExistsForOtherUserAsync(before.Id, request.PhoneNo, ct), Times.Once);
        _context.Users.Verify(x => x.UpdateProfileAsync(before.Id, request.Name, request.PhoneNo, ct), Times.Once);
        _context.Publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<UserProfileUpdatedEvent>(), ct),
            changedFields.Length > 0 ? Times.Once() : Times.Never());
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Duplicate_profile_phone_rejects_without_saving_or_publishing()
    {
        var user = AuthControllerTestContext.User();
        var request = new UpdateProfileRequest { Name = "Updated User", PhoneNo = "+94779876543" };
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Users.Setup(x => x.PhoneExistsForOtherUserAsync(user.Id, request.PhoneNo, CancellationToken.None)).ReturnsAsync(true);

        var result = await _context.Controller(subject: user.Id.ToString()).UpdateMe(request, CancellationToken.None);

        var response = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("This phone number is already in use by another account.", AuthControllerTestContext.Body(response).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("View", null)]
    [InlineData("View", "invalid-id")]
    [InlineData("Update", null)]
    [InlineData("Update", "invalid-id")]
    public async Task Missing_or_invalid_profile_identity_rejects_before_repository_access(string action, string? subject)
    {
        var controller = _context.Controller(subject: subject);

        var result = action == "View" ? await controller.GetMe(CancellationToken.None)
            : await controller.UpdateMe(new UpdateProfileRequest { Name = "User", PhoneNo = "+94771234567" }, CancellationToken.None);

        var response = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid session.", AuthControllerTestContext.Body(response).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("View")]
    [InlineData("Update")]
    public async Task Missing_profile_account_rejects_without_saving(string action)
    {
        var id = Guid.NewGuid();
        _context.Users.Setup(x => x.GetByIdAsync(id, CancellationToken.None)).ReturnsAsync((User?)null);
        var controller = _context.Controller(subject: id.ToString());

        var result = action == "View" ? await controller.GetMe(CancellationToken.None)
            : await controller.UpdateMe(new UpdateProfileRequest { Name = "User", PhoneNo = "+94771234567" }, CancellationToken.None);

        var response = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("User not found.", AuthControllerTestContext.Body(response).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(true, false, 403, "Your account has been suspended.")]
    [InlineData(false, false, 401, "Email not verified.")]
    public async Task Ineligible_account_cannot_view_profile(bool suspended, bool verified, int status, string error)
    {
        var user = AuthControllerTestContext.User(verified);
        user.IsKicked = suspended;
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);

        var result = await _context.Controller(subject: user.Id.ToString()).GetMe(CancellationToken.None);

        var response = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, AuthControllerTestContext.Body(response).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    private static void AssertProfile(User user, UserProfileDto response)
    {
        Assert.Equal(user.Id, response.Id);
        Assert.Equal(user.Email, response.Email);
        Assert.Equal(user.Name, response.Name);
        Assert.Equal(user.PhoneNo, response.PhoneNo);
        Assert.Equal(user.IsAdmin, response.IsAdmin);
        Assert.Equal(user.IsEmailVerified, response.IsEmailVerified);
        Assert.Equal(user.CreatedAt, response.CreatedAt);
    }
}
