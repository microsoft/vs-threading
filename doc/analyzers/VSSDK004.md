# VSSDK004 Avoid unsupported async delegates

C# allows you to define async delegates or lambdas and use them in contexts that accept
void-returning delegates, thus creating an `async void` method such as is forbidden by
[VSSDK003](VSSDK003.md), but is much harder to catch when simply looking at the code
because for the same syntax, the C# compiler will create an `async Func<Task>` delegate
or an `async void` delegate based on the type expected by the method being invoked.

This analyzer helps prevent inadvertent creation of `async void` delegates. 

## Examples of patterns that are flagged by this analyzer

```csharp
void StartWatching(ObservableCollection<string> oc)
{
    // This delegate becomes an "async void" method to match the EventHandler delegate type.
    oc.CollectionChanged += async () =>
    {
        await Task.Yield();
    };
}

void StartWatching(ObservableCollection<string> oc)
{
    // This delegate becomes an "async void" method to match the Action delegate type.
    Callback(async () =>
    {
        await Task.Yield();
    });
}

void Callback(Action action)
{
    // out of scope of sample
}
```

## Solution

1. Wrap the asynchronous behavior in another method that accepts a `Func<Task>` delegate.
1. Change the receiving method's expected delegate type to one that returns a `Task` or `Task<T>`.
1. Implement the delegate synchronously.

```csharp
void StartWatching(ObservableCollection<string> oc)
{
    oc.CollectionChanged += () =>
    {
        // The outer delegate is synchronous, but kicks off async work via a method that accepts an async delegate.
        joinableTaskFactory.RunAsync(async delegate {
            await Task.Yield();
        });
    };
}

void StartWatching(ObservableCollection<string> oc)
{
    // This delegate becomes an "async Task" method to match the Func<Task> delegate type.
    Callback(async () =>
    {
        await Task.Yield();
    });
}

void Callback(Func<Task> action)
{
    // out of scope of sample
}
```
