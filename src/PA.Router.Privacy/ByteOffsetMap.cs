using System.Text;

namespace PA.Router.Privacy;

/// <summary>
/// Traduzione da indice di carattere UTF-16 a offset di byte UTF-8. I contratti misurano in byte
/// e i detector lavorano su stringhe: senza questa mappa uno span calcolato su testo accentato o
/// con emoji indicherebbe la posizione sbagliata.
///
/// La mappa si costruisce una volta per superficie, in tempo lineare.
/// </summary>
public sealed class ByteOffsetMap
{
    private readonly int[] offsets;

    private ByteOffsetMap(int[] offsets)
    {
        this.offsets = offsets;
    }

    /// <summary>Byte totali della superficie.</summary>
    public int TotalBytes => offsets[^1];

    /// <summary>Costruisce la mappa per il testo indicato.</summary>
    public static ByteOffsetMap For(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int[] offsets = new int[text.Length + 1];
        int bytes = 0;

        for (int index = 0; index < text.Length; index++)
        {
            offsets[index] = bytes;

            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                // La coppia surrogata e' un solo code point: 4 byte in UTF-8, e il secondo
                // indice condivide l'offset iniziale della coppia.
                bytes += 4;
                offsets[index + 1] = offsets[index];
                index++;
                continue;
            }

            bytes += Encoding.UTF8.GetByteCount(text.AsSpan(index, 1));
        }

        offsets[text.Length] = bytes;
        return new ByteOffsetMap(offsets);
    }

    /// <summary>Offset in byte del carattere indicato.</summary>
    public int ByteOffset(int charIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(charIndex, offsets.Length - 1);

        return offsets[charIndex];
    }

    /// <summary>Lunghezza in byte dell'intervallo di caratteri indicato.</summary>
    public int ByteLength(int charIndex, int charLength) =>
        ByteOffset(charIndex + charLength) - ByteOffset(charIndex);
}
