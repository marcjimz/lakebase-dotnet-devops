using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApp.Data;
using SampleApp.Models;

namespace SampleApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MembersController : ControllerBase
{
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    public MembersController(AppDbContext db) => _db = db;

    /// <summary>Members ordered by id, one page at a time.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Members per page (1–100, default 10).</param>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<Member>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        if (page < 1)
            ModelState.AddModelError(nameof(page), "page must be 1 or greater.");
        if (pageSize is < 1 or > MaxPageSize)
            ModelState.AddModelError(nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var totalCount = await _db.Members.CountAsync();
        var items = await _db.Members
            .OrderBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return new PagedResult<Member>(items, page, pageSize, totalCount, totalPages);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Member>> GetById(int id)
    {
        var member = await _db.Members
            .Include(m => m.CareEvents)
            .FirstOrDefaultAsync(m => m.Id == id);
        return member is null ? NotFound() : Ok(member);
    }

    [HttpPost]
    public async Task<ActionResult<Member>> Create(Member member)
    {
        _db.Members.Add(member);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = member.Id }, member);
    }
}
