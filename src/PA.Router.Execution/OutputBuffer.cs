using System.Text;

namespace PA.Router.Execution;

/// <summary>
/// Buffer bounded dell'output di un tentativo.
///
/// I frammenti del provider non raggiungono mai il chiamante: si accumulano qui e vengono
/// esaminati solo a risposta completa. Superato il tetto, il buffer smette di accumulare e lo
/// dichiara: non tronca in silenzio, perche' un testo troncato sarebbe una risposta mai esaminata
/// per intero (02-architecture/EXECUTION-PIPELINE.md, punto 9).
/// </summary>
public sealed class OutputBuffer
{
    private readonly StringBuilder builder = new();
    private readonly int maxBytes;
    private int bytes;

    /// <param name="maxBytes">Tetto in byte UTF-8.</param>
    public OutputBuffer(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        this.maxBytes = maxBytes;
    }

    /// <summary>Vero se il tetto e' stato superato.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Byte UTF-8 accumulati.</summary>
    public int ByteLength => bytes;

    /// <summary>Aggiunge un frammento. Dopo l'overflow i frammenti vengono scartati.</summary>
    public void Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (Overflowed)
        {
            return;
        }

        int chunkBytes = Encoding.UTF8.GetByteCount(chunk);

        if (bytes + chunkBytes > maxBytes)
        {
            // Da qui in poi non si accumula piu': la risposta e' gia' oltre il limite del profilo
            // e non verra' comunque restituita.
            Overflowed = true;
            return;
        }

        builder.Append(chunk);
        bytes += chunkBytes;
    }

    /// <summary>
    /// Testo accumulato. Va passato allo screening prima di qualunque uso: qui non e' stato
    /// esaminato nulla.
    /// </summary>
    public string ToUnscreenedText() => builder.ToString();

    /// <summary>Nessun contenuto nella resa testuale.</summary>
    public override string ToString() =>
        "OutputBuffer { Bytes = " + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", Overflowed = " + Overflowed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}
