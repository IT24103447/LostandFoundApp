using ItemService.Databases;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ItemService.Filters;

/// <summary>
/// Makes every state-changing request (POST / PUT / PATCH / DELETE) one database transaction.
///
/// The controllers keep doing what they did before: write the item, then call
/// IEventPublisher.PublishAsync. What changed is that both now happen on the same
/// transaction (via IDbSession), which is committed only after the action finishes without
/// throwing. If anything fails, the item change AND its outbox event are rolled back together,
/// so the database and the event stream can never disagree.
///
/// The transaction commits before the response is written, so a commit failure is returned
/// to the caller as an error instead of a success that was never saved.
/// </summary>
public sealed class RequestTransactionFilter : IAsyncActionFilter
{
    private readonly IDbSession _session;

    public RequestTransactionFilter(IDbSession session)
    {
        _session = session;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            await next();
            return;
        }

        _session.BeginRequestTransaction();

        ActionExecutedContext executed;
        try
        {
            executed = await next();
        }
        catch
        {
            await _session.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (executed.Exception is null)
        {
            // CancellationToken.None: once the action has succeeded, a client disconnect must not
            // abort the commit half way and leave the work in an unknown state.
            await _session.CommitAsync(CancellationToken.None);
        }
        else
        {
            // The exception keeps flowing to the global exception handler untouched.
            await _session.RollbackAsync(CancellationToken.None);
        }
    }
}
