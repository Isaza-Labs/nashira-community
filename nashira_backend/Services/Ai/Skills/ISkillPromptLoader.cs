namespace nashira_backend.Services.Ai.Skills;

// A built-in file skill from the Skills/*.md catalog (Name = path relative to
// Skills/, forward slashes).
public sealed record BuiltinSkill(string Name, string Content);

// Builds the agent system prompt from the built-in Skills/*.md catalog plus the
// uploaded AiPromptSkill rows, injecting the live tool list and current date.
public interface ISkillPromptLoader
{
    Task<string> LoadAsync(string toolList, CancellationToken ct);

    // Lists the built-in file skills that prefix the prompt (base.md first,
    // matching prompt order). They ship with the backend image.
    Task<IReadOnlyList<BuiltinSkill>> ListBuiltinsAsync(CancellationToken ct);

    // Overwrites an EXISTING built-in file skill. Returns false when the name does
    // not resolve to a .md file inside the Skills directory (traversal attempts
    // included). The prompt cache self-invalidates via the file mtime stamp.
    Task<bool> SaveBuiltinAsync(string name, string content, CancellationToken ct);

    // Drops the cached prompt after skills change (hot-reload).
    void Invalidate();
}
