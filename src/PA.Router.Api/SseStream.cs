using Microsoft.AspNetCore.Http;

namespace PA.Router.Api;

/// <summary>Un solo writer HTTP e una sola lettura pendente; il silenzio non ferma gli heartbeat.</summary>
internal static class SseStream
{
    internal static async Task WriteAsync<T>(
        HttpResponse response,
        IAsyncEnumerable<T> events,
        Func<T, Task> writeEvent,
        TimeSpan heartbeat,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using IAsyncEnumerator<T> reader = events.GetAsyncEnumerator(stop.Token);
        Task<bool>? next = null;
        Task beat = Task.Delay(heartbeat, stop.Token);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                next ??= reader.MoveNextAsync().AsTask();
                await Task.WhenAny(next, beat).ConfigureAwait(false);
                if (next.IsCompleted)
                {
                    if (!await next.ConfigureAwait(false)) { return; }
                    next = null;
                    await writeEvent(reader.Current).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (beat.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await response.WriteAsync(": heartbeat\n\n", cancellationToken).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    beat = Task.Delay(heartbeat, stop.Token);
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (next is not null)
            {
                // Prima del dispose dell'enumeratore deve terminare la lettura pendente.
                // L'errore originale della lettura/scrittura HTTP resta quello propagato.
                try { await next.ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }
}
