// Pure layout rules for the owner's control board.
//
// The backend catalog (GET /devices/types) says WHICH attributes a device has and
// their ranges. This file only decides HOW they are grouped into physical-component
// cards. Attributes the layout does not know about fall into a generic "other" card,
// so new catalog attributes stay controllable without touching the UI.

// `power`  : boolean attribute shown as the card's main switch
// `fields` : other attributes rendered inside the card, in order
// `live`   : which telemetry value drives the "right now" chip (see liveState)
// `kind`   : 'blind' renders the dedicated servo control, 'readings' the live measurements
// `wide`   : the card takes two grid columns
export const DEVICE_CARDS = {
  SmartLight: [
    { id: 'light', icon: 'pi-lightbulb', tone: 'amber', power: 'power', fields: ['brightness'], live: 'light' },
  ],
  AirConditioner: [
    { id: 'climate', icon: 'pi-sun', tone: 'blue', power: 'power', fields: ['mode', 'targetTemperature'], live: 'climate' },
  ],
  EdgeNode: [
    { id: 'motion', icon: 'pi-eye', tone: 'violet', power: 'motionEnabled', fields: ['lightHoldSeconds'], live: 'presence' },
    { id: 'blind', icon: 'pi-arrows-v', tone: 'teal', kind: 'blind', wide: true, fields: ['blindAuto', 'blindAngle', 'blindStep'], live: 'blind' },
    { id: 'alarm', icon: 'pi-bell', tone: 'red', power: 'alarmEnabled', fields: ['alarmDelta'], live: 'alarm' },
    { id: 'thermostat', icon: 'pi-sliders-h', tone: 'blue', fields: ['climateDelta'] },
    // Read-only: what the node measures right now. The green status LED is shown in the device header.
    { id: 'readings', icon: 'pi-chart-line', tone: 'blue', kind: 'readings', readOnly: true },
  ],
};

import { parsePower } from './control-state.js';

// Real ESP32 outputs are relays: they switch on/off but cannot dim, and the node does not use a
// target temperature. When a device reports hardware (edge-node) telemetry, controls that would
// do nothing are hidden instead of promising something the hardware cannot do.
export const HARDWARE_UNSUPPORTED = {
  light: ['brightness'],
  climate: ['targetTemperature'],
};

// Same cards, minus the unsupported fields, flagged so the card can explain why.
export function forHardware(cards, onHardware) {
  if (!onHardware) return cards;
  return cards.map((card) => {
    const hidden = HARDWARE_UNSUPPORTED[card.id];
    if (!hidden) return card;
    return { ...card, fields: card.fields.filter((field) => !hidden.includes(field.name)), hardwareNote: true };
  });
}

export const BLIND_CLOSED = 0;
export const BLIND_OPEN = 180;

const byName = (attributes) => Object.fromEntries((attributes ?? []).map((attr) => [attr.name, attr]));

// Cards for one device: the layout cards whose attributes exist in the catalog, plus a
// generic card with every controllable attribute the layout did not claim.
export function buildCards(deviceType, attributes) {
  const catalog = byName(attributes);
  const claimed = new Set();
  const cards = [];

  for (const card of DEVICE_CARDS[deviceType] ?? []) {
    const power = card.power && catalog[card.power] ? catalog[card.power] : null;
    const fields = (card.fields ?? []).map((name) => catalog[name]).filter(Boolean);
    if (!power && fields.length === 0 && !card.readOnly) continue;
    if (power) claimed.add(power.name);
    fields.forEach((field) => claimed.add(field.name));
    cards.push({ ...card, power, fields });
  }

  const rest = (attributes ?? []).filter((attr) => !claimed.has(attr.name));
  if (rest.length) cards.push({ id: 'other', icon: 'pi-cog', tone: 'gray', power: null, fields: rest, live: null });
  return cards;
}

// Value the control shows: what the platform last commanded, else the firmware default
// from the catalog, else a neutral value inside the allowed range.
export function resolveValue(attr, desired) {
  if (desired && Object.prototype.hasOwnProperty.call(desired, attr.name)) return desired[attr.name];
  if (attr.default !== undefined && attr.default !== null) return attr.default;
  const kind = String(attr.type ?? '').toLowerCase();
  if (kind === 'boolean') return false;
  if (kind === 'enum') return attr.enumMembers?.[0] ?? '';
  return attr.min ?? 0;
}

// Is the card's main switch on? Cards without a switch are always "on". An unreadable
// value falls back to its truthiness, so a device never gets stuck looking locked.
export function isCardOn(card, desired) {
  if (!card.power) return true;
  const value = resolveValue(card.power, desired);
  const power = parsePower(value);
  return power === null ? Boolean(value) : power;
}

// Settings are locked only when the card's switch is EXPLICITLY off (commanded, or off by
// catalog default). A device that was never commanded has an unknown power state and locks
// nothing, the same rule the backend applies (see control-state.js).
export function isCardLocked(card, desired) {
  if (!card.power) return false;
  const known = Object.prototype.hasOwnProperty.call(desired ?? {}, card.power.name);
  return parsePower(known ? desired[card.power.name] : card.power.default) === false;
}

// The "right now" chip. `node` is the latest edge-node telemetry (real hardware state, not
// the commanded one). Returns { key, tone } where key is an i18n suffix, or null when the
// device reports nothing for this card (simulated devices have no node block).
export function liveState(card, node, status) {
  if (!card.live) return null;
  if (card.live === 'statusLed') {
    if (String(status ?? '').toLowerCase() === 'offline') return { key: 'ledOffline', tone: 'idle' };
    if (node?.alarm === true) return { key: 'ledAlarm', tone: 'alert' };
    return node ? { key: 'ledOk', tone: 'on' } : null;
  }
  if (card.live === 'blind') return node?.blind_deg == null ? null : { key: 'blindPosition', tone: 'on' };
  const value = node?.[card.live];
  if (value === undefined || value === null) return null;
  if (card.live === 'alarm') return { key: value ? 'alarmOn' : 'alarmOff', tone: value ? 'alert' : 'idle' };
  if (card.live === 'presence') return { key: value ? 'presenceOn' : 'presenceOff', tone: value ? 'on' : 'idle' };
  return { key: value ? `${card.live}On` : `${card.live}Off`, tone: value ? 'on' : 'idle' };
}

// Servo helpers ---------------------------------------------------------------------
export const blindPercent = (angle) => Math.round((Math.min(Math.max(Number(angle) || 0, BLIND_CLOSED), BLIND_OPEN) / BLIND_OPEN) * 100);

// Commands needed to move the blind to `angle`. Switching to manual first means the
// firmware obeys the angle; when already manual only the angle is sent.
export function blindCommands(angle, isAuto) {
  const clamped = Math.min(Math.max(Math.round(Number(angle)), BLIND_CLOSED), BLIND_OPEN);
  return isAuto === false
    ? [['blindAngle', clamped]]
    : [['blindAuto', false], ['blindAngle', clamped]];
}

// Short wall-clock label for the "last reading" caption.
export function formatSeen(value, now = Date.now()) {
  if (!value) return null;
  const seconds = Math.max(0, Math.round((now - new Date(value).getTime()) / 1000));
  if (Number.isNaN(seconds)) return null;
  if (seconds < 60) return { unit: 's', amount: seconds };
  if (seconds < 3600) return { unit: 'm', amount: Math.round(seconds / 60) };
  if (seconds < 86400) return { unit: 'h', amount: Math.round(seconds / 3600) };
  return { unit: 'd', amount: Math.round(seconds / 86400) };
}
