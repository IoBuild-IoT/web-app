#!/usr/bin/env bash
# G2 proof: an ESP32-shaped MQTT message reaches GET /devices/{id}/status as `node`,
# and a malformed one is logged instead of silently dropped.
# Usage (from web-app/): bash backend/tests/Integration/run_esp32_node_e2e.sh
# Needs Docker. Uses its own compose project and removes it (with volumes) on exit.
set -euo pipefail
cd "$(dirname "$0")/../../.."
project="iobuild-esp32-e2e"
api="http://localhost:8080/api/v1"
dc() { docker compose -p "$project" --profile telemetry "$@"; }
cleanup() { dc down -v >/dev/null 2>&1 || true; }
trap cleanup EXIT

dc up -d --build --wait iobuild-api mysql-monolith influxdb mosquitto
for _ in $(seq 1 90); do curl -fsS "$api/devices/types" >/dev/null 2>&1 && break; sleep 2; done

email="esp32-e2e-$(date +%s)@example.test"
curl -sS --fail-with-body -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"secret123\",\"role\":\"Builder\"}" "$api/users" >/dev/null
token=$(curl -sS --fail-with-body -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"secret123\"}" "$api/sessions" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
auth="Authorization: Bearer $token"
project_id=$(curl -sS --fail-with-body -H "$auth" -H 'content-type: application/json' -d '{"name":"ESP32","description":"e2e","location":"Lima","totalUnits":1,"builderId":1,"imageUrl":null}' "$api/projects" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
device_id=$(curl -sS --fail-with-body -H "$auth" -H 'content-type: application/json' -d "{\"name\":\"ESP32 Light\",\"type\":\"SmartLight\",\"location\":\"Lab\",\"macAddress\":null,\"projectId\":$project_id,\"status\":\"online\"}" "$api/devices" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
echo "device $device_id created"

pub() { dc exec -T mosquitto mosquitto_pub -h localhost -t "telemetry/$device_id" -q 1 -m "$1"; }
now=$(date -u +%Y-%m-%dT%H:%M:%S.000Z)
# Same shape as the firmware; blind_deg as a decimal on purpose.
pub "{\"deviceId\":$device_id,\"eventId\":\"esp32-e2e-$(date +%s)\",\"timestamp\":\"$now\",\"status\":\"online\",\"reported\":{\"power\":true,\"brightness\":100},\"energy_kwh\":0.0017,\"temperature_c\":25.4,\"voltage_v\":220,\"node\":{\"humidity\":61,\"presence\":true,\"blind_deg\":179.6,\"alarm\":false}}"
pub "not json from a broken firmware"

body=""
for _ in $(seq 1 20); do
  body=$(curl -sS --fail-with-body -H "$auth" "$api/devices/$device_id/status")
  grep -q '"node":{' <<<"$body" && break
  sleep 1
done
echo "status: $body"
python3 - "$body" <<'PY'
import json, sys
status = json.loads(sys.argv[1])
node = status["node"]
assert abs(status["temperatureC"] - 25.4) < 1e-9, status
assert node["humidity_pct"] == 61, node
assert node["presence"] is True, node
assert node["blind_deg"] == 180, node
assert node["alarm"] is False, node
PY
# The broken message is handled right after the valid one, so give the log a few seconds to show it.
logged=0
for _ in $(seq 1 15); do
  dc logs iobuild-api 2>&1 | grep -q "telemetry/$device_id: telemetry rejected" && { logged=1; break; }
  sleep 1
done
[[ $logged == 1 ]] || { echo "malformed payload was not logged"; dc logs --tail 40 iobuild-api 2>&1 | grep -i "mqtt\|telemetry"; exit 1; }
echo "PASS esp32 node e2e"
