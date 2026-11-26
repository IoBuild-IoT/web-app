import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

// Convergent Testing G0: DEVICES.HARDWARE_LINK - poll only while the hardware is connected,
// never retry on its own, reconnect on request.
const status = vi.fn();
vi.mock('../../src/devices/infrastructure/device-api.js', () => ({
  DeviceApi: class { getDeviceStatus(id) { return status(id); } },
}));
const { useHardwareLinkStore } = await import('../../src/devices/application/hardware-link.store.js');

const up = { connected: true };
const down = { connected: false };

describe('hardware link', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal('document', { hidden: false });
    setActivePinia(createPinia());
    status.mockReset();
  });
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it('polls every 3 s while connected and stops completely once the hardware goes silent', async () => {
    status.mockResolvedValue(up);
    const link = useHardwareLinkStore();
    await link.start([18]);
    expect(link.state).toBe('connected');
    await vi.advanceTimersByTimeAsync(3000);
    expect(status).toHaveBeenCalledTimes(2);

    status.mockResolvedValue(down);
    await vi.advanceTimersByTimeAsync(3000);
    expect(link.state).toBe('disconnected');
    await vi.advanceTimersByTimeAsync(60000);
    expect(status).toHaveBeenCalledTimes(3);              // no reconnect loop
  });

  it('does not start polling when the hardware is already disconnected', async () => {
    status.mockResolvedValue(down);
    const link = useHardwareLinkStore();
    await link.start([18]);
    await vi.advanceTimersByTimeAsync(60000);
    expect(link.state).toBe('disconnected');
    expect(status).toHaveBeenCalledTimes(1);
  });

  it('connect() makes a bounded number of checks and reports failure', async () => {
    status.mockResolvedValue(down);
    const link = useHardwareLinkStore();
    await link.start([18]);
    const request = link.connect();
    await vi.advanceTimersByTimeAsync(60000);
    await request;
    expect(status).toHaveBeenCalledTimes(1 + 4);
    expect(link.failed).toBe(true);
    expect(link.state).toBe('disconnected');
  });

  it('connect() resumes polling when the hardware answers', async () => {
    status.mockResolvedValueOnce(down).mockResolvedValue(up);
    const link = useHardwareLinkStore();
    await link.start([18]);
    await link.connect();
    expect(link.state).toBe('connected');
    expect(link.failed).toBe(false);
  });

  it('tolerates one network error but not two, and marks old readings as disconnected', async () => {
    status.mockResolvedValue(up);
    const link = useHardwareLinkStore();
    await link.start([18]);
    status.mockRejectedValue(new Error('network'));
    await vi.advanceTimersByTimeAsync(3000);
    expect(link.state).toBe('connected');
    await vi.advanceTimersByTimeAsync(3000);
    expect(link.state).toBe('disconnected');
    expect(link.readings[18].connected).toBe(false);
  });
});
