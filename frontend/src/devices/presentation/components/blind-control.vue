<script setup>
// Servo / blind control.
//   Automatic: the node opens/closes the blind by temperature (sensitivity = blindStep).
//   Manual   : the owner picks the angle (Close / Half / Open shortcuts or the slider).
// Moving to an angle from automatic mode first switches to manual (see blindCommands).
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import AttributeField from './attribute-field.vue';
import { BLIND_CLOSED, BLIND_OPEN, blindPercent } from '../../application/control-layout.js';

const props = defineProps({
  auto: { type: Boolean, default: true },
  angle: { type: Number, default: 0 },            // commanded target (manual mode)
  current: { type: Number, default: null },       // real servo position from telemetry
  angleAttr: { type: Object, default: null },
  stepAttr: { type: Object, default: null },
  step: { type: Number, default: 1 },
  disabled: { type: Boolean, default: false },
});
const emit = defineEmits(['set-auto', 'set-angle', 'set-step']);
const { t } = useI18n();

const shown = computed(() => (props.current ?? (props.auto ? BLIND_CLOSED : props.angle)));
const percent = computed(() => blindPercent(shown.value));
const targetPercent = computed(() => blindPercent(props.angle));
const moving = computed(() => !props.auto && props.current != null && Math.abs(props.current - props.angle) > 2);

const shortcuts = computed(() => [
  { key: 'close', angle: BLIND_CLOSED },
  { key: 'half', angle: (BLIND_OPEN - BLIND_CLOSED) / 2 },
  { key: 'open', angle: BLIND_OPEN },
]);
const modes = computed(() => [
  { value: true, text: t('devices.board.blind.auto') },
  { value: false, text: t('devices.board.blind.manual') },
]);
</script>

<template>
  <div class="blind">
    <div class="blind__grid">
      <!-- Left: mode, where the blind really is, and (automatic) what the rule does -->
      <div class="blind__col">
        <div class="segments" role="radiogroup" :aria-label="t('devices.board.attributes.blindAuto')">
          <button
            v-for="mode in modes"
            :key="String(mode.value)"
            type="button"
            role="radio"
            class="segments__item"
            :class="{ 'segments__item--on': mode.value === auto }"
            :aria-checked="mode.value === auto"
            :disabled="disabled"
            @click="mode.value !== auto && emit('set-auto', mode.value)"
          >{{ mode.text }}</button>
        </div>

        <!-- Where the blind really is (telemetry), with the manual target as a tick -->
        <div class="gauge" role="img" :aria-label="`${percent} %`">
          <div class="gauge__bar">
            <div class="gauge__fill" :style="{ width: `${percent}%` }" />
            <div v-if="!auto" class="gauge__target" :style="{ left: `${targetPercent}%` }" />
          </div>
          <div class="gauge__ends"><span>{{ t('devices.board.blind.closedEnd') }}</span><span>{{ t('devices.board.blind.openEnd') }}</span></div>
        </div>

        <p v-if="auto" class="blind__hint">{{ t('devices.board.blind.autoHint', { step }) }}</p>
      </div>

      <!-- Right: the settings of the active mode -->
      <div class="blind__col">
        <template v-if="auto">
          <AttributeField
            v-if="stepAttr"
            :attr="stepAttr"
            :model-value="step"
            :label="t('devices.board.attributes.blindStep')"
            :disabled="disabled"
            @commit="emit('set-step', $event)"
          />
        </template>

        <template v-else>
          <p class="blind__hint">{{ t('devices.board.blind.manualHint') }}</p>
          <div class="shortcuts">
            <button
              v-for="shortcut in shortcuts"
              :key="shortcut.key"
              type="button"
              class="shortcuts__btn"
              :class="{ 'shortcuts__btn--on': angle === shortcut.angle }"
              :disabled="disabled"
              @click="emit('set-angle', shortcut.angle)"
            >
              {{ t(`devices.board.blind.${shortcut.key}`) }}
              <small>{{ shortcut.angle }}°</small>
            </button>
          </div>
          <AttributeField
            v-if="angleAttr"
            :attr="angleAttr"
            :model-value="angle"
            :label="t('devices.board.blind.target')"
            :disabled="disabled"
            @commit="emit('set-angle', $event)"
          />
          <p v-if="moving" class="blind__moving" role="status"><i class="pi pi-sync pi-spin" aria-hidden="true" /> {{ current }}° → {{ angle }}°</p>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
.blind { container-type: inline-size; }
.blind__grid { display: grid; grid-template-columns: 1fr; gap: 0.875rem; }
.blind__col { display: flex; flex-direction: column; gap: 0.875rem; min-width: 0; }
/* Wide card: two columns side by side instead of one tall stack. */
@container (min-width: 24rem) {
  .blind__grid { grid-template-columns: 1fr 1fr; gap: 1.5rem; align-items: start; }
}
.blind__hint { margin: 0; font-size: 0.82rem; color: #6b7280; line-height: 1.4; }
.blind__moving { margin: 0; font-size: 0.8rem; font-weight: 600; color: #0f766e; }

.segments { display: flex; gap: 0.25rem; padding: 0.25rem; background: #f3f4f6; border-radius: 0.75rem; }
.segments__item {
  flex: 1; min-height: 2.25rem; padding: 0.375rem 0.5rem; border: 0; border-radius: 0.5rem;
  background: transparent; color: #4b5563; font: inherit; font-size: 0.85rem; font-weight: 600; cursor: pointer;
  transition: background-color 0.15s ease, color 0.15s ease;
}
.segments__item:hover:not(:disabled):not(.segments__item--on) { background: #e5e7eb; }
.segments__item--on { background: #0f766e; color: #fff; box-shadow: 0 1px 2px rgba(0, 0, 0, 0.2); }
.segments__item:focus-visible { outline: 3px solid #99f6e4; outline-offset: 1px; }
.segments__item:disabled { cursor: not-allowed; }

.gauge__bar { position: relative; height: 0.875rem; border-radius: 999px; background: #e5e7eb; overflow: hidden; }
.gauge__fill { height: 100%; border-radius: 999px; background: #14b8a6; transition: width 0.4s ease; }
.gauge__target { position: absolute; top: 0; bottom: 0; width: 3px; margin-left: -1px; background: #0f172a; }
.gauge__ends { display: flex; justify-content: space-between; margin-top: 0.25rem; font-size: 0.72rem; color: #9ca3af; }
@media (prefers-reduced-motion: reduce) { .gauge__fill { transition: none; } }

.shortcuts { display: grid; grid-template-columns: repeat(3, 1fr); gap: 0.5rem; }
.shortcuts__btn {
  display: flex; flex-direction: column; align-items: center; gap: 0.125rem; min-height: 2.75rem; padding: 0.375rem;
  border: 1px solid #d1d5db; border-radius: 0.625rem; background: #fff; color: #374151;
  font: inherit; font-size: 0.85rem; font-weight: 700; cursor: pointer;
  transition: background-color 0.15s ease, border-color 0.15s ease;
}
.shortcuts__btn small { font-size: 0.72rem; font-weight: 600; color: #6b7280; }
.shortcuts__btn:hover:not(:disabled):not(.shortcuts__btn--on) { background: #f0fdfa; border-color: #5eead4; }
.shortcuts__btn--on { background: #ccfbf1; border-color: #0f766e; color: #0f766e; }
.shortcuts__btn--on small { color: #0f766e; }
.shortcuts__btn:focus-visible { outline: 3px solid #99f6e4; outline-offset: 1px; }
.shortcuts__btn:disabled { opacity: 0.5; cursor: not-allowed; }
</style>
