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

    // 02:05 und 03:05 UTC → läuft dann 04:05 in AT (DST-sicher, wir skippen die falsche Stunde)
    [Function("PlaneryBmdExport")]
    public async Task Run([TimerTrigger("0 5 2,3 * * *")] TimerInfo timer)
    {
        var viennaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        var viennaNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, viennaTz);

        if (viennaNow.Hour != 4)
        {
            _log.LogInformation("Skip. Vienna time is {time}", viennaNow);
            return;
        }

        var baseUrl = Env("PLANERY_BASE_URL");            // https://app.planery.io/api
        var username = Env("PLANERY_USERNAME");
        var password = Env("PLANERY_PASSWORD");
        var companyId = Env("PLANERY_COMPANY_ID");        // 8102

        var blobAccountUrl = Env("BLOB_ACCOUNT_URL");     // https://woodspacedatalake.blob.core.windows.net
        var containerName = Env("BLOB_CONTAINER");        // datalake-raw
        var prefix = (Env("BLOB_PREFIX") ?? "").Trim('/');
        if (!string.IsNullOrWhiteSpace(prefix)) prefix += "/";

        var mode = (Environment.GetEnvironmentVariable("EXPORT_MODE") ?? "PREVIOUS_MONTH").ToUpperInvariant();
        (DateTime startLocal, DateTime endLocal) = GetRange(viennaNow.DateTime, mode);

        // Format, das eure API akzeptiert (wie in Make erfolgreich)
        var startStr = startLocal.ToString("yyyy-MM-dd HH:mm:ss");
        var endStr = endLocal.ToString("yyyy-MM-dd HH:mm:ss");

        _log.LogInformation("Export range Vienna: {start} - {end}", startStr, endStr);

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
            _log.LogError("Planery export failed. Status={status}. Body={body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        // Response: { "data": { "name": "...csv", "data": "<base64>" } }
        using var doc = JsonDocument.Parse(body);
        var dataObj = doc.RootElement.GetProperty("data");
        var fileName = dataObj.GetProperty("name").GetString() ?? "export.csv";
        var base64 = dataObj.GetProperty("data").GetString();

        if (string.IsNullOrWhiteSpace(base64))
            throw new Exception("Response 'data.data' (base64) is empty.");

        var bytes = Convert.FromBase64String(base64);

        var blobName = $"{prefix}{fileName}";
        _log.LogInformation("Uploading to {container}/{blob}", containerName, blobName);

        var blobService = new BlobServiceClient(new Uri(blobAccountUrl), new DefaultAzureCredential());
        var container = blobService.GetBlobContainerClient(containerName);
        var blob = container.GetBlobClient(blobName);

        using var ms = new MemoryStream(bytes);
        await blob.UploadAsync(ms, overwrite: true);

        _log.LogInformation("Done. Uploaded {bytes} bytes.", bytes.Length);
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
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new Exception("No access_token in token response.");
    }

    private static (DateTime start, DateTime end) GetRange(DateTime viennaNow, string mode)
    {
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
