# VSTHRD010 Invoke single-threaded types on Main thread

Acquiring, casting, or invoking single-threaded objects should be done after ensuring
that your code is running on the main thread.

This analyzer can be configured to:
1. Recognize the objects that are single-threaded that are unique to your app or library.   
2. Recognize synchronous methods that verify the caller is already on the main thread.
3. Recognize methods that switch to the main thread when the caller awaits them.
   Calls to `JoinableTaskFactory.SwitchToMainThreadAsync` methods are pre-configured.

See our [configuration](configuration.md) topic for more information.

## Examples of patterns that are flagged by this analyzer

This example is based on the configuration available from the Visual Studio SDK
that defines `IVs*` interfaces as requiring the main thread.

```csharp
private void CallVS()
{
    IVsSolution sln = GetIVsSolution();
    sln.SetProperty(); // This analyzer will report warning on this invocation.
}
```

## Solution

First ensure you are running on the main thread before interacting with single-threaded objects.
Either throw when you are not on the appropriate thread, or explicitly switch to the 
main thread.

This solution example is based on the configuration available from the Visual Studio SDK
that defines `ThreadHelper.ThrowIfNotOnUIThread()` as one which throws if the caller
is not already on the main thread.

```csharp
private void CallVS()
{
    ThreadHelper.ThrowIfNotOnUIThread();
    IVsSolution sln = GetIVsSolution();
    sln.SetProperty(); // This analyzer will report warning on this invocation.
}

private async Task CallVSAsync()
{
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    IVsSolution sln = GetIVsSolution();
    sln.SetProperty(); // This analyzer will report warning on this invocation.
}
```

Refer to [Asynchronous and multithreaded programming within VS using the JoinableTaskFactory](http://blogs.msdn.com/b/andrewarnottms/archive/2014/05/07/asynchronous-and-multithreaded-programming-within-vs-using-the-joinabletaskfactory/) for more info.
