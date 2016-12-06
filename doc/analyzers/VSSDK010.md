# VSSDK010 Use `Async` suffix for async methods

The .NET Guidelines for async methods includes that such methods
should have names that include an "Async" suffix.

## Examples of patterns that are flagged by this analyzer

```csharp
async Task DoSomething() // analyzer flags this line
{
    await Task.Yield();
}
```

## Solution

Simply rename the method to end in "Async":

```csharp
async Task DoSomethingAsync()
{
    await Task.Yield();
}
```

A code fix exists to automatically rename such methods.
