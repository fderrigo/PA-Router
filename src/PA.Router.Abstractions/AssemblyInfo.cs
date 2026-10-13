using System.Runtime.CompilerServices;

// Solo il modulo Policy puo' costruire un AdmissibleTarget e solo il modulo Execution puo'
// costruire un'AuthorizedExecution. I due vincoli sono espressi qui, dove i tipi vivono, invece
// di essere affidati a una convenzione: un ranking, un adapter o un host che provasse a
// fabbricare un'autorizzazione non compilerebbe (INV-02, INV-05).
[assembly: InternalsVisibleTo("PA.Router.Policy")]
[assembly: InternalsVisibleTo("PA.Router.Execution")]
