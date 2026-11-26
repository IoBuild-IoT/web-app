<script setup>
import { computed, onMounted } from 'vue';
import { useI18n } from 'vue-i18n';
import ControlBoard from './control-board.vue';
import HardwareLinkBanner from './hardware-link-banner.vue';
import { useAnalyticsStore } from '../../../analytics/application/analytics.store.js';
import { useDeviceStore } from '../../application/device.store.js';
import { useIamStore } from '../../../iam/application/iam.store.js';

const { t } = useI18n();

const analyticsStore = useAnalyticsStore();
const deviceStore = useDeviceStore();
const iamStore = useIamStore();

// Role guard: control affordances are Owner-only.
const isOwner = computed(() => (iamStore.currentUser?.role ?? '').toLowerCase() === 'owner');
const userId = computed(() => iamStore.currentUser?.id ?? null);

// Owner-scoped device list — sourced exclusively from the owner analytics payload
// (deviceHealthStatus), never from the all-tenant device store.
const devices = computed(() => analyticsStore.ownerDashboard?.deviceHealthStatus ?? []);

/**
 * Map deviceType code → controllable attributes array, sourced from the
 * global catalog (deviceStore.deviceTypes, loaded from GET /types).
 */
const controllableAttributesByType = computed(() => {
  const map = {};
  (deviceStore.deviceTypes ?? []).forEach((dt) => {
    map[dt.code] = dt.controllableAttributes ?? [];
  });
  return map;
});

function getControllableAttrs(deviceType) {
  return controllableAttributesByType.value[deviceType] ?? [];
}

onMounted(() => {
  deviceStore.loadDeviceTypes();
  // Ensure the owner-scoped payload is available even when the analytics dashboard
  // view was never visited this session.
  if (isOwner.value && userId.value != null && !analyticsStore.ownerDashboard) {
    analyticsStore.fetchOwnerDashboard(userId.value);
  }
});
</script>

<template>
  <div class="section">
    <h3 class="section-title">{{ t('devices.board.sectionTitle') }}</h3>
    <p class="section-hint">{{ t('devices.board.sectionHint') }}</p>
    <HardwareLinkBanner :device-ids="devices.map((device) => device.deviceId ?? device.id)" />
    <div v-if="devices.length" class="unit-devices-board">
      <ControlBoard
        v-for="device in devices"
        :key="device.deviceId ?? device.id"
        :device="device"
        :attributes="getControllableAttrs(device.type)"
      />
    </div>
    <p v-else class="no-data">{{ t('devices.board.empty') }}</p>
  </div>
</template>

<style scoped>
.section {
  background: white;
  border-radius: 0.75rem;
  padding: 1.5rem;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  margin-bottom: 1.5rem;
}

.section-title {
  font-size: 1.25rem;
  font-weight: 700;
  color: #111827;
  margin: 0 0 0.25rem;
}

.section-hint {
  margin: 0 0 1.25rem;
  font-size: 0.875rem;
  color: #6b7280;
}

/* Devices with few components (light, climate) share a row; the edge node takes the full width.
   Equal row heights keep the boards aligned. */
.section { container-type: inline-size; }
.unit-devices-board {
  display: grid;
  grid-template-columns: 1fr;
  gap: 1.25rem;
  align-items: stretch;
}
@container (min-width: 44rem) {
  .unit-devices-board { grid-template-columns: repeat(2, 1fr); }
  .unit-devices-board > .device--full { grid-column: 1 / -1; }
}

.no-data {
  text-align: center;
  color: #9CA3AF;
  padding: 2rem;
  font-size: 0.875rem;
}
</style>
