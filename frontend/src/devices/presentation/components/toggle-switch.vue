<script setup>
// Accessible on/off switch (role="switch"). Emits the NEW value; the parent decides
// whether to commit it, so a failed command can leave the switch where it was.
defineProps({
  modelValue: { type: Boolean, default: false },
  disabled: { type: Boolean, default: false },
  label: { type: String, required: true },
});
defineEmits(['update:modelValue']);
</script>

<template>
  <button
    type="button"
    role="switch"
    class="switch"
    :class="{ 'switch--on': modelValue }"
    :aria-checked="modelValue"
    :aria-label="label"
    :disabled="disabled"
    @click="$emit('update:modelValue', !modelValue)"
  >
    <span class="switch__knob" />
  </button>
</template>

<style scoped>
.switch {
  position: relative;
  flex: none;
  width: 3rem;
  height: 1.75rem;
  border: 0;
  border-radius: 999px;
  background: #d1d5db;
  cursor: pointer;
  transition: background-color 0.18s ease;
}
.switch--on { background: #047857; }
.switch:focus-visible { outline: 3px solid #a7f3d0; outline-offset: 2px; }
.switch:disabled { opacity: 0.5; cursor: not-allowed; }
.switch__knob {
  position: absolute;
  top: 0.1875rem;
  left: 0.1875rem;
  width: 1.375rem;
  height: 1.375rem;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.3);
  transition: transform 0.18s ease;
}
.switch--on .switch__knob { transform: translateX(1.25rem); }
@media (prefers-reduced-motion: reduce) {
  .switch, .switch__knob { transition: none; }
}
</style>
