using Microsoft.AspNetCore.Mvc;

namespace WanderLost.Server.Controllers
{
    public class ClientErrorReportingController : Controller
    {
        private readonly ILogger<ClientErrorReportingController> _logger;

        public ClientErrorReportingController(ILogger<ClientErrorReportingController> logger)
        {
            _logger = logger;
        }

        [HttpPost]
        [Route("/ClientErrorReporting")]
        public IActionResult Index([FromBody] string? clientError)
        {
            if (!string.IsNullOrWhiteSpace(clientError))
            {
                _logger.LogError("Client Error: {clientError}", clientError);
            }
            return StatusCode(StatusCodes.Status200OK);
        }
    }
}
