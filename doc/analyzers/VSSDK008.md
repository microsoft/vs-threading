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

## Solution

Await the async version of the method:

```csharp
async Task DoAsync()
{
    await file.ReadAsync(buffer, 0, 10);
}
```
