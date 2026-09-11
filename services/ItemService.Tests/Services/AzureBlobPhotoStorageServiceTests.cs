using ItemService.Configuration;
using ItemService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

public class AzureBlobPhotoStorageServiceTests
{
    [Fact]
    public void BlobStorageSettings_DefaultContainerName_IsStable()
    {
        var settings = new BlobStorageSettings();

        Assert.Equal("lost-item-photos", settings.ContainerName);
        Assert.Equal(string.Empty, settings.ConnectionString);
    }

    [Fact]
    public void Constructor_WithoutConnectionString_FailsFastWithConfigurationError()
    {
        var settings = Options.Create(new BlobStorageSettings
        {
            ConnectionString = "",
            ContainerName = "qa-images"
        });
        var logger = new Mock<ILogger<AzureBlobPhotoStorageService>>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AzureBlobPhotoStorageService(settings, logger.Object));

        Assert.Contains("BlobStorage:ConnectionString", exception.Message, StringComparison.Ordinal);
    }
}
