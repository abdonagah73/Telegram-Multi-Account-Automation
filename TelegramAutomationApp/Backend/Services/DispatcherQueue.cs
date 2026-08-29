using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelegramAutomationApp.Backend.Models;

namespace TelegramAutomationApp.Backend.Services
{
    public class DispatcherQueue
    {
        private readonly ILogger<DispatcherQueue> _logger;

        public DispatcherQueue(ILogger<DispatcherQueue> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Filters active available Telegram accounts (not on cooldown).
        /// </summary>
        public List<TelegramAccount> GetAvailableAccounts(IEnumerable<TelegramAccount> accounts)
        {
            var now = DateTime.UtcNow;
            return accounts.Where(a => a.IsActive && (!a.IsOnCooldown || (a.CooldownUntil.HasValue && a.CooldownUntil.Value <= now))).ToList();
        }

        /// <summary>
        /// Distributes target items among available accounts using Round-Robin load balancing.
        /// </summary>
        public Dictionary<TelegramAccount, List<TaskTargetItem>> DistributeTargetsRoundRobin(
            List<TelegramAccount> availableAccounts, 
            List<TaskTargetItem> pendingTargets)
        {
            var result = new Dictionary<TelegramAccount, List<TaskTargetItem>>();
            if (!availableAccounts.Any() || !pendingTargets.Any())
                return result;

            foreach (var account in availableAccounts)
            {
                result[account] = new List<TaskTargetItem>();
            }

            int accountIndex = 0;
            foreach (var target in pendingTargets)
            {
                var account = availableAccounts[accountIndex % availableAccounts.Count];
                result[account].Add(target);
                accountIndex++;
            }

            return result;
        }
    }
}
