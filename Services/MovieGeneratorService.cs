using Bogus;
using MovieStoreShowcase.Models;

namespace MovieStoreShowcase.Services;

public class MovieGeneratorService
{
    private readonly LocaleRepository _locales;
    private const int MinYear = 1978;

    public MovieGeneratorService(LocaleRepository locales)
    {
        _locales = locales;
    }

    /// <summary>
    /// Generates a page of movies. Core fields (title/cast/year/genre/director/
    /// description/trailer style) depend only on (seed, region, index) - never
    /// on likesAvg/reviewsAvg, per the "parameter independence" requirement.
    /// </summary>
    public MoviePageResult GeneratePage(string region, long userSeed, long page, int pageSize, double likesAvg, double reviewsAvg)
    {
        var config = _locales.Get(region);
        var items = new List<MovieDto>(pageSize);

        long startIndex = (page - 1) * pageSize + 1;
        for (long i = startIndex; i < startIndex + pageSize; i++)
        {
            items.Add(GenerateMovie(config, userSeed, i, likesAvg, reviewsAvg));
        }

        return new MoviePageResult { Items = items, Page = page, PageSize = pageSize };
    }

    public MovieDto GenerateSingle(string region, long userSeed, long index, double likesAvg, double reviewsAvg)
    {
        var config = _locales.Get(region);
        return GenerateMovie(config, userSeed, index, likesAvg, reviewsAvg);
    }

    private MovieDto GenerateMovie(LocaleConfig config, long userSeed, long index, double likesAvg, double reviewsAvg)
    {
        var coreSeed = DeterministicHash.Combine(userSeed, index, "core");
        var f = new Faker { Random = new Randomizer(coreSeed) };

        string adj1 = f.PickRandom(config.TitleAdjectives);
        string adj2 = f.PickRandom(config.TitleAdjectives);
        string noun1 = f.PickRandom(config.TitleNouns);
        string noun2 = f.PickRandom(config.TitleNouns);
        string pattern = f.PickRandom(config.TitlePatterns);

        string title = pattern
            .Replace("{Adj}", adj1)
            .Replace("{Adj2}", adj2)
            .Replace("{Noun}", noun1)
            .Replace("{Noun2}", noun2);

        int castSize = f.Random.Int(1, 4);
        var cast = new List<string>();
        for (int c = 0; c < castSize; c++)
            cast.Add($"{f.PickRandom(config.FirstNames)} {f.PickRandom(config.LastNames)}");

        string director = $"{f.PickRandom(config.FirstNames)} {f.PickRandom(config.LastNames)}";
        string genre = f.PickRandom(config.Genres);
        int year = f.Random.Int(MinYear, DateTime.UtcNow.Year + 1);
        int duration = f.Random.Int(75, 165);
        string ageRating = f.PickRandom("G", "PG", "PG-13", "13+", "16+", "18+");
        string description = f.Lorem.Sentences(2);
        string posterHex = f.Internet.Color();

        var likes = GenerateLikes(userSeed, index, likesAvg);
        var reviews = GenerateReviews(config, userSeed, index, reviewsAvg);
        bool isSeries = DeterministicHash.NextUniform(userSeed, index, "isseries") < 0.15;
        var seasons = isSeries ? GenerateSeasons(userSeed, index, f) : new List<SeasonDto>();

        return new MovieDto
        {
            Index = index,
            Title = title,
            Cast = cast,
            Director = director,
            Year = year,
            Genre = genre,
            DurationMinutes = duration,
            AgeRating = ageRating,
            Description = description,
            PosterHex = posterHex,
            Likes = likes,
            Reviews = reviews,
            TrailerUrl = $"/api/movies/{index}/trailer?region={config.Code}&seed={userSeed}",
            TrailerPosterUrl = $"/api/movies/{index}/poster?region={config.Code}&seed={userSeed}",
            IsSeries = isSeries,
            Seasons = seasons,
        };
    }

    private static List<SeasonDto> GenerateSeasons(long userSeed, long index, Faker f)
    {
        int seasonCount = f.Random.Int(1, 6);
        var seasons = new List<SeasonDto>(seasonCount);
        for (int s = 1; s <= seasonCount; s++)
        {
            var seed = DeterministicHash.Combine(userSeed, index, $"season{s}");
            var sf = new Faker { Random = new Randomizer(seed) };
            seasons.Add(new SeasonDto { SeasonNumber = s, EpisodeCount = sf.Random.Int(4, 24) });
        }
        return seasons;
    }

    /// <summary>
    /// avg=0 -> always 0. avg=10 -> always 10. avg=0.5 -> 0 or 1 with 1:1 odds.
    /// Built on the shared FractionalTimes combinator (see DeterministicHash) -
    /// "add 1 like, n=avg times" - so this is just counting with fn=x=>x+1.
    /// </summary>
    private static int GenerateLikes(long userSeed, long index, double avg)
    {
        avg = Math.Clamp(avg, 0, 10);
        return DeterministicHash.FractionalTimes(userSeed, index, "likes", avg, 0, x => x + 1);
    }

    private static List<ReviewDto> GenerateReviews(LocaleConfig config, long userSeed, long index, double avg)
    {
        avg = Math.Clamp(avg, 0, 10);
        int count = DeterministicHash.FractionalTimes(userSeed, index, "reviewcount", avg, 0, x => x + 1);

        var reviews = new List<ReviewDto>(count);
        for (int j = 0; j < count; j++)
        {
            var seed = DeterministicHash.Combine(userSeed, index, $"review{j}");
            var rf = new Faker { Random = new Randomizer(seed) };
            reviews.Add(new ReviewDto
            {
                Author = $"{rf.PickRandom(config.FirstNames)} {rf.PickRandom(config.LastNames)}",
                Company = rf.PickRandom(config.CompanyNames),
                Text = rf.PickRandom(config.ReviewPhrases)
            });
        }
        return reviews;
    }
}
