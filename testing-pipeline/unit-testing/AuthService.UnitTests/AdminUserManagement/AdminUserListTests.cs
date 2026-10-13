using System.Text.Json;
using AuthService.Models;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.AdminUserManagement;

public class AdminUserListTests
{
    private readonly AuthControllerTestContext _context = new();

    [Fact]
    public async Task Admin_user_list_returns_profile_status_and_pagination_without_password_hashes()
    {
        var first = AuthControllerTestContext.User();
        var second = AuthControllerTestContext.User(verified: false);
        second.IsAdmin = true;
        second.IsKicked = true;
        var users = new List<User> { first, second };
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _context.Users.Setup(x => x.SearchUsersAsync(null, null, null, null, 20, 0, ct)).ReturnsAsync(users);
        _context.Users.Setup(x => x.CountUsersAsync(null, null, null, null, ct)).ReturnsAsync(45);

        var result = Assert.IsType<OkObjectResult>(await _context.Admin(Guid.NewGuid()).GetUsers(null, null, null, null, ct: ct));

        var body = AuthControllerTestContext.Body(result);
        Assert.Equal(45, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, body.GetProperty("totalPages").GetInt32());
        var rows = body.GetProperty("users").EnumerateArray().ToArray();
        Assert.Equal(users.Count, rows.Length);
        for (var i = 0; i < users.Count; i++) AssertUser(users[i], rows[i]);
        _context.Users.Verify(x => x.SearchUsersAsync(null, null, null, null, 20, 0, ct), Times.Once);
        _context.Users.Verify(x => x.CountUsersAsync(null, null, null, null, ct), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("Spam", null, null, null)]
    [InlineData("spam@example.test", null, null, null)]
    [InlineData(null, true, false, false)]
    [InlineData(null, null, null, true)]
    public async Task Admin_search_and_status_filters_are_forwarded_to_both_list_and_count(string? search, bool? kicked, bool? verified, bool? deleted)
    {
        var user = AuthControllerTestContext.User();
        if (search == "Spam") user.Name = "Spam Account";
        if (search == "spam@example.test") user.Email = search;
        if (kicked.HasValue) user.IsKicked = kicked.Value;
        if (verified.HasValue) user.IsEmailVerified = verified.Value;
        if (deleted == true) user.DeletedAt = DateTime.UtcNow.AddDays(-1);
        _context.Users.Setup(x => x.SearchUsersAsync(search, kicked, verified, deleted, 20, 0, CancellationToken.None)).ReturnsAsync(new List<User> { user });
        _context.Users.Setup(x => x.CountUsersAsync(search, kicked, verified, deleted, CancellationToken.None)).ReturnsAsync(1);

        var result = Assert.IsType<OkObjectResult>(await _context.Admin(Guid.NewGuid()).GetUsers(search, kicked, verified, deleted));

        var body = AuthControllerTestContext.Body(result);
        AssertUser(user, Assert.Single(body.GetProperty("users").EnumerateArray()));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("totalPages").GetInt32());
        _context.Users.Verify(x => x.SearchUsersAsync(search, kicked, verified, deleted, 20, 0, CancellationToken.None), Times.Once);
        _context.Users.Verify(x => x.CountUsersAsync(search, kicked, verified, deleted, CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Empty_admin_search_returns_empty_list_and_zero_pages()
    {
        _context.Users.Setup(x => x.SearchUsersAsync("no-match", null, null, null, 20, 0, CancellationToken.None)).ReturnsAsync(new List<User>());
        _context.Users.Setup(x => x.CountUsersAsync("no-match", null, null, null, CancellationToken.None)).ReturnsAsync(0);

        var result = Assert.IsType<OkObjectResult>(await _context.Admin(Guid.NewGuid()).GetUsers("no-match", null, null, null));

        var body = AuthControllerTestContext.Body(result);
        Assert.Empty(body.GetProperty("users").EnumerateArray());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(0, body.GetProperty("totalPages").GetInt32());
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(0, 0, 1, 1, 0)]
    [InlineData(2, 101, 2, 100, 100)]
    [InlineData(3, 10, 3, 10, 20)]
    public async Task Admin_pagination_clamps_bounds_and_computes_repository_offset(int page, int size, int expectedPage, int expectedSize, int expectedOffset)
    {
        _context.Users.Setup(x => x.SearchUsersAsync(null, null, null, null, expectedSize, expectedOffset, CancellationToken.None)).ReturnsAsync(new List<User>());
        _context.Users.Setup(x => x.CountUsersAsync(null, null, null, null, CancellationToken.None)).ReturnsAsync(21);

        var result = Assert.IsType<OkObjectResult>(await _context.Admin(Guid.NewGuid()).GetUsers(null, null, null, null, page, size));

        var body = AuthControllerTestContext.Body(result);
        Assert.Equal(expectedPage, body.GetProperty("page").GetInt32());
        Assert.Equal(expectedSize, body.GetProperty("pageSize").GetInt32());
        Assert.Equal((int)Math.Ceiling(21.0 / expectedSize), body.GetProperty("totalPages").GetInt32());
        _context.Users.Verify(x => x.SearchUsersAsync(null, null, null, null, expectedSize, expectedOffset, CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }

    private static void AssertUser(User user, JsonElement row)
    {
        Assert.Equal(user.Id, row.GetProperty("Id").GetGuid());
        Assert.Equal(user.Name, row.GetProperty("Name").GetString());
        Assert.Equal(user.Email, row.GetProperty("Email").GetString());
        Assert.Equal(user.PhoneNo, row.GetProperty("PhoneNo").GetString());
        Assert.Equal(user.IsAdmin, row.GetProperty("IsAdmin").GetBoolean());
        Assert.Equal(user.IsEmailVerified, row.GetProperty("IsEmailVerified").GetBoolean());
        Assert.Equal(user.IsKicked, row.GetProperty("IsKicked").GetBoolean());
        Assert.Equal(user.CreatedAt, row.GetProperty("CreatedAt").GetDateTime());
        if (user.DeletedAt.HasValue) Assert.Equal(user.DeletedAt.Value, row.GetProperty("DeletedAt").GetDateTime());
        else Assert.Equal(JsonValueKind.Null, row.GetProperty("DeletedAt").ValueKind);
        Assert.False(row.TryGetProperty("PasswordHash", out _));
    }
}
