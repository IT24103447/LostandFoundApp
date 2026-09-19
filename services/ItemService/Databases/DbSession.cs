using System.Data;
using MySqlConnector;

namespace ItemService.Databases;

/// A borrowed connection (plus the transaction it belongs to, if any).
/// Dispose it when the command is done: it only closes connections it opened itself,
/// so a connection shared by the request-wide transaction stays open.
public sealed class DbLease : IAsyncDisposable
{
    private readonly bool _ownsConnection;

    public DbLease(MySqlConnection connection, MySqlTransaction? transaction, bool ownsConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
    }

    public MySqlConnection Connection { get; }

    /// <summary>Pass this to every MySqlCommand created on <see cref="Connection"/>.</summary>
    public MySqlTransaction? Transaction { get; }

    public ValueTask DisposeAsync() =>
        _ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// Request-scoped database session used by every WRITE. It lets the item change and the
/// outbox row for its event succeed or fail together (the "transactional" part of the
/// transactional outbox).
///
/// Outside a request transaction (reads, background work, tests that don't opt in) every
/// <see cref="AcquireAsync"/> call simply opens its own connection with no transaction,
/// which is exactly how the repositories behaved before.
/// </summary>
public interface IDbSession : IAsyncDisposable
{
    /// <summary>True once <see cref="BeginRequestTransaction"/> has been called and until commit/rollback.</summary>
    bool IsTransactional { get; }

    /// <summary>
    /// Marks the current scope as transactional. The connection and the transaction are opened
    /// lazily on the first write, so requests that never write cost nothing.
    /// </summary>
    void BeginRequestTransaction();

    Task<DbLease> AcquireAsync(CancellationToken ct = default);

    /// <summary>Commits everything written since <see cref="BeginRequestTransaction"/>. No-op if nothing was written.</summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Discards everything written since <see cref="BeginRequestTransaction"/>. Never throws.</summary>
    Task RollbackAsync(CancellationToken ct = default);
}

public sealed class DbSession : IDbSession, IDisposable
{
    private readonly IDbConnectionFactory _factory;
    private MySqlConnection? _connection;
    private MySqlTransaction? _transaction;

    public DbSession(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public bool IsTransactional { get; private set; }

    public void BeginRequestTransaction() => IsTransactional = true;

    public async Task<DbLease> AcquireAsync(CancellationToken ct = default)
    {
        if (!IsTransactional)
        {
            var standalone = _factory.Create();
            await standalone.OpenAsync(ct);
            return new DbLease(standalone, transaction: null, ownsConnection: true);
        }

        if (_connection is null)
        {
            var connection = _factory.Create();
            try
            {
                await connection.OpenAsync(ct);
                _transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }

            _connection = connection;
        }

        return new DbLease(_connection, _transaction, ownsConnection: false);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null)
        {
            IsTransactional = false;
            return;
        }

        try
        {
            await _transaction.CommitAsync(ct);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is null)
        {
            IsTransactional = false;
            return;
        }

        try
        {
            await _transaction.RollbackAsync(ct);
        }
        catch (Exception)
        {
            // The connection may already be broken. The server discards uncommitted work when the
            // connection closes, and a failed rollback must never hide the error that caused it.
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Safety net: an un-committed transaction at the end of the scope is rolled back.
        if (_transaction is not null)
        {
            await RollbackAsync(CancellationToken.None);
        }
        else
        {
            await ReleaseAsync();
        }
    }

    /// <summary>
    /// Synchronous twin of <see cref="DisposeAsync"/>. ASP.NET Core disposes request scopes asynchronously,
    /// but a scope disposed with a plain Dispose() (tests, background code) would otherwise throw because
    /// the container refuses to synchronously dispose a service that only supports async disposal.
    /// </summary>
    public void Dispose()
    {
        var transaction = _transaction;
        var connection = _connection;
        _transaction = null;
        _connection = null;
        IsTransactional = false;

        if (transaction is not null)
        {
            try
            {
                transaction.Rollback(); // an un-committed transaction at the end of the scope is discarded
            }
            catch (Exception)
            {
                // The connection may already be broken; the server discards the work when it closes.
            }

            transaction.Dispose();
        }

        connection?.Dispose();
    }

    private async Task ReleaseAsync()
    {
        var transaction = _transaction;
        var connection = _connection;
        _transaction = null;
        _connection = null;
        IsTransactional = false;

        if (transaction is not null)
        {
            await transaction.DisposeAsync();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}