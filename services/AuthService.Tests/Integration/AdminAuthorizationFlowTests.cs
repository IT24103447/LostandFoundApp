using System.Net;
using System.Net.Http.Json;
using AuthService.Models.Dtos;
using Xunit;

namespace AuthService.Tests.Integration;

/// <summary>
/// This is exactly the kind of thing unit tests can't fully settle: the comment in
/// AdminRouteAuthorizationTests.cs (mocked unit test) notes uncertainty about whether
/// AdminController is actually locked down to admins only, since that depends on
/// [Authorize] policy WIRING in Program.cs — which a mocked controller test never runs.
///
/// This test hits the real route through the real ASP.NET Core auth pipeline (real
/// policy registration, real JWT claims, real [Authorize(Policy = "AdminOnly")]) and
/// settles it directly: does a non-admin actually get rejected, and does a real admin
/// actually get through?
/// </summary>
public class AdminAuthorizationFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminAuthorizationFlowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegularUser_CannotAccessAdminUsersEndpoint()
    {
        var user = await TestUserFactory.RegisterAndVerifyAsync(_client, _factory.FakeEmail);

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = user.Password
        });
        var cookie = loginResponse.ExtractCookieValue("auth_token");
        Assert.False(string.IsNullOrEmpty(cookie));

        var adminRequest = CookieTestHelpers.NewJsonRequest(HttpMethod.Get, "/api/admin/users")
            .WithCookie("auth_token", cookie!);
        var adminResponse = await _client.SendAsync(adminRequest);

        // A regular (non-admin) user must be rejected — either 401/403 is an acceptable
        // "denied" outcome, but it must NOT be 200.
        Assert.NotEqual(HttpStatusCode.OK, adminResponse.StatusCode);
        Assert.True(
            adminResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Expected 401 or 403 for a non-admin hitting an admin route, got {adminResponse.StatusCode}.");
    }

    [Fact]
    public async Task AnonymousUser_CannotAccessAdminUsersEndpoint()
    {
        var response = await _client.GetAsync("/api/admin/users");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SeededAdmin_CanAccessAdminUsersEndpoint_AndSeesRealUsers()
    {
        // admin1@lostandfound.com / Admin123! is seeded by Program.cs on startup in the
        // Development environment, which CustomWebApplicationFactory runs under.
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "admin1@lostandfound.com",
            Password = "Admin123!"
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = loginResponse.ExtractCookieValue("auth_token");
        Assert.False(string.IsNullOrEmpty(cookie));

        var adminRequest = CookieTestHelpers.NewJsonRequest(HttpMethod.Get, "/api/admin/users?pageSize=100")
            .WithCookie("auth_token", cookie!);
        var adminResponse = await _client.SendAsync(adminRequest);

        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        var body = await adminResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() > 0);
    }
}
