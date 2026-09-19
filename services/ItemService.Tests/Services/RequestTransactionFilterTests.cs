using ItemService.Databases;
using ItemService.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

public class RequestTransactionFilterTests
{
    private readonly Mock<IDbSession> _session = new();

    private static ActionContext CreateActionContext(string httpMethod)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = httpMethod;
        return new ActionContext(http, new RouteData(), new ActionDescriptor());
    }

    /// <summary>Runs the filter and returns true if the wrapped action ("next") was executed.</summary>
    private async Task<bool> RunAsync(string httpMethod, Exception? actionException = null, bool nextThrows = false)
    {
        var actionContext = CreateActionContext(httpMethod);
        var executing = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

        var nextCalled = false;
        ActionExecutionDelegate next = () =>
        {
            nextCalled = true;
            if (nextThrows)
            {
                throw new InvalidOperationException("action blew up");
            }

            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object())
            {
                Exception = actionException
            });
        };

        await new RequestTransactionFilter(_session.Object).OnActionExecutionAsync(executing, next);
        return nextCalled;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task WriteRequest_ThatSucceeds_IsCommittedOnce(string httpMethod)
    {
        var nextCalled = await RunAsync(httpMethod);

        Assert.True(nextCalled);
        _session.Verify(s => s.BeginRequestTransaction(), Times.Once);
        _session.Verify(s => s.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _session.Verify(s => s.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ReadRequest_RunsWithoutAnyTransaction(string httpMethod)
    {
        var nextCalled = await RunAsync(httpMethod);

        Assert.True(nextCalled);
        _session.Verify(s => s.BeginRequestTransaction(), Times.Never);
        _session.Verify(s => s.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _session.Verify(s => s.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteRequest_WhoseActionThrew_IsRolledBackNotCommitted()
    {
        await RunAsync("POST", actionException: new InvalidOperationException("database unavailable"));

        _session.Verify(s => s.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _session.Verify(s => s.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteRequest_WhenNextThrows_RollsBackAndRethrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("DELETE", nextThrows: true));

        _session.Verify(s => s.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _session.Verify(s => s.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteRequest_CommitFailure_PropagatesSoTheCallerSeesAnErrorNotFalseSuccess()
    {
        _session
            .Setup(s => s.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("commit failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("POST"));
    }
}
