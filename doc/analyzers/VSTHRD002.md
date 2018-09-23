# VSTHRD002 Avoid problematic synchronous waits

Synchronously waiting on `Task`, `ValueTask`, or awaiters is dangerous and may cause dead locks.

## Examples of patterns that are flagged by this analyzer

```csharp
void DoSomething()
{
    DoSomethingElseAsync().Wait();
    DoSomethingElseAsync().GetAwaiter().GetResult();
    var result = CalculateSomethingAsync().Result;
}
```

## Solution

Please consider the following options:

1. Switch to asynchronous wait if the caller is already a "async" method.
1. Change the chain of callers to be "async" methods, and then change this code to be asynchronous await.
1. Use `JoinableTaskFactory.Run()` to wait on the tasks or awaiters.

```csharp
async Task DoSomethingAsync()
{
    await DoSomethingElseAsync();
    await DoSomethingElseAsync();
    var result = await CalculateSomethingAsync();
}

void DoSomething()
{
    joinableTaskFactory.Run(async delegate
    {
        await DoSomethingElseAsync();
        await DoSomethingElseAsync();
        var result = await CalculateSomethingAsync();
    });
}
```

Refer to [Asynchronous and multithreaded programming within VS using the JoinableTaskFactory][1] for more information.

[1]: http://blogs.msdn.com/b/andrewarnottms/archive/2014/05/07/asynchronous-and-multithreaded-programming-within-vs-using-the-joinabletaskfactory.aspx
