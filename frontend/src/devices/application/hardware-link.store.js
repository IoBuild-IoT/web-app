// One place that knows whether the real hardware is connected, and the only live-status poller.
//   connected    -> reads every device each few seconds
//   disconnected -> NO polling and no automatic retry; connect() checks again when the user asks
// The ESP32 is the one that dials in (to the MQTT broker), so "connect" means: wait for it to report.
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { DeviceApi } from '../infrastructure/device-api.js';
import { DASHBOARD_REFRESH_MS, HARDWARE_CONNECT_ATTEMPTS, HARDWARE_CONNECT_WAIT_MS } from '../../shared/infrastructure/constants.js';

const api = new DeviceApi();
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export const useHardwareLinkStore = defineStore('hardwareLink', () => {
  const state = ref('idle');          // idle | checking | connected | disconnected
  const readings = ref({});           // deviceId -> latest /status payload
  const checkedAt = ref(Date.now());
  const failed = ref(false);          // the last connect() got no answer
  const connected = computed(() => state.value === 'connected');

  let ids = [];
  let timer = null;
  let busy = false;
  let errors = 0;
  let session = 0;                    // bumps on stop(), so late answers are ignored

  // true = some device is reporting, false = none is, null = the API did not answer
  async function read() {
    const mine = session;
    const results = await Promise.all(ids.map(async (id) => {
      try { return [id, await api.getDeviceStatus(id)]; } catch { return [id, null]; }
    }));
    const ok = results.filter(([, reading]) => reading);
    if (mine !== session || !ok.length) return null;
    readings.value = { ...readings.value, ...Object.fromEntries(ok) };
    checkedAt.value = Date.now();
    return ok.some(([, reading]) => reading.connected === true);
  }

  function pause() {
    clearInterval(timer);
    timer = null;
  }

  function disconnect() {
    pause();
    state.value = 'disconnected';
    readings.value = Object.fromEntries(Object.entries(readings.value).map(([id, reading]) => [id, { ...reading, connected: false }]));
  }

  async function refresh() {
    if (busy) return;
    busy = true;
    const mine = session;
    try {
      const up = await read();
      if (mine !== session) return;
      errors = up === null ? errors + 1 : 0;
      if (up === false || errors >= 2) disconnect();   // one network blip is tolerated
    } finally {
      busy = false;
    }
  }

  function follow() {
    pause();
    errors = 0;
    failed.value = false;
    state.value = 'connected';
    timer = setInterval(() => { if (!document.hidden) refresh(); }, DASHBOARD_REFRESH_MS);
  }

  async function check(attempts) {
    pause();
    const mine = session;
    state.value = 'checking';
    for (let attempt = 0; attempt < attempts; attempt += 1) {
      if (attempt) await sleep(HARDWARE_CONNECT_WAIT_MS);
      const up = await read();
      if (mine !== session) return false;
      if (up) { follow(); return true; }
    }
    disconnect();
    return false;
  }

  // A page starts watching its devices: a single check, polling only if it succeeds.
  function start(deviceIds) {
    session += 1;
    ids = [...deviceIds];
    busy = false;
    failed.value = false;
    return ids.length ? check(1) : null;
  }

  // The "Connect to ESP32" button.
  async function connect() {
    if (state.value === 'checking') return;
    failed.value = false;
    const mine = session;
    const ok = await check(HARDWARE_CONNECT_ATTEMPTS);
    if (!ok && mine === session) failed.value = true;
  }

  function stop() {
    session += 1;
    pause();
    state.value = 'idle';
  }

  return { state, readings, checkedAt, failed, connected, start, connect, refresh, stop };
});
