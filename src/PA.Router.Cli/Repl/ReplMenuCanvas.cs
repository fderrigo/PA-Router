using Spectre.Console;
using Spectre.Console.Rendering;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Dove disegna un menu del terminale: il menu dei comandi di <c>/</c> e l'elenco dei file di
/// <c>@</c>.
///
/// Con l'area fissa in fondo (ADR-023) l'elenco sta **sopra il riquadro dell'invito**, dove l'occhio
/// lo cerca: e' la stessa forma dei terminali agentici, e soprattutto e' l'unico posto in cui non si
/// contende il cursore con nessuno. Prima disegnava con il display dinamico di Spectre, che si muove
/// sullo schermo con criteri propri: compariva in mezzo alla conversazione, in posizioni diverse ogni
/// volta.
///
/// Senza area fissa — terminale senza ANSI, finestra troppo bassa — resta il display dinamico, che e'
/// il comportamento che il prodotto ha sempre avuto.
///
/// L'impaginazione la scrivono i menu, una volta sola, come righe con un ruolo: la voce scelta, cio'
/// che l'accompagna. Disegnarle e' di questa classe, in entrambi i modi.
/// </summary>
internal static class ReplMenuCanvas
{
    /// <summary>
    /// Apre il menu, esegue il suo ciclo dei tasti e lo chiude comunque vada.
    /// </summary>
    /// <param name="console">Console per il modo senza area fissa.</param>
    /// <param name="region">Area fissa, quando c'e'.</param>
    /// <param name="prompt">Invito del compositore di riga: il menu lo mostra insieme al testo.</param>
    /// <param name="typed">
    /// Che cosa c'e' scritto adesso. Il menu possiede i tasti finche' e' aperto, quindi e' **lui** a
    /// dover aggiornare la riga dell'invito: senza, il riquadro continuava a mostrare il testo di
    /// prima e cio' che si scriveva dopo <c>/</c> non compariva da nessuna parte.
    /// </param>
    /// <param name="lines">Come appare l'elenco adesso; viene richiamata a ogni ridisegno.</param>
    /// <param name="loop">Il ciclo dei tasti; riceve la funzione con cui ridisegnare.</param>
    internal static T Run<T>(
        IAnsiConsole console,
        ReplBottomRegion? region,
        string prompt,
        Func<string> typed,
        Func<IReadOnlyList<ReplBottomRegion.OverlayLine>> lines,
        Func<Action, T> loop)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(loop);

        if (region is not { Enabled: true })
        {
            // Senza area fissa l'invito non ha un posto suo: sta in cima all'elenco, come prima.
            IRenderable View() => Rows([new(prompt + typed()), .. lines()]);

            return console.Live(View()).AutoClear(true).Start(live => loop(() =>
            {
                live.UpdateTarget(View());
                live.Refresh();
            }));
        }

        try
        {
            return loop(() =>
            {
                string text = typed();
                region.Overlay = lines();
                region.Draw(prompt, text, text.Length);
            });
        }
        finally
        {
            // L'elenco sparisce con la scelta: lasciarlo sarebbe una proposta che non vale piu'.
            region.Overlay = [];
            region.Refresh();
        }
    }

    private static IRenderable Rows(IReadOnlyList<ReplBottomRegion.OverlayLine> lines) =>
        new Rows([.. lines.Select(line => new Text(
            line.Text,
            new Style(
                line.Dim ? Color.Grey : null,
                decoration: line.Selected ? Decoration.Bold : Decoration.None)))]);
}
