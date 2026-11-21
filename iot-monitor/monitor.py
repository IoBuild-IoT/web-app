"""
IoBuild IoT monitor - read-only live view of everything that crosses the MQTT broker.

Subscribes to registry/#, commands/# and telemetry/# and serves a single web page
(WebSocket) that shows device registration, commands sent from the app, telemetry from
the ESP32 / simulator and the round trip "command -> device reports the new state".

It never publishes and never touches MySQL or InfluxDB. Meant for demos, not for exposure.

Environment variables:
  MQTT_HOST / MQTT_PORT          - broker (default: localhost:1883)
  MQTT_USERNAME / MQTT_PASSWORD  - broker credentials (optional; anonymous when unset)
  HARDWARE_DEVICE_IDS            - comma-separated ids served by the real ESP32
  PORT                           - HTTP port (default: 8090)
"""

import asyncio
import json
import os
import time
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

MAX_EVENTS = 500
STATIC_DIR = Path(__file__).parent / "static"


def parse_ids(raw):
    return frozenset(int(p) for p in (raw or "").replace(" ", "").split(",") if p.isdigit())


def now_iso():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def state_matches(desired, reported):
    """True when every key the app asked for is reported back with the same value."""
    return isinstance(desired, dict) and isinstance(reported, dict) and bool(desired) and all(
        k in reported and reported[k] == v for k, v in desired.items()
    )


class Monitor:
    """Turns raw MQTT messages into events and keeps the latest state per device."""

    def __init__(self, hardware_ids=frozenset(), clock=time.monotonic):
        self.hardware_ids = hardware_ids
        self.clock = clock
        self.events = deque(maxlen=MAX_EVENTS)
        self.devices = {}   # id -> dict shown on the device cards
        self.pending = {}   # id -> {"desired": dict, "at": float}
        self.mqtt_connected = False
        self._seq = 0

    def _device(self, device_id):
        return self.devices.setdefault(device_id, {
            "id": device_id, "type": None, "source": self._source(device_id),
            "status": None, "reported": None, "temperature_c": None, "node": None, "last_seen": None,
        })

    def _source(self, device_id, event_id=None):
        if device_id in self.hardware_ids or (event_id or "").startswith("esp32-"):
            return "hardware"
        return "simulator"

    def _emit(self, kind, device_id, **data):
        self._seq += 1
        event = {"seq": self._seq, "at": now_iso(), "kind": kind, "deviceId": device_id, **data}
        self.events.append(event)
        return event

    def handle(self, topic, payload, retained=False):
        """Process one MQTT message; returns the list of events it produced."""
        kind, _, tail = topic.partition("/")
        if not tail.isdigit() or kind not in ("registry", "commands", "telemetry"):
            return []
        device_id = int(tail)
        text = payload.decode("utf-8", errors="replace") if isinstance(payload, (bytes, bytearray)) else payload
        if kind == "registry":
            return self._registry(device_id, text)
        try:
            body = json.loads(text) if text else {}
        except json.JSONDecodeError:
            return []
        if not isinstance(body, dict):
            return []
        if kind == "commands":
            return self._command(device_id, body, retained)
        return self._telemetry(device_id, body)

    def _registry(self, device_id, text):
        if not text:
            self.devices.pop(device_id, None)
            self.pending.pop(device_id, None)
            return [self._emit("device_removed", device_id)]
        try:
            dtype = json.loads(text).get("type")
        except (json.JSONDecodeError, AttributeError):
            dtype = None
        device = self._device(device_id)
        device["type"] = dtype
        return [self._emit("device_registered", device_id, type=dtype, source=device["source"])]

    def _command(self, device_id, desired, retained):
        # A retained command delivered on connect is old news: show it, but don't wait for an answer.
        if not retained:
            self.pending[device_id] = {"desired": desired, "at": self.clock()}
        return [self._emit("command_sent", device_id, desired=desired, replayed=retained)]

    def _telemetry(self, device_id, body):
        reported = body.get("reported") if isinstance(body.get("reported"), dict) else {}
        source = self._source(device_id, str(body.get("eventId", "")))
        device = self._device(device_id)
        device.update(
            source=source, status=body.get("status"), reported=reported,
            temperature_c=body.get("temperature_c"),
            node=body.get("node") if isinstance(body.get("node"), dict) else None,
            last_seen=now_iso(),
        )
        events = [self._emit(
            "telemetry", device_id, source=source, status=body.get("status"), reported=reported,
            temperature_c=body.get("temperature_c"), energy_kwh=body.get("energy_kwh"),
            voltage_v=body.get("voltage_v"), node=device["node"],
        )]
        waiting = self.pending.get(device_id)
        if waiting and state_matches(waiting["desired"], reported):
            latency_ms = round((self.clock() - waiting["at"]) * 1000)
            del self.pending[device_id]
            events.append(self._emit("command_executed", device_id, desired=waiting["desired"],
                                     reported=reported, latency_ms=latency_ms, source=source))
        return events

    def snapshot(self):
        return {"mqtt": self.mqtt_connected, "devices": list(self.devices.values()), "events": list(self.events)}


def build_app(monitor):
    from aiohttp import web

    clients = set()

    async def index(_request):
        return web.FileResponse(STATIC_DIR / "index.html")

    async def state(_request):
        return web.json_response(monitor.snapshot())

    async def ws(request):
        socket = web.WebSocketResponse(heartbeat=20)
        await socket.prepare(request)
        await socket.send_json({"type": "snapshot", **monitor.snapshot()})
        clients.add(socket)
        try:
            async for _ in socket:
                pass
        finally:
            clients.discard(socket)
        return socket

    async def broadcast(message):
        for socket in list(clients):
            try:
                await socket.send_json(message)
            except ConnectionResetError:
                clients.discard(socket)

    app = web.Application()
    app.add_routes([web.get("/", index), web.get("/api/state", state), web.get("/ws", ws)])
    app["broadcast"] = broadcast
    return app


def start_mqtt(app, monitor, loop):
    import paho.mqtt.client as mqtt

    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="iobuild-monitor")
    user = os.getenv("MQTT_USERNAME")
    if user:
        client.username_pw_set(user, os.getenv("MQTT_PASSWORD"))

    def on_connect(c, _u, _f, rc, _p=None):
        print(f"[MQTT] connected rc={rc}")
        for topic in ("registry/#", "commands/#", "telemetry/#"):
            c.subscribe(topic, qos=1)
        loop.call_soon_threadsafe(set_connected, True)

    def on_disconnect(*_args):
        print("[MQTT] disconnected")
        loop.call_soon_threadsafe(set_connected, False)

    def set_connected(value):
        monitor.mqtt_connected = value
        asyncio.ensure_future(app["broadcast"]({"type": "status", "mqtt": value}))

    def on_message(_c, _u, message):
        def process():
            events = monitor.handle(message.topic, message.payload, bool(message.retain))
            if events:
                asyncio.ensure_future(app["broadcast"](
                    {"type": "events", "events": events, "devices": list(monitor.devices.values())}))
        loop.call_soon_threadsafe(process)

    client.on_connect, client.on_disconnect, client.on_message = on_connect, on_disconnect, on_message
    client.reconnect_delay_set(min_delay=1, max_delay=10)
    client.connect_async(os.getenv("MQTT_HOST", "localhost"), int(os.getenv("MQTT_PORT", "1883")))
    client.loop_start()
    return client


def main():
    from aiohttp import web

    monitor = Monitor(parse_ids(os.getenv("HARDWARE_DEVICE_IDS")))
    app = build_app(monitor)

    async def on_startup(_app):
        start_mqtt(app, monitor, asyncio.get_running_loop())

    app.on_startup.append(on_startup)
    web.run_app(app, port=int(os.getenv("PORT", "8090")), print=lambda *_: print("iot-monitor listening"))


if __name__ == "__main__":
    main()
