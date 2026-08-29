using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;
using TelegramAutomationApp.Backend.Database;
using TelegramAutomationApp.Backend.Hubs;
using TelegramAutomationApp.Backend.Models;

namespace TelegramAutomationApp.Backend.Services
{
    public class MemberAdderService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SessionManager _sessionManager;
        private readonly DispatcherQueue _dispatcher;
        private readonly IHubContext<AutomationHub> _hubContext;
        private readonly ILogger<MemberAdderService> _logger;

        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _campaignTokens = new();

        public MemberAdderService(
            IServiceProvider serviceProvider,
            SessionManager sessionManager,
            DispatcherQueue dispatcher,
            IHubContext<AutomationHub> hubContext,
            ILogger<MemberAdderService> logger)
        {
            _serviceProvider = serviceProvider;
            _sessionManager = sessionManager;
            _dispatcher = dispatcher;
            _hubContext = hubContext;
            _logger = logger;
        }

        public bool StopCampaign(int campaignId)
        {
            if (_campaignTokens.TryRemove(campaignId, out var cts))
            {
                cts.Cancel();
                return true;
            }
            return false;
        }

        public async Task ExecuteMemberAdderCampaignAsync(int campaignId)
        {
            var cts = new CancellationTokenSource();
            _campaignTokens[campaignId] = cts;
            var token = cts.Token;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var campaign = await db.CampaignTasks
                .Include(c => c.Targets)
                .FirstOrDefaultAsync(c => c.Id == campaignId, cancellationToken: token);

            if (campaign == null) return;

            campaign.Status = CampaignStatus.Running;
            await db.SaveChangesAsync(token);
            await LogAsync("INFO", $"🚀 Starting Group Member Adder Campaign: {campaign.Name}");

            try
            {
                var activeAccounts = await db.Accounts.Where(a => a.IsActive).ToListAsync(token);
                var availableAccounts = _dispatcher.GetAvailableAccounts(activeAccounts);

                if (!availableAccounts.Any())
                {
                    await LogAsync("WARN", "No active available Telegram accounts to run campaign.");
                    campaign.Status = CampaignStatus.Failed;
                    await db.SaveChangesAsync(token);
                    return;
                }

                var pendingTargets = campaign.Targets.Where(t => t.Status == TargetStatus.Pending).ToList();
                if (!pendingTargets.Any())
                {
                    await LogAsync("INFO", "No pending targets left for this campaign.");
                    campaign.Status = CampaignStatus.Completed;
                    await db.SaveChangesAsync(token);
                    return;
                }

                var distribution = _dispatcher.DistributeTargetsRoundRobin(availableAccounts, pendingTargets);

                var tasks = new List<Task>();
                foreach (var kvp in distribution)
                {
                    var account = kvp.Key;
                    var targets = kvp.Value;
                    var targetIds = targets.Select(t => t.Id).ToList();
                    tasks.Add(ProcessAccountTargetsAsync(campaign.Id, account.Id, campaign.TargetGroupUsername ?? "", targetIds, token));
                }

                await Task.WhenAll(tasks);

                // Update final campaign state
                using var finishScope = _serviceProvider.CreateScope();
                var finishDb = finishScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var finalCampaign = await finishDb.CampaignTasks.Include(c => c.Targets).FirstOrDefaultAsync(c => c.Id == campaignId);
                
                if (finalCampaign != null)
                {
                    finalCampaign.ProcessedTargets = finalCampaign.Targets.Count(t => t.Status != TargetStatus.Pending);
                    finalCampaign.SuccessCount = finalCampaign.Targets.Count(t => t.Status == TargetStatus.Success);
                    finalCampaign.FailedCount = finalCampaign.Targets.Count(t => t.Status == TargetStatus.Failed);

                    if (token.IsCancellationRequested)
                    {
                        finalCampaign.Status = CampaignStatus.Paused;
                        await LogAsync("WARN", $"⏸ Campaign {finalCampaign.Name} paused.");
                    }
                    else
                    {
                        finalCampaign.Status = CampaignStatus.Completed;
                        finalCampaign.CompletedTime = DateTime.UtcNow;
                        await LogAsync("SUCCESS", $"🎉 Campaign {finalCampaign.Name} completed! Success: {finalCampaign.SuccessCount}, Failed: {finalCampaign.FailedCount}");
                    }
                    await finishDb.SaveChangesAsync();
                    await BroadcastProgressAsync(finalCampaign);
                }
            }
            catch (OperationCanceledException)
            {
                await LogAsync("WARN", $"Campaign {campaignId} operation canceled (Paused/Stopped).");
            }
            catch (Exception ex)
            {
                await LogAsync("ERROR", $"Unhandled member adder campaign error: {ex.Message}");
            }
            finally
            {
                _campaignTokens.TryRemove(campaignId, out _);
            }
        }

        private async Task ProcessAccountTargetsAsync(
            int campaignId, 
            int accountId, 
            string targetGroupUsername, 
            List<int> targetIds, 
            CancellationToken token)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var account = await db.Accounts.FindAsync(accountId);
            if (account == null) return;

            var client = await _sessionManager.GetOrCreateClientAsync(account.PhoneNumber, account.ApiId, account.ApiHash);

            // Resolve / Join target group
            ChatBase? targetChat = await ResolveOrJoinGroupAsync(client, targetGroupUsername, account.PhoneNumber);

            if (targetChat == null)
            {
                string errMsg = $"Could not resolve/join target group: {targetGroupUsername}";
                await LogAsync("ERROR", errMsg, account.PhoneNumber);

                foreach (var tid in targetIds)
                {
                    await UpdateTargetAndNotifyAsync(campaignId, tid, account.Id, account.PhoneNumber, TargetStatus.Failed, errMsg, token);
                }
                return;
            }

            int processedCountInWorker = 0;

            foreach (var targetId in targetIds)
            {
                if (token.IsCancellationRequested) break;

                var target = await db.TaskTargetItems.FindAsync(targetId);
                if (target == null) continue;

                if (account.IsOnCooldown && account.CooldownUntil > DateTime.UtcNow)
                {
                    await LogAsync("WARN", $"Account {account.PhoneNumber} is on cooldown until {account.CooldownUntil}. Stopping worker.", account.PhoneNumber);
                    break;
                }

                try
                {
                    InputUser inputUser;
                    if (!string.IsNullOrEmpty(target.TargetUsername))
                    {
                        var resolvedUser = await client.Contacts_ResolveUsername(target.TargetUsername.TrimStart('@'));
                        if (resolvedUser.User is User u)
                        {
                            inputUser = new InputUser(u.id, u.access_hash);
                        }
                        else
                        {
                            throw new Exception("User not found on Telegram");
                        }
                    }
                    else if (target.TargetUserId.HasValue && target.AccessHash.HasValue)
                    {
                        inputUser = new InputUser(target.TargetUserId.Value, target.AccessHash.Value);
                    }
                    else
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, "No username or UserID/AccessHash provided", token);
                        continue;
                    }

                    // Add user to group / supergroup
                    if (targetChat is Channel channel)
                    {
                        await client.Channels_InviteToChannel(channel, new[] { inputUser });
                    }
                    else if (targetChat is Chat chat)
                    {
                        await client.Messages_AddChatUser(chat.id, inputUser, 10);
                    }

                    await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Success, null, token);
                    await LogAsync("SUCCESS", $"Added @{target.TargetUsername} to group via {account.PhoneNumber}", account.PhoneNumber);

                    processedCountInWorker++;

                    // Anti-Ban Human Random Delay (8 to 20 seconds) + periodic break
                    int delayMs = Random.Shared.Next(8000, 20000);
                    if (processedCountInWorker % 5 == 0)
                    {
                        delayMs += Random.Shared.Next(12000, 25000);
                        await LogAsync("INFO", $"☕ Taking brief anti-ban human pause ({delayMs / 1000}s) for {account.PhoneNumber}", account.PhoneNumber);
                    }
                    await Task.Delay(delayMs, token);
                }
                catch (RpcException rpcEx)
                {
                    if (rpcEx.Code == 420) // FLOOD_WAIT
                    {
                        int waitSeconds = rpcEx.X;
                        await HandleFloodWaitAsync(account, waitSeconds, db);
                        break;
                    }
                    else if (rpcEx.Message.Contains("USER_ALREADY_PARTICIPANT"))
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Success, "Already in group", token);
                        await LogAsync("INFO", $"@{target.TargetUsername} is already in the group.", account.PhoneNumber);
                    }
                    else if (rpcEx.Message.Contains("CHAT_ADMIN_REQUIRED"))
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, "CHAT_ADMIN_REQUIRED (Admin rights needed to add users)", token);
                        await LogAsync("ERROR", $"Cannot add @{target.TargetUsername}: Admin rights needed in this group.", account.PhoneNumber);
                    }
                    else if (rpcEx.Message.Contains("USER_NOT_MUTUAL_CONTACT"))
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, "USER_NOT_MUTUAL_CONTACT (User privacy: mutual contacts only)", token);
                        await LogAsync("WARN", $"Privacy restricted for @{target.TargetUsername} (Mutual contacts only).", account.PhoneNumber);
                    }
                    else if (rpcEx.Message.Contains("USER_PRIVACY_RESTRICTED"))
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, "USER_PRIVACY_RESTRICTED (User privacy prevents group adds)", token);
                        await LogAsync("WARN", $"Privacy restricted for @{target.TargetUsername}.", account.PhoneNumber);
                    }
                    else if (rpcEx.Message.Contains("PEER_FLOOD"))
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, "PEER_FLOOD", token);
                        await HandleFloodWaitAsync(account, 300, db);
                        break;
                    }
                    else
                    {
                        await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, rpcEx.Message, token);
                        await LogAsync("ERROR", $"Failed to add @{target.TargetUsername}: {rpcEx.Message}", account.PhoneNumber);
                    }
                }
                catch (Exception ex)
                {
                    await UpdateTargetAndNotifyAsync(campaignId, targetId, account.Id, account.PhoneNumber, TargetStatus.Failed, ex.Message, token);
                    await LogAsync("ERROR", $"Exception adding @{target.TargetUsername}: {ex.Message}", account.PhoneNumber);
                }
            }
        }

        private async Task<ChatBase?> ResolveOrJoinGroupAsync(Client client, string groupInput, string accountPhone)
        {
            if (string.IsNullOrWhiteSpace(groupInput)) return null;

            string cleaned = groupInput.Trim();
            cleaned = Regex.Replace(cleaned, @"^https?://t\.me/", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^t\.me/", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.TrimStart('@');

            // Handle Private Invite Links (e.g. +hash or joinchat/hash)
            if (cleaned.StartsWith("+") || cleaned.StartsWith("joinchat/", StringComparison.OrdinalIgnoreCase))
            {
                string hash = cleaned.StartsWith("+") ? cleaned.Substring(1) : cleaned.Substring("joinchat/".Length);
                try
                {
                    var update = await client.Messages_ImportChatInvite(hash);
                    if (update is UpdatesBase ub && ub.Chats.Values.Any())
                    {
                        return ub.Chats.Values.FirstOrDefault();
                    }
                }
                catch (RpcException rpcEx) when (rpcEx.Message.Contains("USER_ALREADY_PARTICIPANT"))
                {
                    var check = await client.Messages_CheckChatInvite(hash);
                    if (check is ChatInviteAlready already && already.chat is ChatBase cb)
                    {
                        return cb;
                    }
                }
                catch (Exception ex)
                {
                    await LogAsync("ERROR", $"Failed importing private invite link: {ex.Message}", accountPhone);
                }
            }

            // Handle Public Group Username
            try
            {
                var resolved = await client.Contacts_ResolveUsername(cleaned);
                var chat = resolved.chats.Values.FirstOrDefault();
                if (chat is Channel channel)
                {
                    try { await client.Channels_JoinChannel(channel); } catch { }
                    return channel;
                }
                else if (chat is Chat c)
                {
                    return c;
                }
                return chat;
            }
            catch (Exception ex)
            {
                await LogAsync("ERROR", $"Failed to resolve public group '{cleaned}': {ex.Message}", accountPhone);
                return null;
            }
        }

        private async Task UpdateTargetAndNotifyAsync(int campaignId, int targetId, int accountId, string phone, TargetStatus status, string? errorMsg, CancellationToken token)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var target = await db.TaskTargetItems.FindAsync(targetId);
            if (target != null)
            {
                target.Status = status;
                target.ErrorMessage = errorMsg;
                target.AssignedAccountId = accountId;
                target.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
            }

            var campaign = await db.CampaignTasks.Include(c => c.Targets).FirstOrDefaultAsync(c => c.Id == campaignId, token);
            if (campaign == null) return;

            campaign.ProcessedTargets = campaign.Targets.Count(t => t.Status != TargetStatus.Pending);
            campaign.SuccessCount = campaign.Targets.Count(t => t.Status == TargetStatus.Success);
            campaign.FailedCount = campaign.Targets.Count(t => t.Status == TargetStatus.Failed);
            await db.SaveChangesAsync(token);

            await _hubContext.Clients.All.SendAsync("ReceiveRecipientStatus", new
            {
                CampaignId = campaignId,
                TargetUsername = target?.TargetUsername ?? "",
                Status = status.ToString(),
                ErrorMessage = errorMsg,
                AccountPhone = phone,
                Timestamp = DateTime.UtcNow.ToString("HH:mm:ss"),
                Total = campaign.TotalTargets,
                Processed = campaign.ProcessedTargets,
                Success = campaign.SuccessCount,
                Failed = campaign.FailedCount,
                Percentage = campaign.TotalTargets > 0 ? (int)((double)campaign.ProcessedTargets / campaign.TotalTargets * 100) : 0
            });
        }

        private async Task HandleFloodWaitAsync(TelegramAccount account, int waitSeconds, AppDbContext db)
        {
            account.IsOnCooldown = true;
            account.CooldownUntil = DateTime.UtcNow.AddSeconds(waitSeconds);
            await db.SaveChangesAsync();
            await LogAsync("WARN", $"⚠️ FLOOD_WAIT received for {account.PhoneNumber}! Account on cooldown for {waitSeconds}s.", account.PhoneNumber);
        }

        private async Task LogAsync(string level, string message, string? phone = null)
        {
            _logger.LogInformation("[{Level}] {Message} (Phone: {Phone})", level, message, phone);
            await _hubContext.Clients.All.SendAsync("ReceiveLog", new
            {
                Timestamp = DateTime.UtcNow.ToString("HH:mm:ss"),
                Level = level,
                Message = message,
                AccountPhone = phone
            });
        }

        private async Task BroadcastProgressAsync(CampaignTask campaign)
        {
            await _hubContext.Clients.All.SendAsync("ReceiveProgress", new
            {
                CampaignId = campaign.Id,
                Total = campaign.TotalTargets,
                Processed = campaign.ProcessedTargets,
                Success = campaign.SuccessCount,
                Failed = campaign.FailedCount,
                Status = campaign.Status.ToString(),
                Percentage = campaign.TotalTargets > 0 ? (int)((double)campaign.ProcessedTargets / campaign.TotalTargets * 100) : 0
            });
        }
    }
}
