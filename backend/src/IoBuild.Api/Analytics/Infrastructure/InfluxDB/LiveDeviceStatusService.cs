using System.Text.Json;
using IoBuild.Api.Analytics.Application.Internal.QueryServices;
using Microsoft.Extensions.Logging;

namespace IoBuild.Api.Analytics.Infrastructure.InfluxDB;

public sealed class LiveDeviceStatusService : ILiveDeviceStatusService
{
    private static DateTimeOffset _lastFailureTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan CoolOffPeriod = TimeSpan.FromSeconds(60);

    // The node block is polled every few seconds by the dashboards, so it has its own short
    // cool-off: a slow read must neither blank the live panel for a minute nor disable the
    // status read above.
    private static DateTimeOffset _lastNodeFailureTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan NodeCoolOffPeriod = TimeSpan.FromSeconds(10);

    // Only the LATEST node reading is needed (freshness is judged from lastSeen), so a short range
    // keeps the query fast no matter how long the node has been reporting every few seconds.
    public const string NodeRange = "-15m";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LiveDeviceStatusService>? _logger;

    public LiveDeviceStatusService(HttpClient httpClient, IConfiguration configuration, ILogger<LiveDeviceStatusService>? logger = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private static readonly string[] NodeFields = ["humidity_pct", "presence", "blind_deg", "alarm", "light", "climate"];

    public async Task<Dictionary<string, object>?> GetLatestNodeAsync(int deviceId, CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _lastFailureTime < CoolOffPeriod || DateTimeOffset.UtcNow - _lastNodeFailureTime < NodeCoolOffPeriod) return null;
        var url = _configuration["Influx:Url"];
        var org = _configuration["Influx:Org"];
        var bucket = _configuration["Influx:Bucket"];
        var token = _configuration["Influx:Token"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(token)) return null;
        var fieldFilter = string.Join(" or ", NodeFields.Select(field => $"r._field == \"{field}\""));
        var flux = $"from(bucket: \"{bucket}\") |> range(start: {NodeRange}) |> filter(fn: (r) => r._measurement == \"telemetry\" and r.deviceId == \"{deviceId}\") |> filter(fn: (r) => {fieldFilter}) |> last()";
        var endpoint = new Uri(new Uri(url.TrimEnd('/') + "/"), $"api/v2/query?org={Uri.EscapeDataString(org)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = flux, type = "flux" }), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var node = new Dictionary<string, object>();
            foreach (var row in FluxCsv.Parse(await response.Content.ReadAsStringAsync(ct)))
            {
                if (!row.TryGetValue("_field", out var field) || !row.TryGetValue("_value", out var raw) || raw.Length == 0) continue;
                object? value = field is "presence" or "alarm" or "light" or "climate"
                    ? bool.TryParse(raw, out var flag) ? flag : null
                    : double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
                if (value is not null) node[field] = value;
            }
            return node.Count > 0 ? node : null;
        }
        catch (Exception ex)
        {
            _lastNodeFailureTime = DateTimeOffset.UtcNow;
            _logger?.LogWarning(ex, "LiveDeviceStatusService: Influx node query failed — cooling off for 10s");
            return null;
        }
    }

    public async Task<Dictionary<string, string>> GetLatestStatusesAsync(IEnumerable<string> deviceIds, CancellationToken ct = default)
    {
        var ids = deviceIds.ToList();
        if (ids.Count == 0) return new Dictionary<string, string>();
        if (DateTimeOffset.UtcNow - _lastFailureTime < CoolOffPeriod)
        {
            return new Dictionary<string, string>();
        }
        var url = _configuration["Influx:Url"];
        var org = _configuration["Influx:Org"];
        var bucket = _configuration["Influx:Bucket"];
        var token = _configuration["Influx:Token"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(token))
        {
            return new Dictionary<string, string>();
        }
        var idFilter = string.Join(" or ", ids.Select(id => $"r.deviceId == \"{id}\""));
        var flux = $"from(bucket: \"{bucket}\") |> range(start: -30d) |> filter(fn: (r) => r._measurement == \"telemetry\" and r._field == \"status\") |> filter(fn: (r) => {idFilter}) |> group(columns: [\"deviceId\"]) |> last()";
        var endpoint = new Uri(new Uri(url.TrimEnd('/') + "/"), $"api/v2/query?org={Uri.EscapeDataString(org)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = flux, type = "flux" }), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            var statuses = new Dictionary<string, string>();
            foreach (var row in FluxCsv.Parse(body))
                if (row.TryGetValue("deviceId", out var id) && row.TryGetValue("_value", out var status) && id.Length > 0 && status.Length > 0)
                    statuses[id] = status;
            _logger?.LogInformation("LiveDeviceStatusService: {Count} status(es) from Influx", statuses.Count);
            return statuses;
        }
        catch (Exception ex)
        {
            _lastFailureTime = DateTimeOffset.UtcNow;
            _logger?.LogWarning(ex, "LiveDeviceStatusService: Influx query failed — cooling off for 60s, returning empty");
            return new Dictionary<string, string>();
        }
    }
}
