# The `!dumpasync` WinDbg extension

The `!dumpasync` command is a WinDbg extension that produces a list of async method callstacks
that may not appear on thread callstacks. This information is useful when diagnosing the cause
for an app hang that is waiting for async methods to complete.

## Tool acquisition

Using this tool requires that you download and install [WinDbg][WinDbg], which can attach to a running process or DMP file. The `!dumpasync` extension is only available for WinDbg.

The `!dumpasync` extension itself is exported from the `AsyncDebugTools.dll` library, which you can acquire from [our releases page](https://github.com/Microsoft/vs-threading/releases).

## Usage

Use WinDbg to open your DMP file or attach to your process, then execute the following commands.
Be sure to use either the x86 or x64 version of the `AsyncDebugTools.dll` library, consistent with the version of WinDbg you are running.

```windbg
.load path\to\AsyncDebugTools.dll
!dumpasync
```

The `!dumpasync` command will produce output such as:

```
07494c7c <0> Microsoft.Cascade.Rpc.RpcSession+<SendRequestAsync>d__49
.07491d10 <1> Microsoft.Cascade.Agent.WorkspaceService+<JoinRemoteWorkspaceAsync>d__28
..073c8be4 <5> Microsoft.Cascade.Agent.WorkspaceService+<JoinWorkspaceAsync>d__22
...073b7e94 <0> Microsoft.Cascade.Rpc.RpcDispatcher`1+<>c__DisplayClass23_2+<<BuildMethodMap>b__2>d[[Microsoft.Cascade.Contracts.IWorkspaceService, Microsoft.Cascade.Common]]
....073b60e0 <0> Microsoft.Cascade.Rpc.RpcServiceUtil+<RequestAsync>d__3
.....073b366c <0> Microsoft.Cascade.Rpc.RpcSession+<ReceiveRequestAsync>d__42
......073b815c <0> Microsoft.Cascade.Rpc.RpcSession+<>c__DisplayClass40_1+<<Receive>b__0>d

073cdb7c <0> StreamJsonRpc.ReadBufferingStream+<FillBufferAsync>d__27
.073cdc6c <0> StreamJsonRpc.HeaderDelimitedMessageHandler+<ReadCoreAsync>d__20
..073cdd84 <1> StreamJsonRpc.DelimitedMessageHandler+<ReadAsync>d__22
...073cd93c <0> Microsoft.Cascade.Rpc.Json.JsonRpcStream+<ReadAsync>d__11
....073cd908 <0> Microsoft.Cascade.Rpc.RpcSession+<ReceiveNextMessageAsync>d__39

0730c750 <0> Microsoft.Cascade.Rpc.NamedPipeServer+<RunAsync>d__23
.0730c17c <0> Microsoft.Cascade.Rpc.PipeRpcServer+<AcceptConnectionsAsync>d__2
..0730bff4 <0> Microsoft.Cascade.Agent.Server+<RunAsync>d__15
...072ef4a8 <1> Microsoft.Cascade.Agent.Program+<ExecuteAsync>d__24

07496420 <0> System.Threading.SemaphoreSlim+<WaitUntilCountOrTimeoutAsync>d__32
.07496208 <0> Microsoft.Cascade.Rpc.RpcSession+<SendNextMessageAsync>d__51
..0731c7b4 <0> Microsoft.Cascade.Rpc.RpcSession+<ProcessMessagesAsync>d__38
...0730b6b4 <1> Microsoft.Cascade.Rpc.RpcServer+<CreateSessionAsync>d__8

072fa138 <0> Microsoft.Cascade.Tracing.LogFileTraceListener+<WriteToLogAsync>d__16
0730a138 <0> Microsoft.Cascade.Rpc.NamedPipeServer+<RunServerAsync>d__29
```

The output above is a set of stacks – not exactly callstacks, but actually "continuation stacks".
A continuation stack is synthesized based on what code has 'awaited' the call to an async method. It's possible that the `Task` returned by an async method was awaited from multiple places (e.g. the `Task` was stored in a field, then awaited by multiple interested parties). When there are multiple awaiters, the stack can branch
and show multiple descendents of a given frame. The stacks above are therefore actually "trees", and the leading
dots at each frame helps recognize when trees have multiple branches.

If an async method is invoked but not awaited on, the caller won't appear in the continuation stack.

In the example output above, there are several stacks shown. The two frames at the bottom are two lone async method frames that have no continuations.
In the first stack above, `SendRequestAsync` method is on top, so it is the method that contains the last `await` that didn't resume. The top frame is usually where you want to investigate the cause of an async hang. The rest of the frames in the stack give you a clue as to why this top frame matters to the rest of your application.
​The `<0>` you see on the top frame is the state of the async state machine that tracks the async method on that frame. This state field should be interpreted based on the following table:

| Value | Meaning |
| -- | -- |
| -2 | The async method has run to completion. This state machine is likely unrooted and subject to garbage collection. Observing this is rare because the `!dumpasync` extension filters these out as noise.
| -1 | The async method is currently executing (you should find it on a real thread's callstack somewhere).
| >= 0 | The 0-index into which "await" has most recently yielded. The list of awaits for the method are in strict syntax appearance order. That is, regardless of code execution, if branching, etc., it's the index into which await in code syntax order has yielded. For example, if you position the caret at the top of the method definition and search for "await ", and count how many times you hit a match, starting from 0, when you arrive at the number that you found in the state field, you've found the await that has most recently yielded. Note that when the code being debugged is compiled with certain Dev14 prerelease versions of the Roslyn compiler, this index is 1-based instead of 0-based.

[WinDbg]: https://aka.ms/windbg-direct-download
