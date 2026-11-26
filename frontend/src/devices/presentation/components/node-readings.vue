<script setup>
// Edge-node readings (ESP32 `node{}` block) returned by GET /devices/{id}/status as `node`.
defineProps({ node: { type: Object, default: null } });
</script>

<template>
  <template v-if="node">
    <div v-if="node.humidity_pct != null" class="status-field">
      <span class="status-label">{{ $t('devices.telemetry.humidity') }}</span>
      <span class="status-value">{{ `${Number(node.humidity_pct).toFixed(0)} %` }}</span>
    </div>
    <div v-if="node.presence != null" class="status-field">
      <span class="status-label">{{ $t('devices.telemetry.presence') }}</span>
      <span class="status-value">{{ node.presence ? $t('devices.telemetry.detected') : $t('devices.telemetry.notDetected') }}</span>
    </div>
    <div v-if="node.blind_deg != null" class="status-field">
      <span class="status-label">{{ $t('devices.telemetry.blind') }}</span>
      <span class="status-value">{{ `${Math.round(node.blind_deg)}° (${Math.round(node.blind_deg / 1.8)} %)` }}</span>
    </div>
    <div v-if="node.light != null" class="status-field">
      <span class="status-label">{{ $t('analytics.owner.live.tiles.light') }}</span>
      <span class="status-value">{{ node.light ? $t('analytics.owner.live.values.lit') : $t('analytics.owner.live.values.off') }}</span>
    </div>
    <div v-if="node.climate != null" class="status-field">
      <span class="status-label">{{ $t('analytics.owner.live.tiles.climate') }}</span>
      <span class="status-value">{{ node.climate ? $t('analytics.owner.live.values.running') : $t('analytics.owner.live.values.idle') }}</span>
    </div>
    <div v-if="node.alarm != null" class="status-field">
      <span class="status-label">{{ $t('devices.telemetry.alarm') }}</span>
      <span class="status-value" :style="node.alarm ? 'color:#DC2626' : null">{{ node.alarm ? $t('devices.telemetry.alarmOn') : $t('devices.telemetry.alarmOff') }}</span>
    </div>
  </template>
</template>

<style scoped>
/* Same look as the dashboard status card rows (their styles are scoped there). */
.status-field { display: flex; justify-content: space-between; align-items: center; padding: 0.5rem 0; border-bottom: 1px solid #F3F4F6; }
.status-label { font-size: 0.8125rem; font-weight: 600; color: #6B7280; }
.status-value { font-size: 0.875rem; font-weight: 600; color: #111827; }
</style>
