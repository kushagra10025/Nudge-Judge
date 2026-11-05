import { browser } from 'wxt/browser';
import { defaultGesture, describeGesture, GESTURES, type Gesture } from '@/utils/gesture';
import { checkReply, explainNativeError, HOST_NAME, PROTOCOL_VERSION, type NativeReply } from '@/utils/protocol';
import { OUTCOMES, TRIGGER_LABELS, type SendRecord } from '@/utils/sends';
import { ALL_SITES, AUTO_ROUTE, autoHostsItem, recentSendsItem, settingsItem, tiedStartItem } from '@/utils/settings';

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
    const usual = defaultGesture(navigator.platform);
    for (const g of GESTURES) select.add(new Option(describeGesture(g, mac) + (g === usual ? ' (default)' : ''), g));
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

async function showTied() {
  const at = await tiedStartItem.getValue();
  $('tied').hidden = at === null;
  if (at !== null) $('tied-when').textContent = `At ${new Date(at).toLocaleString()}`;
}

function showRecent(records: SendRecord[]) {
  const list = $('recent');
  list.replaceChildren(
    ...records.map((r) => {
      const outcome = OUTCOMES[r.outcome];
      const li = document.createElement('li');
      li.title = outcome.explain;

      const lamp = document.createElement('span');
      lamp.className = `lamp ${outcome.lamp === 'ring' ? '' : outcome.lamp}`;
      const host = document.createElement('span');
      host.className = 'host';
      host.textContent = r.host;
      const time = document.createElement('span');
      time.className = 'time';
      time.textContent = new Date(r.at).toLocaleTimeString();
      const how = document.createElement('span');
      how.className = 'how';
      const label = document.createElement('b');
      label.textContent = outcome.label;
      how.append(label, ` · ${TRIGGER_LABELS[r.trigger]}${r.error ? ` · ${r.error}` : ''}`);

      li.append(lamp, host, time, how);
      return li;
    }),
  );
  $('recent-empty').hidden = records.length > 0;
  $('recent-clear').hidden = records.length === 0;
}

$('recent-clear').addEventListener('click', () => void recentSendsItem.setValue([]));
recentSendsItem.watch((records) => showRecent(records ?? []));
// The tied notice changes when a send starts NudgeJudge while this page is open.
tiedStartItem.watch(() => void showTied());

$('tied-dismiss').addEventListener('click', async () => {
  await tiedStartItem.setValue(null);
  await showTied();
});

void (async () => {
  const commands = await browser.commands.getAll();
  const shortcut = commands.find((c) => c.name === 'send-page')?.shortcut;
  $('shortcut').textContent = shortcut || 'a keyboard shortcut (set one in the browser’s extension shortcuts)';
  showRecent(await recentSendsItem.getValue());
  await Promise.all([checkConnection(), showGesture(), showAuto(), showTied()]);
})();
