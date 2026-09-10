namespace ItemService.Configuration;

public class ItemSettings
{
    public string PhotoStoragePath { get; set; } = "wwwroot/photos";

    public string PhotoPublicPath { get; set; } = "/photos";

    public int MaxPhotosPerItem { get; set; } = 1;

    public long MaxPhotoSizeBytes { get; set; } = 5 * 1024 * 1024; // 5 MB

    public string[] AllowedPhotoContentTypes { get; set; } =
        ["image/jpeg", "image/png", "image/webp"];
}
