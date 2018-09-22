# VSTHRD111 Use `.ConfigureAwait(bool)`

Some code bases, particularly libraries with no affinity to an app's UI thread, are advised to use `.ConfigureAwait(false)` for each and every _await_ because it can avoid deadlocks after those calls start on an application's UI thread and the app later decides to synchronously block the UI thread waiting for those tasks to finish. Using `.ConfigureAwait(false)` also allows continuations to switch to a background thread even when no synchronous blocking would cause a deadlock, which makes for a more responsive application and possibly higher throughput of async operations.

Note that this scenario can also be solved using the `JoinableTaskFactory`, but many class libraries may not wish to depend on the application proffers an instance of that type to the library. Where JoinableTaskFactory _does_ apply, use of `.ConfigureAwait(false)` is _not_ recommended. See [this topic](https://github.com/Microsoft/vs-threading/blob/master/doc/cookbook_vs.md#should-i-await-a-task-with-configureawaitfalse) for more on when `.ConfigureAwait(false)` and `.ConfigureAwait(true)` are appropriate.

**This analyzer's diagnostics are *hidden* by default**. You should enable the rule for libraries that use to require this await suffix.

## Examples of patterns that are flagged by this analyzer

Any await on `Task` or `ValueTask` without the `.ConfigureAwait(bool)` method called on it will be flagged.

```csharp
async Task FooAsync() {
    await DoStuffAsync(); // This line is flagged
    await DoMoreStuffAsync(); // This line is flagged
}

async Task DoStuffAsync() { /* ... */ }
async ValueTask DoMoreStuffAsync() { /* ... */ }
```

## Solution

Add `.ConfigureAwait(false)` or `.ConfigureAwait(true)` to the awaited `Task` or `ValueTask`.

```csharp
async Task FooAsync() {
    await DoStuffAsync().ConfigureAwait(true);
    await DoMoreStuffAsync().ConfigureAwait(false);
}

async Task DoStuffAsync() { /* ... */ }
async ValueTask DoMoreStuffAsync() { /* ... */ }
```

Code fixes are offered for for this diagnostic to add either `.ConfigureAwait(false)` or `.ConfigureAwait(true)`
to an awaited expression.
