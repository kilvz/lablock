using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LabLock.Server.Data;
using LabLock.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public record LoginRequest(string Username, string Password);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _db.DashboardUsers.FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { error = "Invalid credentials" });

        user.LastLogin = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var token = GenerateJwtToken(user);

        return Ok(new
        {
            token,
            user = new
            {
                user.Username,
                user.DisplayName,
                user.Role
            }
        });
    }

    [HttpPost("bootstrap")]
    public async Task<IActionResult> Bootstrap()
    {
        var userExists = await _db.DashboardUsers.AnyAsync();
        if (userExists)
            return BadRequest(new { error = "Already bootstrapped" });

        var user = new DashboardUser
        {
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
            DisplayName = "Administrator",
            Role = "admin",
            IsActive = true
        };
        _db.DashboardUsers.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Default user created: admin/admin" });
    }

    private string GenerateJwtToken(DashboardUser user)
    {
        var jwtKey = _config["Jwt:Key"] ?? "LabLockSuperSecretKey2026!@#$%^&*()";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("display_name", user.DisplayName ?? user.Username)
        };

        var token = new JwtSecurityToken(
            issuer: "LabLock",
            audience: "LabLock",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
