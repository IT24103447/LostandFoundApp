using System.Net;
using MySqlConnector;
using Xunit;

[Collection(ItemServiceIntegrationCollection.Name)]
public sealed class DeleteItemReportApiIntegrationTests
{
    // Story 7 API/MySQL checks: real endpoint behavior, persisted soft delete, and fake-event contract.
    private readonly ItemServiceApiFactory _factory;

    public DeleteItemReportApiIntegrationTests(ItemServiceApiFactory factory) => _factory = factory;

    // Verifies owner deletion is auditable internally but absent from public details, browse, and history.
    [Theory]
    [InlineData("lost", "lost_items", "LOST")]
    [InlineData("found", "found_items", "FOUND")]
    public async Task Delete_Owner_SoftDeletesHidesFromDetailsBrowseAndHistory_AndPublishesDeleteRequest(
        string kind, string table, string expectedType)
    {
        var ownerId = Guid.NewGuid();
        var hidden = $"delete-internal-secret-{Guid.NewGuid():N}";
        var title = $"Delete disposable {Guid.NewGuid():N}";
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        var id = await CreateAsync(owner, kind, title, hidden);
        _factory.FakeEvents.Clear();

        var deleted = await owner.DeleteAsync($"/api/items/{kind}/{id}");
        var body = await deleted.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Contains("deleted", body, StringComparison.OrdinalIgnoreCase);
        var evt = Assert.Single(_factory.FakeEvents.Published);
        Assert.Equal("items.item.delete_requested", evt.Topic);
        Assert.Contains("item.delete_requested", evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains(id, evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains($"\"itemType\":\"{expectedType}\"", evt.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hidden, evt.JsonPayload, StringComparison.Ordinal);

        await AssertDeletedButAuditableAsync(table, id, hidden);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/items/{id}")).StatusCode);

        var browse = await owner.GetAsync($"/api/items?q={Uri.EscapeDataString(title)}&type={expectedType}&page=1&pageSize=50");
        var browseJson = await browse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, browse.StatusCode);
        Assert.DoesNotContain(id, browseJson, StringComparison.OrdinalIgnoreCase);

        var mine = await owner.GetAsync($"/api/items/{kind}/mine");
        var historyJson = await mine.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.DoesNotContain(id, historyJson, StringComparison.OrdinalIgnoreCase);
    }

    // Verifies a non-owner cannot delete another user's report.
    [Theory]
    [InlineData("lost", "lost_items")]
    [InlineData("found", "found_items")]
    public async Task Delete_NonOwner_Returns403_LeavesReportPublicAndDoesNotPublish(string kind, string table)
    {
        var ownerId = Guid.NewGuid();
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        using var nonOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());
        var id = await CreateAsync(owner, kind, $"Keep after forbidden {Guid.NewGuid():N}", "private-remains-internal");
        _factory.FakeEvents.Clear();

        var response = await nonOwner.DeleteAsync($"/api/items/{kind}/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.FakeEvents.Published);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/items/{id}")).StatusCode);
        await AssertNotDeletedAsync(table, id);
    }

    // Verifies matched reports are protected and return resolve guidance.
    [Theory]
    [InlineData("lost", "lost_items")]
    [InlineData("found", "found_items")]
    public async Task Delete_MatchedItem_Returns409WithResolveGuidance_WithoutDeletionOrEvent(string kind, string table)
    {
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());
        var id = await CreateAsync(owner, kind, $"Matched cannot delete {Guid.NewGuid():N}", "matched-private-marker");
        await SetStatusAsync(table, id, "MATCHED");
        _factory.FakeEvents.Clear();

        var response = await owner.DeleteAsync($"/api/items/{kind}/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("confirmed match", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolve", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_factory.FakeEvents.Published);
        await AssertNotDeletedAsync(table, id);
    }

    // Verifies unknown or previously deleted reports return 404 without an event.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_UnknownOrAlreadyDeletedItem_Returns404AndDoesNotPublish(string kind)
    {
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());
        _factory.FakeEvents.Clear();

        var response = await owner.DeleteAsync($"/api/items/{kind}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.FakeEvents.Published);
    }

    // Verifies anonymous delete requests are rejected with 401.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_AnonymousRequest_Returns401(string kind)
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/items/{kind}/{Guid.NewGuid()}")).StatusCode);
    }

    private async Task<string> CreateAsync(HttpClient client, string kind, string title, string hidden)
    {
        using var form = kind == "lost" ? BuildLostForm(title, hidden) : BuildFoundForm(title, hidden);
        var created = await client.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return created.Headers.Location!.Segments.Last().Trim('/');
    }

    private static MultipartFormDataContent BuildLostForm(string title, string hidden)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "Title");
        form.Add(new StringContent("Electronics"), "Category");
        form.Add(new StringContent("Disposable report for delete integration testing"), "Description");
        form.Add(new StringContent("2026-08-28"), "DateLost");
        form.Add(new StringContent("Test location"), "LastKnownLocation");
        form.Add(new StringContent(hidden), "HiddenInformation");
        return form;
    }

    private static MultipartFormDataContent BuildFoundForm(string title, string hidden)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "Title");
        form.Add(new StringContent("Accessories"), "Category");
        form.Add(new StringContent("Disposable report for delete integration testing"), "Description");
        form.Add(new StringContent(DateTime.UtcNow.ToString("yyyy-MM-dd")), "DateFound");
        form.Add(new StringContent("Test location"), "LocationFound");
        form.Add(new StringContent(hidden), "HiddenInformation");
        return form;
    }

    private async Task AssertDeletedButAuditableAsync(string table, string id, string expectedHidden)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"SELECT deleted_at, hidden_information FROM {table} WHERE id = @id", db);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(expectedHidden, reader.GetString(1));
    }

    private async Task AssertNotDeletedAsync(string table, string id)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"SELECT deleted_at FROM {table} WHERE id = @id", db);
        command.Parameters.AddWithValue("@id", id);
        var deletedAt = await command.ExecuteScalarAsync();
        Assert.True(deletedAt is null or DBNull);
    }

    private async Task SetStatusAsync(string table, string id, string status)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"UPDATE {table} SET status = @status WHERE id = @id", db);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@status", status);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
