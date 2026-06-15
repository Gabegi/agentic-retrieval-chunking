using HtmlAgilityPack;
using Azure.Storage.Blobs;
using Azure.Identity;
using System.Threading.Channels;

const string BASE = "https://richtlijnendatabase.nl";
const int SCRAPE_PARALLEL = 20;   // concurrent richtlijn pages
const int UPLOAD_PARALLEL = 40;   // concurrent blob uploads (separate pool)
const int MAX_RETRIES = 3;

var storageUrl = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_URL")
    ?? throw new Exception("STORAGE_ACCOUNT_URL is required");
var containerName = Environment.GetEnvironmentVariable("AZURE_CONTAINER_NAME") ?? "documents";

var blobService = new BlobServiceClient(new Uri(storageUrl), new DefaultAzureCredential());
var container = blobService.GetBlobContainerClient(containerName);
await container.CreateIfNotExistsAsync();

// ── Single HttpClient with tuned connection pool ──────────────────────────────
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 30,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    EnableMultipleHttp2Connections = true
};
var http = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(30) // fail fast, retry handles the rest
};
http.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

int uploaded = 0, skipped = 0, failed = 0;

// ── Retry with exponential backoff ───────────────────────────────────────────
async Task<T> WithRetry<T>(Func<Task<T>> action, string label)
{
    for (int i = 1; i <= MAX_RETRIES; i++)
    {
        try { return await action(); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw; // 404 will never recover — skip immediately, no retries
        }
        catch (Exception ex) when (i < MAX_RETRIES)
        {
            Console.WriteLine($"⚠️  Retry {i}/{MAX_RETRIES} [{label[..Math.Min(60, label.Length)]}]: {ex.Message}");
            await Task.Delay(500 * (int)Math.Pow(2, i));
        }
    }
    return await action();
}

// ── Load richtlijn paths ──────────────────────────────────────────────────────
Console.WriteLine("Loading richtlijn links from file...");
var skip404 = new HashSet<string>
{
    "/richtlijn/vlekziekte_exantheem_bij_kinderen",
    "/richtlijn/voedingsbeleid_op_de_intensive_care",
    "/richtlijn/voedingsbeleid_op_de_neonatologie",
    "/richtlijn/voettumoren",
    "/richtlijn/volvulus",
    "/richtlijn/vonk_vuurwerk",
    "/richtlijn/vroeggeboorte",
    "/richtlijn/wondinfecties",
    "/richtlijn/zachte_wekedelen_tumoren",
    "/richtlijn/zelfverwonding",
    "/richtlijn/ziekte_van_huntington",
    "/richtlijn/ziekte_van_peyronie",
    "/richtlijn/zorgpad_hartfalen",
};

var allPaths = File.ReadAllLines("richtlijn-links.txt")
    .Select(l => l.Trim())
    .Where(l => l.StartsWith("/richtlijn/") && !skip404.Contains(l))
    .Distinct()
    .ToList();

Console.WriteLine($"Found {allPaths.Count} richtlijnen — scraping");

// ── PDF href extraction ───────────────────────────────────────────────────────
static List<string> GetPdfLinks(HtmlDocument doc) =>
    doc.DocumentNode
        .SelectNodes("//a[contains(@href,'.pdf')]")
        ?.Select(a => a.GetAttributeValue("href", ""))
        .Where(h => !string.IsNullOrEmpty(h))
        .ToList() ?? [];

// ── Normalize href → absolute URL ────────────────────────────────────────────
static string NormalizeUrl(string href)
{
    const string base_ = "https://richtlijnendatabase.nl";
    href = System.Net.WebUtility.HtmlDecode(href);
    href = Uri.UnescapeDataString(href);
    if (href.Contains("richtlijnendatabase.nl/gerelateerde"))
    {
        var idx = href.IndexOf("/gerelateerde");
        href = href.Substring(idx);
    }
    if (href.Contains("&") && !href.StartsWith("http"))
        href = href.Split('&')[0];
    href = href.Replace(" ", "%20");
    return href.StartsWith("http") ? href : base_ + href;
}

// ── Producer: scrape pages and push PDF work to a channel ────────────────────
// Using a channel decouples scraping speed from upload speed
var channel = Channel.CreateBounded<(string url, string blobName)>(
    new BoundedChannelOptions(500)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });

async Task FetchPage(string url, string richtlijnName)
{
    try
    {
        var html = await WithRetry(() => http.GetStringAsync(url), url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var href in GetPdfLinks(doc))
        {
            var pdfUrl  = NormalizeUrl(href);
            var blobName = $"{richtlijnName}/{pdfUrl.Split('/').Last().Split('?').First()}";
            await channel.Writer.WriteAsync((pdfUrl, blobName));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️  Page failed [{url}]: {ex.Message}");
    }
}

async Task ScrapeRichtlijn(string path)
{
    var name = path.Trim('/').Replace("/", "_");
    try
    {
        var pageHtml = await WithRetry(() => http.GetStringAsync(BASE + path), path);
        var pageDoc = new HtmlDocument();
        pageDoc.LoadHtml(pageHtml);

        // Queue PDFs from index page
        foreach (var href in GetPdfLinks(pageDoc))
        {
            var pdfUrl   = NormalizeUrl(href);
            var blobName = $"{name}/{pdfUrl.Split('/').Last().Split('?').First()}";
            await channel.Writer.WriteAsync((pdfUrl, blobName));
        }

        // Collect module sub-pages
        var modulePaths = pageDoc.DocumentNode
            .SelectNodes("//a[@href]")
            ?.Select(a => a.GetAttributeValue("href", ""))
            .Where(h => h.Contains(path + "/") && h.EndsWith(".html"))
            .Select(h => h.StartsWith("http") ? new Uri(h).AbsolutePath : h)
            .Distinct()
            .ToList() ?? [];

        // Fetch all module pages concurrently (no extra semaphore — channel handles backpressure)
        await Task.WhenAll(modulePaths.Select(m =>
            FetchPage(m.StartsWith("http") ? m : BASE + m, name)));
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine($"⏭️  404 (removed): {path}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ {path}: {ex.Message}");
        Interlocked.Increment(ref failed);
    }
}

// ── Consumer: upload PDFs from channel ───────────────────────────────────────
// Deduplicate by blob name to avoid re-uploading the same file from multiple pages
var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

async Task UploadWorker()
{
    await foreach (var (pdfUrl, blobName) in channel.Reader.ReadAllAsync())
    {
        if (!seen.TryAdd(blobName, 0)) continue; // already queued

        try
        {
            var response = await WithRetry(() => http.GetAsync(pdfUrl, HttpCompletionOption.ResponseHeadersRead), pdfUrl);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (response.IsSuccessStatusCode && contentType.Contains("pdf"))
            {
                // Stream directly to blob — no buffering in memory
                await using var stream = await response.Content.ReadAsStreamAsync();
                await container.GetBlobClient(blobName).UploadAsync(stream, overwrite: true);
                Console.WriteLine($"✅ {blobName}");
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
            Console.WriteLine($"❌ Upload failed [{blobName}]: {ex.Message}");
            Interlocked.Increment(ref failed);
        }
    }
}

// ── Run producer + consumer concurrently ─────────────────────────────────────
var scrapeSemaphore = new SemaphoreSlim(SCRAPE_PARALLEL);

var producerTask = Task.Run(async () =>
{
    var tasks = allPaths.Select(async path =>
    {
        await scrapeSemaphore.WaitAsync();
        try { await ScrapeRichtlijn(path); }
        finally { scrapeSemaphore.Release(); }
    });
    await Task.WhenAll(tasks);
    channel.Writer.Complete(); // signal consumers we're done
});

var consumerTasks = Enumerable
    .Range(0, UPLOAD_PARALLEL)
    .Select(_ => UploadWorker())
    .ToArray();

await producerTask;
await Task.WhenAll(consumerTasks);

Console.WriteLine($"\n🎉 Done — uploaded: {uploaded}, skipped: {skipped}, failed: {failed}");