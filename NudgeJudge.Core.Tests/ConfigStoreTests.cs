using NudgeJudge.Core.Config;
using NudgeJudge.Core.Models;
using NudgeJudge.Core.Routing;

namespace NudgeJudge.Core.Tests;

/// <summary>Persistence guarantees of <see cref="JsonConfigStore"/> and <see cref="ActivityLogFile"/>.</summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nj-store-").FullName;

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Rule NamedRule(string name) =>
        new() { Name = name, Conditions = [RuleCondition.Link(MatchKind.Domain, name + ".com")] };

    // ---- Two processes sharing one file ----

    [Fact]
    public void An_update_keeps_what_another_process_saved_in_the_meantime()
    {
        var primary = new JsonConfigStore(ConfigPath);
        var standalone = new JsonConfigStore(ConfigPath);

        standalone.Update(c => c.Rules.Add(NamedRule("from-chooser")));
        primary.Update(c => c.Settings.RoutingEnabled = !c.Settings.RoutingEnabled);

        var onDisk = new JsonConfigStore(ConfigPath).Current;
        Assert.Equal(["from-chooser"], onDisk.Rules.Select(r => r.Name));
        Assert.Equal(primary.Current.Settings.RoutingEnabled, onDisk.Settings.RoutingEnabled);
        Assert.Equal(["from-chooser"], primary.Current.Rules.Select(r => r.Name));
    }

    [Fact]
    public void Deleting_by_id_after_a_reload_removes_the_right_rule()
    {
        var primary = new JsonConfigStore(ConfigPath);
        primary.Update(c => c.Rules.AddRange([NamedRule("a"), NamedRule("b")]));
        var b = primary.Current.Rules[1].Id;

        // Another process puts a rule in front, shifting every index.
        new JsonConfigStore(ConfigPath).Update(c => c.Rules.Insert(0, NamedRule("temp")));
        primary.Update(c => c.Rules.RemoveAll(r => r.Id == b));

        Assert.Equal(["temp", "a"], new JsonConfigStore(ConfigPath).Current.Rules.Select(r => r.Name));
    }

    [Fact]
    public void Concurrent_updates_from_many_stores_are_all_kept()
    {
        new JsonConfigStore(ConfigPath);
        // Two writers, as in a primary plus one standalone launch. Every write fsyncs, so dozens would queue past the lock timeout.
        var stores = Enumerable.Range(0, 2).Select(_ => new JsonConfigStore(ConfigPath)).ToList();

        Parallel.For(0, 20, i => stores[i % stores.Count].Update(c => c.Rules.Add(NamedRule("r" + i))));

        Assert.Equal(20, new JsonConfigStore(ConfigPath).Current.Rules.Count);
    }

    [Fact]
    public void Refresh_picks_up_another_process_and_says_so_only_when_something_changed()
    {
        // The tray host only reads; the UI process writes.
        var host = new JsonConfigStore(ConfigPath);
        var changes = 0;
        host.Changed += (_, _) => changes++;

        Assert.False(host.Refresh());
        new JsonConfigStore(ConfigPath).Update(c => c.Rules.Add(NamedRule("from-ui")));

        Assert.True(host.Refresh());
        Assert.Equal(["from-ui"], host.Current.Rules.Select(r => r.Name));
        Assert.False(host.Refresh());
        Assert.Equal(1, changes);
    }

    // ---- Failures ----

    [Fact]
    public void A_failed_save_never_overwrites_what_another_process_saved_later()
    {
        // The tray host's toggle fails to save (file locked by a backup tool, say), then the window saves rules.
        var host = new JsonConfigStore(ConfigPath);
        var window = new JsonConfigStore(ConfigPath);
        File.Delete(ConfigPath);
        Directory.CreateDirectory(ConfigPath);
        host.Update(c => c.Settings.RoutingEnabled = false);
        Assert.NotNull(host.SaveError);
        Directory.Delete(ConfigPath);
        window.Update(c => c.Rules.Add(NamedRule("edited-in-window")));

        // The host routes with the window's rules, and its next save keeps them.
        Assert.True(host.Refresh());
        Assert.Equal(["edited-in-window"], host.Current.Rules.Select(r => r.Name));
        Assert.Null(host.SaveError);
        host.Update(c => c.Settings.TemporaryRuleHours = 4);

        var onDisk = new JsonConfigStore(ConfigPath).Current;
        Assert.Equal(["edited-in-window"], onDisk.Rules.Select(r => r.Name));
        Assert.Equal(4, onDisk.Settings.TemporaryRuleHours);
    }

    [Fact]
    public void A_failed_save_keeps_the_change_reports_it_and_retries()
    {
        var store = new JsonConfigStore(ConfigPath);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        // A directory where the file should be makes the rename fail on every platform.
        File.Delete(ConfigPath);
        Directory.CreateDirectory(ConfigPath);
        store.Update(c => c.Rules.Add(NamedRule("kept")));

        Assert.NotNull(store.SaveError);
        Assert.Equal(["kept"], store.Current.Rules.Select(r => r.Name));
        Assert.Equal(1, changes);

        Directory.Delete(ConfigPath);
        store.Update(c => c.Settings.RunInBackground = true);

        Assert.Null(store.SaveError);
        Assert.Equal(["kept"], new JsonConfigStore(ConfigPath).Current.Rules.Select(r => r.Name));
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_never_overwritten_by_the_next_failure()
    {
        File.WriteAllText(ConfigPath, "{ not json");
        var first = new JsonConfigStore(ConfigPath);
        Assert.Contains("kept as config.json.broken-", first.LoadError);

        first.Update(c => c.Settings.RunInBackground = true);
        Assert.Null(first.SaveError);

        var backups = Directory.GetFiles(_dir, "config.json.broken-*");
        Assert.Single(backups);
        Assert.Equal("{ not json", File.ReadAllText(backups[0]));
    }

    // ---- Versions ----

    [Fact]
    public void Upgrading_an_old_file_backs_it_up_and_saves_the_new_shape()
    {
        var old = """{ "version": 1, "rules": [ { "name": "", "matchKind": "Wildcard", "pattern": "*.acme.io" } ] }""";
        File.WriteAllText(ConfigPath, old);

        var store = new JsonConfigStore(ConfigPath);

        Assert.Equal(old, File.ReadAllText(ConfigPath + ".v1.bak"));
        var json = File.ReadAllText(ConfigPath);
        Assert.Contains($"\"version\": {AppConfig.CurrentVersion}", json);
        Assert.DoesNotContain("\"pattern\"", json);
        Assert.Equal("*.acme.io", store.Current.Rules[0].Name);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_never_overwritten()
    {
        var newer = $$"""{ "version": {{AppConfig.CurrentVersion + 1}}, "futureThing": { "x": 1 }, "rules": [] }""";
        File.WriteAllText(ConfigPath, newer);

        var store = new JsonConfigStore(ConfigPath);
        Assert.Contains("newer version", store.LoadError);

        store.Update(c => c.Settings.RoutingEnabled = false);

        Assert.False(store.Current.Settings.RoutingEnabled);
        Assert.NotNull(store.SaveError);
        Assert.Equal(newer, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_current_file_is_not_rewritten_or_backed_up_on_load()
    {
        var store = new JsonConfigStore(ConfigPath);
        store.Update(c => c.Rules.Add(NamedRule("a")));
        var written = File.GetLastWriteTimeUtc(ConfigPath);

        _ = new JsonConfigStore(ConfigPath);

        Assert.Equal(written, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    // ---- Activity log ----

    [Fact]
    public void Activity_updates_replace_the_file_whole_and_leave_no_temp_files()
    {
        var path = Path.Combine(_dir, "activity.log");
        var log = new ActivityLogFile(path);
        File.WriteAllText(path, "");

        log.Update(l => l.Add(new ActivityEntry(DateTimeOffset.UtcNow, "https://a.example/", ActivityOutcome.Routed)));
        log.Update(l => l.Add(new ActivityEntry(DateTimeOffset.UtcNow, "https://b.example/", ActivityOutcome.Asked)));

        Assert.Equal(["https://a.example/", "https://b.example/"], log.Read().Select(e => e.Url));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Concurrent_activity_writers_lose_nothing()
    {
        var path = Path.Combine(_dir, "activity.log");
        var writers = Enumerable.Range(0, 2).Select(_ => new ActivityLogFile(path)).ToList();

        // Two at a time, like the real app (running instance + one launch); more queues past the lock timeout under load.
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i => writers[i % writers.Count]
            .Update(l => l.Add(new ActivityEntry(DateTimeOffset.UtcNow, $"https://{i}.example/", ActivityOutcome.Routed))));

        Assert.Equal(20, new ActivityLogFile(path).Read().Count);
    }

    [Fact]
    public async Task A_waiting_writer_gets_a_turn_between_another_writers_back_to_back_holds()
    {
        // The holder releases and immediately takes the lock again, as a burst of links does. A waiter that only
        // polls never lands in that gap; it has to be let in.
        var path = Path.Combine(_dir, "burst.lock");
        using var stop = new CancellationTokenSource();
        using var hogging = new ManualResetEventSlim();
        var hog = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using (FileLock.Acquire(path))
                {
                    hogging.Set();
                    Thread.Sleep(40);
                }
            }
        });
        try
        {
            Assert.True(hogging.Wait(TimeSpan.FromSeconds(5)));
            using (FileLock.Acquire(path, TimeSpan.FromSeconds(1))) { }
        }
        finally
        {
            stop.Cancel();
            await hog;
        }
    }
}
