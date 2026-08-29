using System;
using System.Collections.Generic;

namespace TelegramAutomationApp.Backend.Models
{
    public enum CampaignType
    {
        GroupMemberAdder = 1,
        DirectMessaging = 2
    }

    public enum CampaignStatus
    {
        Pending = 0,
        Running = 1,
        Paused = 2,
        Completed = 3,
        Failed = 4
    }

    public class CampaignTask
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public CampaignType Type { get; set; }
        public CampaignStatus Status { get; set; } = CampaignStatus.Pending;
        public string? TargetGroupUsername { get; set; }
        public string? MessageTemplate { get; set; }
        public string? ImagePath { get; set; }
        public int DelaySecondsPerAction { get; set; } = 5;
        public int TotalTargets { get; set; }
        public int ProcessedTargets { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedTime { get; set; }

        public List<TaskTargetItem> Targets { get; set; } = new();
    }
}
