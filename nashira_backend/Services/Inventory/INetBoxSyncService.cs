using nashira_backend.Data.DTos.Inventory;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Inventory;

// Pulls devices from a NetBox instance and upserts them into the Device table
// (matched by device name). Tenant-scoped; throws DomainException on bad config
// or a failed NetBox request.
public interface INetBoxSyncService
{
    Task<NetBoxSyncResult> SyncAsync(InventorySource source, bool dryRun, CancellationToken ct);
}
