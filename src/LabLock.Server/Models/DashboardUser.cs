using System.ComponentModel.DataAnnotations;

namespace LabLock.Server.Models;

public class DashboardUser
{
    [Key]
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "viewer";
    public bool IsActive { get; set; } = true;
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
}
