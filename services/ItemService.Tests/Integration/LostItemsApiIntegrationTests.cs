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