<script setup>
// One controllable attribute from the device catalog, rendered by its kind:
//   number  -> slider with a live value; the command is sent when the slider is RELEASED
//   enum    -> segmented buttons (one tap = one command)
//   boolean -> switch
// No "Send" button: every control commits by itself through the `commit` event.
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import ToggleSwitch from './toggle-switch.vue';

const props = defineProps({
  attr: { type: Object, required: true },        // catalog entry { name, type, min, max, step, unit, enumMembers }
  modelValue: { type: [Number, String, Boolean], default: null },
  label: { type: String, required: true },
  disabled: { type: Boolean, default: false },
});
const emit = defineEmits(['commit']);
const { t, te } = useI18n();

const kind = computed(() => String(props.attr.type ?? '').toLowerCase());
const UNITS = { C: '°C', deg: '°' };
const unit = computed(() => (props.attr.unit ? UNITS[props.attr.unit] ?? props.attr.unit : ''));

// The slider shows the draft while it is being dragged; it only becomes a command on release.
const draft = ref(Number(props.modelValue));
const dragging = ref(false);
watch(() => props.modelValue, (value) => { if (!dragging.value) draft.value = Number(value); });

function release(event) {
  dragging.value = false;
  const value = Number(event.target.value);
  draft.value = value;
  if (value !== Number(props.modelValue)) emit('commit', value);
}

const options = computed(() =>
  (props.attr.enumMembers ?? []).map((value) => ({
    value,
    text: te(`devices.board.modes.${value}`) ? t(`devices.board.modes.${value}`) : value,
  })),
);
</script>

<template>
  <div class="field" :class="{ 'field--disabled': disabled }">
    <template v-if="kind === 'number'">
      <div class="field__row">
        <label class="field__label" :for="`f-${attr.name}`">{{ label }}</label>
        <output class="field__value">{{ draft }}<span v-if="unit" class="field__unit"> {{ unit }}</span></output>
      </div>
      <input
        :id="`f-${attr.name}`"
        type="range"
        class="field__slider"
        :min="attr.min"
        :max="attr.max"
        :step="attr.step ?? 1"
        :value="draft"
        :disabled="disabled"
        :aria-label="label"
        :title="t('devices.board.applyHint')"
        @pointerdown="dragging = true"
        @input="draft = Number($event.target.value)"
        @change="release"
        @blur="dragging = false"
      />
      <div class="field__range"><span>{{ attr.min }}{{ unit }}</span><span>{{ attr.max }}{{ unit }}</span></div>
    </template>

    <template v-else-if="kind === 'enum'">
      <span :id="`f-${attr.name}`" class="field__label">{{ label }}</span>
      <div class="segments" role="radiogroup" :aria-labelledby="`f-${attr.name}`">
        <button
          v-for="option in options"
          :key="option.value"
          type="button"
          role="radio"
          class="segments__item"
          :class="{ 'segments__item--on': option.value === modelValue }"
          :aria-checked="option.value === modelValue"
          :disabled="disabled"
          @click="option.value !== modelValue && emit('commit', option.value)"
        >{{ option.text }}</button>
      </div>
    </template>

    <template v-else-if="kind === 'boolean'">
      <div class="field__row">
        <span class="field__label">{{ label }}</span>
        <ToggleSwitch :model-value="modelValue === true" :label="label" :disabled="disabled" @update:model-value="emit('commit', $event)" />
      </div>
    </template>
  </div>
</template>

<style scoped>
.field { display: flex; flex-direction: column; gap: 0.375rem; }
.field--disabled { cursor: not-allowed; }
.field__row { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; }
.field__label { font-size: 0.85rem; font-weight: 600; color: #374151; line-height: 1.3; }
.field__value { font-size: 1.05rem; font-weight: 700; color: #111827; white-space: nowrap; }
.field__unit { font-size: 0.8rem; font-weight: 600; color: #6b7280; }

.field__slider { width: 100%; height: 1.5rem; margin: 0; accent-color: #047857; cursor: pointer; }
.field__slider:disabled { cursor: not-allowed; }
.field__range { display: flex; justify-content: space-between; font-size: 0.72rem; color: #9ca3af; }

.segments { display: flex; gap: 0.25rem; padding: 0.25rem; background: #f3f4f6; border-radius: 0.75rem; }
.segments__item {
  flex: 1;
  min-height: 2.25rem;
  padding: 0.375rem 0.5rem;
  border: 0;
  border-radius: 0.5rem;
  background: transparent;
  color: #4b5563;
  font: inherit;
  font-size: 0.85rem;
  font-weight: 600;
  cursor: pointer;
  transition: background-color 0.15s ease, color 0.15s ease;
}
.segments__item:hover:not(:disabled):not(.segments__item--on) { background: #e5e7eb; }
.segments__item--on { background: #047857; color: #fff; box-shadow: 0 1px 2px rgba(0, 0, 0, 0.2); }
.segments__item:focus-visible { outline: 3px solid #a7f3d0; outline-offset: 1px; }
.segments__item:disabled { cursor: not-allowed; }
</style>
