using System.Net;
using Xunit;

public class LostItemsApiIntegrationTests
    : IClassFixture<ItemServiceApiFactory>
{
    private readonly ItemServiceApiFactory _factory;

    public LostItemsApiIntegrationTests(
        ItemServiceApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AuthedClient()
    {
        return TestAuthHelper.CreateClientWithValidCookie(
            _factory);
    }

    [Fact]
    public async Task Post_ValidForm_ReturnsCreatedResponseWithoutHiddenInformation()
    {
        using var client = AuthedClient();
        using var content = TestMultipartHelper.BuildValidForm("api-hidden-value");

        var response = await client.PostAsync("/api/items/lost", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("api-hidden-value", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":\"ACTIVE\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Post_ValidForm_PublishesLostItemEventWithHiddenInformation()
    {
        const string hidden = "event-only-hidden-value";
        _factory.FakeEvents.Clear();
        using var client = AuthedClient();
        using var content = TestMultipartHelper.BuildValidForm(hidden);

        var response = await client.PostAsync("/api/items/lost", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var published = Assert.Single(_factory.FakeEvents.Published);
        Assert.EndsWith("lost_item.created", published.Topic);
        Assert.Contains(hidden, published.JsonPayload, StringComparison.Ordinal);
        _factory.FakeEvents.Clear();
    }

    [Fact]
    public async Task Get_NonexistentItem_ReturnsNotFound()
    {
        using var client = AuthedClient();

        var response = await client.GetAsync($"/api/items/lost/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_TamperedJwt_Returns401()
    {
        using var client = TestAuthHelper.CreateClientWithTamperedCookie(_factory);
        using var content = TestMultipartHelper.BuildValidForm();

        var response = await client.PostAsync("/api/items/lost", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Title", 151)]
    [InlineData("Description", 2001)]
    [InlineData("HiddenInformation", 501)]
    public async Task Post_FieldOverMaxLength_Returns400(string field, int length)
    {
        using var client = AuthedClient();
        using var content = TestMultipartHelper.BuildValidFormWithOverride(field, new string('x', length));

        var response = await client.PostAsync("/api/items/lost", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_FutureDate_Returns400()
    {
        using var client = AuthedClient();
        using var content = TestMultipartHelper.BuildValidFormWithOverride(
            "DateLost", DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd"));

        var response = await client.PostAsync("/api/items/lost", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // API-09 through API-13
    [Theory]
    [InlineData("Title")]
    [InlineData("Category")]
    [InlineData("Description")]
    [InlineData("LastKnownLocation")]
    [InlineData("HiddenInformation")]
    public async Task Post_MissingRequiredField_Returns400(
        string fieldToOmit)
    {
        using var client = AuthedClient();

        using var content =
            TestMultipartHelper.BuildValidFormExcept(fieldToOmit);

        var response = await client.PostAsync(
            "/api/items/lost",
            content);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var body =
            await response.Content.ReadAsStringAsync();

        Assert.Contains(
            fieldToOmit,
            body,
            StringComparison.OrdinalIgnoreCase);
    }

    // API-23
    [Fact]
    public async Task Post_NoAuthCookie_Returns401()
    {
        using var client = _factory.CreateClient();

        using var content =
            TestMultipartHelper.BuildValidForm();

        var response = await client.PostAsync(
            "/api/items/lost",
            content);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);
    }

    // API-24
    [Fact]
    public async Task Post_ExpiredJwtCookie_Returns401()
    {
        using var client =
            TestAuthHelper.CreateClientWithExpiredCookie(
                _factory);

        using var content =
            TestMultipartHelper.BuildValidForm();

        var response = await client.PostAsync(
            "/api/items/lost",
            content);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);
    }

    // API-20
    [Fact]
    public async Task Post_Then_Get_NeverReturnsHiddenInformationField()
    {
        using var client = AuthedClient();

        const string secret =
            "unique-secret-marker-xyz";

        using var content =
            TestMultipartHelper.BuildValidForm(
                hiddenInfo: secret);

        var postResponse = await client.PostAsync(
            "/api/items/lost",
            content);

        Assert.Equal(
            HttpStatusCode.Created,
            postResponse.StatusCode);

        var postBody =
            await postResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(
            secret,
            postBody,
            StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(postResponse.Headers.Location);

        var getResponse = await client.GetAsync(
            postResponse.Headers.Location);

        var getBody =
            await getResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(
            secret,
            getBody,
            StringComparison.OrdinalIgnoreCase);
    }
}