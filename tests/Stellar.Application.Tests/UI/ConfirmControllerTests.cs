using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>ConfirmController (fw 2.33.0): the modal confirm's state machine — open/confirm/cancel, callbacks after
/// close, replacement, and "Don't ask again" (persisted in the plugin's own config section, else session-only).</summary>
public sealed class ConfirmControllerTests
{
    private sealed class Section : IConfigSection
    {
        public readonly Dictionary<string, object?> Values = new();
        public int Saves;
        public T? Get<T>(string key, T? defaultValue) => Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => Values[key] = value;
        public void Save() => Saves++;
        public void SaveQuiet() => Saves++;
        public void RemoveByPrefix(string prefix) { }
    }

    private static ConfirmRequest Req(List<string> log, string id = "a", string? key = null) =>
        new($"T{id}", "m", () => log.Add("ok:" + id)) { OnCancel = () => log.Add("cancel:" + id), DontAskAgainKey = key };

    [Fact]
    public void ShowOpensAndConfirmClosesThenRunsOnConfirm()
    {
        var log = new List<string>();
        var c = new ConfirmController();
        ConfirmRequest? seenInCallback = null;
        var r = new ConfirmRequest("t", "m", () => { log.Add("ok"); seenInCallback = c.Current; });
        Assert.True(c.Show(r));
        Assert.True(c.IsOpen);
        Assert.Same(r, c.Current);
        c.Confirm();
        Assert.False(c.IsOpen);
        Assert.Null(seenInCallback);   // closed BEFORE the callback (it may open the next confirm)
        Assert.Equal(new[] { "ok" }, log);
    }

    [Fact]
    public void CancelRunsOnCancelOnceAndConfirmAfterIsANoOp()
    {
        var log = new List<string>();
        var c = new ConfirmController();
        c.Show(Req(log));
        c.Cancel();
        c.Cancel();
        c.Confirm();
        Assert.Equal(new[] { "cancel:a" }, log);
    }

    [Fact]
    public void ANewRequestCancelsTheOpenOne()
    {
        var log = new List<string>();
        var c = new ConfirmController();
        c.Show(Req(log, "a"));
        c.Show(Req(log, "b"));
        c.Confirm();
        Assert.Equal(new[] { "cancel:a", "ok:b" }, log);
    }

    [Fact]
    public void DontAskTickedOnConfirmPersistsAndNextShowRunsAtOnce()
    {
        var log = new List<string>();
        var store = new Section();
        var c = new ConfirmController { DontAskStore = store };
        Assert.True(c.Show(Req(log, "a", key: "untick")));
        Assert.False(c.DontAskChecked);   // off when the dialog opens
        c.DontAskChecked = true;
        c.Confirm();
        Assert.True(store.Get("confirm.dontAsk.untick", false));
        Assert.Equal(1, store.Saves);

        var again = new ConfirmController { DontAskStore = store };   // a later session reads the same plugin section
        Assert.False(again.Show(Req(log, "b", key: "untick")));
        Assert.False(again.IsOpen);
        Assert.Equal(new[] { "ok:a", "ok:b" }, log);
    }

    [Fact]
    public void DontAskTickedThenCancelledIsNotStored()
    {
        var log = new List<string>();
        var c = new ConfirmController();
        c.Show(Req(log, "a", key: "k"));
        c.DontAskChecked = true;
        c.Cancel();
        Assert.False(c.IsDontAsk("k"));
        Assert.True(c.Show(Req(log, "b", key: "k")));
        Assert.False(c.DontAskChecked);   // reset for the new request
    }

    [Fact]
    public void WithoutAKeyTheCheckboxIsIgnored()
    {
        var c = new ConfirmController();
        c.Show(new ConfirmRequest("t", "m", () => { }));
        c.DontAskChecked = true;
        c.Confirm();
        Assert.True(c.Show(new ConfirmRequest("t", "m", () => { })));
    }

    [Fact]
    public void SessionOnlyWithoutAStoreAndSetDontAskFalseClearsIt()
    {
        var c = new ConfirmController();
        c.SetDontAsk("k", true);
        Assert.True(c.IsDontAsk("k"));
        Assert.False(new ConfirmController().IsDontAsk("k"));   // no store → not shared, not persisted
        c.SetDontAsk("k", false);
        Assert.False(c.IsDontAsk("k"));
    }
}
