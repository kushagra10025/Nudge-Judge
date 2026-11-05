// The native messaging protocol. Mirrors NudgeJudge.Core/NativeMessaging/NativeMessaging.cs;
// see docs/browser-extension.md#protocol.

export const HOST_NAME = 'com.nudgejudge.host';
export const PROTOCOL_VERSION = 1;

export type NativeRequest =
  | { v: 1; type: 'ping' }
  | { v: 1; type: 'open'; url: string; auto?: boolean }
  | { v: 1; type: 'autoRoutes' };

export interface NativeReply {
  v: number;
  ok: boolean;
  action?: 'pong' | 'sent' | 'stay' | 'autoRoutes';
  error?: string;
  app?: string;
  hosts?: string[];
  /**
   * With "sent": how. "running" went to a running NudgeJudge; "started" started one, separate from the browser;
   * "startedTied" started one that couldn't leave the browser's process job, so it closes when the browser does.
   */
  via?: 'running' | 'started' | 'startedTied';
}

/** What a content script asks the background for. */
export interface OpenMessage {
  kind: 'open';
  url: string;
}

/** Only these ever go to NudgeJudge (it refuses anything else too). */
export function isWebUrl(url: string | undefined): url is string {
  if (!url) return false;
  try {
    const { protocol } = new URL(url);
    return protocol === 'http:' || protocol === 'https:';
  } catch {
    return false;
  }
}

/** True when NudgeJudge itself isn't reachable (as opposed to it answering with an error). */
export function isUnreachable(reply: NativeReply): boolean {
  return !reply.ok && reply.error === UNREACHABLE;
}

const UNREACHABLE = "Can't reach NudgeJudge. Open NudgeJudge, go to Settings → Browser extension and turn it on.";

/** Turns the browsers' native messaging errors into something a person can act on. */
export function explainNativeError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error);
  // Chrome: "Specified native messaging host not found." Firefox: "No such native application com.nudgejudge.host"
  if (/not found|no such native application/i.test(message)) return UNREACHABLE;
  // Chrome: the host manifest doesn't list this extension's ID.
  if (/forbidden/i.test(message))
    return 'NudgeJudge doesn\'t recognise this copy of the extension. Update NudgeJudge, then turn Settings → Browser extension off and on.';
  // The host started but exited or wrote something that isn't a message.
  if (/exited|disconnected|communicat/i.test(message))
    return 'NudgeJudge started but didn\'t answer. Make sure it\'s up to date.';
  return message || 'Something went wrong talking to NudgeJudge.';
}

/** A reply from the host, checked: anything malformed becomes an error reply. */
export function checkReply(reply: unknown): NativeReply {
  if (typeof reply !== 'object' || reply === null || typeof (reply as NativeReply).ok !== 'boolean')
    return { v: PROTOCOL_VERSION, ok: false, error: 'NudgeJudge sent a reply this extension doesn\'t understand.' };
  return reply as NativeReply;
}
