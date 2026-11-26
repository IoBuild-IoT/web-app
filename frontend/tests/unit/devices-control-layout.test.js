import { describe, it, expect } from 'vitest';
import {
  blindCommands,
  blindPercent,
  buildCards,
  forHardware,
  formatSeen,
  isCardLocked,
  isCardOn,
  liveState,
  resolveValue,
} from '../../src/devices/application/control-layout.js';

// Convergent Testing G0: DEVICES.CONTROL_BOARD layout rules (which component cards a device
// gets, what each control shows, how the servo is commanded). Pure functions, no DOM.

const num = (name, extra = {}) => ({ name, type: 'number', min: 0, max: 10, ...extra });
const bool = (name, extra = {}) => ({ name, type: 'boolean', ...extra });

const EDGE_NODE = [
  num('lightHoldSeconds', { min: 5, max: 600, default: 10 }),
  num('climateDelta', { min: 0.5, max: 5, default: 1.5 }),
  num('alarmDelta', { min: 1, max: 10, default: 3 }),
  num('blindStep', { min: 0.5, max: 5, default: 1 }),
  num('blindAngle', { min: 0, max: 180, default: 0 }),
  bool('alarmEnabled', { default: true }),
  bool('motionEnabled', { default: true }),
  bool('blindAuto', { default: true }),
];

describe('buildCards', () => {
  it('splits the edge node into one card per physical component, servo included', () => {
    const cards = buildCards('EdgeNode', EDGE_NODE);
    expect(cards.map((card) => card.id)).toEqual(['motion', 'blind', 'alarm', 'thermostat', 'readings']);
    expect(cards.find((card) => card.id === 'blind').wide).toBe(true);
    expect(cards.find((card) => card.id === 'blind').fields.map((field) => field.name)).toEqual(['blindAuto', 'blindAngle', 'blindStep']);
    expect(cards.find((card) => card.id === 'motion').power.name).toBe('motionEnabled');
    expect(cards.find((card) => card.id === 'alarm').power.name).toBe('alarmEnabled');
  });

  it('gives the light and the climate their own switch and settings', () => {
    const light = buildCards('SmartLight', [bool('power'), num('brightness', { max: 100 })]);
    expect(light).toHaveLength(1);
    expect(light[0]).toMatchObject({ id: 'light', power: { name: 'power' } });
    expect(light[0].fields.map((field) => field.name)).toEqual(['brightness']);

    const climate = buildCards('AirConditioner', [num('targetTemperature'), { name: 'mode', type: 'enum', enumMembers: ['cooling'] }, bool('power')]);
    expect(climate[0].fields.map((field) => field.name)).toEqual(['mode', 'targetTemperature']);
  });

  it('keeps every unknown attribute controllable in a generic card', () => {
    const cards = buildCards('SmartLight', [bool('power'), num('brightness'), num('colorTemperature')]);
    const other = cards.find((card) => card.id === 'other');
    expect(other.fields.map((field) => field.name)).toEqual(['colorTemperature']);
    expect(buildCards('SomethingNew', [num('speed')])[0].id).toBe('other');
  });

  it('hides cards whose attributes the backend does not offer, and floor devices get none', () => {
    expect(buildCards('EdgeNode', [num('climateDelta')]).map((card) => card.id)).toEqual(['thermostat', 'readings']);
    expect(buildCards('SmartMeter', [])).toEqual([]);
    expect(buildCards('SmartLight', undefined)).toEqual([]);
  });
});

describe('resolveValue', () => {
  it('prefers the commanded value, then the catalog default, then a neutral value', () => {
    expect(resolveValue(num('climateDelta', { default: 1.5 }), { climateDelta: 2.5 })).toBe(2.5);
    expect(resolveValue(num('climateDelta', { default: 1.5 }), {})).toBe(1.5);
    expect(resolveValue(num('brightness', { min: 0 }), null)).toBe(0);
    expect(resolveValue(bool('power'), {})).toBe(false);
    expect(resolveValue({ name: 'mode', type: 'enum', enumMembers: ['cooling', 'fan'] }, {})).toBe('cooling');
  });

  it('keeps an explicit false instead of falling back to the default', () => {
    expect(resolveValue(bool('alarmEnabled', { default: true }), { alarmEnabled: false })).toBe(false);
  });
});

describe('isCardOn', () => {
  const card = { power: bool('motionEnabled', { default: true }) };
  it('follows the switch, with the catalog default before any command', () => {
    expect(isCardOn(card, {})).toBe(true);
    expect(isCardOn(card, { motionEnabled: false })).toBe(false);
  });
  it('understands textual power values and treats cards without a switch as on', () => {
    const power = { power: bool('power') };
    expect(isCardOn(power, { power: 'off' })).toBe(false);
    expect(isCardOn(power, { power: 'On' })).toBe(true);
    expect(isCardOn({ power: null }, {})).toBe(true);
  });
});

describe('isCardLocked', () => {
  const power = { power: bool('power') };
  const motion = { power: bool('motionEnabled', { default: true }) };

  it('locks the settings only when the switch is explicitly off', () => {
    expect(isCardLocked(power, { power: false })).toBe(true);
    expect(isCardLocked(power, { power: 'off' })).toBe(true);
    expect(isCardLocked(power, { power: true })).toBe(false);
    expect(isCardLocked(motion, { motionEnabled: false })).toBe(true);
  });
  it('locks nothing while the power state is unknown or the card has no switch', () => {
    expect(isCardLocked(power, {})).toBe(false);
    expect(isCardLocked(power, undefined)).toBe(false);
    expect(isCardLocked(motion, {})).toBe(false);
    expect(isCardLocked({ power: null }, { power: false })).toBe(false);
  });
});

describe('liveState', () => {
  const light = { live: 'light' };
  it('reports the REAL relay state, not the commanded power', () => {
    expect(liveState(light, { light: true })).toEqual({ key: 'lightOn', tone: 'on' });
    expect(liveState(light, { light: false })).toEqual({ key: 'lightOff', tone: 'idle' });
    expect(liveState({ live: 'climate' }, { climate: true })).toEqual({ key: 'climateOn', tone: 'on' });
  });
  it('shows nothing for devices without edge-node telemetry', () => {
    expect(liveState(light, null)).toBeNull();
    expect(liveState(light, {})).toBeNull();
    expect(liveState({ id: 'thermostat' }, { light: true })).toBeNull();
  });
  it('maps presence, alarm and blind position', () => {
    expect(liveState({ live: 'presence' }, { presence: true })).toEqual({ key: 'presenceOn', tone: 'on' });
    expect(liveState({ live: 'alarm' }, { alarm: true })).toEqual({ key: 'alarmOn', tone: 'alert' });
    expect(liveState({ live: 'alarm' }, { alarm: false })).toEqual({ key: 'alarmOff', tone: 'idle' });
    expect(liveState({ live: 'blind' }, { blind_deg: 90 })).toEqual({ key: 'blindPosition', tone: 'on' });
    expect(liveState({ live: 'blind' }, { humidity_pct: 50 })).toBeNull();
  });
  it('derives the green status LED from alarm and connection', () => {
    const led = { live: 'statusLed' };
    expect(liveState(led, { alarm: false }, 'online')).toEqual({ key: 'ledOk', tone: 'on' });
    expect(liveState(led, { alarm: true }, 'online')).toEqual({ key: 'ledAlarm', tone: 'alert' });
    expect(liveState(led, { alarm: false }, 'offline')).toEqual({ key: 'ledOffline', tone: 'idle' });
    expect(liveState(led, null, 'online')).toBeNull();
  });
});

describe('servo commands', () => {
  it('switches to manual first when the blind was automatic, so the firmware obeys the angle', () => {
    expect(blindCommands(90, true)).toEqual([['blindAuto', false], ['blindAngle', 90]]);
  });
  it('sends only the angle when already manual', () => {
    expect(blindCommands(180, false)).toEqual([['blindAngle', 180]]);
  });
  it('clamps and rounds the angle to the servo range', () => {
    expect(blindCommands(250, false)).toEqual([['blindAngle', 180]]);
    expect(blindCommands(-20, false)).toEqual([['blindAngle', 0]]);
    expect(blindCommands(44.6, false)).toEqual([['blindAngle', 45]]);
  });
  it('converts degrees to percent open', () => {
    expect([0, 90, 180, 300, -5, 'x'].map(blindPercent)).toEqual([0, 50, 100, 100, 0, 0]);
  });
});

describe('formatSeen', () => {
  const now = Date.parse('2026-11-24T12:00:00Z');
  it('picks the coarsest readable unit', () => {
    expect(formatSeen('2026-11-24T11:59:55Z', now)).toEqual({ unit: 's', amount: 5 });
    expect(formatSeen('2026-11-24T11:55:00Z', now)).toEqual({ unit: 'm', amount: 5 });
    expect(formatSeen('2026-11-24T09:00:00Z', now)).toEqual({ unit: 'h', amount: 3 });
    expect(formatSeen('2026-11-21T12:00:00Z', now)).toEqual({ unit: 'd', amount: 3 });
  });
  it('returns null for missing or invalid dates', () => {
    expect(formatSeen(null, now)).toBeNull();
    expect(formatSeen('not a date', now)).toBeNull();
  });
});

describe('forHardware', () => {
  const light = buildCards('SmartLight', [bool('power'), num('brightness', { max: 100 })]);
  const climate = buildCards('AirConditioner', [bool('power'), { name: 'mode', type: 'enum', enumMembers: ['cooling'] }, num('targetTemperature')]);

  it('hides brightness on a relay-driven light and flags the card so it can say why', () => {
    const [card] = forHardware(light, true);
    expect(card.fields.map((field) => field.name)).toEqual([]);
    expect(card.hardwareNote).toBe(true);
    expect(card.power.name).toBe('power');          // on/off stays
  });

  it('hides the target temperature the node does not use but keeps the mode', () => {
    const [card] = forHardware(climate, true);
    expect(card.fields.map((field) => field.name)).toEqual(['mode']);
    expect(card.hardwareNote).toBe(true);
  });

  it('leaves simulated devices untouched and never mutates the original cards', () => {
    expect(forHardware(light, false)).toBe(light);
    forHardware(light, true);
    expect(light[0].fields.map((field) => field.name)).toEqual(['brightness']);
    expect(light[0].hardwareNote).toBeUndefined();
  });

  it('does not touch cards without hardware limits', () => {
    const edge = buildCards('EdgeNode', EDGE_NODE);
    expect(forHardware(edge, true)).toEqual(edge);
  });
});
