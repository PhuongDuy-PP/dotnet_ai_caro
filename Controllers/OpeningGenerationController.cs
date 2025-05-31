using Microsoft.AspNetCore.Mvc;
using CaroAIServer.Services;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CaroAIServer.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OpeningGenerationController : ControllerBase
    {
        private readonly OpeningDataService _openingDataService;
        private readonly OpeningGenerationService _openingGenerationService;
        private readonly ILogger<OpeningGenerationController> _logger;

        public OpeningGenerationController(
            OpeningDataService openingDataService, 
            OpeningGenerationService openingGenerationService,
            ILogger<OpeningGenerationController> logger)
        {
            _openingDataService = openingDataService;
            _openingGenerationService = openingGenerationService;
            _logger = logger;
        }

        // POST: api/OpeningGeneration/SeedDatabase
        [HttpPost("SeedDatabase")]
        public async Task<IActionResult> SeedDatabase()
        {
            _logger.LogInformation("SeedDatabase endpoint called.");
            try
            {
                await _openingDataService.SeedDatabaseAsync();
                _logger.LogInformation("Database seeding process completed successfully.");
                return Ok("Database seeding process initiated and completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during database seeding.");
                return StatusCode(500, $"An error occurred during database seeding: {ex.Message}");
            }
        }

        // GET: api/OpeningGeneration/generation-status
        [HttpGet("generation-status")]
        public IActionResult GetGenerationStatus()
        {
            try
            {
                var status = new 
                {
                    CurrentGenerationStatus = OpeningGenerationService.CurrentGenerationStatus,
                    CurrentMoves = OpeningGenerationService.CurrentGeneratingMoves
                };
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching generation status.");
                return StatusCode(500, "Error fetching generation status");
            }
        }

        // DTO for the /generate endpoint payload
        public class GenerateRequestDto
        {
            public int NumberOfGames { get; set; }
            public int MaxMovesPerSequence { get; set; }
            public int StartingPlayer { get; set; } = 1; // Default to player 1
        }

        // POST: api/OpeningGeneration/generate
        [HttpPost("generate")]
        public async Task<IActionResult> GenerateOpenings([FromBody] GenerateRequestDto request)
        {
            if (request == null)
            {
                return BadRequest("Request payload is null.");
            }
            _logger.LogInformation($"Generate openings called: Games={request.NumberOfGames}, MaxMoves={request.MaxMovesPerSequence}, StartPlayer={request.StartingPlayer}");
            try
            {
                await _openingGenerationService.GenerateOpeningsAsync(request.NumberOfGames, request.MaxMovesPerSequence, request.StartingPlayer);
                return Ok("Opening generation process started successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during opening generation.");
                return StatusCode(500, $"An error occurred during opening generation: {ex.Message}");
            }
        }
    }
} 