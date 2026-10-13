using System.Diagnostics;

namespace PA.Router.Abstractions;

/// <summary>
/// Contenitore dell'albero dei processi di un programma avviato dal router.
///
/// Serve a una cosa sola: quando un turno viene annullato o supera un limite, nessun discendente
/// resta vivo. Non e' una sandbox e non limita cio' che un processo legge o scrive.
/// </summary>
public interface IProcessGuard : IDisposable
{
    /// <summary>Assegna un processo al contenitore, con i suoi discendenti.</summary>
    void Assign(Process process);

    /// <summary>Termina l'albero. Idempotente.</summary>
    void Terminate();
}

/// <summary>
/// Fabbrica dei contenitori. Dichiara se la piattaforma corrente offre davvero il controllo: un
/// adapter che richiede confinamento e non lo ottiene deve poterlo sapere, invece di dedurlo.
/// </summary>
public interface IProcessGuardFactory
{
    /// <summary>Vero se la piattaforma offre il contenimento.</summary>
    bool IsSupported { get; }

    /// <summary>Nome del meccanismo, per le evidenze.</summary>
    string Mechanism { get; }

    /// <summary>Crea un contenitore per un singolo albero.</summary>
    IProcessGuard Create();
}
