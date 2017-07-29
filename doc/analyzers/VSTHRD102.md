# VSTHRD102 Implement internal logic asynchronously

Internal or private methods may be invoked by public methods that are asynchronous.
If the internal method has an opportunity to do work asynchronously, it should do so
in order that async public members can truly be async.  

## Examples of patterns that are flagged by this analyzer

```csharp
public void PublicMethod()
{
    DoWork();
}

public async Task PublicMethodAsync()
{
    DoWork();
    await Task.Yield();
}

internal void DoWork()
{
    joinableTaskFactory.Run(async delegate // Analyzer will flag this line
    {
        await DoSomethingAsync();
    });
}
```

Note how `DoWork()` synchronously blocks for both `PublicMethod()` and `PublicMethodAsync()`.

## Solution

Remove the synchronously blocking behavior and make the method async.

```csharp
public void PublicMethod()
{
    joinableTaskFactory.Run(() => PublicMethodAsync());
}

public async Task PublicMethodAsync()
{
    await DoWorkAsync();
    await Task.Yield();
}

internal async Task DoWorkAsync()
{
    await DoSomethingAsync();
}
```

Note how `DoWorkAsync()` now allows `PublicMethodAsync()` to do its work asynchronously
while `PublicMethod()` continues to synchronously block, giving your external caller the option
as to whether to do work asynchronously or synchronously. 
