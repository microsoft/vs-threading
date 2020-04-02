# VSTHRD112 Avoid returning a null Task

Returning `null` from a non-async `Task`/`Task<T>` method will cause a `NullReferenceException` at runtime. This problem can be avoided by returning `Task.CompletedTask`, `Task.FromResult<T>(null)` or `Task.FromResult(default(T))` instead.

## Examples of patterns that are flagged by this analyzer

Any non-async `Task` returning method with an explicit `return null;` will be flagged.

```csharp
Task DoAsync() {
    return null;
}

Task<object> GetSomethingAsync() {
    return null;
}
```

## Solution

Return a task like `Task.CompletedTask` or `Task.FromResult`.

```csharp
Task DoAsync() {
    return Task.CompletedTask;
}

Task<object> GetSomethingAsync() {
    return Task.FromResult<object>(null);
}
```
