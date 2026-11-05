import { describe, expect, it } from 'vitest';
import { describeGesture, linkFromPath, matchesGesture, type ModifierState } from '../utils/gesture';

const click = (mods: Partial<ModifierState>): ModifierState => ({
  altKey: false, shiftKey: false, ctrlKey: false, metaKey: false, button: 0, ...mods,
});

describe('matchesGesture', () => {
  it('takes exactly the chosen modifiers', () => {
    expect(matchesGesture(click({ altKey: true, shiftKey: true }), 'alt-shift')).toBe(true);
    expect(matchesGesture(click({ ctrlKey: true, altKey: true }), 'ctrl-alt')).toBe(true);
    expect(matchesGesture(click({ ctrlKey: true, altKey: true, shiftKey: true }), 'ctrl-alt-shift')).toBe(true);
  });

  it("never takes the browser's own modifier clicks", () => {
    for (const mods of [{}, { ctrlKey: true }, { shiftKey: true }, { ctrlKey: true, shiftKey: true }, { altKey: true }, { metaKey: true }]) {
      expect(matchesGesture(click(mods), 'alt-shift')).toBe(false);
    }
  });

  it('ignores extra modifiers, other buttons and the off setting', () => {
    expect(matchesGesture(click({ altKey: true, shiftKey: true, ctrlKey: true }), 'alt-shift')).toBe(false);
    expect(matchesGesture(click({ altKey: true, shiftKey: true, metaKey: true }), 'alt-shift')).toBe(false);
    expect(matchesGesture(click({ altKey: true, shiftKey: true, button: 1 }), 'alt-shift')).toBe(false);
    expect(matchesGesture(click({ altKey: true, shiftKey: true }), 'off')).toBe(false);
  });
});

describe('describeGesture', () => {
  it('uses symbols on macOS', () => {
    expect(describeGesture('alt-shift', false)).toBe('Alt+Shift+click');
    expect(describeGesture('alt-shift', true)).toBe('⌥⇧ click');
    expect(describeGesture('off', true)).toBe('Off');
  });
});

describe('linkFromPath', () => {
  const el = (tagName: string, href: boolean) => ({ nodeType: 1, tagName, hasAttribute: (n: string) => n === 'href' && href });

  it('finds the nearest link with an href', () => {
    const anchor = el('A', true);
    expect(linkFromPath([el('SPAN', false), anchor, el('DIV', false)] as unknown as EventTarget[])).toBe(anchor);
  });

  it('skips anchors without an href and non-elements', () => {
    expect(linkFromPath([el('A', false), { nodeType: 9 }] as unknown as EventTarget[])).toBeNull();
  });
});
