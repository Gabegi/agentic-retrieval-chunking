using HtmlAgilityPack;
using Azure.Storage.Blobs;
using Azure.Identity;

const string BASE = "https://richtlijnendatabase.nl";
const int MAX_PARALLEL = 5;
const int MAX_RETRIES = 3;

var storageUrl = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_URL")
    ?? throw new Exception("STORAGE_ACCOUNT_URL is required");
var containerName = Environment.GetEnvironmentVariable("AZURE_CONTAINER_NAME") ?? "documents";

var container = new BlobServiceClient(new Uri(storageUrl), new DefaultAzureCredential())
    .GetBlobContainerClient(containerName);

var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
http.Timeout = TimeSpan.FromSeconds(60);

int uploaded = 0, skipped = 0, failed = 0;

// ── Retry helper ──────────────────────────────────────────────────────────────
async Task<T> WithRetry<T>(Func<Task<T>> action, string label)
{
    for (int i = 1; i <= MAX_RETRIES; i++)
    {
        try { return await action(); }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Retry {i}/{MAX_RETRIES} [{label}]: {ex.Message}");
            if (i == MAX_RETRIES) throw;
            await Task.Delay(i * 2000);
        }
    }
    throw new Exception("Unreachable");
}

// ── Step 1: Paginate /zoek?page=N to collect all richtlijn paths ─────────────
Console.WriteLine("Collecting richtlijn links via /zoek...");
var allPaths = new HashSet<string>();
int page = 1;

while (true)
{
    var html = await WithRetry(() => http.GetStringAsync($"{BASE}/zoek?page={page}"), $"zoek page {page}");
    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    var pageLinks = doc.DocumentNode
        .SelectNodes("//a[@href]")
        ?.Select(a => a.GetAttributeValue("href", ""))
        .Where(h => h.Contains("/richtlijn/") && !h.EndsWith(".html"))
        .Select(h => h.StartsWith("http") ? new Uri(h).AbsolutePath : h)
        .Select(h => h.TrimEnd('/'))
        .ToList() ?? [];

    if (pageLinks.Count == 0) break;

    foreach (var l in pageLinks) allPaths.Add(l);
    Console.WriteLine($"  Page {page}: +{pageLinks.Count} (total: {allPaths.Count})");

    var hasNext = doc.DocumentNode
        .SelectNodes("//a[@href]")
        ?.Any(a => a.GetAttributeValue("href", "").Contains($"zoek?page={page + 1}")) ?? false;

    if (!hasNext) break;
    page++;
    await Task.Delay(500);
}

Console.WriteLine($"Found {allPaths.Count} richtlijnen — scraping (max {MAX_PARALLEL} parallel)");

// ── Extract PDF hrefs from a parsed page ─────────────────────────────────────
List<string> GetPdfLinks(HtmlDocument d) =>
    d.DocumentNode
        .SelectNodes("//a[contains(@href,'.pdf')]")
        ?.Select(a => a.GetAttributeValue("href", ""))
        .Where(h => !string.IsNullOrEmpty(h))
        .ToList() ?? [];

// ── Upload one PDF ────────────────────────────────────────────────────────────
async Task UploadPdf(string pdfHref, string richtlijnName)
{
    var pdfUrl = pdfHref.StartsWith("http") ? pdfHref : BASE + pdfHref;
    var fileName = $"{richtlijnName}/{Uri.UnescapeDataString(pdfUrl.Split('/').Last())}";

    try
    {
        var response = await WithRetry(() => http.GetAsync(pdfUrl), pdfUrl);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        if (response.IsSuccessStatusCode && contentType.Contains("pdf"))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await container.GetBlobClient(fileName).UploadAsync(stream, overwrite: true);
            Console.WriteLine($"✅ {fileName}");
            Interlocked.Increment(ref uploaded);
        }
        else
        {
            Console.WriteLine($"⏭️  Skipped ({(int)response.StatusCode} / {contentType}): {pdfUrl}");
            Interlocked.Increment(ref skipped);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ PDF failed [{fileName}]: {ex.Message}");
        Interlocked.Increment(ref failed);
    }
}

// ── Scrape one richtlijn: index page + all module sub-pages ──────────────────
async Task ScrapeRichtlijn(string path)
{
    var name = path.Trim('/').Replace("/", "_");
    try
    {
        var pageHtml = await WithRetry(() => http.GetStringAsync(BASE + path), path);
        var pageDoc = new HtmlDocument();
        pageDoc.LoadHtml(pageHtml);

        var pdfLinks = GetPdfLinks(pageDoc);

        var modulePaths = pageDoc.DocumentNode
            .SelectNodes("//a[@href]")
            ?.Select(a => a.GetAttributeValue("href", ""))
            .Where(h => h.Contains(path + "/") && h.EndsWith(".html"))
            .Select(h => h.StartsWith("http") ? new Uri(h).AbsolutePath : h)
            .Distinct()
            .ToList() ?? [];

        foreach (var modulePath in modulePaths)
        {
            try
            {
                var moduleHtml = await WithRetry(() => http.GetStringAsync(BASE + modulePath), modulePath);
                var moduleDoc = new HtmlDocument();
                moduleDoc.LoadHtml(moduleHtml);
                pdfLinks.AddRange(GetPdfLinks(moduleDoc));
                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Module failed [{modulePath}]: {ex.Message}");
            }
        }

        foreach (var pdfHref in pdfLinks.Distinct())
            await UploadPdf(pdfHref, name);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ {path}: {ex.Message}");
        Interlocked.Increment(ref failed);
    }
}

// ── Parallel execution with throttle ─────────────────────────────────────────
var semaphore = new SemaphoreSlim(MAX_PARALLEL);
var tasks = allPaths.Select(async path =>
{
    await semaphore.WaitAsync();
    try { await ScrapeRichtlijn(path); }
    finally { semaphore.Release(); }
});

await Task.WhenAll(tasks);

Console.WriteLine();
Console.WriteLine($"Done — uploaded: {uploaded}, skipped: {skipped}, failed: {failed}");

if (failed > 0)
    Environment.Exit(1);
