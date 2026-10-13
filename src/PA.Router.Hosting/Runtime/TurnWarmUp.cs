using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Cio' che il primo turno pagherebbe a freddo, fatto mentre l'operatore scrive (23 settembre 2026): il prompt di sistema
/// dell'analizzatore e i vettori degli esempi del classificatore e delle frasi dell'art. 9. Nessun testo dell'operatore,
/// nessuna decisione, nessuna registrazione; ogni parte che fallisce resta al turno, che riprova. Un punto solo per REPL,
/// `pa script` e worker (INV-22).
/// </summary>
public static class TurnWarmUp
{
    /// <summary>
    /// Come l'altra forma, e in piu' carica i rilevatori della privacy della composizione — il modello ONNX costava circa
    /// sette secondi al primo turno. La usa chi possiede lo store: il REPL senza worker e il worker.
    /// </summary>
    public static Task RunAsync(RouterStack stack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        StackView view = stack.EnsureCurrent();

        return Task.WhenAll(
            Task.Run(() => view.Scanner(), cancellationToken),
            RunAsync(view.Snapshot, cancellationToken));
    }

    /// <summary>Scalda tutto in parallelo e ritorna quando ha finito o quando il token lo ferma.</summary>
    public static Task RunAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return RequestAnalyzerModel.WarmUpAsync(snapshot, cancellationToken);
    }
}
