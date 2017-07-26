3 Threading Rules
=================

## Background

In Visual Studio 2013, we consolidated all our lessons learned from writing a complex,
multi-threaded component of Visual Studio into a small and simple set of
rules to avoid deadlocks, unwanted reentrancy, and keep an easier to maintain
codebase.  We do this by comprehensively applying just three rules, as outlined
below. In each case, the instance of `JoinableTaskFactory` used in the samples
comes from `ThreadHelper.JoinableTaskFactory` ([except for code in CPS and its
extensions](cookbook_vs.md)).

## The Rules

The rules are listed below with minimal examples. For a more thorough explanation with more examples, check out [this slideshow](https://www.slideshare.net/aarnott/the-3-vs-threading-rules).

### Rule #1. If a method has certain thread apartment requirements (STA or MTA) it must either:
   1. Have an asynchronous signature, and asynchronously marshal to the appropriate
   thread if it isn't originally invoked on a compatible thread. The recommended 
   means of switching to the main thread is:

      ```csharp
      await joinableTaskFactoryInstance.SwitchToMainThreadAsync();
      ```

      OR
    
   2. Have a synchronous signature, and throw an exception when called on the wrong thread.
     This can be done in Visual Studio with `ThreadHelper.ThrowIfNotOnUIThread()` or
     `ThreadHelper.ThrowIfOnUIThread()`.

        In particular, no method is allowed to synchronously marshal work to
        another thread (blocking while that work is done) except by using the second rule (below).
        Synchronous blocks in general are to be avoided whenever possible.
    
### Rule #2. When an implementation of an already-shipped public API must call asynchronous code and block for its completion, it must do so by following this simple pattern:

```csharp
joinableTaskFactoryInstance.Run(async delegate
{
    await SomeOperationAsync(...);
});
```
        
### Rule #3. If ever awaiting work that was started earlier, that work must be *joined*.

For example, one service kicks off some asynchronous work that may later become synchronously blocking:

```csharp
JoinableTask longRunningAsyncWork = joinableTaskFactoryInstance.RunAsync(
    async delegate
    {
        await SomeOperationAsync(...);
    });
```

then later that async work becomes blocking:

```csharp    
longRunningAsyncWork.Join();
```

or perhaps 

```csharp    
await longRunningAsyncWork;
```

Note however that this extra step is not necessary when awaiting is
done immediately after kicking off an asynchronous operation.
    
In particular, no method should call `Task.Wait()` or `Task.Result` on 
an incomplete `Task`.
    
### Additional "honorable mention" rules: (Not JTF related)

### Rule #4. Never define `async void` methods. Make the methods return `Task` instead.
   - Exceptions thrown from `async void` methods always crash the process.
   - Callers don't even have the option to `await` the result.
   - Exceptions can't be reported to telemetry by the caller.
   - It's impossible for your VS package to responsibly block in `Package.Close`
     till your `async` work is done when it was kicked off this way.
   - Be cautious: `async delegate` or `async () =>` become `async void` 
     methods when passed to a method that accepts `Action` delegates. Only 
     pass `async` delegates to methods that accept `Func<Task>` or 
     `Func<Task<T>>` parameters.
    
Frequently Asked Questions
---------------

##### Do I need to follow these rules?

All code that runs in Visual Studio itself should follow these rules.
These rules have been reviewed by several senior and principal developers
and VS architects, who have reviewed these rules and feel that VS
would do well to follow them in managed code where possible.

Any other GUI app that invokes asynchronous code that it must occasionally
block the UI thread on is also recommended to follow these rules.

##### Why should a method that has a dependency on a specific (kind of) thread be async?

Efficiency and responsiveness: Switching threads means that the original
thread either can do something else productive (e.g., execute more work from
the threadpool queue, or respond to more messages on the main thread) or
that it uselessly blocks doing nothing. Threads are not free. Threadpool
threads allocate 1MB of stack space and are limited in quantity. The main
thread is even more precious since it's tied directly to responsiveness
and lack thereof. So any opportunity you have to allow your calling thread
to return to a productive state is worth considering.

*Information hiding*: the caller no longer has to know what thread the method
requires, whether it's thread-safe, etc. The implementation can change
over time to add or remove thread affinity, or to switch from locking to
scheduling for thread safety, etc.

##### Why do I need to use `JoinableTaskFactory.Run` to synchronously block on asynchronous work rather than just calling `Task.Wait()` or `Task.Result`?

If you're on the main thread, because `Task.Wait` or `Task.Result` will often
deadlock because you're now synchronously blocking the main thread for
the completion of a task that may need the main thread to complete.

If you're on a threadpool thread, it means that you're occupying one
threadpool thread to do nothing but block, while other threadpool threads
get enlisted to execute continuations of this work that your own blocking
thread is perfectly capable of executing. When you see `.Wait()` and
asynchronous code mixed together, you tend to see call stacks with mixed
async and sync methods in it, which means that it may not be just one
thread that is blocked waiting. In fact calling `.Wait()` could mean your
code is blocking several threadpool threads at once, all to get just one
sequence of code execution to complete.

In contrast, when you use `JoinableTaskFactory.Run`, main thread deadlocks
and threadpool exhaustion are automatically mitigated by reusing the
blocking thread to execute the continuations.

##### Why not rely on COM marshaling to switch to the main thread when necessary?

There are several reasons for this:

1. The COM transition synchronously blocks the calling thread. If the 
   main thread isn't immediately pumping messages, the MTA thread will 
   block until it handles the message. If you're on a threadpool thread, 
   this ties up a precious resource and if your code may execute on 
   multiple threadpool threads at once, there is a very real possibility 
   of [threadpool starvation](threadpool_starvation.md).
2. Deadlock: if the main thread is blocked waiting for the background 
   thread, and the main thread happens to be on top of some call stack 
   (like WPF measure-layout) that suppresses the message pump, the code 
   that normally works will randomly deadlock.
3. When the main thread is pumping messages, it will execute your code, 
   regardless as to whether it is relevant to what the main thread may 
   already be doing. If the main thread is in the main message pump, 
   that's fine. But if the main thread is in a pumping wait (in 
   managed code this could be almost anywhere as this includes locks, 
   I/O, sync blocks, etc.) it could be a very bad time. We call these 
   bad times "reentrancy" and the problem comes when you have component 
   X running on the main thread in a pumping wait, the component Y 
   uses COM marshalling to re-enter the main thread, and then Y calls 
   (directly or indirectly) into component X. Component X is typically 
   written with the assumption that by being on the main thread, it's 
   isolated and single-threaded, and it usually isn't prepared to handle 
   reentrancy. As a result, data corruption and/or deadlocks can result. 
   Such has been the source of many deadlocks and crashes in VS for the 
   last few releases.
4. Any method from a VS service that returns a pointer is probably 
   inherently broken when called from a background thread. For example, 
   `ItemID`s returned from `IVsHierarchy` are very often raw pointers cast 
   to integers. These pointers are guaranteed to be valid for as long 
   as you're on the main thread (and no event was raised to invalidate 
   it). But when you call a `IVsHierarchy` method to get an `ItemID` back 
   from a background thread, you leave the STA thread immediately as 
   the call returns, meaning the pointer is unsafe to use. If you then 
   go and pass that pointer back into the project system, the pointer 
   could have been invalidated in the interim, and you'll end up causing 
   an access violation crash in VS. The only safe way to deal with 
   `ItemID`s (or any other pointer type) is while manually marshaled to 
   the UI thread so that you know they are still valid for as long as 
   you hold and use them.
5. If your method runs on a background thread and has a loop that 
   accesses a VS service, that can incur a lot of thread transitions 
   which can hurt performance. If you were explicit in your code about 
   the transition, you'd very likely move it to just before you enter 
   the loop, which would make your code more efficient from the start.
6. Some VS services don't have proxy stubs registered and thus will fail 
   to the type cast or on method invocation when your code executes on 
   a background thread.
7. Some VS services get rewritten from native to managed code, which 
   subtly changes them from single-threaded to free-threaded services. 
   Unless the managed code is written to be thread-safe (most is not) 
   this means that your managed code calling into a managed code VS 
   service on a background thread will not transition to the UI thread 
   first, and you are cruising for thread-safety bugs (data corruption, 
   crashes, hangs, etc). By switching to the main thread yourself first, 
   you won't be the poor soul who has crashes in their feature and has 
   to debug it for days until you finally figure out that you were causing 
   data corruption and a crash later on. Yes, you can blame the free 
   threaded managed code that should have protected itself, but that's 
   not very satisfying after days of investigation. And the owner of 
   that code may refuse to fix their code and you'll have to fix yours anyway.

##### How do these rules protect me from re-entering random code on the main thread?

By always using asynchronous mechanisms to marshal to the UI thread, you're
effectively send a `PostMessage` to the UI thread, which will not re-enter
the main thread when it is in a filtered message pump (i.e. a synchronously
blocking `Wait`). When you use this line in particular:

```csharp
await joinableTaskFactoryInstance.SwitchToMainThreadAsync();
```

It not only posts a message to the UI thread but also communicates with
the rest of the threading framework to avoid deadlocks in the event that
the main thread is blocked waiting for you, and you're waiting for the
main thread. That is, using this method to get to the UI thread just works:
it avoids both deadlocks and undesirable reentrancy. The only time it
deadlocks is when the threading rules listed above are not being followed.

##### Am I protected from other code re-entering my own code while it executes on the main thread?

Yes, somewhat. When you call `JoinableTaskFactory.Run` with an async delegate,
when your delegate yields (using await) the message pump is temporarily
stopped until relevant work needs to come back to the UI thread to unblock
you so your code can complete its execution. This is a significant amount
of protection since 3rd party code has the greatest opportunity to re-enter
the main thread while the main thread is waiting for background work to
complete, and the `JoinableTaskFactory` prevents this from happening.

However, when your code is actively running on the main thread reentrancy
can occur when you call synchronously blocking code (contested locks,
I/O, etc.) simply by virtue of the `DispatcherSynchronizationContext` that
is responsible for the synchronous block. While you can mitigate this by
disabling the message pump yourself, it's usually not a good idea because
3rd party code you may be calling could be relying on a functioning message
pump.

##### I'm trying to analyze a hang around code that uses `JoinableTaskFactory`, but since transitions are asynchronous the active threads' call stacks don't tell the whole story. How can I find the cause and fix the hang?

Debugging async hangs in general is lacking debugger tooling support at
the moment. The debugger and Windows teams are working to improve that
situation. In the meantime, we have learned several techniques to figure
out what is causing the hang, and we're working to enhance the framework
to automatically detect, self-analyze and report hangs to you so you have
almost nothing to do but fix the code bug. 

In the meantime, the most useful technique for analyzing async hangs is to
attach WinDBG to the process and dump out incomplete async methods' states.
This can be tedious, but we have a script in this file that you can use
to make it much easier: [Async hang debugging][AsyncHangDebugging]

##### What is threadpool exhaustion, and why is it bad?

See our [threadpool starvation](threadpool_starvation.md) doc.

##### I'm writing an async method that isn't in a `JoinableTask`. Should I use `JTF.SwitchToMainThreadAsync()` to get to the UI thread?

Yes. `JoinableTaskFactory.SwitchToMainThreadAsync()` works great outside
a `JoinableTask`. It simply posts the continuation to the main thread for
execution, which is the generally accepted safe mechanism for asynchronously
marshaling to the main thread. And if the caller is already on the main
thread, then using this technique is extremely lightweight as you avoid
allocating any closures and delegates.

Keep in mind that although you're not calling this async method within
a `JoinableTask`, a caller even lower in the call stack may actually have
created one before it called your code. This makes it an especially good
idea to use `JTF.SwitchToMainThreadAsync()`, as it means if your caller ever
synchronously blocks on the completion of your code you won't deadlock.

One more reason: if you don't use this method, you'll probably be hard-coding
a priority of how to get to the main thread (background, normal, high).
But often the code that needs the main thread isn't the outer scenario,
and therefore shouldn't really be making the decision about the priority to
the UI thread. For example, your same code may be called from a background
operation and another time as a foreground operation, without your code able
to discern between the two and make the appropriate choice for priority,
and doing so would add unnecessary complexity to your code. Instead, just
focus on the fact that at this point, your code needs the main thread and
call `JTF.SwitchToMainThreadAsync()`. This allows your caller to set the
priority via the `JoinableTask` it may call your code within.

##### What message priority is used to switch to (or resume on) the main thread, and can this be changed?

`JoinableTaskFactory`’s default behavior is to switch to the main thread using
`SynchronizationContext.Post`, which typically posts a message to the main thread,
which puts it below RPC and above user input in priority.

[How to use a different priority for switching to the main thread in VS](cookbook_vs.md#how-to-switch-to-or-use-the-ui-thread-with-background-priority)

The following describes how to replace the mechanism for getting to the
UI thread in a host-independent way:

You can set your own priority by creating your own derived type of
`JoinableTaskFactory` and overriding the `PostToUnderlyingSynchronizationContext`
method. This method is responsible both for initial switches to the UI
thread as well as resuming on the UI thread after a yielding await.

Note that the `JoinableTaskFactory` class has no default constructor, so when
implementing your own `JoinableTaskFactory`-derived type you will need to add
your own constructor that chains in the base constructor, passing in the
required parameters. You are then free to directly instantiate your derived
type by passing in either a `JoinableTaskContext` or a `JoinableTaskCollection`.

For more information on this topic, see Andrew Arnott's blog post 
[Asynchronous and multithreaded programming within VS using the 
`JoinableTaskFactory`][JTFBlog].

[AsyncHangDebugging]: https://github.com/Microsoft/VSProjectSystem/blob/master/doc/scenario/analyze_hangs.md
[JTFBlog]: https://blogs.msdn.com/b/andrewarnottms/archive/2014/05/07/asynchronous-and-multithreaded-programming-within-vs-using-the-joinabletaskfactory.aspx
