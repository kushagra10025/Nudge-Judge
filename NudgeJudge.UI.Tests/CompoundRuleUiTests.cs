using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NudgeJudge.Controls;
using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;
using NudgeJudge.Core.Rules;
using NudgeJudge.Services;
using NudgeJudge.ViewModels;
using NudgeJudge.Views;

namespace NudgeJudge.UI.Tests;

/// <summary>
/// Compound rules and the source app, driven through the real windows: the rule list leads with names,
/// the editor builds "link is A or B, and opened from C", Activity shows where links came from, the tester
/// and the chooser take the source into account.
/// </summary>
public sealed class CompoundRuleUiTests : IDisposable
{
    private static readonly SourceApp Teams = new("ms-teams.exe", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", "Microsoft Teams");
    private static readonly SourceApp Slack = new("slack", "/usr/lib/slack/slack", "Slack");

    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
    }

    // ---- The list ----

    [AvaloniaFact]
    public void Rule_rows_lead_with_the_name_and_summarise_the_conditions_underneath()
    {
        var app = new Harness(withYouTubeRules: true);
        var window = Show(app, AppPage.Rules);

        var name = VisibleTextBlocks(window).Single(t => t.Text == "YouTube from Teams");
        Assert.Equal(FontWeight.SemiBold, name.FontWeight);

        var summaries = window.GetVisualDescendants().OfType<ConditionSummary>().Where(s => s.IsEffectivelyVisible).ToList();
        Assert.Equal(2, summaries.Count);
        var first = InlineText(summaries[0]);
        Assert.Equal("www.youtube.com or youtu.be   and   from ms-teams.exe or Teams.exe", first);
        Assert.Equal("www.youtube.com or youtu.be", InlineText(summaries[1]));

        // Patterns and program names in mono; glyphs drawn inline.
        var runs = summaries[0].Inlines!.OfType<Run>().ToList();
        Assert.All(runs.Where(r => r.Text is "youtu.be" or "ms-teams.exe"),
            r => Assert.Contains("Mono", r.FontFamily.ToString()));
        var glyphs = summaries[0].Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).ToList();
        Assert.IsType<MatchKindGlyph>(glyphs[0]);
        Assert.IsType<Icon>(glyphs[1]);

        // The name sits above its conditions.
        Assert.True(name.TranslatePoint(default, window)!.Value.Y < summaries[0].TranslatePoint(default, window)!.Value.Y);
    }

    // ---- The editor ----

    [AvaloniaFact]
    public void Cloning_a_rule_marks_the_copy_as_a_duplicate_until_it_changes()
    {
        var app = new Harness(withYouTubeRules: true);
        var window = Show(app, AppPage.Rules);
        var rules = app.Main.Rules;

        // Cancel leaves no trace: the copy only exists once saved.
        Click(window, Finds<Button>(window, "Clone rule")[0]);
        Settle(window);
        Assert.True(rules.Editor.IsOpen);
        Assert.Equal("Clone rule", rules.Editor.Title);
        Assert.Equal("YouTube from Teams (copy)", rules.Editor.Name);
        Assert.Equal(2, app.Config.Current.Rules.Count);
        Click(window, Find<Button>(window, "Cancel", byContent: true));
        Settle(window);
        Assert.Equal(2, app.Config.Current.Rules.Count);

        // Saved, it sits right below its original.
        Click(window, Finds<Button>(window, "Clone rule")[0]);
        Settle(window);
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);
        Assert.Equal(["YouTube from Teams", "YouTube from Teams (copy)", "YouTube"], app.Config.Current.Rules.Select(r => r.Name));

        // Unchanged: a badge on the copy, and only on the copy.
        Assert.Equal([false, true, false], rules.Rules.Select(r => r.IsDuplicate));
        Assert.Contains(VisibleTextBlocks(window), t => t.Text == "Duplicate of 1");

        // The badge lights up the original.
        Click(window, Find<Button>(window, "Show original rule"));
        Settle(window);
        Assert.True(rules.Rules[0].IsHighlighted);
        Assert.False(rules.Rules[1].IsHighlighted);

        // Renaming alone doesn't make it different...
        Click(window, Finds<Button>(window, "Edit rule")[1]);
        Settle(window);
        Type(window, Find<TextBox>(window, "Rule name"), "Teams YouTube, again");
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);
        Assert.True(rules.Rules[1].IsDuplicate);

        // ...but changing what it does removes the badge for good.
        Click(window, Finds<Button>(window, "Edit rule")[1]);
        Settle(window);
        Find<ComboBox>(window, "Open in").SelectedIndex = 0; // Firefox instead of Chrome
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);
        Assert.False(rules.Rules[1].IsDuplicate);
        Assert.Null(app.Config.Current.Rules[1].ClonedFromId);
        Assert.DoesNotContain(VisibleTextBlocks(window), t => t.Text?.StartsWith("Duplicate of") == true);
    }

    [AvaloniaFact]
    public void A_browser_awaiting_review_is_explained_everywhere_and_fixed_from_activity()
    {
        var app = new Harness();
        var imported = new BrowserProfile { Name = "Work tools", ExecutablePath = "/home/me/open.sh", Arguments = "--no-sandbox", NeedsReview = true };
        app.Config.Update(c =>
        {
            c.Browsers.Add(imported);
            c.Rules.Add(new Rule { Name = "Tools", Conditions = [RuleCondition.Link(MatchKind.Domain, "tools.io")], BrowserId = imported.Id });
        });

        // The chooser says one is held back instead of silently leaving it out.
        Assert.Equal(2, app.Chooser.Browsers.Count);
        Assert.Equal("1 imported browser is hidden until you review it on the Browsers page.", app.Chooser.HiddenForReviewText);

        // Its link fails over to asking, and Activity offers the fix.
        app.Router.Route("https://tools.io/x");
        var window = Show(app, AppPage.Activity);
        var entry = app.Main.Activity.Entries.First(e => e.Outcome == ActivityOutcome.Failed);
        Assert.True(entry.CanReviewBrowser);
        Assert.False(entry.ShowCreateRule);
        Click(window, Find<Button>(window, "Review browser"));
        Settle(window);

        // The browser opens in its editor with the reasons listed and a save that says what it does.
        Assert.Equal(1, app.Main.SelectedPageIndex);
        var editor = app.Main.Browsers.Editor;
        Assert.Equal(imported.Id, editor.EditingId);
        Assert.Equal("Save as reviewed", editor.SaveLabel);
        Assert.Contains("--no-sandbox", editor.RiskyArguments);
        Assert.Contains(editor.ReviewWarning!, r => r.Contains("script"));
        Assert.Equal("Needs review: runs a script · not installed · risky arguments",
            app.Main.Browsers.Browsers.Single(b => b.Id == imported.Id).ReviewSummary);

        Click(window, Find<Button>(window, "Save as reviewed", byContent: true));
        Settle(window);
        Assert.False(app.Config.Current.FindBrowser(imported.Id)!.NeedsReview);
        Assert.False(entry.CanReviewBrowser);
        Assert.Equal(3, app.Chooser.Browsers.Count);
        Assert.False(app.Chooser.HasHiddenForReview);
    }

    [AvaloniaFact]
    public void A_time_value_is_read_back_so_an_overnight_9_to_5_shows()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);
        Click(window, Find<Button>(window, "Add time condition"));
        Settle(window);
        Type(window, Finds<TextBox>(window, "Pattern")[1], "9-5");

        Assert.Contains(VisibleTextBlocks(window), t => t.Text == "Every day, 09:00 to 05:00 the next day (overnight)");
        Type(window, Finds<TextBox>(window, "Pattern")[1], "Funday 9-17");
        Assert.DoesNotContain(VisibleTextBlocks(window), t => t.Text?.StartsWith("Every day") == true);
        Assert.Contains(VisibleTextBlocks(window), t => t.Text?.StartsWith("Couldn't read the days") == true);
    }

    [AvaloniaFact]
    public void Building_a_time_condition_in_the_editor()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);

        Type(window, Find<TextBox>(window, "Rule name"), "GitHub at work");
        Type(window, Finds<TextBox>(window, "Pattern")[0], "github.com");
        Click(window, Find<Button>(window, "Add time condition"));
        Settle(window);
        var patterns = Finds<TextBox>(window, "Pattern");
        Assert.Equal(2, patterns.Count); // the time is typed into a plain box
        Type(window, patterns[1], "Mon-Fri 09:00-17:00");

        Find<ComboBox>(window, "Open in").SelectedIndex = 1;
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);

        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.Equal(ConditionField.Time, rule.Conditions[1].Field);
        Assert.Equal(["Mon-Fri 09:00-17:00"], rule.Conditions[1].Values);
        var summary = window.GetVisualDescendants().OfType<ConditionSummary>().Single(s => s.IsEffectivelyVisible);
        Assert.Equal("github.com   and   during Mon-Fri 09:00-17:00", InlineText(summary));
    }

    [AvaloniaFact]
    public void Building_youtube_or_youtu_be_from_teams_in_the_editor()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);
        var editor = app.Main.Rules.Editor;
        Assert.True(editor.IsOpen);

        Type(window, Find<TextBox>(window, "Rule name"), "YouTube from Teams");
        Find<ComboBox>(window, "Match type").SelectedIndex = 1; // Exact domain
        Settle(window);
        Type(window, Finds<TextBox>(window, "Pattern")[0], "www.youtube.com");

        Click(window, Find<Button>(window, "Or another pattern", byContent: true));
        Settle(window);
        var patterns = Finds<TextBox>(window, "Pattern");
        Assert.Equal(2, patterns.Count);
        Type(window, patterns[1], "youtu.be");

        Click(window, Find<Button>(window, "Add source app condition"));
        Settle(window);
        Type(window, Find<AutoCompleteBox>(window, "Source app"), "ms-teams.exe");

        // Two conditions: the join choice appears and the track says "and".
        Assert.True(editor.ShowJoin);
        Assert.Contains(VisibleTextBlocks(window), t => t.Text == "and");

        Find<ComboBox>(window, "Open in").SelectedIndex = 1; // Chrome
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);

        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.Equal("YouTube from Teams", rule.Name);
        Assert.Equal(RuleJoin.All, rule.Join);
        Assert.Equal(ConditionField.Link, rule.Conditions[0].Field);
        Assert.Equal(MatchKind.Domain, rule.Conditions[0].MatchKind);
        Assert.Equal(["www.youtube.com", "youtu.be"], rule.Conditions[0].Values);
        Assert.Equal(ConditionField.SourceApp, rule.Conditions[1].Field);
        Assert.Equal(["ms-teams.exe"], rule.Conditions[1].Values);
        Assert.Contains(VisibleTextBlocks(window), t => t.Text == "YouTube from Teams");

        // And it routes on both.
        app.Router.Route("https://youtu.be/x", Teams);
        Assert.Equal(app.Chrome, app.Launcher.Launched.Single().Browser);
        Assert.Equal(RouteKind.NeedsChoice, app.Router.Route("https://youtu.be/x", Slack).Kind);
    }

    [AvaloniaFact]
    public void Building_nested_groups_in_the_editor()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);
        var editor = app.Main.Rules.Editor;

        // acme.io links, and (under /admin, or from Teams, or (under /docs and from Slack)).
        Type(window, Finds<TextBox>(window, "Pattern")[0], "*.acme.io");
        Click(window, Find<Button>(window, "Add condition group"));
        Settle(window);

        // A new group starts with a blank link condition and the opposite join to its parent.
        var group = Assert.IsType<ConditionGroupViewModel>(editor.Conditions[1]);
        Assert.Equal(RuleJoin.Any, group.Join.Join);
        Assert.Equal(2, Finds<TextBox>(window, "Pattern").Count);
        Assert.False(editor.SaveCommand.CanExecute(null)); // the blank pattern isn't filled in yet
        Type(window, Finds<TextBox>(window, "Pattern")[1], "*.acme.io/admin/*");

        Click(window, Find<Button>(window, "Add source app condition to this group"));
        Settle(window);
        Type(window, Find<AutoCompleteBox>(window, "Source app"), "ms-teams.exe");

        Click(window, Find<Button>(window, "Add condition group inside this group"));
        Settle(window);
        var inner = Assert.IsType<ConditionGroupViewModel>(group.Items[2]);
        Assert.Equal(RuleJoin.All, inner.Join.Join);
        Type(window, Finds<TextBox>(window, "Pattern")[2], "*.acme.io/docs/*");
        // The inner group's buttons come first: they sit above the outer group's foot.
        Click(window, Finds<Button>(window, "Add source app condition to this group")[0]);
        Settle(window);
        Type(window, Finds<AutoCompleteBox>(window, "Source app")[1], "slack");

        // Three levels is the limit: the innermost group offers no further group.
        Assert.Single(Finds<Button>(window, "Add condition group inside this group"));
        Assert.Equal(2, Finds<ComboBox>(window, "Match all or any in this group").Count);

        // Each track says its own join word.
        var joins = VisibleTextBlocks(window).Select(t => t.Text).Where(t => t is "and" or "or").ToList();
        Assert.Equal(["and", "or", "or", "and"], joins);

        Find<ComboBox>(window, "Open in").SelectedIndex = 1; // Chrome
        Settle(window);
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);

        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.Equal("acme.io", rule.Name);
        var saved = rule.Conditions[1];
        Assert.True(saved.IsGroup);
        Assert.Equal(3, saved.Conditions!.Count);
        Assert.Equal(["slack"], saved.Conditions[2].Conditions![1].Values);

        // The list summarises it with brackets.
        var summary = window.GetVisualDescendants().OfType<ConditionSummary>().Single(s => s.IsEffectivelyVisible);
        Assert.Equal("*.acme.io   and   (*.acme.io/admin/*  or  from ms-teams.exe  or  (*.acme.io/docs/*  and  from slack))", InlineText(summary));

        // And it routes that way.
        app.Router.Route("https://app.acme.io/admin/x", null);
        app.Router.Route("https://app.acme.io/x", Teams);
        app.Router.Route("https://app.acme.io/docs/a", Slack);
        Assert.Equal(3, app.Launcher.Launched.Count(l => l.Browser == app.Chrome));
        Assert.Equal(RouteKind.NeedsChoice, app.Router.Route("https://app.acme.io/x", Slack).Kind);

        // Reopening it brings the groups back, and removing a group takes its conditions with it.
        editor.OpenExisting(rule);
        Settle(window);
        Assert.Equal(3, Finds<TextBox>(window, "Pattern").Count);
        Click(window, Finds<Button>(window, "Remove group")[1]);
        Settle(window);
        Assert.Equal(2, Finds<TextBox>(window, "Pattern").Count);
        Assert.Single(Finds<AutoCompleteBox>(window, "Source app"));
        Assert.Equal(2, Assert.IsType<ConditionGroupViewModel>(editor.Conditions[1]).Items.Count);
    }

    [AvaloniaFact]
    public void Also_catch_in_the_browser_is_saved_and_warns_when_the_rule_has_no_sites()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);
        var editor = app.Main.Rules.Editor;

        Type(window, Finds<TextBox>(window, "Pattern")[0], "*.zoom.us");
        var box = Find<CheckBox>(window, "Also catch in the browser", byContent: true);
        Click(window, box);
        Settle(window);
        Assert.True(editor.CatchInBrowser);
        Assert.False(editor.ShowCatchWarning);

        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);
        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.True(rule.CatchInBrowser);

        // Reopened, it's still ticked; a regex can't be narrowed to sites, so it says so.
        editor.OpenExisting(rule);
        Settle(window);
        Assert.True(editor.CatchInBrowser);
        rule.Conditions[0].MatchKind = MatchKind.Regex;
        rule.Conditions[0].Values = ["^https://zoom"];
        editor.OpenExisting(rule);
        Settle(window);
        editor.RefreshTestVerdict();
        Assert.True(editor.ShowCatchWarning);
        Assert.Contains(VisibleTextBlocks(window), t => t.Text?.StartsWith("Only works with domain") == true);
    }

    [AvaloniaFact]
    public void Conditions_and_groups_reorder_with_arrows_shown_on_hover()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);
        var editor = app.Main.Rules.Editor;

        // Alone in the rule, a condition has nothing to move.
        Hover(window, VisibleTextBlocks(window).First(t => t.Text == "Link"));
        Assert.Empty(Finds<Button>(window, "Move condition down"));

        Type(window, Finds<TextBox>(window, "Pattern")[0], "*.acme.io");
        Click(window, Find<Button>(window, "Add source app condition"));
        Settle(window);
        Type(window, Find<AutoCompleteBox>(window, "Source app"), "slack");
        Click(window, Find<Button>(window, "Add condition group"));
        Settle(window);

        // Arrows are hidden at rest, appear over a condition, and the first can't move up.
        Hover(window, Find<ComboBox>(window, "Match all or any conditions"));
        Assert.Empty(Finds<Button>(window, "Move condition down"));
        Hover(window, Finds<TextBox>(window, "Pattern")[0]);
        var down = Assert.Single(Finds<Button>(window, "Move condition down"));
        Assert.False(Assert.Single(Finds<Button>(window, "Move condition up")).IsEnabled);

        Click(window, down);
        Settle(window);
        Assert.Equal(["SourceApp", "Link", "Group"], editor.Conditions.Select(c => c is ConditionEditorViewModel e ? e.Field.ToString() : "Group"));
        Assert.Equal("and", editor.Conditions[1].JoinLabel); // join words follow the new order
        Assert.False(editor.Conditions[0].ShowJoinLabel);

        // A group moves as a whole, from its header.
        Hover(window, Find<ComboBox>(window, "Match all or any in this group"));
        Assert.Empty(Finds<Button>(window, "Move condition up")); // the group's own condition is alone
        Click(window, Assert.Single(Finds<Button>(window, "Move group up")));
        Settle(window);
        Assert.IsType<ConditionGroupViewModel>(editor.Conditions[1]);

        // Hints are tooltips on the fields, never inline text.
        Assert.DoesNotContain(VisibleTextBlocks(window), t => t.Text?.StartsWith("Use * for any text") == true);
        Assert.StartsWith("Use * for any text", ToolTip.GetTip(Finds<TextBox>(window, "Pattern")[0]) as string);

        Find<ComboBox>(window, "Open in").SelectedIndex = 1;
        Type(window, Finds<TextBox>(window, "Pattern")[0], "github.com/acme/*"); // the group, now above the link
        Click(window, Find<Button>(window, "Save rule", byContent: true));
        Settle(window);
        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.Equal([ConditionField.SourceApp, ConditionField.Group, ConditionField.Link], rule.Conditions.Select(c => c.Field));
    }

    [AvaloniaFact]
    public void The_name_is_required()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);

        Type(window, Finds<TextBox>(window, "Pattern")[0], "*.acme.io");
        // A name is suggested from the pattern so the field never starts empty...
        Assert.Equal("acme.io", Find<TextBox>(window, "Rule name").Text);
        var save = Find<Button>(window, "Save rule", byContent: true);
        Assert.True(save.IsEffectivelyEnabled);

        // ...but clearing it blocks saving and says why.
        Type(window, Find<TextBox>(window, "Rule name"), "");
        Assert.False(save.IsEffectivelyEnabled);
        Assert.Contains(VisibleTextBlocks(window), t => t.Text?.StartsWith("Give the rule a name") == true);

        // Once named by hand, editing the pattern no longer renames it.
        Type(window, Find<TextBox>(window, "Rule name"), "Work tracker");
        Type(window, Finds<TextBox>(window, "Pattern")[0], "*.atlassian.net");
        Assert.Equal("Work tracker", app.Main.Rules.Editor.Name);
        Assert.True(save.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Switching_to_any_relabels_the_track_and_bad_alternatives_show_on_their_own_field()
    {
        var app = new Harness();
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Add rule", byContent: true));
        Settle(window);
        Type(window, Finds<TextBox>(window, "Pattern")[0], "*.notion.so");
        Click(window, Find<Button>(window, "Add source app condition"));
        Settle(window);
        Type(window, Find<AutoCompleteBox>(window, "Source app"), "slack");

        Find<ComboBox>(window, "Match all or any conditions").SelectedIndex = 1;
        Settle(window);
        Assert.Contains(VisibleTextBlocks(window), t => t.Text == "or" && t.FontWeight == FontWeight.SemiBold);

        Find<ComboBox>(window, "Match type").SelectedIndex = 1; // Exact domain: * isn't allowed
        Settle(window);
        var error = VisibleTextBlocks(window).Single(t => t.Classes.Contains("error"));
        Assert.StartsWith("Domain rules don't take wildcards", error.Text);
        Assert.False(Find<Button>(window, "Save rule", byContent: true).IsEffectivelyEnabled);
    }

    // ---- Activity ----

    [AvaloniaFact]
    public void Activity_shows_which_app_opened_each_link_and_offers_it_for_a_rule()
    {
        var app = new Harness(withYouTubeRules: true);
        app.Router.Route("https://youtu.be/abc", Teams);
        app.Router.Route("https://news.example.org/a", Slack);
        var window = Show(app, AppPage.Activity);

        var sources = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => AutomationProperties.GetName(t) == "Opened from" && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(["Slack", "Microsoft Teams"], sources);
        Assert.Contains(VisibleTextBlocks(window), t => t.Inlines?.OfType<Run>().Any(r => r.Text == "YouTube from Teams") == true);

        // Filtering finds links by app.
        app.Main.Activity.Filter = "teams";
        Settle(window);
        Assert.Single(app.Main.Activity.Entries);

        // "Create rule" on the Slack link: the editor knows the app, one click away.
        app.Main.Activity.Filter = "";
        Settle(window);
        Click(window, window.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Create rule from this link" && b.IsEffectivelyVisible));
        Settle(window);

        var editor = app.Main.Rules.Editor;
        Assert.True(editor.IsOpen);
        Assert.Single(editor.Conditions); // link only until asked
        Assert.Equal("slack", app.Main.Rules.TestSource);

        Click(window, Find<Button>(window, "Add source app condition"));
        Settle(window);
        Assert.Equal("slack", Find<AutoCompleteBox>(window, "Source app").Text);
        Assert.Equal("example.org from Slack", editor.Name);
        Assert.Equal("Matches the test link from Slack", editor.TestVerdict);
        Assert.Contains("ms-teams.exe", editor.KnownApps);
    }

    // ---- Tester ----

    [AvaloniaFact]
    public void The_tester_checks_rules_against_the_source_app()
    {
        var app = new Harness(withYouTubeRules: true);
        var window = Show(app, AppPage.Rules);
        Click(window, Find<Button>(window, "Test a link", byContent: true));
        Settle(window);

        Type(window, Find<TextBox>(window, "Test link"), "https://youtu.be/xyz");
        Assert.Equal("YouTube (rule 2) opens this in Firefox", app.Main.Rules.TestHeadline);

        Type(window, Find<AutoCompleteBox>(window, "Test source app"), "ms-teams.exe");
        Assert.Equal("YouTube from Teams (rule 1) opens this in Chrome", app.Main.Rules.TestHeadline);
        Assert.True(app.Main.Rules.Rules[0].IsTestMatch);
        Assert.False(app.Main.Rules.Rules[1].IsTestMatch);
    }

    // ---- Chooser ----

    [AvaloniaFact]
    public void The_chooser_can_save_a_rule_for_links_from_that_app_only()
    {
        var app = new Harness();
        app.Router.Route("https://jira.acme.io/browse/X-1", Teams); // no rules: asked
        var chooser = app.Chooser;
        var window = new ChooserWindow { DataContext = chooser };
        _windows.Add(window);
        window.Show();
        Settle(window);

        Assert.Contains(VisibleTextBlocks(window), t => t.Text == "No rule matched this link from Microsoft Teams");
        var only = Find<CheckBox>(window, "Only links from this app");
        Assert.False(only.IsEffectivelyVisible); // only when saving a rule

        chooser.Remember = chooser.RememberOptions.Single(o => o.Kind == RememberKind.Always);
        Settle(window);
        Assert.True(only.IsEffectivelyVisible);
        Assert.Equal("Only links from Microsoft Teams", only.Content);
        Click(window, only);
        Settle(window);

        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Chrome"));
        Settle(window);

        var rule = Assert.Single(app.Config.Current.Rules);
        Assert.Equal("acme.io from Microsoft Teams", rule.Name);
        Assert.Equal(["*.acme.io"], rule.Conditions[0].Values);
        Assert.Equal(["ms-teams.exe"], rule.Conditions[1].Values);
        Assert.Equal(Teams, app.Log.Snapshot()[^1].Source); // the open is logged with its source too

        // The saved rule now routes Teams links, and only those.
        Assert.Equal(RouteKind.Launched, app.Router.Route("https://wiki.acme.io/", Teams).Kind);
        Assert.Equal(RouteKind.NeedsChoice, app.Router.Route("https://wiki.acme.io/", Slack).Kind);
    }

    // ---- Older configs ----

    [AvaloniaFact]
    public void Rules_from_an_older_config_show_their_pattern_as_the_name()
    {
        var dir = Directory.CreateTempSubdirectory("nj-ui-upgrade-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json");
            var browser = Guid.NewGuid();
            File.WriteAllText(path, $$"""
                {
                  "version": 1,
                  "browsers": [ { "id": "{{browser}}", "name": "Firefox", "executablePath": "firefox" } ],
                  "rules": [ { "name": "", "matchKind": "Wildcard", "pattern": "*.atlassian.net", "browserId": "{{browser}}" } ]
                }
                """);
            var app = new Harness(config: new JsonConfigStore(path));
            var window = Show(app, AppPage.Rules);

            var row = app.Main.Rules.Rules.Single();
            Assert.Equal("*.atlassian.net", row.Name);
            Assert.Contains(VisibleTextBlocks(window), t => t.Text == "*.atlassian.net" && t.FontWeight == FontWeight.SemiBold);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Helpers ----

    private sealed class Harness
    {
        public Harness(bool withYouTubeRules = false, IConfigStore? config = null)
        {
            Config = config ?? new MemoryConfigStore();
            if (config is null) Config.Current.Browsers.AddRange([Firefox, Chrome]);
            if (withYouTubeRules)
            {
                Config.Current.Rules.Add(new Rule
                {
                    Name = "YouTube from Teams",
                    Conditions = [RuleCondition.Link(MatchKind.Domain, "www.youtube.com", "youtu.be"), RuleCondition.Source("ms-teams.exe", "Teams.exe")],
                    BrowserId = Chrome.Id,
                });
                Config.Current.Rules.Add(new Rule
                {
                    Name = "YouTube",
                    Conditions = [RuleCondition.Link(MatchKind.Domain, "www.youtube.com", "youtu.be")],
                    BrowserId = Firefox.Id,
                });
            }

            var dispatcher = new InlineDispatcher();
            var boot = new FakeBoot();
            var engine = new RuleEngine(boot);
            Log = new ActivityLog();
            Router = new LinkRouter(Config, engine, Launcher, Log);
            var windows = new FakeWindows();
            Chooser = new ChooserViewModel(Config, Router, boot, dispatcher);
            Chooser.Attach(windows);
            Router.ChoiceRequested += (_, link) => Chooser.Enqueue(link.Url, link.Source);
            var settings = new SettingsViewModel(Config, new FakeRegistrar(), new NoTheme(), dispatcher, new FakeStartup());
            Main = new MainWindowViewModel(
                new RulesViewModel(Config, engine, boot, Chooser, windows, dispatcher, settings),
                new BrowsersViewModel(Config, new NoDiscovery(), Router, windows, dispatcher),
                new ActivityViewModel(Log, dispatcher, settings, windows),
                settings,
                Config);
            windows.Main = Main;
        }

        public IConfigStore Config { get; }
        public BrowserProfile Firefox { get; } = new() { Name = "Firefox", ExecutablePath = "/usr/bin/firefox" };
        public BrowserProfile Chrome { get; } = new() { Name = "Chrome", ExecutablePath = "/usr/bin/chrome" };
        public FakeLauncher Launcher { get; } = new();
        public ActivityLog Log { get; }
        public LinkRouter Router { get; }
        public ChooserViewModel Chooser { get; }
        public MainWindowViewModel Main { get; }
    }

    private Window Show(Harness app, AppPage page)
    {
        app.Main.Navigate(page);
        var window = new MainWindow { DataContext = app.Main, Width = 1280, Height = 900 };
        _windows.Add(window);
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<TextBlock> VisibleTextBlocks(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible);

    private static string InlineText(TextBlock block) =>
        string.Concat(block.Inlines!.OfType<Run>().Select(r => r.Text)).Trim();

    private static List<T> Finds<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == name).ToList();

    /// <summary>A visible control by automation name, or (for buttons) by the text it shows.</summary>
    private static T Find<T>(Window window, string name, bool byContent = false) where T : Control
    {
        var all = window.GetVisualDescendants().OfType<T>();
        return byContent
            ? all.First(c => c.IsEffectivelyVisible && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == name) ||
                             c is ContentControl { Content: string s } && s == name && c.IsEffectivelyVisible)
            : all.Where(c => AutomationProperties.GetName(c) == name).OrderByDescending(c => c.IsEffectivelyVisible).First();
    }

    /// <summary>Focuses the field, selects what's there and types over it, as a user would.</summary>
    private static void Type(Window window, Control field, string text)
    {
        var box = field as TextBox ?? field.GetVisualDescendants().OfType<TextBox>().First();
        box.Focus();
        box.SelectAll();
        if (text.Length == 0) window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        else window.KeyTextInput(text);
        Settle(window);
    }

    private static void Hover(Window window, Control control)
    {
        control.BringIntoView();
        Settle(window);
        window.MouseMove(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value);
        Settle(window);
    }

    /// <summary>Hovers first: row actions only appear on hover.</summary>
    private static void Click(Window window, Control control)
    {
        // Tall editors scroll: bring the target on screen first, as a user would.
        control.BringIntoView();
        Settle(window);
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }
}
