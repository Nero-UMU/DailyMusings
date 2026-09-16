using DailyMusings.Server.Composition;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDailyMusingsServer(builder.Configuration);
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Migrate and bootstrap before the first request is served, then wire the pipeline. Both steps live in
// WebApplicationExtensions so the integration tests run exactly this configuration.
await app.InitializeDailyMusingsAsync();
app.UseDailyMusings();

app.Run();
