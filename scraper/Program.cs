using HtmlAgilityPack;
using Azure.Storage.Blobs;
using Azure.Identity;

const string BASE = "https://richtlijnendatabase.nl";
const int MAX_PARALLEL = 10;
const int MAX_RETRIES = 3;

var storageUrl = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_URL")
    ?? throw new Exception("STORAGE_ACCOUNT_URL is required");
var containerName = Environment.GetEnvironmentVariable("AZURE_CONTAINER_NAME") ?? "documents";

var container = new BlobServiceClient(new Uri(storageUrl), new DefaultAzureCredential())
    .GetBlobContainerClient(containerName);

var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 20 };
var http = new HttpClient(handler);
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

// ── Step 1: Load richtlijn paths from committed file ─────────────────────────
Console.WriteLine("Loading richtlijn links from file...");
var allPaths = File.ReadAllLines("richtlijn-links.txt")
    .Select(l => l.Trim())
    .Where(l => l.StartsWith("/richtlijn/"))
    .Distinct()
    .ToList();

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
    pdfHref = Uri.UnescapeDataString(pdfHref);

    if (pdfHref.StartsWith("richtlijnendatabase.nl"))
        pdfHref = "/" + pdfHref.Substring("richtlijnendatabase.nl".Length).TrimStart('/');

    var pdfUrl = pdfHref.StartsWith("http") ? pdfHref : BASE + pdfHref;
    var fileName = $"{richtlijnName}/{pdfUrl.Split('/').Last().Split('?').First()}";

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
            Console.WriteLine($"⏭️  Skipped [{response.StatusCode}]: {pdfUrl}");
            Interlocked.Increment(ref skipped);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ PDF failed [{richtlijnName}/{pdfHref}]: {ex.Message}");
        Interlocked.Increment(ref failed);
    }
}

// ── Fetch one module page and return its PDF links ───────────────────────────
async Task<List<string>> FetchModulePdfLinks(string modulePath)
{
    try
    {
        var moduleHtml = await WithRetry(() => http.GetStringAsync(BASE + modulePath), modulePath);
        var moduleDoc = new HtmlDocument();
        moduleDoc.LoadHtml(moduleHtml);
        return GetPdfLinks(moduleDoc);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️  Module failed [{modulePath}]: {ex.Message}");
        return [];
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

        // Fetch all module pages in parallel
        var moduleResults = await Task.WhenAll(modulePaths.Select(FetchModulePdfLinks));
        pdfLinks.AddRange(moduleResults.SelectMany(l => l));

        // Upload all PDFs in parallel
        await Task.WhenAll(pdfLinks.Distinct().Select(href => UploadPdf(href, name)));
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

Console.WriteLine($"\nDone — uploaded: {uploaded}, skipped: {skipped}, failed: {failed}");
Environment.Exit(0);
