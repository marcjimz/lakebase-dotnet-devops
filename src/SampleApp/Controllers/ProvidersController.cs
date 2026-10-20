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

    [HttpGet]
    public async Task<ActionResult<List<Provider>>> GetAll()
        => await _db.Providers.ToListAsync();

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
