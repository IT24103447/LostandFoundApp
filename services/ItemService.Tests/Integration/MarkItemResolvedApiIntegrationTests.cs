using System.Net;
using Xunit;

public sealed class MarkItemResolvedApiIntegrationTests : IClassFixture<ItemServiceApiFactory>
{
    private readonly ItemServiceApiFactory _factory;

    public MarkItemResolvedApiIntegrationTests(ItemServiceApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ResolveLost_AsOwner_PersistsResolvedStatusReturnsPrivateSafeResponseAndPublishesEvent()
    {
        const string hidden = "resolve-lost-private-marker";
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory);
        using var form = TestMultipartHelper.BuildValidForm(hidden);
        var created = await owner.PostAsync("/api/items/lost", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _factory.FakeEvents.Clear();

        var resolved = await owner.PostAsync($"{created.Headers.Location!.PathAndQuery}/resolve", null);
        var body = await resolved.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Contains("\"status\":\"RESOLVED\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
        var evt = Assert.Single(_factory.FakeEvents.Published);
        Assert.EndsWith("lost_item.resolved", evt.Topic, StringComparison.Ordinal);
        Assert.Contains("lost_item.resolved", evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains(hidden, evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"RESOLVED\"", evt.JsonPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveFound_AsOwner_PersistsResolvedStatusReturnsPrivateSafeResponseAndPublishesEvent()
    {
        const string hidden = "resolve-found-private-marker";
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory);
        using var form = TestMultipartHelper.BuildValidFoundForm(hidden);
        var created = await owner.PostAsync("/api/items/found", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _factory.FakeEvents.Clear();

        var resolved = await owner.PostAsync($"{created.Headers.Location!.PathAndQuery}/resolve", null);
        var body = await resolved.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Contains("\"status\":\"RESOLVED\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
        var evt = Assert.Single(_factory.FakeEvents.Published);
        Assert.EndsWith("found_item.resolved", evt.Topic, StringComparison.Ordinal);
        Assert.Contains("found_item.resolved", evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains(hidden, evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"RESOLVED\"", evt.JsonPayload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Resolve_NonOwner_Returns403LeavesItemActiveAndPublishesNoResolutionEvent(string kind)
    {
        var ownerId = Guid.NewGuid();
        var nonOwnerId = Guid.NewGuid();
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        using var nonOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, nonOwnerId);
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm() : TestMultipartHelper.BuildValidFoundForm();
        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _factory.FakeEvents.Clear();

        var attempt = await nonOwner.PostAsync($"{created.Headers.Location!.PathAndQuery}/resolve", null);
        var reload = await owner.GetAsync(created.Headers.Location);
        var reloadBody = await reload.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reload.StatusCode);
        Assert.Contains("\"status\":\"ACTIVE\"", reloadBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_factory.FakeEvents.Published);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Resolve_MissingItem_Returns404AndDoesNotPublish(string kind)
    {
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory);
        _factory.FakeEvents.Clear();
        var response = await owner.PostAsync($"/api/items/{kind}/{Guid.NewGuid()}/resolve", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.FakeEvents.Published);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Resolve_AlreadyResolved_Returns409AndDoesNotPublishASecondEvent(string kind)
    {
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory);
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm() : TestMultipartHelper.BuildValidFoundForm();
        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _factory.FakeEvents.Clear();
        var first = await owner.PostAsync($"{created.Headers.Location!.PathAndQuery}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Single(_factory.FakeEvents.Published);

        var duplicate = await owner.PostAsync($"{created.Headers.Location!.PathAndQuery}/resolve", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(_factory.FakeEvents.Published);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Resolve_NoAuthentication_Returns401(string kind)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync($"/api/items/{kind}/{Guid.NewGuid()}/resolve", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
