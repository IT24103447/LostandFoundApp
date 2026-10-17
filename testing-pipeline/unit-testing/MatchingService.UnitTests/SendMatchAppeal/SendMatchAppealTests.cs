using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using MatchingService.Appeals;
using MatchingService.Claims;
using MatchingService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace MatchingService.UnitTests.SendMatchAppeal;

public sealed class SendMatchAppealTests
{
    [Theory]
    [InlineData(301)]
    [InlineData(450)]
    public async Task OverlongNote_IsRejectedBeforeAnyItemOrDatabaseCall(int length)
    {
        var service = new AppealService(null!, null!, null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SendAsync(
            new SendAppealRequest(Guid.NewGuid(), Guid.NewGuid(), "preview", new string('n', length)),
            Guid.NewGuid(), "user@example.test", "+94770000001", CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("The note can be at most 300 characters.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingPreviewVersion_IsRejectedBeforeAnyItemOrDatabaseCall(string version)
    {
        var service = new AppealService(null!, null!, null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SendAsync(
            new SendAppealRequest(Guid.NewGuid(), Guid.NewGuid(), version, null),
            Guid.NewGuid(), "user@example.test", "+94770000001", CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Preview the reports before sending an appeal.", error.Message);
    }

    [Fact]
    public async Task UserOwningNeitherReport_IsRejectedWithTheClaimFlowError()
    {
        var (service, handler, lost, found) = ServiceWithReports();

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SendAsync(
            new SendAppealRequest(lost.Id, found.Id, "preview", null),
            Guid.NewGuid(), "user@example.test", "+94770000001", CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("You must own one selected report.", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UserOwningBothReports_IsRejectedWithTheClaimFlowError()
    {
        var owner = Guid.NewGuid();
        var (service, _, lost, found) = ServiceWithReports(owner, owner);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SendAsync(
            new SendAppealRequest(lost.Id, found.Id, "preview", null),
            owner, "user@example.test", "+94770000001", CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("You cannot match your own lost and found reports.", error.Message);
    }

    [Theory]
    [InlineData("RESOLVED", "ACTIVE")]
    [InlineData("ACTIVE", "DELETED")]
    public async Task InactiveReport_IsRejectedWithTheClaimFlowError(string lostStatus, string foundStatus)
    {
        var (service, _, lost, found) = ServiceWithReports();
        lost.Status = lostStatus;
        found.Status = foundStatus;

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.SendAsync(
            new SendAppealRequest(lost.Id, found.Id, "preview", null),
            lost.UserId, "user@example.test", "+94770000001", CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Both reports must still be active.", error.Message);
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public async Task EditWarning_RejectsInvalidReportTypeBeforeDatabaseAccess(string type)
    {
        var service = new AppealService(null!, null!, null!);

        var error = await Assert.ThrowsAsync<ClaimException>(() => service.HasEditWarningAsync(
            type, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Type must be lost or found.", error.Message);
    }

    [Fact]
    public void MyAppealProjection_ShowsDecisionAndReasonButNeverTheAdminIdentity()
    {
        var record = AppealRecordWith("REJECTED", "The details do not match.");
        var method = typeof(AppealService).GetMethod("ToMyView", BindingFlags.NonPublic | BindingFlags.Static)!;

        var view = Assert.IsType<MyAppealView>(method.Invoke(null, [record]));
        var json = JsonSerializer.Serialize(view);

        Assert.Equal("REJECTED", view.Status);
        Assert.Equal("The details do not match.", view.RejectionReason);
        Assert.Equal(record.DecidedAt, view.DecidedAt);
        Assert.Equal(record.Lost, view.Lost);
        Assert.Equal(record.Found, view.Found);
        Assert.DoesNotContain("DecidedBy", json);
        Assert.DoesNotContain(record.DecidedBy!.Value.ToString(), json);
        Assert.DoesNotContain(record.AppellantEmail, json);
        Assert.DoesNotContain(record.AppellantPhone, json);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("mine")]
    [InlineData("pair-status")]
    [InlineData("edit-warning")]
    public async Task AppealEndpoints_RejectRequestsWithoutAValidUserId(string endpoint)
    {
        var controller = new AppealsController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = endpoint switch
        {
            "send" => await controller.Send(
                new SendAppealRequest(Guid.NewGuid(), Guid.NewGuid(), "preview", null), CancellationToken.None),
            "mine" => await controller.Mine(cancellationToken: CancellationToken.None),
            "pair-status" => await controller.PairStatus(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None),
            _ => await controller.EditWarning("LOST", Guid.NewGuid(), CancellationToken.None)
        };

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task SendPolicy_AllowsOnlyAuthenticatedEmailVerifiedUsers()
    {
        using var services = AuthorizationServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();

        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(), "VerifiedClaimUser")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(Principal("email_verified", "0"), "VerifiedClaimUser")).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(Principal("email_verified", "1"), "VerifiedClaimUser")).Succeeded);
        Assert.Equal("VerifiedClaimUser", typeof(AppealsController)
            .GetMethod(nameof(AppealsController.Send))!
            .GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    private static ClaimsPrincipal Principal(string claimType, string value) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(claimType, value)], "test"));

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

    private static (AppealService Service, ItemHandler Handler, ItemReport Lost, ItemReport Found)
        ServiceWithReports(Guid? lostOwner = null, Guid? foundOwner = null)
    {
        var lost = Report(lostOwner ?? Guid.NewGuid());
        var found = Report(foundOwner ?? Guid.NewGuid());
        var handler = new ItemHandler();
        handler.Reports[$"api/items/lost/{lost.Id}"] = lost;
        handler.Reports[$"api/items/found/{found.Id}"] = found;
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-token";
        var items = new ClaimItemClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://item.example.test/") },
            new HttpContextAccessor { HttpContext = context });
        return (new AppealService(new ClaimService(items, null!), items, null!), handler, lost, found);
    }

    private static ItemReport Report(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Status = "ACTIVE", Title = "Blue bag",
        Category = "Bags", Description = "Blue canvas backpack"
    };

    private sealed class ItemHandler : HttpMessageHandler
    {
        public Dictionary<string, ItemReport> Reports { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Requests.Add(path);
            return Task.FromResult(Reports.TryGetValue(path, out var report)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(report) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static AppealRecord AppealRecordWith(string status, string? reason)
    {
        var lost = new ClaimItemView(Guid.NewGuid(), "LOST", "Blue bag", "Bags", "Canvas bag", "2026-10-01", "Library");
        var found = new ClaimItemView(Guid.NewGuid(), "FOUND", "Blue bag", "Bags", "Canvas bag", "2026-10-02", "Station");
        return new AppealRecord(
            Guid.NewGuid(), lost.Id, found.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "LOST",
            "sender@example.test", "+94770000001", 42m, new ScoreBreakdown(40, 30, 50, 0, 0),
            lost, found, "Please review this pair.", status, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, reason);
    }
}
