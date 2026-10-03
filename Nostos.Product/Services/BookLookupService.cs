using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services;

public partial class BookLookupService(IHttpClientFactory httpClientFactory, ILogger<BookLookupService> logger)
{
    public const string HttpClientName = "book-lookup";

    [GeneratedRegex("[^0-9X]", RegexOptions.IgnoreCase)]
    private static partial Regex IsbnCleanupRegex();

    public async Task<CreateBookDto?> LookupCombinedAsync(string isbn, CancellationToken ct = default) =>
        (await LookupCombinedDetailedAsync(isbn, ct)).Metadata;

    /// <summary>
    /// Combined lookup that distinguishes "no metadata found" from "lookup
    /// failed" (network error, timeout, provider 5xx). The caller decides
    /// whether a failure is worth surfacing (e.g. the library resolver maps
    /// it to lookup_timeout). Cancellation of the CALLER's token always
    /// propagates; a provider timeout on a live caller token is treated as a
    /// failure, not as caller cancellation.
    /// </summary>
    public async Task<BookLookupOutcome> LookupCombinedDetailedAsync(string isbn, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        isbn = CleanIsbn(isbn);

        // 1. Fire both requests in parallel; provider failures are collected
        //    into Failed without breaking the other provider.
        var failed = false;
        CreateBookDto? olData = null;
        CreateBookDto? gbData = null;

        var olTask = Task.Run(async () =>
        {
            try
            {
                return await FetchOpenLibrary(client, isbn, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OpenLibrary API lookup failed for ISBN {Isbn}", isbn);
                failed = true;
                return null;
            }
        }, ct);

        var gbTask = Task.Run(async () =>
        {
            try
            {
                return await FetchGoogleBooks(client, isbn, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Google Books API lookup failed for ISBN {Isbn}", isbn);
                failed = true;
                return null;
            }
        }, ct);

        await Task.WhenAll(olTask, gbTask);

        olData = await olTask;
        gbData = await gbTask;

        // 2. Fail fast
        if (olData is null && gbData is null)
            return new BookLookupOutcome(null, failed);

        // 3. Base Data (Priority: OpenLibrary)
        var baseData =
            olData
            ?? new CreateBookDto(
                Type: "physical",
                Title: "",
                Subtitle: null,
                Author: null,
                Editor: null,
                Translator: null,
                Narrator: null,
                Description: null,
                Isbn: isbn,
                Asin: null,
                Duration: null,
                Publisher: null,
                PlaceOfPublication: null,
                PublishedDate: null,
                Edition: null,
                PageCount: null,
                Language: null,
                Categories: null,
                Series: null,
                VolumeNumber: null,
                Rating: 0,
                IsFavorite: false,
                PersonalReview: null,
                FinishedAt: null
            );

        if (gbData is null)
            return new BookLookupOutcome(baseData, failed);

        // 4. Merge (Priority: Keep existing OL data, fill gaps with GB)
        return new BookLookupOutcome(baseData with
        {
            Title = !string.IsNullOrWhiteSpace(baseData.Title) ? baseData.Title : gbData.Title,
            Subtitle = !string.IsNullOrWhiteSpace(baseData.Subtitle)
                ? baseData.Subtitle
                : gbData.Subtitle,
            Author = !string.IsNullOrWhiteSpace(baseData.Author) ? baseData.Author : gbData.Author,
            Description = !string.IsNullOrWhiteSpace(baseData.Description)
                ? baseData.Description
                : gbData.Description,
            Publisher = !string.IsNullOrWhiteSpace(baseData.Publisher)
                ? baseData.Publisher
                : gbData.Publisher,
            PlaceOfPublication = !string.IsNullOrWhiteSpace(baseData.PlaceOfPublication)
                ? baseData.PlaceOfPublication
                : gbData.PlaceOfPublication,
            PublishedDate = !string.IsNullOrWhiteSpace(baseData.PublishedDate)
                ? baseData.PublishedDate
                : gbData.PublishedDate,
            PageCount = baseData.PageCount ?? gbData.PageCount,
            Language = !string.IsNullOrWhiteSpace(baseData.Language)
                ? baseData.Language
                : gbData.Language,
            Categories = !string.IsNullOrWhiteSpace(baseData.Categories)
                ? baseData.Categories
                : gbData.Categories,
        }, failed);
    }

    private string CleanIsbn(string isbn)
    {
        if (string.IsNullOrWhiteSpace(isbn))
            return "";
        return IsbnCleanupRegex().Replace(isbn, "").ToUpper();
    }

    // --- GOOGLE BOOKS FETCH (Strictly Typed) ---
    private async Task<CreateBookDto?> FetchGoogleBooks(HttpClient client, string isbn, CancellationToken ct)
    {
        // Note: Use 'volumeInfo' in the response record directly
        var response = await client.GetFromJsonAsync<GoogleBooksResponse>(
            $"https://www.googleapis.com/books/v1/volumes?q=isbn:{isbn}",
            ct
        );

        var item = response?.Items?.FirstOrDefault()?.VolumeInfo;
        if (item is null)
            return null;

        return new CreateBookDto(
            Type: "physical",
            Title: item.Title ?? "",
            Subtitle: item.Subtitle,
            Author: item.Authors is not null ? string.Join(", ", item.Authors) : null,
            Editor: null,
            Translator: null,
            Narrator: null,
            Description: item.Description,
            Isbn: isbn,
            Asin: null,
            Duration: null,
            Publisher: item.Publisher,
            PlaceOfPublication: null,
            PublishedDate: item.PublishedDate,
            Edition: null,
            PageCount: item.PageCount,
            Language: item.Language,
            Categories: item.Categories is not null ? string.Join(", ", item.Categories) : null,
            Series: null,
            VolumeNumber: null,
            Rating: 0,
            IsFavorite: false,
            PersonalReview: null,
            FinishedAt: null
        );
    }

    // --- OPEN LIBRARY FETCH ---
    private async Task<CreateBookDto?> FetchOpenLibrary(HttpClient client, string isbn, CancellationToken ct)
    {
        var key = $"ISBN:{isbn}";
        Exception? legacyFailure = null;

        try
        {
            using var legacyResponse = await client.GetAsync(
                $"https://openlibrary.org/api/books?bibkeys={key}&jscmd=data&format=json",
                ct
            );

            if (legacyResponse.IsSuccessStatusCode)
            {
                var response =
                    await legacyResponse.Content.ReadFromJsonAsync<Dictionary<string, OpenLibraryBook>>(
                        cancellationToken: ct
                    );

                if (response is not null && response.TryGetValue(key, out var item))
                    return MapOpenLibraryBook(item, isbn);
            }
            else if (legacyResponse.StatusCode != HttpStatusCode.NotFound)
            {
                legacyFailure = new HttpRequestException(
                    $"Open Library legacy lookup returned {(int)legacyResponse.StatusCode} ({legacyResponse.StatusCode}).",
                    null,
                    legacyResponse.StatusCode
                );
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A legacy endpoint failure should not prevent the canonical ISBN
            // endpoint from recovering the lookup. Keep the failure around so
            // a final 404/empty canonical response is still reported as a
            // provider failure rather than a genuine "not found".
            legacyFailure = ex;
        }

        using var canonicalResponse = await client.GetAsync(
            $"https://openlibrary.org/isbn/{isbn}.json",
            ct
        );

        if (canonicalResponse.StatusCode == HttpStatusCode.NotFound)
        {
            if (legacyFailure is not null)
            {
                throw CreateOpenLibraryFallbackFailure(
                    legacyFailure,
                    "Open Library canonical ISBN fallback returned 404."
                );
            }

            return null;
        }

        canonicalResponse.EnsureSuccessStatusCode();

        var canonical =
            await canonicalResponse.Content.ReadFromJsonAsync<OpenLibraryCanonicalBook>(
                cancellationToken: ct
            );

        if (canonical is null || string.IsNullOrWhiteSpace(canonical.Title))
        {
            if (legacyFailure is not null)
            {
                throw CreateOpenLibraryFallbackFailure(
                    legacyFailure,
                    "Open Library canonical ISBN fallback returned no usable metadata."
                );
            }

            return null;
        }

        return MapCanonicalOpenLibraryBook(canonical, isbn);
    }

    private static HttpRequestException CreateOpenLibraryFallbackFailure(
        Exception legacyFailure,
        string message
    ) =>
        new(
            message,
            legacyFailure,
            (legacyFailure as HttpRequestException)?.StatusCode
        );

    private static CreateBookDto MapOpenLibraryBook(OpenLibraryBook item, string isbn)
    {
        var authors = item.Authors?.Select(a => a.Name).Where(x => !string.IsNullOrWhiteSpace(x));
        var place = item.PublishPlaces?.FirstOrDefault()?.Name;

        return new CreateBookDto(
            Type: "physical",
            Title: item.Title ?? "",
            Subtitle: item.Subtitle,
            Author: authors is not null ? string.Join(", ", authors) : null,
            Editor: null,
            Translator: null,
            Narrator: null,
            Description: null,
            Isbn: isbn,
            Asin: null,
            Duration: null,
            Publisher: item.Publishers?.FirstOrDefault()?.Name,
            PlaceOfPublication: place,
            PublishedDate: item.PublishDate,
            Edition: null,
            PageCount: item.NumberOfPages,
            Language: null,
            Categories: null,
            Series: null,
            VolumeNumber: null,
            Rating: 0,
            IsFavorite: false,
            PersonalReview: null,
            FinishedAt: null
        );
    }

    private static CreateBookDto MapCanonicalOpenLibraryBook(
        OpenLibraryCanonicalBook item,
        string isbn
    )
    {
        var authors = item.Authors?
            .Select(CanonicalAuthorDisplayName)
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        var categories = item.Subjects?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        return new CreateBookDto(
            Type: "physical",
            Title: item.Title ?? "",
            Subtitle: item.Subtitle,
            Author: authors is { Length: > 0 } ? string.Join(", ", authors) : null,
            Editor: null,
            Translator: null,
            Narrator: null,
            Description: null,
            Isbn: isbn,
            Asin: null,
            Duration: null,
            Publisher: item.Publishers?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            PlaceOfPublication: item.PublishPlaces?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            PublishedDate: item.PublishDate,
            Edition: item.EditionName,
            PageCount: item.NumberOfPages,
            Language: NormalizeOpenLibraryKey(item.Languages?.FirstOrDefault()?.Key),
            Categories: categories is { Length: > 0 } ? string.Join(", ", categories) : null,
            Series: null,
            VolumeNumber: null,
            Rating: 0,
            IsFavorite: false,
            PersonalReview: null,
            FinishedAt: null
        );
    }

    private static string? CanonicalAuthorDisplayName(OpenLibraryCanonicalAuthor author)
    {
        if (!string.IsNullOrWhiteSpace(author.Name))
            return author.Name;

        if (!string.IsNullOrWhiteSpace(author.Author?.Name))
            return author.Author.Name;

        if (!string.IsNullOrWhiteSpace(author.Key))
            return author.Key;

        return author.Author?.Key;
    }

    private static string? NormalizeOpenLibraryKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var separator = key.LastIndexOf('/');
        return separator >= 0 && separator < key.Length - 1 ? key[(separator + 1)..] : key;
    }

}

// Distinguishes "no metadata found" from "lookup failed" so callers can
// surface provider failures (e.g. lookup_timeout) without breaking local
// resolution.
public sealed record BookLookupOutcome(CreateBookDto? Metadata, bool Failed);

// --- STRICT TYPES (Internal) ---

// 1. Google Books Records
record GoogleBooksResponse(List<GoogleBookItem>? Items);

record GoogleBookItem(GoogleVolumeInfo VolumeInfo);

record GoogleVolumeInfo(
    string? Title,
    string? Subtitle,
    string[]? Authors,
    string? Description,
    string? Publisher,
    string? PublishedDate,
    int? PageCount,
    string? Language,
    string[]? Categories
);

// 2. Open Library Records
// Note: JSON properties often have snake_case, we use [JsonPropertyName] if we want C# PascalCase,
// or we can just match the API casing strictly for simple internal DTOs.
// To avoid importing System.Text.Json.Serialization everywhere, I'll stick to matching the API naming or using the simple options.
// For simplicity and zero-dependency bloat in this file, let's use the [JsonPropertyName] attribute.
// I will need: using System.Text.Json.Serialization;

record OpenLibraryBook(
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string? Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("subtitle")] string? Subtitle,
    [property: System.Text.Json.Serialization.JsonPropertyName("authors")] List<OlName>? Authors,
    [property: System.Text.Json.Serialization.JsonPropertyName("publishers")]
        List<OlName>? Publishers,
    [property: System.Text.Json.Serialization.JsonPropertyName("publish_places")]
        List<OlName>? PublishPlaces,
    [property: System.Text.Json.Serialization.JsonPropertyName("publish_date")] string? PublishDate,
    [property: System.Text.Json.Serialization.JsonPropertyName("number_of_pages")]
        int? NumberOfPages
);

record OlName([property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name);

record OpenLibraryCanonicalBook(
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string? Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("subtitle")] string? Subtitle,
    [property: System.Text.Json.Serialization.JsonPropertyName("authors")]
        List<OpenLibraryCanonicalAuthor>? Authors,
    [property: System.Text.Json.Serialization.JsonPropertyName("publishers")]
        List<string>? Publishers,
    [property: System.Text.Json.Serialization.JsonPropertyName("publish_places")]
        List<string>? PublishPlaces,
    [property: System.Text.Json.Serialization.JsonPropertyName("publish_date")]
        string? PublishDate,
    [property: System.Text.Json.Serialization.JsonPropertyName("number_of_pages")]
        int? NumberOfPages,
    [property: System.Text.Json.Serialization.JsonPropertyName("edition_name")]
        string? EditionName,
    [property: System.Text.Json.Serialization.JsonPropertyName("languages")]
        List<OlKey>? Languages,
    [property: System.Text.Json.Serialization.JsonPropertyName("subjects")]
        List<string>? Subjects
);

record OpenLibraryCanonicalAuthor(
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] string? Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("author")]
        OpenLibraryAuthorReference? Author
);

record OpenLibraryAuthorReference(
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] string? Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name
);

record OlKey([property: System.Text.Json.Serialization.JsonPropertyName("key")] string? Key);
