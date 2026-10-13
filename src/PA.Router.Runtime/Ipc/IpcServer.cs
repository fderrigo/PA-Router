using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using PA.Router.Abstractions;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Server IPC del worker. Accetta piu' client dello stesso utente sulla stessa pipe: il vincolo
/// di unicita' riguarda il proprietario dello store, non il numero di connessioni.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Func<string, JsonElement?, CancellationToken, Task<object>> dispatch;
    private readonly Func<string, JsonElement?, Func<object, Task>, CancellationToken, Task>? stream;
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> connections = [];
    private readonly Lock connectionsGate = new();
    private Task? acceptLoop;
    private int connectedClients;

    /// <param name="pipeName">Nome della pipe derivato dalla directory dati.</param>
    /// <param name="dispatch">Esecuzione del metodo richiesto; restituisce il payload.</param>
    /// <param name="stream">
    /// Esecuzione dei metodi che trasmettono **piu' frame** per una sola richiesta (S65): riceve una
    /// funzione con cui scriverli e termina quando non c'e' piu' nulla da mandare.
    ///
    /// E' un percorso separato di proposito. La regola «una richiesta, una risposta» resta vera per
    /// ogni altro metodo, e un metodo che la rompesse in silenzio sarebbe indistinguibile da un
    /// guasto per chi sta leggendo la risposta.
    /// </param>
    public IpcServer(
        string pipeName,
        Func<string, JsonElement?, CancellationToken, Task<object>> dispatch,
        Func<string, JsonElement?, Func<object, Task>, CancellationToken, Task>? stream = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(dispatch);

        this.pipeName = pipeName;
        this.dispatch = dispatch;
        this.stream = stream;
    }

    /// <summary>Client attualmente collegati.</summary>
    public int ConnectedClients => Volatile.Read(ref connectedClients);

    /// <summary>Istante dell'ultima attivita' osservata su una connessione.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Avvia il loop di accettazione.</summary>
    public void Start()
    {
        if (acceptLoop is not null)
        {
            throw new InvalidOperationException("The IPC server is already started.");
        }

        acceptLoop = Task.Run(() => AcceptLoopAsync(shutdown.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server = CreatePipe();

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // Client sparito fra creazione e connessione: la pipe successiva riparte pulita.
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            Task connection = Task.Run(() => ServeAsync(server, cancellationToken), CancellationToken.None);

            lock (connectionsGate)
            {
                connections.RemoveAll(task => task.IsCompleted);
                connections.Add(connection);
            }
        }
    }

    private NamedPipeServerStream CreatePipe() =>
        new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            // CurrentUserOnly limita la ACL all'utente corrente: e' il controllo di identita'
            // del canale, non il nonce dell'handshake.
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref connectedClients);

        try
        {
            if (!await HandshakeAsync(server, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                IpcRequest? request = await IpcFraming.ReadAsync<IpcRequest>(server, cancellationToken)
                    .ConfigureAwait(false);

                if (request is null)
                {
                    return;
                }

                LastActivityUtc = DateTimeOffset.UtcNow;

                // Un metodo che trasmette piu' frame prende un percorso suo (S65): scrive gli eventi
                // man mano e chiude con un frame terminale che porta lo stesso identificatore di
                // correlazione. Per tutti gli altri metodi resta vero «una richiesta, una risposta».
                if (stream is not null && IpcProtocol.IsStreaming(request.Method))
                {
                    await StreamAsync(server, request, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await IpcFraming.WriteAsync(server, await HandleAsync(request, cancellationToken).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arresto del worker.
        }
        catch (IOException)
        {
            // Il client ha chiuso la pipe senza saluto: nulla da registrare.
        }
        catch (PaRouterException)
        {
            // Errore di protocollo gia' tipizzato: la connessione viene chiusa.
        }
        finally
        {
            Interlocked.Decrement(ref connectedClients);
            LastActivityUtc = DateTimeOffset.UtcNow;
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> HandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        await IpcFraming.WriteAsync(
            stream,
            new ServerHello(IpcProtocol.Version, IpcProtocol.RuntimeVersion, nonce),
            cancellationToken).ConfigureAwait(false);

        ClientHello? hello = await IpcFraming.ReadAsync<ClientHello>(stream, cancellationToken).ConfigureAwait(false);

        if (hello is null)
        {
            return false;
        }

        if (hello.ProtocolVersion != IpcProtocol.Version
            || !string.Equals(hello.ClientVersion, IpcProtocol.RuntimeVersion, StringComparison.Ordinal))
        {
            await IpcFraming.WriteAsync(
                stream,
                new HandshakeResult(
                    false,
                    ErrorCode.RuntimeVersionMismatch.ToWire(),
                    "The client and the worker must run the same runtime version."),
                cancellationToken).ConfigureAwait(false);

            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(hello.Nonce),
                Convert.FromBase64String(nonce)))
        {
            await IpcFraming.WriteAsync(
                stream,
                new HandshakeResult(
                    false,
                    ErrorCode.ProtocolError.ToWire(),
                    "The handshake challenge does not belong to this connection."),
                cancellationToken).ConfigureAwait(false);

            return false;
        }

        await IpcFraming.WriteAsync(stream, new HandshakeResult(true, null, null), cancellationToken)
            .ConfigureAwait(false);

        LastActivityUtc = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// Serve un metodo che trasmette piu' frame (S65).
    ///
    /// Ogni frame intermedio porta l'identificatore della richiesta e <c>ok</c> vero, cosi' chi legge
    /// sa che la risposta non e' finita; l'ultimo lo dichiara con <see cref="IpcResponse.Final"/>.
    /// Un guasto a meta' stream chiude con un frame terminale che porta il codice: una connessione
    /// interrotta a meta' lascerebbe il client ad aspettare qualcosa che non arriva.
    /// </summary>
    private async Task StreamAsync(Stream server, IpcRequest request, CancellationToken cancellationToken)
    {
        Task Write(object payload) => IpcFraming.WriteAsync(
            server,
            new IpcResponse(
                request.Id,
                true,
                null,
                null,
                JsonSerializer.SerializeToElement(payload, IpcProtocol.SerializerOptions)),
            cancellationToken);

        try
        {
            await stream!(request.Method, request.Payload, Write, cancellationToken).ConfigureAwait(false);

            await IpcFraming.WriteAsync(
                server,
                new IpcResponse(request.Id, true, null, null, null) { Final = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            await IpcFraming.WriteAsync(
                server,
                new IpcResponse(
                    request.Id,
                    false,
                    exception.Code.ToWire(),
                    exception.Message,
                    null,
                    exception.Metadata.TryGetValue("reason", out string? reason) ? reason : null)
                {
                    Final = true,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            object payload = await dispatch(request.Method, request.Payload, cancellationToken).ConfigureAwait(false);

            return new IpcResponse(
                request.Id,
                true,
                null,
                null,
                JsonSerializer.SerializeToElement(payload, IpcProtocol.SerializerOptions));
        }
        catch (PaRouterException ex)
        {
            // La **ragione** viaggia con il codice (S62). Senza, un front end che distingue due
            // rifiuti dello stesso codice — «quella sessione non esiste ancora» e «quel nome e' gia'
            // preso» sono entrambi INPUT_INVALID — ottiene la distinzione solo quando lo store e'
            // suo, e con un worker vivo degrada in un codice nudo. E' un chiave chiusa e senza
            // contenuti, come quelle dell'audit.
            return new IpcResponse(
                request.Id,
                false,
                ex.Code.ToWire(),
                ex.Message,
                null,
                ex.Metadata.TryGetValue("reason", out string? reason) ? reason : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un difetto senza codice: il client riceve un esito, non una connessione chiusa a meta'. Dopo un turno non
            // si sa se il servizio abbia lavorato, quindi l'esito e' dichiarato incerto.
            return new IpcResponse(
                request.Id,
                false,
                ErrorCode.SideEffectUncertain.ToWire(),
                "The worker could not complete the request.",
                null);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Arresto richiesto.
            }
        }

        Task[] pending;
        lock (connectionsGate)
        {
            pending = [.. connections];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Le connessioni chiudono con l'arresto.
        }

        shutdown.Dispose();
    }
}
