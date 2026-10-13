using PA.Router.Abstractions;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Bootstrap;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Esito della creazione di una directory dati.</summary>
/// <param name="Paths">Percorsi dello store preparato.</param>
/// <param name="Files">File dei modelli, con l'indicazione di quelli gia' presenti.</param>
/// <param name="SchemaVersion">Versione dello schema dello store dopo le migrazioni.</param>
/// <param name="Report">Che cosa risulta della configurazione appena scritta.</param>
public sealed record WorkspaceCreated(
    StorePaths Paths,
    IReadOnlyList<BootstrapFile> Files,
    int SchemaVersion,
    ConfigurationReport Report);

/// <summary>
/// Creazione di una directory dati, in un punto solo (INV-22).
///
/// Non e' la scrittura dei modelli e basta: e' una sequenza in cui **l'ordine e' la garanzia**. La
/// directory esiste prima che qualcuno ci scriva; il lock si prende prima dei modelli, perche'
/// scriverli sotto un worker che sta servendo lo store sostituirebbe la configurazione sotto i suoi
/// turni; lo schema si applica mentre la directory e' ancora posseduta, cosi' i comandi che leggono
/// lo trovano pronto invece di crearlo loro (S50); e solo alla fine si rilegge, perche' il verdetto
/// su una configurazione appena scritta va preso dai file, non da cio' che credevamo di scrivere.
///
/// Fino alla S88 questa sequenza viveva dentro <c>pa init</c>. Il configuratore guidato deve creare
/// una directory esattamente come la crea quel comando: riscrivere i cinque passi altrove avrebbe
/// prodotto due installazioni diverse a seconda di come l'operatore ci fosse arrivato, e la
/// differenza si sarebbe vista solo in campo.
/// </summary>
public static class WorkspaceCreation
{
    /// <summary>Crea la directory dati con un progetto nuovo.</summary>
    /// <param name="dataDirectory">Directory da preparare.</param>
    /// <param name="persistContent">Vero per il modo dei contenuti cifrato.</param>
    /// <param name="clock">Orologio dello store e della rilettura.</param>
    public static WorkspaceCreated Create(string dataDirectory, bool persistContent, IClock clock) =>
        Create(dataDirectory, Guid.NewGuid(), persistContent, clock);

    /// <summary>
    /// Crea la directory dati con un progetto dato.
    ///
    /// L'identificatore esplicito serve alle prove, che altrimenti non potrebbero osservare due volte
    /// la stessa installazione.
    /// </summary>
    /// <param name="dataDirectory">Directory da preparare.</param>
    /// <param name="projectId">Progetto del chatbot.</param>
    /// <param name="persistContent">Vero per il modo dei contenuti cifrato.</param>
    /// <param name="clock">Orologio dello store e della rilettura.</param>
    /// <exception cref="PaRouterException">
    /// <c>STORE_IN_USE</c> quando la directory e' gia' posseduta. Non viene catturata qui: chi ha
    /// chiesto la creazione deve sapere che non e' avvenuta, e un worker vivo e' una condizione che
    /// si corregge fermandolo, non riprovando.
    /// </exception>
    public static WorkspaceCreated Create(
        string dataDirectory,
        Guid projectId,
        bool persistContent,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        StorePaths paths = StorePaths.For(dataDirectory).EnsureCreated();

        using StoreLock ownership = StoreLock.Acquire(paths, clock);

        IReadOnlyList<BootstrapFile> files = WorkspaceTemplate.Write(paths.DataDirectory, projectId, persistContent);

        int schemaVersion = new MigrationRunner(new SqliteConnectionFactory(paths), clock).Migrate();

        return new WorkspaceCreated(
            paths,
            files,
            schemaVersion,
            ConfigurationHealth.Describe(new ConfigurationLoader(clock).Load(paths.DataDirectory), clock));
    }
}
