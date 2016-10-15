Cookbook for Visual Studio
==========================

Important for CPS users: If you're in CPS or a CPS extension, please
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

## How to switch to or use the UI thread using a specific (VS-defined) priority

Simply call the `JoinableTaskFactory.RunAsync` extension method 
passing in a priority as the first parameter, like this:

```csharp
await ThreadHelper.JoinableTaskFactory.RunAsync(
    VsTaskRunContext.UIThreadBackgroundPriority,
    async delegate
    {
        // We're still on the caller's thread. Switch to main thread (if we're not already there).
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Now yield, in case we were already on the UI thread and thereby skipped the background priority scheduling.
        await Task.Yield();

        // Resumed on UI thread via background priority.
        for (int i = 0; i < 10; i++)
        {
            DoSomeWorkOn(i);
            
            // Ensure we quickly yield the UI thread to the user if necessary.
            // Each time we resume, we're using the background priority scheduler.
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
using the `JoinableTaskFactory.RunAsync` method:

```csharp
void StartOperation()
{
  this.JoinableTaskFactory.RunAsync(async delegate
  {
    await Task.Yield(); // get off the caller's callstack.
    DoMoreWork();
  });
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
others reading the code that you are intentionally ignoring the result:

```csharp
DoSomeWork();
DoSomeWorkAsync().Forget(); // no await here
DoABitMore();
```

It is usually more preferable to track the Task so that if it fails, you can report it to the user
and/or file failure telemetry.

```csharp
DoSomeWork();
DoSomeWorkAsync().ContinueWith(
  t => TelemetryService.DefaultSession.PostFault(
    eventName: EventNames.TippingFailed,
    description: $"Failed to tip for scenario {tippingScenario}.",
    exceptionObject: t.Exception.InnerException),
  TaskContinuationOptions.OnlyOnFaulted);
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

[NuPkg]: https://www.nuget.org/packages/Microsoft.VisualStudio.Threading
[MSDNIVsTaskGetAwaiter]: https://msdn.microsoft.com/en-us/library/vstudio/hh598836.aspx
