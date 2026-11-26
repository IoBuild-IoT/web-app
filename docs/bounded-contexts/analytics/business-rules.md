# Analytics business rules

Status: piloted. Rules verified against the codebase under Convergent Testing.
Journey: ANALYTICS.VIEW (Builder dashboard and Owner dashboard variants).

## Identity

- Served actors: `Builder` and `Owner`, each with a dashboard endpoint family.
  Every metric route requires authentication and serves only the caller:
  foreign ids fail (404 on item routes, empty on scoped queries).
- Insights are project-scoped: the caller must own the project (builder) or
  own a unit inside it (owner); otherwise the project reads as not found.

## View

- Builder metrics aggregate projects, units, devices, and subscriptions.
- Owner metrics fall back to live energy data when projections are stale.
- Live energy windows clamp to 1–60 minutes (default 10).
- Device projections stay synchronized with provisioning events; dashboards
  read projections, never live device rows directly.
- Projection self-sync serializes per user: concurrent dashboard loads for one
  tenant converge on single projection rows instead of doubling them.

## Live owner dashboard (real device readings)

- The owner dashboard shows a "live prototype status" built from each owner device's
  latest real reading (`GET /devices/{id}/status`): devices online, temperature, humidity,
  presence, hallway light, climate, blind position and alarm.
- A device counts as online only if its last reading is at most 2 minutes old and its status
  is online/idle/standby. A silent device is offline; its old values never feed the summary.
- Alerts shown on the owner dashboard are real: one for an active alarm plus one per device
  that stopped reporting. (The backend `alertsCount` is a placeholder and is not used while
  live readings are available.)
- Temperature prefers edge-node hardware readings over simulated devices; zero readings are ignored.
- Cards, charts, the live status and the selected device's telemetry refresh every 5 seconds
  while the tab is visible. A failed background refresh keeps the last good data on screen and
  never raises the page-level error state.
- The latest edge-node block is read from InfluxDB over a short recent window (15 minutes) so the
  query stays fast as the node keeps reporting; staleness is decided from `lastSeen`.
