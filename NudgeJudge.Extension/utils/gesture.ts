// The modifier-click that sends a link to NudgeJudge. Browsers already use Ctrl/⌘ (new tab), Shift (new window),
// Ctrl+Shift (foreground tab), Alt (download, in Chrome) and, on macOS, Ctrl (right-click). Alt+Shift is free.

export type Gesture = 'alt-shift' | 'ctrl-alt' | 'ctrl-alt-shift' | 'off';

export const DEFAULT_GESTURE: Gesture = 'alt-shift';

export interface ModifierState {
  altKey: boolean;
  shiftKey: boolean;
  ctrlKey: boolean;
  metaKey: boolean;
  button: number;
}

/** Exactly these modifiers with the main button, so the browser's own shortcuts never fire us by accident. */
export function matchesGesture(event: ModifierState, gesture: Gesture): boolean {
  if (gesture === 'off' || event.button !== 0 || event.metaKey) return false;
  const want = {
    'alt-shift': { alt: true, shift: true, ctrl: false },
    'ctrl-alt': { alt: true, shift: false, ctrl: true },
    'ctrl-alt-shift': { alt: true, shift: true, ctrl: true },
  }[gesture];
  return event.altKey === want.alt && event.shiftKey === want.shift && event.ctrlKey === want.ctrl;
}

/** How a gesture reads on this platform (⌥ and ⇧ on macOS). */
export function describeGesture(gesture: Gesture, mac: boolean): string {
  if (gesture === 'off') return 'Off';
  const keys = { 'alt-shift': ['alt', 'shift'], 'ctrl-alt': ['ctrl', 'alt'], 'ctrl-alt-shift': ['ctrl', 'alt', 'shift'] }[gesture];
  const name = (k: string) => (mac ? { ctrl: '⌃', alt: '⌥', shift: '⇧' } : { ctrl: 'Ctrl', alt: 'Alt', shift: 'Shift' })[k];
  return mac ? keys.map(name).join('') + ' click' : keys.map(name).join('+') + '+click';
}

export const GESTURES: readonly Gesture[] = ['alt-shift', 'ctrl-alt', 'ctrl-alt-shift', 'off'];

/** The link a click landed on, looking through shadow roots; null when it isn't on a link. */
export function linkFromPath(path: readonly EventTarget[]): HTMLAnchorElement | HTMLAreaElement | null {
  for (const target of path) {
    const el = target as Element;
    if (el?.nodeType !== 1) continue;
    const tag = el.tagName;
    if ((tag === 'A' || tag === 'AREA') && (el as HTMLAnchorElement).hasAttribute('href'))
      return el as HTMLAnchorElement | HTMLAreaElement;
  }
  return null;
}

