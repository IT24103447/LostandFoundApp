using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Repositories;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.Tests.Controllers;

public class SearchAndFilterItemsStoryTests
{
    [Fact]
    public async Task Search_WithoutFilters_UsesFirstPageAndDefaultPageSize()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search(null, null, null, null, null, ct: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(captured.Value);
        Assert.Equal(1, captured.Value!.Page);
        Assert.Equal(20, captured.Value.PageSize);
        Assert.Null(captured.Value.Keyword);
    }

    [Fact]
    public async Task Search_ValidCombinedFilters_NormalizesAndForwardsEveryFilter()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search("  WALLET  ", "Accessories", "found", "2026-09-01", "2026-09-12", 2, 12, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var query = Assert.IsType<ItemSearchQuery>(captured.Value);
        Assert.Equal("WALLET", query.Keyword);
        Assert.Equal("Accessories", query.Category);
        Assert.Equal("FOUND", query.ItemType);
        Assert.Equal(new DateOnly(2026, 9, 1), query.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 12), query.DateTo);
        Assert.Equal(2, query.Page);
        Assert.Equal(12, query.PageSize);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("  invalid  ")]
    public async Task Search_InvalidCategory_ReturnsValidationProblemWithoutQueryingRepository(string category)
    {
        var repo = new Mock<IItemsSearchRepository>();

        var result = await new ItemsController(repo.Object).Search(null, category, null, null, null, ct: CancellationToken.None);

        AssertValidationError(result, "Category");
        repo.Verify(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("ARCHIVED")]
    [InlineData("lost-and-found")]
    public async Task Search_InvalidType_ReturnsValidationProblemWithoutQueryingRepository(string type)
    {
        var repo = new Mock<IItemsSearchRepository>();

        var result = await new ItemsController(repo.Object).Search(null, null, type, null, null, ct: CancellationToken.None);

        AssertValidationError(result, "Type");
        repo.Verify(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("2026/09/01", null, "DateFrom")]
    [InlineData(null, "12-09-2026", "DateTo")]
    public async Task Search_MalformedDate_ReturnsValidationProblemWithoutQueryingRepository(string? dateFrom, string? dateTo, string errorKey)
    {
        var repo = new Mock<IItemsSearchRepository>();

        var result = await new ItemsController(repo.Object).Search(null, null, null, dateFrom, dateTo, ct: CancellationToken.None);

        AssertValidationError(result, errorKey);
        repo.Verify(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_DateFromAfterDateTo_ReturnsValidationProblemWithoutQueryingRepository()
    {
        var repo = new Mock<IItemsSearchRepository>();

        var result = await new ItemsController(repo.Object).Search(null, null, null, "2026-09-12", "2026-09-01", ct: CancellationToken.None);

        AssertValidationError(result, "DateRange");
        repo.Verify(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 20, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, 51, "PageSize")]
    public async Task Search_InvalidPagination_ReturnsValidationProblemWithoutQueryingRepository(int page, int pageSize, string errorKey)
    {
        var repo = new Mock<IItemsSearchRepository>();

        var result = await new ItemsController(repo.Object).Search(null, null, null, null, null, page, pageSize, CancellationToken.None);

        AssertValidationError(result, errorKey);
        repo.Verify(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_RepositoryReturnsNoMatches_ReturnsSuccessfulEmptyPage()
    {
        var repo = new Mock<IItemsSearchRepository>();
        repo.Setup(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResultDto<ItemSummaryDto> { Items = [], Page = 1, PageSize = 20, TotalCount = 0, TotalPages = 0 });

        var result = await new ItemsController(repo.Object).Search("not-present", null, null, null, null, ct: CancellationToken.None);

        var page = Assert.IsType<PagedResultDto<ItemSummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    [Fact]
    public void BrowseSummaryContract_NeverContainsHiddenInformation()
    {
        Assert.DoesNotContain(typeof(ItemSummaryDto).GetProperties(), p => p.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_KeywordWithWildcardCharacters_IsForwardedAsLiteralInputToRepository()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search("  50%_off  ", null, null, null, null, ct: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("50%_off", captured.Value!.Keyword);
    }

    [Fact]
    public async Task Search_ValidBoundaryPagination_IsAccepted()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search(null, null, null, null, null, page: 1, pageSize: 50, ct: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, captured.Value!.Page);
        Assert.Equal(50, captured.Value.PageSize);
    }

    [Fact]
    public async Task Search_EqualDateBounds_AreAcceptedAsAnInclusiveSingleDayFilter()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search(null, null, null, "2026-09-12", "2026-09-12", ct: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new DateOnly(2026, 9, 12), captured.Value!.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 12), captured.Value.DateTo);
    }

    [Fact]
    public async Task Search_LostType_IsNormalizedBeforeReachingRepository()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search(null, null, "lost", null, null, ct: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("LOST", captured.Value!.ItemType);
    }

    [Fact]
    public async Task Search_ValidRepositoryPage_IsReturnedWithoutAlteringItsMetadata()
    {
        var expected = new PagedResultDto<ItemSummaryDto>
        {
            Items = [new ItemSummaryDto { Id = Guid.NewGuid(), ItemType = "LOST", Title = "Wallet", Status = "ACTIVE" }],
            Page = 2,
            PageSize = 12,
            TotalCount = 13,
            TotalPages = 2
        };
        var repo = new Mock<IItemsSearchRepository>();
        repo.Setup(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await new ItemsController(repo.Object).Search(null, null, null, null, null, page: 2, pageSize: 12, ct: CancellationToken.None);

        var actual = Assert.IsType<PagedResultDto<ItemSummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Same(expected, actual);
        Assert.Equal(13, actual.TotalCount);
        Assert.Equal(2, actual.TotalPages);
    }

    [Fact]
    public async Task Search_WhitespaceOnlyKeyword_IsNormalizedToAnEmptySearch()
    {
        var repo = ReturningEmpty(out var captured);

        var result = await new ItemsController(repo.Object).Search("   ", null, null, null, null, ct: CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(string.Empty, captured.Value!.Keyword);
    }

    private static Mock<IItemsSearchRepository> ReturningEmpty(out StrongBox<ItemSearchQuery?> captured)
    {
        var box = new StrongBox<ItemSearchQuery?>();
        captured = box;
        var repo = new Mock<IItemsSearchRepository>();
        repo.Setup(r => r.SearchAsync(It.IsAny<ItemSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ItemSearchQuery, CancellationToken>((query, _) => box.Value = query)
            .ReturnsAsync(new PagedResultDto<ItemSummaryDto>());
        return repo;
    }

    private static void AssertValidationError(ActionResult<PagedResultDto<ItemSummaryDto>> result, string key)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains(key, problem.Errors.Keys);
    }
}
