# VSTHRD107 Using `Task<T>` instead of result `T`

The C# `using` statement requires that the used expression implement `IDisposable`.
Because `Task<T>` implements `IDisposable`, one may accidentally omit an `await` operator
and `Dispose` of the `Task<T>` instead of the `T` result itself when `T` derives from `IDisposable`.

## Examples of patterns that are flagged by this analyzer

```csharp
AsyncSemaphore lck;
using (lck.EnterAsync())
{
    // ...
}
```

## Solution

Add the `await` operator within the `using` expression.

```csharp
AsyncSemaphore lck;
using (await lck.EnterAsync())
{
    // ...
}
```
