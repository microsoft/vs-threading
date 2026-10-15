# VSTHRD107 Await Task within using expression

The C# `using` statement and `using` declaration require that the used expression or variable implement `IDisposable`.
Because `Task<T>` implements `IDisposable`, one may accidentally omit an `await` operator
and `Dispose` of the `Task<T>` instead of the `T` result itself when `T` derives from `IDisposable`.

## Examples of patterns that are flagged by this analyzer

```csharp
AsyncSemaphore lck;
using (lck.EnterAsync())
{
    // ...
}

using (var releaser = lck.EnterAsync())
{
    // ...
}

using var releaser = lck.EnterAsync();
```

## Solution

Add the `await` operator within the `using` expression or variable initializer.

```csharp
AsyncSemaphore lck;
using (await lck.EnterAsync())
{
    // ...
}

using (var releaser = await lck.EnterAsync())
{
    // ...
}

using var releaser = await lck.EnterAsync();
```
