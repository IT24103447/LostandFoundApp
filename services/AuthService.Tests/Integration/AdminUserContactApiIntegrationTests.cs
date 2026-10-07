using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Models.Dtos;
using Xunit;

namespace AuthService.Tests.Integration;

// HTTP-level tests for GET /api/admin/users/{id:guid} — the user-contact half of Story LF-79.
// Real MySQL via CustomWebApplicationFactory, real routing, real [Authorize(Policy = "AdminOnly")]
// pipeline. Only IEmailService and IEventPublisher are faked, and every test class in this
// project pays for its own MySQL container (there is no [CollectionDefinition] here).
//
// Why this file exists separately from Controllers/AdminUserContactStoryTests.cs: direct
// controller calls bypass the MVC filter pipeline entirely, so they cannot prove anything about
// authorization. These tests can.
//
// Two facts about this service that shape every test below:
//
//  1. "AdminOnly" is DB-based, not claim-based. AdminOnlyHandler looks the caller up via
//     IUsersRepository.GetByIdAsync and checks user.IsAdmin. So a hand-minted JWT proves nothing
//     here — the caller must be a real row with is_admin = 1. (ItemService's policy is the
//     opposite, which is why AdminTestTokenHelper works there and must not be copied here.)
//     The tests therefore log in as admin1@lostandfound.com, seeded by Program.cs in Development.
//
//  2. JWTs are read from the httpOnly auth_token cookie only. Program.cs:89-99 sets
//     OnMessageReceived to read the cookie and there is no Authorization: Bearer fallback.
public class AdminUserContactApiIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminUserContactApiIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // admin1/admin2/admin3 are seeded by Program.cs (SeedUsersAsync) with known names, emails and
    // phone numbers, all already email-verified. Using admin2 as the subject lets these tests
    // assert exact field values without seeding anything or writing SQL directly.
    private const string SeededAdminEmail = "admin1@lostandfound.com";
    private const string SeededAdminPassword = "Admin123!";

    private const string TargetAdminEmail = "admin2@lostandfound.com";
    private const string TargetAdminName = "Admin Two";
    private const string TargetAdminPhone = "+94770000002";

    private async Task<string> LoginAsync(string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookie = response.ExtractCookieValue("auth_token");
        Assert.False(string.IsNullOrEmpty(cookie), "login did not set an auth_token cookie");
        return cookie!;
    }

    // Logs in and returns a client that is already authenticated as that user.
    //
    // The cookie is applied as a default header on a *fresh* client rather than per request, and
    // one client is created per identity. That is not incidental tidiness — it is required for
    // correctness. CreateClient() installs a CookieContainer, so once any response on a client
    // carries Set-Cookie, the container starts injecting its own auth_token automatically. Adding
    // a manually supplied Cookie header on top of that yields a request with two auth_token
    // cookies, and the server reads whichever one wins. Observed directly: a test that logged in as
    // a throwaway user and then issued an admin request on the same client got 403, because the
    // container's non-admin token shadowed the admin header.
    //
    // One client per identity, header only, no ambiguity — and a test can never accidentally
    // borrow another identity's session.
    private async Task<HttpClient> LoginAsNewClientAsync(string email, string password)
    {
        var cookie = await LoginAsync(email, password);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"auth_token={cookie}");
        return client;
    }

    private Task<HttpClient> LoginAsSeededAdminAsync() => LoginAsNewClientAsync(SeededAdminEmail, SeededAdminPassword);

    // Resolves a user's GUID through the admin list route rather than by querying MySQL, so no
    // test-only database accessor is needed. Matches on live (non-deleted) users only, which is
    // the default when isDeleted is not supplied.
    private async Task<Guid> FindUserIdAsync(HttpClient adminClient, string email)
    {
        var url = $"/api/admin/users?search={Uri.EscapeDataString(email)}&pageSize=100";

        var response = await adminClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var matches = body.GetProperty("users").EnumerateArray()
            .Where(u => u.GetProperty("email").GetString() == email)
            .ToList();

        Assert.True(matches.Count == 1, $"expected exactly one user matching '{email}' but found {matches.Count}");
        return matches[0].GetProperty("id").GetGuid();
    }

    // Same idea, but keyed on id rather than email. Needed after an account deletion because
    // SoftDeleteAsync anonymises the row as well as timestamping it — email becomes
    // del-<first 8 chars of id>@deleted.local and phone_no becomes DEL-<first 8 chars> — so
    // searching by the original email after deletion correctly finds nothing.
    private async Task<JsonElement> FindListedUserAsync(HttpClient adminClient, Guid id, bool isDeleted)
    {
        var flag = isDeleted.ToString().ToLowerInvariant();
        var response = await adminClient.GetAsync($"/api/admin/users?pageSize=100&isDeleted={flag}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var matches = body.GetProperty("users").EnumerateArray()
            .Where(u => u.GetProperty("id").GetGuid() == id)
            .ToList();

        Assert.True(matches.Count == 1, $"expected user {id} to be listed exactly once with isDeleted={flag}");
        return matches[0];
    }

    // ---------- Reading a contact ----------

    [Fact]
    public async Task GetUserContact_SeededAdmin_ReturnsAnotherAdminsExactContactDetails()
    {
        var admin = await LoginAsSeededAdminAsync();
        var targetId = await FindUserIdAsync(admin, TargetAdminEmail);

        var response = await admin.GetAsync($"/api/admin/users/{targetId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TargetAdminName, body.GetProperty("name").GetString());
        Assert.Equal(TargetAdminEmail, body.GetProperty("email").GetString());
        Assert.Equal(TargetAdminPhone, body.GetProperty("phoneNo").GetString());
    }

    // Wire-level leak guard. AdminUserContactDto is only three fields wide by design; this asserts
    // what actually reaches the client, so widening the DTO later cannot quietly start shipping
    // id, isAdmin, isKicked or a credential to the browser.
    [Fact]
    public async Task GetUserContact_ResponseBody_ContainsExactlyNameEmailAndPhoneNo()
    {
        var admin = await LoginAsSeededAdminAsync();
        var targetId = await FindUserIdAsync(admin, TargetAdminEmail);

        var response = await admin.GetAsync($"/api/admin/users/{targetId}");
        var raw = await response.Content.ReadAsStringAsync();

        var propertyNames = JsonDocument.Parse(raw).RootElement.EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "email", "name", "phoneNo" }, propertyNames);
    }

    [Fact]
    public async Task GetUserContact_UnknownGuid_Returns404WithUserNotFoundError()
    {
        var admin = await LoginAsSeededAdminAsync();

        var response = await admin.GetAsync($"/api/admin/users/{Guid.NewGuid()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("User not found.", body.GetProperty("error").GetString());
    }

    // The route template is {id:guid}, so a malformed id never matches the action and MVC returns
    // its own 404 with an empty body — a different 404 from the controller's {"error": ...}.
    [Fact]
    public async Task GetUserContact_MalformedId_Returns404FromRoutingWithEmptyBody()
    {
        var admin = await LoginAsSeededAdminAsync();

        var response = await admin.GetAsync("/api/admin/users/not-a-guid");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(raw), $"expected an empty body from routing but got '{raw}'");
    }

    // ---------- Authorization ----------

    [Fact]
    public async Task GetUserContact_AnonymousRequest_Returns401()
    {
        var response = await _client.GetAsync($"/api/admin/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The one assertion nothing in the repo previously made: that a real, authenticated,
    // email-verified non-admin is rejected with 403 specifically, and that the rejection body
    // carries none of the target's contact details. AdminAuthorizationFlowTests only accepts
    // "401 or 403", so it cannot distinguish a working policy from a broken login.
    [Fact]
    public async Task GetUserContact_VerifiedNonAdminCookie_Returns403AndLeaksNoContactDetails()
    {
        var admin = await LoginAsSeededAdminAsync();
        var targetId = await FindUserIdAsync(admin, TargetAdminEmail);

        var nonAdmin = await TestUserFactory.RegisterAndVerifyAsync(_client, _factory.FakeEmail);
        var nonAdminClient = await LoginAsNewClientAsync(nonAdmin.Email, nonAdmin.Password);

        var response = await nonAdminClient.GetAsync($"/api/admin/users/{targetId}");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(TargetAdminPhone, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetAdminEmail, raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Known gap: soft-deleted users ----------

    // Documents a genuine asymmetry rather than an endorsement. GET /api/admin/users?isDeleted=true
    // lists soft-deleted users, but GET /api/admin/users/{id} cannot fetch any of them, because
    // UsersRepository.GetByIdAsync hard-codes `AND deleted_at IS NULL`. An admin can see that a
    // deleted account exists in the list and then be told "User not found." when fetching its
    // details. Tracked in known-gaps.md.
    [Fact]
    public async Task GetUserContact_SoftDeletedUser_Returns404_EvenThoughTheListRouteCanShowThem()
    {
        var admin = await LoginAsSeededAdminAsync();

        // Register and verify a throwaway user, then let them delete their own account so the row
        // is soft-deleted (deleted_at set) rather than physically removed.
        var target = await TestUserFactory.RegisterAndVerifyAsync(_client, _factory.FakeEmail);
        var targetId = await FindUserIdAsync(admin, target.Email);

        var targetClient = await LoginAsNewClientAsync(target.Email, target.Password);
        var deleteResponse = await targetClient.SendAsync(CookieTestHelpers.NewJsonRequest(
            HttpMethod.Delete, "/api/auth/me", new DeleteAccountRequest { Password = target.Password }));
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // The list route can still find them once isDeleted=true is passed, and the row has been
        // anonymised: SoftDeleteAsync overwrites email and phone_no as well as setting deleted_at.
        var listed = await FindListedUserAsync(admin, targetId, isDeleted: true);
        Assert.Equal($"del-{targetId.ToString()[..8]}@deleted.local", listed.GetProperty("email").GetString());
        Assert.DoesNotContain(target.Email, listed.GetProperty("email").GetString()!, StringComparison.OrdinalIgnoreCase);

        // But get-by-id reports "not found", because GetByIdAsync hard-filters deleted_at IS NULL.
        var response = await admin.GetAsync($"/api/admin/users/{targetId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("User not found.", body.GetProperty("error").GetString());
    }
}