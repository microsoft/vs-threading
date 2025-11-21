# VSTHRD003 Avoid awaiting foreign Tasks

Tasks that are created and run from another context (not within the currently running method or delegate)
should not be returned or awaited on. Doing so can result in deadlocks because awaiting a `Task`
does not result in the awaiter "joining" the effort such that access to the main thread is shared.
If the awaited `Task` requires the main thread, and the caller that is awaiting it is blocking the
main thread, a deadlock will result.

When required to await a task that was started earlier, start it within a delegate passed to
`JoinableTaskFactory.RunAsync`, storing the resulting `JoinableTask` in a field or variable.
You can safely await the `JoinableTask` later.

## Suppressing warnings for completed tasks

If you have a property, method, or field that returns a pre-completed task (such as a cached task with a known value),
you can suppress this warning by applying the `[CompletedTask]` attribute to the member.
This attribute is automatically included when you install the `Microsoft.VisualStudio.Threading.Analyzers` package.

```csharp
[Microsoft.VisualStudio.Threading.CompletedTask]
private static readonly Task<bool> TrueTask = Task.FromResult(true);

async Task MyMethodAsync()
{
    await TrueTask; // No warning - TrueTask is marked as a completed task
}
```

The analyzer already recognizes the following as safe to await without the attribute:
- `Task.CompletedTask`
- `Task.FromResult(...)`
- `Task.FromCanceled(...)`
- `Task.FromException(...)`
- `TplExtensions.CompletedTask`
- `TplExtensions.CanceledTask`
- `TplExtensions.TrueTask`
- `TplExtensions.FalseTask`

## Simple examples of patterns that are flagged by this analyzer

The following example would likely deadlock if `MyMethod` were called on the main thread,
since `SomeOperationAsync` cannot gain access to the main thread in order to complete.

```csharp
void MyMethod()
{
    System.Threading.Tasks.Task task = SomeOperationAsync();
    joinableTaskFactory.Run(async delegate
    {
        await task;  /* This analyzer will report warning on this line. */
    });
}
```

In the next example, `WaitForMyMethod` may deadlock when `this.task` has not completed
and needs the main thread to complete.

```csharp
class SomeClass
{
    System.Threading.Tasks.Task task;

    SomeClass()
    {
       this.task = SomeOperationAsync();
    }

    async Task MyMethodAsync()
    {
        await this.task;  /* This analyzer will report warning on this line. */
    }

    void WaitForMyMethod()
    {
        joinableTaskFactory.Run(() => MyMethodAsync());
    }
}
```

More [advanced examples](#advanced-cases) are further down in this document, below the solution section for the simpler examples.

## Solution for simpler cases

To await the result of an async method from with a JoinableTaskFactory.Run delegate,
invoke the async method within the JoinableTaskFactory.Run delegate:

```csharp
void MyMethod()
{
    joinableTaskFactory.Run(async delegate
    {
        System.Threading.Tasks.Task task = SomeOperationAsync();
        await task;
    });
}
```

Alternatively wrap the original method invocation with JoinableTaskFactory.RunAsync:

```csharp
class SomeClass
{
    JoinableTask joinableTask;

    SomeClass()
    {
       this.joinableTask = joinableTaskFactory.RunAsync(() => SomeOperationAsync());
    }

    async Task MyMethodAsync()
    {
        await this.joinableTask;
    }

    void WaitForMyMethod()
    {
        joinableTaskFactory.Run(() => MyMethodAsync());
    }
}
```

## Advanced cases

### `TaskCompletionSource<T>`

In the next example, a `TaskCompletionSource<T>` is used as a black-box for unblocking functionality.
It too represents awaiting a foreign task:

```cs
class SomeClass
{
    TaskCompletionSource<bool> tcs = new();

    public async Task MyMethodAsync()
    {
        await this.tcs.Task; /* This analyzer will report warning on this line. */
        /* do more stuff */
    }

    void UnlockProgress()
    {
        this.tcs.TrySetResult(true);
    }
}
```

The problem with the above code is that `MyMethodAsync()` waits for unknown work (whatever work will lead to the completion of the `TaskCompletionSource`) before making progress.
If `UnlockProgress()` is never called, the caller of `MyMethodAsync()` will be awaiting forever.
Now suppose that the caller of `MyMethodAsync()` is actually inside a `JoinableTaskFactory.Run` delegate:

```cs
void SomeCaller()
{
    joinableTaskFactory.Run(async delegate
    {
        await someClass.MyMethodAsync();
    });
}
```

If `SomeCaller()` runs on the main thread, then it will effectively block the main thread while waiting for `this.tcs.Task` from `SomeClass` to complete.
Now suppose that another thread comes along and wants to do some work before calling `UnlockProgress()`:

```cs
partial class SomeClass
{
    async Task KeyMasterAsync()
    {
        await joinableTaskFactory.SwitchToMainThreadAsync();
        // do some work
        // Unblock others
        someClass.UnlockProgress();
    }
}
```

We have a deadlock, because `SomeCaller()` is blocking the main thread while waiting for `UnlockProgress()` to be called, but `UnlockProgress()` will not be called until `KeyMasterAsync` can reach the main thread.

Fixing this fundamentally means that `SomeCaller` will need to *join* whatever work may be needed to ultimately call `UnlockProgress`. But for `SomeCaller`, that work is unknown, since it's at least partially inside another class.
`TaskCompletionSource<T>` is fundamentally a blackbox and the most difficult thing to use correctly while avoiding deadlocks.

Preferred solutions involve replacing `TaskCompletionSource<T>` with another type that makes tracking the work involved automatic.
These include:

1. Use `JoinableTaskFactory.RunAsync` and store the resulting `JoinableTask` in a field to await later.
1. Use `AsyncLazy<T>` for one-time init work that should only start if required. Be sure to pass in a `JoinableTaskFactory` instance to its constructor.

Assuming you must keep using `TaskCompletionSource<T>` though, here's how it can be done as safely as possible.
Joining a set of unknown work is best done with the `JoinableTaskCollection` class.
It is the responsibility of `SomeClass` in the example above to work with this collection to avoid deadlocks, like this:

```cs
class SomeClass
{
    TaskCompletionSource<bool> tcs = new();
    JoinableTaskCollection jtc;
    JoinableTaskFactory jtf;

    internal SomeClass(JoinableTaskContext joinableTaskContext)
    {
        this.jtc = joinableTaskContext.CreateCollection();
        this.jtf = joinableTaskContext.CreateFactory(this.jtc);
    }

    public async Task MyMethodAsync()
    {
        // Our caller is interested in completion of the TaskCompletionSource,
        // so join the collected effort while waiting, to avoid deadlocks.
        using (this.jtc.Join())
        {
            await this.tcs.Task; /* This analyzer will report warning on this line. */
        }

        /* do more stuff */
    }

    void UnlockProgress()
    {
        this.tcs.TrySetResult(true);
    }

    async Task KeyMasterAsync()
    {
        // As this method must complete to signal the TaskCompletionSource,
        // all of its work must be done within the context of a JoinableTask
        // that belongs to the JoinableTaskCollection.
        // jtf.RunAsync will add the JoinableTask it creates to the jtc collection
        // because jtf was created with jtc as an argument in our constructor.
        await this.jtf.RunAsync(async delegate
        {
            // Because we're in the jtc collection, anyone waiting on MyMethodAsync
            // will automatically lend us use of the main thread if they have it
            // to avoid deadlocks.
            // It does NOT matter whether we use jtf or another JoinableTaskFactory instance
            // at this point.
            await anyOldJTF.SwitchToMainThreadAsync();

            // do some work
            // Unblock others
            this.UnlockProgress();
        });
    }
}
```

Notice how the public API of the class does not need to expose any `JoinableTask`-related types.
It's an implementation detail of the class.

This works fine when the class itself fully controls the work to complete the `TaskCompletionSource`.
When _other_ classes also do work (independently of work started within `SomeClass`), the placement and access to the `JoinableTaskFactory` that is associated with the `JoinableTaskCollection` may need to be elevated so that other classes can access it as well so that *all* the work required to complete the `TaskCompletionSource` will be tracked.

### Task chaining or other means to ensure sequential execution

Task chaining is another technique that can lead to deadlocks.
Task chaining is where a single `Task` is kept in a field and used to call `Task.ContinueWith` to append another Task, and the resulting Task is then assigned to the field, like this:

```cs
class TaskChainingExample
{
    private readonly object lockObject = new();
    private Task lastTask = Task.CompletedTask;

    internal Task AddWorkToEnd(Funk<Task> work)
    {
        lock (this.lockObject)
        {
            return this.lastTask = this.lastTask.ContinueWith(_ => work()).Unwrap();
        }
    }
}
```

(Note: The above example has several *other* issues that would require more code to address, but it illustrates the idea of task chaining.)

The deadlock risk with task chaining is that again, the chain of tasks come together to form a kind of private queue which the `JoinableTaskFactory` has no visibility into.
When a task is not at the front of the queue but its owner blocks the main thread for its completion, and if any other task ahead of it in the queue needs the main thread, a deadlock will result.

For this reason (and several others), task chaining is *not* recommended.
Instead, you can achieve a thread-safe queue that executes work sequentially by utilizing the `ReentrantSemaphore` class.

Fixing the above example would translate to this (allowing for a variety of reentrancy modes):

```cs
class SequentialExecutingQueueExample
{
    private readonly ReentrantSemaphore semaphore = ReentrantSemaphore.Create(initialCount: 1, joinableTaskContext, ReentrancyMode.Stack);

    internal Task AddWorkToEnd(Func<Task> work)
    {
        return semaphore.ExecuteAsync(work);
    }
}
```
