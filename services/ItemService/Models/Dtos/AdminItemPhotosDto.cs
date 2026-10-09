namespace ItemService.Models.Dtos;

public sealed class AdminItemPhotosDto
{
    public IReadOnlyList<string> PhotoUrls { get; init; } = [];
}
