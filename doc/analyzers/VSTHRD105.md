# VSTHRD105 Avoid method overloads that assume `TaskScheduler.Current`

Certain methods in the .NET Framework have overloads that allow specifying or omitting
a `TaskScheduler` instance. Always specify one explicitly to avoid the assumed `TaskScheduler.Current`
value, whose behavior is defined by your caller and may vary at runtime.

The "current" `TaskScheduler` is defined by the one that is executing the currently running code.
But when your code is executing without having been scheduled by a `TaskScheduler` (as is the case with most code),
then the `TaskScheduler.Current` property returns `TaskScheduler.Default` which schedules tasks on the thread pool.
This leads many to incorrectly assume that task scheduling methods such as `StartNew` and `ContinueWith` default
to using the thread pool when in fact their default behavior varies by your caller.

This variability in behavior leads to bugs when, for example, `TaskScheduler.Current` returns a `TaskScheduler`
that executes tasks on the application's main thread and/or only executes one task at once, such as one obtained
from the `TaskScheduler.FromCurrentSynchronizationContext()` method.
Such a circumstance often leads to deadlocks or responsiveness issues in the application.

Always explicitly specifying `TaskScheduler.Default` (or other if appropriate) ensures your code will schedule
tasks in a predictable, consistent way.

No diagnostic is produced by this analyzer when `TaskFactory.StartNew` is invoked on a private instance
of `TaskFactory`, since it may in fact have a safe default for `TaskScheduler`.

## Examples of patterns that are flagged by this analyzer

```csharp
private void FirstMethod()
{
    TaskScheduler uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
    Task.Factory.StartNew(
        () =>
        {
            this.AnotherMethod();
        },
        System.Threading.CancellationToken.None,
        TaskCreationOptions.None,
        uiScheduler);
}

private void AnotherMethod()
{
    // TaskScheduler.Current is assumed here, which is determined by our caller.
    var nestedTask = Task.Factory.StartNew(  // analyzer flags this line
        () =>
        {
            // Ooops, we're still on the UI thread when called by FirstMethod.
            // But we might be on the thread pool if someone else called us.
        });
}
```

## Solution

Specify a `TaskScheduler` explicitly to suppress the warning:

```csharp
private void FirstMethod()
{
    TaskScheduler uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
    Task.Factory.StartNew(
        () =>
        {
            this.AnotherMethod();
        },
        CancellationToken.None,
        TaskCreationOptions.None,
        uiScheduler);
}

private void AnotherMethod()
{
    var nestedTask = Task.Factory.StartNew(
        () =>
        {
            // Ah, now we're reliably running on the thread pool. :)
        },
        CancellationToken.None,
        TaskCreationOptions.None,
        TaskScheduler.Default); // Specify TaskScheduler explicitly here.
}
```
