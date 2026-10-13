using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Gli appunti per la selezione fatta dal router (ADR-024).
///
/// Nello schermo del REPL il terminale non seleziona piu' con il mouse: i clic li riceve il router insieme alla
/// rotella. La selezione la fa quindi il router, e il testo va negli appunti da qui. Su Windows si usano gli
/// appunti di sistema, che funzionano in qualunque terminale; altrove si chiede al terminale con la sequenza
/// OSC 52, che i terminali moderni accettano. Leggere gli appunti — per incollare con il tasto destro — si sa
/// fare solo su Windows: altrove resta l'incolla del terminale, che arriva come testo incollato.
/// </summary>
internal static class ReplClipboard
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;

    /// <summary>Copia il testo; falso se non e' stato possibile.</summary>
    internal static bool Copy(string text, TextWriter terminal)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terminal);

        if (OperatingSystem.IsWindows()) { return WindowsCopy(text); }

        terminal.Write("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\u0007");
        terminal.Flush();
        return true;
    }

    /// <summary>Il testo negli appunti, se si sa leggerli e ce n'e'.</summary>
    internal static string? Read() => OperatingSystem.IsWindows() ? WindowsRead() : null;

    [SupportedOSPlatform("windows")]
    private static bool WindowsCopy(string text)
    {
        if (!Open()) { return false; }

        try
        {
            if (!EmptyClipboard()) { return false; }

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr memory = GlobalAlloc(Moveable, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) { return false; }

            IntPtr target = GlobalLock(memory);
            if (target == IntPtr.Zero) { GlobalFree(memory); return false; }

            Marshal.Copy(bytes, 0, target, bytes.Length);
            GlobalUnlock(memory);

            // Riuscita la consegna, la memoria e' degli appunti: liberarla qui sarebbe un errore.
            if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero) { GlobalFree(memory); return false; }

            return true;
        }
        finally { CloseClipboard(); }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsRead()
    {
        if (!IsClipboardFormatAvailable(UnicodeText) || !Open()) { return null; }

        try
        {
            IntPtr memory = GetClipboardData(UnicodeText);
            if (memory == IntPtr.Zero) { return null; }

            IntPtr source = GlobalLock(memory);
            if (source == IntPtr.Zero) { return null; }

            try { return Marshal.PtrToStringUni(source); }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Un altro programma puo' tenere gli appunti per un istante: si riprova qualche volta.</summary>
    [SupportedOSPlatform("windows")]
    private static bool Open()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) { return true; }
            Thread.Sleep(20);
        }

        return false;
    }

    // DllImport e non LibraryImport, per la stessa ragione di ProcessTreeGuard: il generatore del secondo richiede
    // codice unsafe, che questo repository non abilita.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
