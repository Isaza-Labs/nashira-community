namespace nashira_backend.Services.Audit;

// Opts a controller or action out of the global AuditMutationFilter. Use for auth/chat
// endpoints, dry-runs, and operations that already emit their own audit (promotion, runs).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class SkipAuditAttribute : Attribute;
