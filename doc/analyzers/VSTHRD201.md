# VSTHRD201 Check cancellation after `SwitchToMainThreadAsync`

Code that should not run if a `CancellationToken` is canceled should call
`CancellationToken.ThrowIfCancellationRequested()` explicitly rather than
relying on `JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken)` to
throw `OperationCanceledException`.

While passing a `CancellationToken` to `SwitchToMainThreadAsync` is an effective
way to abort a switch to the main thread that is taking too long, it is not a guarantee
that code execution will not continue when the token is canceled.

The `SwitchToMainThreadAsync(CancellationToken)` method *will* throw `OperationCanceledException`
back at the caller if **all** these conditions are met:

1. The `CancellationToken` passed to it is canceled.
2. The caller was not on the main thread already.
3. The request to resume the main thread is fulfilled by the threadpool before the main thread can respond to the request.

OR when **all** these conditions are met:

1. The `CancellationToken` passed to it is already canceled before calling `SwitchToMainThreadAsync`.
2. The caller was not on the main thread already.

When the token is canceled, a request to resume the async method is scheduled to the threadpool.
If the original request to resume on the main thread executes before the threadpool responds to the request,
the request made to `SwitchToMainThreadAsync` is considered fulfilled and therefore it returns without throwing
to the caller.
However, if the threadpool responds to the request and resumes the async method before the main thread, then
the request to cancel is considered successful and `SwitchToMainThreadAsync` will
throw `OperationCanceledException` back to the caller.

Depending on the intent of the caller of this `SwitchToMainThreadAsync` method, it may be appropriate
to explicitly call `CancellationToken.ThrowIfCancellationRequested()` after a switch to the main thread
to guarantee an exception is always thrown if the token is canceled.

## Examples of patterns that are flagged by this analyzer

```csharp
async Task DoSomethingAsync(CancellationToken cancellationToken)
{
    await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken); // analyzer flags this line
    DoSomeWork();
}
```

## Solution

Throw if cancellation is requested, even if the code reaches the main thread:

```csharp
async Task DoSomethingAsync(CancellationToken cancellationToken)
{
    await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    DoSomeWork();
}
```

Or check the cancellation status another way:

```csharp
async Task DoSomethingAsync(CancellationToken cancellationToken)
{
    await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
    if (cancellationToken.IsCancellationRequested)
    {
        return;
    }

    DoSomeWork();
}
```

A code fix exists to automatically add the `ThrowIfCancellationRequested()` method call.

However, if the remainder of the work in the async method after the switch is fast and it is more desirable
to complete the work and return rather than throw back to the caller, this analyzer may be suppressed
either at the project level or locally using `#pragma` like this:

```csharp
async Task DoSomethingAsync(CancellationToken cancellationToken)
{
#pragma warning disable VSTHRD201
    await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
#pragma warning restore VSTHRD201
    DoSomeWork();
}
```
