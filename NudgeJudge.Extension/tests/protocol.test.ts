import { describe, expect, it } from 'vitest';
import { checkReply, explainNativeError, isUnreachable, isWebUrl } from '../utils/protocol';

describe('isWebUrl', () => {
  it('only lets http and https through', () => {
    expect(isWebUrl('https://example.com')).toBe(true);
    expect(isWebUrl('http://example.com')).toBe(true);
    for (const url of ['javascript:alert(1)', 'file:///etc/passwd', 'chrome://settings', 'about:blank', '', undefined]) {
      expect(isWebUrl(url)).toBe(false);
    }
  });
});

describe('explainNativeError', () => {
  it('turns a missing host into setup instructions', () => {
    const chrome = { v: 1, ok: false, error: explainNativeError(new Error('Specified native messaging host not found.')) };
    const firefox = { v: 1, ok: false, error: explainNativeError(new Error('No such native application com.nudgejudge.host')) };
    expect(isUnreachable(chrome)).toBe(true);
    expect(isUnreachable(firefox)).toBe(true);
    expect(chrome.error).toContain('Settings → Browser extension');
  });

  it('explains an extension ID the host does not allow', () => {
    expect(explainNativeError(new Error('Access to the specified native messaging host is forbidden.'))).toContain("doesn't recognise");
  });
});

describe('checkReply', () => {
  it('turns anything malformed into an error', () => {
    expect(checkReply(undefined).ok).toBe(false);
    expect(checkReply({ ok: 'yes' }).ok).toBe(false);
    expect(checkReply({ v: 1, ok: true, action: 'sent' }).action).toBe('sent');
  });
});
