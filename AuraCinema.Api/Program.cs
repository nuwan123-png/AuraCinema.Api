using System.Threading.RateLimiting;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// 1. Core Services & Swagger Authorize Setup
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API Key එක මෙතන දාන්න (උදා: AuraCinema_Secret_Key_2026)",
        Type = SecuritySchemeType.ApiKey,
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Scheme = "ApiKeyScheme"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddMemoryCache();

// 2. CORS (Web App එකට API එක Call කිරීමට අවසර දීම)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAuraWeb", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// 3. Network Rate Limiting (1 IP එකකට විනාඩියකට උපරිම Requests 60)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("api_limit", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 60;
        opt.QueueLimit = 0;
    });
});

// 4. Supabase Connection Pooler (Fixed Singleton Registration)
var connectionString = builder.Configuration.GetConnectionString("SupabaseDb");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString!));

// 5. Cloudflare R2 Client Setup
var r2Config = builder.Configuration.GetSection("CloudflareR2");
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var credentials = new BasicAWSCredentials(r2Config["AccessKey"], r2Config["SecretKey"]);
    var config = new AmazonS3Config
    {
        ServiceURL = $"https://{r2Config["AccountId"]}.r2.cloudflarestorage.com",
        ForcePathStyle = true
    };
    return new AmazonS3Client(credentials, config);
});

var app = builder.Build();

app.UseCors("AllowAuraWeb");
app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();

// 6. API Key Security Middleware
var validApiKey = builder.Configuration["ApiKey"];
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/swagger"))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-API-Key", out var extractedApiKey) || extractedApiKey != validApiKey)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized: Invalid API Key" });
        return;
    }
    await next();
});

var api = app.MapGroup("/api/v1").RequireRateLimiting("api_limit");

// ==========================================================
// 1. HOME SCREEN DATA (Cached for 10 mins)
// ==========================================================
api.MapGet("/home", async (NpgsqlDataSource db, IMemoryCache cache) =>
{
    const string cacheKey = "home_data";
    if (cache.TryGetValue(cacheKey, out object? cachedHome))
        return Results.Ok(cachedHome);

    await using var conn = await db.OpenConnectionAsync();

    var latestMovies = await conn.QueryAsync(
        @"SELECT id, title, image, year, type, genre, watch_with_sub 
          FROM movies WHERE type = 'movie' ORDER BY created_at DESC LIMIT 12");

    var latestSeries = await conn.QueryAsync(
        @"SELECT id, title, image, year, type, genre, watch_with_sub 
          FROM movies WHERE type != 'movie' ORDER BY created_at DESC LIMIT 12");

    var result = new { latestMovies, latestSeries };
    cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
    return Results.Ok(result);
});

// ==========================================================
// 2. MOVIES / SERIES LIST WITH PAGINATION & FILTERS
// ==========================================================
api.MapGet("/movies", async (string? type, string? genre, bool? subOnly, int page, NpgsqlDataSource db, IMemoryCache cache) =>
{
    page = page < 1 ? 1 : page;
    int pageSize = 20;
    int offset = (page - 1) * pageSize;
    string cacheKey = $"movies_{type}_{genre}_{subOnly}_p{page}";

    if (cache.TryGetValue(cacheKey, out object? cachedList))
        return Results.Ok(cachedList);

    await using var conn = await db.OpenConnectionAsync();

    var conditions = new List<string>();
    if (!string.IsNullOrEmpty(type)) conditions.Add("type = @Type");
    if (!string.IsNullOrEmpty(genre)) conditions.Add("genre ILIKE @Genre");
    if (subOnly == true) conditions.Add("watch_with_sub = true");

    string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    string sql = $@"SELECT id, title, image, year, type, genre, watch_with_sub 
                    FROM movies {whereClause} ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset";

    var movies = await conn.QueryAsync(sql, new { Type = type, Genre = $"%{genre}%", Limit = pageSize, Offset = offset });

    cache.Set(cacheKey, movies, TimeSpan.FromMinutes(10));
    return Results.Ok(movies);
});

// ==========================================================
// 3. SINGLE MOVIE DETAILS + EPISODES
// ==========================================================
api.MapGet("/movies/{id:long}", async (long id, NpgsqlDataSource db, IMemoryCache cache) =>
{
    string cacheKey = $"movie_detail_{id}";
    if (cache.TryGetValue(cacheKey, out object? cachedDetail))
        return Results.Ok(cachedDetail);

    await using var conn = await db.OpenConnectionAsync();

    var movie = await conn.QueryFirstOrDefaultAsync("SELECT * FROM movies WHERE id = @Id", new { Id = id });
    if (movie == null) return Results.NotFound(new { message = "Movie not found" });

    var episodes = await conn.QueryAsync(
        "SELECT id, number, url, subtitle_url, download_url, telegram_url FROM episodes WHERE movie_id = @Id ORDER BY number ASC",
        new { Id = id });

    var detail = new { movie, episodes };
    cache.Set(cacheKey, detail, TimeSpan.FromMinutes(15));
    return Results.Ok(detail);
});

// ==========================================================
// 4. FAST SEARCH (Uses GIN Trigram Index)
// ==========================================================
api.MapGet("/search", async (string q, NpgsqlDataSource db, IMemoryCache cache) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        return Results.BadRequest(new { message = "Query must be at least 2 characters" });

    string normalizedQuery = q.Trim().ToLower();
    string cacheKey = $"search_{normalizedQuery}";

    if (cache.TryGetValue(cacheKey, out object? cachedSearch))
        return Results.Ok(cachedSearch);

    await using var conn = await db.OpenConnectionAsync();
    var results = await conn.QueryAsync(
        @"SELECT id, title, image, year, type, watch_with_sub 
          FROM movies WHERE title ILIKE @Pattern ORDER BY created_at DESC LIMIT 20",
        new { Pattern = $"%{normalizedQuery}%" });

    cache.Set(cacheKey, results, TimeSpan.FromMinutes(5));
    return Results.Ok(results);
});

// ==========================================================
// 5. SUBMIT MOVIE REQUEST (From Android App & Web)
// ==========================================================
api.MapPost("/requests", async (MovieRequestDto req, NpgsqlDataSource db) =>
{
    if (string.IsNullOrWhiteSpace(req.Title))
        return Results.BadRequest(new { message = "Title is required" });

    await using var conn = await db.OpenConnectionAsync();
    await conn.ExecuteAsync(
        "INSERT INTO movie_requests (title, note, created_at, updated_at) VALUES (@Title, @Note, NOW(), NOW())",
        new { req.Title, req.Note });

    return Results.Ok(new { message = "Request submitted successfully!" });
});

// ==========================================================
// 6. RECORD PAGE VIEW (Stats Analytics for Mobile & Web)
// ==========================================================
api.MapPost("/stats/view", async (PageViewDto view, HttpContext ctx, NpgsqlDataSource db) =>
{
    string visitorIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    await using var conn = await db.OpenConnectionAsync();

    await conn.ExecuteAsync(
        @"INSERT INTO page_views (viewed_on, visitor, path, movie_id, referrer_host, device, created_at) 
          VALUES (CURRENT_DATE, @Visitor, @Path, @MovieId, @Referrer, @Device, NOW())",
        new
        {
            Visitor = visitorIp,
            Path = view.Path ?? "/app",
            view.MovieId,
            Referrer = view.ReferrerHost ?? "android_app",
            Device = view.Device ?? "mobile"
        });

    return Results.Ok();
});

// ==========================================================
// 7. APP CONFIG & MONETIZATION (Ads & VIP Control)
// ==========================================================
api.MapGet("/config", () =>
{
    return Results.Ok(new
    {
        minAppVersion = 1,
        latestAppVersion = 1,
        adsEnabled = true,
        admobBannerId = "ca-app-pub-xxxxxxxxxxxxxxxx/xxxxxxxxxx",
        admobInterstitialId = "ca-app-pub-xxxxxxxxxxxxxxxx/xxxxxxxxxx",
        telegramChannel = "https://t.me/auracinema"
    });
});

// ==========================================================
// 8. ADMIN: CLOUDFLARE R2 UPLOAD (Posters & Subtitles)
// ==========================================================
api.MapPost("/admin/upload", async (IFormFile file, string folder, HttpRequest req, IConfiguration config, IAmazonS3 s3Client) =>
{
    if (!req.Headers.TryGetValue("X-Admin-Secret", out var secret) || secret != config["AdminSecret"])
        return Results.Unauthorized();

    if (file == null || file.Length == 0)
        return Results.BadRequest(new { message = "No file selected" });

    var allowedFolders = new[] { "posters", "subtitles" };
    folder = allowedFolders.Contains(folder?.ToLower()) ? folder!.ToLower() : "misc";

    string fileExt = Path.GetExtension(file.FileName);
    string key = $"{folder}/{Guid.NewGuid()}{fileExt}";
    string bucketName = config["CloudflareR2:BucketName"]!;
    string publicBaseUrl = config["CloudflareR2:PublicUrl"]!;

    using var stream = file.OpenReadStream();
    var putRequest = new PutObjectRequest
    {
        BucketName = bucketName,
        Key = key,
        InputStream = stream,
        ContentType = file.ContentType,
        DisablePayloadSigning = true
    };

    await s3Client.PutObjectAsync(putRequest);

    return Results.Ok(new { url = $"{publicBaseUrl}/{key}" });
}).DisableAntiforgery();

// ==========================================================
// 9. ADMIN: CLEAR CACHE (අලුත් Movie එකක් දැමූ විගස Call කිරීමට)
// ==========================================================
api.MapPost("/admin/clear-cache", (HttpRequest req, IConfiguration config, IMemoryCache cache) =>
{
    if (!req.Headers.TryGetValue("X-Admin-Secret", out var secret) || secret != config["AdminSecret"])
        return Results.Unauthorized();

    if (cache is MemoryCache concreteCache)
    {
        concreteCache.Compact(1.0);
    }

    return Results.Ok(new { message = "API Cache cleared successfully!" });
});

app.Run();

// DTO Models
record MovieRequestDto(string Title, string? Note);
record PageViewDto(string? Path, long? MovieId, string? ReferrerHost, string? Device);