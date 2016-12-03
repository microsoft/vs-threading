# VSSDK005 Use `InvokeAsync` to raise async events

Asynchronous events (those typed as `AsyncEventHandler`) must be raised carefully to ensure
all event handlers are invoked and awaited on.

Although C# lets you invoke event handlers naturally, it has no awareness of async event handlers
and thus will not let you correctly await on their invocation nor invoke them sequentially.

## Examples of patterns that are flagged by this analyzer

```csharp
public AsyncEventHandler Clicked;

async Task OnClicked() {
    await Clicked(this, EventArgs.Empty); // only awaits the first event handler.
}
```

## Solution

Use the `InvokeAsync` extension method defined in the `TplExtensions` class and await its result.
This will ensure each event handler completes before invoking the next event handler in the list,
similar to the default behavior for raising synchronous events. 

```csharp
public AsyncEventHandler Clicked;

async Task OnClicked() {
    await Clicked.InvokeAsync(this, EventArgs.Empty); // await for the completion of all handlers.
}
```
