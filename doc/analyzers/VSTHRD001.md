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

In the above example, we obtain a `JoinableTaskFactory` instance from the `ThreadHelper.JoinableTaskFactory` static property
as it exists within Visual Studio itself. Other applications should create and expose their own `JoinableTaskContext` and/or `JoinableTaskFactory` for use in code that run in these applications. 
See our doc on [consuming `JoinableTaskFactory` from a library](https://github.com/microsoft/vs-threading/blob/master/doc/library_with_jtf.md) for more information.

## Configuration

This analyzer is configurable via the `vs-threading.LegacyThreadSwitchingMembers.txt` file.
See our [configuration](configuration.md) topic for more information.
