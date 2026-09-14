using System.Net;
using Xunit;

[Collection(ItemServiceIntegrationCollection.Name)]
public sealed class MyReportsApiIntegrationTests
{
    // Story 8 API/MySQL checks: history is JWT-scoped and deletion is verified through the real endpoint.
    private readonly ItemServiceApiFactory _factory;
    public MyReportsApiIntegrationTests(ItemServiceApiFactory factory) => _factory = factory;

    // Verifies one owner sees only their lost/found reports, current statuses, and no private values.
    [Fact]
    public async Task MyReports_OwnerReceivesOwnLostAndFoundReportsWithActiveAndResolvedStatuses_PrivateSafe()
    {
        var ownerId = Guid.NewGuid();
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        const string lostSecret = "my-reports-lost-private";
        const string foundSecret = "my-reports-found-private";
        using var lostForm = TestMultipartHelper.BuildValidForm(lostSecret);
        using var foundForm = TestMultipartHelper.BuildValidFoundForm(foundSecret);
        var lostCreated = await owner.PostAsync("/api/items/lost", lostForm);
        var foundCreated = await owner.PostAsync("/api/items/found", foundForm);
        Assert.Equal(HttpStatusCode.Created, lostCreated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foundCreated.StatusCode);
        var resolve = await owner.PostAsync($"{lostCreated.Headers.Location!.PathAndQuery}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        var lostHistory = await owner.GetAsync("/api/items/lost/mine");
        var foundHistory = await owner.GetAsync("/api/items/found/mine");
        var lostJson = await lostHistory.Content.ReadAsStringAsync();
        var foundJson = await foundHistory.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, lostHistory.StatusCode);
        Assert.Equal(HttpStatusCode.OK, foundHistory.StatusCode);
        Assert.Contains(lostCreated.Headers.Location!.Segments.Last().Trim('/'), lostJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":\"RESOLVED\"", lostJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(foundCreated.Headers.Location!.Segments.Last().Trim('/'), foundJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":\"ACTIVE\"", foundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lostSecret, lostJson, StringComparison.Ordinal);
        Assert.DoesNotContain(foundSecret, foundJson, StringComparison.Ordinal);
    }

    // Verifies a user-ID query string cannot bypass JWT-scoped history access.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task MyReports_OtherUserCannotRetrieveOwnersHistory_EvenWithAUserIdQueryString(string kind)
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        using var other = TestAuthHelper.CreateClientWithValidCookie(_factory, otherId);
        const string ownerSecret = "owner-history-private";
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm(ownerSecret) : TestMultipartHelper.BuildValidFoundForm(ownerSecret);
        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var otherHistory = await other.GetAsync($"/api/items/{kind}/mine?userId={ownerId}");
        var json = await otherHistory.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, otherHistory.StatusCode);
        Assert.DoesNotContain(created.Headers.Location!.Segments.Last().Trim('/'), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ownerSecret, json, StringComparison.Ordinal);
    }

    // Verifies anonymous history requests return 401.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task MyReports_AnonymousRequestReturns401(string kind)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/items/{kind}/mine");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Verifies the owner delete endpoint succeeds and public details become unavailable.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task MyReports_OwnerCanDeleteOwnReportThroughTheBackendEndpoint(string kind)
    {
        var ownerId = Guid.NewGuid();
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        const string hidden = "deleted-history-private";
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm(hidden) : TestMultipartHelper.BuildValidFoundForm(hidden);
        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = created.Headers.Location!.Segments.Last().Trim('/');
        var delete = await owner.DeleteAsync($"/api/items/{kind}/{id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/items/{id}")).StatusCode);
    }
}
