using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace nashira_backend.Services.Ai.Skills;

// A built-in file skill that is loaded on demand: Name is its path relative to
// Skills/ (the name SkillPromptLoader and ModuleContentCatalog use), LoadKey the id
// it is remembered by in a conversation's loaded set.
public sealed record TopicSkill(string Name, string Summary, string Content, Guid LoadKey);

// The built-in skills that ride only when their topic is in play.
//
// Every built-in skill used to go into every turn: some 110 KB of Markdown, around
// 30k tokens, re-sent on every tool round of every turn whether the conversation
// was about building a workflow or about who is logged in. Most of it documents one
// area each — snippets, git, inventory, email — so it gets the scheme integration
// skills already have (ScopedSkillCatalog): one index line per topic in the prompt,
// the text only when the topic is in play, which is when
//
//   1. the user's message names it (the keywords below, English and Spanish);
//   2. it was loaded earlier in the same conversation (the same persisted set as
//      integration skills; a topic's key is derived from its name);
//   3. one of its tools is called (the text lands right behind that call's result,
//      so the next call is made with the rules in view);
//   4. the model asks for it with load_skill.
//
// What stays in every turn: base.md (identity, tool list) and nashira.md (the map
// of the platform), plus every uploaded AiPromptSkill row — those are an
// operator's, and nothing here knows what they cover. Module filtering still
// applies first: a skill of a disabled capability is neither in the prompt nor a
// topic (ModuleContentCatalog).
public static class TopicSkillCatalog
{
    private sealed record Definition(string Summary, Regex? Mentions, FrozenSet<string> Tools);

    private static Definition Def(string summary, string[] keywords, params string[] tools)
        => new(
            summary,
            keywords.Length == 0
                ? null
                // Each keyword matches at the start of a word, so "workflow" also finds
                // "workflows" and "aprobaci" finds "aprobación"; it never matches inside
                // a word ("rol" does not fire on "control"). A trailing `$` asks for the
                // whole word, for the short ones that begin longer unrelated words
                // ("repo$" must not fire on "report").
                : new Regex(
                    @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", keywords.Select(Pattern)) + ")",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            tools.ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    private static string Pattern(string keyword)
        => keyword.EndsWith('$')
            ? Regex.Escape(keyword[..^1]) + @"(?![\p{L}\p{N}_])"
            : Regex.Escape(keyword);

    private static readonly string[] SnippetKeywords =
        ["snippet", "workflow", "flujo", "automatiz", "step type", "tipo de paso", "nodo$", "nodos$", "node$", "nodes$"];
    private static readonly string[] SnippetTools = ["create_snippet", "create_workflow", "update_workflow"];

    private static readonly string[] GitKeywords =
        ["git", "github", "gitlab", "bitbucket", "repo$", "repos$", "repositor", "commit", "push",
         "branch", "rama$", "ramas$", "pull request"];
    private static readonly string[] GitTools =
        ["git_checkout", "git_commit_push", "git_create_webhook", "git_diff", "git_list_branches", "git_list_files",
         "git_list_repositories", "git_list_webhooks", "git_pull", "git_read_file", "git_status", "git_write_file",
         "github_create_pr", "github_create_repo", "github_merge_pr"];

    private static readonly FrozenDictionary<string, Definition> Definitions =
        new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflows.md"] = Def(
                "authoring, simulating and promoting workflows",
                ["workflow", "flujo", "automatiz", "automation", "automate", "playbook", "pipeline", "orquest", "orchestrat"],
                "create_workflow", "update_workflow", "simulate_workflow", "promote_workflow"),
            // The two snippet guides are alternatives (ModuleContentCatalog keeps one):
            // the full one, or the automation-only one for deployments without the
            // capabilities the full one documents.
            ["snippets.md"] = Def(
                "the step types a workflow node can be, and how to configure each", SnippetKeywords, SnippetTools),
            ["snippets-automation.md"] = Def(
                "the automation-owned step types a workflow node can be", SnippetKeywords, SnippetTools),
            ["mermaid.md"] = Def(
                "authoring the `logic_diagram_mermaid` field of workflows and snippets",
                ["mermaid", "diagram", "diagrama", "workflow", "flujo", "snippet"],
                "create_workflow", "update_workflow", "create_snippet"),
            ["runs.md"] = Def(
                "reading a workflow execution after the fact",
                ["runs$", "run_id", "ejecuci", "execution", "corrida", "step log"],
                "run_workflow"),
            ["troubleshooting.md"] = Def(
                "the failures that actually happen and what each one means",
                ["error", "falla", "fallo", "falló", "fail", "troubleshoot", "problema", "debug", "no funciona", "broken", "roto"]),
            ["git.md"] = Def(
                "repositories, branches and GitHub, and the git workflow node", GitKeywords, GitTools),
            ["git-direct.md"] = Def(
                "direct repository operations: read, write, commit, branches", GitKeywords, GitTools),
            ["exports.md"] = Def(
                "handing results out: exports (xlsx, csv, docx, pdf), saved reports and dashboards",
                ["export", "report", "informe", "reporte", "excel", "xlsx", "csv", "pdf", "docx", "dashboard"],
                "export_document", "export_table", "save_report", "read_report", "list_reports", "bulk_delete_reports"),
            ["inventory.md"] = Def(
                "devices, pools, inventory sources, and reaching a device",
                ["inventor", "device", "dispositiv", "pool", "netbox", "router", "switch", "ping$", "ssh"],
                "create_device", "update_device", "delete_device", "bulk_delete_devices", "query_devices",
                "device_connect", "device_ping", "create_inventory_source", "update_inventory_source",
                "delete_inventory_source", "list_inventory_sources", "sync_netbox_inventory"),
            ["administration.md"] = Def(
                "the model you run on (AI providers), personas and profiles, skills, and template checks",
                ["proveedor", "provider", "llm", "persona", "profile", "perfil", "template", "plantilla", "skill"],
                "create_ai_provider", "update_ai_provider", "delete_ai_provider", "list_ai_providers",
                "create_profile", "update_profile", "delete_profile", "assign_profile", "list_profiles",
                "validate_template", "list_validations", "create_skill", "update_skill", "delete_skill",
                "get_skill", "list_skills"),
            ["integrations.md"] = Def(
                "integrations and API specs: calling an external system",
                ["integration", "integraci", "spec$", "specs$", "openapi", "swagger", "external api", "api externa"],
                "list_integrations", "create_spec", "update_spec", "delete_spec", "get_spec", "list_specs",
                "discover_operations"),
            ["mcp.md"] = Def(
                "MCP servers: someone else's tools, someone else's credentials",
                ["mcp", "model context protocol"],
                "list_mcp_tools", "mcp_call"),
            ["governance.md"] = Def(
                "roles, autonomy, permissions, policies and audit",
                ["govern", "gobern", "permis", "permission", "rbac", "role", "rol$", "roles$", "policy", "policies",
                 "polític", "audit", "autonom", "aprobaci", "approv"],
                "set_user_permissions", "get_user_permissions", "list_permission_domains", "list_audit_events",
                "get_audit_event", "verify_audit", "create_user", "update_user", "delete_user", "list_users"),
            ["knowledge.md"] = Def(
                "knowledge articles and learnings: two stores, two purposes",
                ["knowledge", "conocimiento", "artículo", "articulo", "article", "learning", "aprendizaje", "lección", "lesson"],
                "search_knowledge", "create_knowledge_article", "update_knowledge_article", "delete_knowledge_article",
                "get_knowledge_article", "create_learning", "update_learning", "delete_learning", "list_learnings"),
            ["email.md"] = Def(
                "email mailboxes: reading and managing mail",
                ["email", "e-mail", "correo", "mail", "buzón", "buzon", "mailbox", "inbox", "bandeja"],
                "list_emails", "read_email", "send_email", "move_email", "mark_email", "archive_email",
                "delete_email", "list_email_folders"),
            ["secrets.md"] = Def(
                "secrets and credentials: reusable sensitive material",
                ["secret", "secreto", "credential", "credencial", "password", "contraseña", "token$", "tokens$", "api key"],
                "set_secret", "clear_secret", "list_secrets", "create_credential", "update_credential",
                "delete_credential", "list_credentials"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool IsTopic(string name) => Definitions.ContainsKey(name);

    // The topic for a built-in skill, or null when it is not one of the on-demand ones.
    public static TopicSkill? From(string name, string content)
        => Definitions.TryGetValue(name, out var d) ? new TopicSkill(name, d.Summary, content, LoadKeyFor(name)) : null;

    // A topic's id in a conversation's loaded-skill set, which already holds
    // integration ids. Derived from the name, so the same file always has the same key.
    public static Guid LoadKeyFor(string name)
        => new(MD5.HashData(Encoding.UTF8.GetBytes("topic-skill:" + name.Trim().ToLowerInvariant())));

    // Topics whose keywords appear in the text.
    public static IReadOnlySet<Guid> MatchMentions(string? text, IReadOnlyList<TopicSkill> topics)
    {
        var hits = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        foreach (var t in topics)
            if (Definitions.TryGetValue(t.Name, out var d) && d.Mentions?.IsMatch(text) == true)
                hits.Add(t.LoadKey);
        return hits;
    }

    // Topics a call to this tool puts in play.
    public static IReadOnlyList<TopicSkill> ForTool(string toolName, IReadOnlyList<TopicSkill> topics)
        => topics.Where(t => Definitions.TryGetValue(t.Name, out var d) && d.Tools.Contains(toolName)).ToList();

    // The topic a load_skill argument names: "workflows.md", "workflows" or
    // "Skills/workflows.md", the three ways the skills refer to one another.
    public static TopicSkill? Find(string name, IReadOnlyList<TopicSkill> topics)
    {
        var key = name.Trim().Replace('\\', '/');
        if (key.StartsWith("Skills/", StringComparison.OrdinalIgnoreCase)) key = key["Skills/".Length..];
        if (!key.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) key += ".md";
        return topics.FirstOrDefault(t => t.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // The topic part of the prompt: the index, then the text of every loaded topic.
    // Empty when there are none.
    public static string Render(IReadOnlyList<TopicSkill> topics, IReadOnlySet<Guid> loaded)
    {
        if (topics.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("## Topic skills\n\n");
        sb.Append("Detailed guides, one per area, loaded only when their topic is in play so this prompt ");
        sb.Append("stays small. A reference elsewhere in your instructions to `Skills/<name>.md` or ");
        sb.Append("`<name>.md` points at one of these. Available:\n\n");
        foreach (var t in topics)
        {
            var state = loaded.Contains(t.LoadKey) ? "loaded — its text follows below" : "not loaded";
            sb.Append("- `").Append(t.Name).Append("`: ").Append(t.Summary).Append(" — ").Append(state).Append('\n');
        }
        sb.Append('\n');
        sb.Append("A topic skill loads by itself when the user's message names its topic or when you call one ");
        sb.Append("of its tools, and stays loaded for the rest of the conversation. Before you work in one of ");
        sb.Append("these areas — above all before creating or changing anything — load its skill with ");
        sb.Append("`load_skill` if it is not loaded yet: it holds the rules and shapes that area expects, and ");
        sb.Append("work done without it tends to be wrong.");

        foreach (var t in topics)
            if (loaded.Contains(t.LoadKey))
                sb.Append("\n\n---\n\n").Append(RenderLoaded(t));
        return sb.ToString();
    }

    public static string RenderLoaded(TopicSkill t) => $"[Topic skill `{t.Name}`]\n\n{t.Content}";
}
