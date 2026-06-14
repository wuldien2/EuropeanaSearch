using DotNetEnv;
using EuropeanSearch.Services;

Env.Load();

string? europeanaKey = Environment.GetEnvironmentVariable("EUROPEANA_API_KEY");
if (string.IsNullOrEmpty(europeanaKey))
{
    Console.WriteLine("GRESKA: EUROPEANA_API_KEY nije pronadjen u .env fajlu.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration["EUROPEANA_API_KEY"] = europeanaKey;

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Europeana Art Search API",
        Version = "v1",
        Description = "Web server za pretragu umetnickih dela koriscenjem Europeana API-a "
    });
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<EuropeanaService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Europeana Art Search API v1");
    c.RoutePrefix = "swagger";
});

app.MapGet("/search", async (
    EuropeanaService service,
    string? query,
    string? recsource,
    string? language,
    string? year,
    int rows = 10) =>
{
    // Validacija - query je obavezan parametar
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest(new
        {
            greska = "Parametar 'query' je obavezan.",
            primer = "/search?query=van+gogh"
        });
    }

    if (rows <= 0 || rows > 100)
        rows = 10;

    try
    {
        var results = await service.SearchAsync(query, recsource, language, year, rows);

        if (results.Count == 0)
        {
            return Results.NotFound(new
            {
                poruka = $"Nisu pronadjena umetnička dela za upit: '{query}'",
                filteri = new { recsource, language, year }
            });
        }

        return Results.Ok(new
        {
            upit = query,
            filteri = new { recsource, language, year },
            brojRezultata = results.Count,
            dela = results
        });
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            detail: $"Greska pri komunikaciji sa Europeana API-om: {ex.Message}",
            statusCode: 502,
            title: "Bad Gateway"
        );
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 500,
            title: "Greska servera"
        );
    }
})
.WithName("SearchArtworks")
.WithSummary("Pretrazuje umetnička dela putem Europeana API-a")
.WithDescription(
    "Vraca listu umetnickih dela koja odgovaraju zadatim filterima. " +
    "Rezultati se keširaju na serveru po strategiji vremena isticanja (TTL). " +
    "Drugi poziv sa istim parametrima vraca odgovor iz kesa bez poziva ka Europeana API-u.")
.WithOpenApi();

//  GET /cache/stats  - prikaz trenutnog stanja kesa
app.MapGet("/cache/stats", (EuropeanaService service) =>
{
    var stats = service.GetCacheStats();
    return Results.Ok(stats);
})
.WithName("GetCacheStats")
.WithSummary("Prikazuje trenutno stanje kes memorije")
.WithDescription("Vraca informacije o svim elementima u kesu: kljuc, vreme kreiranja, vreme isticanja, broj rezultata.")
.WithOpenApi();

//  DELETE /cache/clean  - rucno brisanje isteklih elemenata
app.MapDelete("/cache/clean", (EuropeanaService service) =>
{
    int removed = service.CleanExpiredEntries();
    return Results.Ok(new
    {
        poruka = $"Uklonjeno {removed} isteklih elemenata iz kesa."
    });
})
.WithName("CleanExpiredCache")
.WithSummary("Rucno uklanja istekle elemente iz kesa")
.WithOpenApi();

Console.WriteLine("  Europeana Art Search Server");
Console.WriteLine("  Swagger UI: http://localhost:5213/swagger");
Console.WriteLine("  Primer poziva: GET /search?query=van+gogh");

Console.WriteLine("  Pritisnite ENTER za izlaz...");

await app.StartAsync();

Console.ReadLine();

await app.StopAsync();
