using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// One WorkflowSchemaValidator per schema text, for the whole test process.
//
// JsonSchema.Net registers a built schema in a PROCESS-WIDE registry keyed by its
// `$id`, and refuses to overwrite an existing registration. workflow.v1 declares an
// `$id`, so the second `new WorkflowSchemaValidator(...)` anywhere in the run throws
// `Overwriting registered schemas is not permitted` — even with identical text.
//
// That is invisible when a class is run alone (the app itself builds exactly one, as a
// singleton) and shows up only in a full-suite run, as ~30 unrelated failures in
// whichever class happened to build second. Sharing the instance keeps the registry to
// one registration and is also what production does.
internal static class TestSchemas
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, WorkflowSchemaValidator> Built = new(StringComparer.Ordinal);

    public static WorkflowSchemaValidator For(string schemaJson)
    {
        lock (Gate)
        {
            if (!Built.TryGetValue(schemaJson, out var validator))
            {
                validator = new WorkflowSchemaValidator(schemaJson);
                Built[schemaJson] = validator;
            }
            return validator;
        }
    }
}
