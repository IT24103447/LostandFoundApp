using System.Reflection;
using System.Security.Claims;
using MatchingService.Appeals;
using MatchingService.Claims;
using MatchingService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MatchingService.UnitTests.AdminMatchAppealReview;

public sealed class AdminMatchAppealReviewTests
{
    [Theory]
    [InlineData("")]
    [InlineData("OPEN")]
    [InlineData("DELETED")]
    public async Task ListRejectsUnsupportedStatusBeforeDatabaseAccess(string status)
    {
        var service = AdminService();

        var error = await Assert.ThrowsAsync<ClaimException>(() =>
            service.ListAsync(status, 1, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Status must be PENDING, VERIFIED or REJECTED.", error.Message);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(450)]
    public async Task OverlongRejectionReason_IsRejectedBeforeAnyDatabaseCall(int length)
    {
        var service = AdminService();

        var error = await Assert.ThrowsAsync<ClaimException>(() =>
            service.RejectAsync(Guid.NewGuid(), Guid.NewGuid(), new string('r', length), CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("The reason can be at most 300 characters.", error.Message);
    }

    [Theory]
    [InlineData(AppealStatus.Verified)]
    [InlineData(AppealStatus.Rejected)]
    public void DecidedAppeal_CannotBeDecidedAgain(string status)
    {
        var method = typeof(AdminAppealService)
            .GetMethod("EnsurePending", BindingFlags.NonPublic | BindingFlags.Static)!;

        var wrapper = Assert.Throws<TargetInvocationException>(() =>
        {
            method.Invoke(null, [Record(status)]);
        });
        var error = Assert.IsType<ClaimException>(wrapper.InnerException);

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("This appeal has already been decided.", error.Message);
    }

    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("active", true)]
    [InlineData("RESOLVED", false)]
    [InlineData("DELETED", false)]
    public void VerifyOnlyAcceptsActiveReports(string status, bool expected)
    {
        var method = typeof(AdminAppealService)
            .GetMethod("IsActive", BindingFlags.NonPublic | BindingFlags.Static)!;

        var actual = (bool)method.Invoke(null, [new ItemReport { Status = status }])!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AdminAppealProjection_IncludesScoreBreakdownReasonAndAuditFields()
    {
        var record = Record(AppealStatus.Rejected) with
        {
            RejectionReason = "The reports describe different items.",
            Note = "Please check the mark on the bag."
        };
        var method = typeof(AdminAppealService)
            .GetMethod("ToAdminView", BindingFlags.NonPublic | BindingFlags.Static)!;

        var view = Assert.IsType<AdminAppealView>(method.Invoke(null, [record]));

        Assert.Equal(record.Score, view.Score);
        Assert.Equal(record.Breakdown, view.Breakdown);
        Assert.Equal(record.Lost, view.Lost);
        Assert.Equal(record.Found, view.Found);
        Assert.Equal(record.Note, view.Note);
        Assert.Equal(record.Status, view.Status);
        Assert.Equal(record.DecidedBy, view.DecidedBy);
        Assert.Equal(record.DecidedAt, view.DecidedAt);
        Assert.Equal(record.RejectionReason, view.RejectionReason);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("open")]
    [InlineData("verify")]
    [InlineData("reject")]
    public async Task AdminEndpoints_RejectRequestsWithoutAValidAdminId(string endpoint)
    {
        var controller = new AdminAppealsController(null!, NullLogger<AdminAppealsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var id = Guid.NewGuid();

        var result = endpoint switch
        {
            "list" => await controller.List(cancellationToken: CancellationToken.None),
            "open" => await controller.Open(id, CancellationToken.None),
            "verify" => await controller.Verify(id, CancellationToken.None),
            _ => await controller.Reject(id, new RejectAppealRequest(null), CancellationToken.None)
        };

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task AdminOnlyPolicy_RejectsOrdinaryUsersAndAcceptsAdmins()
    {
        using var services = AuthorizationServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();

        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(), "AdminOnly")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(Principal("0"), "AdminOnly")).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(Principal("1"), "AdminOnly")).Succeeded);
        Assert.Equal("AdminOnly", typeof(AdminAppealsController)
            .GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public async Task AdminListRejectsInvalidPageBeforeDatabaseAccess(int page)
    {
        var context = new DefaultHttpContext();
        context.User = Principal("1");
        var controller = new AdminAppealsController(null!, NullLogger<AdminAppealsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = Assert.IsType<ObjectResult>(await controller.List(page: page,
            cancellationToken: CancellationToken.None));

        Assert.Equal(400, result.StatusCode);
    }

    private static AdminAppealService AdminService() => new(null!, null!, null!, null!);

    private static ClaimsPrincipal Principal(string isAdmin) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("is_admin", isAdmin)], "test"));

    private static ServiceProvider AuthorizationServices()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = new string('x', 32), ["Jwt:Issuer"] = "unit-tests",
            ["Jwt:Audience"] = "unit-tests", ["ItemService:BaseUrl"] = "https://item.example.test"
        }).Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddManualClaims(config, environment.Object);
        return services.BuildServiceProvider();
    }

    private static AppealRecord Record(string status)
    {
        var lost = new ClaimItemView(Guid.NewGuid(), "LOST", "Blue bag", "Bags", "Canvas bag", "2026-10-01", "Library");
        var found = new ClaimItemView(Guid.NewGuid(), "FOUND", "Blue bag", "Bags", "Canvas bag", "2026-10-02", "Station");
        return new AppealRecord(Guid.NewGuid(), lost.Id, found.Id, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "LOST", "user@example.test", "+94770000001", 42m,
            new ScoreBreakdown(40, 30, 50, 0, 0), lost, found, null, status,
            Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, null);
    }
}
