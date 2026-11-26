<script setup>
// Control board for ONE device: a card per physical component (see control-layout.js).
// - Switches and shortcuts send their command immediately; sliders send on release.
// - Values shown are the platform's confirmed desired state; "right now" chips show the
//   real hardware state from telemetry. Both refresh every few seconds.
// - A failed command puts the control back where it was and says why.
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useToast } from 'primevue/usetoast';
import { useCommandStore } from '../../application/command.store.js';
import { useHardwareLinkStore } from '../../application/hardware-link.store.js';
import { blindCommands, blindPercent, buildCards, forHardware, formatSeen, isCardLocked, isCardOn, liveState, resolveValue } from '../../application/control-layout.js';
import { TOAST_AUTH_ERROR_DURATION_MS, TOAST_SUCCESS_DURATION_MS } from '../../../shared/infrastructure/constants.js';
import ControlCard from './control-card.vue';
import ToggleSwitch from './toggle-switch.vue';
import AttributeField from './attribute-field.vue';
import BlindControl from './blind-control.vue';

const props = defineProps({
  /** Row from the owner dashboard: { deviceId, deviceName, type, status }. */
  device: { type: Object, required: true },
  /** controllableAttributes of this device type, from GET /devices/types. */
  attributes: { type: Array, default: () => [] },
});

const { t, te } = useI18n();
const toast = useToast();
const commandStore = useCommandStore();

const deviceId = computed(() => props.device.deviceId ?? props.device.id);
// Once this device has reported edge-node telemetry it is real hardware; keep that for the session
// so the layout does not flip back while the board is briefly offline.
const onHardware = ref(false);
const cards = computed(() => forHardware(buildCards(props.device.type, props.attributes), onHardware.value));

const link = useHardwareLinkStore();
const status = computed(() => link.readings[deviceId.value] ?? null);   // last GET /devices/{id}/status
const now = computed(() => link.checkedAt);
const desired = ref({});           // confirmed + optimistic settings
const inFlight = ref(0);

watch(status, (next) => {
  if (!next || inFlight.value > 0) return;  // never overwrite a setting that is still being sent
  if (next.node) onHardware.value = true;
  desired.value = next.desired ?? {};
}, { immediate: true });

let refreshTimeout = null;
onBeforeUnmount(() => clearTimeout(refreshTimeout));

// ---- Commands ------------------------------------------------------------------------
const attrLabel = (name) => (te(`devices.board.attributes.${name}`) ? t(`devices.board.attributes.${name}`) : name);

function describe(value) {
  if (value === true) return t('devices.board.switchOn');
  if (value === false) return t('devices.board.switchOff');
  return te(`devices.board.modes.${value}`) ? t(`devices.board.modes.${value}`) : String(value);
}

async function send(name, value) {
  const had = Object.prototype.hasOwnProperty.call(desired.value, name);
  const previous = desired.value[name];
  desired.value = { ...desired.value, [name]: value };        // optimistic
  inFlight.value += 1;
  const result = await commandStore.sendCommand(deviceId.value, name, value);
  inFlight.value -= 1;

  if (result.success) {
    toast.add({ severity: 'success', summary: t('devices.board.sent'), detail: `${attrLabel(name)}: ${describe(value)}`, life: TOAST_SUCCESS_DURATION_MS });
    clearTimeout(refreshTimeout);
    refreshTimeout = setTimeout(() => { if (link.connected) link.refresh(); }, 1500);                // show what the node really did
    return true;
  }
  const rolledBack = { ...desired.value };
  if (had) rolledBack[name] = previous; else delete rolledBack[name];
  desired.value = rolledBack;
  toast.add({
    severity: result.status === 403 ? 'warn' : 'error',
    summary: t('devices.board.failed'),
    detail: result.message,
    life: TOAST_AUTH_ERROR_DURATION_MS,
  });
  return false;
}

async function sendInOrder(commands) {
  for (const [name, value] of commands) {
    if (!(await send(name, value))) return;
  }
}

// ---- View helpers --------------------------------------------------------------------
const cardText = (card, key) => t(`devices.board.cards.${card.id}.${key}`);
const locked = (card) => isCardLocked(card, desired.value);
const fieldValue = (field) => resolveValue(field, desired.value);
const switchText = (card) => {
  const on = isCardOn(card, desired.value);
  if (card.power.name === 'power') return on ? t('devices.board.switchOn') : t('devices.board.switchOff');
  return on ? t('devices.board.enabled') : t('devices.board.disabled');
};

// Bubble color: switch state for cards with a switch, live state for the rest.
function isActive(card) {
  if (card.power) return isCardOn(card, desired.value);
  const live = liveState(card, status.value?.node, status.value?.status);
  return live ? live.tone !== 'idle' : false;
}

// Old readings must not pose as "right now": the chips go away and the status LED card says
// what the real LED does without a connection (slow blink).
const stale = computed(() => Boolean(status.value) && status.value.connected !== true);

function liveFor(card) {
  const node = stale.value ? null : status.value?.node;
  const state = stale.value && card.live === 'statusLed'
    ? { key: 'ledOffline', tone: 'idle' }
    : liveState(card, node, status.value?.status);
  if (!state) return null;
  const angle = Math.round(node?.blind_deg ?? 0);
  return { tone: state.tone, text: t(`devices.board.live.${state.key}`, { angle, percent: blindPercent(angle) }) };
}

// The green status LED is a property of the node, so it lives in the device header.
const ledPill = computed(() => (props.device.type === 'EdgeNode' ? liveFor({ live: 'statusLed' }) : null));

// Live measurements card: temperature comes from the status payload, humidity from the node block.
const readings = computed(() => {
  if (stale.value || !status.value) return { temperature: null, humidity: null };
  const temperature = status.value.node ? Number(status.value.temperatureC) : null;
  const humidity = status.value.node?.humidity_pct;
  return {
    temperature: Number.isFinite(temperature) && temperature > 0 ? temperature.toFixed(1) : null,
    humidity: humidity == null ? null : Math.round(humidity),
  };
});

const blindField = (card, name) => card.fields.find((field) => field.name === name) ?? null;
const blindValue = (card, name, fallback) => {
  const field = blindField(card, name);
  return field ? fieldValue(field) : fallback;
};
const blindIsAuto = (card) => blindValue(card, 'blindAuto', true) !== false;

// Header: connection state + how fresh the last reading is.
const statusKey = computed(() => {
  if (stale.value) return 'offline';
  const value = String(status.value?.status ?? props.device.status ?? '').trim().toLowerCase();
  if (value === 'online' || value === 'active') return 'online';
  if (value === 'idle' || value === 'standby') return 'standby';
  return value ? 'offline' : 'unknown';
});
const STATUS_SEVERITY = { online: 'success', standby: 'warn', offline: 'danger', unknown: 'info' };
const seen = computed(() => {
  const value = formatSeen(status.value?.lastSeen, now.value);
  return value ? t(`devices.board.seen.${value.unit}`, { n: value.amount }) : null;
});
</script>

<template>
  <article class="device" :class="{ 'device--full': cards.length >= 3 }" :data-device-id="deviceId">
    <header class="device__head">
      <h3 class="device__name">{{ device.deviceName }}</h3>
      <div class="device__meta">
        <span v-if="ledPill" class="led" :class="`led--${ledPill.tone}`" role="status">
          <span class="led__dot" aria-hidden="true" />{{ t('devices.board.cards.statusLed.title') }}: {{ ledPill.text }}
        </span>
        <pv-tag :value="t(`devices.board.status.${statusKey}`)" :severity="STATUS_SEVERITY[statusKey]" />
        <span v-if="seen" class="device__seen">{{ t('devices.board.lastReading') }}: {{ seen }}</span>
      </div>
    </header>

    <div class="device__grid">
      <ControlCard
        v-for="card in cards"
        :key="card.id"
        :data-card="card.id"
        :class="{ 'is-wide': card.wide }"
        :icon="card.icon"
        :tone="card.tone"
        :title="cardText(card, 'title')"
        :hint="cardText(card, 'hint')"
        :active="isActive(card)"
        :muted="locked(card)"
        :live="liveFor(card)"
      >
        <template v-if="card.power" #toggle>
          <span class="state">{{ switchText(card) }}</span>
          <ToggleSwitch
            :model-value="isCardOn(card, desired)"
            :label="cardText(card, 'title')"
            @update:model-value="send(card.power.name, $event)"
          />
        </template>

        <template v-if="card.kind === 'blind' || card.kind === 'readings' || card.fields.length || card.hardwareNote" #default>
          <div v-if="card.kind === 'readings'" class="readings">
            <div class="readings__item">
              <span class="readings__label">{{ t('devices.board.readings.temperature') }}</span>
              <strong class="readings__value">{{ readings.temperature ?? '—' }}<small v-if="readings.temperature"> °C</small></strong>
            </div>
            <div class="readings__item">
              <span class="readings__label">{{ t('devices.board.readings.humidity') }}</span>
              <strong class="readings__value">{{ readings.humidity ?? '—' }}<small v-if="readings.humidity !== null"> %</small></strong>
            </div>
          </div>
          <BlindControl
            v-else-if="card.kind === 'blind'"
            :auto="blindIsAuto(card)"
            :angle="Number(blindValue(card, 'blindAngle', 0))"
            :current="stale ? null : (status?.node?.blind_deg ?? null)"
            :angle-attr="blindField(card, 'blindAngle')"
            :step-attr="blindField(card, 'blindStep')"
            :step="Number(blindValue(card, 'blindStep', 1))"
            @set-auto="send('blindAuto', $event)"
            @set-angle="sendInOrder(blindCommands($event, blindIsAuto(card)))"
            @set-step="send('blindStep', $event)"
          />
          <template v-else>
            <AttributeField
              v-for="field in card.fields"
              :key="field.name"
              :attr="field"
              :model-value="fieldValue(field)"
              :label="attrLabel(field.name)"
              :disabled="locked(card)"
              @commit="send(field.name, $event)"
            />
            <p v-if="card.hardwareNote" class="note">{{ t(`devices.board.cards.${card.id}.hardwareNote`) }}</p>
            <p v-if="card.id === 'motion' && locked(card)" class="note">{{ t('devices.board.cards.motion.offNote') }}</p>
            <p v-else-if="locked(card)" class="note">{{ t('devices.board.lockedHint') }}</p>
          </template>
        </template>
      </ControlCard>
    </div>
  </article>
</template>

<style scoped>
.device {
  display: flex;
  flex-direction: column;
  padding: 1.25rem;
  background: #f9fafb;
  border: 1px solid #e5e7eb;
  border-radius: 1rem;
  container-type: inline-size;   /* the grid adapts to the board's own width, not the window's */
}
.device__head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.5rem 1rem; margin-bottom: 1rem; }
.device__name { margin: 0; font-size: 1.1rem; font-weight: 800; color: #111827; }
.device__meta { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem 0.75rem; }
.device__seen { font-size: 0.8rem; color: #6b7280; }

/* Status LED indicator (read-only) */
.led { display: inline-flex; align-items: center; gap: 0.4rem; padding: 0.2rem 0.65rem; border-radius: 999px; font-size: 0.78rem; font-weight: 600; background: #f3f4f6; color: #4b5563; }
.led__dot { width: 0.55rem; height: 0.55rem; border-radius: 50%; background: #9ca3af; }
.led--on { background: #d1fae5; color: #047857; }
.led--on .led__dot { background: #10b981; }
.led--alert { background: #fee2e2; color: #b91c1c; }
.led--alert .led__dot { background: #dc2626; animation: blink 0.6s steps(2) infinite; }
@keyframes blink { 50% { opacity: 0.2; } }
@media (prefers-reduced-motion: reduce) { .led--alert .led__dot { animation: none; } }

/* At most three columns; the servo card takes two. Dense flow back-fills gaps, and rows
   stretch so neighbouring cards share one height. */
.device__grid { flex: 1; display: grid; grid-template-columns: 1fr; gap: 1rem; grid-auto-flow: dense; align-items: stretch; }
@container (min-width: 34rem) {
  .device__grid { grid-template-columns: repeat(2, 1fr); }
  .is-wide { grid-column: span 2; }
}
@container (min-width: 56rem) {
  .device__grid { grid-template-columns: repeat(3, 1fr); }
}

.state { font-size: 0.8rem; font-weight: 700; color: #4b5563; }
.note { margin: 0; font-size: 0.8rem; color: #6b7280; line-height: 1.4; }

.readings { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; }
.readings__item { display: flex; flex-direction: column; gap: 0.25rem; padding: 0.75rem; background: #f3f4f6; border-radius: 0.75rem; }
.readings__label { font-size: 0.78rem; font-weight: 600; color: #6b7280; }
.readings__value { font-size: 1.6rem; font-weight: 800; color: #111827; line-height: 1.1; }
.readings__value small { font-size: 0.85rem; font-weight: 600; color: #6b7280; }
</style>
