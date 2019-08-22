# Async hang investigations

An "asynchronous hang" is one in which the UI thread is synchronously blocked waiting for an asynchronous operation to complete. Because the operation is an async one, the callstack for operation that must complete to resolve the hang may not be evident on the UI thread or any other.

Finding the cause for an asynchronous hang can be challenging, but it can be done with a live, hung process or with a DMP file that contains the application heap, using WinDbg. The following are the recommended steps for diagnosing the problem:

1. Look at the application's UI thread to understand what scenario is blocking.
   1. Note whether the callstack includes a `JoinableTaskFactory.WaitSynchronously` frame. If it does not, but some other synchronously blocking frame does appear, this constitutes a violation of [rule #2][ThreadingRules].

      **Resolution:** Replace uses of `Task.Wait()`, `Task.Result`, `WaitHandle.WaitOne()` or other sync blocking primitives with `JoinableTask.Join()` or `JoinableTaskFactory.Run(Func<Task>)`.

1. Check background threads for any that are blocked on an STA COM call that's waiting to marshal to the UI thread. If you find any, and they are performing an operation that the UI thread is blocked waiting to complete, the STA switch is probably failing. You can search for such pending RPC calls using the `!ftw -rpc` command in WinDBG.

   **Resolution:** Modify the code running on the background thread to switch to the UI thread before making the call to the STA COM object, by using `await JoinableTaskFactory.SwitchToMainThreadAsync();` per [rule #1][ThreadingRules].

1. Use [WinDbg](https://docs.microsoft.com/en-us/windows-hardware/drivers/debugger/debugger-download-tools) with the [`!dumpasync` extension](dumpasync.md) to reveal the async methods that do not appear on thread callstacks to identify why an async method did not complete.

## Visual Studio specific considerations

If the hang shows a JoinableTaskFactory.WaitSynchronously frame near the top of the callstack on the UI thread, and if the hung application is Visual Studio itself, and if CPS was loaded before the hang, there is probably a DGML file on the disk of the repro machine that contains a hang report that shows you what went wrong. That way you may be able to avoid any WinDBG heap scouring manual investigation. Look for directories with this pattern: `%temp%\CPS.*` where `*` is a random GUID.
   If you don't have access to the TEMP directory, you can also get the hang report from the dump file itself:

        ```
        !dumpheap -stat -type CpsJoinableTaskContext
        !dumpheap -mt <MTAddress from previous command>
        !do <object address from previous command>
        !do <address from mostRecentHangReport field>
        ```

[WinDbg]: https://aka.ms/windbg-direct-download
[ThreadingRules]: threading_rules.md
