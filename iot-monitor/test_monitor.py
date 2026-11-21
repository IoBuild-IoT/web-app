"""Run from iot-monitor/: python -m unittest test_monitor"""
import json
import unittest

import monitor as m


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now


def esp32_telemetry(reported, **extra):
    return json.dumps({"deviceId": 8, "eventId": "esp32-8-1-1", "status": "online", "reported": reported,
                       "temperature_c": 25.0, "node": {"humidity": 55, "presence": False, "blind_deg": 180, "alarm": False},
                       **extra}).encode()


class MonitorTest(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.mon = m.Monitor(frozenset({8}), clock=self.clock)

    def test_registry_registers_and_tombstone_removes(self):
        events = self.mon.handle("registry/8", b'{"deviceId": 8, "type": "AirConditioner"}')
        self.assertEqual([e["kind"] for e in events], ["device_registered"])
        self.assertEqual(self.mon.devices[8]["type"], "AirConditioner")
        self.assertEqual(self.mon.devices[8]["source"], "hardware")
        events = self.mon.handle("registry/8", b"")
        self.assertEqual([e["kind"] for e in events], ["device_removed"])
        self.assertNotIn(8, self.mon.devices)

    def test_command_then_matching_telemetry_is_traced_with_latency(self):
        self.mon.handle("commands/8", b'{"power": true, "mode": "cooling", "targetTemperature": 24}')
        self.clock.now += 1.25
        events = self.mon.handle("telemetry/8", esp32_telemetry({"power": True, "mode": "cooling", "targetTemperature": 24}))
        self.assertEqual([e["kind"] for e in events], ["telemetry", "command_executed"])
        self.assertEqual(events[1]["latency_ms"], 1250)
        self.assertNotIn(8, self.mon.pending)

    def test_unrelated_telemetry_keeps_the_command_pending(self):
        self.mon.handle("commands/8", b'{"power": true}')
        events = self.mon.handle("telemetry/8", esp32_telemetry({"power": False}))
        self.assertEqual([e["kind"] for e in events], ["telemetry"])
        self.assertIn(8, self.mon.pending)

    def test_retained_command_on_connect_is_shown_but_not_awaited(self):
        events = self.mon.handle("commands/8", b'{"power": true}', retained=True)
        self.assertTrue(events[0]["replayed"])
        self.assertNotIn(8, self.mon.pending)
        events = self.mon.handle("telemetry/8", esp32_telemetry({"power": True}))
        self.assertEqual([e["kind"] for e in events], ["telemetry"])

    def test_telemetry_updates_device_card_and_detects_source(self):
        self.mon.handle("telemetry/2", json.dumps({"eventId": "sim-1", "status": "idle", "reported": {}}).encode())
        self.assertEqual(self.mon.devices[2]["source"], "simulator")
        self.mon.handle("telemetry/9", esp32_telemetry({}, eventId="esp32-9-1-1"))
        self.assertEqual(self.mon.devices[9]["source"], "hardware")
        self.assertEqual(self.mon.devices[9]["node"]["humidity"], 55)

    def test_garbage_is_ignored(self):
        for topic, payload in [("telemetry/x", b"{}"), ("other/1", b"{}"), ("telemetry/1", b"not json"), ("telemetry/1", b"[1]")]:
            self.assertEqual(self.mon.handle(topic, payload), [])

    def test_event_buffer_is_bounded(self):
        for _ in range(m.MAX_EVENTS + 25):
            self.mon.handle("telemetry/8", esp32_telemetry({}))
        self.assertEqual(len(self.mon.events), m.MAX_EVENTS)

    def test_state_matches_requires_every_requested_key(self):
        self.assertTrue(m.state_matches({"power": True}, {"power": True, "mode": "fan"}))
        self.assertFalse(m.state_matches({"power": True, "mode": "heating"}, {"power": True, "mode": "fan"}))
        self.assertFalse(m.state_matches({}, {"power": True}))


if __name__ == "__main__":
    unittest.main()
