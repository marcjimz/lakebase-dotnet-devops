using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApp.Data;
using SampleApp.Models;

namespace SampleApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProvidersController : ControllerBase
{
    private readonly AppDbContext _db;
    public ProvidersController(AppDbContext db) => _db = db;

    /// <summary>Providers ordered by id, one page at a time.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Providers per page (1–100, default 10).</param>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<Provider>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize)
    {
        foreach (var (key, error) in Paging.Validate(page, pageSize))
            ModelState.AddModelError(key, error);
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        return await _db.Providers.OrderBy(p => p.Id).ToPagedResultAsync(page, pageSize);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Provider>> GetById(int id)
    {
        var provider = await _db.Providers.FindAsync(id);
        return provider is null ? NotFound() : Ok(provider);
    }

    [HttpPost]
    public async Task<ActionResult<Provider>> Create(Provider provider)
    {
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = provider.Id }, provider);
    }
}
