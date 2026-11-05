import { browser, type Browser } from 'wxt/browser';
import { matchesAny } from '@/utils/hosts';
import { sendNative as send } from '@/utils/native';
import { isUnreachable, isWebUrl, PROTOCOL_VERSION, type NativeReply, type OpenMessage } from '@/utils/protocol';
import { serialQueue } from '@/utils/serial';
import { addRecord, recordFor, type Trigger } from '@/utils/sends';
import { ALL_SITES, AUTO_ROUTE, autoHostsItem, recentSendsItem, settingsItem, tiedStartItem } from '@/utils/settings';

const MENU_LINK = 'open-link';
const GESTURE_SCRIPT = 'nudgejudge-gesture';

/** How long the hosts for automatic routing are trusted before asking NudgeJudge again. */
const AUTO_HOSTS_TTL_MS = 5 * 60 * 1000;

/** A link automatic routing just sent isn't sent again this soon (redirect chains, reloads). */
const AUTO_DEDUPE_MS = 10 * 1000;

export default defineBackground(() => {
  // Menus persist, but a reinstall or update can drop them: recreate on both.
  browser.runtime.onInstalled.addListener(createMenus);
  browser.runtime.onStartup.addListener(createMenus);

  browser.contextMenus.onClicked.addListener((info, tab) => {
    if (info.menuItemId === MENU_LINK && isWebUrl(info.linkUrl)) void sendLinkFromMenu(info.linkUrl, tab);
  });

  browser.action.onClicked.addListener((tab) => void sendPage(tab));

  browser.commands.onCommand.addListener(async (command) => {
    if (command !== 'send-page') return;
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    if (tab) await sendPage(tab);
  });

  // From the gesture content script. Answered asynchronously: the page waits for "stay" before following the link.
  // Through sendResponse and `return true`, which every browser supports; Chrome doesn't document a returned promise.
  browser.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
    const open = message as OpenMessage;
    if (open?.kind !== 'open' || !isWebUrl(open.url) || sender.id !== browser.runtime.id) return false;
    void openLink(open.url, 'click', sender.tab?.id).then((reply) => {
      if (!reply.ok) void showError(reply, sender.tab?.id);
      sendResponse(reply);
    });
    return true;
  });

  // The optional permissions and settings decide which features run.
  browser.permissions.onAdded.addListener(() => void syncSerially());
  browser.permissions.onRemoved.addListener(() => void syncSerially());
  settingsItem.watch(() => void syncSerially());
  void syncSerially();

  // webNavigation only exists once its optional permission is granted. Listeners must be added at startup for the
  // worker to be woken by them, so add it now when we can, and from syncFeatures right after a grant.
  listenForNavigations();
});

function createMenus() {
  browser.contextMenus.removeAll(() => {
    browser.contextMenus.create({ id: MENU_LINK, title: 'Open with NudgeJudge', contexts: ['link'] });
  });
}

/** Storage writes that read the list first, one at a time, so two sends at once don't drop a line. */
const recording = serialQueue();

/** Sends a link, and keeps a line about what happened for the options page's "Recent links". */
async function openLink(url: string, trigger: Trigger, tabId: number | undefined, auto = false): Promise<NativeReply> {
  const reply = await send({ v: PROTOCOL_VERSION, type: 'open', url, ...(auto ? { auto } : {}) });
  const record = recordFor(url, trigger, reply);
  await recording(async () => recentSendsItem.setValue(addRecord(await recentSendsItem.getValue(), record)));
  await noteHowItWasDelivered(reply, tabId);
  return reply;
}

/**
 * When NudgeJudge wasn't running, sending a link starts it. Normally it then runs on its own; if this browser's
 * process job wouldn't let it go, it closes with the browser, tray icon and all. Say so, once per start.
 */
async function noteHowItWasDelivered(reply: NativeReply, tabId: number | undefined) {
  if (reply.via === 'started') await tiedStartItem.setValue(null);
  if (reply.via !== 'startedTied') return;
  await tiedStartItem.setValue(Date.now());
  await flashBadge(tabId, '!', '#4B5B52',
    'NudgeJudge: sent. NudgeJudge had to start inside this browser, so it will close when the browser does. ' +
      'Start NudgeJudge yourself, or turn on Launch at startup in its Settings, so it runs on its own.');
}

/** Right-click → Open with NudgeJudge. If the rules keep it here, it opens like "Open link in new tab". */
async function sendLinkFromMenu(url: string, tab: Browser.tabs.Tab | undefined) {
  const reply = await openLink(url, 'menu', tab?.id);
  if (!reply.ok) return showError(reply, tab?.id, true);
  if (reply.action === 'stay')
    await browser.tabs.create({ url, active: false, openerTabId: tab?.id, index: tab ? tab.index + 1 : undefined });
}

/** Toolbar button or Alt+Shift+N: the current page. If the rules keep it here, there's nothing to do. */
async function sendPage(tab: Browser.tabs.Tab) {
  if (!isWebUrl(tab.url)) return showError({ v: PROTOCOL_VERSION, ok: false, error: 'Only web pages can be sent to NudgeJudge.' }, tab.id);
  const reply = await openLink(tab.url, 'page', tab.id);
  if (!reply.ok) return showError(reply, tab.id, true);
  if (reply.action === 'stay') await flashBadge(tab.id, '✓', '#1B7F4D', 'Your rules open this page in this browser.');
}

/**
 * A badge and tooltip on the toolbar button. When NudgeJudge isn't set up and the user did something deliberate,
 * the options page explains how to set it up.
 */
async function showError(reply: NativeReply, tabId: number | undefined, deliberate = false) {
  await flashBadge(tabId, '!', '#BF2F35', `NudgeJudge: ${reply.error ?? 'something went wrong.'}`);
  if (deliberate && isUnreachable(reply)) await browser.runtime.openOptionsPage();
}

async function flashBadge(tabId: number | undefined, text: string, color: string, title: string) {
  if (tabId === undefined) return;
  await browser.action.setBadgeBackgroundColor({ tabId, color });
  await browser.action.setBadgeText({ tabId, text });
  await browser.action.setTitle({ tabId, title });
  setTimeout(() => {
    void browser.action.setBadgeText({ tabId, text: '' }).catch(() => {});
    void browser.action.setTitle({ tabId, title: 'Send this page to NudgeJudge' }).catch(() => {});
  }, 6000);
}

// --- Features that depend on optional permissions ---

/** A grant and a settings change fire together; checking and registering twice at once registers the script twice. */
const syncing = serialQueue();
const syncSerially = () => syncing(syncFeatures).catch((e) => console.warn('NudgeJudge: couldn\'t update features', e));

async function syncFeatures() {
  const settings = await settingsItem.getValue();
  const allSites = await browser.permissions.contains(ALL_SITES);
  await syncGestureScript(allSites && settings.gesture !== 'off');
  if (settings.autoRoute && (await browser.permissions.contains(AUTO_ROUTE))) {
    listenForNavigations();
    void refreshAutoHosts(true);
  }
}

/** The modifier-click script runs on every page only while it's on and allowed. */
async function syncGestureScript(wanted: boolean) {
  const registered = await browser.scripting.getRegisteredContentScripts({ ids: [GESTURE_SCRIPT] });
  if (wanted && registered.length === 0) {
    await browser.scripting.registerContentScripts([
      {
        id: GESTURE_SCRIPT,
        js: ['content-scripts/content.js'],
        matches: ['<all_urls>'],
        allFrames: true,
        runAt: 'document_start',
        // Re-registered whenever the background starts (it wakes at browser startup), the same way in every browser.
        persistAcrossSessions: false,
      },
    ]);
  } else if (!wanted && registered.length > 0) {
    await browser.scripting.unregisterContentScripts({ ids: [GESTURE_SCRIPT] });
  }
}

// --- Automatic routing ---

let listening = false;
const recentlySent = new Map<string, number>();

function listenForNavigations() {
  if (listening || !browser.webNavigation) return;
  listening = true;
  browser.webNavigation.onBeforeNavigate.addListener((details) => void onBeforeNavigate(details));
}

let refreshing: Promise<string[]> | null = null;

/** One refresh at a time: page loads in several tabs at once share it. */
function refreshAutoHosts(force = false): Promise<string[]> {
  refreshing ??= (async () => {
    try {
      const cached = await autoHostsItem.getValue();
      if (!force && Date.now() - cached.fetchedAt < AUTO_HOSTS_TTL_MS) return cached.hosts;
      const reply = await send({ v: PROTOCOL_VERSION, type: 'autoRoutes' });
      // Keep the old list if NudgeJudge can't be reached, and wait the usual time before asking again: retrying on
      // every page load would start a failing process each time.
      const hosts = reply.ok ? (reply.hosts ?? []) : cached.hosts;
      await autoHostsItem.setValue({ hosts, fetchedAt: Date.now() });
      return hosts;
    } finally {
      refreshing = null;
    }
  })();
  return refreshing;
}

async function onBeforeNavigate(details: Browser.webNavigation.WebNavigationBaseCallbackDetails) {
  if (details.frameId !== 0 || details.tabId < 0 || !isWebUrl(details.url)) return;
  if (!(await settingsItem.getValue()).autoRoute) return;

  // Cheap check first, from the session cache; the list itself refreshes in the background.
  const { hosts, fetchedAt } = await autoHostsItem.getValue();
  if (Date.now() - fetchedAt >= AUTO_HOSTS_TTL_MS) void refreshAutoHosts();
  if (!matchesAny(details.url, hosts)) return;

  const last = recentlySent.get(details.url);
  if (last !== undefined && Date.now() - last < AUTO_DEDUPE_MS) return;

  const tab = await browser.tabs.get(details.tabId).catch(() => undefined);
  if (!tab) return;
  // Only links followed inside the browser. A tab NudgeJudge (or any app) opened from outside has no page before
  // this one and no opener: leave it, or a link could bounce between browsers.
  const previous = isWebUrl(tab.url) && tab.url !== details.url ? tab.url : undefined;
  if (!previous && tab.openerTabId === undefined) return;

  recentlySent.set(details.url, Date.now());
  for (const [url, at] of recentlySent) if (Date.now() - at > AUTO_DEDUPE_MS) recentlySent.delete(url);

  const reply = await openLink(details.url, 'auto', details.tabId, true);
  if (!reply.ok || reply.action !== 'sent') return;

  if (!previous) {
    // A tab opened just for this link: it's elsewhere now.
    await browser.tabs.remove(details.tabId).catch(() => {});
    return;
  }
  // Put the tab back where it was: back in history if the link already loaded, else cancel by going to the old page.
  const now = await browser.tabs.get(details.tabId).catch(() => undefined);
  if (now?.url === details.url) await browser.tabs.goBack(details.tabId).catch(() => {});
  else await browser.tabs.update(details.tabId, { url: previous }).catch(() => {});
}
