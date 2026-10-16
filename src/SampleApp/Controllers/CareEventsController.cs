using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApp.Data;
using SampleApp.Models;

namespace SampleApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CareEventsController : ControllerBase
{
    private readonly AppDbContext _db;
    public CareEventsController(AppDbContext db) => _db = db;

    [HttpGet("member/{memberId}")]
    public async Task<ActionResult<List<CareEvent>>> GetByMember(int memberId)
        => await _db.CareEvents.Where(e => e.MemberId == memberId).ToListAsync();

    [HttpPost]
    public async Task<ActionResult<CareEvent>> Create(CareEvent careEvent)
    {
        _db.CareEvents.Add(careEvent);
        await _db.SaveChangesAsync();
        return CreatedAtAction(null, careEvent);
    }
}
