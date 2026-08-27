# VSTHRD202 Remove unnecessary async state machine

This informational rule identifies an `async` method whose only `await` is the final operation and whose awaited expression produces exactly the same `Task` or `Task<T>` type as the method returns. Returning that task directly avoids allocating and running an async state machine.

## Examples of patterns that are flagged by this analyzer

```csharp
async Task<string> DoSomethingAsync()
{
    return await SomethingElseAsync();
}
```

The analyzer also recognizes `.ConfigureAwait(bool)` on the final expression:

```csharp
async Task DoSomethingAsync()
{
    await SomethingElseAsync().ConfigureAwait(false);
}
```

It does not report methods with multiple awaits, a non-terminal await, an await inside a `try` statement, a `using` declaration, a different returned task type, or a return type other than `Task` or `Task<T>`.

## Code fixes

The minimal code fix removes `async`, `await`, and any `.ConfigureAwait(bool)` call:

```csharp
Task<string> DoSomethingAsync()
{
    return SomethingElseAsync();
}
```

This changes how a synchronous exception from `SomethingElseAsync()` or from earlier code in the method is observed: it is thrown directly instead of being stored in the returned task. A second code fix wraps such exceptions in the returned task by adding a `try`/`catch`:

```csharp
Task<string> DoSomethingAsync()
{
    try
    {
        return SomethingElseAsync();
    }
    catch (Exception ex)
    {
        return Task.FromException<string>(ex);
    }
}
```

## Caveats

Removing the state machine is a performance optimization, but the two forms are not identical:

* While debugging a continuation in `SomethingElseAsync`, `DoSomethingAsync` no longer appears as an async frame in the call stack.
* Exception stack traces can change. The minimal fix also changes synchronous exceptions from a faulted returned task into exceptions thrown directly to the caller. The try/catch fix keeps ordinary exceptions in the returned task, but a synchronously thrown `OperationCanceledException` produces a faulted task instead of the canceled task produced by an async state machine.
* The returned task is the original task instead of a task created by the wrapper method, which can make task identity observable.
* Compiler warning CS4014 for unawaited calls is only produced within an `async` method. Removing `async` may therefore stop the compiler from warning about other unawaited calls in the method. Consider enabling [VSTHRD110](VSTHRD110.md) before applying this optimization broadly.

The try/catch fix is only offered when the target framework provides `Task.FromException`.

These tradeoffs are why this diagnostic has an **Informational** default severity. Suppress or disable it when preserving the debugging experience or the original method boundary is more important than avoiding the state machine.
