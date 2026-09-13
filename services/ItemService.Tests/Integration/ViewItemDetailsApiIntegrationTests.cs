using System.Net;
using System.Net.Http.Headers;
using MySqlConnector;
using Xunit;

[Collection(ItemServiceIntegrationCollection.Name)]
public sealed class ViewItemDetailsApiIntegrationTests
{
    // Story 6 API/MySQL checks: public details, privacy, 404 rules, and reporter access to resolved reports.
    private readonly ItemServiceApiFactory _factory;
    public ViewItemDetailsApiIntegrationTests(ItemServiceApiFactory factory) => _factory = factory;

    // Verifies active lost and found reports return complete public details without private data.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Details_ActiveItem_ReturnsPublicDetailsForEachTypeWithoutPrivateData(string kind)
    {
        var ownerId = Guid.NewGuid();
        const string secret = "details-http-private-marker";
        using var client = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm(secret) : TestMultipartHelper.BuildValidFoundForm(secret);
        using var photo = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]);
        photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(photo, "Photos", "details.jpg");
        var created = await client.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await client.GetAsync($"/api/items/{created.Headers.Location!.Segments.Last().Trim('/')}");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"type\":\"" + kind.ToUpperInvariant() + "\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(kind == "lost" ? "\"title\":\"Test Title\"" : "\"title\":\"Found wallet\"", json, StringComparison.Ordinal);
        Assert.Contains(kind == "lost" ? "\"description\":\"Test Description\"" : "\"description\":\"Found near the library entrance\"", json, StringComparison.Ordinal);
        Assert.Contains(kind == "lost" ? "\"category\":\"Electronics\"" : "\"category\":\"Accessories\"", json, StringComparison.Ordinal);
        Assert.Contains(kind == "lost" ? "\"location\":\"Test Location\"" : "\"location\":\"Main library entrance\"", json, StringComparison.Ordinal);
        Assert.Contains(kind == "lost" ? "\"date\":\"2026-08-28\"" : $"\"date\":\"{DateTime.UtcNow:yyyy-MM-dd}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"ACTIVE\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"photoUrls\":[\"/photos/", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"photo\":{", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("hiddenInformation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
    }

    // Verifies malformed and unknown IDs return 404.
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("not-a-guid")]
    public async Task Details_InvalidOrUnknownId_Returns404(string id)
    {
        using var client = TestAuthHelper.CreateClientWithValidCookie(_factory);
        var response = await client.GetAsync($"/api/items/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Verifies resolved reports are hidden from other users but remain available to their reporter.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Details_ResolvedItem_Is404ForOtherUserButAvailableToOriginalReporter(string kind)
    {
        var ownerId = Guid.NewGuid();
        using var owner = TestAuthHelper.CreateClientWithValidCookie(_factory, ownerId);
        using var other = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm() : TestMultipartHelper.BuildValidFoundForm();
        var created = await owner.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = created.Headers.Location!.Segments.Last().Trim('/');
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/items/{kind}/{id}/resolve", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/items/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/items/{id}")).StatusCode);
    }

    // Verifies soft-deleted rows cannot be retrieved through the public endpoint.
    [Theory]
    [InlineData("lost", "lost_items")]
    [InlineData("found", "found_items")]
    public async Task Details_SoftDeletedItem_Returns404ThroughThePublicEndpoint(string kind, string table)
    {
        using var client = TestAuthHelper.CreateClientWithValidCookie(_factory, Guid.NewGuid());
        using var form = kind == "lost" ? TestMultipartHelper.BuildValidForm() : TestMultipartHelper.BuildValidFoundForm();
        var created = await client.PostAsync($"/api/items/{kind}", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = created.Headers.Location!.Segments.Last().Trim('/');

        await using (var db = new MySqlConnection(_factory.GetTestDatabaseConnectionString()))
        {
            await db.OpenAsync();
            await using var command = new MySqlCommand($"UPDATE {table} SET deleted_at = UTC_TIMESTAMP(3) WHERE id = @id", db);
            command.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/items/{id}")).StatusCode);
    }

    // Verifies the endpoint requires authentication.
    [Fact]
    public async Task Details_AnonymousRequest_Returns401()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/items/{Guid.NewGuid()}")).StatusCode);
    }
}
