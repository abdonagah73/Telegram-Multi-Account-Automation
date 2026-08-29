using System;

namespace TelegramAutomationApp.Backend.Models
{
    public class TelegramAccount
    {
        public int Id { get; set; }
        public string PhoneNumber { get; set; } = string.Empty;
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = string.Empty;
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Username { get; set; }
        public string SessionPath { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public bool IsOnCooldown { get; set; } = false;
        public DateTime? CooldownUntil { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    }
}
