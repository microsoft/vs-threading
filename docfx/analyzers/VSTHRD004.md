# VSTHRD004 Await SwitchToMainThreadAsync

Calls to `JoinableTaskFactory.SwitchToMainThreadAsync` must be awaited
or it is a no-op.

## Examples of patterns that are flagged by this analyzer

```csharp
void MyMethod()
{
    joinableTaskFactory.SwitchToMainThreadAsync();
    UIThreadBoundWork();
}
```

## Solution

Add `await` in front of the call to `JoinableTaskFactory.SwitchToMainThreadAsync`.

This requires an async context. Here, we fix the problem by making the outer method async:

```csharp
async Task MyMethodAsync()
{
    await joinableTaskFactory.SwitchToMainThreadAsync();
    UIThreadBoundWork();
}
```


Alternatively if found in a synchronous method that cannot be made async,
this failure can be fixed by lifting the code into a delegate passed to `JoinableTaskFactory.Run`:

```csharp
void MyMethod()
{
    joinableTaskFactory.Run(async delegate
    {
        await joinableTaskFactory.SwitchToMainThreadAsync();
        UIThreadBoundWork();
    });
}
```
