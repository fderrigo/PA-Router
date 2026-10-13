namespace PA.Router.Cli.Repl;

/// <summary>
/// Eventi del terminale che non sono tasti ma viaggiano con i tasti: la rotella e i clic del mouse, e il
/// ridimensionamento della finestra (ADR-024).
///
/// Viaggiano come <see cref="ConsoleKeyInfo"/> perche' il compositore di riga, i menu e le prove parlano
/// gia' quella lingua: un tipo nuovo li avrebbe costretti a un secondo percorso per gli stessi tasti. Si
/// usano tasti funzione che nessuna tastiera comune possiede — da F17 a F24 — e senza carattere, cosi'
/// nessun punto che li ignori li scambia per testo. Dove e' avvenuto un clic lo dice la sorgente dei tasti
/// (<see cref="IKeySource.Pointer"/>), perche' un tasto non ha posto per due coordinate.
/// </summary>
internal static class ReplKeys
{
    /// <summary>Tasto sinistro premuto: comincia una selezione.</summary>
    internal static readonly ConsoleKeyInfo Press = new('\0', ConsoleKey.F17, false, false, false);

    /// <summary>Il mouse si muove con il tasto sinistro premuto: la selezione si allarga.</summary>
    internal static readonly ConsoleKeyInfo Drag = new('\0', ConsoleKey.F18, false, false, false);

    /// <summary>Tasto sinistro rilasciato: la selezione finisce e si copia.</summary>
    internal static readonly ConsoleKeyInfo Release = new('\0', ConsoleKey.F19, false, false, false);

    /// <summary>Tasto destro: incolla, come nei terminali.</summary>
    internal static readonly ConsoleKeyInfo SecondaryClick = new('\0', ConsoleKey.F20, false, false, false);

    /// <summary>La finestra ha cambiato dimensione.</summary>
    internal static readonly ConsoleKeyInfo Resize = new('\0', ConsoleKey.F22, false, false, false);

    /// <summary>Rotella verso l'alto: indietro nella conversazione.</summary>
    internal static readonly ConsoleKeyInfo WheelUp = new('\0', ConsoleKey.F23, false, false, false);

    /// <summary>Rotella verso il basso: avanti nella conversazione.</summary>
    internal static readonly ConsoleKeyInfo WheelDown = new('\0', ConsoleKey.F24, false, false, false);

    /// <summary>Righe per un passo di rotella: tre, come nei terminali.</summary>
    internal const int WheelStep = 3;

    internal static bool IsResize(ConsoleKeyInfo key) => key.Key == ConsoleKey.F22 && key.KeyChar == '\0';

    /// <summary>Vero per un passo di rotella; <paramref name="back"/> e' vero verso l'alto.</summary>
    internal static bool IsWheel(ConsoleKeyInfo key, out bool back)
    {
        back = key.Key == ConsoleKey.F23;
        return key.KeyChar == '\0' && key.Key is ConsoleKey.F23 or ConsoleKey.F24;
    }

    /// <summary>Vero per un evento di selezione con il tasto sinistro: pressione, trascinamento o rilascio.</summary>
    internal static bool IsSelection(ConsoleKeyInfo key) =>
        key.KeyChar == '\0' && key.Key is ConsoleKey.F17 or ConsoleKey.F18 or ConsoleKey.F19;

    internal static bool IsSecondaryClick(ConsoleKeyInfo key) => key.Key == ConsoleKey.F20 && key.KeyChar == '\0';

    /// <summary>Vero per qualunque evento che non e' un tasto: nessun menu e nessun campo deve prenderlo per testo.</summary>
    internal static bool IsTerminalEvent(ConsoleKeyInfo key) =>
        key.KeyChar == '\0' && key.Key is >= ConsoleKey.F17 and <= ConsoleKey.F24;
}
