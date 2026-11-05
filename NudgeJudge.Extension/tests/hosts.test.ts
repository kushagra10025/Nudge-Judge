import { describe, expect, it } from 'vitest';
import { hostMatches, matchesAny } from '../utils/hosts';

describe('hostMatches', () => {
  it('matches exact hosts', () => {
    expect(hostMatches('zoom.us', 'zoom.us')).toBe(true);
    expect(hostMatches('ZOOM.us.', 'zoom.us')).toBe(true);
    expect(hostMatches('notzoom.us', 'zoom.us')).toBe(false);
  });

  it('lets a leading *. cover the bare domain too, like NudgeJudge', () => {
    expect(hostMatches('acme.io', '*.acme.io')).toBe(true);
    expect(hostMatches('a.b.acme.io', '*.acme.io')).toBe(true);
    expect(hostMatches('evilacme.io', '*.acme.io')).toBe(false);
  });

  it('matches stars in the middle without overlapping', () => {
    expect(hostMatches('docs.acme.io', 'docs.*.io')).toBe(true);
    expect(hostMatches('ab', 'a*b*b')).toBe(false);
    expect(hostMatches('abb', 'a*b*b')).toBe(true);
  });
});

describe('matchesAny', () => {
  it('looks at the URL host only', () => {
    expect(matchesAny('https://us02web.zoom.us/j/123', ['*.zoom.us'])).toBe(true);
    expect(matchesAny('https://example.com/zoom.us', ['zoom.us'])).toBe(false);
    expect(matchesAny('not a url', ['*'])).toBe(false);
  });
});
