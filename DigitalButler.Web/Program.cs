using DigitalButler.Context;
using DigitalButler.Common;
using DigitalButler.Data;
using DigitalButler.Data.Repositories;
using DigitalButler.Skills;
using DigitalButler.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography;
using System.Text;
using DigitalButler.Telegram;
using DigitalButler.Web;
using System.IO;
using System.Text.RegularExpressions;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

// Persist DataProtection keys to a volume when running in containers.
// This prevents antiforgery token decryption errors after container restarts.
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(dataProtectionKeysPath) && Directory.Exists("/data"))
{
    dataProtectionKeysPath = "/data/dpkeys";
}

if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Dapper type handlers (TimeOnly, DateTimeOffset)
DapperTypeHandlers.Register();

// Support simple env-var names (AI_BASE_URL, BUTLER_ADMIN_PASSWORD, etc.) by mapping them
// into the configuration keys used by options binding and app code.
var envOverrides = new Dictionary<string, string?>();

static void MapEnv(Dictionary<string, string?> dict, string envVar, string configKey)
{
    var value = Environment.GetEnvironmentVariable(envVar);
    if (!string.IsNullOrWhiteSpace(value))
    {
        dict[configKey] = value;
    }
}

MapEnv(envOverrides, "AI_BASE_URL", "AiDefaults:BaseUrl");
MapEnv(envOverrides, "AI_MODEL", "AiDefaults:Model");
MapEnv(envOverrides, "AI_API_KEY", "AiDefaults:ApiKey");

MapEnv(envOverrides, "BUTLER_ADMIN_USERNAME", "Auth:Username");
MapEnv(envOverrides, "BUTLER_ADMIN_PASSWORD", "Auth:Password");
MapEnv(envOverrides, "BUTLER_ADMIN_PASSWORD_HASH", "Auth:PasswordHash");

MapEnv(envOverrides, "BUTLER_TIMEZONE", "Butler:TimeZone");

MapEnv(envOverrides, "TELEGRAM_CHAT_ID", "Telegram:ChatId");

MapEnv(envOverrides, "GCAL_ICAL_URLS", "GoogleCalendar:IcalUrls");
MapEnv(envOverrides, "GCAL_FEED1_NAME", "GoogleCalendar:IcalFeeds:0:Name");
MapEnv(envOverrides, "GCAL_FEED1_URL", "GoogleCalendar:IcalFeeds:0:Url");

// Support GCAL_FEED{N}_NAME / GCAL_FEED{N}_URL (N = 1..n)
// Example: GCAL_FEED2_NAME -> GoogleCalendar:IcalFeeds:1:Name
//          GCAL_FEED2_URL  -> GoogleCalendar:IcalFeeds:1:Url
var feedEnvRegex = new Regex(@"^GCAL_FEED(?<n>\d+)_(?<kind>NAME|URL)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
{
    if (entry.Key is not string key)
    {
        continue;
    }

    var match = feedEnvRegex.Match(key);
    if (!match.Success)
    {
        continue;
    }

    if (!int.TryParse(match.Groups["n"].Value, out var feedNumber) || feedNumber <= 0)
    {
        continue;
    }

    var value = entry.Value?.ToString();
    if (string.IsNullOrWhiteSpace(value))
    {
        continue;
    }

    var index = feedNumber - 1;
    var kind = match.Groups["kind"].Value;
    var configKey = kind.Equals("NAME", StringComparison.OrdinalIgnoreCase)
        ? $"GoogleCalendar:IcalFeeds:{index}:Name"
        : $"GoogleCalendar:IcalFeeds:{index}:Url";

    envOverrides[configKey] = value;
}

// Gmail env vars (env-vars-only multi-account format)
MapEnv(envOverrides, "GMAIL_ACCOUNTS", "Gmail:Accounts");
MapEnv(envOverrides, "GMAIL_HOST", "Gmail:Host");
MapEnv(envOverrides, "GMAIL_PORT", "Gmail:Port");
MapEnv(envOverrides, "GMAIL_USE_SSL", "Gmail:UseSsl");
MapEnv(envOverrides, "GMAIL_UNREAD_ONLY_DEFAULT", "Gmail:UnreadOnlyDefault");
MapEnv(envOverrides, "GMAIL_DAYS_BACK_DEFAULT", "Gmail:DaysBackDefault");
MapEnv(envOverrides, "GMAIL_MAX_MESSAGES_DEFAULT", "Gmail:MaxMessagesDefault");

if (envOverrides.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(envOverrides);
}

var sqlitePath = builder.Configuration["Database:SqlitePath"] ?? "data/butler.db";
var sqliteConnectionString = $"Data Source={sqlitePath}";
builder.Services.AddSingleton<IButlerDb>(_ => new SqliteButlerDb(sqliteConnectionString));
builder.Services.AddSingleton<ButlerSchemaInitializer>();

builder.Services.AddScoped<ContextRepository>();
builder.Services.AddScoped<InstructionRepository>();
builder.Services.AddScoped<SkillInstructionRepository>();
builder.Services.AddScoped<AiTaskSettingRepository>();
builder.Services.AddScoped<ScheduleRepository>();
builder.Services.AddScoped<GoogleCalendarFeedRepository>();
builder.Services.AddScoped<AppSettingsRepository>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var aiDefaults = builder.Configuration.GetSection("AiDefaults");
builder.Services.Configure<AiDefaults>(aiDefaults);

builder.Services.Configure<ButlerOptions>(builder.Configuration.GetSection("Butler"));

builder.Services.Configure<GoogleCalendarOptions>(builder.Configuration.GetSection("GoogleCalendar"));
builder.Services.Configure<GmailOptions>(builder.Configuration.GetSection("Gmail"));
builder.Services.AddHttpClient<ISummarizationService, OpenAiSummarizationService>();
builder.Services.AddHttpClient<ISkillRouter, OpenAiSkillRouter>();
builder.Services.AddScoped<AiSettingsResolver>();
builder.Services.AddHostedService<SchedulerService>();

// Context sources
builder.Services.AddScoped<PersonalContextSource>();
builder.Services.AddHttpClient<GoogleCalendarContextSource>();
builder.Services.AddScoped<GmailContextSource>();

// Context updater registry - allows lookup by source
builder.Services.AddScoped<IContextUpdaterRegistry, ContextUpdaterRegistry>();

// TelegramBotClient singleton - reused by scheduler instead of recreating each tick
// Registered as optional (null if no token configured)
var telegramToken = builder.Configuration["TELEGRAM_BOT_TOKEN"];
var telegramAllowedUserId = builder.Configuration["TELEGRAM_ALLOWED_USER_ID"];
if (!string.IsNullOrWhiteSpace(telegramToken) && !string.IsNullOrWhiteSpace(telegramAllowedUserId))
{
    builder.Services.AddHostedService<BotService>();
}
if (!string.IsNullOrWhiteSpace(telegramToken))
{
    builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(telegramToken));
}

builder.Services.AddScoped<ContextService>();
builder.Services.AddScoped<InstructionService>();
builder.Services.AddScoped<SkillInstructionService>();
builder.Services.AddScoped<AiTaskSettingsService>();
builder.Services.AddScoped<TimeZoneService>();
builder.Services.AddSingleton<IManualSyncRunner, ManualSyncRunner>();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health").AllowAnonymous();

app.MapPost("/login", async (HttpContext http) =>
{
    var username = http.Request.Form["username"].ToString();
    var password = http.Request.Form["password"].ToString();
    var expectedUser = http.RequestServices.GetRequiredService<IConfiguration>()["Auth:Username"];
    var expectedPass = http.RequestServices.GetRequiredService<IConfiguration>()["Auth:Password"];
    var expectedHash = http.RequestServices.GetRequiredService<IConfiguration>()["Auth:PasswordHash"];
    var isValid = expectedHash is not null
        ? Hash(password) == expectedHash
        : password == expectedPass;
    if (username == expectedUser && isValid)
    {
        var claims = new[] { new Claim(ClaimTypes.Name, username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect("/");
    }
        return Results.Redirect("/login?error=1");
}).AllowAnonymous();

app.MapGet("/login", (HttpContext http) =>
{
        var showError = http.Request.Query.ContainsKey("error");
        var errorHtml = showError
                ? "<div class=\"notification is-danger is-light\">Invalid username or password.</div>"
                : string.Empty;

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Login</title>
    <link rel="stylesheet" href="/lib/bulma/bulma.min.css" />
    <link rel="stylesheet" href="/app.css" />
    <link rel="stylesheet" href="/DigitalButler.Web.styles.css" />
</head>
<body>
    <section class="section">
        <div class="container" style="max-width: 520px;">
            <div class="content">
                <h1 class="title is-3">Login</h1>
                <p class="subtitle is-6">Sign in to continue.</p>
            </div>

            {{errorHtml}}

            <div class="box">
                <form method="post" action="/login">
                    <div class="field">
                        <label class="label" for="username">Username</label>
                        <div class="control">
                            <input class="input" id="username" name="username" autocomplete="username" />
                        </div>
                    </div>

                    <div class="field">
                        <label class="label" for="password">Password</label>
                        <div class="control">
                            <input class="input" id="password" name="password" type="password" autocomplete="current-password" />
                        </div>
                    </div>

                    <div class="buttons">
                        <button class="button is-primary" type="submit">Login</button>
                    </div>
                </form>
            </div>
        </div>
    </section>
</body>
</html>
""";

        return Results.Content(html, "text/html");
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

using (var scope = app.Services.CreateScope())
{
    var schema = scope.ServiceProvider.GetRequiredService<ButlerSchemaInitializer>();
    schema.EnsureCreatedAsync().GetAwaiter().GetResult();
}

app.Run();

string Hash(string value)
{
    using var sha = SHA256.Create();
    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes);
}
