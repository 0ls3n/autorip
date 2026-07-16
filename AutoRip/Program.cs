using Microsoft.EntityFrameworkCore;
using AutoRip.Components;
using AutoRip.Data;
using AutoRip.Hubs;
using AutoRip.Services;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["Database:Path"] ?? "Data/autorip.db";
var fullDbPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, dbPath));
var dbDir = Path.GetDirectoryName(fullDbPath);
if (!string.IsNullOrEmpty(dbDir))
    Directory.CreateDirectory(dbDir);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={fullDbPath}"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<RipHistoryService>();
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<MakeMkvService>();
builder.Services.AddSingleton<HandbrakeService>();
builder.Services.AddSingleton<DriveService>();
builder.Services.AddSingleton<TransferService>();
builder.Services.AddSingleton<RipOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RipOrchestrator>());
builder.Services.AddHttpClient<TmdbService>();
builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

var settings = app.Services.GetRequiredService<SettingsService>();
await settings.LoadAsync();

var driveService = app.Services.GetRequiredService<DriveService>();
driveService.StartPolling();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHub<ProgressHub>("/progress");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
