# Diagnostic Analyzers

The following are the diagnostic analyzers installed with the [Microsoft.VisualStudio.Threading.Analyzers][1]
NuGet package.

ID | Title
---- | ---
[VSSDK001](VSSDK001.md) | Avoid problematic synchronous waits
[VSSDK002](VSSDK002.md) | Use VS services from UI thread
[VSSDK003](VSSDK003.md) | Avoid `async void` methods
[VSSDK004](VSSDK004.md) | Avoid unsupported async delegates
[VSSDK005](VSSDK005.md) | Use `InvokeAsync` to raise async events
[VSSDK006](VSSDK006.md) | Avoid awaiting non-joinable tasks in join contexts
[VSSDK007](VSSDK007.md) | Avoid using `Lazy<T>` where `T` is `Task<T2>`
[VSSDK008](VSSDK008.md) | Call async methods when in an async method
[VSSDK009](VSSDK009.md) | Implement internal logic asynchronously
[VSSDK010](VSSDK010.md) | Use `Async` suffix for async methods
[VSSDK011](VSSDK011.md) | Avoid method overloads that assume TaskScheduler.Current

[1]: https://nuget.org/packages/microsoft.visualstudio.threading.analyzers
