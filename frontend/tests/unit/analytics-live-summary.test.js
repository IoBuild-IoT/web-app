import { describe, it, expect } from 'vitest';
import { isDeviceUp, summarizeLive } from '../../src/analytics/application/live-summary.js';

// Convergent Testing G0: ANALYTICS.LIVE owner summary built from real device readings.

const NOW = Date.parse('2026-11-24T12:00:00Z');
const ago = (seconds) => new Date(NOW - seconds * 1000).toISOString();
const devices = [{ deviceId: 16 }, { deviceId: 17 }, { deviceId: 18 }];
const node = (extra = {}) => ({ humidity_pct: 64, presence: false, blind_deg: 90, alarm: false, light: false, climate: false, ...extra });
const reading = (extra = {}) => ({ connected: true, status: 'online', lastSeen: ago(3), temperatureC: 25, node: node(), ...extra });

describe('isDeviceUp', () => {
  it('trusts only the server connection flag, not the commanded status', () => {
    expect(isDeviceUp(reading())).toBe(true);
    expect(isDeviceUp(reading({ connected: false, status: 'online' }))).toBe(false);
    expect(isDeviceUp(null)).toBe(false);
  });
});

describe('summarizeLive', () => {
  it('is not ready until a first reading arrives, so the UI shows placeholders, not "0 online"', () => {
    const summary = summarizeLive(devices, {});
    expect(summary.ready).toBe(false);
    expect(summary.alerts).toBe(0);
    expect(summary.temperature).toBeNull();
  });

  it('summarizes real node telemetry', () => {
    const readings = {
      16: reading({ temperatureC: 25.2 }),
      17: reading({ temperatureC: 24.8, node: node({ light: true, presence: true }) }),
      18: reading({ temperatureC: 25.0, node: node({ blind_deg: 180 }) }),
    };
    const summary = summarizeLive(devices, readings);
    expect(summary).toMatchObject({ ready: true, total: 3, online: 3, offline: 0, alarm: false, alerts: 0 });
    expect(summary.temperature).toBeCloseTo(25.0, 5);
    expect(summary.humidity).toBe(64);
    expect(summary.presence).toBe(true);   // any device saw motion
    expect(summary.light).toBe(true);
    expect(summary.climate).toBe(false);
    expect(summary.blindDeg).toBe(90);
    expect(summary.lastSeen).toBe(ago(3));
  });

  it('raises ONE alarm alert even though every device repeats the node block', () => {
    const alarmNode = node({ alarm: true });
    const readings = { 16: reading({ node: alarmNode }), 17: reading({ node: alarmNode }), 18: reading({ node: alarmNode }) };
    const summary = summarizeLive(devices, readings);
    expect(summary.alarm).toBe(true);
    expect(summary.alerts).toBe(1);
  });

  it('counts a device that stopped reporting as offline and as an alert, and ignores its old values', () => {
    const readings = {
      16: reading(),
      17: reading({ connected: false, lastSeen: ago(900), node: node({ presence: true, alarm: true }), temperatureC: 40 }),
      18: reading(),
    };
    const summary = summarizeLive(devices, readings);
    expect(summary).toMatchObject({ online: 2, offline: 1, alerts: 1, alarm: false, presence: false });
    expect(summary.temperature).toBeCloseTo(25, 5);   // the stale 40 °C is not averaged in
  });

  it('with every device silent, shows no live values and one alert per device', () => {
    const old = ago(900);
    const readings = { 16: reading({ connected: false, lastSeen: old }), 17: reading({ connected: false, lastSeen: old }), 18: reading({ connected: false, lastSeen: old }) };
    const summary = summarizeLive(devices, readings);
    expect(summary).toMatchObject({ ready: true, online: 0, offline: 3, alerts: 3, temperature: null, humidity: null, presence: null, blindDeg: null, alarm: null });
    expect(summary.lastSeen).toBe(old);   // still tells when it was last heard
  });

  it('prefers real hardware temperature over simulated devices', () => {
    const readings = {
      16: reading({ temperatureC: 33, node: null }),   // simulated: no node block
      18: reading({ temperatureC: 25 }),
    };
    expect(summarizeLive([{ deviceId: 16 }, { deviceId: 18 }], readings).temperature).toBe(25);
  });

  it('falls back to simulated devices when there is no edge node, and ignores zero temperatures', () => {
    const readings = { 16: reading({ temperatureC: 0, node: null }), 17: reading({ temperatureC: 22, node: null }) };
    const summary = summarizeLive([{ deviceId: 16 }, { deviceId: 17 }], readings);
    expect(summary.temperature).toBe(22);
    expect(summary.humidity).toBeNull();
    expect(summary.light).toBeNull();
  });

  it('keeps unknown values null for devices that never sent the node block', () => {
    const summary = summarizeLive([{ deviceId: 16 }], { 16: reading({ node: null }) });
    expect(summary).toMatchObject({ online: 1, presence: null, alarm: null, blindDeg: null });
  });
});
