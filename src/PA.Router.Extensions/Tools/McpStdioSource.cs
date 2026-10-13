using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Extensions.Tools;

/// <summary>
/// Server MCP avviato come programma (specifica 2025-11-25, stdio): messaggi JSON-RPC su standard input e output,
/// uno per riga. Si dichiara solo nella configurazione del router: un'applicazione esterna non fa avviare
/// processi su questa macchina (ADR-020).
///
/// Il programma parte senza shell, con un ambiente ridotto all'allowlist dei programmi ufficiali e dentro un
/// contenitore dell'albero dei processi. **Non e' una sandbox**: il programma resta codice con i permessi
/// dell'utente, e puo' raggiungere la rete; per questo la sua collocazione e' quella di un terzo, finche'
/// l'operatore non attesta altro.
/// </summary>
public sealed class McpStdioSource : McpSource
{
    /// <summary>Tetto di una riga.</summary>
    public const int MaxFrameChars = 4 * 1024 * 1024;

    private readonly Process process;
    private readonly IProcessGuard guard;
    private readonly SemaphoreSlim exchange = new(1, 1);
    private readonly Task standardErrorDrain;
    private readonly char[] readBuffer = new char[16 * 1024];
    private int readStart;
    private int readEnd;
    private volatile bool broken;

    private McpStdioSource(string id, Process process, IProcessGuard guard)
        : base(id, "stdio:" + id, Placement.ThirdParty)
    {
        this.process = process;
        this.guard = guard;

        // Lo standard error non si propaga: puo' contenere percorsi o frammenti di dati.
        standardErrorDrain = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Avvia il programma dentro un contenitore dell'albero dei processi.</summary>
    public static McpStdioSource Start(string id, string command, IReadOnlyList<string> arguments, IProcessGuardFactory guards)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(guards);

        if (!guards.IsSupported)
        {
            throw SourceHttp.Unavailable(id, "containment_unavailable");
        }

        if (string.IsNullOrWhiteSpace(command) || !Path.IsPathFullyQualified(command) || !File.Exists(command))
        {
            throw SourceHttp.Unavailable(id, "command_missing");
        }

        ProcessStartInfo start = new()
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();

        foreach (string name in CliProcessLauncher.BaseEnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is string value)
            {
                start.Environment[name] = value;
            }
        }

        IProcessGuard guard = guards.Create();
        Process process = new() { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                throw SourceHttp.Unavailable(id, "start_failed");
            }

            guard.Assign(process);
        }
        catch (Win32Exception)
        {
            guard.Dispose();
            process.Dispose();

            throw SourceHttp.Unavailable(id, "start_failed");
        }
        catch (InvalidOperationException)
        {
            guard.Dispose();
            process.Dispose();

            throw SourceHttp.Unavailable(id, "start_failed");
        }

        return new McpStdioSource(id, process, guard);
    }

    /// <inheritdoc />
    protected override async Task<JsonNode?> ExchangeAsync(JsonObject message, bool expectsResponse, CancellationToken cancellationToken)
    {
        // Un solo scambio alla volta: le risposte arrivano sullo stesso flusso, e due lettori le mescolerebbero.
        await exchange.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (broken)
            {
                // Uno scambio interrotto lascia il flusso a meta' di un messaggio: la sorgente non si riusa nel turno.
                throw SourceHttp.Unavailable(Id, "stream_closed");
            }

            try
            {
                await InterruptibleAsync(
                    process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), CancellationToken.None),
                    cancellationToken).ConfigureAwait(false);
                await InterruptibleAsync(process.StandardInput.FlushAsync(CancellationToken.None), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw SourceHttp.Unavailable(Id, "stream_closed");
            }

            if (!expectsResponse)
            {
                return null;
            }

            string id = message["id"]!.GetValue<string>();

            while (true)
            {
                string? line = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    throw SourceHttp.Unavailable(Id, "stream_closed");
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonNode? node;

                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    throw SourceHttp.Unavailable(Id, "malformed_frame");
                }

                if (Answers(node, id))
                {
                    return node;
                }

                // Notifiche e richieste del server: il router non offre capacita' e non le serve.
            }
        }
        finally
        {
            exchange.Release();
        }
    }

    /// <summary>
    /// Una riga dello standard output, con il tetto applicato mentre si legge: una riga senza fine non cresce in memoria
    /// oltre <see cref="MaxFrameChars"/>. Null a fine flusso.
    /// </summary>
    private async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        StringBuilder line = new();

        while (true)
        {
            if (readStart == readEnd)
            {
                int count;

                try
                {
                    count = await InterruptibleAsync(
                        process.StandardOutput.ReadAsync(readBuffer.AsMemory(), CancellationToken.None).AsTask(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw SourceHttp.Unavailable(Id, "stream_closed");
                }

                if (count == 0)
                {
                    return line.Length > 0 ? line.ToString() : null;
                }

                readStart = 0;
                readEnd = count;
            }

            int newline = Array.IndexOf(readBuffer, '\n', readStart, readEnd - readStart);
            int end = newline < 0 ? readEnd : newline;

            if (line.Length + (end - readStart) > MaxFrameChars)
            {
                Break();
                throw SourceHttp.Unavailable(Id, "frame_too_large");
            }

            line.Append(readBuffer, readStart, end - readStart);
            readStart = newline < 0 ? readEnd : newline + 1;

            if (newline >= 0)
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }
        }
    }

    /// <summary>
    /// Attende un'operazione sulle pipe del programma fino all'annullamento. Le pipe di un processo sono sincrone: il token
    /// passato alla lettura non la interrompe, e un server fermo tratterrebbe il turno oltre ogni limite. All'annullamento
    /// il programma si termina, e l'operazione rimasta sospesa si chiude con lui.
    /// </summary>
    private async Task InterruptibleAsync(Task operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon(operation);
            throw;
        }
    }

    /// <inheritdoc cref="InterruptibleAsync(Task, CancellationToken)"/>
    private async Task<T> InterruptibleAsync<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon(operation);
            throw;
        }
    }

    private void Abandon(Task operation)
    {
        Break();

        // L'operazione sospesa fallira' quando il processo muore: il suo errore non interessa piu' a nessuno.
        _ = operation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Segna il flusso come non piu' allineato e termina il programma.</summary>
    private void Break()
    {
        broken = true;
        Kill();
    }

    private void Kill()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // Chiusura come chiede la specifica: prima lo standard input, poi l'attesa, poi la terminazione dell'albero.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            using CancellationTokenSource wait = new(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        guard.Terminate();

        // Il contenimento termina l'albero; dove non l'ha fatto, il programma non sopravvive comunque al turno.
        Kill();
        guard.Dispose();

        try
        {
            await standardErrorDrain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        process.Dispose();
        exchange.Dispose();
        DisposeInitialization();
    }
}
