namespace PA.Router.Abstractions;

/// <summary>A UTF-8 line reader with a byte limit before allocating the line's string.</summary>
public sealed class BoundedNdjsonReader(Stream stream, int lineLimit)
{
    private readonly byte[] buffer = new byte[4096];
    private int offset;
    private int count;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using MemoryStream line = new();
        while (true)
        {
            if (offset == count)
            {
                count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, lineLimit - line.Length + 1)), cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (count == 0) { return line.Length == 0 ? null : Decode(line); }
            }
            int end = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
            int length = (end < 0 ? count : end) - offset;
            if (line.Length + length > lineLimit)
            {
                throw new PaRouterException(ErrorCode.ProtocolError, "The response line exceeds its transport byte limit.");
            }
            line.Write(buffer, offset, length);
            offset += length;
            if (end >= 0) { offset++; return Decode(line); }
        }
    }

    private static string Decode(MemoryStream line)
    {
        int length = checked((int)line.Length);
        byte[] bytes = line.GetBuffer();
        if (length > 0 && bytes[length - 1] == '\r') { length--; }
        return BoundedHttpContent.Decode(bytes.AsSpan(0, length));
    }
}
