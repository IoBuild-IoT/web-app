using System.Buffers;
using IoBuild.Api.Devices.Application.Internal.CommandServices;
using IoBuild.Api.Devices.Domain.Model.Aggregates;
using IoBuild.Api.Devices.Domain.Model.Entities;
using IoBuild.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace IoBuild.Api.Devices.Infrastructure.Mqtt;

public sealed class MqttDeviceTransport(Microsoft.Extensions.Configuration.IConfiguration configuration, IServiceScopeFactory scopes, ILogger<MqttDeviceTransport> logger) : IDeviceMqttPublisher, IHostedService, IAsyncDisposable
{
    private readonly MQTTnet.IMqttClient client = new MQTTnet.MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private bool messageHandlerConfigured;
    private bool reconnectHandlerConfigured;
    private volatile bool stopping;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Mqtt:Enabled")) return;
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (!messageHandlerConfigured)
            {
                client.ApplicationMessageReceivedAsync += async arguments =>
                {
                    var topic = arguments.ApplicationMessage.Topic ?? string.Empty;
                    if (!topic.StartsWith("telemetry/", StringComparison.Ordinal) || !int.TryParse(topic["telemetry/".Length..], out var id)) return;
                    var payloadString = System.Text.Encoding.UTF8.GetString(arguments.ApplicationMessage.Payload.ToArray());
                    try
                    {
                        var message = ParseTelemetry(id, payloadString, DateTimeOffset.UtcNow);
                        using var scope = scopes.CreateScope();
                        if (!await scope.ServiceProvider.GetRequiredService<DeviceTelemetryService>().IngestAsync(message))
                            logger.LogWarning("MQTT {Topic}: device {DeviceId} is not registered; telemetry ignored", topic, id);
                    }
                    catch (Exception exception)
                    {
                        // ponytail: log and drop; a malformed reading must not kill the subscription.
                        logger.LogWarning(exception, "MQTT {Topic}: telemetry rejected. Payload: {Payload}", topic, payloadString.Length > 500 ? payloadString[..500] : payloadString);
                    }
                };
                messageHandlerConfigured = true;
            }
            if (!reconnectHandlerConfigured)
            {
                client.DisconnectedAsync += async _ =>
                {
                    if (stopping || !configuration.GetValue<bool>("Mqtt:Enabled")) return;
                    for (var attempt = 0; attempt < 30 && !stopping && !client.IsConnected; attempt++)
                    {
                        try { await StartAsync(CancellationToken.None); }
                        catch (HttpRequestException) { await Task.Delay(TimeSpan.FromSeconds(1)); }
                    }
                };
                reconnectHandlerConfigured = true;
            }
            if (client.IsConnected) return;
            var host = configuration["Mqtt:Host"] ?? "localhost"; var port = configuration.GetValue("Mqtt:Port", 1883);
            Exception? failure = null;
            for (var attempt = 0; attempt < 3 && !client.IsConnected; attempt++)
            {
                try { await client.ConnectAsync(ConnectionOptions(host, port), cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException) { failure = exception; await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken); }
            }
            if (!client.IsConnected)
            {
                // Broker not up yet (e.g. Docker restarting everything at once): keep the API alive and retry in background.
                logger.LogWarning(failure, "MQTT broker {Host}:{Port} unavailable; retrying in background", host, port);
                _ = Task.Run(async () =>
                {
                    for (var attempt = 0; attempt < 60 && !stopping && !client.IsConnected; attempt++)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(2)); await StartAsync(CancellationToken.None); }
                        catch { }
                    }
                });
                return;
            }
            await client.SubscribeAsync(new MQTTnet.MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter => filter.WithTopic("telemetry/#").WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)).Build(), cancellationToken);
            using (var scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<DeviceRegistryService>().AnnounceAllAsync(cancellationToken);
                await scope.ServiceProvider.GetRequiredService<DeviceCommandService>().RepublishPendingAsync(cancellationToken);
            }
        }
        finally { connectionGate.Release(); }
    }
    // An ESP32 that has not synced NTP yet reports 1970; anything before this is treated as "no clock".
    private static readonly DateTimeOffset ClockFloor = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TelemetryMessage ParseTelemetry(int id, string payload, DateTimeOffset now)
    {
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var root = json.RootElement;
        var rawTimestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
        var timestamp = rawTimestamp is not null && DateTimeOffset.TryParse(rawTimestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) && parsed >= ClockFloor ? parsed : now;
        var eventId = root.TryGetProperty("eventId", out var eventValue) && eventValue.GetString() is { Length: > 0 } given ? given : $"{id}:{timestamp:O}";
        var status = root.TryGetProperty("status", out var state) ? state.GetString() ?? "unknown" : "unknown";
        var reported = root.TryGetProperty("reported", out var reportedValue) ? reportedValue.GetRawText() : "{}";
        return new TelemetryMessage(id, eventId, timestamp, status, reported, Number(root, "energy_kwh"), Number(root, "temperature_c"), Number(root, "voltage_v"), ParseNode(root));
    }

    private static double Number(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetDouble() : 0;

    public static NodeSample? ParseNode(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("node", out var node) || node.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        double? humidity = node.TryGetProperty("humidity", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number ? h.GetDouble() : null;
        bool? presence = Flag(node, "presence");
        int? blind = node.TryGetProperty("blind_deg", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number ? (int)Math.Round(b.GetDouble()) : null;
        return new NodeSample(humidity, presence, blind, Flag(node, "alarm"), Flag(node, "light"), Flag(node, "climate"));
    }

    private static bool? Flag(System.Text.Json.JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? value.GetBoolean() : null;
    private MQTTnet.MqttClientOptions ConnectionOptions(string host, int port)
    {
        var options = new MQTTnet.MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId("iobuild-devices").WithCleanSession();
        var username = configuration["Mqtt:Username"];
        if (!string.IsNullOrEmpty(username)) options.WithCredentials(username, configuration["Mqtt:Password"]);
        return options.Build();
    }
    public async Task StopAsync(CancellationToken cancellationToken) { stopping = true; if (client.IsConnected) await client.DisconnectAsync(new MQTTnet.MqttClientDisconnectOptions(), cancellationToken); }
    public async Task PublishAsync(string topic, string payload, bool qos1, bool retain, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("Mqtt:Enabled")) return;
        if (!client.IsConnected) await StartAsync(cancellationToken);
        if (!client.IsConnected) throw new HttpRequestException("MQTT is not configured or unavailable.");
        var message = new MQTTnet.MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithQualityOfServiceLevel(qos1 ? MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce : MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce).WithRetainFlag(retain).Build();
        await client.PublishAsync(message, cancellationToken);
    }
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}
