using System.IO.Compression;
using MovieStoreShowcase.Services;

// Fix for Render inotify limit by disabling content root reloading watch
var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory()
};

var builder = WebApplication.CreateBuilder(options);

// Render Port Configuration
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

builder.Services.AddSingleton<LocaleRepository>();
builder.Services.AddSingleton<MovieGeneratorService>();
builder.Services.AddSingleton<AiClipService>();
builder.Services.AddSingleton<TrailerGeneratorService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

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
            hint = "ffmpeg was not found on PATH."
        });
    }
});

app.MapGet("/api/health/falai", (AiClipService aiClips) =>
{
    return Results.Ok(new
    {
        configured = aiClips.IsConfigured,
        hint = aiClips.IsConfigured ? "fal.ai is configured" : "fal.ai is not configured"
    });
});

app.MapGet("/api/locales", (LocaleRepository locales) =>
{
    var list = locales.ListAvailable().Select(l => new { code = l.Code, displayName = l.DisplayName });
    return Results.Ok(list);
});

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

    long startIndex = cursor + 1;
    var items = new List<MovieStoreShowcase.Models.MovieDto>(batchSize);
    for (long i = startIndex; i < startIndex + batchSize; i++)
        items.Add(generator.GenerateSingle(region, seed, i, likes, reviews));

    return Results.Ok(new { items, nextCursor = cursor + batchSize });
});

app.MapGet("/api/movies/{index:long}/trailer", async (
    long index,
    string region,
    long seed,
    MovieGeneratorService generator,
    TrailerGeneratorService trailers) =>
{
    try
    {
        var movie = generator.GenerateSingle(region, seed, index, 0, 0);
        var path = await trailers.GetOrCreateTrailerAsync(region, seed, index, movie.Title, movie.Genre, movie.Year);
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return Results.Problem("ffmpeg was not found on PATH.", statusCode: 500);
    }
});

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
        return Results.Problem("ffmpeg was not found on PATH.", statusCode: 500);
    }
});

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
        return Results.Problem("ffmpeg was not found on PATH.", statusCode: 500);
    }
});

app.Run();