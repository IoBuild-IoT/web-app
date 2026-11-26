// Pure summary of the owner's devices built from their REAL latest readings
// (GET /devices/{id}/status), so the dashboard shows what the hardware is doing now
// instead of precomputed or placeholder numbers.
// A device is up only while the server sees it reporting.
export const isDeviceUp = (reading) => reading?.connected === true;

const average = (values) => (values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : null);
const anyOf = (values) => (values.length ? values.some(Boolean) : null);

/**
 * @param {Array<{deviceId:number}>} devices   owner's devices (deviceHealthStatus)
 * @param {Object<number, object|null>} readings  latest /status payload per device id
 * @returns summary; `ready` is false until at least one reading has arrived, so the UI can
 *          show placeholders instead of "0 online" while the first poll is in flight.
 */
export function summarizeLive(devices, readings) {
  const list = devices ?? [];
  const loaded = list.filter((device) => readings?.[device.deviceId]);
  const fresh = loaded.filter((device) => isDeviceUp(readings[device.deviceId]));
  const freshReadings = fresh.map((device) => readings[device.deviceId]);

  // Edge-node telemetry (humidity, presence, relays, alarm, blind) is only present on real hardware.
  const nodes = freshReadings.map((reading) => reading.node).filter(Boolean);
  const withTemperature = freshReadings.filter((reading) => Number(reading.temperatureC) > 0);
  const preferred = withTemperature.filter((reading) => reading.node);
  const temperatures = (preferred.length ? preferred : withTemperature).map((reading) => Number(reading.temperatureC));
  const humidities = nodes.map((node) => node.humidity_pct).filter((value) => typeof value === 'number');
  const blind = nodes.map((node) => node.blind_deg).filter((value) => typeof value === 'number');

  const alarm = anyOf(nodes.filter((node) => node.alarm !== undefined && node.alarm !== null).map((node) => node.alarm === true));
  const offline = list.length - fresh.length;
  const times = loaded.map((device) => new Date(readings[device.deviceId].lastSeen).getTime()).filter(Number.isFinite);

  return {
    ready: loaded.length > 0,
    total: list.length,
    online: fresh.length,
    offline,
    temperature: average(temperatures),
    humidity: average(humidities),
    presence: anyOf(nodes.map((node) => node.presence).filter((value) => typeof value === 'boolean')),
    light: anyOf(nodes.map((node) => node.light).filter((value) => typeof value === 'boolean')),
    climate: anyOf(nodes.map((node) => node.climate).filter((value) => typeof value === 'boolean')),
    blindDeg: blind.length ? blind[0] : null,
    alarm,
    // Real alerts: an alarm that is active right now, plus every device that stopped reporting.
    alerts: (alarm ? 1 : 0) + (loaded.length ? offline : 0),
    lastSeen: times.length ? new Date(Math.max(...times)).toISOString() : null,
  };
}
