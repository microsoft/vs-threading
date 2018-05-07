# VSTHRD109 Switch instead of assert in async methods

Methods that are or can be async should switch to the main thread when necessary
rather than throw an exception if invoked from a different thread.
This allows callers to invoke any async method from any thread
without having to concern themselves with the threading requirements of a method that
can support its own threading requirements by switching.

## Examples of patterns that are flagged by this analyzer

```csharp
async Task FooAsync() {
    ThreadHelper.ThrowIfNotOnUIThread();
    DoStuff();
    await DoMoreStuff();
}
```

## Solution

Use `await SwitchToMainThreadAsync()` instead:

```csharp
async Task FooAsync() {
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    DoStuff();
    await DoMoreStuff();
}
```
