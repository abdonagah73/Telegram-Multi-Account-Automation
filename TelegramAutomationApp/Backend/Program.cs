using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelegramAutomationApp.Backend.Database;
using TelegramAutomationApp.Backend.Hubs;
using TelegramAutomationApp.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// SignalR
builder.Services.AddSignalR();

// EF Core SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=Database/app_data.db"));

// Application Services
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<DispatcherQueue>();
builder.Services.AddSingleton<MemberAdderService>();
builder.Services.AddSingleton<MessagingService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Ensure Database is Created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseRouting();

app.MapControllers();
app.MapHub<AutomationHub>("/hubs/automation");

// Fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();
