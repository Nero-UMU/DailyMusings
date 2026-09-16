using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Persistence;

/// <summary>
/// Opens a transaction on the scope's connection and rolls it back unless it was explicitly committed.
/// <para>
/// The rollback-on-dispose default is what makes the compound operations safe: if the use case throws
/// between registering a device and burning a pairing code, nothing is written at all.
/// </para>
/// </summary>
public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnectionAccessor _accessor;

    public SqliteUnitOfWork(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<ITransaction> BeginAsync(CancellationToken cancellationToken)
    {
        await _accessor.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return new Transaction(_accessor);
    }

    private sealed class Transaction : ITransaction
    {
        private readonly SqliteConnectionAccessor _accessor;
        private bool _completed;

        public Transaction(SqliteConnectionAccessor accessor) => _accessor = accessor;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await _accessor.CommitAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                await _accessor.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
