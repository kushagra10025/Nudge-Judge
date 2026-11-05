import { browser } from 'wxt/browser';
import { DEFAULT_GESTURE, linkFromPath, matchesGesture, type Gesture } from '@/utils/gesture';
import { isWebUrl, type NativeReply, type OpenMessage } from '@/utils/protocol';
import { settingsItem } from '@/utils/settings';

// The modifier-click. Registered at runtime by the background, only once the "all sites" permission is granted
// and the gesture is on, so there's no install warning and no script on pages until the user asks for it.
export default defineContentScript({
  registration: 'runtime',
  main(ctx) {
    let gesture: Gesture = DEFAULT_GESTURE;
    void settingsItem.getValue().then((s) => (gesture = s.gesture));
    const unwatch = settingsItem.watch((s) => (gesture = s.gesture));
    ctx.onInvalidated(unwatch);

    const linkFor = (event: MouseEvent) => {
      // isTrusted: a page can't script a fake modified click to push links into the user's router.
      if (!event.isTrusted || !matchesGesture(event, gesture)) return null;
      const link = linkFromPath(event.composedPath());
      return link && isWebUrl(link.href) ? link : null;
    };

    // Shift+mousedown would start extending a text selection.
    ctx.addEventListener(window, 'mousedown', (event) => {
      if (linkFor(event)) event.preventDefault();
    }, { capture: true });

    ctx.addEventListener(window, 'click', (event) => {
      const link = linkFor(event);
      if (!link) return;
      // Before the page's own handlers and the browser's default (a download for Alt+click in Chrome).
      event.preventDefault();
      event.stopImmediatePropagation();

      const message: OpenMessage = { kind: 'open', url: link.href };
      void browser.runtime.sendMessage(message).then((reply: NativeReply | undefined) => {
        // The rules keep it in this browser: click it again, plainly. That honours its target, its frame and the
        // page's own handlers, and isn't trusted, so it doesn't come back here.
        if (reply?.ok && reply.action === 'stay') link.click();
      });
    }, { capture: true });
  },
});
