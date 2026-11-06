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
| Not the default browser | hollow ring | "Not default yet", a detail saying routing is on/off but no links reach NudgeJudge, a dimmed switch, and a **Make default browser** button to Settings |
| Routing off | stop red | "Routing off" |
| Routing on | proceed green | "Routing on" |

Settings opens with the default-browser card for the same reason: nothing else works until links reach NudgeJudge. When routing is off, the Rules page shows a stop notice above the list and dims the junctions.

## Match-kind glyphs

[Controls/MatchKindGlyph.cs](NudgeJudge/Controls/MatchKindGlyph.cs): domain = filled square, wildcard = open diamond, path prefix = arrowhead, regex = ring with dot. Show them next to every pattern. Source-app conditions use the `Icon.App` stroke icon (a window with content lines) in the same places.

## Rules: name first, conditions underneath

A rule row leads with the rule's **name** (semibold, UI font): names are required, because a compound rule has no single pattern to show. Under it, [Controls/ConditionSummary.cs](NudgeJudge/Controls/ConditionSummary.cs) draws the conditions as one trimmed line: glyph or app icon, values in mono, connecting words ("or", "from", "and") in the faint text face. The full line is the tooltip.

The editor's **When** section lays conditions on a track like junctions on the line: each condition's glyph sits in a 16 px gutter, a 1.5 px Track line runs from it to the next, and the join word ("and"/"or", semibold muted) sits on the line between them. The "Match all/any of these" choice appears only with two or more conditions. Alternatives within a condition are stacked fields; every field after the first shows a faint "or" inside it. Errors sit under the field they belong to. Conditions are not boxed: the editor is already a panel, and cards never nest.

**Groups** nest conditions up to three levels (the rule, a group in it, and a group in that). A group sits on the parent's track like a set of points: an `Icon.Branch` glyph in the gutter, a "Match [all/any] of these" header with its own delete, and its rows inset on their own track. That track runs on past the last row to a short stub at the group's ghost add row ("+ Link", "+ Opened from", "+ Group"), so each add row visibly belongs to its group; the deepest level offers no "+ Group". The rule's own level works the same way: same ghost row, same stub on its own track. Every level's add row looks alike, because the actions are the same and only the line says where they add; with no conditions yet the row sits flush left. A new group starts with one blank link condition and the opposite join to its parent, because a group with the parent's join changes nothing. A condition's hint is its field's tooltip, never inline text: repeated in every row it buried the structure. Conditions and groups reorder within their group with up/down arrows left of the delete button; like rule-row actions they're hidden at rest and appear on hover or keyboard focus (a condition anywhere over its fields, a group only over its header), and not at all when the row is alone in its group. Order never changes what matches. Groups aren't boxed either. In the rule list's summary line, groups of two or more sit in parentheses with tighter spacing around their join word. Suggested names only use conditions that must always hold: the rule's own, and those in "all of" groups.

The rule name is suggested from the first link pattern and app ("acme.io from Microsoft Teams") until the user types their own, so the required field never starts empty. Clearing it disables Save and says why.

**Clone** opens an unsaved copy in the editor (title "Clone rule"); it's added below its original only on Save. A saved copy that still routes like its original carries a neutral **Duplicate of n ↑** badge (`Icon.Up`, since the original is always above). Clicking it outlines the original in ink (`Border.row.highlight`: 1.5 px InkMuted border, hover surface) for two seconds. Not amber: nothing is being routed.

Time values are free text, so each one is read back under its field in small muted text once it parses ("Weekdays, 09:00 to 17:00"; overnight windows say so). It hides while the field shows an error. This is feedback, not a hint, so it doesn't break the tooltip rule.

## Source app

Where a link came from is shown as "from ▤ Microsoft Teams" (faint "from", muted name, path in the tooltip) beside the host in Activity, and in the chooser's eyebrow line ("No rule matched this link from Microsoft Teams"). The chooser's footer offers "Only links from …" when saving a rule. The tester takes an optional app beside the link.

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
- **Needs review** (an imported browser that looked suspicious): a stop badge on the browser row with one trimmed line under the program ("Needs review: runs a script · risky arguments", full reasons in the tooltip). The editor opens with a `notice stop` listing one reason per line, risky arguments in mono, and the save button reads **Save as reviewed**. Activity rows held back by one show **Review browser** at rest, and the chooser notes how many it left out.
- **Import review** (Settings): a counted `notice stop` ("1 browser would run a different program · 1 rule will open somewhere else"), then per section the items needing a look in full, then one ghost toggle ("7 rules more, nothing to check"). Rule lines reuse `ConditionSummary`; destinations that need review get a stop badge, not parentheses. Rows update in place when a choice changes, so nothing moves under the pointer. Radios, like checkboxes, are ink when on.
- **Setup checklist:** the **Get started** page (first in the sidebar, selected on launch while there are no rules, hidden once there are rules and NudgeJudge is the default browser) shows four numbered junction steps (find browsers, first rule, default browser, routing on). With no rules, the Rules page shows a short empty state with **Add rule** and **Get started**. Done steps show a proceed check in the junction; the next step's button is the accent.

## Chooser

Keyboard first: 1–9 picks a browser, Esc skips, Ctrl+Tab cycles queued links. Browsers are "platform" buttons with key caps, all the same height so the two columns line up. The remember mode and the pattern sit in a sunken footer.
