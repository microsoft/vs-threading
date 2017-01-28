# Diagnostic Analyzers

The following are the diagnostic analyzers installed with the [Microsoft.VisualStudio.Threading.Analyzers][1]
NuGet package.

ID | Title
---- | ---
[VSTHRD002](VSTHRD002.md) | Avoid problematic synchronous waits
[VSTHRD010](VSTHRD010.md) | Use VS services from UI thread
[VSTHRD100](VSTHRD100.md) | Avoid `async void` methods
[VSTHRD101](VSTHRD101.md) | Avoid unsupported async delegates
[VSTHRD106](VSTHRD106.md) | Use `InvokeAsync` to raise async events
[VSTHRD003](VSTHRD003.md) | Avoid awaiting non-joinable tasks in join contexts
[VSTHRD011](VSTHRD011.md) | Avoid using `Lazy<T>` where `T` is `Task<T2>`
[VSTHRD103](VSTHRD103.md) | Call async methods when in an async method
[VSTHRD102](VSTHRD102.md) | Implement internal logic asynchronously
[VSTHRD200](VSTHRD200.md) | Use `Async` suffix for async methods
[VSTHRD105](VSTHRD105.md) | Avoid method overloads that assume TaskScheduler.Current
[VSTHRD012](VSTHRD012.md) | Provide JoinableTaskFactory where allowed
[VSTHRD104](VSTHRD104.md) | Offer async option
[VSTHRD001](VSTHRD001.md) | Avoid legacy thread switching methods

## Severity descriptions

Severity  | IDs     | Analyzer catches...
--------- | ------- | -------------------
Critical  | 1-99    | Code issues that often result in deadlocks
Advisory  | 100-199 | Code that may perform differently than intended or lead to occasional deadlocks
Guideline | 200-299 | Code that deviates from best practices and may limit the benefits of other analyzers

[1]: https://nuget.org/packages/microsoft.visualstudio.threading.analyzers
