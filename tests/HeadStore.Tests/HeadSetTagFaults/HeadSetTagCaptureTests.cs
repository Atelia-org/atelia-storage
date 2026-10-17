using Atelia.HeadStore.Tests.ConditionalUpdateFaults;
using Xunit;

namespace Atelia.HeadStore.Tests.HeadSetTagFaults;

public sealed class HeadSetTagCaptureTests {
    [Fact]
    public void EveryMutationRejectsReentryWhileSelectingHeadsAndGuardEndsAfterSuccess() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        int callbacks = 0;
        f.Hooks.AfterTagHeadSelected = _ => {
            callbacks++;
            var create = store.CreateHead([0xff], out var outcome);
            AssertRejected(create.Error, outcome);
            create = store.CreateHead(9, [0xff], out outcome);
            AssertRejected(create.Error, outcome);
            var publish = store.PublishHead(f.Id(2), [0xff], out outcome);
            AssertRejected(publish.Error, outcome);
            var batch = store.PublishHeads(f.Changes((1, [0xff]), (2, [0xff])), out outcome);
            AssertRejected(batch.Error, outcome);
            var fork = store.ForkHead(f.Initial[1], out outcome);
            AssertRejected(fork.Error, outcome);
            fork = store.ForkHead(9, f.Initial[1], out outcome);
            AssertRejected(fork.Error, outcome);
            var tag = store.CreateTag("nested", [f.Id(3)], out outcome);
            AssertRejected(tag.Error, outcome);
        };

        var captured = store.CreateTag("outer", [f.Id(2), f.Id(1)], out var outerOutcome).Unwrap();

        Assert.Equal(2, callbacks);
        Assert.Equal(PublicationOutcome.Confirmed, outerOutcome);
        Assert.Equal(f.Initial[1], captured.Entries[f.Id(1)]);
        Assert.Equal(f.Initial[2], captured.Entries[f.Id(2)]);
        f.Hooks.AfterTagHeadSelected = null;
        store.PublishHead(f.Id(2), [0x55], out var laterOutcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, laterOutcome);
        Assert.Equal("HeadStore.TagNotFound", store.ReadTag("nested").Error!.ErrorCode);
    }

    [Theory]
    [InlineData("selected", PublicationOutcome.NotAttempted)]
    [InlineData("clock", PublicationOutcome.NotAttempted)]
    [InlineData("dependencies", PublicationOutcome.NotAttempted)]
    [InlineData("append", PublicationOutcome.Unknown)]
    [InlineData("confirmed", PublicationOutcome.Confirmed)]
    public void DisposeAtAUserExecutableHookStopsBeforeFurtherIo(string stage, PublicationOutcome expected) {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        int eventsAfterDispose = -1;
        int selected = 0;
        bool laterHook = false;
        void Stop() {
            store.Dispose();
            eventsAfterDispose = f.Events.Count;
        }
        f.Hooks.AfterTagHeadSelected = _ => {
            selected++;
            if (stage == "selected") { Stop(); }
        };
        f.Hooks.GetRecordedAtUnixMs = () => {
            if (stage == "clock") { Stop(); }
            return 123;
        };
        f.Hooks.AfterTagDependenciesConfirmed = () => { if (stage == "dependencies") { Stop(); } };
        f.Hooks.AfterTagRecordAppended = _ => {
            if (stage == "append") { Stop(); }
            else if (stage is "selected" or "clock" or "dependencies") { laterHook = true; }
        };
        f.Hooks.AfterPublicationConfirmed = () => {
            if (stage == "confirmed") { Stop(); }
            else { laterHook = true; }
        };
        PublicationOutcome outcome = PublicationOutcome.Confirmed;

        Assert.Throws<ObjectDisposedException>(() => store.CreateTag("disposed", [f.Id(1), f.Id(2)], out outcome));

        Assert.Equal(expected, outcome);
        Assert.True(eventsAfterDispose >= 0);
        Assert.Equal(eventsAfterDispose, f.Events.Count);
        Assert.False(laterHook);
        if (stage == "selected") { Assert.Equal(1, selected); }
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Operations.Locks[^1].DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void InputSliceIsCapturedBeforeHooksCanChangeTheCallersArray() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        HeadId[] input = [default, f.Id(2), f.Id(1), default];
        f.Hooks.AfterTagHeadSelected = _ => Array.Fill(input, f.Id(3));

        var tag = store.CreateTag("slice", input.AsSpan(1, 2), out _).Unwrap();

        Assert.Equal(new[] { f.Id(1), f.Id(2) }, tag.Entries.Keys.OrderBy(id => id.GetLocalId(f.Identity)));
        Assert.Equal(f.Initial[1], tag.Entries[f.Id(1)]);
        Assert.Equal(f.Initial[2], tag.Entries[f.Id(2)]);
        Array.Clear(input);
        Assert.Equal(2, tag.Entries.Count);
    }

    [Fact]
    public void DefiniteInputRejectionReleasesCreateGuardAndDoesNotAttemptOutput() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();

        Assert.True(store.CreateTag("duplicate-input", [f.Id(1), f.Id(1)], out var outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations.Files);

        store.CreateTag("valid-after-rejection", [f.Id(1)], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    private static void AssertRejected(AteliaError? error, PublicationOutcome outcome) {
        Assert.NotNull(error);
        Assert.Equal("HeadStore.TagCreateActive", error.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
    }
}
