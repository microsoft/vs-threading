# VSTHRD003 Avoid awaiting foreign Tasks

Tasks that are created and run from another context (not within the currently running method or delegate)
should not be returned or awaited on. Doing so can result in deadlocks because awaiting a `Task`
does not result in the awaiter "joining" the effort such that access to the main thread is shared.
If the awaited `Task` requires the main thread, and the caller that is awaiting it is blocking the
main thread, a deadlock will result.

When required to await a task that was started earlier, start it within a delegate passed to
`JoinableTaskFactory.RunAsync`, storing the resulting `JoinableTask` in a field or variable.
You can safely await the `JoinableTask` later.

## Examples of patterns that are flagged by this analyzer

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

## Solution

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
