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

### Event handlers

For event handlers, avoid `async void` by using `RunAsync`:
```csharp
obj.Event += (s, e) => joinableTaskFactory.RunAsync(() => OnEventAsync(s, e));
}

private async Task OnEventAsync(object sender, EventArgs e)
{
   // async code here.
}
```

When using method group syntax as an argument, you can define the method with the required signature, without the `async` modifier, and define an anonymous delegate or lambda within the method, like this:

```cs
var menuItem = new MenuCommand(HandleEvent, commandId);

private void HandleEvent(object sender, EventArgs e)
{
   _ = joinableTaskFactory.RunAsync(async () =>
   {
      // async code
   });
}
```

Refer to [Async/Await - Best Practices in Asynchronous Programming](https://msdn.microsoft.com/en-us/magazine/jj991977.aspx) for more info.
