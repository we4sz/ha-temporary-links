using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Services;

var builder = WebApplication.CreateBuilder(args);

// Load Home Assistant addon options from /data/options.json
var optionsPath = "/data/options.json";
if (File.Exists(optionsPath))
{
    builder.Configuration.AddJsonFile(optionsPath, optional: true, reloadOnChange: true);
}

// Configure forwarded headers for running behind HA ingress proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configuration binding
builder.Services.Configure<AddonConfiguration>(options =>
{
    // Bind from HomeAssistant section
    builder.Configuration.GetSection(AddonConfiguration.SectionName).Bind(options);

    // Override with addon options from root level (from /data/options.json)
    // Map ha_url -> HaUrl
    var haUrl = builder.Configuration["ha_url"];
    if (!string.IsNullOrWhiteSpace(haUrl))
    {
        options.HaUrl = haUrl;
    }

    // Map ha_token -> HaToken
    var haToken = builder.Configuration["ha_token"];
    if (!string.IsNullOrWhiteSpace(haToken))
    {
        options.HaToken = haToken;
    }

    // Map default_message_template -> DefaultMessageTemplate
    var defaultMessageTemplate = builder.Configuration["default_message_template"];
    if (!string.IsNullOrWhiteSpace(defaultMessageTemplate))
    {
        options.DefaultMessageTemplate = defaultMessageTemplate;
    }
});

builder.Services.Configure<TwilioConfiguration>(options =>
{
    // Bind from Twilio section
    builder.Configuration.GetSection(TwilioConfiguration.SectionName).Bind(options);

    // Override with addon options from root level
    var twilioAccountSid = builder.Configuration["twilio_account_sid"];
    if (!string.IsNullOrWhiteSpace(twilioAccountSid))
    {
        options.AccountSid = twilioAccountSid;
    }

    var twilioAuthToken = builder.Configuration["twilio_auth_token"];
    if (!string.IsNullOrWhiteSpace(twilioAuthToken))
    {
        options.AuthToken = twilioAuthToken;
    }

    var twilioPhoneNumber = builder.Configuration["twilio_phone_number"];
    if (!string.IsNullOrWhiteSpace(twilioPhoneNumber))
    {
        options.PhoneNumber = twilioPhoneNumber;
    }
});

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

// Background services
builder.Services.AddHostedService<LinkExpirationService>();
builder.Services.AddHostedService<HaEventListenerService>();

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

// Handle forwarded headers from HA ingress proxy
app.UseForwardedHeaders();

// Handle ingress path prefix from Home Assistant
app.Use(async (context, next) =>
{
    var ingressPath = context.Request.Headers["X-Ingress-Path"].FirstOrDefault();
    if (!string.IsNullOrEmpty(ingressPath))
    {
        context.Request.PathBase = ingressPath.TrimEnd('/');
    }

    // Normalize double slashes in path (HA ingress sends // for root)
    var path = context.Request.Path.Value;
    if (!string.IsNullOrEmpty(path) && path.StartsWith("//"))
    {
        context.Request.Path = path.TrimStart('/').Insert(0, "/");
    }

    await next();
});

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapControllers();

app.Run();
