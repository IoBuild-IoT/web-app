# Devices business rules

Status: piloted. Rules verified against the codebase under Convergent Testing.
Journey: DEVICES.CONTROL (Owner sends commands to own unit devices).

## Identity

- Served actors: `Owner` controls unit devices; `Builder` provisions and
  manages project devices. The catalog fixes controllable attributes per type
  (power, brightness, mode, targetTemperature with ranges).
- A device belongs to a project and optionally to a unit; unit ownership is
  proven by `UnitOwnerProjection`, project ownership by `Project.BuilderId`.

## Manage

- Device data reads require ownership: Builders see devices in their own
  projects; Owners see devices in units assigned through `UnitOwnerProjection`.
  Collection filters (`projectId`, `unitId`) narrow that authorized set; they
  never broaden it. Foreign device IDs read as not found.
- The device list, by-ID read, status, and energy routes share this access
  boundary. There is no global Admin list role; other roles cannot list devices.
- The device-type catalog and anonymous telemetry ingestion are separate
  endpoints with their own intentionally different access rules.
- Mutations require ownership: the unit owner for unit devices, the project
  builder for project devices. Foreign ids read as not found.
- Custom unit devices require the Owner role plus propagated unit ownership;
  floor-scope catalog types cannot live in a unit; MAC addresses and
  per-unit type rows stay unique (409 on duplicates).
- Telemetry ingestion is anonymous by design (devices report without users).

## Control

- Only unit owners send commands: Owner role, device assigned to a unit, and
  matching unit ownership, enforced inside the service with per-device locks.
- Attributes validate per type and range before anything is persisted or
  published; violations fail closed with 400.
- Desired state persists in the device shadow before publish; unacknowledged
  commands republish; telemetry merges without regressing reported state.
- `EdgeNode` (unit scope) exposes the ESP32 thresholds `lightHoldSeconds`
  (5–600), `climateDelta` (0.5–5), `alarmDelta` (1–10), `blindStep` (0.5–5),
  servo target `blindAngle` (0–180) and booleans `alarmEnabled`,
  `motionEnabled` (PIR) and `blindAuto`; it has no `power`. Ranges and defaults
  live in `EdgeNodeSettings` and feed both `/devices/types` and command validation.
- Servo: with `blindAuto` true the node opens/closes the blind by temperature
  (`blindStep`); with `blindAuto` false it moves to `blindAngle`. Moving to an
  angle from automatic mode sends `blindAuto=false` first, then `blindAngle`.
- Edge-node telemetry `node` also carries the real relay states `light` and
  `climate`; the owner board shows those as "right now", never the commanded power.
- The owner control board treats a reading older than 2 minutes as offline and
  hides live chips instead of presenting stale hardware state as current.
- Real hardware limits: the prototype's yellow and blue LEDs are switched by relays (on/off only),
  so once a device reports edge-node telemetry the owner board hides `brightness` (light) and
  `targetTemperature` (climate, unused by the node) and says why. The firmware's hallway light does
  not depend on `brightness`. Only the green status LED (direct pin) could be dimmed with PWM.
- Connection (supersedes the 2-minute rule): `GET /devices/{id}/status` returns `connected`, true only
  if the last reading is at most 10 s old. `status` still follows the commanded power. The owner UI polls
  every 3 s only while connected; once disconnected it stops and waits for the user to press
  "Connect to ESP32" (4 checks, 2 s apart). The node publishes every 3 s and immediately on change.
  Individual LEDs, relays, servo and PIR give no feedback, so only the board as a whole is detectable.
