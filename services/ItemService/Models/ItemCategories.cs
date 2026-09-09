namespace ItemService.Models;

// Single source of truth for the categories a lost/found item can belong to.
// Must stay in sync with LOST_ITEM_CATEGORIES in the frontend's reportLostItemSchema.ts
// (found items reuse the same list — see FOUND_ITEM_CATEGORIES there).
public static class ItemCategories
{
    public static readonly string[] All =
    [
        "Electronics",
        "Bags",
        "Clothing",
        "Accessories",
        "Documents",
        "Keys",
        "Other"
    ];

    public static bool IsValid(string category) =>
        All.Contains(category, StringComparer.Ordinal);
}