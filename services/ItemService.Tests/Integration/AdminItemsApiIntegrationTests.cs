using System.Net;
using System.Text.Json;
using MySqlConnector;
using Xunit;

[Collection(ItemServiceIntegrationCollection.Name)]
public sealed class AdminItemsApiIntegrationTests
{
    // Story LF-79 HTTP checks: real routing, the AdminOnly claim gate, persisted soft delete, and the delete-request event.
    private readonly ItemServiceApiFactory _factory;

    public AdminItemsApiIntegrationTests(ItemServiceApiFactory factory) => _factory = factory;

    // Verifies an admin can read either kind of listing and see the owner and status needed to act on it.
    [Theory]
    [InlineData("lost", "lost_items", "LOST")]
    [InlineData("found", "found_items", "FOUND")]
    public async Task Get_ExistingListing_Returns200WithOwnerTypeAndStatus(string kind, string table, string wireType)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync(kind);
        var ownerId = await OwnerIdAsync(table, id);

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"\"type\":\"{wireType}\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ownerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":\"ACTIVE\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"deletedAt\":null", body, StringComparison.OrdinalIgnoreCase);
    }

    // Verifies the route segment tolerates either casing, because parsing ignores case.
    [Theory]
    [InlineData("LOST")]
    [InlineData("Lost")]
    public async Task Get_TypeSegment_IsCaseInsensitive(string segment)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync("lost");

        var response = await admin.GetAsync($"/api/admin/items/{segment}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Confirms the agreed oversight rule end to end: once an admin deletes a listing it stays readable, the
    // deletion timestamp is exposed, and every field matches what the live listing reported. The title is
    // captured from the live response first, so this cannot pass by agreeing with a stale default.
    // Deliberate; see known-gaps.md.
    [Theory]
    [InlineData("lost", "LOST")]
    [InlineData("found", "FOUND")]
    public async Task Get_SoftDeletedListing_Returns200WithCompleteOversightView(string kind, string wireType)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync(kind);

        var liveResponse = await admin.GetAsync($"/api/admin/items/{kind}/{id}");
        using var live = JsonDocument.Parse(await liveResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        var liveTitle = live.RootElement.GetProperty("title").GetString();

        _factory.FakeEvents.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/items/{kind}/{id}")).StatusCode);

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{id}");
        using var after = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, after.RootElement.GetProperty("id").GetGuid().ToString(), ignoreCase: true);
        Assert.Equal(wireType, after.RootElement.GetProperty("type").GetString());
        Assert.Equal(liveTitle, after.RootElement.GetProperty("title").GetString());
        Assert.Equal("ACTIVE", after.RootElement.GetProperty("status").GetString());

        var deletedAt = after.RootElement.GetProperty("deletedAt").GetDateTime();
        Assert.True(deletedAt > DateTime.UtcNow.AddMinutes(-10), $"expected a recent deletion timestamp but got {deletedAt:O}");
    }

    // Verifies the oversight view never widens what an admin can see: hidden_information stays excluded
    // once the listing is soft-deleted and otherwise fully readable. The marker is read back from the
    // database first so this cannot pass vacuously through a failed write.
    [Theory]
    [InlineData("lost", "lost_items")]
    [InlineData("found", "found_items")]
    public async Task Get_SoftDeletedListing_DoesNotExposeHiddenInformation(string kind, string table)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var hidden = $"lf79-hidden-marker-{Guid.NewGuid():N}";
        var id = await CreateAsync(kind, hidden);

        Assert.Equal(hidden, await HiddenInformationAsync(table, id));

        _factory.FakeEvents.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/items/{kind}/{id}")).StatusCode);

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(hidden, body, StringComparison.Ordinal);
    }

    // Verifies an unknown listing reports the shared error message the frontend can rely on.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Get_UnknownListing_Returns404WithItemNotFoundError(string kind)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Item not found.", body, StringComparison.Ordinal);
    }

    // Verifies an unsupported segment never reaches the controller, so no rows are disclosed.
    [Theory]
    [InlineData("stolen")]
    [InlineData("lost_item")]
    [InlineData("LostItem")]
    public async Task Get_UnsupportedTypeSegment_Returns404FromRouting(string segment)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync("lost");

        var response = await admin.GetAsync($"/api/admin/items/{segment}/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    // Verifies a malformed identifier is rejected by routing rather than reaching the controller.
    [Fact]
    public async Task Get_MalformedId_Returns404FromRouting()
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());

        var response = await admin.GetAsync("/api/admin/items/lost/not-a-guid");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    // Verifies a verified non-admin is forbidden and the body carries no listing details.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Get_NonAdminToken_Returns403WithEmptyBody(string kind)
    {
        using var nonAdmin = AdminTestTokenHelper.CreateNonAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync(kind);

        var response = await nonAdmin.GetAsync($"/api/admin/items/{kind}/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    // Verifies an unauthenticated read is challenged before any listing lookup.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Get_AnonymousRequest_Returns401WithEmptyBody(string kind)
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/admin/items/{kind}/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    // Verifies an admin delete persists the soft delete and announces it exactly once.
    [Theory]
    [InlineData("lost", "lost_items", "LOST")]
    [InlineData("found", "found_items", "FOUND")]
    public async Task Delete_ActiveListing_SoftDeletesPersistsAndPublishesDeleteRequest(string kind, string table, string wireType)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync(kind);
        var ownerId = await OwnerIdAsync(table, id);
        _factory.FakeEvents.Clear();

        var response = await admin.DeleteAsync($"/api/admin/items/{kind}/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Item deleted.", body, StringComparison.OrdinalIgnoreCase);

        var evt = Assert.Single(_factory.FakeEvents.Published);
        Assert.Equal("items.item.delete_requested", evt.Topic);
        Assert.Contains(id, evt.JsonPayload, StringComparison.Ordinal);
        Assert.Contains(ownerId.ToString(), evt.JsonPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"ItemType\":\"{wireType}\"", evt.JsonPayload, StringComparison.OrdinalIgnoreCase);

        await AssertSoftDeletedAsync(table, id);
    }

    // Verifies a matched listing stays protected from deletion and remains publicly visible.
    [Fact]
    public async Task Delete_MatchedListing_Returns409NamingStatusAndLeavesRowActive()
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync("found");
        await SetStatusAsync("found_items", id, "MATCHED");
        _factory.FakeEvents.Clear();

        var response = await admin.DeleteAsync($"/api/admin/items/found/{id}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("This listing is MATCHED.", body, StringComparison.Ordinal);
        Assert.Empty(_factory.FakeEvents.Published);
        await AssertNotDeletedAsync("found_items", id);
    }

    // Verifies deleting an already-deleted listing is a no-op 404 and does not re-announce the deletion.
    // The 404 is a deliberate decision: reading a deleted listing (oversight) and deleting it (gone) differ
    // on purpose, unlike GET, which exposes soft-deleted rows. Do not "fix" this without reading known-gaps.md.
    [Fact]
    public async Task Delete_AlreadyDeletedListing_Returns404AndDoesNotRepublish()
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync("lost");
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/items/lost/{id}")).StatusCode);
        _factory.FakeEvents.Clear();

        var response = await admin.DeleteAsync($"/api/admin/items/lost/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.FakeEvents.Published);
    }

    // Verifies a verified non-admin cannot delete, and the listing is untouched.
    [Theory]
    [InlineData("lost", "lost_items")]
    [InlineData("found", "found_items")]
    public async Task Delete_NonAdminToken_Returns403AndLeavesListingUndeleted(string kind, string table)
    {
        using var nonAdmin = AdminTestTokenHelper.CreateNonAdminClient(_factory, Guid.NewGuid());
        var id = await CreateAsync(kind);
        _factory.FakeEvents.Clear();

        var response = await nonAdmin.DeleteAsync($"/api/admin/items/{kind}/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.FakeEvents.Published);
        await AssertNotDeletedAsync(table, id);
    }

    // Verifies an unauthenticated delete is challenged and the listing is untouched.
    [Theory]
    [InlineData("lost", "lost_items")]
    [InlineData("found", "found_items")]
    public async Task Delete_AnonymousRequest_Returns401AndLeavesListingUndeleted(string kind, string table)
    {
        using var anonymous = _factory.CreateClient();
        var id = await CreateAsync(kind);
        _factory.FakeEvents.Clear();

        var response = await anonymous.DeleteAsync($"/api/admin/items/{kind}/{id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.FakeEvents.Published);
        await AssertNotDeletedAsync(table, id);
    }

    private async Task<string> CreateAsync(string kind, string? hidden = null)
    {
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());
        var marker = hidden ?? $"lf79-hidden-{Guid.NewGuid():N}";

        using var form = kind == "lost"
            ? TestMultipartHelper.BuildValidFormExcept(null, marker)
            : TestMultipartHelper.BuildValidFoundFormExcept(null, marker);

        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return created.Headers.Location!.Segments.Last().Trim('/');
    }

    private async Task<Guid> OwnerIdAsync(string table, string id)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"SELECT user_id FROM {table} WHERE id = @id", db);
        command.Parameters.AddWithValue("@id", id);
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return Guid.Parse(Convert.ToString(value)!);
    }

    private async Task<string?> HiddenInformationAsync(string table, string id)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"SELECT hidden_information FROM {table} WHERE id = @id", db);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToString(await command.ExecuteScalarAsync());
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

    private async Task AssertSoftDeletedAsync(string table, string id)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"SELECT deleted_at FROM {table} WHERE id = @id", db);
        command.Parameters.AddWithValue("@id", id);
        var deletedAt = await command.ExecuteScalarAsync();
        Assert.False(deletedAt is null or DBNull);
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
}