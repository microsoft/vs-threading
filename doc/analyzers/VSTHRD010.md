# VSTHRD010 Invoke single-threaded types on Main thread

Acquiring, casting, or invoking Visual Studio services should be done after ensuring
that your code is running on the UI thread.

## Examples of patterns that are flagged by this analyzer

```csharp
private void CallVS()
{
    IVsSolution sln = GetIVsSolution();
    sln.SetProperty(); // This analyzer will report warning on this invocation.
}
```

## Solution

First ensure you are running on the UI thread before interacting with a Visual Studio service.
Either throw when you are not on the appropriate thread, or explicitly switch to the 
UI thread.

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
