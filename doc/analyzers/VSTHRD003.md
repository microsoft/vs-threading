# VSTHRD003 Avoid awaiting non-joinable tasks in join contexts

Tasks created from async methods executed outside of a JoinableTaskFactory.Run
delegate cannot be safely awaited inside one.

## Examples of patterns that are flagged by this analyzer

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
void MyMethod()
{
    JoinableTask joinableTask = joinableTaskFactory.RunAsync(() => SomeOperationAsync());
    joinableTaskFactory.Run(async delegate
    {
        await joinableTask;
    });
}
```
