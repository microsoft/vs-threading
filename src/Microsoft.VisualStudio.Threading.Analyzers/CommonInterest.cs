namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Text;

    internal static class CommonInterest
    {
        internal static readonly Regex FileNamePatternForTypesRequiringMainThread = new Regex(@"^vs-threading\.TypesRequiringMainThread(\..*)?.txt$", FileNamePatternRegexOptions);
        internal static readonly Regex FileNamePatternForMethodsThatAssertMainThread = new Regex(@"^vs-threading\.MainThreadAssertingMethods(\..*)?.txt$", FileNamePatternRegexOptions);
        internal static readonly Regex FileNamePatternForMethodsThatSwitchToMainThread = new Regex(@"^vs-threading\.MainThreadSwitchingMethods(\..*)?.txt$", FileNamePatternRegexOptions);

        internal static readonly IEnumerable<SyncBlockingMethod> JTFSyncBlockers = new[]
        {
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTaskFactory.TypeName), Types.JoinableTaskFactory.Run), Types.JoinableTaskFactory.RunAsync),
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTask.TypeName), Types.JoinableTask.Join), Types.JoinableTask.JoinAsync),
        };

        internal static readonly IEnumerable<SyncBlockingMethod> ProblematicSyncBlockingMethods = new[]
        {
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task.Wait)), null),
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(TaskAwaiter)), nameof(TaskAwaiter.GetResult)), null),
        };

        internal static readonly IEnumerable<SyncBlockingMethod> SyncBlockingMethods = JTFSyncBlockers.Concat(ProblematicSyncBlockingMethods);

        internal static readonly IEnumerable<QualifiedMember> LegacyThreadSwitchingMethods = new[]
        {
            new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName), Types.ThreadHelper.Invoke),
            new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName), Types.ThreadHelper.InvokeAsync),
            new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName), Types.ThreadHelper.BeginInvoke),
            new QualifiedMember(new QualifiedType(Namespaces.SystemWindowsThreading, Types.Dispatcher.TypeName), Types.Dispatcher.Invoke),
            new QualifiedMember(new QualifiedType(Namespaces.SystemWindowsThreading, Types.Dispatcher.TypeName), Types.Dispatcher.BeginInvoke),
            new QualifiedMember(new QualifiedType(Namespaces.SystemWindowsThreading, Types.Dispatcher.TypeName), Types.Dispatcher.InvokeAsync),
            new QualifiedMember(new QualifiedType(Namespaces.SystemThreading, Types.SynchronizationContext.TypeName), Types.SynchronizationContext.Send),
            new QualifiedMember(new QualifiedType(Namespaces.SystemThreading, Types.SynchronizationContext.TypeName), Types.SynchronizationContext.Post),
        };

        internal static readonly IReadOnlyList<SyncBlockingMethod> SyncBlockingProperties = new[]
        {
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task<int>.Result)), null),
        };

        internal static readonly IEnumerable<QualifiedMember> ThreadAffinityTestingMethods = new[]
        {
            new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName), Types.ThreadHelper.CheckAccess),
        };

        internal static readonly IImmutableSet<SyntaxKind> MethodSyntaxKinds = ImmutableHashSet.Create(
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.AnonymousMethodExpression,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.GetAccessorDeclaration,
            SyntaxKind.SetAccessorDeclaration,
            SyntaxKind.AddAccessorDeclaration,
            SyntaxKind.RemoveAccessorDeclaration);

        private const RegexOptions FileNamePatternRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        /// <summary>
        /// An array with '.' as its only element.
        /// </summary>
        private static readonly char[] QualifiedIdentifierSeparators = new[] { '.' };

        /// <summary>
        /// This is an explicit rule to ignore the code that was generated by Xaml2CS.
        /// </summary>
        /// <remarks>
        /// The generated code has the comments like this:
        /// <![CDATA[
        ///   //------------------------------------------------------------------------------
        ///   // <auto-generated>
        /// ]]>
        /// This rule is based on the fact the keyword "&lt;auto-generated&gt;" should be found in the comments.
        /// </remarks>
        internal static bool ShouldIgnoreContext(SyntaxNodeAnalysisContext context)
        {
            var namespaceDeclaration = context.Node.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
            if (namespaceDeclaration != null)
            {
                foreach (var trivia in namespaceDeclaration.NamespaceKeyword.GetAllTrivia())
                {
                    const string autoGeneratedKeyword = @"<auto-generated>";
                    if (trivia.FullSpan.Length > autoGeneratedKeyword.Length
                        && trivia.ToString().Contains(autoGeneratedKeyword))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static void InspectMemberAccess(
            SyntaxNodeAnalysisContext context,
            MemberAccessExpressionSyntax memberAccessSyntax,
            DiagnosticDescriptor descriptor,
            IEnumerable<SyncBlockingMethod> problematicMethods,
            bool ignoreIfInsideAnonymousDelegate = false)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (memberAccessSyntax == null)
            {
                return;
            }

            if (ShouldIgnoreContext(context))
            {
                return;
            }

            if (ignoreIfInsideAnonymousDelegate && context.Node.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() != null)
            {
                // We do not analyze JTF.Run inside anonymous functions because
                // they are so often used as callbacks where the signature is constrained.
                return;
            }

            if (Utils.IsWithinNameOf(context.Node as ExpressionSyntax))
            {
                // We do not consider arguments to nameof( ) because they do not represent invocations of code.
                return;
            }

            var typeReceiver = context.SemanticModel.GetTypeInfo(memberAccessSyntax.Expression).Type;
            if (typeReceiver != null)
            {
                foreach (var item in problematicMethods)
                {
                    if (memberAccessSyntax.Name.Identifier.Text == item.Method.Name &&
                        typeReceiver.Name == item.Method.ContainingType.Name &&
                        typeReceiver.BelongsToNamespace(item.Method.ContainingType.Namespace))
                    {
                        var location = memberAccessSyntax.Name.GetLocation();
                        context.ReportDiagnostic(Diagnostic.Create(descriptor, location));
                    }
                }
            }
        }

        internal static IEnumerable<QualifiedMember> ReadMethods(CompilationStartAnalysisContext context, Regex fileNamePattern)
        {
            foreach (string line in ReadAdditionalFiles(context, fileNamePattern))
            {
                string[] elements = line.TrimEnd(null).Split(QualifiedIdentifierSeparators);
                string methodName = elements[elements.Length - 1];
                string typeName = elements[elements.Length - 2];
                var containingType = new QualifiedType(elements.Take(elements.Length - 2).ToImmutableArray(), typeName);
                yield return new QualifiedMember(containingType, methodName);
            }
        }

        internal static IEnumerable<TypeMatchSpec> ReadTypes(CompilationStartAnalysisContext context, Regex fileNamePattern)
        {
            foreach (string line in ReadAdditionalFiles(context, fileNamePattern))
            {
                bool inverted = line.StartsWith("!", StringComparison.Ordinal);
                string[] elements = line.Substring(inverted ? 1 : 0).TrimEnd(null).Split(QualifiedIdentifierSeparators);
                string typeName = elements[elements.Length - 1];
                var containingNamespace = elements.Take(elements.Length - 1).ToImmutableArray();
                yield return new TypeMatchSpec(new QualifiedType(containingNamespace, typeName), inverted);
            }
        }

        internal static IEnumerable<string> ReadAdditionalFiles(CompilationStartAnalysisContext context, Regex fileNamePattern)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (fileNamePattern == null)
            {
                throw new ArgumentNullException(nameof(fileNamePattern));
            }

            var lines = from file in context.Options.AdditionalFiles
                        let fileName = Path.GetFileName(file.Path)
                        where fileNamePattern.IsMatch(fileName)
                        let text = file.GetText(context.CancellationToken)
                        from line in text.Lines
                        select line;
            foreach (TextLine line in lines)
            {
                string lineText = line.ToString();

                if (!string.IsNullOrWhiteSpace(lineText) && !lineText.StartsWith("#"))
                {
                    yield return lineText;
                }
            }
        }

        internal static bool Contains(this ImmutableArray<QualifiedMember> methods, ISymbol symbol)
        {
            foreach (var method in methods)
            {
                if (method.IsMatch(symbol))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool Contains(this ImmutableArray<TypeMatchSpec> types, ISymbol symbol)
        {
            TypeMatchSpec matching = default(TypeMatchSpec);
            foreach (var type in types)
            {
                if (type.IsMatch(symbol))
                {
                    if (matching.IsEmpty || matching.IsWildcard)
                    {
                        matching = type;
                        if (!matching.IsWildcard)
                        {
                            // It's an exact match, so return it immediately.
                            return !matching.InvertedLogic;
                        }
                    }
                }
            }

            return !matching.IsEmpty && !matching.InvertedLogic;
        }

        internal struct TypeMatchSpec
        {
            internal TypeMatchSpec(QualifiedType type, bool inverted)
            {
                this.InvertedLogic = inverted;
                this.Type = type;
            }

            /// <summary>
            /// Gets a value indicating whether this entry appeared in a file with a leading "!" character.
            /// </summary>
            internal bool InvertedLogic { get; }

            /// <summary>
            /// Gets the type described by this type entry.
            /// </summary>
            internal QualifiedType Type { get; }

            /// <summary>
            /// Gets a value indicating whether the typename is a wildcard.
            /// </summary>
            internal bool IsWildcard => this.Type.Name == "*";

            /// <summary>
            /// Gets a value indicating whether this is an uninitialized (default) instance.
            /// </summary>
            internal bool IsEmpty => this.Type.Namespace == null;

            /// <summary>
            /// Tests whether a given symbol matches the description of a type (independent of its <see cref="InvertedLogic"/> property).
            /// </summary>
            internal bool IsMatch(ISymbol symbol)
            {
                return (this.IsWildcard || symbol?.Name == this.Type.Name)
                    && symbol.BelongsToNamespace(this.Type.Namespace);
            }
        }

        internal struct QualifiedType
        {
            public QualifiedType(IReadOnlyList<string> containingTypeNamespace, string typeName)
            {
                this.Namespace = containingTypeNamespace;
                this.Name = typeName;
            }

            public IReadOnlyList<string> Namespace { get; }

            public string Name { get; }

            public bool IsMatch(ISymbol symbol)
            {
                return symbol?.Name == this.Name
                    && symbol.BelongsToNamespace(this.Namespace);
            }

            public override string ToString() => string.Join(".", this.Namespace) + "." + this.Name;
        }

        internal struct QualifiedMember
        {
            public QualifiedMember(QualifiedType containingType, string methodName)
            {
                this.ContainingType = containingType;
                this.Name = methodName;
            }

            public QualifiedType ContainingType { get; }

            public string Name { get; }

            public bool IsMatch(ISymbol symbol)
            {
                return symbol?.Name == this.Name
                    && this.ContainingType.IsMatch(symbol.ContainingType);
            }

            public override string ToString() => this.ContainingType.ToString() + "." + this.Name;
        }

        [DebuggerDisplay("{" + nameof(Method) + "} -> {" + nameof(AsyncAlternativeMethodName) + "}")]
        internal struct SyncBlockingMethod
        {
            public SyncBlockingMethod(QualifiedMember method, string asyncAlternativeMethodName)
            {
                this.Method = method;
                this.AsyncAlternativeMethodName = asyncAlternativeMethodName;
            }

            public QualifiedMember Method { get; }

            public string AsyncAlternativeMethodName { get; }
        }
    }
}
