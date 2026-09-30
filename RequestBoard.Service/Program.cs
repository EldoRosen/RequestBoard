using Microsoft.Extensions.Logging.Console;
using RequestBoard.Contracts;
using RequestBoard.Service;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    o.ColorBehavior = LoggerColorBehavior.Enabled;
});
var dbPath = Path.GetFullPath(builder.Configuration["DatabasePath"] ?? "requestboard.db", AppContext.BaseDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddSingleton(new Database(dbPath));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<DiscordNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordNotifier>());
builder.Services.AddSingleton<BoardService>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RequestBoard");

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception e)
    {
        log.LogError(e, "{Method} {Path} failed", ctx.Request.Method, ctx.Request.Path);
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new ApiResult { Ok = false, Message = "The request board service hit an internal error." });
    }
});

app.MapGet("/health", (SettingsStore s) => Results.Ok(new { ok = true, currency = s.Current.Currency }));
app.MapGet("/requests", (BoardService b) => b.ListOpen());
app.MapGet("/requests/{id:int}", (int id, BoardService b) => b.Get(id));
app.MapPost("/requests", (CreateCommand c, BoardService b) => b.Create(c));
app.MapPost("/requests/{id:int}/accept", (int id, AcceptCommand c, BoardService b) => b.Accept(id, c));
app.MapPost("/requests/{id:int}/deliver", (int id, PlayerCommand c, BoardService b) => b.Deliver(id, c));
app.MapPost("/requests/{id:int}/fail", (int id, PlayerCommand c, BoardService b) => b.Fail(id, c));
app.MapPost("/requests/{id:int}/cancel", (int id, PlayerCommand c, BoardService b) => b.Cancel(id, c));
app.MapPost("/requests/{id:int}/admin-cancel", (int id, AdminCommand c, BoardService b) => b.AdminCancel(id, c));
app.MapPost("/sync", (SyncCommand c, BoardService b) => b.Sync(c));
app.MapGet("/settings", (SettingsStore s) => s.Get());
app.MapPost("/settings", (SaveSettingsCommand c, SettingsStore s) => s.Save(c));

var settings = app.Services.GetRequiredService<SettingsStore>().Current;
log.LogInformation("Request Board service starting");
log.LogInformation("Database: {Path}", dbPath);
log.LogInformation("Rules: {Rules}", SettingsStore.Describe(settings));
log.LogInformation("Discord webhook: {State}", SettingsStore.WebhookState(settings));
log.LogInformation("Rules and the webhook are stored in the database, edit them from the Request Board tab in Torch.");

app.Run();
