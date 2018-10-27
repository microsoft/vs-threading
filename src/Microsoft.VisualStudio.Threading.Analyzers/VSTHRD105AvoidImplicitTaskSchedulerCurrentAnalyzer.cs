/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Report errors when <see cref="TaskFactory.StartNew(Action)"/> or <see cref="Task.ContinueWith(Action{Task, object}, object)"/>
    /// overloads are invoked that do not accept an explicit <see cref="TaskScheduler"/>.
    /// </summary>
    /// <remarks>
    /// Not specifying the <see cref="TaskScheduler"/> explicitly is problematic because <see cref="TaskScheduler.Current"/>
    /// will then be used. While this is normally <see cref="TaskScheduler.Default"/> (the thread pool), it may not always be.
    /// For example, when the calling code is itself running as a scheduled task on a different <see cref="TaskScheduler"/>,
    /// then that will be inherited, leading to the calling code to run in an unexpected context.
    /// Explicitly specifying <see cref="TaskScheduler.Default"/> will ensure that the behavior is always to run the <see cref="Task"/>
    /// on the thread pool. Of course any <see cref="TaskScheduler"/> is fine, so long as it is explicitly given (including
    /// <see cref="TaskScheduler.Current"/> itself).
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class VSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "VSTHRD105";

        internal static readonly DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            id: Id,
            title: Strings.VSTHRD105_Title,
            messageFormat: Strings.VSTHRD105_MessageFormat,
            helpLinkUri: Utils.GetHelpLink(Id),
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

            context.RegisterSyntaxNodeAction(Utils.DebuggableWrapper(this.AnalyzeInvocation), SyntaxKind.InvocationExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var node = (InvocationExpressionSyntax)context.Node;
            var invokeMethod = context.SemanticModel.GetSymbolInfo(context.Node).Symbol as IMethodSymbol;
            if (invokeMethod?.ContainingType.BelongsToNamespace(Namespaces.SystemThreadingTasks) ?? false)
            {
                bool reportDiagnostic = false;
                bool isContinueWith = invokeMethod.Name == nameof(Task.ContinueWith) && invokeMethod.ContainingType.Name == nameof(Task);
                bool isTaskFactoryStartNew = invokeMethod.Name == nameof(TaskFactory.StartNew) && invokeMethod.ContainingType.Name == nameof(TaskFactory);

                if (isContinueWith || isTaskFactoryStartNew)
                {
                    if (!invokeMethod.Parameters.Any(p => p.Type.Name == nameof(TaskScheduler) && p.Type.BelongsToNamespace(Namespaces.SystemThreadingTasks)))
                    {
                        reportDiagnostic |= isContinueWith;

                        // Only notice uses of TaskFactory on the static instance (since custom instances may have a non-problematic default TaskScheduler set).
                        reportDiagnostic |= isTaskFactoryStartNew
                            && node.Expression is MemberAccessExpressionSyntax memberAccess && memberAccess.Expression is MemberAccessExpressionSyntax memberAccessInner
                                && context.SemanticModel.GetSymbolInfo(memberAccessInner, context.CancellationToken).Symbol is IPropertySymbol factoryProperty
                                && factoryProperty.ContainingType.Name == Types.Task.TypeName && factoryProperty.ContainingType.BelongsToNamespace(Namespaces.SystemThreadingTasks)
                                && factoryProperty.Name == nameof(Task.Factory);
                    }
                }

                if (reportDiagnostic)
                {
                    var memberAccessExpression = (MemberAccessExpressionSyntax)node.Expression;
                    context.ReportDiagnostic(Diagnostic.Create(Descriptor, memberAccessExpression.Name.GetLocation()));
                }
            }
        }
    }
}
