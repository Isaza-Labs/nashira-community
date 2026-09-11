using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Knowledge;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using ArticleEntity = nashira_backend.Data.Models.KnowledgeArticle;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Knowledge-base CRUD. Read = viewer; write = operator+. Slugs are unique per tenant.
[ApiController]
[Route("api/knowledge")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class KnowledgeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public KnowledgeController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<KnowledgeArticleResponse>>> Get(
        string? q = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var query = _db.KnowledgeArticles.AsNoTracking().Where(a => a.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q}%";
            query = query.Where(a => EF.Functions.ILike(a.Title, pattern) || EF.Functions.ILike(a.Content, pattern));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.UpdatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<KnowledgeArticleResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnowledgeArticleResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<KnowledgeArticleResponse>> Post([FromBody] CreateKnowledgeArticle dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Title)) throw new ValidationException("title is required");
        var row = await CreateAsync(_db, dto.Title, dto.Content, dto.Tags, ct);
        return new CreatedAtActionResult(nameof(GetById), "Knowledge", new { id = row.KnowledgeArticleId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<KnowledgeArticleResponse>> Update(Guid id, [FromBody] UpdateKnowledgeArticle dto, CancellationToken ct)
        => ToResponse(await UpdateAsync(_db, id, dto.Title, dto.Content, dto.Tags, ct));

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<KnowledgeArticleResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    // Shared creation logic (also used by the create_knowledge_article tool).
    public static async Task<ArticleEntity> CreateAsync(
        AppDbContext db, string title, string? content, List<string>? tags, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = new ArticleEntity
        {
            KnowledgeArticleId = Guid.NewGuid(),
            Title = title.Trim(),
            Slug = await UniqueSlugAsync(db, title, null, ct),
            Content = content ?? string.Empty,
            Tags = NormalizeTags(tags),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KnowledgeArticles.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    // Shared update logic (also used by the update_knowledge_article tool). Regenerates the
    // slug when the title changes; only non-null fields are applied.
    public static async Task<ArticleEntity> UpdateAsync(
        AppDbContext db, Guid id, string? title, string? content, List<string>? tags, CancellationToken ct)
    {
        var row = await db.KnowledgeArticles.FirstOrDefaultAsync(
            a => a.KnowledgeArticleId == id && a.IsActive, ct);
        if (row is null) throw new NotFoundException("knowledge article not found");

        if (title is not null && title.Trim().Length > 0)
        {
            row.Title = title.Trim();
            row.Slug = await UniqueSlugAsync(db, row.Title, row.KnowledgeArticleId, ct);
        }
        if (content is not null) row.Content = content;
        if (tags is not null) row.Tags = NormalizeTags(tags);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static async Task<string> UniqueSlugAsync(AppDbContext db, string title, Guid? excludeId, CancellationToken ct)
    {
        var baseSlug = ArticleEntity.Slugify(title);
        var slug = baseSlug;
        for (var n = 2; ; n++)
        {
            var taken = await db.KnowledgeArticles.AnyAsync(
                a => a.Slug == slug && a.IsActive && (excludeId == null || a.KnowledgeArticleId != excludeId), ct);
            if (!taken) return slug;
            slug = $"{baseSlug}-{n}";
        }
    }

    private static List<string> NormalizeTags(List<string>? tags) => tags is null
        ? []
        : tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();

    private async Task<ArticleEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.KnowledgeArticles.FirstOrDefaultAsync(
            a => a.KnowledgeArticleId == id && a.IsActive, ct);
        if (row is null) throw new NotFoundException("knowledge article not found");
        return row;
    }

    private static KnowledgeArticleResponse ToResponse(ArticleEntity a) => new()
    {
        KnowledgeArticleId = a.KnowledgeArticleId,
        Title = a.Title,
        Slug = a.Slug,
        Content = a.Content,
        Tags = a.Tags,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt,
    };
}
