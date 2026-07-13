namespace AutoRip.Models;

public class MovieInfo
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    public string? BackdropPath { get; set; }

    public string? PosterUrl(string baseUrl, string size = "w342")
    {
        if (string.IsNullOrEmpty(PosterPath))
            return null;
        return $"{baseUrl}{size}{PosterPath}";
    }

    public string? BackdropUrl(string baseUrl, string size = "w780")
    {
        if (string.IsNullOrEmpty(BackdropPath))
            return null;
        return $"{baseUrl}{size}{BackdropPath}";
    }
}
