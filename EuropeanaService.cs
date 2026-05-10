using System.Collections.Concurrent;
using EuropeanSearch.Models;
using Newtonsoft.Json.Linq;

namespace EuropeanSearch.Services
{
    public class EuropeanaService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _baseUrl = "https://api.europeana.eu/record/v2/search.json";
        private readonly ConcurrentDictionary<string, CacheEntry<List<ArtworkItem>>> _cache
            = new ConcurrentDictionary<string, CacheEntry<List<ArtworkItem>>>();
        private readonly TimeSpan _cacheTtl;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly int _maxParallelRequests;

        private readonly ILogger<EuropeanaService> _logger;

        public EuropeanaService(HttpClient httpClient, IConfiguration config, ILogger<EuropeanaService> logger)
        {
            _httpClient = httpClient;
            _apiKey = config["EUROPEANA_API_KEY"] ?? throw new InvalidOperationException("EUROPEANA_API_KEY nije pronadjen u konfiguraciji.");
            int ttlMinutes = config.GetValue<int>("CacheTtlMinutes", 60);
            _cacheTtl = TimeSpan.FromMinutes(ttlMinutes);

            _maxParallelRequests = config.GetValue<int>("MaxParallelRequests", 4);

            ThreadPool.SetMinThreads(_maxParallelRequests, _maxParallelRequests);
            ThreadPool.SetMaxThreads(_maxParallelRequests * 4, _maxParallelRequests * 4);

            _logger = logger;
        }
        public async Task<List<ArtworkItem>> SearchAsync(string query, string? recsource = null,
            string? language = null, string? year = null, int rows = 10)
        {
            string cacheKey = BuildCacheKey(query, recsource, language, year, rows);

            if (_cache.TryGetValue(cacheKey, out var existingEntry) && !existingEntry.IsExpired(_cacheTtl))
            {
                _logger.LogInformation("[KES] CACHE HIT  -> kljuc: '{Key}' (kreiran: {Created}, istica za: {Remaining:F1} min)",
                    cacheKey,
                    existingEntry.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
                    (_cacheTtl - (DateTime.UtcNow - existingEntry.CreatedAt)).TotalMinutes);
                return existingEntry.Value;
            }

            var sem = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

            await sem.WaitAsync();
            try
            {
                if (_cache.TryGetValue(cacheKey, out var doubleCheckEntry) && !doubleCheckEntry.IsExpired(_cacheTtl))
                {
                    _logger.LogInformation("[KES] CACHE HIT (posle double-check) -> kljuc: '{Key}'", cacheKey);
                    return doubleCheckEntry.Value;
                }

                _logger.LogInformation("[KES] CACHE MISS -> kljuc: '{Key}', saljem zahtev ka Europeana API-u...", cacheKey);

                var results = await FetchFromApiAsync(query, recsource, language, year, rows);

                var newEntry = new CacheEntry<List<ArtworkItem>>(results);
                _cache[cacheKey] = newEntry;

                _logger.LogInformation("[KES] Rezultati smesteni u kes -> kljuc: '{Key}', istica: {Expiry}",
                    cacheKey,
                    (newEntry.CreatedAt + _cacheTtl).ToLocalTime().ToString("HH:mm:ss"));

                return results;
            }
            finally
            {
                sem.Release();
            }
        }

        private async Task<List<ArtworkItem>> FetchFromApiAsync(string query, string? recsource,
            string? language, string? year, int rows)
        {
            var queryParams = new List<string>
            {
                $"wskey={_apiKey}",
                $"query={Uri.EscapeDataString(query)}",
                $"rows={rows}",
                "profile=rich"
            };

            if (!string.IsNullOrWhiteSpace(recsource))
                queryParams.Add($"qf=DATA_PROVIDER:{Uri.EscapeDataString(recsource)}");

            if (!string.IsNullOrWhiteSpace(language))
                queryParams.Add($"qf=LANGUAGE:{Uri.EscapeDataString(language)}");

            if (!string.IsNullOrWhiteSpace(year))
                queryParams.Add($"qf=YEAR:{Uri.EscapeDataString(year)}");

            string url = $"{_baseUrl}?{string.Join("&", queryParams)}";

            _logger.LogInformation("[API] GET {Url}", url.Replace(_apiKey, "***"));

            HttpResponseMessage response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync();
            JObject json = JObject.Parse(body);

            string? apiStatus = json["success"]?.ToString();
            if (apiStatus == "False" || apiStatus == "false")
            {
                string errorMsg = json["error"]?.ToString() ?? "Nepoznata greska sa Europeana API-a";
                throw new Exception($"Europeana API greska: {errorMsg}");
            }

            var items = new List<ArtworkItem>();
            var itemsArray = json["items"] as JArray;

            if (itemsArray == null)
                return items;

            foreach (var item in itemsArray)
            {
                items.Add(new ArtworkItem
                {
                    Id = item["id"]?.ToString(),
                    Title = GetFirstValue(item["title"]),
                    Creator = GetFirstValue(item["dcCreator"]),
                    Description = GetFirstValue(item["dcDescription"]),
                    Thumbnail = item["edmPreview"]?.FirstOrDefault()?.ToString(),
                    Country = GetFirstValue(item["country"]),
                    Year = GetFirstValue(item["year"]),
                    DataProvider = GetFirstValue(item["dataProvider"]),
                    Url = item["guid"]?.ToString()
                });
            }

            _logger.LogInformation("[API] Primljeno {Count} rezultata za upit: '{Query}'", items.Count, query);
            return items;
        }

        private static string? GetFirstValue(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Array)
                return token.FirstOrDefault()?.ToString();
            return token.ToString();
        }

        private static string BuildCacheKey(string query, string? recsource, string? language, string? year, int rows)
            => $"{query.ToLower()}|{recsource?.ToLower()}|{language?.ToLower()}|{year}|{rows}";

        public CacheStatsDto GetCacheStats()
        {
            int total = _cache.Count;
            int valid = _cache.Values.Count(e => !e.IsExpired(_cacheTtl));
            int expired = total - valid;

            return new CacheStatsDto
            {
                TotalEntries = total,
                ValidEntries = valid,
                ExpiredEntries = expired,
                TtlMinutes = (int)_cacheTtl.TotalMinutes,
                Entries = _cache.Select(kvp => new CacheEntryDto
                {
                    Key = kvp.Key,
                    CreatedAt = kvp.Value.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
                    ExpiresAt = (kvp.Value.CreatedAt + _cacheTtl).ToLocalTime().ToString("HH:mm:ss"),
                    IsExpired = kvp.Value.IsExpired(_cacheTtl),
                    ResultCount = kvp.Value.Value.Count
                }).ToList()
            };
        }

        public int CleanExpiredEntries()
        {
            int removed = 0;
            foreach (var key in _cache.Keys)
            {
                if (_cache.TryGetValue(key, out var entry) && entry.IsExpired(_cacheTtl))
                {
                    if (_cache.TryRemove(key, out _))
                        removed++;
                }
            }
            _logger.LogInformation("[KES] Uklonjeno {Count} isteklih elemenata.", removed);
            return removed;
        }
    }

    public class CacheStatsDto
    {
        public int TotalEntries { get; set; }
        public int ValidEntries { get; set; }
        public int ExpiredEntries { get; set; }
        public int TtlMinutes { get; set; }
        public List<CacheEntryDto> Entries { get; set; } = new();
    }

    public class CacheEntryDto
    {
        public string Key { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
        public bool IsExpired { get; set; }
        public int ResultCount { get; set; }
    }
}
