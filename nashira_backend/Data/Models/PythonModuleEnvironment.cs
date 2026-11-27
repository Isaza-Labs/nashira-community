namespace nashira_backend.Data.Models;

// One install target of a pip allowlist row: "also install this package in that uv
// environment". Table `python_module_environments`.
//
// A plain link, hard-deleted rather than soft-deleted: it carries no history of its
// own (the module's audit trail records target changes), and a dangling link would
// put a package into an environment nobody chose it for. Deleting either side removes
// its links (AllowedPythonModuleController.Delete, PythonEnvironmentService.DeleteAsync).
//
// No foreign keys, like the rest of this model: both sides are soft-deleted rows, and
// the code that deletes them removes the links in the same SaveChanges.
public class PythonModuleEnvironment
{
    public Guid AllowedPythonModuleId { get; set; }
    public Guid PythonEnvironmentId { get; set; }
    public DateTime CreatedAt { get; set; }
}
