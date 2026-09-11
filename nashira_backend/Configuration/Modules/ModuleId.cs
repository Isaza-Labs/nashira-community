namespace nashira_backend.Configuration.Modules;

// Stable identifiers used inside the backend. Their public kebab-case spellings
// live in ModuleCatalog, so parsing and rendering never depend on enum casing.
public enum ModuleId
{
    Core,
    Chat,
    AiStudio,
    Automation,
    Fleet,
    Integrations,
    Communications,
    Secrets,
    Git,
    Knowledge,
    Artifacts,
    Governance,
    Observability,
}

public enum ModuleConfigurationMode
{
    DefaultAll,
    Explicit,
}
