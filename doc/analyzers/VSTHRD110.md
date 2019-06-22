# VSTHRD110 Observe result of async calls

Tasks returned from async methods should be awaited, or assigned to a variable for observation later.
Methods that return `Task`s often complete and report their work via the `Task` they return, and simply
invoking the method does not guarantee that its work is complete nor successful. Using the `await` keyword
just before the method call causes execution of the calling method to effectively suspend until the called
method has completed and rethrows any exception thrown by the method.

When a `Task` or `Task<T>` is returned and is not awaited or redirected in some other way,
within the context of a synchronous method, a warning is reported.

This rule does *not* apply to calls made within async methods, since [CS4014][CS4014] already reports these.

## Examples of patterns that are flagged by this analyzer

```csharp
void Foo() {
    DoStuffAsync();
}

async Task DoStuffAsync() { /* ... */ }
```

## Solution

Convert the method to be async and await the expression:

```csharp
async Task FooAsync() {
    await DoStuffAsync();
}

async Task DoStuffAsync() { /* ... */ }
```

When the calling method's signature cannot be changed, wrap the method body in a `JoinableTaskFactory.Run` delegate instead:

```csharp
void Foo() {
    jtf.Run(async delegate {
        await DoStuffAsync();
    });
}

async Task DoStuffAsync() { /* ... */ }
```

One other option is to assign the result of the method call to a field or local variable, presumably to track it later:

```csharp
void Foo() {
    Task watchThis = DoStuffAsync();
}

async Task DoStuffAsync() { /* ... */ }
```

When tracking the `Task` with a field, remember that to await it later without risk of deadlocking,
wrap it in a `JoinableTask` using `JoinableTaskFactory.RunAsync`, per [the 3rd rule](../threading_rules.md#Rule3).

```csharp
JoinableTask watchThis;

void Foo() {
    this.watchThis = jtf.RunAsync(() => DoStuffAsync());
}

async Task WaitForFooToFinishAsync() {
    await this.watchThis;
}

async Task DoStuffAsync() { /* ... */ }
```

[CS4014]: https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs4014
