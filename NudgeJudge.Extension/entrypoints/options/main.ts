import { browser } from 'wxt/browser';
import { describeGesture, GESTURES, type Gesture } from '@/utils/gesture';
import { checkReply, explainNativeError, HOST_NAME, PROTOCOL_VERSION, type NativeReply } from '@/utils/protocol';
import { ALL_SITES, AUTO_ROUTE, autoHostsItem, settingsItem } from '@/utils/settings';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const mac = navigator.platform.toLowerCase().startsWith('mac');

async function ask(request: { v: 1; type: 'ping' | 'autoRoutes' }): Promise<NativeReply> {
  try {
    return checkReply(await browser.runtime.sendNativeMessage(HOST_NAME, request));
  } catch (error) {
    return { v: PROTOCOL_VERSION, ok: false, error: explainNativeError(error) };
  }
}

async function checkConnection() {
  const lamp = $('lamp');
  lamp.className = 'lamp';
  $('status-label').textContent = 'Checking for NudgeJudge…';
  $('status-detail').textContent = '';
  $('retry').hidden = true;

  const reply = await ask({ v: PROTOCOL_VERSION, type: 'ping' });
  if (reply.ok) {
    lamp.className = 'lamp on';
    $('status-label').textContent = `Connected to NudgeJudge ${reply.app ?? ''}`.trim();
    $('status-detail').textContent = 'Links you send open where your rules say.';
  } else {
    lamp.className = 'lamp off';
    $('status-label').textContent = "Can't reach NudgeJudge";
    $('status-detail').textContent = reply.error ?? '';
    $('retry').hidden = false;
  }
}

async function showGesture() {
  const settings = await settingsItem.getValue();
  const select = $<HTMLSelectElement>('gesture');
  if (select.options.length === 0) {
    for (const g of GESTURES) select.add(new Option(describeGesture(g, mac), g));
  }
  select.value = settings.gesture;
  const allowed = await browser.permissions.contains(ALL_SITES);
  $('gesture-permission').hidden = allowed || settings.gesture === 'off';
}

async function showAuto() {
  const settings = await settingsItem.getValue();
  const allowed = await browser.permissions.contains(AUTO_ROUTE);
  $<HTMLInputElement>('auto').checked = settings.autoRoute && allowed;
  const hostsLine = $('auto-hosts');
  if (!(settings.autoRoute && allowed)) {
    hostsLine.textContent = '';
    return;
  }
  const reply = await ask({ v: PROTOCOL_VERSION, type: 'autoRoutes' });
  if (!reply.ok) {
    hostsLine.textContent = reply.error ?? '';
    return;
  }
  const hosts = reply.hosts ?? [];
  await autoHostsItem.setValue({ hosts, fetchedAt: Date.now() });
  hostsLine.textContent = hosts.length
    ? `Watching ${hosts.join(', ')}.`
    : 'No rules are marked yet. In NudgeJudge, edit a rule and tick “Also catch in the browser”.';
}

// Permission prompts must come straight from the click, before any other await.
$('grant-gesture').addEventListener('click', async () => {
  if (await browser.permissions.request(ALL_SITES)) $('gesture-reload').hidden = false;
  await showGesture();
});

$<HTMLSelectElement>('gesture').addEventListener('change', async (event) => {
  const gesture = (event.target as HTMLSelectElement).value as Gesture;
  const granted = gesture === 'off' || (await browser.permissions.request(ALL_SITES));
  const settings = await settingsItem.getValue();
  await settingsItem.setValue({ ...settings, gesture });
  if (granted && gesture !== 'off') $('gesture-reload').hidden = false;
  await showGesture();
});

$<HTMLInputElement>('auto').addEventListener('change', async (event) => {
  const on = (event.target as HTMLInputElement).checked;
  const granted = !on || (await browser.permissions.request(AUTO_ROUTE));
  const settings = await settingsItem.getValue();
  await settingsItem.setValue({ ...settings, autoRoute: on && granted });
  await showAuto();
});

$('retry').addEventListener('click', () => void checkConnection());

void (async () => {
  const commands = await browser.commands.getAll();
  const shortcut = commands.find((c) => c.name === 'send-page')?.shortcut;
  $('shortcut').textContent = shortcut || 'a keyboard shortcut (set one in the browser’s extension shortcuts)';
  await Promise.all([checkConnection(), showGesture(), showAuto()]);
})();
