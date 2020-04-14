# VSTHRD113 Check for `System.IAsyncDisposable`

The `Microsoft.VisualStudio.Threading.IAsyncDisposable` interface is obsolete now that the
`System.IAsyncDisposable` interface has been defined for .NET Standard 2.0 and .NET Framework 4.6.1
by the [`Microsoft.Bcl.AsyncInterfaces` NuGet package](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces).

Existing code that tests for the `Microsoft.VisualStudio.Threading.IAsyncDisposable` interface on some object should also check for `System.IAsyncDisposable` and behave similarly in either case.
New code should consider only supporting the new `System.IAsyncDisposable` interface.

## Examples of patterns that are flagged by this analyzer

The following code only checks for the obsolete interface and is flagged by this diagnostic:

```cs
using Microsoft.VisualStudio.Threading;

if (obj is IAsyncDisposable asyncDisposable)
{
    await asyncDisposable.DisposeAsync();
}
```

## Solution

Fix this by adding a code branch for the new interface that behaves similarly
within the same containing code block:

```cs
if (obj is Microsoft.VisualStudio.Threading.IAsyncDisposable vsThreadingAsyncDisposable)
{
    await vsThreadingAsyncDisposable.DisposeAsync();
}
else if (obj is System.IAsyncDisposable bclAsyncDisposable)
{
    await bclAsyncDisposable.DisposeAsync();
}
```
