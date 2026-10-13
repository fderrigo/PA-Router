using System.Buffers.Binary;
using System.Text.Json;
using PA.Router.Abstractions;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Frame lunghezza + payload JSON UTF-8. Il limite di 768 KiB e' applicato in lettura prima di
/// allocare: un mittente che dichiara un frame enorme viene respinto, non assecondato.
/// </summary>
public static class IpcFraming
{
    private const int HeaderSize = 4;

    /// <summary>Serializza e scrive un messaggio.</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcProtocol.SerializerOptions);

        if (payload.Length > IpcProtocol.MaxFrameBytes)
        {
            throw new PaRouterException(
                ErrorCode.ProtocolError,
                "The outgoing IPC frame exceeds the protocol limit.");
        }

        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Legge un messaggio. Restituisce <c>default</c> quando il peer ha chiuso in modo pulito,
    /// cosi' la chiusura normale non viene trattata come errore di protocollo.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderSize];
        int read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new PaRouterException(ErrorCode.ProtocolError, "The IPC frame header is truncated.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (length == 0 || length > IpcProtocol.MaxFrameBytes)
        {
            throw new PaRouterException(ErrorCode.ProtocolError, "The IPC frame length is out of range.")
                .WithMetadata("declaredBytes", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<T>(payload, IpcProtocol.SerializerOptions)
                ?? throw new PaRouterException(ErrorCode.ProtocolError, "The IPC frame carried a null message.");
        }
        catch (JsonException ex)
        {
            throw new PaRouterException(ErrorCode.ProtocolError, "The IPC frame is not valid JSON.", ex);
        }
    }
}
