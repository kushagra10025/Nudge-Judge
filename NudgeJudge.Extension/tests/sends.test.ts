import { describe, expect, it } from 'vitest';
import { addRecord, MAX_RECORDS, outcomeOf, recordFor } from '../utils/sends';

describe('outcomeOf', () => {
  it('reads how the link was handled from the reply', () => {
    expect(outcomeOf({ v: 1, ok: true, action: 'stay' })).toBe('stay');
    expect(outcomeOf({ v: 1, ok: true, action: 'sent', via: 'running' })).toBe('running');
    expect(outcomeOf({ v: 1, ok: true, action: 'sent', via: 'started' })).toBe('started');
    expect(outcomeOf({ v: 1, ok: true, action: 'sent', via: 'startedTied' })).toBe('startedTied');
    expect(outcomeOf({ v: 1, ok: false, error: 'x' })).toBe('error');
  });

  it('treats a reply from an older NudgeJudge without "via" as running', () => {
    expect(outcomeOf({ v: 1, ok: true, action: 'sent' })).toBe('running');
  });
});

describe('records', () => {
  it('keep only the host, and the error when there is one', () => {
    const ok = recordFor('https://us02web.zoom.us/j/123?pwd=secret', 'click', { v: 1, ok: true, action: 'stay' }, 5);
    expect(ok).toEqual({ at: 5, host: 'us02web.zoom.us', trigger: 'click', outcome: 'stay' });
    const failed = recordFor('https://a.example/', 'menu', { v: 1, ok: false, error: 'nope' }, 6);
    expect(failed.error).toBe('nope');
  });

  it('are newest first and capped', () => {
    let list = [] as ReturnType<typeof addRecord>;
    for (let i = 0; i < MAX_RECORDS + 5; i++) list = addRecord(list, recordFor('https://a.example/', 'page', { v: 1, ok: true, action: 'stay' }, i));
    expect(list).toHaveLength(MAX_RECORDS);
    expect(list[0]!.at).toBe(MAX_RECORDS + 4);
  });
});
