using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class CancellationAndSchedulerTests
{
    [Fact]
    public void RequestCancellationIsIdempotentAndCleansUp()
    {
        using var registry = new RequestCancellationRegistry();
        CancellationTokenSource first = registry.Register("1");
        CancellationTokenSource second = registry.Register("2");
        Assert.True(registry.Cancel("1"));
        Assert.True(registry.Cancel("1"));
        Assert.True(first.IsCancellationRequested);
        registry.Complete("1", first);
        Assert.False(registry.Cancel("1"));
        registry.CancelAll();
        Assert.True(second.IsCancellationRequested);
        registry.Complete("2", second);
    }

    [Fact]
    public void RequestCancellationContinuesWhenConsumerCallbackThrows()
    {
        using var registry = new RequestCancellationRegistry();
        CancellationTokenSource throwing = registry.Register("throwing");
        CancellationTokenSource observed = registry.Register("observed");
        using CancellationTokenRegistration throwingRegistration = throwing.Token.Register(() =>
            throw new InvalidOperationException("consumer cancellation failure"));
        int observedCancellation = 0;
        using CancellationTokenRegistration observedRegistration = observed.Token.Register(() =>
            Interlocked.Exchange(ref observedCancellation, 1));

        registry.CancelAll();

        Assert.True(throwing.IsCancellationRequested);
        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(1, Volatile.Read(ref observedCancellation));
        registry.Complete("throwing", throwing);
        registry.Complete("observed", observed);
        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public async Task SchedulerCoalescesRapidWorkAndPublishesCurrentGenerationOnly()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "one\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        using var workspace = WorkspaceDiscovery.CreateLooseFile(file);
        var contexts = new LanguageServerAnalysisContextFactory(new DocumentContextResolver());
        var published = new TaskCompletionSource<DiagnosticResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int analyses = 0;
        await using var scheduler = new DiagnosticScheduler(contexts, context =>
        {
            Interlocked.Increment(ref analyses);
            return Task.FromResult<object?>(context.Document.EffectiveText.Text);
        }, (_, result) =>
        {
            published.TrySetResult(result);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(25));

        scheduler.Schedule(workspace, uri);
        scheduler.Schedule(workspace, uri);
        DiagnosticResult result = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, analyses);
        Assert.Equal(workspace.CurrentSnapshot.Generation, result.Generation);
        Assert.Equal("one\n", result.Value);
    }

    [Fact]
    public async Task SupersededDiagnosticResultCannotPublish()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "one\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        using var workspace = WorkspaceDiscovery.CreateLooseFile(file);
        DocumentId id = Assert.Single(workspace.CurrentSnapshot.Documents).Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int publishes = 0;
        await using var scheduler = new DiagnosticScheduler(
            new LanguageServerAnalysisContextFactory(new DocumentContextResolver()),
            async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return null;
            }, (_, _) =>
            {
                Interlocked.Increment(ref publishes);
                return Task.CompletedTask;
            }, TimeSpan.Zero);

        scheduler.Schedule(workspace, uri);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workspace.OpenDocument(id, "two\n", new DocumentVersion(1));
        release.TrySetResult();
        await Task.Delay(100);

        Assert.Equal(0, publishes);
    }

    [Fact]
    public async Task ReplacedSameGenerationJobCannotPublishAfterNewerJob()
    {
        using var fixture = CreateSchedulerFixture();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondPublished = NewSignal();
        int invocation = 0;
        var publications = new List<int>();
        await using var scheduler = new DiagnosticScheduler(
            new LanguageServerAnalysisContextFactory(new DocumentContextResolver()),
            async _ =>
            {
                int current = Interlocked.Increment(ref invocation);
                if (current == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                return current;
            }, (_, result) =>
            {
                int value = Assert.IsType<int>(result.Value);
                lock (publications) publications.Add(value);
                if (value == 2) secondPublished.TrySetResult();
                return Task.CompletedTask;
            }, TimeSpan.Zero);

        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await secondPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirst.TrySetResult();
        await WaitForAsync(() => scheduler.InFlightJobCount == 0);

        Assert.Equal(new[] { 2 }, publications);
    }

    [Fact]
    public async Task ReplacedJobRemainsOwnedAndAwaitedUntilItActuallyCompletes()
    {
        using var fixture = CreateSchedulerFixture();
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var releaseFirst = NewSignal();
        var releaseSecond = NewSignal();
        int invocation = 0;
        var scheduler = fixture.CreateScheduler(async _ =>
        {
            int current = Interlocked.Increment(ref invocation);
            (current == 1 ? firstStarted : secondStarted).TrySetResult();
            await (current == 1 ? releaseFirst : releaseSecond).Task;
            return current;
        });

        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.CurrentJobCount);
        Assert.Equal(2, scheduler.InFlightJobCount);

        Task disposal = scheduler.DisposeAsync().AsTask();
        releaseSecond.TrySetResult();
        await AssertStillRunningAsync(disposal);
        Assert.Equal(1, scheduler.InFlightJobCount);
        releaseFirst.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.IsDisposed);
        Assert.Equal(0, scheduler.CurrentJobCount);
        Assert.Equal(0, scheduler.InFlightJobCount);
    }

    [Fact]
    public async Task ExplicitlyCancelledJobRemainsOwnedUntilCompletion()
    {
        using var fixture = CreateSchedulerFixture();
        var started = NewSignal();
        var release = NewSignal();
        var scheduler = fixture.CreateScheduler(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return null;
        });
        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.Cancel(fixture.Workspace, fixture.Uri));
        Assert.Equal(0, scheduler.CurrentJobCount);
        Assert.Equal(1, scheduler.InFlightJobCount);
        Task disposal = scheduler.DisposeAsync().AsTask();
        await AssertStillRunningAsync(disposal);
        release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, scheduler.InFlightJobCount);
    }

    [Fact]
    public async Task MultipleReplacementsAllDrainBeforeDisposeReturns()
    {
        using var fixture = CreateSchedulerFixture();
        TaskCompletionSource[] started = [NewSignal(), NewSignal(), NewSignal()];
        TaskCompletionSource[] release = [NewSignal(), NewSignal(), NewSignal()];
        int invocation = 0;
        var scheduler = fixture.CreateScheduler(async _ =>
        {
            int index = Interlocked.Increment(ref invocation) - 1;
            started[index].TrySetResult();
            await release[index].Task;
            return index;
        });
        for (int index = 0; index < 3; index++)
        {
            scheduler.Schedule(fixture.Workspace, fixture.Uri);
            await started[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(3, scheduler.InFlightJobCount);

        Task disposal = scheduler.DisposeAsync().AsTask();
        release[2].TrySetResult();
        release[1].TrySetResult();
        await AssertStillRunningAsync(disposal);
        release[0].TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, scheduler.CurrentJobCount);
        Assert.Equal(0, scheduler.InFlightJobCount);
    }

    [Fact]
    public async Task ScheduleFailsDeterministicallyOnceDisposeBegins()
    {
        using var fixture = CreateSchedulerFixture();
        var started = NewSignal();
        var release = NewSignal();
        var scheduler = fixture.CreateScheduler(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return null;
        });
        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal = scheduler.DisposeAsync().AsTask();

        Assert.Throws<ObjectDisposedException>(() =>
            scheduler.Schedule(fixture.Workspace, fixture.Uri));
        release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.IsDisposed);
    }

    [Fact]
    public async Task WorkspaceStaleCancellationIsExpectedAndSchedulerRemainsUsable()
    {
        using var fixture = CreateSchedulerFixture();
        DocumentId documentId = Assert.Single(fixture.Workspace.CurrentSnapshot.Documents).Id;
        var staleStarted = NewSignal();
        var published = new TaskCompletionSource<DiagnosticResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int invocation = 0;
        await using var scheduler = new DiagnosticScheduler(
            new LanguageServerAnalysisContextFactory(new DocumentContextResolver()),
            async context =>
            {
                if (Interlocked.Increment(ref invocation) == 1)
                {
                    staleStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }
                return context.Document.EffectiveText.Text;
            }, (_, result) =>
            {
                published.TrySetResult(result);
                return Task.CompletedTask;
            }, TimeSpan.Zero);

        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        await staleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Workspace.OpenDocument(documentId, "two\n", new DocumentVersion(1));
        await WaitForAsync(() => scheduler.InFlightJobCount == 0);
        Assert.Equal(0, scheduler.CurrentJobCount);

        scheduler.Schedule(fixture.Workspace, fixture.Uri);
        DiagnosticResult result = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("two\n", result.Value);
        Assert.Equal(2, invocation);
    }

    private static SchedulerFixture CreateSchedulerFixture() => new();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertStillRunningAsync(Task task)
    {
        await Task.Delay(75);
        Assert.False(task.IsCompleted);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class SchedulerFixture : IDisposable
    {
        private readonly TestDirectory _directory = new();

        public SchedulerFixture()
        {
            string file = _directory.Write("main.xe", "one\n");
            Uri = DocumentUri.FromPath(file).AbsoluteUri;
            Workspace = WorkspaceDiscovery.CreateLooseFile(file);
        }

        public string Uri { get; }
        public Workspace Workspace { get; }

        public DiagnosticScheduler CreateScheduler(
            Func<LanguageServerAnalysisContext, Task<object?>> analyzer) => new(
                new LanguageServerAnalysisContextFactory(new DocumentContextResolver()),
                analyzer, (_, _) => Task.CompletedTask, TimeSpan.Zero);

        public void Dispose()
        {
            Workspace.Dispose();
            _directory.Dispose();
        }
    }
}
