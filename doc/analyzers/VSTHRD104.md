# VSTHRD104 Offer async option

When a publicly accessible method uses `JoinableTaskFactory.Run`, there should be
another way to access the async behavior without synchronously blocking the thread
so that an async caller can be async throughout.

This rule encourages this pattern by recognizing when some method *Foo* exists and
calls `JoinableTaskFactory.Run` that there is also a method *FooAsync*.
The recommended pattern then is for *Foo* to call *FooAsync* from the delegate
passed to `JoinableTaskFactory.Run` so that the implementation only need be written once.

## Examples of patterns that are flagged by this analyzer

```csharp
public void Foo() {
    this.joinableTaskFactory.Run(async delegate {
        await Task.Yield();
    });
}
```

## Solution

Add a FooAsync method, and (optionally) call it from the Foo method:

```csharp
public void Foo() {
    this.joinableTaskFactory.Run(async delegate {
        await FooAsync();
    });
}

public async Task FooAsync() {
    await Task.Yield();
}
```
