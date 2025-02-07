# VSTHRD115 Avoid creating a JoinableTaskContext with an explicit `null` `SynchronizationContext`

Constructing a `JoinableTaskContext` with an explicit `null` `SynchronizationContext` is not recommended as a means to construct an instance for use in unit tests or processes without a main thread.
This is because the constructor will automatically use `SynchronizationContext.Current` in lieu of a non-`null` argument.
If `SynchronizationContext.Current` happens to be non-`null`, the constructor may unexpectedly configure the new instance as if a main thread were present.

## Examples of patterns that are flagged by this analyzer

```csharp
void SetupJTC() {
    this.jtc = new JoinableTaskContext(null, null);
}
```

This code *appears* to configure the `JoinableTaskContext` to not be associated with any `SynchronizationContext`.
But in fact it will be associated with the current `SynchronizationContext` if one is present.

## Solution

If you intended to inherit `SynchronizationContext.Current` to initialize with a main thread,
provide that value explicitly as the second argument to suppress the warning:

```cs
void SetupJTC() {
    this.jtc = new JoinableTaskContext(null, SynchronizationContext.Current);
}
```

If you intended to create a `JoinableTaskContext` for use in a unit test or in a process without a main thread,
call `JoinableTaskContext.CreateNoOpContext()` instead:

```cs
void SetupJTC() {
    this.jtc = JoinableTaskContext.CreateNoOpContext();
}
```

Code fixes are offered to update code to either of the above patterns.
