namespace PA.Router.Cli.Repl;

/// <summary>UI exit intent; never kills a process or bypasses cancellation/recovery.</summary>
internal sealed class ReplInterrupt(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long? firstPress;
    internal bool ExitRequested { get; private set; }
    internal Action? CancelCurrent { get; set; }
    internal bool Armed => firstPress is long first && time.GetElapsedTime(first) <= TimeSpan.FromSeconds(2);
    internal static bool IsControlC(ConsoleKeyInfo key) => key.KeyChar == '\x03'
        || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
    internal bool Press()
    {
        ExitRequested |= Armed;
        firstPress = time.GetTimestamp();
        CancelCurrent?.Invoke();
        return ExitRequested;
    }
    internal void Activity() => firstPress = null;
    internal void Stay() { ExitRequested = false; firstPress = null; }
}

/// <summary>Console modes are local to the interactive invocation and always restored.</summary>
internal sealed class ReplTerminalScope : IDisposable
{
    private static readonly AsyncLocal<ReplTerminalScope?> slot = new();
    private readonly ReplTerminalScope? previous = slot.Value;
    private readonly bool originalControlC = Console.TreatControlCAsInput;
    private readonly TextWriter output;
    internal static ReplTerminalScope? Current => slot.Value;
    internal ReplInterrupt Interruptions { get; } = new();
    internal IKeySource Keys { get; }

    /// <summary>
    /// Su Windows i tasti si leggono dagli eventi della console, perche' e' l'unico modo di ricevere la rotella e il
    /// ridimensionamento (ADR-024); altrove, e se l'ingresso non e' una console, resta <c>Console.ReadKey</c>.
    /// </summary>
    private readonly IDisposable? native;
    internal ReplTerminalScope(TextWriter output)
    {
        this.output = output;
        IKeySource source = new ConsoleKeySource();
        if (OperatingSystem.IsWindows() && WindowsConsoleKeys.TryOpen() is { } windows) { source = windows; native = windows; }
        Keys = new BracketedPasteKeys(source);
        Console.TreatControlCAsInput = true;
        slot.Value = this;
        try { output.Write("\x1b[?2004h"); output.Flush(); }
        catch { slot.Value = previous; Console.TreatControlCAsInput = originalControlC; throw; }
    }
    public void Dispose()
    {
        try { output.Write("\x1b[?2004l"); output.Flush(); }
        finally { native?.Dispose(); slot.Value = previous; Console.TreatControlCAsInput = originalControlC; }
    }
}

/// <summary>Recognizes terminal paste boundaries. Payload is input, not editor shortcuts.</summary>
internal sealed class BracketedPasteKeys(IKeySource source) : IKeySource
{
    private readonly Queue<ConsoleKeyInfo> pending = new();
    public bool Pasting { get; private set; }
    public bool WaitedForKey { get; private set; }
    public bool KeyAvailable => pending.Count > 0 || source.KeyAvailable;
    public bool Mouse { get => source.Mouse; set => source.Mouse = value; }
    private (int Row, int Column)? decoded;
    public (int Row, int Column) Pointer => decoded ?? source.Pointer;
    public ConsoleKeyInfo Read()
    {
        while (true)
        {
            WaitedForKey = pending.Count == 0 && !source.KeyAvailable;
            ConsoleKeyInfo key = pending.Count > 0 ? pending.Dequeue() : source.Read();
            if (ReplKeys.IsSelection(key) || ReplKeys.IsSecondaryClick(key)) { decoded = null; }
            if (key.KeyChar != '\x1b' || pending.Count > 0 || !source.KeyAvailable) { return key; }
            List<ConsoleKeyInfo> sequence = [];
            string text = "";
            while (source.KeyAvailable && text.Length < 24)
            {
                ConsoleKeyInfo next = source.Read();
                sequence.Add(next);
                text += next.KeyChar;
                // La rotella di un terminale che la manda come testo (modo SGR, ADR-024): ESC [ < b ; x ; y M.
                if (text.StartsWith("[<", StringComparison.Ordinal))
                {
                    if (next.KeyChar is 'M' or 'm') { break; }
                    continue;
                }
                if (text.Length > 5) { break; }
                if (!"[200~".StartsWith(text, StringComparison.Ordinal) && !"[201~".StartsWith(text, StringComparison.Ordinal)) { break; }
                if (text == "[200~") { Pasting = true; break; }
                if (text == "[201~") { Pasting = false; break; }
            }
            if (text is "[200~" or "[201~") { continue; }
            if (TryMouse(text, out ConsoleKeyInfo? wheel, out (int Row, int Column) point))
            {
                if (wheel is { } step)
                {
                    if (ReplKeys.IsSelection(step) || ReplKeys.IsSecondaryClick(step)) { decoded = point; }
                    return step;
                }
                continue;
            }
            foreach (ConsoleKeyInfo next in sequence) { pending.Enqueue(next); }
            return key;
        }
    }

    /// <summary>
    /// Un evento del mouse in modo SGR — <c>[&lt;b;x;yM</c> — come lo mandano i terminali che passano il mouse come
    /// testo. Il bit 64 e' la rotella (il bit 1 il verso), il bit 32 il movimento; i due bit bassi dicono il tasto,
    /// e la <c>m</c> finale il rilascio. Diventano rotella, selezione o tasto destro; il resto si scarta, perche'
    /// lasciato passare diventerebbe caratteri nella richiesta. Le coordinate del terminale partono da uno.
    /// </summary>
    internal static bool TryMouse(string text, out ConsoleKeyInfo? wheel) => TryMouse(text, out wheel, out _);

    internal static bool TryMouse(string text, out ConsoleKeyInfo? wheel, out (int Row, int Column) point)
    {
        wheel = null;
        point = (0, 0);
        if (!text.StartsWith("[<", StringComparison.Ordinal) || text.Length < 4 || text[^1] is not ('M' or 'm')) { return false; }
        string[] parts = text[2..^1].Split(';');
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length != 3
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, invariant, out int button)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, invariant, out int column)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, invariant, out int row))
        {
            return true;
        }

        point = (Math.Max(0, row - 1), Math.Max(0, column - 1));
        bool release = text[^1] == 'm';

        wheel = (button & 64) != 0 ? ((button & 1) == 0 ? ReplKeys.WheelUp : ReplKeys.WheelDown)
            : (button & 3) == 2 && !release ? ReplKeys.SecondaryClick
            : (button & 3) != 0 ? null
            : release ? ReplKeys.Release
            : (button & 32) != 0 ? ReplKeys.Drag
            : ReplKeys.Press;
        return true;
    }
}
