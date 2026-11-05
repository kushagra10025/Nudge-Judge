import { storage } from '#imports';
import type { Browser } from 'wxt/browser';
import { DEFAULT_GESTURE, type Gesture } from './gesture';

export interface Settings {
  /** The modifier-click. Only active once the "all sites" permission is granted. */
  gesture: Gesture;
  /** Send links on hosts of rules marked "Also catch in the browser". Needs the same permission, plus webNavigation. */
  autoRoute: boolean;
}

export const DEFAULT_SETTINGS: Settings = { gesture: DEFAULT_GESTURE, autoRoute: false };

export const settingsItem = storage.defineItem<Settings>('local:settings', { fallback: DEFAULT_SETTINGS });

/** Hosts from NudgeJudge for automatic routing, cached for the browser session. */
export const autoHostsItem = storage.defineItem<{ hosts: string[]; fetchedAt: number }>('session:autoHosts', {
  fallback: { hosts: [], fetchedAt: 0 },
});

/** The optional permissions each feature needs. */
export const ALL_SITES: Browser.permissions.Permissions = { origins: ['<all_urls>'] };
export const AUTO_ROUTE: Browser.permissions.Permissions = { origins: ['<all_urls>'], permissions: ['webNavigation'] };
