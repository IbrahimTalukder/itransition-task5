namespace MovieStoreShowcase.Models;

/// <summary>
/// Raw locale/config data loaded from Data/Locales/*.json.
/// Nothing region-specific ever lives in code - it all comes from here.
/// </summary>
public class LocaleConfig
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> TitleAdjectives { get; set; } = new();
    public List<string> TitleNouns { get; set; } = new();
    public List<string> TitlePatterns { get; set; } = new();
    public List<string> FirstNames { get; set; } = new();
    public List<string> LastNames { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<string> CompanyNames { get; set; } = new();
    public List<string> ReviewPhrases { get; set; } = new();
}

public class ReviewDto
{
    public string Author { get; set; } = "";
    public string Company { get; set; } = "";
    public string Text { get; set; } = "";
}

public class MovieDto
{
    public long Index { get; set; }
    public string Title { get; set; } = "";
    public List<string> Cast { get; set; } = new();
    public string Director { get; set; } = "";
    public int Year { get; set; }
    public string Genre { get; set; } = "";
    public string Description { get; set; } = "";
    public int DurationMinutes { get; set; }
    public string AgeRating { get; set; } = "";
    public int Likes { get; set; }
    public List<ReviewDto> Reviews { get; set; } = new();
    public string TrailerUrl { get; set; } = "";
    public string TrailerPosterUrl { get; set; } = "";
    public string PosterHex { get; set; } = "";
    public bool IsSeries { get; set; }
    public List<SeasonDto> Seasons { get; set; } = new();
}

public class SeasonDto
{
    public int SeasonNumber { get; set; }
    public int EpisodeCount { get; set; }
}

public class MoviePageResult
{
    public List<MovieDto> Items { get; set; } = new();
    public long Page { get; set; }
    public int PageSize { get; set; }
}
