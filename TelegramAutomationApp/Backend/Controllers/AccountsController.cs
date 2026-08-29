using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TelegramAutomationApp.Backend.Database;
using TelegramAutomationApp.Backend.Models;
using TelegramAutomationApp.Backend.Services;

namespace TelegramAutomationApp.Backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AccountsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly SessionManager _sessionManager;

        public AccountsController(AppDbContext db, SessionManager sessionManager)
        {
            _db = db;
            _sessionManager = sessionManager;
        }

        [HttpGet]
        public async Task<IActionResult> GetAccounts()
        {
            var accounts = await _db.Accounts.OrderByDescending(a => a.CreatedTime).ToListAsync();
            var now = DateTime.UtcNow;
            
            var result = accounts.Select(a => new
            {
                a.Id,
                a.PhoneNumber,
                a.ApiId,
                a.FirstName,
                a.LastName,
                a.Username,
                a.IsActive,
                IsOnCooldown = a.IsOnCooldown && a.CooldownUntil.HasValue && a.CooldownUntil > now,
                CooldownUntil = a.CooldownUntil
            });

            return Ok(result);
        }

        public record LoginRequestDto(string PhoneNumber, int ApiId, string ApiHash);

        [HttpPost("login-request")]
        public async Task<IActionResult> LoginRequest([FromBody] LoginRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.PhoneNumber) || dto.ApiId <= 0 || string.IsNullOrWhiteSpace(dto.ApiHash))
            {
                return BadRequest("Phone number, API ID, and API Hash are required.");
            }

            try
            {
                string state = await _sessionManager.StartAuthFlowAsync(dto.PhoneNumber, dto.ApiId, dto.ApiHash);

                // Save or update account info in DB
                var account = await _db.Accounts.FirstOrDefaultAsync(a => a.PhoneNumber == dto.PhoneNumber);
                if (account == null)
                {
                    account = new TelegramAccount
                    {
                        PhoneNumber = dto.PhoneNumber,
                        ApiId = dto.ApiId,
                        ApiHash = dto.ApiHash,
                        SessionPath = $"Sessions/{dto.PhoneNumber}.session",
                        IsActive = true
                    };
                    _db.Accounts.Add(account);
                }
                else
                {
                    account.ApiId = dto.ApiId;
                    account.ApiHash = dto.ApiHash;
                    account.IsActive = true;
                }
                await _db.SaveChangesAsync();

                return Ok(new { PhoneNumber = dto.PhoneNumber, AuthState = state });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }

        public record VerifyCodeDto(string PhoneNumber, string Code);

        [HttpPost("verify-code")]
        public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeDto dto)
        {
            try
            {
                string state = await _sessionManager.SubmitCodeAsync(dto.PhoneNumber, dto.Code);
                return Ok(new { PhoneNumber = dto.PhoneNumber, AuthState = state });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }

        public record Verify2FaDto(string PhoneNumber, string Password);

        [HttpPost("verify-2fa")]
        public async Task<IActionResult> Verify2FA([FromBody] Verify2FaDto dto)
        {
            try
            {
                string state = await _sessionManager.Submit2FAAsync(dto.PhoneNumber, dto.Password);
                return Ok(new { PhoneNumber = dto.PhoneNumber, AuthState = state });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAccount(int id)
        {
            var account = await _db.Accounts.FindAsync(id);
            if (account == null) return NotFound();

            await _sessionManager.RemoveClientAsync(account.PhoneNumber);
            _db.Accounts.Remove(account);
            await _db.SaveChangesAsync();

            return Ok(new { Message = $"Account {account.PhoneNumber} removed." });
        }
    }
}
