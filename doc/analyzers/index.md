# Diagnostic Analyzers

The following are the diagnostic analyzers installed with the [Microsoft.VisualStudio.Threading.Analyzers][1]
NuGet package.

ID | Title | Severity | Supports
---- | --- | --- | --- |
[VSTHRD001](VSTHRD001.md) | Use JTF.SwitchToMainThread to switch | Critical | [1st rule](../threading_rules.md#Rule1)
[VSTHRD002](VSTHRD002.md) | Use JTF.Run to block | Critical | [2nd rule](../threading_rules.md#Rule2)
[VSTHRD003](VSTHRD003.md) | Use JTF.RunAsync to block later | Critical | [3rd rule](../threading_rules.md#Rule3)
[VSTHRD010](VSTHRD010.md) | Invoke single-threaded types on Main thread | Critical | [1st rule](../threading_rules.md#Rule1)
[VSTHRD011](VSTHRD011.md) | Avoid using `Lazy<T>` where `T` is `Task<T2>` | Critical | [3rd rule](../threading_rules.md#Rule3)
[VSTHRD012](VSTHRD012.md) | Provide JoinableTaskFactory where allowed | Critical | [All rules](../threading_rules.md)
[VSTHRD100](VSTHRD100.md) | Avoid `async void` methods | Advisory
[VSTHRD101](VSTHRD101.md) | Avoid unsupported async delegates | Advisory | [VSTHRD100](VSTHRD100.md)
[VSTHRD102](VSTHRD102.md) | Implement internal logic asynchronously | Advisory | [2nd rule](../threading_rules.md#Rule2)
[VSTHRD103](VSTHRD103.md) | Use async option | Advisory
[VSTHRD104](VSTHRD104.md) | Offer async option | Advisory
[VSTHRD105](VSTHRD105.md) | Avoid implicit use of TaskScheduler.Current | Advisory
[VSTHRD106](VSTHRD106.md) | Use `InvokeAsync` to raise async events | Advisory
[VSTHRD107](VSTHRD107.md) | Await Task within using expression | Advisory
[VSTHRD108](VSTHRD108.md) | Assert thread affinity unconditionally | Advisory | [1st rule](../threading_rules.md#Rule1), [VSTHRD010](VSTHRD010.md)
[VSTHRD200](VSTHRD200.md) | Use `Async` naming convention | Guideline | [VSTHRD103](VSTHRD103.md)

## Severity descriptions

Severity  | IDs     | Analyzer catches...
--------- | ------- | -------------------
Critical  | 1-99    | Code issues that often result in deadlocks
Advisory  | 100-199 | Code that may perform differently than intended or lead to occasional deadlocks
Guideline | 200-299 | Code that deviates from best practices and may limit the benefits of other analyzers

## Configuration

Some analyzers' behavior can be configured. See our [configuration](configuration.md) topic for more information.

[1]: https://nuget.org/packages/microsoft.visualstudio.threading.analyzers
