// Host globs for automatic routing, with NudgeJudge's own semantics (NudgeJudge.Core/Rules/PatternCompiler.cs,
// HostGlob): lower case, '*' spans any characters including dots, and a leading "*." also matches the bare domain.
// This is only a pre-filter: NudgeJudge runs the full rules on every link it's sent.

export function hostMatches(host: string, pattern: string): boolean {
  host = host.toLowerCase().replace(/\.$/, '');
  if (glob(host, pattern)) return true;
  return pattern.startsWith('*.') && glob(host, pattern.slice(2));
}

export function matchesAny(url: string, patterns: readonly string[]): boolean {
  let host: string;
  try {
    host = new URL(url).hostname;
  } catch {
    return false;
  }
  return patterns.some((p) => hostMatches(host, p));
}

/** '*' globs without a regex: the literal pieces must appear in order, anchored at both ends. */
function glob(text: string, pattern: string): boolean {
  const pieces = pattern.split('*');
  if (pieces.length === 1) return text === pattern;
  const first = pieces[0]!;
  const last = pieces[pieces.length - 1]!;
  if (!text.startsWith(first) || text.length < first.length + last.length || !text.endsWith(last)) return false;
  let at = first.length;
  const end = text.length - last.length;
  for (const piece of pieces.slice(1, -1)) {
    const found = text.indexOf(piece, at);
    if (found < 0 || found + piece.length > end) return false;
    at = found + piece.length;
  }
  return true;
}
