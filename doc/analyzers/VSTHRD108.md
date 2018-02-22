# VSTHRD108 Assert thread affinity unconditionally

When a method has thread affinity and throws if called from the wrong thread, it should do so without regard to any other condition. This helps ensure the caller will notice early during development that they are calling from the wrong thread. Extra conditions can hide the problem till end users discover an application failure.

## Examples of patterns that are flagged by this analyzer

```csharp
private int? age;

public int GetAge()
{
    if (!this.age.HasValue)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        this.age = DoExpensiveUIThreadWork();
    }

    return this.age.Value;
}
```

The problem here is that although the UI thread is only strictly required when the field is actually initialized, callers generally cannot predict whether they will be the first or a subsequent caller. If they call from a background thread and tend to be a subsequent caller, no exception will be thrown. But under some conditions in the app when they happen to be the first caller, they'll fail at runtime because they're calling from the background thread.

## Solution

Move the code that throws when not on the UI thread outside the conditional block.

```csharp
private int? age;

public int GetAge()
{
    ThreadHelper.ThrowIfNotOnUIThread();
    if (!this.age.HasValue)
    {
        this.age = DoExpensiveUIThreadWork();
    }

    return this.age.Value;
}
```

## Configuration

This analyzer is configurable via the `vs-threading.MainThreadAssertingMethods.txt` file.
See our [configuration](configuration.md) topic for more information.
