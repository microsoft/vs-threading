namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;

    internal static class CommonInterest
    {
        internal static readonly IReadOnlyList<SyncBlockingMethod> JTFSyncBlockers = new[]
        {
            new SyncBlockingMethod(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTaskFactory.TypeName, Types.JoinableTaskFactory.Run, Types.JoinableTaskFactory.RunAsync),
            new SyncBlockingMethod(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTask.TypeName, Types.JoinableTask.Join, Types.JoinableTask.JoinAsync),
        };

        internal static readonly IReadOnlyList<SyncBlockingMethod> SyncBlockingMethods = new[]
        {
            new SyncBlockingMethod(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTaskFactory.TypeName, Types.JoinableTaskFactory.Run, Types.JoinableTaskFactory.RunAsync),
            new SyncBlockingMethod(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTask.TypeName, Types.JoinableTask.Join, Types.JoinableTask.JoinAsync),
            new SyncBlockingMethod(Namespaces.SystemThreadingTasks, nameof(Task), nameof(Task.Wait), null),
            new SyncBlockingMethod(Namespaces.SystemRuntimeCompilerServices, nameof(TaskAwaiter), nameof(TaskAwaiter.GetResult), null),
        };

        internal static readonly IReadOnlyList<LegacyThreadSwitchingMethod> LegacyThreadSwitchingMethods = new[]
        {
            new LegacyThreadSwitchingMethod(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName, Types.ThreadHelper.Invoke),
            new LegacyThreadSwitchingMethod(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName, Types.ThreadHelper.InvokeAsync),
            new LegacyThreadSwitchingMethod(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName, Types.ThreadHelper.BeginInvoke),
            new LegacyThreadSwitchingMethod(Namespaces.SystemWindowsThreading, Types.Dispatcher.TypeName, Types.Dispatcher.Invoke),
            new LegacyThreadSwitchingMethod(Namespaces.SystemWindowsThreading, Types.Dispatcher.TypeName, Types.Dispatcher.BeginInvoke),
            new LegacyThreadSwitchingMethod(Namespaces.SystemWindowsThreading, Types.Dispatcher.TypeName, Types.Dispatcher.InvokeAsync),
            new LegacyThreadSwitchingMethod(Namespaces.SystemThreading, Types.SynchronizationContext.TypeName, Types.SynchronizationContext.Send),
            new LegacyThreadSwitchingMethod(Namespaces.SystemThreading, Types.SynchronizationContext.TypeName, Types.SynchronizationContext.Post),
        };

        internal static readonly IReadOnlyList<SyncBlockingMethod> SyncBlockingProperties = new[]
        {
            new SyncBlockingMethod(Namespaces.SystemThreadingTasks, nameof(Task), nameof(Task<int>.Result), null),
        };

        internal struct SyncBlockingMethod
        {
            public SyncBlockingMethod(IReadOnlyList<string> containingTypeNamespace, string containingTypeName, string methodName, string asyncAlternativeMethodName)
            {
                this.ContainingTypeNamespace = containingTypeNamespace;
                this.ContainingTypeName = containingTypeName;
                this.MethodName = methodName;
                this.AsyncAlternativeMethodName = asyncAlternativeMethodName;
            }

            public IReadOnlyList<string> ContainingTypeNamespace { get; private set; }

            public string ContainingTypeName { get; private set; }

            public string MethodName { get; private set; }

            public string AsyncAlternativeMethodName { get; private set; }
        }

        internal struct LegacyThreadSwitchingMethod
        {
            public LegacyThreadSwitchingMethod(IReadOnlyList<string> containingTypeNamespace, string containingTypeName, string methodName)
            {
                this.ContainingTypeNamespace = containingTypeNamespace;
                this.ContainingTypeName = containingTypeName;
                this.MethodName = methodName;
            }

            public IReadOnlyList<string> ContainingTypeNamespace { get; private set; }

            public string ContainingTypeName { get; private set; }

            public string MethodName { get; private set; }
        }
    }
}
