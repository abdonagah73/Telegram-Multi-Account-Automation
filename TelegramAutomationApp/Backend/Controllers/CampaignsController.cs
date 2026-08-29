using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExcelDataReader;
using TelegramAutomationApp.Backend.Database;
using TelegramAutomationApp.Backend.Models;
using TelegramAutomationApp.Backend.Services;

namespace TelegramAutomationApp.Backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CampaignsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly MemberAdderService _memberAdderService;
        private readonly MessagingService _messagingService;

        private static readonly HashSet<string> IgnoredHeaderKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "access", "hash", "accesshash", "access_hash", "access hash",
            "username", "user", "usernames", "users", "handle", "target", "targets",
            "id", "userid", "user_id", "user id", "phone", "phonenumber", "phone number",
            "name", "firstname", "first_name", "first name", "lastname", "last_name", "last name",
            "status", "date", "created", "time"
        };

        private static readonly Regex ScientificNumberRegex = new(@"^-?[0-9]+(\.[0-9]+)?[eE][+-]?[0-9]+$", RegexOptions.Compiled);
        private static readonly Regex PureNumberRegex = new(@"^-?[0-9]+$", RegexOptions.Compiled);
        private static readonly Regex ValidUsernameRegex = new(@"^@?([a-zA-Z0-9_]{3,32})$", RegexOptions.Compiled);

        public CampaignsController(
            AppDbContext db,
            MemberAdderService memberAdderService,
            MessagingService messagingService)
        {
            _db = db;
            _memberAdderService = memberAdderService;
            _messagingService = messagingService;
        }

        [HttpGet]
        public async Task<IActionResult> GetCampaigns()
        {
            var campaigns = await _db.CampaignTasks
                .Include(c => c.Targets)
                .OrderByDescending(c => c.CreatedTime)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    Type = c.Type.ToString(),
                    Status = c.Status.ToString(),
                    c.TargetGroupUsername,
                    c.MessageTemplate,
                    c.ImagePath,
                    c.DelaySecondsPerAction,
                    c.TotalTargets,
                    c.ProcessedTargets,
                    c.SuccessCount,
                    c.FailedCount,
                    c.CreatedTime,
                    c.CompletedTime
                })
                .ToListAsync();

            return Ok(campaigns);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetCampaign(int id)
        {
            var campaign = await _db.CampaignTasks
                .Include(c => c.Targets)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null) return NotFound();

            return Ok(new
            {
                campaign.Id,
                campaign.Name,
                Type = campaign.Type.ToString(),
                Status = campaign.Status.ToString(),
                campaign.TargetGroupUsername,
                campaign.MessageTemplate,
                campaign.ImagePath,
                campaign.DelaySecondsPerAction,
                campaign.TotalTargets,
                campaign.ProcessedTargets,
                campaign.SuccessCount,
                campaign.FailedCount,
                campaign.CreatedTime,
                campaign.CompletedTime,
                Targets = campaign.Targets.Select(t => new
                {
                    t.Id,
                    t.TargetUsername,
                    t.TargetUserId,
                    Status = t.Status.ToString(),
                    t.ErrorMessage,
                    t.ProcessedAt
                })
            });
        }

        [HttpPost("parse-file")]
        public async Task<IActionResult> ParseTargetFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            var extractedTargets = new List<string>();
            string extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                using (var stream = file.OpenReadStream())
                {
                    if (extension == ".xlsx" || extension == ".xls")
                    {
                        using (var reader = ExcelReaderFactory.CreateReader(stream))
                        {
                            var dataSet = reader.AsDataSet();
                            foreach (System.Data.DataTable table in dataSet.Tables)
                            {
                                ParseDataTable(table, extractedTargets);
                            }
                        }
                    }
                    else // .csv or .txt
                    {
                        using (var sr = new StreamReader(stream))
                        {
                            string content = await sr.ReadToEndAsync();
                            ParseDelimitedContent(content, extractedTargets);
                        }
                    }
                }

                var distinctTargets = extractedTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return Ok(new { Count = distinctTargets.Count, Targets = distinctTargets });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Error = $"Failed to parse file: {ex.Message}" });
            }
        }

        private static void ParseDataTable(System.Data.DataTable table, List<string> targetList)
        {
            if (table.Rows.Count == 0) return;

            // 1. Check if first row contains column headers
            int usernameColIndex = -1;
            var firstRow = table.Rows[0];
            for (int col = 0; col < table.Columns.Count; col++)
            {
                string header = firstRow[col]?.ToString()?.Trim() ?? "";
                if (header.Equals("username", StringComparison.OrdinalIgnoreCase) ||
                    header.Equals("usernames", StringComparison.OrdinalIgnoreCase) ||
                    header.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                    header.Equals("handle", StringComparison.OrdinalIgnoreCase) ||
                    header.Equals("target", StringComparison.OrdinalIgnoreCase))
                {
                    usernameColIndex = col;
                    break;
                }
            }

            int startRow = (usernameColIndex >= 0) ? 1 : 0;

            for (int r = startRow; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                if (usernameColIndex >= 0)
                {
                    string val = row[usernameColIndex]?.ToString()?.Trim() ?? "";
                    TryAddValidUsername(val, targetList);
                }
                else
                {
                    // Fallback: Check every cell in the row
                    for (int c = 0; c < table.Columns.Count; c++)
                    {
                        string val = row[c]?.ToString()?.Trim() ?? "";
                        TryAddValidUsername(val, targetList);
                    }
                }
            }
        }

        private static void ParseDelimitedContent(string content, List<string> targetList)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            // Check if first line is a CSV header
            char[] delimiters = new[] { ',', ';', '\t' };
            char activeDelimiter = ',';
            int usernameColIndex = -1;

            string firstLine = lines[0];
            foreach (var d in delimiters)
            {
                if (firstLine.Contains(d))
                {
                    activeDelimiter = d;
                    var headers = firstLine.Split(d);
                    for (int i = 0; i < headers.Length; i++)
                    {
                        string h = headers[i].Trim().Trim('"');
                        if (h.Equals("username", StringComparison.OrdinalIgnoreCase) ||
                            h.Equals("usernames", StringComparison.OrdinalIgnoreCase) ||
                            h.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                            h.Equals("handle", StringComparison.OrdinalIgnoreCase) ||
                            h.Equals("target", StringComparison.OrdinalIgnoreCase))
                        {
                            usernameColIndex = i;
                            break;
                        }
                    }
                    break;
                }
            }

            int startLine = (usernameColIndex >= 0) ? 1 : 0;

            for (int i = startLine; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (usernameColIndex >= 0 && line.Contains(activeDelimiter))
                {
                    var parts = line.Split(activeDelimiter);
                    if (usernameColIndex < parts.Length)
                    {
                        string val = parts[usernameColIndex].Trim().Trim('"');
                        TryAddValidUsername(val, targetList);
                    }
                }
                else
                {
                    var tokens = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var token in tokens)
                    {
                        TryAddValidUsername(token.Trim().Trim('"'), targetList);
                    }
                }
            }
        }

        private static void TryAddValidUsername(string text, List<string> targetList)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string cleaned = text.Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(cleaned)) return;

            // Filter out column header words
            if (IgnoredHeaderKeywords.Contains(cleaned)) return;

            // Filter out scientific notation (e.g. -3.5E+18, 4.93E+18)
            if (ScientificNumberRegex.IsMatch(cleaned)) return;

            // Filter out pure numbers or access hashes
            if (PureNumberRegex.IsMatch(cleaned) && cleaned.Length > 15) return;

            // Validate Telegram username format
            var match = ValidUsernameRegex.Match(cleaned);
            if (match.Success)
            {
                targetList.Add(match.Groups[1].Value);
            }
        }

        [HttpPost("upload-image")]
        public async Task<IActionResult> UploadImage(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No image uploaded.");

            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            var fileName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
            var filePath = Path.Combine(uploadsFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var relativeUrl = $"/uploads/{fileName}";
            return Ok(new { ImageUrl = relativeUrl });
        }

        public record CreateMemberAdderCampaignDto(
            string Name,
            string TargetGroupUsername,
            List<string> Usernames);

        [HttpPost("member-adder")]
        public async Task<IActionResult> CreateMemberAdderCampaign([FromBody] CreateMemberAdderCampaignDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.TargetGroupUsername) || dto.Usernames == null || !dto.Usernames.Any())
            {
                return BadRequest("Name, TargetGroupUsername, and Usernames list are required.");
            }

            var campaign = new CampaignTask
            {
                Name = dto.Name,
                Type = CampaignType.GroupMemberAdder,
                Status = CampaignStatus.Pending,
                TargetGroupUsername = dto.TargetGroupUsername,
                DelaySecondsPerAction = 10,
                TotalTargets = dto.Usernames.Count
            };

            foreach (var rawUsername in dto.Usernames)
            {
                var cleaned = rawUsername.Trim().TrimStart('@');
                if (!string.IsNullOrEmpty(cleaned))
                {
                    campaign.Targets.Add(new TaskTargetItem
                    {
                        TargetUsername = cleaned,
                        Status = TargetStatus.Pending
                    });
                }
            }

            _db.CampaignTasks.Add(campaign);
            await _db.SaveChangesAsync();

            _ = Task.Run(() => _memberAdderService.ExecuteMemberAdderCampaignAsync(campaign.Id));

            return Ok(new { CampaignId = campaign.Id, Message = "Member adder campaign created and launched." });
        }

        public record CreateDirectMessagingCampaignDto(
            string Name,
            string MessageTemplate,
            string? ImagePath,
            List<string> Usernames);

        [HttpPost("direct-messaging")]
        public async Task<IActionResult> CreateDirectMessagingCampaign([FromBody] CreateDirectMessagingCampaignDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.MessageTemplate) || dto.Usernames == null || !dto.Usernames.Any())
            {
                return BadRequest("Name, MessageTemplate, and Usernames list are required.");
            }

            var campaign = new CampaignTask
            {
                Name = dto.Name,
                Type = CampaignType.DirectMessaging,
                Status = CampaignStatus.Pending,
                MessageTemplate = dto.MessageTemplate,
                ImagePath = dto.ImagePath,
                DelaySecondsPerAction = 10,
                TotalTargets = dto.Usernames.Count
            };

            foreach (var rawUsername in dto.Usernames)
            {
                var cleaned = rawUsername.Trim().TrimStart('@');
                if (!string.IsNullOrEmpty(cleaned))
                {
                    campaign.Targets.Add(new TaskTargetItem
                    {
                        TargetUsername = cleaned,
                        Status = TargetStatus.Pending
                    });
                }
            }

            _db.CampaignTasks.Add(campaign);
            await _db.SaveChangesAsync();

            _ = Task.Run(() => _messagingService.ExecuteMessagingCampaignAsync(campaign.Id));

            return Ok(new { CampaignId = campaign.Id, Message = "Direct messaging campaign created and launched." });
        }

        [HttpPost("{id}/pause")]
        public async Task<IActionResult> PauseCampaign(int id)
        {
            var campaign = await _db.CampaignTasks.FindAsync(id);
            if (campaign == null) return NotFound();

            bool stoppedAdder = _memberAdderService.StopCampaign(id);
            bool stoppedMsg = _messagingService.StopCampaign(id);

            campaign.Status = CampaignStatus.Paused;
            await _db.SaveChangesAsync();

            return Ok(new { CampaignId = id, Message = "Campaign paused." });
        }

        [HttpPost("{id}/resume")]
        public async Task<IActionResult> ResumeCampaign(int id)
        {
            var campaign = await _db.CampaignTasks.FindAsync(id);
            if (campaign == null) return NotFound();

            if (campaign.Status != CampaignStatus.Paused && campaign.Status != CampaignStatus.Pending)
            {
                return BadRequest("Campaign is not in a resumeable state.");
            }

            campaign.Status = CampaignStatus.Running;
            await _db.SaveChangesAsync();

            if (campaign.Type == CampaignType.GroupMemberAdder)
            {
                _ = Task.Run(() => _memberAdderService.ExecuteMemberAdderCampaignAsync(campaign.Id));
            }
            else if (campaign.Type == CampaignType.DirectMessaging)
            {
                _ = Task.Run(() => _messagingService.ExecuteMessagingCampaignAsync(campaign.Id));
            }

            return Ok(new { CampaignId = id, Message = "Campaign resumed." });
        }
    }
}
