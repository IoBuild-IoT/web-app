<script setup>
// "Live prototype status": what the owner's real devices are doing right now.
// Pure view over a summary from live-summary.js; the parent keeps it fresh.
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { blindPercent, formatSeen } from '../../../devices/application/control-layout.js';

const props = defineProps({
  summary: { type: Object, required: true },
  seconds: { type: Number, default: 5 },
  now: { type: Number, default: () => Date.now() },
});
const { t } = useI18n();

const dash = '—';
const yesNo = (value, yes, no) => (value === null || value === undefined ? dash : t(`analytics.owner.live.values.${value ? yes : no}`));

const tiles = computed(() => {
  const s = props.summary;
  const ready = s.ready;
  const tone = (value, onTone) => (!ready || value === null || value === undefined ? 'muted' : onTone);
  const onlineTone = !ready ? 'muted' : s.online === s.total ? 'ok' : s.online === 0 ? 'alert' : 'warn';
  return [
    { id: 'online', icon: 'pi-wifi', tone: onlineTone, value: ready ? `${s.online} / ${s.total}` : dash },
    { id: 'temperature', icon: 'pi-chart-line', tone: tone(s.temperature, 'info'), value: s.temperature == null ? dash : `${s.temperature.toFixed(1)} °C` },
    { id: 'humidity', icon: 'pi-cloud', tone: tone(s.humidity, 'info'), value: s.humidity == null ? dash : `${Math.round(s.humidity)} %` },
    { id: 'presence', icon: 'pi-eye', tone: tone(s.presence, s.presence ? 'violet' : 'idle'), value: yesNo(s.presence, 'detected', 'noMotion') },
    { id: 'light', icon: 'pi-lightbulb', tone: tone(s.light, s.light ? 'amber' : 'idle'), value: yesNo(s.light, 'lit', 'off') },
    { id: 'climate', icon: 'pi-sync', tone: tone(s.climate, s.climate ? 'info' : 'idle'), value: yesNo(s.climate, 'running', 'idle') },
    { id: 'blind', icon: 'pi-arrows-v', tone: tone(s.blindDeg, 'teal'), value: s.blindDeg == null ? dash : `${Math.round(s.blindDeg)}° · ${blindPercent(s.blindDeg)} %` },
    { id: 'alarm', icon: 'pi-bell', tone: tone(s.alarm, s.alarm ? 'alert' : 'ok'), value: yesNo(s.alarm, 'alarm', 'normal') },
  ];
});

const seen = computed(() => {
  const value = formatSeen(props.summary.lastSeen, props.now);
  return value ? t(`devices.board.seen.${value.unit}`, { n: value.amount }) : null;
});
const allSilent = computed(() => props.summary.ready && props.summary.online === 0);
</script>

<template>
  <section class="live" aria-live="polite" data-testid="live-panel">
    <header class="live__head">
      <div>
        <h3 class="live__title">{{ t('analytics.owner.live.title') }}</h3>
        <p class="live__sub">{{ t('analytics.owner.live.subtitle', { seconds }) }}</p>
      </div>
      <p class="live__badge" :class="{ 'live__badge--off': allSilent }">
        <span class="live__dot" aria-hidden="true" />{{ t(allSilent ? 'analytics.owner.live.pausedBadge' : 'analytics.owner.live.liveBadge') }}
        <span v-if="seen" class="live__seen">· {{ t('devices.board.lastReading') }}: {{ seen }}</span>
      </p>
    </header>

    <div class="live__grid">
      <div v-for="tile in tiles" :key="tile.id" class="tile" :class="`tile--${tile.tone}`" :data-tile="tile.id">
        <div class="tile__icon" aria-hidden="true"><i class="pi" :class="tile.icon" /></div>
        <div class="tile__body">
          <span class="tile__label">{{ t(`analytics.owner.live.tiles.${tile.id}`) }}</span>
          <strong class="tile__value">{{ tile.value }}</strong>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.live { padding: 1.25rem; margin-bottom: 1.5rem; background: #fff; border: 1px solid #e5e7eb; border-radius: 1rem; box-shadow: 0 1px 3px rgba(0, 0, 0, 0.06); }
.live__head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.5rem 1rem; margin-bottom: 1rem; }
.live__title { margin: 0; font-size: 1.15rem; font-weight: 800; color: #111827; }
.live__sub { margin: 0.125rem 0 0; font-size: 0.82rem; color: #6b7280; }
.live__badge { display: inline-flex; align-items: center; gap: 0.5rem; margin: 0; padding: 0.25rem 0.75rem; border-radius: 999px; background: #d1fae5; color: #047857; font-size: 0.8rem; font-weight: 700; }
.live__badge--off { background: #fee2e2; color: #b91c1c; }
.live__seen { font-weight: 600; opacity: 0.85; }
.live__dot { width: 0.55rem; height: 0.55rem; border-radius: 50%; background: currentColor; animation: pulse 1.6s ease-in-out infinite; }
@keyframes pulse { 50% { opacity: 0.25; } }
@media (prefers-reduced-motion: reduce) { .live__dot { animation: none; } }

.live__grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(11.5rem, 1fr)); gap: 0.75rem; }
.tile { --fg: #6b7280; --bg: #f3f4f6; display: flex; align-items: center; gap: 0.75rem; padding: 0.75rem 0.875rem; background: var(--bg); border-radius: 0.875rem; }
.tile__icon { flex: none; display: grid; place-items: center; width: 2.25rem; height: 2.25rem; border-radius: 0.625rem; background: #fff; color: var(--fg); font-size: 1.05rem; }
.tile__body { display: flex; flex-direction: column; min-width: 0; }
.tile__label { font-size: 0.75rem; font-weight: 600; color: #6b7280; }
.tile__value { font-size: 1.1rem; font-weight: 800; color: #111827; line-height: 1.25; }
.tile--ok { --fg: #047857; --bg: #ecfdf5; }
.tile--info { --fg: #1d4ed8; --bg: #eff6ff; }
.tile--violet { --fg: #6d28d9; --bg: #f5f3ff; }
.tile--amber { --fg: #b45309; --bg: #fffbeb; }
.tile--teal { --fg: #0f766e; --bg: #f0fdfa; }
.tile--warn { --fg: #b45309; --bg: #fffbeb; }
.tile--alert { --fg: #b91c1c; --bg: #fef2f2; }
.tile--alert .tile__value { color: #b91c1c; }
.tile--idle .tile__value, .tile--muted .tile__value { color: #4b5563; }
</style>
