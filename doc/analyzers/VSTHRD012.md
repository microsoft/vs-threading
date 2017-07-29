# VSTHRD012 Provide `JoinableTaskFactory` where allowed

When constructing types or calling methods that accept a `JoinableTaskFactory`
or `JoinableTaskContext`, take the opportunity to supply one if your application
has a main thread with a single threaded `SynchronizationContext` such as WPF or WinForms.

## Examples of patterns that are flagged by this analyzer

```csharp
void F() {
    var o = new AsyncLazy<int>(() => Task.FromResult(1)); // analyzer flags this line
}
```

## Solution

Call the overload that accepts a `JoinableTaskFactory` or `JoinableTaskContext` instance:

```csharp
void F() {
    var o = new AsyncLazy<int>(() => Task.FromResult(1), this.JoinableTaskFactory);
}
```

## Suppression

You can suppress the diagnostic by explicitly specifying `null` for the argument:

```csharp
void F() {
    var o = new AsyncLazy<int>(() => Task.FromResult(1), null);
}
```
