using Microsoft.AspNetCore.Mvc;
using CaroAIServer.Services;
using System.Threading.Tasks;

namespace CaroAIServer.Controllers
{
    [Route("api/openings")]
    [ApiController]
    public class OpeningGenerationController : ControllerBase
    {
        private readonly OpeningDataService _openingDataService;
        private readonly ILogger<OpeningGenerationController> _logger;

        public OpeningGenerationController(OpeningDataService openingDataService, ILogger<OpeningGenerationController> logger)
        {
            _openingDataService = openingDataService;
            _logger = logger;
        }

        // GET: api/openings/generate
        [HttpGet("generate")]
        public async Task<IActionResult> GenerateOpeningMoves()
        {
            _logger.LogInformation("Received request to generate opening moves via API.");
            try
            {
                await _openingDataService.SeedDatabaseAsync();
                _logger.LogInformation("Successfully initiated opening moves generation via API.");
                return Ok("Opening moves generation process started successfully.");
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error occurred while generating opening moves via API.");
                return StatusCode(500, $"An error occurred: {ex.Message}");
            }
        }
    }
} 