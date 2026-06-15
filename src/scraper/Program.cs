using Microsoft.Playwright;
using Azure.Storage.Blobs;
using Azure.Identity;

const string BASE = "https://lci.rivm.nl";
const int MAX_PARALLEL = 3; // be gentle — headless browser is heavier than HTTP

var storageUrl = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_URL")
    ?? throw new Exception("STORAGE_ACCOUNT_URL is required");
var containerName = Environment.GetEnvironmentVariable("AZURE_CONTAINER_NAME") ?? "protocols";

var container = new BlobServiceClient(new Uri(storageUrl), new DefaultAzureCredential())
    .GetBlobContainerClient(containerName);

await container.CreateIfNotExistsAsync();

var allPaths = File.ReadAllLines("richtlijn-links.txt")
    .Select(l => l.Trim())
    .Where(l => l.StartsWith("/richtlijnen/"))
    .Distinct()
    .ToList();

Console.WriteLine($"Found {allPaths.Count} richtlijnen");

// Skip already uploaded blobs
Console.WriteLine("Fetching existing blobs...");
var existingBlobs = new HashSet<string>();
await foreach (var blob in container.GetBlobsAsync())
    existingBlobs.Add(blob.Name);
Console.WriteLine($"{existingBlobs.Count} already uploaded — will skip");

// Install Playwright browsers if not already present
var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
if (exitCode != 0) throw new Exception("Playwright browser install failed");

int uploaded = 0, skipped = 0, failed = 0;

async Task ScrapeRichtlijn(IPage page, string path)
{
    var slug     = path.Split('/').Last();
    var blobName = $"lci_{slug}/{slug}.pdf";

    if (existingBlobs.Contains(blobName))
    {
        Console.WriteLine($"⏭️  Exists: {blobName}");
        Interlocked.Increment(ref skipped);
        return;
    }

    try
    {
        await page.GotoAsync(BASE + path, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });

        // Accept cookie banner if present
        var cookieBtn = page.Locator("button:has-text('Accepteren')");
        if (await cookieBtn.CountAsync() > 0)
            await cookieBtn.ClickAsync();

        // Strip nav/chrome for a clean PDF
        await page.EvaluateAsync(@"() => {
            document.querySelectorAll('header, footer, nav, .breadcrumb, #navbar-main, .cookie-banner')
                .forEach(el => el.remove());
        }");

        var pdfBytes = await page.PdfAsync(new PagePdfOptions
        {
            Format          = "A4",
            PrintBackground = true,
            Margin          = new Margin { Top = "20mm", Bottom = "20mm", Left = "15mm", Right = "15mm" }
        });

        using var stream = new MemoryStream(pdfBytes);
        await container.GetBlobClient(blobName).UploadAsync(stream, overwrite: true);
        Console.WriteLine($"✅ {blobName} ({pdfBytes.Length / 1024}kb)");
        Interlocked.Increment(ref uploaded);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ {path}: {ex.Message}");
        Interlocked.Increment(ref failed);
    }
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

var semaphore = new SemaphoreSlim(MAX_PARALLEL);

var tasks = allPaths.Select(async path =>
{
    await semaphore.WaitAsync();
    var page = await browser.NewPageAsync();
    try { await ScrapeRichtlijn(page, path); }
    finally
    {
        await page.CloseAsync();
        semaphore.Release();
    }
});

await Task.WhenAll(tasks);

Console.WriteLine();
Console.WriteLine($"Done — uploaded: {uploaded}, skipped: {skipped}, failed: {failed}");
