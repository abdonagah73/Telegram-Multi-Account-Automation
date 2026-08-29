using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TelegramAutomationApp.Backend.Services;

namespace TelegramAutomationApp.Backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly SessionManager _sessionManager;

        public AuthController(SessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        [HttpGet("status/{phoneNumber}")]
        public IActionResult GetStatus(string phoneNumber)
        {
            var client = _sessionManager.GetClient(phoneNumber);
            if (client == null)
            {
                return Ok(new { PhoneNumber = phoneNumber, Status = "NOT_INITIALIZED" });
            }

            return Ok(new { PhoneNumber = phoneNumber, Status = "ACTIVE" });
        }
    }
}
