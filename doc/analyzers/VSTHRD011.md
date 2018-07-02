# VSTHRD011 Use `AsyncLazy<T>`

The `Lazy<T>` type executes the value factory just once and
the value factory inherits the context of the first one to request the
`Lazy<T>.Value` property's value. This can lead to deadlocks when
the value factory attempts to switch to the main thread.

## Examples of patterns that are flagged by this analyzer

### Using `Lazy<T>` where `T` is `Task<T2>`

When `T` is `Task<T2>` (because the value factory is an async method),
if the first caller had no access to the main thread, and the value factory
requires it, it will block. If later a second caller calls the `Value` property
and that second caller is blocking the UI thread for its result, it will deadlock.

```csharp
var lazy = new Lazy<Task<int>>(async delegate // analyzer flags this line
{
    await Task.Yield();
    return 3;
});

int value = await lazy.Value;
```

### Using synchronously blocking methods in `Lazy<T>` value factories

When the value factory passed to the `Lazy<T>` constructor calls synchronously
blocking methods such as `JoinableTaskFactory.Run`, only the first caller
can help any required transition to the main thread.

```csharp
var lazy = new Lazy<int>(delegate
{
    return joinableTaskFactory.Run(async delegate { // analyzer flags this line
        int result = await SomeAsyncMethod();
        return result + 3;
    });
});

int value = lazy.Value;
```

## Solution

Use `AsyncLazy<T>` with an async value factory:

```csharp
var lazy = new AsyncLazy<int>(async delegate
{
    await Task.Yield();
    return 3;
});

int value = await lazy.GetValueAsync();
```
