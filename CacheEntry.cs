namespace EuropeanSearch.Models
{
    public class CacheEntry<T>
    {
        public T Value { get; set; }
        public DateTime CreatedAt { get; set; }

        public CacheEntry(T value)
        {
            Value = value;
            CreatedAt = DateTime.UtcNow;
        }

        public bool IsExpired(TimeSpan ttl)
        {
            return (DateTime.UtcNow - CreatedAt) > ttl;
        }
    }

    public class ArtworkItem
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Creator { get; set; }
        public string? Description { get; set; }
        public string? Thumbnail { get; set; }
        public string? Country { get; set; }
        public string? Year { get; set; }
        public string? DataProvider { get; set; }
        public string? Url { get; set; }
    }
}
