"""Run: python -m unittest iot-simulator/test_simulator.py (from repo root) or python -m unittest test_simulator."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import simulator  # noqa: E402


class FakeClient:
    def __init__(self):
        self.subscribed = []

    def subscribe(self, topic, qos=0):
        self.subscribed.append(topic)

    def unsubscribe(self, topic):
        pass


class HardwareDeviceIdsTest(unittest.TestCase):
    def test_parse(self):
        self.assertEqual(simulator.parse_hardware_ids(" 7, 8,,x, 12 "), frozenset({7, 8, 12}))
        self.assertEqual(simulator.parse_hardware_ids(None), frozenset())

    def test_registry_skips_hardware_ids_and_keeps_others(self):
        simulator.HARDWARE_DEVICE_IDS = frozenset({7})
        simulator._devices.clear()
        client = FakeClient()
        simulator._handle_registry(client, 7, b'{"deviceId": 7, "type": "SmartLight"}')
        simulator._handle_registry(client, 9, b'{"deviceId": 9, "type": "SmartMeter"}')
        self.assertNotIn(7, simulator._devices)
        self.assertIn(9, simulator._devices)
        self.assertEqual(client.subscribed, ["commands/9"])


if __name__ == "__main__":
    unittest.main()
