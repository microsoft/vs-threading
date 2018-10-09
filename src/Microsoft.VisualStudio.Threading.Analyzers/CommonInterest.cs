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
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Text;

    internal static class CommonInterest
    {
        internal static readonly Regex FileNamePatternForMembersRequiringMainThread = new Regex(@"^vs-threading\.MembersRequiringMainThread(\..*)?.txt$", FileNamePatternRegexOptions);
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
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(ValueTaskAwaiter)), nameof(ValueTaskAwaiter.GetResult)), null),
        };

        internal static readonly IEnumerable<SyncBlockingMethod> SyncBlockingMethods = JTFSyncBlockers.Concat(ProblematicSyncBlockingMethods).Concat(new[]
        {
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShellInterop, "IVsTask"), "Wait"), extensionMethodNamespace: Namespaces.MicrosoftVisualStudioShell),
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShellInterop, "IVsTask"), "GetResult"), extensionMethodNamespace: Namespaces.MicrosoftVisualStudioShell),
        });

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
            new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(ValueTask)), nameof(ValueTask<int>.Result)), null),
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

        private static readonly Regex NegatableTypeOrMemberReferenceRegex = new Regex(@"^(?<negated>!)?\[(?<typeName>[^\[\]\:]+)+\](?:\:\:(?<memberName>\S+))?\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex MemberReferenceRegex = new Regex(@"^\[(?<typeName>[^\[\]\:]+)+\]::(?<memberName>\S+)\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

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

        internal static IEnumerable<QualifiedMember> ReadMethods(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
        {
            foreach (string line in ReadAdditionalFiles(analyzerOptions, fileNamePattern, cancellationToken))
            {
                yield return ParseAdditionalFileMethodLine(line);
            }
        }

        internal static async Task<ImmutableArray<QualifiedMember>> ReadMethodsAsync(CodeFixContext codeFixContext, Regex fileNamePattern, CancellationToken cancellationToken)
        {
            var result = ImmutableArray.CreateBuilder<QualifiedMember>();
            foreach (string line in await ReadAdditionalFilesAsync(codeFixContext.Document.Project.AdditionalDocuments, fileNamePattern, cancellationToken))
            {
                result.Add(ParseAdditionalFileMethodLine(line));
            }

            return result.ToImmutable();
        }

        internal static IEnumerable<TypeMatchSpec> ReadTypesAndMembers(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
        {
            foreach (string line in ReadAdditionalFiles(analyzerOptions, fileNamePattern, cancellationToken))
            {
                Match match = NegatableTypeOrMemberReferenceRegex.Match(line);
                if (!match.Success)
                {
                    throw new InvalidOperationException($"Parsing error on line: {line}");
                }

                bool inverted = match.Groups["negated"].Success;
                string[] typeNameElements = match.Groups["typeName"].Value.Split(QualifiedIdentifierSeparators);
                string typeName = typeNameElements[typeNameElements.Length - 1];
                var containingNamespace = typeNameElements.Take(typeNameElements.Length - 1).ToImmutableArray();
                var type = new QualifiedType(containingNamespace, typeName);
                var member = match.Groups["memberName"].Success ? new QualifiedMember(type, match.Groups["memberName"].Value) : default(QualifiedMember);
                yield return new TypeMatchSpec(type, member, inverted);
            }
        }

        internal static async Task<ImmutableArray<string>> ReadAdditionalFilesAsync(IEnumerable<TextDocument> additionalFiles, Regex fileNamePattern, CancellationToken cancellationToken)
        {
            if (additionalFiles == null)
            {
                throw new ArgumentNullException(nameof(additionalFiles));
            }

            if (fileNamePattern == null)
            {
                throw new ArgumentNullException(nameof(fileNamePattern));
            }

            var docs = from doc in additionalFiles.OrderBy(x => x.FilePath, StringComparer.Ordinal)
                       let fileName = Path.GetFileName(doc.Name)
                       where fileNamePattern.IsMatch(fileName)
                       select doc;
            var result = ImmutableArray.CreateBuilder<string>();
            foreach (var doc in docs)
            {
                var text = await doc.GetTextAsync(cancellationToken);
                result.AddRange(ReadLinesFromAdditionalFile(text));
            }

            return result.ToImmutable();
        }

        internal static IEnumerable<string> ReadAdditionalFiles(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
        {
            if (analyzerOptions == null)
            {
                throw new ArgumentNullException(nameof(analyzerOptions));
            }

            if (fileNamePattern == null)
            {
                throw new ArgumentNullException(nameof(fileNamePattern));
            }

            var docs = from file in analyzerOptions.AdditionalFiles.OrderBy(x => x.Path, StringComparer.Ordinal)
                        let fileName = Path.GetFileName(file.Path)
                        where fileNamePattern.IsMatch(fileName)
                        let text = file.GetText(cancellationToken)
                        select text;
            return docs.SelectMany(ReadLinesFromAdditionalFile);
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

        internal static bool Contains(this ImmutableArray<TypeMatchSpec> types, ITypeSymbol typeSymbol, ISymbol memberSymbol)
        {
            TypeMatchSpec matching = default(TypeMatchSpec);
            foreach (var type in types)
            {
                if (type.IsMatch(typeSymbol, memberSymbol))
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

        private static IEnumerable<string> ReadLinesFromAdditionalFile(SourceText text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            foreach (TextLine line in text.Lines)
            {
                string lineText = line.ToString();

                if (!string.IsNullOrWhiteSpace(lineText) && !lineText.StartsWith("#"))
                {
                    yield return lineText;
                }
            }
        }

        private static QualifiedMember ParseAdditionalFileMethodLine(string line)
        {
            Match match = MemberReferenceRegex.Match(line);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Parsing error on line: {line}");
            }

            string methodName = match.Groups["memberName"].Value;
            string[] typeNameElements = match.Groups["typeName"].Value.Split(QualifiedIdentifierSeparators);
            string typeName = typeNameElements[typeNameElements.Length - 1];
            var containingType = new QualifiedType(typeNameElements.Take(typeNameElements.Length - 1).ToImmutableArray(), typeName);
            return new QualifiedMember(containingType, methodName);
        }

        internal struct TypeMatchSpec
        {
            internal TypeMatchSpec(QualifiedType type, QualifiedMember member, bool inverted)
            {
                this.InvertedLogic = inverted;
                this.Type = type;
                this.Member = member;

                if (this.IsWildcard && this.Member.Name != null)
                {
                    throw new ArgumentException("Wildcard use is not allowed when member of type is specified.");
                }
            }

            /// <summary>
            /// Gets a value indicating whether this entry appeared in a file with a leading "!" character.
            /// </summary>
            internal bool InvertedLogic { get; }

            /// <summary>
            /// Gets the type described by this entry.
            /// </summary>
            internal QualifiedType Type { get; }

            /// <summary>
            /// Gets the member described by this entry.
            /// </summary>
            internal QualifiedMember Member { get; }

            /// <summary>
            /// Gets a value indicating whether a member match is reuqired.
            /// </summary>
            internal bool IsMember => this.Member.Name != null;

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
            internal bool IsMatch(ITypeSymbol typeSymbol, ISymbol memberSymbol)
            {
                if (typeSymbol == null)
                {
                    return false;
                }

                if (!this.IsMember
                    && (this.IsWildcard || typeSymbol.Name == this.Type.Name)
                    && typeSymbol.BelongsToNamespace(this.Type.Namespace))
                {
                    return true;
                }

                if (this.IsMember
                    && memberSymbol?.Name == this.Member.Name
                    && typeSymbol.Name == this.Type.Name
                    && typeSymbol.BelongsToNamespace(this.Type.Namespace))
                {
                    return true;
                }

                return false;
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

            public override string ToString() => string.Join(".", this.Namespace.Concat(new[] { this.Name }));
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
            public SyncBlockingMethod(QualifiedMember method, string asyncAlternativeMethodName = null, IReadOnlyList<string> extensionMethodNamespace = null)
            {
                this.Method = method;
                this.AsyncAlternativeMethodName = asyncAlternativeMethodName;
                this.ExtensionMethodNamespace = extensionMethodNamespace;
            }

            public QualifiedMember Method { get; }

            public string AsyncAlternativeMethodName { get; }

            public IReadOnlyList<string> ExtensionMethodNamespace { get; }
        }
    }
}
