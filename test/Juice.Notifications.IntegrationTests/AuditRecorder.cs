using System.Collections.Concurrent;
using Juice.Audit;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>What the host would have sent to the audit service, kept in memory: every test host records through it (NotificationsApp).</summary>
    public sealed class AuditRecorder
    {
        public sealed record Saved(AccessLog Access, IReadOnlyList<DataAudit> Changes);

        private readonly ConcurrentQueue<Saved> _saved = new();

        public IReadOnlyList<Saved> All => [.. _saved];

        internal void Add(AccessLog access, DataAudit[] changes) => _saved.Enqueue(new Saved(access, changes));

        /// <summary>The records of one tenant whose path ends with <paramref name="pathSuffix"/>.</summary>
        public IReadOnlyList<Saved> For(Guid tenantId, string pathSuffix)
            => [.. All.Where(s => s.Access.TenantId == tenantId.ToString() && (s.Access.Request?.Path.EndsWith(pathSuffix, StringComparison.Ordinal) ?? false))];

        /// <summary>The audit service registered in place of the gRPC one.</summary>
        internal sealed class Service(AuditRecorder recorder) : IAuditService
        {
            public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries, CancellationToken token)
            {
                recorder.Add(accessLog, auditEntries);
                return Task.FromResult<IOperationResult>(OperationResult.Success);
            }
        }
    }
}
