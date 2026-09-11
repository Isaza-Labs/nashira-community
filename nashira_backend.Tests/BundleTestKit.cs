using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// Shared wiring for the bundle test suites: a real WorkflowValidator (the same
// workflow.v1 schema the app loads) and a real WorkflowReferenceChecker, because the
// importer's promise is that it runs the SAME gates as a hand-authored write.
internal static class BundleTestKit
{
    private static readonly Lazy<WorkflowValidator> Validator = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conformance", "schema", "workflow.v1.schema.json");
        // Shared instance: JsonSchema.Net's schema registry is process-wide and
        // refuses a second registration of the same `$id` — see TestSchemas.
        return new WorkflowValidator(TestSchemas.For(File.ReadAllText(path)));
    });

    public static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bundle-{Guid.NewGuid()}")
            .Options);

    public static WorkflowBundleService NewService(
        AppDbContext db, ICurrentUser user, ISnippetHandlerRegistry registry) =>
        new(db, user, registry, Validator.Value, new WorkflowReferenceChecker(db, registry), new FakeProtector(),
            NullLogger<WorkflowBundleService>.Instance);

    // Reversible and obviously not encryption: the tests only need "the stored bytes
    // are not the plaintext" and "two fresh secrets differ".
    public sealed class FakeProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) =>
            plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext).Reverse().ToArray();

        public string? Decrypt(byte[]? ciphertext) =>
            ciphertext is null ? null : Encoding.UTF8.GetString(ciphertext.Reverse().ToArray());
    }
}
