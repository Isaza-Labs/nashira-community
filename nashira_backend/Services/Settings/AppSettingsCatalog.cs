using nashira_backend.Configuration.Modules;

namespace nashira_backend.Services.Settings;

// What may be changed from the settings screen, and nothing else.
//
// Flow-weaver's equivalent exposes four flags that belong to flow-weaver: an RBAC
// rollout switch for a migration Nashira never had, and two thresholds for a workflow
// importer Nashira does not have either. Porting the shape would have produced a page
// of controls that control nothing, which is worse than no page — a setting that does
// not settle anything teaches people the screen is decoration.
//
// So the catalog is built the other way round: start from values the code actually
// reads at the moment it uses them, and expose only those. Every key here has a read
// site that goes through AppSettingsProvider, which is what makes a change take effect
// without a redeploy. Adding a row to this list without changing its read site would
// produce exactly the dead control this exists to avoid.
//
// The same argument settles what a modular deployment shows: a setting whose read site
// belongs to a capability this deployment does not run is a control that controls
// nothing. Settings stays in core; the rows are filtered by module.
public sealed record AppSettingDefinition(
    string Key,
    string Category,
    string DisplayName,
    string Description,
    string InputType,
    string Default,
    int DisplayOrder,
    ModuleId Module)
{
    public const string TypeBool = "bool";
    public const string TypeInt = "int";

    public const string Provider = "platform";

    public static IReadOnlyList<AppSettingDefinition> All { get; } =
    [
        new("Ssh:AllowDestructiveCommands", "Safety",
            "Allow destructive SSH commands",
            "Off by default. When off, commands that erase or reload a device are refused "
            + "before they reach it, whatever the workflow says. This is the net beneath "
            + "the per-device environment flags, not a replacement for them.",
            TypeBool, "false", 10, ModuleId.Fleet),

        new("Python:PackageProvisioningEnabled", "Python",
            "Install approved pip packages",
            "Whether the provisioner installs packages an admin has allowed. Turning it "
            + "off leaves approved packages pending rather than failing them, so they "
            + "install when it is turned back on.",
            TypeBool, "true", 20, ModuleId.Automation),

        new("Python:PipInstallTimeoutSeconds", "Python",
            "pip install timeout (seconds)",
            "How long a single install may take before it is abandoned. Raise it for large "
            + "wheels on a slow link. Values below 30 are treated as 30.",
            TypeInt, "300", 21, ModuleId.Automation),

        new("Git:MaxFileBytes", "Git",
            "Largest readable file (bytes)",
            "Files above this are not read into memory by the git browser. It bounds a "
            + "request, so a repository with one enormous file cannot exhaust the server.",
            TypeInt, "5242880", 30, ModuleId.Git),
    ];

    /** Every definition, regardless of deployment — the exhaustiveness tests read this. */
    public static AppSettingDefinition? Find(string key) => All.FirstOrDefault(d => d.Key == key);

    // What this deployment may show and change. A key belonging to a disabled module is
    // not merely hidden: writing it is refused too, so a stored value cannot be planted
    // for a capability that is off and then take effect the day it is turned on.
    public static IReadOnlyList<AppSettingDefinition> For(ModuleSelection selection) =>
        [.. All.Where(definition => selection.IsEnabled(definition.Module))];

    public static AppSettingDefinition? Find(string key, ModuleSelection selection) =>
        Find(key) is { } definition && selection.IsEnabled(definition.Module) ? definition : null;
}
