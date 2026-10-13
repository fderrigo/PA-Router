using System.Text;
using System.Text.Json;

namespace PA.Router.Abstractions;

/// <summary>Bounds the bytes received before decoding or parsing untrusted HTTP bodies.</summary>
public static class BoundedHttpContent
{
    public const int ErrorBytes = 64 * 1024;
    public const int MetadataBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Preserves the configured client timeout through body reads after ResponseHeadersRead.</summary>
    public static CancellationTokenSource Deadline(HttpClient client, CancellationToken cancellationToken)
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan) { timeout.CancelAfter(client.Timeout); }
        return timeout;
    }

    /// <summary>JSON escapes may expand one authorized output byte into six wire bytes.</summary>
    public static int JsonBytes(int outputBytes) => (int)Math.Clamp(6L * Math.Max(0, outputBytes) + ErrorBytes, ErrorBytes, 16 * 1024 * 1024);

    /// <summary>Streaming framing and reasoning are bounded independently of the displayed text.</summary>
    public static int StreamBytes(int outputBytes) => (int)Math.Clamp(64L * Math.Max(0, outputBytes) + ErrorBytes, ErrorBytes, 64 * 1024 * 1024);

    public static async Task<string> ReadUtf8Async(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using Stream raw = await OpenAsync(content, limit, cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        await raw.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Decode(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    public static async Task<Stream> OpenAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (content.Headers.ContentLength is long length && length > limit) { throw TooLarge(); }
        Stream raw = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new LimitedStream(raw, limit);
    }

    /// <summary>Invalid or negative provider counters are unknown, never zero or a signed credit.</summary>
    public static long? NonNegativeCount(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long count) && count >= 0 ? count : null;

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new PaRouterException(ErrorCode.ProtocolError, "The response is not valid UTF-8."); }
    }

    private static PaRouterException TooLarge() => new(ErrorCode.ProtocolError, "The response exceeds its transport byte limit.");

    private sealed class LimitedStream(Stream inner, int limit) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (read > limit) { throw TooLarge(); }
            int actual = inner.Read(buffer, offset, (int)Math.Min(count, limit - read + 1));
            read += actual;
            if (read > limit) { throw TooLarge(); }
            return actual;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read > limit) { throw TooLarge(); }
            int actual = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - read + 1)], cancellationToken).ConfigureAwait(false);
            read += actual;
            if (read > limit) { throw TooLarge(); }
            return actual;
        }
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
