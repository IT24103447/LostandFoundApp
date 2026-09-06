namespace ItemService.Configuration;

public class BlobStorageSettings
{
    public string ConnectionString { get; set; } = string.Empty;

    public string ContainerName { get; set; } = "lost-item-photos";
}
