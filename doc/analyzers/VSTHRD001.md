# VSTHRD001 Avoid legacy thread switching methods

Switching to the UI thread should be done using `JoinableTaskFactory.SwitchToMainThreadAsync`
rather than legacy methods such as `Dispatcher.Invoke` or `ThreadHelper.Invoke`.
This avoids deadlocks and can reduce threadpool starvation.

## Examples of patterns that are flagged by this analyzer

```csharp
void Foo() {
    ThreadHelper.Generic.Invoke(delegate {
        DoSomething();
    });
}
```

## Solution

Use `await SwitchToMainThreadAsync()` instead, wrapping with `JoinableTaskFactory.Run` if necessary:

```csharp
void Foo() {
    ThreadHelper.JoinableTaskFactory.Run(async delegate {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        DoSomething();
    });
}
```

## Configuration

This analyzer is configurable via the `vs-threading.LegacyThreadSwitchingMembers.txt` file.
See our [configuration](configuration.md) topic for more information.
