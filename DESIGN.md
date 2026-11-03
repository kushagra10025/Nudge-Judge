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
| Route (amber) | `#C97C06` | `#F2B33D` | **The only live accent**: a route being set (matched junction and row, the diagram's diverted line, "Routed" lamp), one primary button per view, selection. Never on toggles, checkboxes, badges or notices |
| Proceed / Stop | `#1B7F4D` / `#BF2F35` | `#45C486` / `#EA5A5F` | States only: routing on/off, errors, "Primary", "Not secure" |

Text on amber is always the dark `OnLampBrush` (`#1A1305`). Soft variants (`*SoftBrush`) are backgrounds for badges and highlighted rows.

"On" is ink, not amber: ToggleSwitch and CheckBox fills use Ink (light) / Ink (dark) with a Surface knob or glyph, so a page of enabled rules stays quiet and the matched rule is the only amber on it. The sidebar switch uses panel ink because the panel is dark in every theme.

## Type

- UI: Inter (bundled). Scale is 12 / 13 / 14 / 16 / 24; page titles use 24 semibold with -0.3 tracking.
- Mono (Cascadia Mono → JetBrains Mono → DejaVu Sans Mono → Consolas): **only** for URLs, patterns, paths, arguments and timestamps.

## Signature: route diagram

[Controls/RouteDiagram.cs](NudgeJudge/Controls/RouteDiagram.cs). The main line has one junction per rule, numbered. Passed junctions are amber rings, the matching junction is an amber diamond, and from there the route diverts to the browser's platform. With no match, the line runs to the "Ask me" terminal. Disabled rules are dashed and errors are red. The route draws on over 320 ms with an ease-out; this is the only authored motion in the app.

Rule rows echo the diagram: the number sits in a junction circle that lights amber when the tester matches that rule. The tester is secondary: it stays closed until "Test a link" (Ctrl+T) opens it as a panel docked under the rule list, so the page leads with creating rules.

## Signal states

The sidebar signal shows the one state that matters most, in this order:

| State | Lamp | Label |
|---|---|---|
| Not the default browser | hollow ring | "Not default yet" + a **Set up** button to Settings |
| Routing off | stop red | "Routing off" |
| Routing on | proceed green | "Routing on" |

Settings opens with the default-browser card for the same reason: nothing else works until links reach NudgeJudge. When routing is off, the Rules page shows a stop notice above the list and dims the junctions.

## Match-kind glyphs

[Controls/MatchKindGlyph.cs](NudgeJudge/Controls/MatchKindGlyph.cs): domain = filled square, wildcard = open diamond, path prefix = arrowhead, regex = ring with dot. Show them next to every pattern.

## Icons

Stroke icons drawn by [Controls/Icon.cs](NudgeJudge/Controls/Icon.cs) from `Icon.*` geometries on a 24-unit box, at stroke 1.8 with round caps. Add new icons as geometries, never Unicode glyphs or emoji.

## Layout

- 4/8 px grid. Pages use 32 px side margins and 28 px top.
- Containers (lists, cards, the setup checklist, Settings cards) span the page width; only text is capped (paragraphs about 640 px, setting descriptions 600 px) and actions sit at the right edge. Unboxed empty-state text may keep a narrow column.
- Lists are rows (`Border.row`), not cards. Never nest cards.
- Editors are a side panel (`Border.panel`, 340–360 px) beside the list, not a modal.
- Rows must trim (put text in Grid cells with `TextTrimming`; horizontal StackPanels don't trim). Row errors are one line with the full text in a tooltip.
- Row actions (move, edit, delete, try, make primary) are hidden at rest and appear on hover, keyboard focus (`:focus-within`) and while the row is being edited. Toggles stay visible.
- When the page is narrower than a 520 px list plus the editor, the editor becomes a drawer over the list with a scrim (`Views/EditorDock.cs`); clicking the scrim closes it. Editors scroll on short windows.

## Components

- **Buttons:** accent (amber) for the single primary action in view; secondary buttons are outlined on the surface (`Line` border), and disabled ones lose the fill and border so they never look like the enabled ones; ghost for row and inline actions.
- **Notices** (`Border.notice`): a sunken strip with one lamp (faint for information, stop for routing off) and at most one inline action. No coloured fills and no coloured edges.
- **Badges:** neutral by default (Sample, Private, profile, time left). Only "Primary" (proceed) and "Not secure" (stop) carry colour.
- **Undo toast** (`UndoToast`, `UndoBar`): deleting a rule or browser and clearing Activity happen at once and offer **Undo** for 8 s in a panel-coloured slip at the page foot. No confirmation dialogs.
- **Activity outcomes:** a lamp and a word. Amber = a rule routed it, ink = you chose, hollow = the chooser asked, stop = failed or rejected.
- **Setup checklist:** with no rules, the Rules page shows three numbered junction steps (find browsers, first rule, default browser). Done steps show a proceed check in the junction; the next step's button is the accent.

## Chooser

Keyboard first: 1–9 picks a browser, Esc skips, Ctrl+Tab cycles queued links. Browsers are "platform" buttons with key caps, all the same height so the two columns line up. The remember mode and the pattern sit in a sunken footer.
