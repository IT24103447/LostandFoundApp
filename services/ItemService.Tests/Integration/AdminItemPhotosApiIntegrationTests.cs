using System.Net;
using System.Text.Json;
using MySqlConnector;
using Xunit;

[Collection(ItemServiceIntegrationCollection.Name)]
public sealed class AdminItemPhotosApiIntegrationTests
{
    // Story LF-267: the new admin photos endpoint GET /api/admin/items/{lost|found}/{id}/photos.
    // Through the real Program.cs host + real MySQL: URLs oldest first, an empty list when the
    // listing has no photos, 404 for an unknown listing, photos still served for resolved and
    // soft-deleted listings (GetByIdAsync reads any live row), 403 for non-admins, 401 anonymous,
    // and the existing admin read endpoint's response left unchanged.
    private readonly ItemServiceApiFactory _factory;

    public AdminItemPhotosApiIntegrationTests(ItemServiceApiFactory factory) => _factory = factory;

    [Theory] // LF-267 DoD: photo URLs come back oldest first, in the exact `{ photoUrls }` shape.
    [InlineData("lost", "lost_item_photos")]
    [InlineData("found", "found_item_photos")]
    public async Task Photos_ReturnsUrls_OldestFirst(string kind, string photoTable)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var itemId = await CreateItemAsync(kind);

        // Insert newer before older to prove the endpoint orders, not the seed order.
        await SeedPhotoAsync(photoTable, itemId, "/photos/later.jpg", DateTime.UtcNow);
        await SeedPhotoAsync(photoTable, itemId, "/photos/earlier.jpg", DateTime.UtcNow.AddHours(-2));

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{itemId}/photos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var names = body.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["photoUrls"], names); // exact wire shape - nothing extra

        var urls = body.RootElement.GetProperty("photoUrls")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["/photos/earlier.jpg", "/photos/later.jpg"], urls);
    }

    [Theory] // LF-267 DoD: a listing with no photos yields an empty photo list, not an error.
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Photos_NoPhotos_ReturnsEmptyArray(string kind)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var itemId = await CreateItemAsync(kind);

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{itemId}/photos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("photoUrls").EnumerateArray());
    }

    [Theory] // LF-267 DoD: an unknown listing is a 404 with the shared error message.
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Photos_UnknownListing_Returns404_ItemNotFound(string kind)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{Guid.NewGuid()}/photos");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Item not found.", body, StringComparison.Ordinal);
    }

    [Theory] // LF-267 DoD: photos show for resolved listings (state is read live, never filtered).
    [InlineData("lost", "lost_item_photos", "lost_items")]
    [InlineData("found", "found_item_photos", "found_items")]
    public async Task Photos_ResolvedListing_StillServed(string kind, string photoTable, string itemTable)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var itemId = await CreateItemAsync(kind);
        await SeedPhotoAsync(photoTable, itemId, "/photos/resolved.jpg", DateTime.UtcNow);
        await SetStatusAsync(itemTable, itemId, "RESOLVED");

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{itemId}/photos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/photos/resolved.jpg", body.RootElement.GetProperty("photoUrls")[0].GetString());
    }

    [Theory] // LF-267 DoD: photos show for soft-deleted listings too - oversight needs them.
    [InlineData("lost", "lost_item_photos")]
    [InlineData("found", "found_item_photos")]
    public async Task Photos_SoftDeletedListing_StillServed(string kind, string photoTable)
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var itemId = await CreateItemAsync(kind);
        await SeedPhotoAsync(photoTable, itemId, "/photos/deleted.jpg", DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/items/{kind}/{itemId}")).StatusCode);

        var response = await admin.GetAsync($"/api/admin/items/{kind}/{itemId}/photos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/photos/deleted.jpg", body.RootElement.GetProperty("photoUrls")[0].GetString());
    }

    [Theory] // LF-267 DoD: non-admins get 403 on the photos endpoint.
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Photos_NonAdminToken_Returns403_WithEmptyBody(string kind)
    {
        using var nonAdmin = AdminTestTokenHelper.CreateNonAdminClient(_factory, Guid.NewGuid());
        var itemId = await CreateItemAsync(kind);

        var response = await nonAdmin.GetAsync($"/api/admin/items/{kind}/{itemId}/photos");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    [Theory] // LF-267 DoD: anonymous callers are challenged at the auth boundary.
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Photos_AnonymousRequest_Returns401_WithEmptyBody(string kind)
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/admin/items/{kind}/{Guid.NewGuid()}/photos");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    [Fact] // LF-267 DoD: the route only admits lost|found - an unknown type never reaches the API.
    public async Task Photos_UnsupportedType_Returns404FromRouting()
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());

        var response = await admin.GetAsync($"/api/admin/items/stolen/{Guid.NewGuid()}/photos");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(body), $"expected an empty body but got '{body}'");
    }

    [Fact] // LF-267 DoD: the existing admin read endpoint and its response are unchanged - the
    // photos endpoint is additive and the read schema still exposes exactly its original fields.
    public async Task Read_ResponseSchema_IsUnchanged()
    {
        using var admin = AdminTestTokenHelper.CreateAdminClient(_factory, Guid.NewGuid());
        var itemId = await CreateItemAsync("lost");

        var response = await admin.GetAsync($"/api/admin/items/lost/{itemId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var names = body.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            ["createdAt", "deletedAt", "description", "id", "status", "title", "type", "userId"],
            names);
        Assert.DoesNotContain("photoUrls", names);
    }

    // ---- helpers ----

    private async Task<string> CreateItemAsync(string kind)
    {
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());

        using var form = kind == "lost"
            ? TestMultipartHelper.BuildValidForm()
            : TestMultipartHelper.BuildValidFoundForm();

        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return created.Headers.Location!.Segments.Last().Trim('/');
    }

    private async Task SeedPhotoAsync(string photoTable, string itemId, string url, DateTime createdAt)
    {
        var itemColumn = photoTable == "lost_item_photos" ? "lost_item_id" : "found_item_id";

        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();

        await using var command = new MySqlCommand(
            $"INSERT INTO {photoTable} (id, {itemColumn}, url, created_at) VALUES (@id, @itemId, @url, @createdAt);",
            db);
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@itemId", Guid.Parse(itemId).ToString());
        command.Parameters.AddWithValue("@url", url);
        command.Parameters.AddWithValue("@createdAt", createdAt);

        await command.ExecuteNonQueryAsync();
    }

    private async Task SetStatusAsync(string itemTable, string itemId, string status)
    {
        await using var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await db.OpenAsync();
        await using var command = new MySqlCommand($"UPDATE {itemTable} SET status = @status WHERE id = @id", db);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@id", Guid.Parse(itemId).ToString());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}