using System.Text.Json;
using MovieStoreShowcase.Models;

namespace MovieStoreShowcase.Services;

/// <summary>
/// Reads locale/region word-list configs from Data/Locales/*.json.
/// Adding a new region = dropping in a new JSON file, no code changes.
/// </summary>
public class LocaleRepository
{
    private readonly Dictionary<string, LocaleConfig> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _localesFolder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LocaleRepository(IWebHostEnvironment env)
    {
        _localesFolder = Path.Combine(env.ContentRootPath, "Data", "Locales");
        LoadAll();
    }

    private void LoadAll()
    {
        if (!Directory.Exists(_localesFolder))
            throw new DirectoryNotFoundException($"Locale folder not found: {_localesFolder}");

        foreach (var file in Directory.GetFiles(_localesFolder, "*.json"))
        {
            var json = File.ReadAllText(file);
            var config = JsonSerializer.Deserialize<LocaleConfig>(json, JsonOptions);
            
            if (config != null && !string.IsNullOrWhiteSpace(config.Code))
            {
                _cache[config.Code] = config;
            }
        }

        if (_cache.Count == 0)
            throw new InvalidOperationException("No locale configs were loaded.");
    }

    public IEnumerable<(string Code, string DisplayName)> ListAvailable() =>
        _cache.Values.Select(c => (c.Code, c.DisplayName)).OrderBy(c => c.Code);

    public LocaleConfig Get(string code)
    {
        if (_cache.TryGetValue(code, out var cfg)) return cfg;
        // fall back to first available (e.g. en-US) rather than throwing on a typo
        return _cache.Values.First();
    }
}