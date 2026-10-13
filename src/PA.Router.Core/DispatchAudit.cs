using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Core;

/// <summary>Effect-boundary audit. Unlike progress audit, a failed commit must prevent the operation.</summary>
internal sealed class DispatchAudit(IExecutionStore store, IIdGenerator ids)
{
    internal ValueTask WriteAsync(string executionId, string attemptId, string code,
        CancellationToken cancellationToken, params (string Key, string Value)[] metadata) =>
        store.AppendAuditAsync(new AuditRecord(ids.NewId(), executionId, attemptId, code,
            AuditMetadata.ToJson(metadata)), cancellationToken);
}
