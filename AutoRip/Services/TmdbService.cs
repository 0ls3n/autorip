using System.Text.Json;
using AutoRip.Models;

namespace AutoRip.Services;

public class TmdbService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<TmdbService> _logger;

    private string? _imageBaseUrl;
    private const string TmdbBaseUrl = "https://api.themoviedb.org/3";

    public TmdbService(HttpClient http, SettingsService settings, ILogger<TmdbService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured =>
        _settings.Current?.UseTmdbAutoDetect == true &&
        !string.IsNullOrWhiteSpace(_settings.Current?.TmdbApiKey);

    private string? ApiKey => _settings.Current?.TmdbApiKey;

    public async Task<string> GetImageBaseUrlAsync()
    {
        if (_imageBaseUrl != null) return _imageBaseUrl;

        var key = ApiKey;
        if (string.IsNullOrEmpty(key)) return string.Empty;

        try
        {
            var response = await _http.GetAsync($"{TmdbBaseUrl}/configuration?api_key={key}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var images = json.GetProperty("images");
            _imageBaseUrl = images.GetProperty("secure_base_url").GetString() ?? string.Empty;
            return _imageBaseUrl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch TMDB configuration");
            return string.Empty;
        }
    }

    public async Task<MovieInfo?> SearchMovieAsync(string query)
    {
        var key = ApiKey;
        if (string.IsNullOrEmpty(key)) return null;

        try
        {
            var encoded = Uri.EscapeDataString(query);
            var response = await _http.GetAsync($"{TmdbBaseUrl}/search/movie?api_key={key}&query={encoded}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = json.GetProperty("results");

            if (results.GetArrayLength() == 0)
                return null;

            var first = results[0];
            var year = first.TryGetProperty("release_date", out var date) && date.ValueKind != JsonValueKind.Null
                ? (DateTime.TryParse(date.GetString(), out var d) ? d.Year : 0)
                : 0;

            return new MovieInfo
            {
                TmdbId = first.GetProperty("id").GetInt32(),
                Title = first.GetProperty("title").GetString() ?? string.Empty,
                Year = year,
                PosterPath = first.TryGetProperty("poster_path", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null,
                Overview = first.TryGetProperty("overview", out var o) && o.ValueKind != JsonValueKind.Null ? o.GetString() : null,
                BackdropPath = first.TryGetProperty("backdrop_path", out var b) && b.ValueKind != JsonValueKind.Null ? b.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB search failed for query: {Query}", query);
            return null;
        }
    }

    public async Task<List<MovieInfo>> SearchMoviesAsync(string query)
    {
        var key = ApiKey;
        if (string.IsNullOrEmpty(key)) return new List<MovieInfo>();

        try
        {
            var encoded = Uri.EscapeDataString(query);
            var response = await _http.GetAsync($"{TmdbBaseUrl}/search/movie?api_key={key}&query={encoded}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = json.GetProperty("results");

            var movies = new List<MovieInfo>();
            foreach (var item in results.EnumerateArray())
            {
                var year = item.TryGetProperty("release_date", out var date) && date.ValueKind != JsonValueKind.Null
                    ? (DateTime.TryParse(date.GetString(), out var d) ? d.Year : 0)
                    : 0;

                movies.Add(new MovieInfo
                {
                    TmdbId = item.GetProperty("id").GetInt32(),
                    Title = item.GetProperty("title").GetString() ?? string.Empty,
                    Year = year,
                    PosterPath = item.TryGetProperty("poster_path", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null,
                    Overview = item.TryGetProperty("overview", out var o) && o.ValueKind != JsonValueKind.Null ? o.GetString() : null,
                    BackdropPath = item.TryGetProperty("backdrop_path", out var b) && b.ValueKind != JsonValueKind.Null ? b.GetString() : null
                });
            }
            return movies;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB search failed for query: {Query}", query);
            return new List<MovieInfo>();
        }
    }
}
