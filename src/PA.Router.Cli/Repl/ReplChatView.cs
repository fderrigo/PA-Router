using System.Diagnostics;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using Spectre.Console;

namespace PA.Router.Cli.Repl;

/// <summary>Chat presentation only. Released output is deferred while input owns the terminal.</summary>
internal sealed class ReplChatView(TextWriter output, TextWriter error, IAnsiConsole? console = null)
{
    private readonly object sync = new();
    private readonly Queue<Action> deferred = new();
    private readonly List<string> events = [];
    private readonly Stopwatch elapsed = new();
    /// <summary>
    /// Vero quando l'invito sta in un riquadro fisso in fondo (ADR-023). Allora l'etichetta
    /// «Risposta» non serve: sopra scorre la conversazione e sotto c'e' la riga dell'operatore, e
    /// nominare le parti ripete quello che la forma dice gia'.
    /// </summary>
    internal bool Framed { get; set; }

    private int inputDepth;
    private bool activity;
    private bool answer;
    private bool lineStart = true;
    private string? pendingPhase;
    private readonly IAnsiConsole? diagnostics = console is null ? null : AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = console.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No,
        ColorSystem = ColorSystemSupport.Detect,
        Out = new AnsiConsoleOutput(error),
    });

    internal void Render(Action action)
    {
        lock (sync)
        {
            if (inputDepth > 0) { deferred.Enqueue(action); return; }
            ClearActivity();
            action();
        }
    }

    internal void Welcome(string profile, string location = "") => Render(() =>
    {
        output.WriteLine();
        Label("PA Router", ReplChatStyle.Accent);
        output.WriteLine(Ui.Text("profile.session") + ": " + SafeText.Inline(profile));
        if (location.Length > 0) { Secondary(Ui.Text("esecuzione") + ": " + location); }
        Secondary(Ui.Text("chat.hint"));
        output.WriteLine();
    });

    internal void Command(string command) => Render(() =>
    {
        FinishAnswer();
        output.WriteLine();
        Label(Ui.Text("chat.command") + " " + SafeText.Inline(command.Split(' ', 2)[0]), ReplChatStyle.Accent);
    });

    internal void BeginTurn() => Render(() =>
    {
        FinishAnswer();
        answer = false;
        events.Clear();
        elapsed.Restart();
    });

    internal void EndTurn() => Render(() =>
    {
        FinishAnswer();
        FlushEvents();
        elapsed.Stop();
        pendingPhase = null;
    });

    internal void BeginInput()
    {
        lock (sync)
        {
            ClearActivity();
            if (!lineStart) { output.WriteLine(); lineStart = true; }
            inputDepth++;
        }
    }

    internal void EndInput()
    {
        lock (sync)
        {
            inputDepth = Math.Max(0, inputDepth - 1);
            if (inputDepth != 0) { return; }
            while (deferred.TryDequeue(out Action? action)) { ClearActivity(); action(); }
            if (pendingPhase is { } phase && !answer) { DrawPhase(phase); }
        }
    }

    internal void Phase(string message)
    {
        lock (sync)
        {
            pendingPhase = SafeText.Inline(message);
            if (inputDepth == 0 && !answer) { DrawPhase(pendingPhase); }
        }
    }

    private void DrawPhase(string message)
    {
        // Carriage-return redraw of one bounded row: never move above the current region.
        if (console is null || !console.Profile.Capabilities.Ansi) { error.WriteLine("... " + message); return; }
        ClearActivity();
        int width = Math.Max(8, console.Profile.Width - 2);
        console.Write(new Text(Output.Layout.Truncate(Ui.Text("chat.progress") + " - " + message, width), new Style(ReplChatStyle.Accent)));
        activity = true;
    }

    private void ClearActivity()
    {
        if (!activity) { return; }
        output.Write("\r\u001b[2K");
        activity = false;
    }

    internal void Event(string message) => Render(() =>
    {
        string safe = SafeText.Inline(message);
        if (answer) { events.Add(safe); }
        else { Secondary(safe); }
    });

    internal void Answer(string text) => Render(() =>
    {
        string safe = SafeText.Sanitize(text);
        if (safe.Length == 0) { return; }
        pendingPhase = null;
        if (!answer)
        {
            output.WriteLine();
            if (!Framed) { Label(Ui.Text("chat.answer"), ReplChatStyle.Response); }
            answer = true;
            lineStart = true;
        }
        // Preserve indentation and chunk boundaries. Only the renderer adds the margin.
        foreach (char character in safe)
        {
            if (lineStart) { output.Write("  "); lineStart = false; }
            output.Write(character);
            if (character == '\n') { lineStart = true; }
        }
        output.Flush();
    });

    internal void Outcome(string state)
    {
        // Capture execution time now, not when a pending keyboard edit releases the terminal.
        string duration;
        lock (sync)
        {
            duration = elapsed.IsRunning ? " - " + elapsed.Elapsed.TotalSeconds.ToString("0.0", Formats.Invariant) + " s" : "";
        }
        Render(() =>
        {
            FinishAnswer();
            FlushEvents();
            string label = state == ExecutionState.Completed.ToWire() ? Ui.Text("chat.completed")
                : state == ExecutionState.Denied.ToWire() ? Ui.Text("chat.blocked") : Ui.Text("chat.notCompleted");
            Color color = state == ExecutionState.Completed.ToWire() ? ReplChatStyle.Success : ReplChatStyle.Error;
            Label(label + duration, color);
            pendingPhase = null;
        });
    }

    internal void Message(string message) => Render(() =>
    {
        if (answer && !lineStart) { events.Add(SafeText.Inline(message)); }
        else { output.WriteLine(SafeText.Sanitize(message)); }
    });

    internal void Warning(string message, bool blocking = false) => Render(() =>
    {
        FinishAnswer();
        string label = Ui.Text(blocking ? "chat.error" : "chat.warning") + ": ";
        if (diagnostics is null) { error.Write(label); }
        else { diagnostics.Write(new Text(label, new Style(blocking ? ReplChatStyle.Error : ReplChatStyle.Warning, decoration: Decoration.Bold))); }
        error.WriteLine(SafeText.Inline(message));
    });

    internal void Confirmation(string question, Action table) => Render(() =>
    {
        FinishAnswer();
        output.WriteLine();
        Label(Ui.Text("chat.confirmation"), ReplChatStyle.Warning);
        output.WriteLine(SafeText.Inline(question));
        table();
        output.WriteLine(Ui.Text("Confermi? (si / una volta / no)"));
    });

    private void FinishAnswer()
    {
        if (!lineStart) { output.WriteLine(); lineStart = true; }
    }

    private void FlushEvents()
    {
        foreach (string message in events) { Secondary(message); }
        events.Clear();
    }

    private void Secondary(string text)
    {
        if (console is null) { output.WriteLine(SafeText.Inline(text)); }
        else { console.Write(new Text(SafeText.Inline(text) + Environment.NewLine, new Style(ReplChatStyle.Secondary))); }
    }

    private void Label(string text, Color color)
    {
        if (console is null) { output.WriteLine(SafeText.Inline(text)); }
        else { console.Write(new Text(SafeText.Inline(text) + Environment.NewLine, new Style(color, decoration: Decoration.Bold))); }
    }
}

/// <summary>Chat roles only; setup readiness retains its separate meaning.</summary>
internal static class ReplChatStyle
{
    internal static readonly Color Accent = new(0, 128, 128);
    internal static readonly Color Response = new(175, 95, 0);
    internal static readonly Color Secondary = Color.Grey;
    internal static readonly Color Success = Color.Green;
    internal static readonly Color Warning = Color.Yellow;
    internal static readonly Color Error = Color.Red1;
}
