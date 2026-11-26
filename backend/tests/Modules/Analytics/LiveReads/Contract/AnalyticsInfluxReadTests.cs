using System.Net;
using System.Text;
using IoBuild.Api.Analytics.Infrastructure.InfluxDB;
using Microsoft.Extensions.Configuration;

namespace IoBuild.Modules.Tests;

// Convergent Testing G0/G1: ANALYTICS live reads parse what InfluxDB really returns
// (annotated CSV from /api/v2/query) instead of discarding it.
[Trait("Context", "Analytics")]
public sealed partial class AnalyticsInfluxReadTests
{
    // Shape produced by the LiveEnergyService Flux query: one table per _time group.
    private const string EnergyCsv =
        "#datatype,string,long,dateTime:RFC3339,double\r\n" +
        "#group,false,false,true,false\r\n" +
        "#default,_result,,,\r\n" +
        ",result,table,_time,_value\r\n" +
        ",,0,2026-09-28T21:00:00Z,1.5\r\n" +
        "\r\n" +
        "#datatype,string,long,dateTime:RFC3339,double\r\n" +
        "#group,false,false,true,false\r\n" +
        "#default,_result,,,\r\n" +
        ",result,table,_time,_value\r\n" +
        ",,1,2026-09-28T21:01:00Z,2.25\r\n";

    private const string StatusCsv =
        "#datatype,string,long,dateTime:RFC3339,dateTime:RFC3339,dateTime:RFC3339,string,string,string,string\r\n" +
        "#group,false,false,true,true,false,false,true,true,true\r\n" +
        "#default,_result,,,,,,,,\r\n" +
        ",result,table,_start,_stop,_time,_value,_field,_measurement,deviceId\r\n" +
        ",,0,2026-08-29T00:00:00Z,2026-09-28T00:00:00Z,2026-09-28T20:59:00Z,idle,status,telemetry,7\r\n" +
        ",,1,2026-08-29T00:00:00Z,2026-09-28T00:00:00Z,2026-09-28T20:59:00Z,\"on,line\",status,telemetry,8\r\n";

    [Fact]
    [Trait("Flow", "ANALYTICS.LIVE")]
    [Trait("Layer", "Contract")]
    [Trait("Risk", "A")]
    public async Task LIVE_ENERGY_returns_ordered_points_from_influx_csv()
    {
        var service = new LiveEnergyService(Client(HttpStatusCode.OK, EnergyCsv), Configured());
        var points = (await service.GetAggregatedAsync(["1", "2"], 10)).ToList();

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateTime(2026, 9, 28, 21, 0, 0, DateTimeKind.Utc), points[0].Timestamp);
        Assert.Equal(1.5, points[0].TotalEnergyKwh);
        Assert.Equal(2.25, points[1].TotalEnergyKwh);
    }

    [Fact]
    [Trait("Flow", "ANALYTICS.LIVE")]
    [Trait("Layer", "Contract")]
    [Trait("Risk", "A")]
    public async Task LIVE_DEVICE_STATUS_maps_latest_status_by_device()
    {
        var service = new LiveDeviceStatusService(Client(HttpStatusCode.OK, StatusCsv), Configured());
        var statuses = await service.GetLatestStatusesAsync(["7", "8"]);

        Assert.Equal("idle", statuses["7"]);
        Assert.Equal("on,line", statuses["8"]);
    }

    [Fact]
    [Trait("Flow", "ANALYTICS.LIVE")]
    [Trait("Layer", "Contract")]
    [Trait("Risk", "B")]
    public async Task LIVE_NODE_maps_latest_edge_node_fields_by_type()
    {
        const string nodeCsv = ",result,table,_field,_value,deviceId\r\n,_result,0,humidity_pct,55.5,8\r\n\r\n,result,table,_field,_value,deviceId\r\n,_result,1,presence,true,8\r\n,_result,2,alarm,false,8\r\n\r\n,result,table,_field,_value,deviceId\r\n,_result,3,blind_deg,180,8\r\n,_result,4,light,true,8\r\n,_result,5,climate,false,8\r\n";
        var node = await new LiveDeviceStatusService(Client(HttpStatusCode.OK, nodeCsv), Configured()).GetLatestNodeAsync(8);

        Assert.NotNull(node);
        Assert.Equal(55.5, node["humidity_pct"]);
        Assert.Equal(true, node["presence"]);
        Assert.Equal(false, node["alarm"]);
        Assert.Equal(180.0, node["blind_deg"]);
        Assert.Equal(true, node["light"]);
        Assert.Equal(false, node["climate"]);
        Assert.Null(await new LiveDeviceStatusService(Client(HttpStatusCode.OK, ""), Configured()).GetLatestNodeAsync(8));
    }

    [Fact]
    [Trait("Flow", "ANALYTICS.LIVE")]
    [Trait("Layer", "Contract")]
    [Trait("Risk", "B")]
    public async Task LIVE_NODE_reads_only_a_short_recent_window_so_it_stays_fast_as_data_grows()
    {
        var handler = new CapturingHandler();
        var node = await new LiveDeviceStatusService(new HttpClient(handler), Configured()).GetLatestNodeAsync(8);

        Assert.Null(node);   // empty CSV: no node data yet
        var flux = System.Text.Json.JsonDocument.Parse(handler.Body).RootElement.GetProperty("query").GetString()!;
        Assert.Contains($"range(start: {LiveDeviceStatusService.NodeRange})", flux);
        Assert.DoesNotContain("-1d", flux);
        Assert.Contains("r.deviceId == \"8\"", flux);
    }

    [Fact]
    [Trait("Flow", "ANALYTICS.LIVE")]
    [Trait("Layer", "Contract")]
    [Trait("Risk", "C")]
    public async Task LIVE_READS_degrade_to_empty_when_influx_fails_is_unconfigured_or_malformed()
    {
        var down = new LiveEnergyService(Client(HttpStatusCode.InternalServerError, "boom"), Configured());
        Assert.Empty(await down.GetAggregatedAsync(["1"], 10));

        var malformed = new LiveEnergyService(Client(HttpStatusCode.OK, ",result,table,_time,_value\r\n,,0,not-a-date,NaNx\r\n"), Configured());
        Assert.Empty(await malformed.GetAggregatedAsync(["1"], 10));

        var unconfigured = new LiveEnergyService(Client(HttpStatusCode.OK, EnergyCsv), new ConfigurationBuilder().Build());
        Assert.Empty(await unconfigured.GetAggregatedAsync(["1"], 10));
        var noStatus = new LiveDeviceStatusService(Client(HttpStatusCode.OK, StatusCsv), new ConfigurationBuilder().Build());
        Assert.Empty(await noStatus.GetLatestStatusesAsync(["7"]));
    }

    private static IConfiguration Configured() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Influx:Url"] = "http://influx.test:8086",
        ["Influx:Org"] = "iobuild",
        ["Influx:Bucket"] = "iobuild-telemetry",
        ["Influx:Token"] = "token",
    }).Build();

    private static HttpClient Client(HttpStatusCode status, string body) => new(new StubHandler(status, body));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty, Encoding.UTF8, "text/csv") };
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/csv") });
    }
}
