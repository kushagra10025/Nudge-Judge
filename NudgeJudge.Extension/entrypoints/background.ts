import { browser, type Browser } from 'wxt/browser';
import { matchesAny } from '@/utils/hosts';
import {
  checkReply,
  explainNativeError,
  HOST_NAME,
  isUnreachable,
  isWebUrl,
  PROTOCOL_VERSION,
  type NativeReply,
  type NativeRequest,
  type OpenMessage,
} from '@/utils/protocol';
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
  browser.runtime.onMessage.addListener((message: unknown, sender) => {
    const open = message as OpenMessage;
    if (open?.kind !== 'open' || !isWebUrl(open.url) || sender.id !== browser.runtime.id) return undefined;
    return openLink(open.url, 'click', sender.tab?.id).then((reply) => {
      if (!reply.ok) showError(reply, sender.tab?.id);
      return reply;
    });
  });

  // The optional permissions and settings decide which features run.
  browser.permissions.onAdded.addListener(() => void syncFeatures());
  browser.permissions.onRemoved.addListener(() => void syncFeatures());
  settingsItem.watch(() => void syncFeatures());
  void syncFeatures();

  // webNavigation only exists once its optional permission is granted. Listeners must be added at startup for the
  // worker to be woken by them, so add it now when we can, and from syncFeatures right after a grant.
  listenForNavigations();
});

function createMenus() {
  browser.contextMenus.removeAll(() => {
    browser.contextMenus.create({ id: MENU_LINK, title: 'Open with NudgeJudge', contexts: ['link'] });
  });
}

async function send(request: NativeRequest): Promise<NativeReply> {
  try {
    return checkReply(await browser.runtime.sendNativeMessage(HOST_NAME, request));
  } catch (error) {
    return { v: PROTOCOL_VERSION, ok: false, error: explainNativeError(error) };
  }
}

/** Sends a link, and keeps a line about what happened for the options page's "Recent links". */
async function openLink(url: string, trigger: Trigger, tabId: number | undefined, auto = false): Promise<NativeReply> {
  const reply = await send({ v: PROTOCOL_VERSION, type: 'open', url, ...(auto ? { auto } : {}) });
  await recentSendsItem.setValue(addRecord(await recentSendsItem.getValue(), recordFor(url, trigger, reply)));
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

async function refreshAutoHosts(force = false): Promise<string[]> {
  const cached = await autoHostsItem.getValue();
  if (!force && Date.now() - cached.fetchedAt < AUTO_HOSTS_TTL_MS) return cached.hosts;
  const reply = await send({ v: PROTOCOL_VERSION, type: 'autoRoutes' });
  // Keep the old list if NudgeJudge can't be reached, but try again next time.
  const hosts = reply.ok ? (reply.hosts ?? []) : cached.hosts;
  await autoHostsItem.setValue({ hosts, fetchedAt: reply.ok ? Date.now() : cached.fetchedAt });
  return hosts;
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
