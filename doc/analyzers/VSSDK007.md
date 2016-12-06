# VSSDK007 Avoid using `Lazy<T>` where `T` is `Task<T2>`

The `Lazy<T>` type executes the value factory just once and
the value factory inherits the context of the first one to request the
`Lazy<T>.Value` property's value.
When `T` is `Task<T2>` (because the value factory is an async method),
if the first caller had no access to the main thread, and the value factory
requires it, it will block. If later a second caller calls the `Value` property
and that second caller is blocking the UI thread for its result, it will deadlock. 

## Examples of patterns that are flagged by this analyzer

```csharp
var lazy = new Lazy<Task<int>>(async delegate // analyzer flags this line
{
    await Task.Yield();
    return 3;
});

int value = await lazy.Value;
```

## Solution

Instead of using `Lazy<Task<T>>` when the value factory is async, use `AsyncLazy<T>`:

```csharp
var lazy = new AsyncLazy<int>(async delegate
{
    await Task.Yield();
    return 3;
});

int value = await lazy.GetValueAsync();
```
