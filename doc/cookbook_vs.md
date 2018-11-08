Cookbook for Visual Studio
==========================

Important for CPS extension authors and clients: please
replace all references to `ThreadHelper.JoinableTaskFactory` with
`this.ThreadHandling.AsyncPump`, where `this.ThreadHandling` is an `[Import]
IThreadHandling`.

## Initial setup

- Add a reference to [Microsoft.VisualStudio.Threading][NuPkg].
- Add these `using` directives to your C# source file:

  ```csharp
  using System.Threading.Tasks;
  using Microsoft.VisualStudio.Threading;
  ```

- When using `Microsoft.VisualStudio.Shell` types in the same source file, import that namespace
  along with explicitly assigning `Task` to the .NET namespace so you can more conveniently use .NET Tasks:

  ```csharp
  using Microsoft.VisualStudio.Shell;
  using Task = System.Threading.Tasks.Task;
  ```

### Installing the analyzers

It is highly recommended that you install the [Microsoft.VisualStudio.Threading.Analyzers][AnalyzerNuPkg] NuGet package which adds analyzers to your project to help catch many common violations of [the threading rules](threading_rules.md), helping you prevent your code from deadlocking.

## How to switch to a background thread

```csharp
await TaskScheduler.Default;
```

## How to switch to the UI thread

In an async method

```csharp
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
```

In a regular synchronous method

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

## How to switch to or use the UI thread with a specific priority

For those times when you need the UI thread but you don't want to introduce UI delays for the user,
you can use the `StartOnIdle` extension method which will run your code on the UI thread when it is otherwise idle.

```csharp
await ThreadHelper.JoinableTaskFactory.StartOnIdle(
    async delegate
    {
        for (int i = 0; i < 10; i++)
        {
            DoSomeWorkOn(i);

            // Ensure we frequently yield the UI thread in case user input is waiting.
            await Task.Yield();
        }
    });
```

If you have a requirement for a specific priority (which may be higher or lower than background), you can use the `WithPriority` extension method, like this:

```csharp
var databindPriorityJTF = ThreadHelper.JoinableTaskFactory.WithPriority(someDispatcher, DispatcherPriority.DataBind);
await databindPriorityJTF.RunAsync(
  async delegate
  {
    // The JTF instance you use to actually make the switch is irrelevant here.
    // The priority is dictated by the scenario owner, which is the one
    // running JTF.RunAsync at the bottom of the stack (or just above us in this sample).
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
  });
```

The way the priority is dictated by the `JoinableTaskFactory` instance that `RunAsync` is called on rather than the one on which `SwitchToMainThreadAsync` is invoked allows you to set the priority for the scenario and then call arbitrary code in VS and expect all of the switches to the main thread that code might require to honor that priority that you set as the scenario owner.

There are several `WithPriority` extension method overloads, allowing you to typically match exactly the priority your code was accustomed to before switching to use `JoinableTaskFactory` for switching to the main thread.

## Call async code from a synchronous method (and block on the result)

```csharp
ThreadHelper.JoinableTaskFactory.Run(async delegate
{
    await SomeOperationAsync(...);
});
```

## How to write a "fire and forget" method responsibly?

"Fire and forget" methods are async methods that are called without awaiting the
`Task` that they (may) return.

Do *not* implement your fire and forget async method by returning `async void`.
These methods will crash the process if they throw an unhandled exception.

There are two styles of "fire and forget":

### `void`-returning fire and forget methods

These methods are designed to always be called in a fire and forget fashion, as the
caller only gets a `void` return value and cannot await the async method even if it wanted to.

These methods should be named to indicate that they start an operation. For example,
instead of calling the method `Task DoSomethingAsync()` you might call it `void StartSomething()`.

Notwithstanding the `void` return type, you'll want to be async internally in order to actually
do the work later instead of on your caller's callstack. But you also should be sure your async
work finishes before your object claims to be disposed. You can accomplish both of these objectives
using the `JoinableTaskFactory.RunAsync` method, and tacking on an extension method that captures
failures and reports them for your analysis later. For the Visual Studio team's own internal use,
the `FileAndForget` method can be tacked on at the end to send failure reports via VS telemetry fault events.
You may want to handle your own exceptions within the async delegate as well.

```csharp
void StartOperation()
{
  this.JoinableTaskFactory.RunAsync(async delegate
  {
    await Task.Yield(); // get off the caller's callstack.
    DoWork();
    this.DisposalToken.ThrowIfCancellationRequested();
    DoMoreWork();
  }).FileAndForget("vs/YOUR-FEATURE/YOUR-ACTION"); // Microsoft's own internal extension method
}
```

Where `this.JoinableTaskFactory` is defined by your `AsyncPackage` or, if you don't have one,
by re-implementing this pattern in your own class:

```csharp
class MyResponsibleType : IDisposable
{
  private readonly CancellationTokenSource disposeCancellationTokenSource = new CancellationTokenSource();

  internal MyResponsibleType()
  {
    this.JoinableTaskCollection = ThreadHelper.JoinableTaskContext.CreateCollection();
    this.JoinableTaskFactory = ThreadHelper.JoinableTaskContext.CreateFactory(this.JoinableTaskCollection);
  }

  JoinableTaskFactory JoinableTaskFactory { get; }
  JoinableTaskCollection JoinableTaskCollection { get; }

  /// <summary>
  /// Gets a <see cref="CancellationToken"/> that can be used to check if the package has been disposed.
  /// </summary>
  CancellationToken DisposalToken => this.disposeCancellationTokenSource.Token;

  public void Dispose()
  {
    this.Dispose(true);
    GC.SuppressFinalize(this);
  }

  protected virtual void Dispose(bool disposing)
  {
    if (disposing)
    {
      this.disposeCancellationTokenSource.Cancel();

      try
      {
        // Block Dispose until all async work has completed.
        ThreadHelper.JoinableTaskFactory.Run(this.JoinableTaskCollection.JoinTillEmptyAsync);
      }
      catch (OperationCanceledException)
      {
        // this exception is expected because we signaled the cancellation token
      }
      catch (AggregateException ex)
      {
        // ignore AggregateException containing only OperationCanceledException
        ex.Handle(inner => (inner is OperationCanceledException));
      }
    }
  }
}
```

### `Task`-returning fire and forget methods

These are your typical async methods, but called without awaiting the resulting `Task`.

Not awaiting on the `Task` means that if that async method were to throw an exception
(thus faulting the returned Task) that no one would notice. The error would go unreported
and undetected -- until the side effects of the async method are discovered to be incomplete
some other way. In a *few* cases this may be desirable.
Since calling a `Task`-returning method without awaiting its result will often make C# emit
a compiler warning, a `void`-returning `Forget()` extension method exists so you can tack it
onto the end of an async method call to suppress the compiler warning, and make it clear to
others reading the code that you are intentionally ignoring the result.

```csharp
DoSomeWork();
DoSomeWorkAsync().Forget(); // no await here
DoABitMore();
```

Better than `Forget()` is to use `FileAndForget(string)` which will handle faulted Tasks by
adding the error to the VS activity log and file a fault event for telemetry collection so
the fault can be detected and fixed.

```csharp
DoSomeWork();
DoSomeWorkAsync().FileAndForget("vs/YOUR-FEATURE/YOUR-ACTION"); // no await here
DoABitMore();
```

## How can I await `IVsTask`?

Make sure you have the `using` directives defined at the top of this document.
That adds a [`GetAwaiter` extension method][MSDNIVsTaskGetAwaiter]
to `IVsTask` that makes it awaitable.

You can safely await an `IVsTask` in an ordinary C# async method or inside a
`JoinableTaskFactory.Run` or `RunAsync` delegate.

## How can I return `IVsTask` from a C# async method?

The preferred way is to use the `JoinableTaskFactory.RunAsyncAsVsTask(Func<CancellationToken,
Task>)` extension method. This gives you a `CancellationToken` that is tied
to `IVsTask.Cancel()`.

```csharp
IVsTask IVsYourNativeCompatibleInterface.DoAsync(string arg1)
{
    return ThreadHelper.JoinableTaskFactory.RunAsyncAsVsTask(
        VsTaskRunContext.UIThreadNormalPriority,
        cancellationToken => this.DoAsync(arg1, cancellationToken));
}

public async Task DoAsync(string arg1, CancellationToken cancellationToken)
{
    await Task.Yield();
}
```

In a pinch, where all you have is a `JoinableTask`, you can readily convert it
to an `IVsTask` by calling the `JoinableTask.AsVsTask()` extension method as shown
below. But this means the `IVsTask.Cancel()` method will not be able to communicate
the cancellation request to the async delegate.

```csharp
public IVsTask DoAsync()
{
    JoinableTask jt = ThreadHelper.JoinableTaskFactory.RunAsync(
        async delegate
        {
            await SomethingAsync();
            await SomethingElseAsync();
        });
    return jt.AsVsTask();
}
```

## How can I block the UI thread for long or async work while displaying a (cancelable) wait dialog?

While it's generally preferable that you do work quickly or asynchronously so as to not block the user
from interacting with the IDE, it is sometimes necessary and/or desirable to block user input.
While doing so, it's good to provide feedback so the user knows the application hasn't hung.
To display a dialog explaining what's going on while you're on and blocking the UI thread, and even
give the user a chance to cancel the operation, you can call this extension method on `JoinableTaskFactory`:

```csharp
ThreadHelper.JoinableTaskFactory.Run(
    "I'm churning on your data",
    async (progress, cancellationToken) =>
    {
        DoLongRunningWork();
        progress.Report(new ThreadedWaitDialogProgressData("Getting closer to being done.", isCancelable: true));
        await AndSomeAsyncWorkButBlockMainThreadAnyway(cancellationToken);
    });
```

If your operation cannot be canceled, just drop the `CancellationToken` parameter from your anonymous delegate
and the dialog that is shown to the user will not include a Cancel button:

```csharp
ThreadHelper.JoinableTaskFactory.Run(
    "I'm churning on your data",
    async (progress) =>
    {
        DoLongRunningWork();
        progress.Report(new ThreadedWaitDialogProgressData("Getting closer to being done."));
        await AndSomeAsyncWorkButBlockMainThreadAnyway();
    });
```

## How can I initialize my VS package asynchronously?
As of Visual Studio 2015 (Dev14) you can define async VS packages, allowing binaries to be loaded on background threads and for your package to complete async work without blocking the UI thread as part of package load.
[Async Package 101][AsyncPackage101]

## How do I make sure my async work has finished before my VS Package closes?
This is a very important topic because we know VS can crash on shutdown because async tasks or background threads still actively running after all VS packages have supposedly shutdown.
The recommended pattern to solve this problem is to derive from `AsyncPackage` instead of `Package`.
Then for any async work your package is responsible for starting but that isn't awaited on somewhere,
you should start that work with your `AsyncPackage`'s `JoinableTaskFactory` property, calling the `RunAsync` method.
This ensures that on package close, your async work must complete before the AppDomain is shutdown.
Your async work should generally also honor the `AsyncPackage.DisposalToken` and cancel itself right away when that
token is signaled so that VS shutdown is not slowed down significantly by work that no longer matters.

## How do I effectively verify that my code is fully free-threaded?

Code is free-threaded if it (and all code it invokes transitively) can complete on the caller's thread, no matter which thread that is. Code that is *not* free-threaded is said to be *thread-affinitized*, meaning some of its work may require execution on a particular thread.

It is not always necessary for code to be free-threaded. There are many cases where code *must* run on the main thread because it calls into other code that itself has thread-affinity or is not thread-safe. Important places to be free-threaded include:

1. Code that runs in the MEF activation code paths (e.g. importing constructors, OnImportsSatisfied callbacks, and any code called therefrom).
1. Code that may run on a background thread, especially when run within an async context.
1. Static field initializers and static constructors.

In Visual Studio, thread-affinitized code typically requires the main thread (a.k.a. the UI thread). Code that has main thread affinity may appear to work when called from background threads for a few reasons, including:

1. The code can sometimes run without requiring the main thread. For example, it only requires the main thread the first time it runs, and subsequent calls are effectively free-threaded.
1. The code may switch itself to the main thread if its caller didn't call it on that thread. This approach only works if the main thread is available to service such a request to switch from a background thread.

The foregoing conditions can make it difficult to test whether your code is truly free-threaded. Knowing your code is free-threaded is important before executing it off the main thread, particularly when that code executes synchronously, since otherwise it can deadlock or contribute to [threadpool starvation][ThreadpoolStarvation].

To test whether your code executes without any requirement on the main thread, you can run that code in VS within such a construct as this:

```cs
ThreadHelper.ThrowIfNotOnUIThread(); // this test only catches issues when it blocks the UI thread.
jtf.Run(async delegate
{
   using (jtf.Context.SuppressRelevance())
   {
      await Task.Run(delegate
      {
         var c = ActivateComponent();
         c.UseComponent();
      });
   }
});
```

This would effectively ensure that the main thread will *not* respond to any request from your test method, thus forcing a deadlock if one was lurking and could occur in otherwise non-deterministic conditions.

Try to execute such test code as early in the VS process as possible. This will help you catch any issues with code you may call that is thread affinitized the first time it is executed.

Note that being free-threaded is *not* the same thing as being thread-*safe*, which is an independent metric. Code can be free-threaded, thread-safe, both, or neither.

## How do I effectively verify that my code is fully thread-safe?

Code is thread-safe if your code does not malfunction when called from more than one thread at a time.

It is not always necessary for code to be thread-safe. In fact many classes in the Base Class Library of .NET itself is not thread-safe. Writing thread-safe code involves mitigating data corruption, deadlocks, and higher level goals such as avoiding lock contention and threadpool starvation. Validating that code is thread-safe requires exhaustive reviews and testing, and thread-safety bugs can still slip through. Whether thread-safety should be a goal should typically be determined at the class level and clearly documented. Changing from thread-safe to non-thread-safe should be considered a breaking change. A class should be made thread-safe if being called from multiple threads at once is a supported scenario.

Techniques for writing thread-safe code include:

1. Using synchronization primitives such as C# `lock` when accessing fields.
1. Using immutable data structures and interlocked exchange methods while handling race conditions.

Techniques for verifying that code is thread-safe include both thorough code reviews and automated tests. Automated tests should execute your code concurrently. You may find you can shake out different bugs by testing with concurrency levels equal to `Environment.ProcessorCount` to maximize parallelism and throughput as well as a multiplier of that number (e.g. `Environment.ProcessorCount * 3`) so that hyper-switching of the CPU leads to different time slices and unique thread-safety bugs to be found. Such tests should verify that unexpected exceptions are not thrown, no hangs occur, and that the data at the conclusion of such concurrent execution is not corrupted.

Code can be made to run concurrently from a unit test using this technique:

```csharp
int concurrencyLevel = Environment.ProcessorCount; // * 3
await Task.WhenAll(Enumerable.Range(1, concurrencyLevel).Select(i => Task.Run(delegate {
    CallYourCodeHere();
})));
```

If `CallYourCodeHere()` executes fast enough, it's possible that the above does not lead to actual concurrent execution since `Task.Run` does not guarantee that the delegate executes at a particular time. To increase the chances of concurrent execution, you can use the `CountdownEvent` class:

```csharp
int concurrencyLevel = Environment.ProcessorCount; // * 3
var countdown = new CountdownEvent(concurrencyLevel);
await Task.WhenAll(Enumerable.Range(1, concurrencyLevel).Select(i => Task.Run(delegate {
    countdown.Signal();
    countdown.Wait();
    CallYourCodeHere();
})));
```

The above code will force allocation of a thread for each degree of concurrency you specify, and each thread will block until all threads are ready to go, at which point they will all unblock together (subject to kernel thread scheduling).

Note that being thread-safe is *not* the same thing as being free-threaded, which is an independent metric. Code can be free-threaded, thread-safe, both, or neither.

## Should I await a Task with `.ConfigureAwait(false)`?

### What does it even mean?

When you *await* an expression (e.g. a `Task` or `Task<T>`), that expression must produce an "awaiter". This is mostly hidden as an implementation detail by the compiler. At runtime this "awaiter" can indicate whether the expression being awaited on represents a completed operation or one that is not yet complete. When the operation is complete, the awaiting code simply continues execution immediately (no yielding, no thread switching, etc.).
When the operation is not complete, the awaiter is asked to execute a delegate when the operation is done.
At a high level, this means that when you write `await SomethingAsync()` the next line of code in your method will not execute until the `Task` returned by `SomethingAsync()` is complete.

Suppose that `SomethingAsync()` returns a `Task` that does not complete for 5 seconds. During that time, your method is no longer on the callstack (because it yielded when the `Task` wasn't complete). So when the `Task` completes, it now has a responsibility to invoke the callback the compiler created in order to "resume" your method right where it left off. Which thread should it use to invoke your delegate? That is up to the awaiter to decide.

When you await a `Task`, the policy for what thread the next part of your async method executes on is determined by a type called `TaskAwaiter`. It will execute the callback on the same thread/context that your async method was on when it originally awaited the `Task`. This allows you to be on the UI thread, await something, and then continue your code, still on the UI thread. This is independent of which thread the `Task` itself may have been running on. `TaskAwaiter` does this by capturing the `SynchronizationContext` or `TaskScheduler.Current` from the caller and using either of those as a means of scheduling the invocation of the callback. When neither of those are present, it will simply schedule the callback to execute on the threadpool.

But what if your code may be on the UI thread, but does not need the UI thread to finish its work after the awaited `Task` has completed? You can express to the awaited `Task` that you don't mind executing on the threadpool by adding `.ConfigureAwait(false)` to the end of the `Task`. This causes the compiler to interact with `ConfiguredTaskAwaiter` instead of `TaskAwaiter`. The `ConfiguredTaskAwaiter`'s policy is (if you pass in `false` when creating it) to always schedule continuations to the threadpool (or in some cases inline the continuation immediately after the `Task` itself is completed).

**Note:** Use of `.ConfigureAwait(true)` is equivalent to awaiting a `Task` directly. Using this suffix is a way to suppress the warning emitted by some analyzers that like to see `.ConfigureAwait(false)` everywhere. Where no such analyzer is active, omitting the suffix is recommended for improved code readability.

### Short answer

* Use `ConfigureAwait(false)` when writing app-independent library code. Such a library should avoid frequent use of `Task.Wait()` and `Task.Result`.
* Use `ConfigureAwait(true)` when writing code where a `JoinableTaskFactory` is available. Use `await TaskScheduler.Default;` before CPU intensive, free-threaded work.

For Visual Studio packages, the recommendation is to *not* use `.ConfigureAwait(false)`.

### Justification

Awaiting tasks with `.ConfigureAwait(false)` is a popular trend for a couple reasons:

1. It allows the continuation (when scheduled) to run on the threadpool instead of returning to an unknown `SynchronizationContext` set by the caller. This can improve efficiency, and keep CPU intensive work off the UI thread, thereby increasing an application's responsiveness to user input.
1. It can avoid deadlocks when people use `Task.Wait()` or `Task.Result` from the UI thread.

But `.ConfigureAwait(false)` carries disadvantages as well:

1. The tendency for `Task.Wait()` to work actually encourages such synchronously blocking code, yet deadlocks can still happen if the UI thread is actually required for one of the scheduled continuations.
1. It makes for harder to read and maintain async code.
1. It may not actually move CPU intensive work off the UI thread since it only makes the switch on the first *yielding* await.
1. It contributes to [threadpool starvation][ThreadpoolStarvation] when the code using it is called in the context of a `JoinableTaskFactory.Run` delegate.

The last disadvantage above deserves some elaboration. The `JoinableTaskFactory.Run` method sets a special `SynchronizationContext` when invoking its delegate so that async continuations automatically run on the original thread (without deadlocking). When awaits use `.ConfigureAwait(false)` it ignores the `SynchronizationContext` and defeats this optimization. As a result the scheduled continuation will occupy a thread from the threadpool, even though the JTF.Run thread is blocked waiting for the delegate to complete and could have executed the continuation. In this scenario, *two* threads are allocated although only one is active.

The problem grows when multiple frames in the callstack use `Task.Wait` or `JoinableTaskFactory.Run`. With each synchronously blocking frame, that thread now becomes useless till the whole operation that it is waiting for is complete, and yet another thread is allocated to make that possible. In some severe cases, we've seen the application hang for over a minute while 75+ threadpool threads were allocated one at a time, each to try to make progress after the thread previously allocated just synchronously blocks for completion. Using `JoinableTaskFactory.Run` consistently prevents this, but only when the code executed by the delegate passed to it avoids using `.ConfigureAwait(false)`.

Code invoked from within `JoinableTaskFactory.RunAsync` (the async version) does not immediately synchronously block and thus tends to be less of a concern when using `.ConfigureAwait(false)`. However, since a delegated passed to this method can become blocking later using `JoinableTask.Join()` or await the `JoinableTask` within another `JoinableTaskFactory.Run` call, it is similarly recommended to avoid `.ConfigureAwait(false)` in all JTF contexts.

So how do we get the best of both worlds? How can we have a responsive app, keeping CPU intensive work off the UI thread while not using `.ConfigureAwait(false)`? The guideline is that when you're about to start some CPU intensive, free-threaded work is to first explicitly switch to the threadpool using `await TaskScheduler.Default;`. This simple approach works consistently without many of the disadvantages listed above.

### Some important notes

1. Using `.ConfigureAwait(false)` does *not* guarantee that code after it will be on the threadpool. It has absolutely no effect at all if the `Task` itself is already complete since the compiler will simply continue execution immediately as if there were no await there.
1. If you have a policy to use `.ConfigureAwait(false)` it is important to use it *everywhere* (not just on the first await in a method), because the first awaited expression might not yield but the second one may, and therefore the yielding expression must have that suffix to get the effect.
1. If the awaited `Task` completes on the UI thread, continuations are typically *not* inlined, even if `.ConfigureAwait(false)` is used when awaiting the `Task`.
1. If the awaited `Task` completes on a threadpool thread, then it will usually inline continuations that are expecting to be invoked on the threadpool as an optimization. This includes continuations scheduled with `.ConfigureAwait(false)` and those that were scheduled while already on the threadpool.

[NuPkg]: https://www.nuget.org/packages/Microsoft.VisualStudio.Threading
[AnalyzerNuPkg]: https://www.nuget.org/packages/Microsoft.VisualStudio.Threading.Analyzers
[MSDNIVsTaskGetAwaiter]: https://msdn.microsoft.com/en-us/library/vstudio/hh598836.aspx
[AsyncPackage101]: https://microsoft.sharepoint.com/teams/DD_VSIDE/_layouts/15/WopiFrame.aspx?sourcedoc=%7b84C6ABED-E111-4B5D-B2D6-8B6FAF37F0D4%7d&file=Async%20Package%20101.docx&action=default
[ThreadpoolStarvation]: threadpool_starvation.md
