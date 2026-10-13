using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Detectors;

/// <summary>
/// Un detector locale. Deterministico e in-process: nessun modello, nessuna rete, nessun
/// addestramento sulle richieste dell'utente (03-specs/PRIVACY.md).
///
/// Un detector non classifica il payload: dichiara la classe che il proprio tipo di match
/// comporta, e la pipeline prende il massimo. Nessun detector puo' abbassare una classe.
/// </summary>
public interface IPrivacyDetector
{
    /// <summary>Nome stabile, usato nelle diagnosi e nei report di scansione incompleta.</summary>
    string Name { get; }

    /// <summary>Versione del detector, registrata in ogni finding.</summary>
    string Version { get; }

    /// <summary>Tipo di finding prodotto.</summary>
    FindingType Type { get; }

    /// <summary>Classe minima che un match di questo tipo comporta.</summary>
    DataClass ClassContribution { get; }

    /// <summary>
    /// Cerca i match nel testo e li aggiunge, con span in byte, a <paramref name="findings"/>.
    /// Un'implementazione che non riesce a concludere entro il tempo concesso deve lasciar
    /// propagare l'eccezione di timeout: la pipeline la traduce in scansione incompleta.
    /// </summary>
    void Scan(string text, ByteOffsetMap offsets, List<PrivacyFinding> findings);
}
