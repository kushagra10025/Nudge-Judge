import { describe, expect, it } from 'vitest';
import { serialQueue } from '../utils/serial';

describe('serialQueue', () => {
  it('runs read-change-write jobs one at a time, so none is lost', async () => {
    const queue = serialQueue();
    let stored: number[] = [];
    const tick = () => new Promise((r) => setTimeout(r, 1));
    const append = (n: number) =>
      queue(async () => {
        const read = stored;
        await tick();
        stored = [...read, n];
      });
    await Promise.all([append(1), append(2), append(3)]);
    expect(stored).toEqual([1, 2, 3]);
  });

  it('keeps going after a job fails', async () => {
    const queue = serialQueue();
    await expect(queue(() => Promise.reject(new Error('x')))).rejects.toThrow('x');
    await expect(queue(async () => 'next')).resolves.toBe('next');
  });
});
