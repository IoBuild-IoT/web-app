<script setup>
// Visual proof of the hardware connection, plus the button to ask for it again.
import { computed, onBeforeUnmount, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useHardwareLinkStore } from '../../application/hardware-link.store.js';
import { formatSeen } from '../../application/control-layout.js';

const props = defineProps({ deviceIds: { type: Array, required: true } });
const { t } = useI18n();
const link = useHardwareLinkStore();

// Restart only when the set of devices changes, not on every dashboard refresh.
watch(() => props.deviceIds.join(','), () => link.start(props.deviceIds), { immediate: true });
onBeforeUnmount(() => link.stop());

const view = computed(() => (link.state === 'idle' ? 'checking' : link.state));
const lastSignal = computed(() => {
  const times = props.deviceIds.map((id) => Date.parse(link.readings[id]?.lastSeen)).filter(Number.isFinite);
  const seen = times.length ? formatSeen(Math.max(...times), link.checkedAt) : null;
  return seen ? `${t('devices.link.lastSignal')}: ${t(`devices.board.seen.${seen.unit}`, { n: seen.amount })}` : null;
});
</script>

<template>
  <div v-if="deviceIds.length" class="link" :class="`link--${view}`" role="status" data-testid="hardware-link">
    <i v-if="view === 'checking'" class="pi pi-spinner pi-spin" aria-hidden="true" />
    <span v-else class="link__dot" aria-hidden="true" />
    <div class="link__text">
      <strong>{{ t(`devices.link.${view}`) }}</strong>
      <span v-if="view === 'disconnected'">{{ t(link.failed ? 'devices.link.failed' : 'devices.link.paused') }}</span>
      <span v-if="lastSignal">{{ lastSignal }}</span>
    </div>
    <pv-button v-if="view === 'disconnected'" :label="t('devices.link.connect')" icon="pi pi-wifi" size="small" @click="link.connect()" />
  </div>
</template>

<style scoped>
.link { --fg: #92400e; display: flex; flex-wrap: wrap; align-items: center; gap: 0.75rem; margin-bottom: 1.25rem; padding: 0.75rem 1rem; border: 1px solid #fde68a; border-radius: 0.875rem; background: #fffbeb; color: var(--fg); }
.link--connected { --fg: #047857; border-color: #a7f3d0; background: #ecfdf5; }
.link--disconnected { --fg: #b91c1c; border-color: #fecaca; background: #fef2f2; }
.link__dot { width: 0.75rem; height: 0.75rem; border-radius: 50%; background: currentColor; }
.link--connected .link__dot { box-shadow: 0 0 0 4px rgba(16, 185, 129, 0.25); }
.link__text { flex: 1; display: flex; flex-wrap: wrap; align-items: baseline; gap: 0.25rem 0.75rem; font-size: 0.85rem; }
.link__text strong { font-size: 0.95rem; }
</style>
