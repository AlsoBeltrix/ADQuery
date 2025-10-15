using AdQuery.Orchestrator.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdQuery.Orchestrator.Controllers;

[Authorize(Roles = "ANALOG\\ADEXNLQ_Users")]
[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    [HttpGet("info")]
    public ActionResult<UserInfoResponse> GetUserInfo()
    {
        var identity = HttpContext.User?.Identity;
        return new UserInfoResponse
        {
            Username = identity?.Name ?? "unknown",
            IsAuthenticated = identity?.IsAuthenticated ?? false
        };
    }
}

