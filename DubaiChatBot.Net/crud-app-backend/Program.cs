using crud_app_backend;
using crud_app_backend.Bot.Services;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

// PERF FIX: AddDbContextPool instead of AddDbContext.
// Reuses DbContext instances from a pool — SQL connections stay open and warm.
// Eliminates per-request connection setup cost (~3-5s for new users on cold start).
builder.Services.AddDbContextPool<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")),
    poolSize: 128);

// ── Existing repositories ─────────────────────────────────────────────────────
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IFeedbackRepository, FeedbackRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IWhatsAppSessionRepository, WhatsAppSessionRepository>();
builder.Services.AddScoped<IWhatsAppMessageRepository, WhatsAppMessageRepository>();
builder.Services.AddScoped<IWhatsAppComplaintRepository, WhatsAppComplaintRepository>();

// ── Existing services ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IWhatsAppSessionService, WhatsAppSessionService>();
builder.Services.AddScoped<IWhatsAppMessageService, WhatsAppMessageService>();
builder.Services.AddScoped<IWhatsAppComplaintService, WhatsAppComplaintService>();

// ── NEW Bot services ──────────────────────────────────────────────────────────
// BotStateService is Singleton — holds per-user locks and image timestamps
// that must be shared across all HTTP requests (BotService is Scoped).
builder.Services.AddSingleton<BotStateService>();   // per-user locks + image timestamps
builder.Services.AddSingleton<WebhookQueue>();       // Channel for instant 200 OK
builder.Services.AddScoped<IBotService, BotService>();
builder.Services.AddScoped<IDialogClient, DialogClient>();
builder.Services.AddScoped<IHrisService, HrisService>();

// ── Background services ───────────────────────────────────────────────────────
// WebhookProcessorService: reads WebhookQueue, processes messages in background
builder.Services.AddHostedService<WebhookProcessorService>();
// KeepAliveService: pings DB + warms session cache every 3 min
builder.Services.AddHostedService<KeepAliveService>();

// ── HTTP clients ──────────────────────────────────────────────────────────────

// CRM client — 60s timeout
builder.Services.AddHttpClient("CrmClient", client =>
{
    var key = builder.Configuration["Crm:ApiKey"];
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Add("access-token", key);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// 360dialog client
builder.Services.AddHttpClient("Dialog", client =>
{
    var key = builder.Configuration["Dialog:ApiKey"];
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Add("D360-API-KEY", key);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// HRIS client
builder.Services.AddHttpClient("Hris", client =>
{
    var basicAuth = builder.Configuration["Hris:BasicAuth"];
    var apiKey = builder.Configuration["Hris:ApiKey"];
    if (!string.IsNullOrWhiteSpace(basicAuth))
        client.DefaultRequestHeaders.Add("Authorization", basicAuth);
    if (!string.IsNullOrWhiteSpace(apiKey))
        client.DefaultRequestHeaders.Add("S_KEYSD", apiKey);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// ── Form limits ───────────────────────────────────────────────────────────────
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 25 * 1024 * 1024; // 25 MB
});

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── EF Core warm-up ───────────────────────────────────────────────────────────
// Run one dummy query at startup so EF compiles its query model and opens
// the first SQL connection BEFORE any user messages arrive.
// Without this, the very first user after app restart pays a 3-5s cold-start tax.
using (var warmupScope = app.Services.CreateScope())
{
    var db = warmupScope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await db.WhatsAppSessions
            .AsNoTracking()
            .Select(s => s.Phone)
            .FirstOrDefaultAsync();
    }
    catch { /* DB may not be reachable at startup — that is fine, just warming */ }
}

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowAngular");
app.UseStaticFiles();       // serves wwwroot/wa-media/ and wwwroot/images/ as public URLs
app.UseAuthorization();
app.MapControllers();

app.Run();
