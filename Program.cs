using System.IO.Compression;
using MovieStoreShowcase.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LocaleRepository>();
builder.Services.AddSingleton<MovieGeneratorService>();
builder.Services.AddSingleton<AiClipService>();
builder.Services.AddSingleton<TrailerGeneratorService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Show real exception details in the browser/response instead of a blank 500 -
// this is a learning-project API, not a public production service, so a
// detailed error page is far more useful than hiding it.
app.UseDeveloperExceptionPage();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

// ---- Diagnostics: is ffmpeg actually reachable on PATH? -------------------
// Hit this first if trailer/poster/export calls are failing - it tells you
// immediately whether the problem is "ffmpeg isn't installed / not on PATH"
// (the most common cause) versus something else.
app.MapGet("/api/health/ffmpeg", () =>
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        var firstLine = stdout.Split('\n').FirstOrDefault() ?? "";
        return Results.Ok(new { found = true, version = firstLine.Trim() });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            found = false,
            error = ex.Message,
            hint = "ffmpeg was not found on PATH. Install it and make sure the 'ffmpeg' command works in a *new* terminal window (PATH changes need a fresh shell/IDE restart to take effect)."
        });
    }
});

// ---- Diagnostics: is fal.ai (AI trailer clips) configured? ----------------
// Hit this to check whether AI-generated scenes are active or the app is
// falling back to the plain ffmpeg gradient scenes.
app.MapGet("/api/health/falai", (AiClipService aiClips) =>
{
    return Results.Ok(new
    {
        configured = aiClips.IsConfigured,
        hint = aiClips.IsConfigured
            ? "fal.ai is configured - trailers will try AI-generated clips first."
            : "fal.ai is not configured (FalAi:Enabled=false or ApiKey empty in appsettings.json) - falling back to gradient scenes."
    });
});

// ---- Locales -------------------------------------------------------------
// Regions are discovered from Data/Locales/*.json - add a file, get a new region,
// no code change needed (satisfies the "no hardcoded region data" requirement).
app.MapGet("/api/locales", (LocaleRepository locales) =>
{
    var list = locales.ListAvailable().Select(l => new { code = l.Code, displayName = l.DisplayName });
    return Results.Ok(list);
});

// ---- Table view (paginated) ----------------------------------------------
app.MapGet("/api/movies", (
    string region,
    long seed,
    long page,
    int pageSize,
    double likes,
    double reviews,
    MovieGeneratorService generator) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    likes = Math.Clamp(likes, 0, 10);
    reviews = Math.Clamp(reviews, 0, 10);

    var result = generator.GeneratePage(region, seed, page, pageSize, likes, reviews);
    return Results.Ok(result);
});

// ---- Gallery view (infinite scroll, cursor-based, no pagination UI) ------
app.MapGet("/api/movies/gallery", (
    string region,
    long seed,
    long cursor,
    int batchSize,
    double likes,
    double reviews,
    MovieGeneratorService generator) =>
{
    cursor = Math.Max(0, cursor);
    batchSize = Math.Clamp(batchSize, 1, 100);
    likes = Math.Clamp(likes, 0, 10);
    reviews = Math.Clamp(reviews, 0, 10);

    // reuse the same page generator: "page" here is purely a math convenience,
    // cursor is the 0-based count of items already loaded.
    long startIndex = cursor + 1;
    var items = new List<MovieStoreShowcase.Models.MovieDto>(batchSize);
    for (long i = startIndex; i < startIndex + batchSize; i++)
        items.Add(generator.GenerateSingle(region, seed, i, likes, reviews));

    return Results.Ok(new { items, nextCursor = cursor + batchSize });
});

// ---- Trailer (generated + cached mp4) -------------------------------------
app.MapGet("/api/movies/{index:long}/trailer", async (
    long index,
    string region,
    long seed,
    MovieGeneratorService generator,
    TrailerGeneratorService trailers) =>
{
    try
    {
        // Only core fields matter for a trailer (title/genre/year); likes/reviews
        // averages don't affect it, so 0s here are irrelevant and cheap.
        var movie = generator.GenerateSingle(region, seed, index, 0, 0);
        var path = await trailers.GetOrCreateTrailerAsync(region, seed, index, movie.Title, movie.Genre, movie.Year);
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return Results.Problem("ffmpeg was not found on PATH. Install ffmpeg and restart your terminal/IDE, then check GET /api/health/ffmpeg.", statusCode: 500);
    }
});

// ---- Trailer freeze-frame poster (Table View "before play" image) --------
app.MapGet("/api/movies/{index:long}/poster", async (
    long index,
    string region,
    long seed,
    MovieGeneratorService generator,
    TrailerGeneratorService trailers) =>
{
    try
    {
        var movie = generator.GenerateSingle(region, seed, index, 0, 0);
        var path = await trailers.GetOrCreatePosterAsync(region, seed, index, movie.Title, movie.Genre, movie.Year);
        return Results.File(path, "image/jpeg");
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return Results.Problem("ffmpeg was not found on PATH. Install ffmpeg and restart your terminal/IDE, then check GET /api/health/ffmpeg.", statusCode: 500);
    }
});

// ---- Optional: export current table page's trailers as a ZIP -------------
app.MapGet("/api/movies/export", async (
    string region,
    long seed,
    long page,
    int pageSize,
    MovieGeneratorService generator,
    TrailerGeneratorService trailers) =>
{
    try
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var pageResult = generator.GeneratePage(region, seed, page, pageSize, 0, 0);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var usedNames = new HashSet<string>();
            foreach (var movie in pageResult.Items)
            {
                var trailerPath = await trailers.GetOrCreateTrailerAsync(region, seed, movie.Index, movie.Title, movie.Genre, movie.Year);
                var safeName = string.Concat(movie.Title.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = $"movie_{movie.Index}";
                var finalName = safeName + ".mp4";
                int dupe = 1;
                while (!usedNames.Add(finalName)) finalName = $"{safeName} ({++dupe}).mp4";

                var entry = zip.CreateEntry(finalName, CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(trailerPath);
                await fileStream.CopyToAsync(entryStream);
            }
        }

        ms.Position = 0;
        return Results.File(ms.ToArray(), "application/zip", $"movies_page{page}.zip");
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return Results.Problem("ffmpeg was not found on PATH. Install ffmpeg and restart your terminal/IDE, then check GET /api/health/ffmpeg.", statusCode: 500);
    }
});

app.Run();
