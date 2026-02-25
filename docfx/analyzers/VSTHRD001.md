# VSTHRD001 Avoid legacy thread switching methods

Switching to the UI thread should be done using `JoinableTaskFactory.SwitchToMainThreadAsync`
rather than legacy methods such as `Dispatcher.Invoke` or `ThreadHelper.Invoke`.
This avoids deadlocks and can reduce threadpool starvation.

## Examples of patterns that are flagged by this analyzer

```csharp
ThreadHelper.Generic.Invoke(delegate {
    DoSomething();
});
```

or

```cs
Dispatcher.CurrentDispatcher.BeginInvoke(delegate {
    DoSomething();
});
```

## Solution

Use `await SwitchToMainThreadAsync()` instead, wrapping with the `JoinableTaskFactory`'s `Run` or `RunAsync` method if necessary:

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
See our doc on [consuming `JoinableTaskFactory` from a library](https://github.com/microsoft/vs-threading/blob/main/doc/library_with_jtf.md) for more information.

### Replacing Dispatcher.BeginInvoke

When updating calls to `Dispatcher.BeginInvoke`, there are a few considerations to consider.

1. `BeginInvoke` schedules the delegate for execution later.
1. `BeginInvoke` always executes the delegate on the dispatcher's thread.
1. `BeginInvoke` schedules the delegate at some given priority, or default priority determined by the dispatcher.

To resolve a warning for such code, it is often sufficient to replace it with this, which is *roughly* equivalent:

```cs
await joinableTaskFactory.RunAsync(async delegate {
    await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
    DoSomething();
})
```

The first line in the delegate is necessary to match the behaviors of 1 and 2 on the above list.
When the caller is known to already be on the main thread, you can simplify it slightly to this:

```cs
await joinableTaskFactory.RunAsync(async delegate {
    await Task.Yield();
    DoSomething();
})
```

Matching behavior 3 on the list above may be important when the dispatcher priority is specified in the BeginInvoke call and was chosen for a particular reason.
In such a case, you can ensure that `JoinableTaskFactory` matches that priority instead of using its default by creating a special `JoinableTaskFactory` instance with the priority setting you require using the [`JoinableTaskFactory.WithPriority`](https://learn.microsoft.com/dotnet/api/microsoft.visualstudio.threading.dispatcherextensions.withpriority?view=visualstudiosdk-2022) method.

Altogether, this might look like:

```cs
await joinableTaskFactory.WithPriority(DispatcherPriority.DataBind).RunAsync(async delegate {
    await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
    DoSomething();
})
```

## Configuration

This analyzer is configurable via the `vs-threading.LegacyThreadSwitchingMembers.txt` file.
See our [configuration](configuration.md) topic for more information.
