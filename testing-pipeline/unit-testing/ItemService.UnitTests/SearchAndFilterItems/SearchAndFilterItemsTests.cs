using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.SearchAndFilterItems;

public class SearchAndFilterItemsTests
{
    [Fact]
    public async Task BrowseWithoutFilters_RequestsDefaultFirstPageAndReturnsRepositoryPage()
    {
        var context = new ItemControllerTestContext();
        var expected = new PagedResultDto<ItemSummaryDto>
        {
            Items = [new ItemSummaryDto { Id = Guid.NewGuid(), ItemType = "LOST", Status = "ACTIVE" }],
            Page = 1, PageSize = 20, TotalCount = 1, TotalPages = 1
        };
        var ct = new CancellationTokenSource().Token;
        ItemSearchQuery? captured = null;
        context.SearchItems.Setup(x => x.SearchAsync(It.IsAny<ItemSearchQuery>(), ct))
            .Callback<ItemSearchQuery, CancellationToken>((query, token) => captured = query)
            .ReturnsAsync(expected);

        var result = await context.ItemsController(Guid.NewGuid()).Search(null, null, null, null, null, ct: ct);

        Assert.Same(expected, Assert.IsType<PagedResultDto<ItemSummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.NotNull(captured);
        Assert.Equal(1, captured.Page);
        Assert.Equal(20, captured.PageSize);
        Assert.Null(captured.Keyword);
        Assert.Null(captured.Category);
        Assert.Null(captured.ItemType);
        Assert.Null(captured.DateFrom);
        Assert.Null(captured.DateTo);
        context.SearchItems.VerifyAll();
    }

    [Theory]
    [InlineData("wallet", "wallet")]
    [InlineData("  WALLET  ", "WALLET")]
    [InlineData("  library entrance  ", "library entrance")]
    public async Task KeywordSearch_TrimsInputBeforeQueryingRepository(string input, string expected)
    {
        var context = new ItemControllerTestContext();
        ItemSearchQuery? captured = null;
        context.SearchItems.Setup(x => x.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ItemSearchQuery, CancellationToken>((query, token) => captured = query)
            .ReturnsAsync(EmptyPage());

        await context.ItemsController(Guid.NewGuid()).Search(input, null, null, null, null);

        Assert.Equal(expected, captured!.Keyword);
        context.SearchItems.VerifyAll();
    }

    [Fact]
    public async Task CategoryFilter_IsPassedToRepository()
    {
        var context = new ItemControllerTestContext();
        ItemSearchQuery? captured = null;
        context.SearchItems.Setup(x => x.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ItemSearchQuery, CancellationToken>((query, token) => captured = query)
            .ReturnsAsync(EmptyPage());

        await context.ItemsController(Guid.NewGuid()).Search(null, "Keys", null, null, null);

        Assert.Equal("Keys", captured!.Category);
        context.SearchItems.VerifyAll();
    }

    [Fact]
    public async Task DateRangeFilter_UsesInclusiveBoundsAndSupportsEqualDates()
    {
        var context = new ItemControllerTestContext();
        ItemSearchQuery? captured = null;
        context.SearchItems.Setup(x => x.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ItemSearchQuery, CancellationToken>((query, token) => captured = query)
            .ReturnsAsync(EmptyPage());

        await context.ItemsController(Guid.NewGuid()).Search(null, null, null, "2026-09-12", "2026-09-12");

        Assert.Equal(new DateOnly(2026, 9, 12), captured!.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 12), captured.DateTo);
        context.SearchItems.VerifyAll();
    }

    [Fact]
    public async Task CombinedFilters_ReachRepositoryTogetherWithPagination()
    {
        var context = new ItemControllerTestContext();
        ItemSearchQuery? captured = null;
        context.SearchItems.Setup(x => x.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ItemSearchQuery, CancellationToken>((query, token) => captured = query)
            .ReturnsAsync(EmptyPage());

        var result = await context.ItemsController(Guid.NewGuid()).Search(
            "  phone  ", "Electronics", "found", "2026-09-01", "2026-09-30", 2, 12);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(captured);
        Assert.Equal("phone", captured.Keyword);
        Assert.Equal("Electronics", captured.Category);
        Assert.Equal("FOUND", captured.ItemType);
        Assert.Equal(new DateOnly(2026, 9, 1), captured.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 30), captured.DateTo);
        Assert.Equal(2, captured.Page);
        Assert.Equal(12, captured.PageSize);
        context.SearchItems.VerifyAll();
    }

    [Fact]
    public async Task NoMatchingResults_ReturnsEmptyPageRatherThanError()
    {
        var context = new ItemControllerTestContext();
        var empty = EmptyPage();
        context.SearchItems.Setup(x => x.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(empty);

        var result = await context.ItemsController(Guid.NewGuid()).Search("missing", null, null, null, null);

        var page = Assert.IsType<PagedResultDto<ItemSummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
        context.SearchItems.VerifyAll();
    }

    [Theory]
    [InlineData("category", "Unsupported", "Category")]
    [InlineData("type", "RESOLVED", "Type")]
    [InlineData("dateFrom", "09/01/2026", "DateFrom")]
    [InlineData("dateTo", "30-09-2026", "DateTo")]
    public async Task InvalidFilters_AreRejectedBeforeRepositoryCall(string field, string value, string errorKey)
    {
        var context = new ItemControllerTestContext();
        var result = await context.ItemsController(Guid.NewGuid()).Search(
            null,
            field == "category" ? value : null,
            field == "type" ? value : null,
            field == "dateFrom" ? value : null,
            field == "dateTo" ? value : null);

        Assert.Contains(errorKey, ItemControllerTestContext.Problem(result).Errors.Keys);
        context.SearchItems.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, 20, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, 51, "PageSize")]
    public async Task InvalidPagination_IsRejectedBeforeRepositoryCall(int page, int pageSize, string errorKey)
    {
        var context = new ItemControllerTestContext();
        var result = await context.ItemsController(Guid.NewGuid()).Search(null, null, null, null, null, page, pageSize);
        Assert.Contains(errorKey, ItemControllerTestContext.Problem(result).Errors.Keys);
        context.SearchItems.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReversedDateRange_IsRejectedBeforeRepositoryCall()
    {
        var context = new ItemControllerTestContext();
        var result = await context.ItemsController(Guid.NewGuid()).Search(null, null, null, "2026-09-30", "2026-09-01");
        Assert.Contains("DateRange", ItemControllerTestContext.Problem(result).Errors.Keys);
        context.SearchItems.VerifyNoOtherCalls();
    }

    [Fact]
    public void PublicSearchSummary_HasNoHiddenInformationField()
    {
        Assert.DoesNotContain(typeof(ItemSummaryDto).GetProperties(), p =>
            p.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    private static PagedResultDto<ItemSummaryDto> EmptyPage() => new()
    {
        Items = [], Page = 1, PageSize = 20, TotalCount = 0, TotalPages = 0
    };
}
