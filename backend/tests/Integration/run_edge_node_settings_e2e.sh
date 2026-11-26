#!/usr/bin/env bash
# G2 proof: an Owner tunes ESP32 thresholds (EdgeNode) and the full desired state
# lands retained on commands/{id}; out-of-range values are refused; the node's
# echo in `reported` acknowledges the command.
# Usage (from web-app/): bash backend/tests/Integration/run_edge_node_settings_e2e.sh
set -euo pipefail
cd "$(dirname "$0")/../../.."
project="iobuild-edgenode-e2e"
api="http://localhost:8080/api/v1"
dc() { docker compose -p "$project" --profile telemetry "$@"; }
cleanup() { dc down -v >/dev/null 2>&1 || true; }
trap cleanup EXIT
json() { python3 -c "import json,sys; print(json.load(sys.stdin)$1)"; }
post() {
  local out status
  out=$(curl -sS -w $'\n%{http_code}' -H "$1" -H 'content-type: application/json' -d "$2" "$api/$3")
  status=${out##*$'\n'}; out=${out%$'\n'*}
  [[ $status == 2* ]] || { echo "POST $3 -> $status: $out" >&2; return 1; }
  printf '%s' "$out"
}
code() { curl -sS -o /dev/null -w '%{http_code}' -H "$1" -H 'content-type: application/json' -d "$2" "$api/$3"; }

dc up -d --build --wait iobuild-api mysql-monolith influxdb mosquitto
for _ in $(seq 1 90); do curl -fsS "$api/devices/types" >/dev/null 2>&1 && break; sleep 2; done

curl -fsS "$api/devices/types" | python3 -c '
import json, sys
edge = {t["code"]: t for t in json.load(sys.stdin)["deviceTypes"]}["EdgeNode"]
attrs = {a["name"]: a for a in edge["controllableAttributes"]}
assert edge["scope"] == "unit"
assert set(attrs) == {"lightHoldSeconds", "climateDelta", "alarmDelta", "blindStep", "blindAngle", "alarmEnabled", "motionEnabled", "blindAuto"}, attrs
assert attrs["climateDelta"]["step"] == 0.5 and attrs["climateDelta"]["default"] == 1.5
assert attrs["alarmEnabled"]["type"] == "boolean"
print("catalog ok")'

stamp=$(date +%s); pw="secret123"; owner_email="owner-$stamp@example.test"
none='X-None: 1'
post "$none" "{\"email\":\"builder-$stamp@example.test\",\"password\":\"$pw\",\"role\":\"Builder\"}" users >/dev/null
builder=$(post "$none" "{\"email\":\"builder-$stamp@example.test\",\"password\":\"$pw\"}" sessions)
bauth="Authorization: Bearer $(json '["token"]' <<<"$builder")"; builder_id=$(json '["id"]' <<<"$builder")
project_id=$(post "$bauth" "{\"name\":\"Edge\",\"description\":\"e2e\",\"location\":\"Lima\",\"totalUnits\":1,\"builderId\":$builder_id,\"imageUrl\":null}" projects | json '["id"]')
unit_id=$(post "$bauth" "{\"projectId\":$project_id,\"unitNumber\":\"E1\",\"floor\":1,\"roomNumber\":\"E1\"}" units | json '["id"]')
post "$bauth" "{\"fullName\":\"Edge Owner\",\"projectName\":\"Edge\",\"accountStatement\":\"Pending\",\"builderId\":$builder_id,\"projectId\":$project_id,\"email\":\"$owner_email\",\"phoneNumber\":\"+51987654312\",\"address\":\"Av. Arequipa 100\",\"unitId\":$unit_id,\"unitNumber\":\"E1\"}" clients >/dev/null
post "$none" "{\"email\":\"$owner_email\",\"password\":\"$pw\",\"role\":\"Owner\"}" users >/dev/null
oauth="Authorization: Bearer $(post "$none" "{\"email\":\"$owner_email\",\"password\":\"$pw\"}" sessions | json '["token"]')"
device_id=$(post "$oauth" "{\"name\":\"ESP32 Nodo\",\"type\":\"EdgeNode\",\"location\":\"Sala\",\"projectId\":$project_id,\"unitId\":$unit_id,\"status\":\"online\"}" devices | json '["id"]')
echo "edge node $device_id created"

post "$oauth" '{"attribute":"climateDelta","value":2.5}' "devices/$device_id/commands" >/dev/null
post "$oauth" '{"attribute":"lightHoldSeconds","value":30}' "devices/$device_id/commands" >/dev/null
post "$oauth" '{"attribute":"alarmEnabled","value":false}' "devices/$device_id/commands" >/dev/null
[[ $(code "$oauth" '{"attribute":"alarmDelta","value":50}' "devices/$device_id/commands") == 400 ]]
[[ $(code "$oauth" '{"attribute":"power","value":true}' "devices/$device_id/commands") == 400 ]]
[[ $(code "$bauth" '{"attribute":"blindStep","value":2}' "devices/$device_id/commands") == 403 ]]

retained=$(dc exec -T mosquitto mosquitto_sub -h localhost -t "commands/$device_id" -C 1 -W 10)
echo "retained: $retained"
python3 - "$retained" <<'PY'
import json, sys
assert json.loads(sys.argv[1]) == {"climateDelta": 2.5, "lightHoldSeconds": 30, "alarmEnabled": False}, sys.argv[1]
PY

# The ESP32 applies the settings and echoes them back in `reported` → command acknowledged.
# Timestamp needs ms precision: the backend only acks telemetry not older than the command.
dc exec -T mosquitto mosquitto_pub -h localhost -t "telemetry/$device_id" -q 1 -m "{\"deviceId\":$device_id,\"eventId\":\"esp32-$device_id-$stamp\",\"timestamp\":\"$(python3 -c 'from datetime import datetime,timezone; print(datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00","Z"))')\",\"status\":\"online\",\"reported\":$retained,\"energy_kwh\":0,\"temperature_c\":24.8,\"voltage_v\":220,\"node\":{\"humidity\":50,\"presence\":false,\"blind_deg\":0,\"alarm\":false}}"
acked=0
for _ in $(seq 1 20); do
  acked=$(dc exec -T mysql-monolith sh -c 'mysql -N -uroot -p"$MYSQL_ROOT_PASSWORD" iobuild -e "SELECT COUNT(*) FROM device_commands WHERE DeviceId='"$device_id"' AND AcknowledgedAt IS NOT NULL;"' 2>/dev/null | tr -d '\r')
  [[ "$acked" -gt 0 ]] && break; sleep 1
done
[[ "$acked" -gt 0 ]] || { echo "edge node command was not acknowledged"; exit 1; }
curl -fsS -H "$oauth" "$api/devices/$device_id/status" | python3 -c '
import json, sys
s = json.load(sys.stdin)
assert s["desired"] == {"climateDelta": 2.5, "lightHoldSeconds": 30, "alarmEnabled": False}, s
assert s["node"]["humidity_pct"] == 50, s
print("status ok:", s)'
echo "PASS edge node settings e2e"
