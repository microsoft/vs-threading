# VSSDK008 Call async methods when in an async method

In a method which is already asynchronous, calls to other methods should
be to their async versions, where they exist.

## Examples of patterns that are flagged by this analyzer

```csharp
Task DoAsync()
{
    file.Read(buffer, 0, 10);
}
```

All methods where an Async-suffixed equivalent exists will produce this warning
when called from a `Task`-returning method.
In addition, calling `Task.Wait()`, `Task<T>.Result` or `Task.GetAwaiter().GetResult()`
will produce this warning.

## Solution

Await the async version of the method:

```csharp
async Task DoAsync()
{
    await file.ReadAsync(buffer, 0, 10);
}
```
