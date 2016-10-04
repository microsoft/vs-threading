Cookbook for Visual Studio
==========================

Important for CPS users: If you're in CPS or a CPS extension, please
replace all references to `ThreadHelper.JoinableTaskFactory` with
`this.ThreadHandling.AsyncPump`, where `this.ThreadHandling` is an `[Import]
IThreadHandling`.

Initial setup
---

- Reference Microsoft.VisualStudio.Threading.dll
- Add `using Microsoft.VisualStudio.Threading;` to the start of any relevant 
  C# source files.

Block a thread while doing async work
---------------------

```csharp
ThreadHelper.JoinableTaskFactory.Run(async delegate
{
    // caller's thread
    await SomeOperationAsync(...);

    // switch to main thread
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    SomeUIThreadBoundWork();

    // switch to threadpool
    await TaskScheduler.Default; // using Microsoft.VisualStudio.Threading;
    SomeWorkThatCanBeOnThreadpool();
});
```

If any of your async work should be on a background thread, you can switch
explicitly:

```csharp
// assuming we're on the UI thread already…
await Task.Run(async delegate
{
    // we're on a background thread now
});

// ...now we're back on the UI thread.
```

Alternatively:

```csharp
// On some thread, but definitely want to be on the threadpool:

// using Microsoft.VisualStudio.Threading;

await TaskScheduler.Default;

// On some thread, but definitely want to be on the main thread:
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
```

How to switch to the UI thread
-----------------

In an async method

```csharp
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
```

In a sync method

First, consider carefully whether you can make your method async first
and then follow the above pattern. If you must remain synchronous, you
can switch to the UI thread like this:

```csharp
ThreadHelper.JoinableTaskFactory.Run(async delegate
{
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    // You're now on the UI thread.
});
```

How to switch to the UI thread using a specific priority
--------------------------------

Simply call the `JoinableTaskFactory.RunAsync` extension method (defined in
the `Microsoft.VisualStudio.Shell` namespace) passing in a priority as the
first parameter, like this:

```csharp
await ThreadHelper.JoinableTaskFactory.RunAsync(
    VsTaskRunContext.UIThreadBackgroundPriority,
    async delegate
    {
        // On caller's thread. Switch to main thread (if we're not already there).
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Now on UI thread via background priority.
        await Task.Yield();

        // Resumed on UI thread, also via background priority.
    });
```

How can I complete asynchronously with respect to my caller while still
using the UI thread?

If you need to complete work on the UI thread, and your caller is
(usually) on the UI thread, but you want to return a `Task<TResult>` to
your caller and complete the work asynchronously, you should use the
`JoinableTaskFactory.RunAsync` method, as described in the answer to: How
do I switch to the UI thread using a specific priority?

How can I await `IVsTask`?
--------------

Make sure you have these using directives at the top of your code file:

```csharp
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;
```

That adds a [`GetAwaiter` extension
method](https://msdn.microsoft.com/en-us/library/vstudio/hh598836(v=vs.110).aspx)
to `IVsTask` that makes it awaitable. In fact just the `Microsoft.VisualStudio.Shell`
namespace is required for that, but the first one is very useful. And since
they both define Task classes they conflict making TPL Task difficult to
use unless you add the third line.

How can I return `IVsTask` from a C# async method?
-----------------------------

The preferred way is to use the `JoinableTaskFactory.RunAsyncAsVsTask(Func<CancellationToken,
Task>)` extension method. This gives you a `CancellationToken` that is tied
to `IVsTask.Cancel()`.

```csharp
public IVsTask DoAsync()
{
    return ThreadHelper.JoinableTaskFactory.RunAsyncAsVsTask(
        VsTaskRunContext.UIThreadNormalPriority, // (or lower UI thread priorities)
        async cancellationToken =>
        {
            await SomethingAsync(cancellationToken);
            await SomethingElseAsync();
        });
}

private async Task SomethingAsync(CancellationToken cancellationToken)
{
    await Task.Yield();
}
```

Alternately, if you only have a `JoinableTask`, you can readily convert it
to an `IVsTask` by calling the `JoinableTask.AsVsTask()` extension method.

```csharp
public IVsTask DoAsync()
{
    return ThreadHelper.JoinableTaskFactory.RunAsync(
        async delegate
        {
            await SomethingAsync();
            await SomethingElseAsync();
        }).AsVsTask();
}
```
