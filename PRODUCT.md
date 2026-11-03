# Product

<!-- impeccable:product-schema 1 -->

## Platform

desktop (Avalonia UI, native window chrome). Linux and Windows are primary and tested; macOS is stubbed for now.

## Stack

C# / .NET 10, Avalonia 12 (Fluent theme), CommunityToolkit.Mvvm. Strict MVVM: views hold no business logic.
Projects: `NudgeJudge` (core + UI), `NudgeJudge.Desktop` (entry point / platform head).

## Users

Layered audience. By default it serves general users who want links to "just open in the right browser"; power users and developers (who keep work, personal and client browser profiles apart) get regex, path conditions, flags and profiles one step away. Simple by default, power features one click away.

## Product Purpose

NudgeJudge registers as the system's default browser, receives every http/https link the OS hands it, matches the URL against an ordered set of user rules, and launches it in the right browser or profile. When no rule matches, a small chooser window asks once and can turn the answer into a rule.

Success: links open in the right place without the user thinking about it, and the idle background process costs nothing.

## Positioning

A single background instance with a secured local IPC channel (session token, URL validation, regex time limits) routes links in milliseconds. The fallback chooser groups bursts of unmatched links into tabs and turns one decision into a one-time open, a temporary rule, or a permanent rule.

## Operating Context

- Lives in the system tray most of the time; the main window is opened occasionally to manage rules.
- The fallback chooser appears without warning while the user is in another app (clicked a link in chat, mail, terminal). It must be readable and decided in a second or two, keyboard first.
- Tray offers a global on/off toggle (when off, links pass through to a primary browser), recent history, open settings, quit.

## Capabilities and Constraints

- Rule match types: exact domain, wildcard (`*.workdomain.com`), regular expression, path conditions. Rules are ordered; first match wins.
- Rule actions from the chooser: Just Open Once, Save Temp Rule (expires after X hours or at reboot), Save as Rule.
- Browser auto-discovery (Chrome, Firefox, Edge, Brave, Opera, Vivaldi, Safari…) plus manual entries with custom path, arguments, profile, private mode.
- Security: IPC session token in a 0600 file, URL validation, regex match timeouts against ReDoS.
- Terminology: "Rule", "Browser" (an installed browser or a configured profile/variant of one), "Activity" (the redirect log), "Chooser" (fallback window).

## Brand Commitments

Name: NudgeJudge. No other identity assets exist yet.

## Evidence on Hand

None. Don't make up usage stats, testimonials or browser counts.

## Product Principles

1. Invisible when right: the best interaction is no interaction.
2. One decision, remembered: every manual choice offers to become a rule.
3. Simple surface, deep controls: wildcards up front, regex and flags behind a disclosure.
4. Trust is a feature: show plainly which rule fired and why.
