namespace nashira_backend.Data.Models;

// The CLI command that expresses one intent on one platform.
//
// "Show the interfaces" is `show ip interface brief` on cisco_ios, `show
// interfaces terse` on juniper_junos and `show interface brief` on nokia_srl. A
// workflow that hardcodes one of them only works on one vendor; looking the intent
// up per device is what makes a single workflow multi-vendor.
//
// Keyed by (Intent, Platform), where Platform is the Netmiko device_type already
// stored on Device — so the lookup is a direct join, not a mapping table nobody
// maintains.
public class VendorCommand : BaseModel
{
    public Guid VendorCommandId { get; set; }

    // Stable, vendor-neutral name: "show_interfaces", "show_bgp_summary".
    public string Intent { get; set; } = string.Empty;

    // Netmiko device_type, matching Device.Platform.
    public string Platform { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Whether the command only reads. Feeds the SSH policy and the idempotency
    // tier, so a catalogue entry that mutates cannot be treated as a show.
    public bool ReadOnly { get; set; } = true;

    // Optional TextFSM template name for structured parsing, when the runner's
    // built-in templates do not cover it.
    public string? ParserTemplate { get; set; }

    public Guid? CreatedBy { get; set; }
}
