import type { NativeReply } from './protocol';

/** What made the extension send a link. */
export type Trigger = 'menu' | 'page' | 'click' | 'auto';

/** What happened to it, from the host's reply. */
export type Outcome = 'stay' | 'running' | 'started' | 'startedTied' | 'error';

/** One line of the options page's "Recent links". Only the host is kept, not the full address. */
export interface SendRecord {
  at: number;
  host: string;
  trigger: Trigger;
  outcome: Outcome;
  error?: string;
}

export const MAX_RECORDS = 20;

export function outcomeOf(reply: NativeReply): Outcome {
  if (!reply.ok) return 'error';
  if (reply.action === 'stay') return 'stay';
  // An older NudgeJudge doesn't say how; it was running or started, either way it has the link.
  return reply.via ?? 'running';
}

export function recordFor(url: string, trigger: Trigger, reply: NativeReply, at = Date.now()): SendRecord {
  let host = url;
  try {
    host = new URL(url).hostname;
  } catch {
    // keep what we were given
  }
  const outcome = outcomeOf(reply);
  return { at, host, trigger, outcome, ...(outcome === 'error' ? { error: reply.error } : {}) };
}

/** Newest first, capped. */
export function addRecord(records: readonly SendRecord[], record: SendRecord): SendRecord[] {
  return [record, ...records].slice(0, MAX_RECORDS);
}

export const TRIGGER_LABELS: Record<Trigger, string> = {
  menu: 'Right-click',
  page: 'Toolbar / shortcut',
  click: 'Modifier-click',
  auto: 'Automatic',
};

/** Label, explanation and lamp for each outcome. */
export const OUTCOMES: Record<Outcome, { label: string; explain: string; lamp: 'on' | 'ink' | 'ring' | 'off' | 'warn' }> = {
  stay: {
    label: 'Stayed here',
    explain: 'Your rules pick this browser, so the link opened here as usual.',
    lamp: 'ring',
  },
  running: {
    label: 'Sent to running NudgeJudge',
    explain: 'NudgeJudge was already running (window or tray) and routed the link.',
    lamp: 'on',
  },
  started: {
    label: 'Started NudgeJudge',
    explain: "NudgeJudge wasn't running, so it was started with the link. It runs on its own and stays in the tray if that's on.",
    lamp: 'ink',
  },
  startedTied: {
    label: 'Started NudgeJudge, tied to this browser',
    explain: "NudgeJudge wasn't running and couldn't be started separately from this browser, so it closes when the browser does.",
    lamp: 'warn',
  },
  error: {
    label: 'Failed',
    explain: "The link couldn't be sent.",
    lamp: 'off',
  },
};
