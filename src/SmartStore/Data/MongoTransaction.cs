using MongoDB.Driver;

namespace SmartStore.Data;

internal static class MongoTransaction
{
    private static readonly TransactionOptions Options = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    public static async Task<T> RunAsync<T>(IMongoClient client,
        Func<IClientSessionHandle, CancellationToken, Task<T>> action,
        string operation, string reference, CancellationToken cancellationToken)
    {
        using var session = await client.StartSessionAsync(cancellationToken: cancellationToken);
        var bodyCompleted = false;
        try
        {
            return await session.WithTransactionAsync(async (transaction, token) =>
            {
                // The driver may retry the entire callback after a write conflict.
                bodyCompleted = false;
                try
                {
                    var result = await action(transaction, token);
                    bodyCompleted = true;
                    return result;
                }
                catch
                {
                    if (transaction.IsInTransaction)
                    {
                        // The request token may already be cancelled. Reusing it for abort
                        // can leave server-side locks held until the transaction expires.
                        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        await transaction.AbortTransactionAsync(abort.Token);
                    }
                    throw;
                }
            }, Options, cancellationToken);
        }
        catch (Exception exception) when (bodyCompleted &&
            (exception is OperationCanceledException or TimeoutException or MongoConnectionException or MongoConnectionPoolPausedException ||
             exception is MongoException mongoException && mongoException.HasErrorLabel("UnknownTransactionCommitResult")))
        {
            // A lost acknowledgement is not evidence that the transaction failed.
            // Never compensate or invite another write when the commit may have succeeded.
            throw new TransactionOutcomeUnknownException(
                $"{operation} status is unknown (reference {reference}). Do not repeat this operation until store staff confirms its result.",
                exception);
        }
    }
}

public sealed class TransactionOutcomeUnknownException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
