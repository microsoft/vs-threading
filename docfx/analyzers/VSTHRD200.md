# VSTHRD200 Use `Async` suffix for async methods

The .NET Guidelines for async methods includes that such methods
should have names that include an "Async" suffix.

Methods that return awaitable types such as `Task` or `ValueTask`
should have an Async suffix.
Methods that do not return awaitable types should not use the Async suffix.

## Examples of patterns that are flagged by this analyzer

This `Task`-returning method should have a name that ends with Async:

```csharp
async Task DoSomething() // analyzer flags this line
{
    await Task.Yield();
}
```

This method should not have a name that ends with Async, since it does not return an awaitable type:

```csharp
bool DoSomethingElseAsync() // analyzer flags this line
{
    return false;
}
```

## Solution

Simply rename the method to end in "Async" (or remove the suffix, as appropriate):

```csharp
async Task DoSomethingAsync()
{
    await Task.Yield();
}

bool DoSomethingElse()
{
    return false;
}
```


A code fix exists to automatically rename such methods.
