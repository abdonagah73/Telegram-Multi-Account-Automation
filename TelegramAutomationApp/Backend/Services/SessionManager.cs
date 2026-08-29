using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WTelegram;
using TelegramAutomationApp.Backend.Hubs;

namespace TelegramAutomationApp.Backend.Services
{
    public class SessionManager : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ConcurrentDictionary<string, Client> _clients = new();
        private readonly ConcurrentDictionary<string, string> _loginState = new(); // phone -> state
        private readonly IHubContext<AutomationHub> _hubContext;
        private readonly ILogger<SessionManager> _logger;
        private readonly string _sessionsDir;

        public SessionManager(IHubContext<AutomationHub> hubContext, ILogger<SessionManager> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
            _sessionsDir = Path.Combine(Directory.GetCurrentDirectory(), "Sessions");
            if (!Directory.Exists(_sessionsDir))
            {
                Directory.CreateDirectory(_sessionsDir);
            }
        }

        public SemaphoreSlim GetLock(string phoneNumber)
        {
            return _locks.GetOrAdd(phoneNumber, _ => new SemaphoreSlim(1, 1));
        }

        public async Task<Client> GetOrCreateClientAsync(string phoneNumber, int apiId, string apiHash)
        {
            var sem = GetLock(phoneNumber);
            await sem.WaitAsync();
            try
            {
                if (_clients.TryGetValue(phoneNumber, out var existingClient))
                {
                    return existingClient;
                }

                string sessionFilePath = Path.Combine(_sessionsDir, $"{phoneNumber}.session");
                
                string? Config(string what)
                {
                    return what switch
                    {
                        "api_id" => apiId.ToString(),
                        "api_hash" => apiHash,
                        "phone_number" => phoneNumber,
                        "session_pathname" => sessionFilePath,
                        _ => null
                    };
                }

                var client = new Client(Config);
                _clients[phoneNumber] = client;

                // Ensure user is logged in
                await client.Login(null);

                await LogAsync("INFO", $"Session initialized for {phoneNumber}", phoneNumber);
                return client;
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task<string> StartAuthFlowAsync(string phoneNumber, int apiId, string apiHash)
        {
            var sem = GetLock(phoneNumber);
            await sem.WaitAsync();
            try
            {
                string sessionFilePath = Path.Combine(_sessionsDir, $"{phoneNumber}.session");

                // If client exists, check if user is already logged in
                if (_clients.TryGetValue(phoneNumber, out var existing))
                {
                    if (existing.User != null)
                    {
                        return "AUTHORIZED";
                    }
                }

                string? Config(string what)
                {
                    return what switch
                    {
                        "api_id" => apiId.ToString(),
                        "api_hash" => apiHash,
                        "phone_number" => phoneNumber,
                        "session_pathname" => sessionFilePath,
                        _ => null
                    };
                }

                var client = new Client(Config);
                _clients[phoneNumber] = client;

                var loginState = await client.Login(null);
                if (loginState == null)
                {
                    await LogAsync("SUCCESS", $"Account {phoneNumber} authorized successfully.", phoneNumber);
                    return "AUTHORIZED";
                }
                else
                {
                    await LogAsync("INFO", $"Auth required for {phoneNumber}: {loginState}", phoneNumber);
                    _loginState[phoneNumber] = loginState;
                    return loginState; // e.g. "verification_code" or "password"
                }
            }
            catch (Exception ex)
            {
                await LogAsync("ERROR", $"Auth flow failed for {phoneNumber}: {ex.Message}", phoneNumber);
                throw;
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task<string> SubmitCodeAsync(string phoneNumber, string code)
        {
            var sem = GetLock(phoneNumber);
            await sem.WaitAsync();
            try
            {
                if (!_clients.TryGetValue(phoneNumber, out var client))
                {
                    throw new InvalidOperationException($"No active login session found for {phoneNumber}");
                }

                var loginState = await client.Login(code);
                if (loginState == null)
                {
                    await LogAsync("SUCCESS", $"Code verified! Account {phoneNumber} authorized.", phoneNumber);
                    return "AUTHORIZED";
                }
                else
                {
                    _loginState[phoneNumber] = loginState;
                    return loginState; // e.g., "password" for 2FA
                }
            }
            catch (Exception ex)
            {
                await LogAsync("ERROR", $"Verification code error for {phoneNumber}: {ex.Message}", phoneNumber);
                throw;
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task<string> Submit2FAAsync(string phoneNumber, string password)
        {
            var sem = GetLock(phoneNumber);
            await sem.WaitAsync();
            try
            {
                if (!_clients.TryGetValue(phoneNumber, out var client))
                {
                    throw new InvalidOperationException($"No active login session found for {phoneNumber}");
                }

                var loginState = await client.Login(password);
                if (loginState == null)
                {
                    await LogAsync("SUCCESS", $"2FA verified! Account {phoneNumber} authorized.", phoneNumber);
                    return "AUTHORIZED";
                }
                else
                {
                    return loginState;
                }
            }
            catch (Exception ex)
            {
                await LogAsync("ERROR", $"2FA error for {phoneNumber}: {ex.Message}", phoneNumber);
                throw;
            }
            finally
            {
                sem.Release();
            }
        }

        public Client? GetClient(string phoneNumber)
        {
            _clients.TryGetValue(phoneNumber, out var client);
            return client;
        }

        public async Task RemoveClientAsync(string phoneNumber)
        {
            var sem = GetLock(phoneNumber);
            await sem.WaitAsync();
            try
            {
                if (_clients.TryRemove(phoneNumber, out var client))
                {
                    client.Dispose();
                    await LogAsync("INFO", $"Session for {phoneNumber} closed.", phoneNumber);
                }
            }
            finally
            {
                sem.Release();
            }
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

        public async ValueTask DisposeAsync()
        {
            foreach (var kvp in _clients)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _clients.Clear();
            await Task.CompletedTask;
        }
    }
}
