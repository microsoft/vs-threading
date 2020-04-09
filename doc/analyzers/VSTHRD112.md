# VSTHRD112 Implement `System.IAsyncDisposable`

The `Microsoft.VisualStudio.Threading.IAsyncDisposable` interface is obsolete now that the
`System.IAsyncDisposable` interface has been defined for .NET Standard 2.0 and .NET Framework 4.6.1
by the [`Microsoft.Bcl.AsyncInterfaces` NuGet package](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces).

New classes looking to support async disposable should use `System.IAsyncDisposable` instead of `Microsoft.VisualStudio.Threading.IAsyncDisposable`.
Existing classes that already implement `Microsoft.VisualStudio.Threading.IAsyncDisposable` should *also* implement `System.IAsyncDisposable` so the async disposal option will be recognized by code that only checks for presence of the new interface.

## Examples of patterns that are flagged by this analyzer

This class only implements `Microsoft.VisualStudio.Threading.IAsyncDisposable` and will produce the VSTHRD112 diagnostic:

```cs
using Microsoft.VisualStudio.Threading;

class SomeClass : IAsyncDisposable
{
    public Task DisposeAsync()
    {
    }
}
```

## Solution

Implement `System.IAsyncDisposable` in addition to (or instead of) `Microsoft.VisualStudio.Threading.IAsyncDisposable`.
Add a package reference to `Microsoft.Bcl.AsyncInterfaces` if the compiler cannot find `System.IAsyncDisposable`.

In this example, only `System.IAsyncDisposable` is supported, which is acceptable:

```cs
using System;

class SomeClass : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
    }
}
```

In this next example, both interfaces are supported:

```cs
class SomeClass : System.IAsyncDisposable, Microsoft.VisualStudio.Threading.IAsyncDisposable
{
    Task Microsoft.VisualStudio.Threading.IAsyncDisposable.DisposeAsync()
    {
        // Simply forward the call to the other DisposeAsync overload.
        System.IAsyncDisposable self = this;
        return self.Dispose().AsTask();
    }

    ValueTask System.IAsyncDisposable.DisposeAsync()
    {
        // Interesting dispose logic here.
    }
}
```

In the above example both `DisposeAsync` methods are explicit interface implementations.
Promoting one of the methods to be `public` is typically advised.
If one of these methods was already public and the class itself is public or protected, keep the same method public to avoid an API binary breaking change.

An automated code fix may be offered for VSTHRD112 diagnostics.
