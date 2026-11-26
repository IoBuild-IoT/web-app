import { onBeforeUnmount, onMounted } from 'vue';

// Runs `task` every `intervalMs` while the component is mounted and the tab is visible.
// A tick is skipped while the previous one is still running, so a slow API never piles up
// requests; a failing task is simply retried on the next tick.
export function useAutoRefresh(task, intervalMs) {
  let timer = null;
  let running = false;

  async function tick() {
    if (running || document.hidden) return;
    running = true;
    try {
      await task();
    } catch {
      // next tick retries
    } finally {
      running = false;
    }
  }

  onMounted(() => { timer = setInterval(tick, intervalMs); });
  onBeforeUnmount(() => clearInterval(timer));
  return { tick };
}
