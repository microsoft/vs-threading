# Diagnostic Analyzers

The following are the diagnostic analyzers installed with the [Microsoft.VisualStudio.Threading.Analyzers][1]
NuGet package.

ID | Title | Severity | Supports | Default diagnostic severity
---- | --- | --- | --- | --- |
[VSTHRD001](VSTHRD001.md) | Avoid legacy thread switching methods | Critical | [1st rule](../threading_rules.md#Rule1) | 🔡 Warning
[VSTHRD002](VSTHRD002.md) | Avoid problematic synchronous waits | Critical | [2nd rule](../threading_rules.md#Rule2) | Warning
[VSTHRD003](VSTHRD003.md) | Avoid awaiting foreign Tasks | Critical | [3rd rule](../threading_rules.md#Rule3) | Warning
[VSTHRD004](VSTHRD004.md) | Await SwitchToMainThreadAsync | Critical | [1st rule](../threading_rules.md#Rule1) | Error
[VSTHRD010](VSTHRD010.md) | Invoke single-threaded types on Main thread | Critical | [1st rule](../threading_rules.md#Rule1) | Warning
[VSTHRD011](VSTHRD011.md) | Use `AsyncLazy<T>` | Critical | [3rd rule](../threading_rules.md#Rule3) | Error
[VSTHRD012](VSTHRD012.md) | Provide JoinableTaskFactory where allowed | Critical | [All rules](../threading_rules.md) | Warning
[VSTHRD100](VSTHRD100.md) | Avoid `async void` methods | Advisory | | Warning
[VSTHRD101](VSTHRD101.md) | Avoid unsupported async delegates | Advisory | [VSTHRD100](VSTHRD100.md) | Warning
[VSTHRD102](VSTHRD102.md) | Implement internal logic asynchronously | Advisory | [2nd rule](../threading_rules.md#Rule2) | Info
[VSTHRD103](VSTHRD103.md) | Call async methods when in an async method | Advisory | | Warning
[VSTHRD104](VSTHRD104.md) | Offer async option | Advisory | | Info
[VSTHRD105](VSTHRD105.md) | Avoid method overloads that assume `TaskScheduler.Current` | Advisory | | Warning
[VSTHRD106](VSTHRD106.md) | Use `InvokeAsync` to raise async events | Advisory | | Warning
[VSTHRD107](VSTHRD107.md) | Await Task within using expression | Advisory | | Error
[VSTHRD108](VSTHRD108.md) | Assert thread affinity unconditionally | Advisory | [1st rule](../threading_rules.md#Rule1), [VSTHRD010](VSTHRD010.md) | Warning
[VSTHRD109](VSTHRD109.md) | Switch instead of assert in async methods | Advisory | [1st rule](../threading_rules.md#Rule1) | Error
[VSTHRD110](VSTHRD110.md) | Observe result of async calls | Advisory | | Warning
[VSTHRD111](VSTHRD111.md) | Use `.ConfigureAwait(bool)` | Advisory | | Hidden
[VSTHRD112](VSTHRD112.md) | Implement `System.IAsyncDisposable` | Advisory | | Info
[VSTHRD113](VSTHRD113.md) | Check for `System.IAsyncDisposable` | Advisory | | Info
[VSTHRD114](VSTHRD114.md) | Avoid returning null from a `Task`-returning method. | Advisory | | Warning
[VSTHRD200](VSTHRD200.md) | Use `Async` naming convention | Guideline | [VSTHRD103](VSTHRD103.md) | Warning

## Severity descriptions

Severity  | IDs     | Analyzer catches...
--------- | ------- | -------------------
Critical  | 1-99    | Code issues that often result in deadlocks
Advisory  | 100-199 | Code that may perform differently than intended or lead to occasional deadlocks
Guideline | 200-299 | Code that deviates from best practices and may limit the benefits of other analyzers

## Default diagnostic severity legend

| Icon | Meaning |
| ---- | ------- |
| 🔡 | The analyzer only produces diagnostics when configured.

## Configuration

Some analyzers' behavior can be configured. See our [configuration](configuration.md) topic for more information.

[1]: https://nuget.org/packages/microsoft.visualstudio.threading.analyzers
