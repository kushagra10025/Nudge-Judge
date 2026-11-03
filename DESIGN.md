# Design: Signal Box

A link is a train on a line. Rules are points and browsers are platforms. The UI shows the route a link takes, and uses standard Avalonia Fluent controls everywhere else.

Source of truth: [NudgeJudge/Styles/Theme.axaml](NudgeJudge/Styles/Theme.axaml) (tokens, icons) and [NudgeJudge/Styles/Styles.axaml](NudgeJudge/Styles/Styles.axaml) (classes).

## Colour

| Role | Light | Dark | Use |
|---|---|---|---|
| Panel | `#0F1F18` | same | Sidebar in every theme: the signalling panel |
| Ground | `#EDF1EE` | `#0D1411` | Window background (green-tinted neutral) |
| Surface | `#FFFFFF` | `#141E19` | Cards, editors, chooser |
| Ink / Muted / Faint | `#13201A` / `#4B5B52` / `#66766D` | `#E3EBE6` / `#A2B3A9` / `#84978C` | Text hierarchy. Never use grey for text |
| Track | `#B4C1B9` | `#3A4C42` | Unlit lines, junction outlines |
| Route (amber) | `#C97C06` | `#F2B33D` | **The only live accent**: route set, matched rule, primary buttons, selection |
| Proceed / Stop | `#1B7F4D` / `#BF2F35` | `#45C486` / `#EA5A5F` | States only: routing on/off, errors, "Primary", "Not secure" |

Text on amber is always the dark `OnLampBrush` (`#1A1305`). Soft variants (`*SoftBrush`) are backgrounds for badges and highlighted rows.

## Type

- UI: Inter (bundled). Scale is 12 / 13 / 14 / 16 / 24; page titles use 24 semibold with -0.3 tracking.
- Mono (Cascadia Mono → JetBrains Mono → DejaVu Sans Mono → Consolas): **only** for URLs, patterns, paths, arguments and timestamps.

## Signature: route diagram

[Controls/RouteDiagram.cs](NudgeJudge/Controls/RouteDiagram.cs). The main line has one junction per rule, numbered. Passed junctions are amber rings, the matching junction is an amber diamond, and from there the route diverts to the browser's platform. With no match, the line runs to the "Ask me" terminal. Disabled rules are dashed and errors are red. The route draws on over 320 ms with an ease-out; this is the only authored motion in the app.

Rule rows echo the diagram: the number sits in a junction circle that lights amber when the tester matches that rule. The tester is secondary: it stays closed until "Test a link" (Ctrl+T) opens it as a panel docked under the rule list, so the page leads with creating rules.

## Match-kind glyphs

[Controls/MatchKindGlyph.cs](NudgeJudge/Controls/MatchKindGlyph.cs): domain = filled square, wildcard = open diamond, path prefix = arrowhead, regex = ring with dot. Show them next to every pattern.

## Icons

Stroke icons drawn by [Controls/Icon.cs](NudgeJudge/Controls/Icon.cs) from `Icon.*` geometries on a 24-unit box, at stroke 1.8 with round caps. Add new icons as geometries, never Unicode glyphs or emoji.

## Layout

- 4/8 px grid. Pages use 32 px side margins and 28 px top.
- Lists are rows (`Border.row`), not cards. Never nest cards.
- Editors are a side panel (`Border.panel`, 340–360 px) beside the list, not a modal.
- Rows must trim (put text in Grid cells with `TextTrimming`; horizontal StackPanels don't trim).

## Chooser

Keyboard first: 1–9 picks a browser, Esc skips, Ctrl+Tab cycles queued links. Browsers are "platform" buttons with key caps. The remember mode and the pattern sit in a sunken footer.
