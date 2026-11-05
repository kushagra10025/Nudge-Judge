import { storage } from '#imports';
import type { Browser } from 'wxt/browser';
import { defaultGesture, type Gesture } from './gesture';
import type { SendRecord } from './sends';

export interface Settings {
  /** The modifier-click. Only active once the "all sites" permission is granted. */
  gesture: Gesture;
  /** Send links on hosts of rules marked "Also catch in the browser". Needs the same permission, plus webNavigation. */
  autoRoute: boolean;
}

/** Ctrl+Alt+click on Linux, Alt+Shift+click (⌥⇧ on macOS) elsewhere. A choice the user saved wins. */
export const DEFAULT_SETTINGS: Settings = { gesture: defaultGesture(globalThis.navigator?.platform ?? ''), autoRoute: false };

export const settingsItem = storage.defineItem<Settings>('local:settings', { fallback: DEFAULT_SETTINGS });

/** Hosts from NudgeJudge for automatic routing, cached for the browser session. */
export const autoHostsItem = storage.defineItem<{ hosts: string[]; fetchedAt: number }>('session:autoHosts', {
  fallback: { hosts: [], fetchedAt: 0 },
});

/**
 * When this browser last had to start a NudgeJudge that closes with the browser (reply via "startedTied"). Shown on
 * the options page until NudgeJudge is started separately again, or the user dismisses it.
 */
export const tiedStartItem = storage.defineItem<number | null>('local:tiedStart', { fallback: null });

/** The last links the extension sent and what happened to each, newest first (hosts only). */
export const recentSendsItem = storage.defineItem<SendRecord[]>('local:recentSends', { fallback: [] });

/** The optional permissions each feature needs. */
export const ALL_SITES: Browser.permissions.Permissions = { origins: ['<all_urls>'] };
export const AUTO_ROUTE: Browser.permissions.Permissions = { origins: ['<all_urls>'], permissions: ['webNavigation'] };
