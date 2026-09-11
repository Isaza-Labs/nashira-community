using Microsoft.AspNetCore.Mvc;
using nashira_backend.Data.DTos;

namespace nashira_backend.Services.Interfaces;

// Base CRUD contract every entity service implements. Uses DTOs on the wire:
//   - TResponse is returned to clients (no DB-internal or encrypted fields)
//   - TCreate is the POST body
//   - TUpdate is the PUT body (usually all-nullable so clients can PATCH)
// Ids are Guid across the codebase. GetAsync is paginated (limit default 50,
// max clamped in each service; offset default 0; total = COUNT(*)).
public interface IBaseService<TResponse, TCreate, TUpdate>
{
    Task<ActionResult<ListResponse<TResponse>>> GetAsync(int limit = 50, int offset = 0);

    Task<ActionResult<TResponse>> GetByIdAsync(Guid id);

    Task<ActionResult<TResponse>> PostAsync(TCreate dto);

    Task<ActionResult<TResponse>> UpdateAsync(Guid id, TUpdate dto);

    Task<ActionResult<TResponse>> DeleteAsync(Guid id);
}
