// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Interop;

namespace CpsDbg
{
    internal class DumpAsyncCommand : ICommandHandler
    {
        public void Execute(DebuggerContext context, string args)
        {
            ClrHeap heap = context.Runtime.Heap;

            var allStateMachines = new List<AsyncStateMachine>();
            var knownStateMachines = new Dictionary<ulong, AsyncStateMachine>();

            GetAllStateMachines(context, heap, allStateMachines, knownStateMachines);

            ChainStateMachinesBasedOnTaskContinuations(context, knownStateMachines);
            ChainStateMachinesBasedOnJointableTasks(context, allStateMachines);
            MarkThreadingBlockTasks(context, allStateMachines);
            MarkUIThreadDependingTasks(allStateMachines);
            FixBrokenDependencies(allStateMachines);

            PrintOutStateMachines(allStateMachines, context.Output);
            LoadCodePages(context, allStateMachines);
        }

        private static void GetAllStateMachines(DebuggerContext context, ClrHeap heap, List<AsyncStateMachine> allStateMachines, Dictionary<ulong, AsyncStateMachine> knownStateMachines)
        {
            foreach (ClrObject obj in heap.GetObjectsOfType("System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner"))
            {
                try
                {
                    ClrObject stateMachine = obj.ReadObjectField("m_stateMachine");
                    if (!knownStateMachines.ContainsKey(stateMachine.Address))
                    {
                        try
                        {
                            var state = stateMachine.ReadField<int>("<>1__state");
                            if (state >= -1)
                            {
                                ClrObject taskField = default(ClrObject);
                                ClrValueType? asyncBuilder = stateMachine.TryGetValueClassField("<>t__builder");
                                if (asyncBuilder.HasValue)
                                {
                                    while (asyncBuilder.HasValue)
                                    {
                                        taskField = asyncBuilder.TryGetObjectField("m_task");
                                        if (!taskField.IsNull)
                                        {
                                            break;
                                        }

                                        ClrValueType? nextAsyncBuilder = asyncBuilder.TryGetValueClassField("m_builder");
                                        if (nextAsyncBuilder is null)
                                        {
                                            asyncBuilder = asyncBuilder.TryGetValueClassField("_methodBuilder");
                                        }
                                        else
                                        {
                                            asyncBuilder = nextAsyncBuilder;
                                        }
                                    }
                                }
                                else
                                {
                                    // CLR debugger may not be able to access t__builder, when NGEN assemblies are being used, and the type of the field could be lost.
                                    // Our workaround is to pick up the first Task object referenced by the state machine, which seems to be correct.
                                    // That function works with the raw data structure (like how GC scans the object, so it doesn't depend on symbols.
                                    //
                                    // However, one problem of that is we can pick up tasks from other reference fields of the same structure. So, we go through fields which we have symbols
                                    // and remember references encounted, and we skip them when we go through GC references.
                                    // Note: we can do better by going through other value structures, and extract references from them here, which we can consider when we have a real scenario.
                                    var previousReferences = new Dictionary<ulong, int>();
                                    if (stateMachine.Type?.GetFieldByName("<>t__builder") is not null)
                                    {
                                        foreach (ClrInstanceField field in stateMachine.Type.Fields)
                                        {
                                            if (string.Equals(field.Name, "<>t__builder", StringComparison.Ordinal))
                                            {
                                                break;
                                            }

                                            if (field.IsObjectReference)
                                            {
                                                ClrObject referencedValue = field.ReadObject(stateMachine.Address, interior: false);
                                                if (!referencedValue.IsNull)
                                                {
                                                    if (previousReferences.TryGetValue(referencedValue.Address, out int refCount))
                                                    {
                                                        previousReferences[referencedValue.Address] = refCount + 1;
                                                    }
                                                    else
                                                    {
                                                        previousReferences[referencedValue.Address] = 1;
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    foreach (ClrObject referencedObject in stateMachine.EnumerateReferences(true))
                                    {
                                        if (!referencedObject.IsNull)
                                        {
                                            if (previousReferences.TryGetValue(referencedObject.Address, out int refCount) && refCount > 0)
                                            {
                                                if (refCount == 1)
                                                {
                                                    previousReferences.Remove(referencedObject.Address);
                                                }
                                                else
                                                {
                                                    previousReferences[referencedObject.Address] = refCount - 1;
                                                }

                                                continue;
                                            }
                                            else if (previousReferences.Count > 0)
                                            {
                                                continue;
                                            }

                                            if (referencedObject.Type is object &&
                                                (string.Equals(referencedObject.Type.Name, "System.Threading.Tasks.Task", StringComparison.Ordinal) || string.Equals(referencedObject.Type.BaseType?.Name, "System.Threading.Tasks.Task", StringComparison.Ordinal)))
                                            {
                                                taskField = referencedObject;
                                                break;
                                            }
                                        }
                                    }
                                }

                                var asyncState = new AsyncStateMachine(state, stateMachine, taskField);
                                allStateMachines.Add(asyncState);
                                knownStateMachines.Add(stateMachine.Address, asyncState);

                                if (stateMachine.Type is object)
                                {
                                    foreach (ClrMethod? method in stateMachine.Type.Methods)
                                    {
                                        if (method.Name == "MoveNext" && method.NativeCode != ulong.MaxValue)
                                        {
                                            asyncState.CodeAddress = method.NativeCode;
                                        }
                                    }
                                }
                            }
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                        {
                            context.Output.WriteLine($"Fail to process state machine {stateMachine.Address:x} Type:'{stateMachine.Type?.Name}' Module:'{stateMachine.Type?.Module?.Name}' Error: {ex.Message}");
                        }
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    context.Output.WriteLine($"Fail to process AsyncStateMachine Runner {obj.Address:x} Error: {ex.Message}");
                }
            }
        }

        private static void ChainStateMachinesBasedOnTaskContinuations(DebuggerContext context, Dictionary<ulong, AsyncStateMachine> knownStateMachines)
        {
            foreach (AsyncStateMachine? stateMachine in knownStateMachines.Values)
            {
                ClrObject taskObject = stateMachine.Task;
                try
                {
                    while (!taskObject.IsNull)
                    {
                        // 3 cases in order to get the _target:
                        // 1. m_continuationObject.m_action._target
                        // 2. m_continuationObject._target
                        // 3. m_continuationObject.m_task.m_stateObject._target
                        ClrObject continuationObject = taskObject.TryGetObjectField("m_continuationObject");
                        if (continuationObject.IsNull)
                        {
                            break;
                        }

                        ChainStateMachineBasedOnTaskContinuations(knownStateMachines, stateMachine, continuationObject);

                        taskObject = continuationObject;
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    context.Output.WriteLine($"Fail to fix continuation of state {stateMachine.StateMachine.Address:x} Error: {ex.Message}");
                }
            }
        }

        private static void ChainStateMachineBasedOnTaskContinuations(Dictionary<ulong, AsyncStateMachine> knownStateMachines, AsyncStateMachine stateMachine, ClrObject continuationObject)
        {
            ClrObject continuationAction = continuationObject.TryGetObjectField("m_action");

            // case 1
            ClrObject continuationTarget = continuationAction.TryGetObjectField("_target");
            if (continuationTarget.IsNull)
            {
                // case 2
                continuationTarget = continuationObject.TryGetObjectField("_target");
                if (continuationTarget.IsNull)
                {
                    // case 3
                    continuationTarget = continuationObject.TryGetObjectField("m_task").TryGetObjectField("m_stateObject").TryGetObjectField("_target");
                }
            }

            while (!continuationTarget.IsNull)
            {
                // now get the continuation from the target
                ClrObject continuationTargetStateMachine = continuationTarget.TryGetObjectField("m_stateMachine");
                if (!continuationTargetStateMachine.IsNull)
                {
                    AsyncStateMachine targetAsyncState;
                    if (knownStateMachines.TryGetValue(continuationTargetStateMachine.Address, out targetAsyncState) && targetAsyncState != stateMachine)
                    {
                        stateMachine.Next = targetAsyncState;
                        stateMachine.DependentCount++;
                        targetAsyncState.Previous = stateMachine;
                    }

                    break;
                }
                else
                {
                    ClrObject nextContinuation = continuationTarget.TryGetObjectField("m_continuation");
                    continuationTarget = nextContinuation.TryGetObjectField("_target");
                }
            }

            ClrObject items = continuationObject.TryGetObjectField("_items");
            if (!items.IsNull && items.IsArray && items.ContainsPointers)
            {
                foreach (ClrObject promise in items.EnumerateReferences(true))
                {
                    if (!promise.IsNull)
                    {
                        ClrObject innerContinuationObject = promise.TryGetObjectField("m_continuationObject");
                        if (!innerContinuationObject.IsNull)
                        {
                            ChainStateMachineBasedOnTaskContinuations(knownStateMachines, stateMachine, innerContinuationObject);
                        }
                        else
                        {
                            ChainStateMachineBasedOnTaskContinuations(knownStateMachines, stateMachine, promise);
                        }
                    }
                }
            }
        }

        private static void ChainStateMachinesBasedOnJointableTasks(DebuggerContext context, List<AsyncStateMachine> allStateMachines)
        {
            foreach (AsyncStateMachine? stateMachine in allStateMachines)
            {
                if (stateMachine.Previous is null)
                {
                    try
                    {
                        ClrObject joinableTask = stateMachine.StateMachine.TryGetObjectField("<>4__this");
                        ClrObject wrappedTask = joinableTask.TryGetObjectField("wrappedTask");
                        if (!wrappedTask.IsNull)
                        {
                            AsyncStateMachine? previousStateMachine = allStateMachines
                                .FirstOrDefault(s => s.Task.Address == wrappedTask.Address);
                            if (previousStateMachine is object && stateMachine != previousStateMachine)
                            {
                                stateMachine.Previous = previousStateMachine;
                                previousStateMachine.Next = stateMachine;
                                previousStateMachine.DependentCount++;
                            }
                        }
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        context.Output.WriteLine($"Fail to fix continuation of state {stateMachine.StateMachine.Address:x} Error: {ex.Message}");
                    }
                }
            }
        }

        private static void MarkThreadingBlockTasks(DebuggerContext context, List<AsyncStateMachine> allStateMachines)
        {
            foreach (ClrThread? thread in context.Runtime.Threads)
            {
                ClrStackFrame? stackFrame = thread.EnumerateStackTrace().Take(50).FirstOrDefault(
                    f => f.Method is { } method
                        && string.Equals(f.Method.Name, "CompleteOnCurrentThread", StringComparison.Ordinal)
                        && string.Equals(f.Method.Type?.Name, "Microsoft.VisualStudio.Threading.JoinableTask", StringComparison.Ordinal));

                if (stackFrame is object)
                {
                    var visitedObjects = new HashSet<ulong>();
                    foreach (IClrStackRoot stackRoot in thread.EnumerateStackRoots())
                    {
                        ClrObject stackObject = stackRoot.Object;
                        if (string.Equals(stackObject.Type?.Name, "Microsoft.VisualStudio.Threading.JoinableTask", StringComparison.Ordinal) ||
                            string.Equals(stackObject.Type?.BaseType?.Name, "Microsoft.VisualStudio.Threading.JoinableTask", StringComparison.Ordinal))
                        {
                            if (visitedObjects.Add(stackObject.Address))
                            {
                                var joinableTaskObject = new ClrObject(stackObject.Address, stackObject.Type);
                                int state = joinableTaskObject.ReadField<int>("state");
                                if ((state & 0x10) == 0x10)
                                {
                                    // This flag indicates the JTF is blocking the thread
                                    ClrObject wrappedTask = joinableTaskObject.TryGetObjectField("wrappedTask");
                                    if (!wrappedTask.IsNull)
                                    {
                                        AsyncStateMachine? blockingStateMachine = allStateMachines
                                            .FirstOrDefault(s => s.Task.Address == wrappedTask.Address);
                                        if (blockingStateMachine is object)
                                        {
                                            blockingStateMachine.BlockedThread = thread.OSThreadId;
                                            blockingStateMachine.BlockedJoinableTask = joinableTaskObject;
                                        }
                                    }

                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void MarkUIThreadDependingTasks(List<AsyncStateMachine> allStateMachines)
        {
            foreach (AsyncStateMachine? stateMachine in allStateMachines)
            {
                if (stateMachine.Previous is null && stateMachine.State >= 0)
                {
                    try
                    {
                        ClrInstanceField? awaitField = stateMachine.StateMachine.Type?.GetFieldByName($"<>u__{stateMachine.State + 1}");
                        if (awaitField is object && awaitField.IsValueType && string.Equals(awaitField.Type?.Name, "Microsoft.VisualStudio.Threading.JoinableTaskFactory+MainThreadAwaiter", StringComparison.Ordinal))
                        {
                            ClrValueType? awaitObject = stateMachine.StateMachine.TryGetValueClassField($"<>u__{stateMachine.State + 1}");
                            if (awaitObject.HasValue)
                            {
                                stateMachine.SwitchToMainThreadTask = awaitObject.TryGetObjectField("job");
                            }
                        }
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                    }
                }
            }
        }

        private static void FixBrokenDependencies(List<AsyncStateMachine> allStateMachines)
        {
            foreach (AsyncStateMachine? stateMachine in allStateMachines)
            {
                if (stateMachine.Previous is object && stateMachine.Previous.Next != stateMachine)
                {
                    // If the previous task actually has two continuations, we end up in a one way dependencies chain, we need fix it in the future.
                    stateMachine.AlterPrevious = stateMachine.Previous;
                    stateMachine.Previous = null;
                }
            }
        }

        private static void PrintOutStateMachines(List<AsyncStateMachine> allStateMachines, DebuggerOutput output)
        {
            int loopMark = -1;
            foreach (AsyncStateMachine? stateMachine in allStateMachines)
            {
                int depth = 0;
                if (stateMachine.Previous is null)
                {
                    AsyncStateMachine? p = stateMachine;
                    while (p is object)
                    {
                        depth++;
                        if (p.Depth == loopMark)
                        {
                            break;
                        }

                        p.Depth = loopMark;
                        p = p.Next;
                    }
                }

                if (stateMachine.AlterPrevious is object)
                {
                    depth++;
                }

                stateMachine.Depth = depth;
                loopMark--;
            }

            var printedMachines = new HashSet<AsyncStateMachine>();

            foreach (AsyncStateMachine? node in allStateMachines
                .Where(m => m.Depth > 0)
                .OrderByDescending(m => m.Depth)
                .ThenByDescending(m => m.SwitchToMainThreadTask.Address))
            {
                bool multipleLineBlock = PrintAsyncStateMachineChain(output, node, printedMachines);

                if (multipleLineBlock)
                {
                    output.WriteLine(string.Empty);
                }
            }

            // Print nodes which we didn't print because of loops.
            if (allStateMachines.Count > printedMachines.Count)
            {
                output.WriteLine("States form dependencies loop -- could be an error caused by the analysis tool");
                foreach (AsyncStateMachine? node in allStateMachines)
                {
                    if (!printedMachines.Contains(node))
                    {
                        PrintAsyncStateMachineChain(output, node, printedMachines);
                        output.WriteLine(string.Empty);
                    }
                }
            }
        }

        private static bool PrintAsyncStateMachineChain(DebuggerOutput output, AsyncStateMachine node, HashSet<AsyncStateMachine> printedMachines)
        {
            int nLevel = 0;
            bool multipleLineBlock = false;

            var loopDetection = new HashSet<AsyncStateMachine>();
            for (AsyncStateMachine? p = node; p is object; p = p.Next)
            {
                printedMachines.Add(p);

                if (nLevel > 0)
                {
                    output.WriteString("..");
                    multipleLineBlock = true;
                }
                else if (p.AlterPrevious is object)
                {
                    output.WriteObjectAddress(p.AlterPrevious.StateMachine.Address);
                    output.WriteString($" <{p.AlterPrevious.State}> * {p.AlterPrevious.StateMachine.Type?.Name} @ ");
                    output.WriteMethodInfo($"{p.AlterPrevious.CodeAddress:x}", p.AlterPrevious.CodeAddress);
                    output.WriteLine(string.Empty);
                    output.WriteString("..");
                    multipleLineBlock = true;
                }
                else if (!p.SwitchToMainThreadTask.IsNull)
                {
                    output.WriteObjectAddress(p.SwitchToMainThreadTask.Address);
                    output.WriteLine(".SwitchToMainThreadAsync");
                    output.WriteString("..");
                    multipleLineBlock = true;
                }

                output.WriteObjectAddress(p.StateMachine.Address);
                string doubleDependentTaskMark = p.DependentCount > 1 ? " * " : " ";
                output.WriteString($" <{p.State}>{doubleDependentTaskMark}{p.StateMachine.Type?.Name} @ ");
                output.WriteMethodInfo($"{p.CodeAddress:x}", p.CodeAddress);
                output.WriteLine(string.Empty);

                if (!loopDetection.Add(p))
                {
                    output.WriteLine("!!Loop task dependencies");
                    break;
                }

                if (p.Next is null && p.BlockedThread.HasValue)
                {
                    output.WriteString("-- ");
                    output.WriteThreadLink(p.BlockedThread.Value);
                    output.WriteString(" - JoinableTask: ");
                    output.WriteObjectAddress(p.BlockedJoinableTask.Address);

                    int state = p.BlockedJoinableTask.ReadField<int>("state");
                    if ((state & 0x20) == 0x20)
                    {
                        output.WriteLine(" SynchronouslyBlockingMainThread");
                    }
                    else
                    {
                        output.WriteLine(string.Empty);
                    }

                    multipleLineBlock = true;
                }

                nLevel++;
            }

            return multipleLineBlock;
        }

        private static void LoadCodePages(DebuggerContext context, List<AsyncStateMachine> allStateMachines)
        {
            var loadedAddresses = new HashSet<ulong>();
            foreach (AsyncStateMachine? stateMachine in allStateMachines)
            {
                ulong codeAddress = stateMachine.CodeAddress;
                if (loadedAddresses.Add(codeAddress))
                {
                    context.DebugControl.Execute(DEBUG_OUTCTL.IGNORE, $"u {codeAddress} {codeAddress}", DEBUG_EXECUTE.NOT_LOGGED);
                }
            }
        }

        private class AsyncStateMachine
        {
            public AsyncStateMachine(int state, ClrObject stateMachine, ClrObject task)
            {
                this.State = state;
                this.StateMachine = stateMachine;
                this.Task = task;
            }

            public int State { get; } // -1 == currently running, 0 = still waiting on first await, 2= before the 3rd await

            public ClrObject StateMachine { get; }

            public ClrObject Task { get; }

            public AsyncStateMachine? Previous { get; set; }

            public AsyncStateMachine? Next { get; set; }

            public int DependentCount { get; set; }

            public int Depth { get; set; }

            public uint? BlockedThread { get; set; }

            public ClrObject BlockedJoinableTask { get; set; }

            public ClrObject SwitchToMainThreadTask { get; set; }

            public AsyncStateMachine? AlterPrevious { get; set; }

            public ulong CodeAddress { get; set; }

            public override string ToString()
            {
                return $"state = {this.State} Depth {this.Depth} StateMachine = {this.StateMachine} Task = {this.Task}";
            }
        }
    }
}
