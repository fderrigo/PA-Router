namespace PA.Router.Cli.Repl;

/// <summary>
/// La gabbia di una schermata del setup (S111): dove sei, perche' sei qui, che cosa e' appena successo.
///
/// Esiste perche' posizione e spiegazione erano due stringhe libere passate a mano da ogni metodo, e ogni
/// schermata le metteva dove capitava. Qui sono campi con un nome, e un solo punto le disegna, sempre nello
/// stesso ordine: percorso, titolo, perche', esito dell'ultima azione, scelta, aiuto, tasti.
///
/// <see cref="Why"/> e' obbligatorio: una schermata che non dice che cosa succede scegliendo non guida.
/// </summary>
/// <param name="Path">Percorso dalla home, separato da <c> &gt; </c>.</param>
/// <param name="Why">Che cosa si decide qui e che cosa succede dopo, nella lingua di chi configura.</param>
internal sealed record SetupPage(string Path, string Why)
{
    /// <summary>Esito dell'ultima azione. Non fa parte della spiegazione: compare su una riga propria.</summary>
    public string Notice { get; init; } = "";

    /// <summary>Vero se la copia di lavoro ha modifiche non salvate.</summary>
    public bool Dirty { get; init; }

    /// <summary>Passo della guida, quando la schermata ne fa parte: «Passo 3 di 6».</summary>
    public string? Step { get; init; }

    /// <summary>Quante cose obbligatorie mancano ancora; null quando non e' il posto per dirlo.</summary>
    public int? Missing { get; init; }

    /// <summary>
    /// La forma storica, per le rese che non disegnano una regione: esito e spiegazione su una riga.
    /// </summary>
    internal string Context => string.IsNullOrEmpty(Notice) ? Why : Notice + " | " + Why;

    /// <summary>Rifiuta una schermata senza percorso o senza spiegazione.</summary>
    internal SetupPage Checked()
    {
        if (string.IsNullOrWhiteSpace(Path) || string.IsNullOrWhiteSpace(Why))
        {
            throw new InvalidProgramException("Una schermata del setup deve dichiarare percorso e spiegazione: " + Path);
        }

        return this;
    }
}
