using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

public class PlaneryBmdExport
{
    private readonly ILogger _log;
    private static readonly HttpClient _http = new HttpClient();

    public PlaneryBmdExport(ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<PlaneryBmdExport>();
    }

    // Läuft 04:05 Europe/Vienna (im Portal bei App Settings: WEBSITE_TIME_ZONE = Europe/Vienna setzen)
    [Function("PlaneryBmdExport")]
    public async Task Run([TimerTrigger("0 5 2,3 * * *")] TimerInfo timer)
    {
        var baseUrl = Env("PLANERY_BASE_URL");            // https://app.planery.io/api
        var username = Env("PLANERY_USERNAME");
        var password = Env("PLANERY_PASSWORD");
        var companyId = Env("PLANERY_COMPANY_ID");        // 8102

        var blobAccountUrl = Env("BLOB_ACCOUNT_URL");     // https://woodspacedatalake.blob.core.windows.net
        var containerName = Env("BLOB_CONTAINER");        // datalake-raw
        var prefix = (Env("BLOB_PREFIX") ?? "").Trim('/');
        if (!string.IsNullOrWhiteSpace(prefix)) prefix += "/";

        // WICHTIG: viennaNow muss existieren
        var viennaNow = DateTime.Now;

        // 2 Dateien: CURRENT + PREVIOUS (im selben Ordner), benannt "MMM yyyy.csv"
        await ExportMonthToBlob(baseUrl, username, password, companyId, blobAccountUrl, containerName, prefix, viennaNow, "CURRENT_MONTH");
        await ExportMonthToBlob(baseUrl, username, password, companyId, blobAccountUrl, containerName, prefix, viennaNow, "PREVIOUS_MONTH");
    }

    private async Task ExportMonthToBlob(
        string baseUrl,
        string username,
        string password,
        string companyId,
        string blobAccountUrl,
        string containerName,
        string prefix,
        DateTime viennaNow,
        string mode)
    {
        (DateTime startLocal, DateTime endLocal) = GetRange(viennaNow, mode);

        // Planery akzeptiert das Format (wie bei dir in Make)
        var startStr = startLocal.ToString("yyyy-MM-dd HH:mm:ss");
        var endStr = endLocal.ToString("yyyy-MM-dd HH:mm:ss");

        _log.LogInformation("Export mode={mode}. Range Vienna: {start} - {end}", mode, startStr, endStr);

        var token = await GetToken(baseUrl, username, password);

        var url = $"{baseUrl}/companies/{companyId}/export/bmd/times" +
                  $"?start={Uri.EscapeDataString(startStr)}&end={Uri.EscapeDataString(endStr)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Planery export failed. Mode={mode}. Status={status}. Body={body}", mode, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        // Response: { "data": { "name": "...csv", "data": "<base64>" } }
        using var doc = JsonDocument.Parse(body);
        var dataObj = doc.RootElement.GetProperty("data");
        var base64 = dataObj.GetProperty("data").GetString();

        if (string.IsNullOrWhiteSpace(base64))
            throw new Exception($"Response 'data.data' (base64) is empty. Mode={mode}");

        var bytes = Convert.FromBase64String(base64);

        // Filename: "feb 2026.csv" / "jan 2026.csv" (immer automatisch passend)
        var fileName = startLocal.ToString("MMM yyyy").ToLowerInvariant() + ".csv";
        var blobName = $"{prefix}{fileName}";

        _log.LogInformation("Uploading Mode={mode} to {container}/{blob} ({bytes} bytes)", mode, containerName, blobName, bytes.Length);

        var blobService = new BlobServiceClient(new Uri(blobAccountUrl), new DefaultAzureCredential());
        var container = blobService.GetBlobContainerClient(containerName);
        var blob = container.GetBlobClient(blobName);

        using var ms = new MemoryStream(bytes);
        await blob.UploadAsync(ms, overwrite: true);

        _log.LogInformation("Done. Mode={mode}. Uploaded {bytes} bytes to {blob}", mode, bytes.Length, blobName);
    }

    private static async Task<string> GetToken(string baseUrl, string username, string password)
    {
        var tokenUrl = $"{baseUrl}/oauth/token";

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "planery-cli",
            ["username"] = username,
            ["password"] = password
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token request failed. Status={(int)resp.StatusCode}. Body={body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new Exception("No access_token in token response.");
    }

    private static (DateTime start, DateTime end) GetRange(DateTime viennaNow, string mode)
    {
        mode = (mode ?? "PREVIOUS_MONTH").ToUpperInvariant();

        if (mode == "CURRENT_MONTH")
        {
            var start = new DateTime(viennaNow.Year, viennaNow.Month, 1, 0, 0, 0);
            var end = start.AddMonths(1).AddSeconds(-1);
            return (start, end);
        }

        // PREVIOUS_MONTH default
        var firstThisMonth = new DateTime(viennaNow.Year, viennaNow.Month, 1, 0, 0, 0);
        var startPrev = firstThisMonth.AddMonths(-1);
        var endPrev = firstThisMonth.AddSeconds(-1);
        return (startPrev, endPrev);
    }

    private static string Env(string key)
        => Environment.GetEnvironmentVariable(key)
           ?? throw new Exception($"Missing app setting: {key}");
}


