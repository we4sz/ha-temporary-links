using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration binding
builder.Services.Configure<AddonConfiguration>(
    builder.Configuration.GetSection(AddonConfiguration.SectionName));
builder.Services.Configure<TwilioConfiguration>(
    builder.Configuration.GetSection(TwilioConfiguration.SectionName));

// Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=/data/temporarylinks.db"));

// HTTP client for Home Assistant
builder.Services.AddHttpClient<IHomeAssistantService, HomeAssistantService>();

// Services
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<ITwilioService, TwilioService>();
builder.Services.AddScoped<ILinkService, LinkService>();

// Background service for link expiration
builder.Services.AddHostedService<LinkExpirationService>();

// Razor Pages and Controllers
builder.Services.AddRazorPages();
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Ensure database is created and migrated
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.EnsureCreated();
}

// Handle ingress path prefix from Home Assistant
app.Use(async (context, next) =>
{
    var ingressPath = context.Request.Headers["X-Ingress-Path"].FirstOrDefault();
    if (!string.IsNullOrEmpty(ingressPath))
    {
        context.Request.PathBase = ingressPath.TrimEnd('/');
    }
    await next();
});

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapControllers();

app.Run();
