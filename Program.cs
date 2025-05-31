using CaroAIServer.Components;
using Microsoft.EntityFrameworkCore;
using CaroAIServer.Data;
using CaroAIServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register ApplicationDbContext
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers();

builder.Services.AddScoped<OpeningDataService>();
builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<AIService>();
builder.Services.AddScoped<OpeningGenerationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    // app.UseHsts(); // Consider re-enabling HSTS if not behind a terminating proxy
}

// app.UseHttpsRedirection(); // Consider if HTTPS is enforced elsewhere

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapControllers();

/*
// Seed initial data - REMOVED/COMMENTED OUT
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var openingDataService = services.GetRequiredService<OpeningDataService>();
        await openingDataService.SeedDatabaseAsync();
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while seeding the database.");
    }
}
*/

// Endpoint to manually trigger database seeding
app.MapGet("/admin/seed-opening-data", async (HttpContext context, IServiceProvider serviceProvider) =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Received request to /admin/seed-opening-data.");

    // Run the seeding process in a background task to avoid blocking the HTTP request
    _ = Task.Run(async () =>
    {
        using (var scope = serviceProvider.CreateScope()) // Create a new scope for the background task
        {
            var scopedOpeningDataService = scope.ServiceProvider.GetRequiredService<OpeningDataService>();
            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            try
            {
                scopedLogger.LogInformation("Starting database seeding via /admin/seed-opening-data endpoint (background task).");
                await scopedOpeningDataService.SeedDatabaseAsync();
                scopedLogger.LogInformation("Database seeding via /admin/seed-opening-data endpoint (background task) completed.");
            }
            catch (Exception ex)
            {
                scopedLogger.LogError(ex, "An error occurred while seeding the database via /admin/seed-opening-data endpoint (background task).");
            }
        }
    });

    await context.Response.WriteAsync("Opening data seeding process has been initiated in the background. Check server logs for progress and completion. It may take a very long time.");
});

app.Run();
