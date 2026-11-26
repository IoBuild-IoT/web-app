<script setup>
// Shell for one physical component of the prototype (a LED, the PIR sensor, the servo...).
// Icon + name + what it is, a main switch (slot "toggle"), a "right now" chip that shows the
// REAL hardware state, and the settings body (default slot).
defineProps({
  icon: { type: String, required: true },
  tone: { type: String, default: 'gray' },          // amber | blue | violet | teal | red | green | gray
  title: { type: String, required: true },
  hint: { type: String, default: '' },
  active: { type: Boolean, default: false },        // colors the icon bubble
  muted: { type: Boolean, default: false },         // dims the body when the component is off
  live: { type: Object, default: null },            // { text, tone: on | idle | alert }
});
</script>

<template>
  <section class="card" :class="[`tone-${tone}`, { 'card--active': active, 'card--muted': muted }]">
    <header class="card__head">
      <div class="card__bubble" aria-hidden="true"><i class="pi" :class="icon" /></div>
      <div class="card__titles">
        <h4 class="card__title">{{ title }}</h4>
        <p v-if="hint" class="card__hint">{{ hint }}</p>
      </div>
      <div v-if="$slots.toggle" class="card__toggle"><slot name="toggle" /></div>
    </header>

    <p v-if="live" class="card__live" :class="`card__live--${live.tone}`" role="status">
      <span class="card__dot" aria-hidden="true" />{{ live.text }}
    </p>

    <div v-if="$slots.default" class="card__body" :class="{ 'card__body--muted': muted }">
      <slot />
    </div>
  </section>
</template>

<style scoped>
.card {
  --tone: #6b7280;
  --tone-bg: #f3f4f6;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1rem;
  background: #fff;
  border: 1px solid #e5e7eb;
  border-radius: 0.875rem;
  box-shadow: 0 1px 2px rgba(17, 24, 39, 0.05);
}
.tone-amber  { --tone: #b45309; --tone-bg: #fef3c7; }
.tone-blue   { --tone: #1d4ed8; --tone-bg: #dbeafe; }
.tone-violet { --tone: #6d28d9; --tone-bg: #ede9fe; }
.tone-teal   { --tone: #0f766e; --tone-bg: #ccfbf1; }
.tone-red    { --tone: #b91c1c; --tone-bg: #fee2e2; }
.tone-green  { --tone: #047857; --tone-bg: #d1fae5; }

.card__head { display: flex; align-items: center; gap: 0.75rem; }
.card__bubble {
  flex: none;
  display: grid;
  place-items: center;
  width: 2.5rem;
  height: 2.5rem;
  border-radius: 0.75rem;
  background: #f3f4f6;
  color: #9ca3af;
  font-size: 1.15rem;
  transition: background-color 0.18s ease, color 0.18s ease;
}
.card--active .card__bubble { background: var(--tone-bg); color: var(--tone); }
.card__titles { flex: 1; min-width: 0; }
.card__title { margin: 0; font-size: 1rem; font-weight: 700; color: #111827; line-height: 1.25; }
.card__hint { margin: 0.125rem 0 0; font-size: 0.78rem; color: #6b7280; line-height: 1.3; }
.card__toggle { flex: none; display: flex; align-items: center; gap: 0.5rem; }

.card__live {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  align-self: flex-start;
  margin: 0;
  padding: 0.25rem 0.75rem;
  border-radius: 999px;
  font-size: 0.8rem;
  font-weight: 600;
  background: #f3f4f6;
  color: #4b5563;
}
.card__dot { width: 0.5rem; height: 0.5rem; border-radius: 50%; background: #9ca3af; }
.card__live--on { background: var(--tone-bg); color: var(--tone); }
.card__live--on .card__dot { background: var(--tone); }
.card__live--alert { background: #fee2e2; color: #b91c1c; }
.card__live--alert .card__dot { background: #dc2626; animation: pulse 1s ease-in-out infinite; }
@keyframes pulse { 50% { opacity: 0.25; } }
@media (prefers-reduced-motion: reduce) { .card__live--alert .card__dot { animation: none; } }

.card__body { display: flex; flex-direction: column; gap: 0.875rem; }
.card__body--muted { opacity: 0.55; }
</style>
