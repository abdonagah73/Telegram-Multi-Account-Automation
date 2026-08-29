using System;

namespace TelegramAutomationApp.Backend.Models
{
    public enum TargetStatus
    {
        Pending = 0,
        Success = 1,
        Failed = 2
    }

    public class TaskTargetItem
    {
        public int Id { get; set; }
        public int CampaignTaskId { get; set; }
        public CampaignTask? CampaignTask { get; set; }

        public string TargetUsername { get; set; } = string.Empty;
        public long? TargetUserId { get; set; }
        public long? AccessHash { get; set; }
        
        public int? AssignedAccountId { get; set; }
        public TargetStatus Status { get; set; } = TargetStatus.Pending;
        public string? ErrorMessage { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }
}
