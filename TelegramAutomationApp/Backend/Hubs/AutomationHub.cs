using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace TelegramAutomationApp.Backend.Hubs
{
    public class AutomationHub : Hub
    {
        public async Task SendLog(string level, string message, string? accountPhone = null)
        {
            await Clients.All.SendAsync("ReceiveLog", new
            {
                Timestamp = DateTime.UtcNow.ToString("HH:mm:ss"),
                Level = level,
                Message = message,
                AccountPhone = accountPhone
            });
        }

        public async Task SendProgress(int campaignId, int total, int processed, int success, int failed, string status)
        {
            await Clients.All.SendAsync("ReceiveProgress", new
            {
                CampaignId = campaignId,
                Total = total,
                Processed = processed,
                Success = success,
                Failed = failed,
                Status = status,
                Percentage = total > 0 ? (int)((double)processed / total * 100) : 0
            });
        }
    }
}
