# VSTHRD100 Avoid `async void` methods

Methods with `async void` signatures make it impossible for their caller to track
the entire asynchronous operation and handle exceptions that may be thrown by that method.
If the method throws an exception, it crashes the process.

## Examples of patterns that are flagged by this analyzer

```csharp
async void DoSomethingAsync()
{
    await SomethingElseAsync();
}
```

## Solution

Change the method to return `Task` instead of `void`.

```csharp
async Task DoSomethingAsync()
{
    await SomethingElseAsync();
}
```

A code fix is offered that automatically changes the return type of the method. 

Refer to [Async/Await - Best Practices in Asynchronous Programming](https://msdn.microsoft.com/en-us/magazine/jj991977.aspx) for more info.
