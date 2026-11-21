using IoBuild.Api.Devices;
using IoBuild.Api.Devices.Application.Internal.CommandServices;
using IoBuild.Api.Devices.Domain.Model.Aggregates;
using IoBuild.Api.Devices.Infrastructure.InfluxDB;
using IoBuild.Api.Devices.Infrastructure.Mqtt;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace IoBuild.Integration.Tests.Devices.CommandTelemetry.Application;

[Trait("Context", "Devices")]
[Trait("Capability", "CommandTelemetry")]
[Trait("Layer", "Application")]
[Trait("Dependency", "InMemory")]
public sealed class NodeTelemetryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-21T18:38:55Z");

    [Fact]
    [Trait("Category", "IoT")]
    public void Influx_line_includes_node_fields_when_the_edge_node_reports_them()
    {
        var message = new TelemetryMessage(8, "e1", At, "online", "{}", 0.5, 25, 220, new NodeSample(55, true, 180, false));

        var line = InfluxHttpTelemetrySink.BuildLine(message);

        Assert.Contains("humidity_pct=55,presence=true,blind_deg=180i,alarm=false", line);
        Assert.StartsWith("telemetry,deviceId=8 energy_kwh=0.5,", line);
    }

    [Fact]
    [Trait("Category", "IoT")]
    public void Influx_line_is_unchanged_without_node_data()
    {
        var line = InfluxHttpTelemetrySink.BuildLine(new TelemetryMessage(8, "e1", At, "online", "{}", 0.5, 25, 220));

        Assert.DoesNotContain("humidity_pct", line);
        Assert.DoesNotContain("presence", line);
        Assert.EndsWith($"status=\"online\" {At.ToUnixTimeMilliseconds() * 1_000_000}", line);
    }

    // Payload exactly as documented in the edge-node README (MQTT contract).
    private const string Esp32Payload = """
        {"deviceId":8,"eventId":"esp32-8-1795286335-27","timestamp":"2026-11-21T18:38:55.079Z","status":"online",
         "reported":{"power":true,"mode":"cooling","targetTemperature":24},"energy_kwh":0.0017,"temperature_c":25.0,
         "voltage_v":220,"node":{"relay":true,"humidity":55,"presence":false,"blind_deg":180,"alarm":false}}
        """;

    [Fact]
    [Trait("Category", "IoT")]
    public async Task Esp32_payload_is_ingested_updates_state_and_reaches_influx_with_node_data()
    {
        await using var db = Db();
        db.Devices.Add(new Device { Id = 8, Name = "Clima", Type = "AirConditioner", Location = "A", ProjectId = 1, Status = "offline", OwnerId = 1 });
        await db.SaveChangesAsync();
        var sink = new RecordingSink();
        using var doc = JsonDocument.Parse(Esp32Payload);
        var root = doc.RootElement;
        var message = new TelemetryMessage(8, root.GetProperty("eventId").GetString()!, DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!),
            root.GetProperty("status").GetString()!, root.GetProperty("reported").GetRawText(), root.GetProperty("energy_kwh").GetDouble(),
            root.GetProperty("temperature_c").GetDouble(), root.GetProperty("voltage_v").GetDouble(), MqttDeviceTransport.ParseNode(root));

        Assert.True(await new DeviceTelemetryService(db, sink).IngestAsync(message));

        Assert.Equal("online", (await db.Devices.SingleAsync()).Status);
        Assert.Equal(25.0, (await db.DeviceTelemetry.SingleAsync()).TemperatureC);
        Assert.Contains("\"mode\":\"cooling\"", (await db.DeviceShadows.SingleAsync()).ReportedJson);
        var written = Assert.Single(sink.Writes);
        Assert.Equal(new NodeSample(55, false, 180, false), written.Node);
        Assert.NotNull((await db.DeviceTelemetry.SingleAsync()).InfluxWrittenAt);
    }

    [Fact]
    [Trait("Category", "IoT")]
    public async Task App_command_is_confirmed_when_the_esp32_reports_the_same_state()
    {
        await using var db = Db();
        db.Devices.Add(new Device { Id = 7, Name = "Luz", Type = "SmartLight", Location = "A", ProjectId = 1, Status = "online", OwnerId = 4 });
        await db.SaveChangesAsync();
        var commands = new DeviceCommandService(db, new NullPublisher());
        var command = await commands.SendAsync(7, 4, "brightness", JsonSerializer.SerializeToElement(80));
        var sink = new RecordingSink();

        // ESP32 echoes the desired state in "reported" with its own eventId (not the command id).
        await new DeviceTelemetryService(db, sink).IngestAsync(new TelemetryMessage(7, "esp32-7-1-1", command.IssuedAt.AddSeconds(1), "online", "{\"brightness\":80}", 0.01, 22, 220, new NodeSample(50, true, 0, false)));

        Assert.NotNull((await db.DeviceCommands.SingleAsync()).AcknowledgedAt);
    }

    [Fact]
    [Trait("Category", "IoT")]
    public async Task Influx_outage_keeps_core_telemetry_in_mysql_and_replay_writes_it_without_node_fields()
    {
        await using var db = Db();
        db.Devices.Add(new Device { Id = 8, Name = "Clima", Type = "AirConditioner", Location = "A", ProjectId = 1, Status = "online", OwnerId = 1 });
        await db.SaveChangesAsync();
        var sink = new RecordingSink { Available = false };
        var service = new DeviceTelemetryService(db, sink);

        await service.IngestAsync(new TelemetryMessage(8, "e-outage", At, "online", "{}", 0.2, 26, 220, new NodeSample(60, true, 90, false)));

        Assert.Null((await db.DeviceTelemetry.SingleAsync()).InfluxWrittenAt);
        sink.Available = true;
        Assert.Equal(1, await service.ReplayInfluxAsync());
        var replayed = Assert.Single(sink.Writes);
        Assert.Null(replayed.Node);
        Assert.Equal(26, replayed.TemperatureC);
    }

    [Fact]
    [Trait("Category", "IoT")]
    public async Task Telemetry_for_an_unregistered_device_is_rejected_and_not_written_anywhere()
    {
        await using var db = Db();
        var sink = new RecordingSink();

        Assert.False(await new DeviceTelemetryService(db, sink).IngestAsync(new TelemetryMessage(99, "e-unknown", At, "online", "{}", 0, 20, 220, new NodeSample(40))));

        Assert.Empty(sink.Writes);
        Assert.Empty(await db.DeviceTelemetry.ToListAsync());
    }

    [Fact]
    [Trait("Category", "IoT")]
    public void Node_block_is_parsed_and_malformed_values_are_ignored()
    {
        using var full = JsonDocument.Parse("{\"node\":{\"relay\":true,\"humidity\":55,\"presence\":false,\"blind_deg\":90,\"alarm\":true}}");
        Assert.Equal(new NodeSample(55, false, 90, true), MqttDeviceTransport.ParseNode(full.RootElement));

        using var partial = JsonDocument.Parse("{\"node\":{\"humidity\":\"x\",\"presence\":1}}");
        Assert.Equal(new NodeSample(), MqttDeviceTransport.ParseNode(partial.RootElement));

        using var none = JsonDocument.Parse("{\"status\":\"online\"}");
        Assert.Null(MqttDeviceTransport.ParseNode(none.RootElement));
    }

    private static IoBuildDbContext Db() => new(new DbContextOptionsBuilder<IoBuildDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NullPublisher : IDeviceMqttPublisher
    {
        public Task PublishAsync(string topic, string payload, bool qos1, bool retain, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSink : IInfluxTelemetrySink
    {
        public bool Available { get; set; } = true;
        public List<TelemetryMessage> Writes { get; } = [];
        public Task WriteAsync(TelemetryMessage message, CancellationToken cancellationToken = default)
        {
            if (!Available) throw new HttpRequestException("influx unavailable");
            Writes.Add(message);
            return Task.CompletedTask;
        }
    }
}
