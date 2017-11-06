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

## How to switch to or use the UI thread with background priority

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
using the `JoinableTaskFactory.RunAsync` method, and tacking on the `FileAndForget` method at the end
so that faults are detectable. You may want to handle your own exceptions within the async delegate as well.

```csharp
void StartOperation()
{
  this.JoinableTaskFactory.RunAsync(async delegate
  {
    await Task.Yield(); // get off the caller's callstack.
    DoWork();
    this.DisposalToken.ThrowIfCancellationRequested();
    DoMoreWork();
  }).FileAndForget("vs/YOUR-FEATURE/YOUR-ACTION");
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

[NuPkg]: https://www.nuget.org/packages/Microsoft.VisualStudio.Threading
[AnalyzerNuPkg]: https://www.nuget.org/packages/Microsoft.VisualStudio.Threading.Analyzers
[MSDNIVsTaskGetAwaiter]: https://msdn.microsoft.com/en-us/library/vstudio/hh598836.aspx
[AsyncPackage101]: https://microsoft.sharepoint.com/teams/DD_VSIDE/_layouts/15/WopiFrame.aspx?sourcedoc=%7b84C6ABED-E111-4B5D-B2D6-8B6FAF37F0D4%7d&file=Async%20Package%20101.docx&action=default
