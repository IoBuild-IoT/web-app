# Devices evidence ledger

## Backend test navigation — 2026-10-02

The [Devices catalogue](test-catalog.md) maps management/control/telemetry and
direct service collaboration. [Reorganization evidence](../../../backend/tests/reorganization-evidence.md)
records source/discovery preservation and local reruns. No broker delivery,
live-Influx/MySQL runtime proof or CI pass is added by this organization-only entry.

See the [crosscutting frontend performance report](../../performance/evidence-ledger.md)
for shared gzip/cache/chunks, functional E2E evidence and the new local
mobile/desktop Lighthouse audit of `/devices/device-management` for Builder and
Owner. The small fixture has no live telemetry samples. Local observations do
not establish CI acceptance or all-state performance coverage.

The central round-two entry records named Owner controls and shared contrasts
(Owner measured A100), plus repeated command/power-lock E2E. CPU reduction is
deferred; shadow refresh and all device widgets remain intact.

```yaml
context: devices
status: piloted
journey: DEVICES.CONTROL (Owner sends commands to own unit devices)
feature: devices-convergence
gates:
  G0: passed
  G1: passed
  G2: passed
  G3: passed
  G4: passed
environment:
  database: mysql:8.0 (production engine for all persistence proofs)
  migrations: 202608280001_FoundationSchema → 202608290002_IamAndDispatch → 202608290003_CoreBusiness → 202608300004_DevicesTelemetry → 202608300005_AnalyticsProjections → 202609170006_SubscriptionActiveArbiter
  backend: .NET 9 (CI setup-dotnet 9.0.x)
  frontend: node 22 (CI setup-node 22)
reruns:
  - command: dotnet test backend/IoBuild.sln with live MySQL (temporary 3306:3306 mapping, reverted afterwards)
    result: 197/197 green (15 architecture + 20 contract + 41 integration + 121 modules)
  - command: E2E_BASE_URL=http://localhost:8081 npx playwright test (deployed nginx + dist + API + MySQL)
    result: 8/8 green across journeys; e2e and seeded rows cleaned afterwards
flaky_rate:
  observed: 0 unexplained flakes across all reruns above
mutations:
  - mutant: PUT/DELETE ownership checks removed (any user edits any device)
    killed_by: OWNER/BUILDER manage-own plus foreign-404 and anonymous-401 tests
  - mutant: telemetry field guard removed (null payload reaches EF)
    killed_by: minimal-payload partition expecting 400 (proven red 500 without the guard during development)
  - mutant: per-device lock removed (burst corrupts shadow)
    killed_by: CONCURRENT_SAME_DEVICE 8-way burst asserting one coherent shadow
  - mutant: attribute validation removed (garbage actuates)
    killed_by: CONTROL_REJECTS and Tier D fuzz partitions
skip_reasons: []
commands:
  - command: dotnet test backend/tests/Modules --filter DeviceManageTests
    result: 3/3 passed (owner/builder manage own devices, foreign 404, anonymous 401)
  - command: npm run test:unit (frontend)
    result: 32/32 passed (validators 7/7 + iam-contract 7/7 + subscriptions-contract 7/7 + profiles-contract 6/6 + devices-contract 5/5, last local run)
  - command: dotnet test backend/tests/Modules --filter DeviceControlFlowTests
    result: 2/2 passed (command→telemetry→status convergence; wrong role/unit/attribute/range/missing/anonymous rejections)
  - command: IOBUILD_TEST_MYSQL_CONNECTION=... dotnet test --filter DeviceControlMySqlTests (temporary 3306:3306 mapping, reverted afterwards)
    result: 3/3 passed against live MySQL 8.0 (command plus shadow durability, telemetry durability visible on status, per-unit-type duplicate 409 with MAC intentionally ignored on the owner-custom path); probe rows cleaned
  - command: E2E_BASE_URL=http://localhost:8081 npx playwright test (deployed nginx + dist + API + MySQL)
    result: devices-control.spec.js 1/1 green (builder provisions, owner registers with auto-link, brightness from UI, desired state on status endpoint); full suite 8/8; seeded projects/units/clients/devices/users cleaned afterwards
artifacts: []
failures:
  - class: product
    evidence_for: [telemetry ingest without eventId/status/payload threw unhandled DbUpdateException to a 500]
    evidence_against: [ingest is anonymous by design and takes raw device payloads]
    verdict: fail-closed field guard in the endpoint (400); covered by the minimal-payload fuzz partition
  - class: test
    evidence_for: [telemetry recovery rows survived test cleanup and poisoned the next run with duplicate-key failures]
    evidence_against: [all other tables were cleaned]
    verdict: cleanup extended to recovery rows by event key; proven by 3 consecutive green runs
  - class: product
    evidence_for: [PUT and DELETE device endpoints required login but enforced zero ownership: any user could edit or delete any device]
    evidence_against: [create-custom path already checks Owner role plus unit ownership]
    verdict: unit-owner-or-project-builder management check on mutations; list visibility was unscoped at that time and is closed by the TS04 read-scope follow-up below
open_risks: []
roles_covered:
  - role: Owner
    happy_path: frontend/tests/e2e/devices-control.spec.js
```

## TS04 read-scope follow-up — 2026-10-05

Builder list/read access now requires the device's project to belong to the
authenticated Builder. Owner list/read access requires a unit assigned through
`UnitOwnerProjection`. The same check applies to list, by-ID, status, and energy
reads. No global Admin panel or list role is supported.

```yaml
journey: DEVICES.VISIBILITY (Builder project and Owner unit device reads)
gates:
  G0: skipped (no separate pure rule changed; ownership is asserted at the API boundary)
  G1: passed (WebApplicationFactory with isolated EF Core InMemory store)
  G2: passed (Builder device-list Gherkin and Owner controls E2E on disposable Compose/MySQL stack)
  G3: passed (foreign Builder and unassigned Owner reads return 404)
  G4: passed (TS04 reran in complete TS suite; isolated system tests cleaned up)
commands:
  - command: npm run test:ts -- 04 (from frontend/)
    result: 4/4 passed
  - command: dotnet test backend/tests/Modules/IoBuild.Modules.Tests.csproj --filter "FullyQualifiedName~IoBuild.Modules.Tests.Devices"
    result: 13/13 passed
  - command: dotnet test backend/tests/Modules/IoBuild.Modules.Tests.csproj --filter "TechnicalStory~TS"
    result: 15/15 passed (TS01-TS05)
  - command: npm run test:us -- 01 (from frontend/)
    result: 3/3 scenarios and 9/9 steps passed in disposable Compose stack (Builder journey)
  - command: E2E_BASE_URL=http://localhost:18081 npm --prefix frontend run test:e2e -- tests/e2e/devices-control.spec.js (disposable Compose project ts04-device-readscope)
    result: 1/1 passed (Owner control happy path; stack and volumes removed)
skip_reasons:
  G0: No independent pure domain rule changed; authorization is verified at the API boundary.
open_risks: []
```

## ESP32 edge-node readings and MQTT ingest hardening (2026-11-21)

Real ESP32 `node{}` readings (humidity, presence, blind, alarm) are returned by
`GET /api/v1/devices/{id}/status` as `node` (latest values from InfluxDB) and shown
in the Builder and Owner telemetry status cards. MQTT ingest now logs rejected
payloads instead of swallowing them, rounds decimal `blind_deg`, and uses server
time when the node reports a pre-NTP (1970) timestamp.

```yaml
journey: DEVICES.EDGE_NODE (ESP32 telemetry → status card)
gates:
  G0: passed (ParseTelemetry/ParseNode parser rules; LiveDeviceStatusService node CSV mapping)
  G1: passed (full backend suite; frontend unit contracts and production build)
  G2: passed (disposable Compose stack, mosquitto_pub ESP32 payload → /status node, malformed payload logged)
  G3: partial (malformed JSON, decimal blind, unsynced clock, missing node covered; no auth change)
  G4: not run (single run of each layer)
commands:
  - command: dotnet test backend/IoBuild.sln (sdk:9.0 container)
    result: 274/274 passed (Architecture 17, Contract 20, Integration 50, Modules 187)
  - command: npx vitest run && npx vite build (from frontend/)
    result: 79/79 passed; build OK
  - command: python -m unittest discover -s iot-simulator / -s iot-monitor
    result: OK / 8/8 passed
  - command: bash backend/tests/Integration/run_esp32_node_e2e.sh
    result: PASS (stack and volumes removed)
skip_reasons:
  G4: deterministic rerun not performed in this session.
open_risks:
  - Dashboard rendering of node rows verified by build only, not by browser E2E.
  - Firmware (IoBuild-IoT/edge-node) not in this repo; NTP sync on the node unverified.
  - POST /api/v1/devices/telemetry is AllowAnonymous (pre-existing).

## EdgeNode configurable thresholds (2026-11-21)

```yaml
journey: DEVICES.EDGE_NODE_SETTINGS (Owner tunes ESP32 thresholds → retained command → node echo acks)
actor_coverage: { Owner: sends settings (G2), Builder: refused 403 (G2) }
gates:
  G0: passed (range/type/unknown-attribute validation theory, 7 cases)
  G1: passed (accumulated desired state retained QoS1; full echo acknowledges)
  G2: passed (Compose stack: catalog, Owner commands, 400 out-of-range, 403 Builder, retained payload, ack in MySQL, /status desired+node)
  G3: partial (no cross-field rule alarmDelta > climateDelta; documented, not enforced)
  G4: passed (both e2e scripts rerun from clean state)
commands:
  - command: dotnet test backend/IoBuild.sln (sdk:9.0 container)
    result: 282/282 passed
  - command: npx vitest run && npx vite build (from frontend/)
    result: 79/79 passed; build OK
  - command: bash backend/tests/Integration/run_edge_node_settings_e2e.sh
    result: PASS
  - command: bash backend/tests/Integration/run_esp32_node_e2e.sh
    result: PASS (regression)
diagnostic_verdicts:
  - failure: first run, command not acknowledged
    class: test
    root_cause: test telemetry timestamp truncated to the second, older than IssuedAt; backend correctly ignores pre-command echoes
    fix: millisecond UTC timestamp in the script
open_risks:
  - Firmware side (NVS + echo) delivered as reference snippet only; edge-node repo not available here.
  - Control labels show raw attribute names (existing behavior for all types).

## Owner control board and servo control (2026-11-24)

```yaml
journey: DEVICES.CONTROL_BOARD (Owner controls each prototype component; servo manual/auto)
actor_coverage: { Owner: all components (G0-G2), Builder: unchanged, sees no control board }
gates:
  G0: passed (control-layout rules: cards per device, defaults, lock-only-when-explicitly-off, live state, servo commands, stale readings)
  G1: passed (catalog 8 EdgeNode attributes; blindAngle 0-180, boolean validation, accumulated desired state; node light/climate through Influx)
  G2: partial (integrated browser on the local stack: manual blind + Open -> retained commands/18 {blindAuto:false,blindAngle:180}; Playwright spec updated but not executed)
  G3: partial (out-of-range angle, wrong types, unknown attribute; board rolls back on failed command)
  G4: not run
open_risks:
  - Firmware servo/PIR changes compile (ESP32) but were not flashed or observed on the board in this session.
  - Playwright devices-control.spec.js was rewritten for the new board and not executed (browser download not approved).
  - Live chips depend on the firmware reporting node.light/node.climate (new firmware only).
```
