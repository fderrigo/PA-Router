using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Tasti, rotella e ridimensionamento letti dall'input della console di Windows (ADR-024).
///
/// <c>Console.ReadKey</c> scarta gli eventi del mouse: con lui la rotella non arriva mai. Questa classe
/// legge gli eventi della console direttamente e li traduce nelle stesse strutture di prima — un tasto e'
/// lo stesso <see cref="ConsoleKeyInfo"/> che darebbe <c>Console.ReadKey</c>, la rotella e il
/// ridimensionamento sono i tasti convenzionali di <see cref="ReplKeys"/>.
///
/// Il mouse si chiede alla console solo mentre serve (<see cref="Mouse"/>): quando il router lo riceve, il
/// terminale non lo usa piu' per selezionare, e un REPL senza schermo alternativo non ha niente da scorrere
/// che giustifichi quel prezzo. La modalita' originale della console torna alla chiusura.
/// </summary>
internal sealed class WindowsConsoleKeys : IKeySource, IDisposable
{
    private const int StandardInput = -10;

    private const ushort KeyEvent = 0x0001;
    private const ushort MouseEvent = 0x0002;
    private const ushort WindowBufferSizeEvent = 0x0004;

    private const uint MouseMoved = 0x0001;
    private const uint DoubleClick = 0x0002;
    private const uint MouseWheeled = 0x0004;
    private const uint LeftButton = 0x0001;
    private const uint RightButton = 0x0002;

    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableWindowInput = 0x0008;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    private const uint RightAltPressed = 0x0001;
    private const uint LeftAltPressed = 0x0002;
    private const uint RightCtrlPressed = 0x0004;
    private const uint LeftCtrlPressed = 0x0008;
    private const uint ShiftPressed = 0x0010;

    private readonly IntPtr handle;
    private readonly uint original;
    private readonly Queue<ConsoleKeyInfo> pending = new();
    private readonly Queue<(int Row, int Column)> points = new();
    private bool mouse;
    private bool dragging;

    private WindowsConsoleKeys(IntPtr handle, uint original)
    {
        this.handle = handle;
        this.original = original;
        Apply();
    }

    /// <summary>
    /// Il lettore, se l'ingresso e' davvero una console. Un ingresso rediretto — una pipe, un file — non
    /// ha eventi da leggere, e li' resta <c>Console.ReadKey</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static WindowsConsoleKeys? TryOpen()
    {
        if (Console.IsInputRedirected) { return null; }

        IntPtr input = GetStdHandle(StandardInput);

        return input != IntPtr.Zero && input != new IntPtr(-1) && GetConsoleMode(input, out uint mode)
            ? new WindowsConsoleKeys(input, mode)
            : null;
    }

    /// <summary>Vero mentre il router riceve la rotella; lo decide lo schermo alternativo.</summary>
    public bool Mouse
    {
        get => mouse;
        set
        {
            if (mouse == value) { return; }
            mouse = value;
            Apply();
        }
    }

    /// <inheritdoc />
    public (int Row, int Column) Pointer { get; private set; }

    /// <inheritdoc />
    public bool KeyAvailable
    {
        get
        {
            if (pending.Count > 0) { return true; }

            // Si legge solo cio' che e' gia' arrivato, quindi non si attende. Gli eventi che non diventano un
            // tasto — un rilascio, un movimento del mouse — si consumano qui: lasciati in coda farebbero
            // credere che qualcuno stia scrivendo. Quelli che lo diventano restano in `pending`, una volta sola.
            while (GetNumberOfConsoleInputEvents(handle, out uint count) && count > 0
                && ReadConsoleInputW(handle, out InputRecord record, 1, out uint read) && read == 1)
            {
                if (Take(record) > 0) { return true; }
            }

            return pending.Count > 0;
        }
    }

    /// <inheritdoc />
    public ConsoleKeyInfo Read()
    {
        while (pending.Count == 0)
        {
            if (!ReadConsoleInputW(handle, out InputRecord record, 1, out uint read) || read == 0)
            {
                // La console non risponde piu': la lettura di sempre sa come comportarsi.
                return Console.ReadKey(intercept: true);
            }

            _ = Take(record);
        }

        ConsoleKeyInfo key = pending.Dequeue();
        if (ReplKeys.IsSelection(key) || ReplKeys.IsSecondaryClick(key)) { Pointer = points.Dequeue(); }
        return key;
    }

    public void Dispose() => SetConsoleMode(handle, original);

    /// <summary>Traduce un evento, con i clic del mouse e la loro posizione.</summary>
    private int Take(InputRecord record)
    {
        if (mouse && Click(record, ref dragging) is { } click)
        {
            pending.Enqueue(click);
            points.Enqueue((record.Mouse.Y, record.Mouse.X));
            return 1;
        }

        return Translate(record, pending, mouse);
    }

    /// <summary>
    /// Un evento del mouse come selezione: il tasto sinistro premuto la comincia, il movimento con il tasto
    /// premuto la allarga, il rilascio la chiude. Il tasto destro incolla. Il doppio clic si tratta come una
    /// pressione: selezionare una parola sola non e' ancora fatto.
    /// </summary>
    internal static ConsoleKeyInfo? Click(InputRecord record, ref bool dragging)
    {
        if (record.EventType != MouseEvent || (record.Mouse.EventFlags & MouseWheeled) != 0) { return null; }

        uint buttons = record.Mouse.ButtonState & 0xFFFF;
        uint flags = record.Mouse.EventFlags;

        if ((flags & MouseMoved) != 0)
        {
            return dragging && (buttons & LeftButton) != 0 ? ReplKeys.Drag : null;
        }

        if (flags is 0 or DoubleClick)
        {
            if ((buttons & LeftButton) != 0 && !dragging) { dragging = true; return ReplKeys.Press; }
            if ((buttons & LeftButton) == 0 && dragging) { dragging = false; return ReplKeys.Release; }
            if ((buttons & RightButton) != 0) { return ReplKeys.SecondaryClick; }
        }

        return null;
    }

    /// <summary>
    /// Il modo di partenza e' letto prima che <c>Console.TreatControlCAsInput</c> tolga l'elaborazione di Ctrl+C:
    /// rimetterlo com'era trasformava di nuovo Ctrl+C in un segnale, e il processo si chiudeva al primo colpo invece di
    /// chiedere il secondo. Finche' questo lettore e' aperto, Ctrl+C e' un tasto.
    /// </summary>
    private void Apply() => _ = SetConsoleMode(handle, ModeFor(original, mouse));

    internal static uint ModeFor(uint original, bool mouse)
    {
        uint mode = (original | EnableWindowInput | EnableExtendedFlags) & ~EnableProcessedInput;
        return mouse ? (mode | EnableMouseInput) & ~EnableQuickEditMode : mode & ~EnableMouseInput;
    }

    /// <summary>
    /// Traduce un evento della console e ne accoda i tasti. E' la stessa regola di <c>Console.ReadKey</c>:
    /// solo le pressioni, mai i soli tasti modificatori, e un carattere composto con Alt e il tastierino
    /// arriva al rilascio di Alt. Restituisce quanti tasti ha accodato.
    ///
    /// Rotella e ridimensionamento diventano tasti solo con lo schermo del REPL (<paramref name="screen"/>): il
    /// configuratore legge dalla stessa sorgente e non saprebbe che farne.
    /// </summary>
    internal static int Translate(InputRecord record, Queue<ConsoleKeyInfo> into, bool screen = true)
    {
        ArgumentNullException.ThrowIfNull(into);

        switch (record.EventType)
        {
            case WindowBufferSizeEvent when screen:
                into.Enqueue(ReplKeys.Resize);
                return 1;

            case MouseEvent when screen && (record.Mouse.EventFlags & MouseWheeled) != 0:
                // Il verso sta nella parola alta dello stato dei pulsanti, con segno.
                into.Enqueue((short)(record.Mouse.ButtonState >> 16) > 0 ? ReplKeys.WheelUp : ReplKeys.WheelDown);
                return 1;

            case KeyEvent:
                KeyEventRecord key = record.Key;
                bool altComposition = key.KeyDown == 0 && key.VirtualKeyCode == 0x12 && key.UnicodeChar != 0;

                if ((key.KeyDown == 0 && !altComposition) || (key.KeyDown != 0 && IsModifier(key.VirtualKeyCode))) { return 0; }

                uint state = key.ControlKeyState;
                ConsoleKeyInfo info = new(
                    (char)key.UnicodeChar,
                    altComposition ? 0 : (ConsoleKey)key.VirtualKeyCode,
                    (state & ShiftPressed) != 0,
                    !altComposition && (state & (LeftAltPressed | RightAltPressed)) != 0,
                    (state & (LeftCtrlPressed | RightCtrlPressed)) != 0);
                int repeat = Math.Max(1, (int)key.RepeatCount);

                for (int index = 0; index < repeat; index++) { into.Enqueue(info); }

                return repeat;

            default:
                return 0;
        }
    }

    /// <summary>Maiuscole, Ctrl, Alt, blocchi e tasti Windows: da soli non sono un tasto per chi scrive.</summary>
    private static bool IsModifier(ushort code) => code is 0x10 or 0x11 or 0x12 or 0x14 or 0x90 or 0x91 or 0x5B or 0x5C;

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord Key;
        [FieldOffset(4)] public MouseEventRecord Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseEventRecord
    {
        public short X;
        public short Y;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    // DllImport e non LibraryImport, per la stessa ragione di ProcessTreeGuard: il generatore del secondo
    // richiede codice unsafe, che questo repository non abilita.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr console, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr console, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfConsoleInputEvents(IntPtr console, out uint count);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleInputW(IntPtr console, out InputRecord buffer, uint length, out uint read);

}
