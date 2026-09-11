using System.Collections.Frozen;
using System.Collections.ObjectModel;
using nashira_backend.Configuration.Modules;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Auth;

namespace nashira_backend.Services.Ai.Seed;

// Which capability each boot-time seeder belongs to, and therefore which ones run.
//
// Seeding is not registration: it writes rows. A seeder of a disabled module would
// install a baseline for something the deployment cannot reach — vendor commands with
// no devices, an import allowlist for a sandbox nothing can invoke — and the operator
// would find the tables of a capability they switched off filling up on every boot.
//
// Nothing here deletes. Turning a module off leaves its rows where they are, and turning
// it back on runs its seeder again over them: every one is idempotent by key, so the
// baseline is restored without duplicating what an operator kept or resurrecting what
// they removed. Migrations stay global — the schema is one thing, its contents another.
//
// The delegates exist because each seeder has its own signature; centralising the
// invocation is what makes it impossible for a call site to forget the guard.
public static class SeederModuleCatalog
{
    private static readonly ReadOnlyCollection<SeederRegistration> Registrations =
        Array.AsReadOnly<SeederRegistration>(
        [
            // ── core ──────────────────────────────────────────────────────────
            // A deterministic admin so a fresh dev database is usable at all. Runs in
            // every valid selection, because a deployment nobody can sign in to is not
            // a smaller deployment, it is a broken one.
            new(
                typeof(DevSeedService),
                [ModuleId.Core],
                DevelopmentOnly: true,
                (services, logger, _) =>
                    DevSeedService.SeedAsync(services.GetRequiredService<IServiceScopeFactory>(), logger)),

            // ── ai-studio ─────────────────────────────────────────────────────
            // Curated system knowledge for self-correction.
            new(
                typeof(LearningsSeedService),
                [ModuleId.AiStudio],
                DevelopmentOnly: false,
                (services, logger, ct) => LearningsSeedService.SeedSystemKnowledgeAsync(
                    services.GetRequiredService<IServiceScopeFactory>(), logger, ct)),
            // Built-in OpenAPI specs (Specs/*.yaml) -> ai_api_specs, so the agent's
            // discover/detail/execute catalogue is populated on a fresh install.
            // Per-api idempotent: an admin's edits are never overwritten.
            new(
                typeof(BuiltinSpecSeeder),
                [ModuleId.AiStudio],
                DevelopmentOnly: false,
                (services, logger, ct) => BuiltinSpecSeeder.SeedAsync(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    services.GetRequiredService<IWebHostEnvironment>(),
                    services.GetRequiredService<IApiSpecIndex>(),
                    services.GetRequiredService<ModuleSelection>(),
                    logger,
                    ct)),

            // ── knowledge ─────────────────────────────────────────────────────
            new(
                typeof(KnowledgeSeedService),
                [ModuleId.Knowledge],
                DevelopmentOnly: false,
                (services, logger, ct) => KnowledgeSeedService.SeedAsync(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    services.GetRequiredService<IWebHostEnvironment>(),
                    logger,
                    ct)),

            // ── automation ────────────────────────────────────────────────────
            // Baseline python_snippet import allowlist (computational stdlib only).
            // Admin deletions survive a redeploy.
            new(
                typeof(PythonModuleSeeder),
                [ModuleId.Automation],
                DevelopmentOnly: false,
                (services, logger, ct) => PythonModuleSeeder.SeedAsync(
                    services.GetRequiredService<IServiceScopeFactory>(), logger, ct)),
            // Baseline snippet catalogue: one runnable snippet per registered handler.
            // It reads the registry rather than a list of its own, so a deployment
            // without git seeds no git snippet without this catalog having to say so
            // twice. Idempotent by slug.
            new(
                typeof(DefaultSnippetSeeder),
                [ModuleId.Automation],
                DevelopmentOnly: false,
                (services, logger, ct) => DefaultSnippetSeeder.SeedAsync(
                    services.GetRequiredService<IServiceScopeFactory>(), logger, ct)),

            // ── fleet ─────────────────────────────────────────────────────────
            // Vendor command catalogues (Skills/vendors/*.yaml) -> vendor_commands, so
            // /api/vendor-commands/resolve answers on a fresh install instead of 404ing
            // every intent. Idempotent per (intent, platform), soft-deleted rows
            // included: an operator's removal sticks.
            new(
                typeof(VendorCommandSeeder),
                [ModuleId.Fleet],
                DevelopmentOnly: false,
                (services, logger, ct) => VendorCommandSeeder.SeedAsync(
                    services.GetRequiredService<IServiceScopeFactory>(),
                    services.GetRequiredService<IWebHostEnvironment>(),
                    logger,
                    ct)),
        ]);

    private static readonly FrozenDictionary<Type, IReadOnlyList<ModuleId>> Owners =
        Registrations.ToFrozenDictionary(
            registration => registration.Seeder,
            registration => registration.Modules);

    public static IReadOnlyCollection<Type> All => Owners.Keys;

    public static IReadOnlyList<ModuleId> RequirementsFor(Type seeder) => Owners[seeder];

    // What this deployment would run, in declaration order. `isDevelopment` is part of
    // the answer rather than a caller's business: a dev-only seeder skipped in
    // production is not the same statement as a module being off, and both are decided
    // here so neither can be decided twice.
    public static IReadOnlyList<Type> Enabled(ModuleSelection selection, bool isDevelopment) =>
        [.. Selected(selection, isDevelopment).Select(registration => registration.Seeder)];

    public static async Task RunEnabledAsync(
        IServiceProvider services,
        ModuleSelection selection,
        bool isDevelopment,
        ILogger logger,
        CancellationToken ct = default)
    {
        foreach (var registration in Selected(selection, isDevelopment))
            await registration.RunAsync(services, logger, ct);
    }

    private static IEnumerable<SeederRegistration> Selected(
        ModuleSelection selection,
        bool isDevelopment) =>
        Registrations.Where(registration =>
            (!registration.DevelopmentOnly || isDevelopment)
            && selection.AreEnabled(registration.Modules));
}

public sealed record SeederRegistration(
    Type Seeder,
    IReadOnlyList<ModuleId> Modules,
    bool DevelopmentOnly,
    Func<IServiceProvider, ILogger, CancellationToken, Task> RunAsync);
