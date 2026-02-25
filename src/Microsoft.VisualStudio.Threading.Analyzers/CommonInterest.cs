// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.VisualStudio.Threading.Analyzers;

public static class CommonInterest
{
    public static readonly Regex FileNamePatternForLegacyThreadSwitchingMembers = new Regex(@"^vs-threading\.LegacyThreadSwitchingMembers(\..*)?.txt$", FileNamePatternRegexOptions);
    public static readonly Regex FileNamePatternForMembersRequiringMainThread = new Regex(@"^vs-threading\.MembersRequiringMainThread(\..*)?.txt$", FileNamePatternRegexOptions);
    public static readonly Regex FileNamePatternForMethodsThatAssertMainThread = new Regex(@"^vs-threading\.MainThreadAssertingMethods(\..*)?.txt$", FileNamePatternRegexOptions);
    public static readonly Regex FileNamePatternForMethodsThatSwitchToMainThread = new Regex(@"^vs-threading\.MainThreadSwitchingMethods(\..*)?.txt$", FileNamePatternRegexOptions);
    public static readonly Regex FileNamePatternForSyncMethodsToExcludeFromVSTHRD103 = new Regex(@"^vs-threading\.SyncMethodsToExcludeFromVSTHRD103(\..*)?.txt$", FileNamePatternRegexOptions);

    public static readonly IEnumerable<SyncBlockingMethod> JTFSyncBlockers =
    [
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTaskFactory.TypeName), Types.JoinableTaskFactory.Run), Types.JoinableTaskFactory.RunAsync),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioThreading, Types.JoinableTask.TypeName), Types.JoinableTask.Join), Types.JoinableTask.JoinAsync),
    ];

    public static readonly IEnumerable<SyncBlockingMethod> ProblematicSyncBlockingMethods =
    [
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task.Wait)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task.WaitAll)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task.WaitAny)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(ConfiguredTaskAwaitable.ConfiguredTaskAwaiter)), nameof(ConfiguredTaskAwaitable.ConfiguredTaskAwaiter.GetResult)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(TaskAwaiter)), nameof(TaskAwaiter.GetResult)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(ValueTaskAwaiter)), nameof(ValueTaskAwaiter.GetResult)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter)), nameof(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter.GetResult)), null),
    ];

    public static readonly IEnumerable<SyncBlockingMethod> SyncBlockingMethods = JTFSyncBlockers.Concat(ProblematicSyncBlockingMethods).Concat(
    [
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShellInterop, "IVsTask"), "Wait"), extensionMethodNamespace: Namespaces.MicrosoftVisualStudioShell),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShellInterop, "IVsTask"), "GetResult"), extensionMethodNamespace: Namespaces.MicrosoftVisualStudioShell),
    ]);

    public static readonly ImmutableArray<SyncBlockingMethod> SyncBlockingProperties =
    [
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task<int>.Result)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(ValueTask)), nameof(ValueTask<int>.Result)), null),
    ];

    public static readonly IEnumerable<QualifiedMember> ThreadAffinityTestingMethods =
    [
        new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName), Types.ThreadHelper.CheckAccess),
    ];

    public static readonly ImmutableArray<QualifiedMember> TaskConfigureAwait = ImmutableArray.Create(
        new QualifiedMember(new QualifiedType(Types.Task.Namespace, Types.Task.TypeName), nameof(Task.ConfigureAwait)),
        new QualifiedMember(new QualifiedType(Types.AwaitExtensions.Namespace, Types.AwaitExtensions.TypeName), Types.AwaitExtensions.ConfigureAwaitRunInline));

    private const RegexOptions FileNamePatternRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    private const string GetAwaiterMethodName = "GetAwaiter";

    public static IEnumerable<QualifiedMember> ReadMethods(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        foreach (string line in ReadAdditionalFiles(analyzerOptions, fileNamePattern, cancellationToken))
        {
            yield return ParseAdditionalFileMethodLine(line);
        }
    }

    public static IEnumerable<TypeMatchSpec> ReadTypesAndMembers(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        foreach (string line in ReadAdditionalFiles(analyzerOptions, fileNamePattern, cancellationToken))
        {
            if (!CommonInterestParsing.TryParseNegatableTypeOrMemberReference(line, out bool negated, out ReadOnlyMemory<char> typeNameMemory, out string? memberNameValue))
            {
                throw new InvalidOperationException($"Parsing error on line: {line}");
            }

            (ImmutableArray<string> containingNamespace, string? typeName) = SplitQualifiedIdentifier(typeNameMemory);
            var type = new QualifiedType(containingNamespace, typeName);
            QualifiedMember member = memberNameValue is not null ? new QualifiedMember(type, memberNameValue) : default(QualifiedMember);
            yield return new TypeMatchSpec(type, member, negated);
        }
    }

    public static IEnumerable<string> ReadAdditionalFiles(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        if (analyzerOptions is null)
        {
            throw new ArgumentNullException(nameof(analyzerOptions));
        }

        if (fileNamePattern is null)
        {
            throw new ArgumentNullException(nameof(fileNamePattern));
        }

        IEnumerable<SourceText>? docs = from file in analyzerOptions.AdditionalFiles.OrderBy(x => x.Path, StringComparer.Ordinal)
                                        let fileName = Path.GetFileName(file.Path)
                                        where fileNamePattern.IsMatch(fileName)
                                        let text = file.GetText(cancellationToken)
                                        select text;
        return docs.SelectMany(ReadLinesFromAdditionalFile);
    }

    public static bool Contains(this ImmutableArray<QualifiedMember> methods, ISymbol symbol)
    {
        foreach (QualifiedMember method in methods)
        {
            if (method.IsMatch(symbol))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Contains(this ImmutableArray<TypeMatchSpec> types, [NotNullWhen(true)] ITypeSymbol? typeSymbol, ISymbol? memberSymbol)
    {
        TypeMatchSpec matching = default(TypeMatchSpec);
        foreach (TypeMatchSpec type in types)
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

    public static CommonInterest.AwaitableTypeTester CollectAwaitableTypes(Compilation compilation, CancellationToken cancellationToken)
    {
        HashSet<ITypeSymbol> awaitableTypes = new(SymbolEqualityComparer.Default);
        void AddAwaitableType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } genericType)
            {
                awaitableTypes.Add(genericType.ConstructUnboundGenericType());
            }
            else
            {
                awaitableTypes.Add(type);
            }
        }

        foreach (ISymbol getAwaiterMember in compilation.GetSymbolsWithName(GetAwaiterMethodName, SymbolFilter.Member, cancellationToken))
        {
            if (TryGetAwaitableType(getAwaiterMember, out ITypeSymbol? awaitableType))
            {
                AddAwaitableType(awaitableType);
            }
        }

        foreach (IAssemblySymbol referenceAssembly in compilation.Assembly.Modules.First().ReferencedAssemblySymbols)
        {
            CollectNamespace(referenceAssembly.GlobalNamespace);

            void CollectNamespace(INamespaceOrTypeSymbol nsOrType)
            {
                if (nsOrType is INamespaceSymbol ns)
                {
                    foreach (INamespaceSymbol nestedNs in ns.GetNamespaceMembers())
                    {
                        CollectNamespace(nestedNs);
                    }
                }

                foreach (INamedTypeSymbol nestedType in nsOrType.GetTypeMembers())
                {
                    CollectNamespace(nestedType);
                }

                foreach (ISymbol getAwaiterMember in nsOrType.GetMembers(GetAwaiterMethodName))
                {
                    if (TryGetAwaitableType(getAwaiterMember, out ITypeSymbol? awaitableType))
                    {
                        AddAwaitableType(awaitableType);
                    }
                }
            }
        }

        bool TryGetAwaitableType(ISymbol getAwaiterMember, [NotNullWhen(true)] out ITypeSymbol? awaitableType)
        {
            if (getAwaiterMember is IMethodSymbol getAwaiterMethod && compilation.IsSymbolAccessibleWithin(getAwaiterMember, compilation.Assembly) && TestGetAwaiterMethod(getAwaiterMethod))
            {
                awaitableType = getAwaiterMethod.IsExtensionMethod ? getAwaiterMethod.Parameters[0].Type : getAwaiterMethod.ContainingType;
                return true;
            }

            awaitableType = null;
            return false;
        }

        return new AwaitableTypeTester(awaitableTypes);
    }

    public static bool IsAwaitable(this ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            return false;
        }

        foreach (ISymbol symbol in typeSymbol.GetMembers(GetAwaiterMethodName))
        {
            if (symbol is IMethodSymbol getAwaiterMethod && TestGetAwaiterMethod(getAwaiterMethod))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAwaitable(this ITypeSymbol? typeSymbol, SemanticModel semanticModel, int position)
    {
        if (typeSymbol is null)
        {
            return false;
        }

        // We're able to do a more comprehensive job by detecting extension methods as well when we have a semantic model.
        foreach (ISymbol symbol in semanticModel.LookupSymbols(position, typeSymbol, name: GetAwaiterMethodName, includeReducedExtensionMethods: true))
        {
            if (symbol is IMethodSymbol getAwaiterMethod && TestGetAwaiterMethod(getAwaiterMethod))
            {
                return true;
            }
        }

        return false;
    }

    public static IOperation? GetContainingFunction(this IOperation? operation)
    {
        while (operation?.Parent is not null)
        {
            if (operation.Parent is IAnonymousFunctionOperation or IMethodBodyOperation or ILocalFunctionOperation)
            {
                return operation.Parent;
            }

            if (operation.Language == LanguageNames.VisualBasic)
            {
                if (operation.Parent is IBlockOperation)
                {
                    return operation.Parent;
                }
            }

            operation = operation.Parent;
        }

        return null;
    }

    public static bool ConformsToAwaiterPattern(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is null)
        {
            return false;
        }

        var hasGetResultMethod = false;
        var hasOnCompletedMethod = false;
        var hasIsCompletedProperty = false;

        foreach (ISymbol? member in typeSymbol.GetMembers())
        {
            hasGetResultMethod |= member.Name == nameof(TaskAwaiter.GetResult) && member is IMethodSymbol m && m.Parameters.IsEmpty;
            hasOnCompletedMethod |= member.Name == nameof(TaskAwaiter.OnCompleted) && member is IMethodSymbol;
            hasIsCompletedProperty |= member.Name == nameof(TaskAwaiter.IsCompleted) && member is IPropertySymbol;

            if (hasGetResultMethod && hasOnCompletedMethod && hasIsCompletedProperty)
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> ReadLinesFromAdditionalFile(SourceText text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        foreach (TextLine line in text.Lines)
        {
            string lineText = line.ToString();

            if (!string.IsNullOrWhiteSpace(lineText) && !lineText.StartsWith("#", StringComparison.OrdinalIgnoreCase))
            {
                yield return lineText;
            }
        }
    }

    public static QualifiedMember ParseAdditionalFileMethodLine(string line)
    {
        if (!CommonInterestParsing.TryParseMemberReference(line, out ReadOnlyMemory<char> typeNameMemory, out string? memberName))
        {
            throw new InvalidOperationException($"Parsing error on line: {line}");
        }

        (ImmutableArray<string> containingNamespace, string? typeName) = SplitQualifiedIdentifier(typeNameMemory);
        var containingType = new QualifiedType(containingNamespace, typeName);
        return new QualifiedMember(containingType, memberName!);
    }

    /// <summary>
    /// Splits a qualified type name (e.g. <c>My.Namespace.MyType</c>) into its containing namespace
    /// segments and the simple type name, without allocating an intermediate joined string.
    /// </summary>
    /// <param name="qualifiedName">The qualified type name as a memory slice.</param>
    /// <returns>The namespace segments and the simple type name.</returns>
    private static (ImmutableArray<string> ContainingNamespace, string TypeName) SplitQualifiedIdentifier(ReadOnlyMemory<char> qualifiedName)
    {
        int lastDot = qualifiedName.Span.LastIndexOf('.');
        if (lastDot < 0)
        {
            return (ImmutableArray<string>.Empty, qualifiedName.ToString());
        }

        string typeName = qualifiedName.Slice(lastDot + 1).ToString();
        ReadOnlyMemory<char> nsPart = qualifiedName.Slice(0, lastDot);
        ImmutableArray<string>.Builder nsBuilder = ImmutableArray.CreateBuilder<string>();
        while (!nsPart.IsEmpty)
        {
            int dot = nsPart.Span.IndexOf('.');
            if (dot < 0)
            {
                nsBuilder.Add(nsPart.ToString());
                break;
            }

            nsBuilder.Add(nsPart.Slice(0, dot).ToString());
            nsPart = nsPart.Slice(dot + 1);
        }

        return (nsBuilder.ToImmutable(), typeName);
    }

    private static bool TestGetAwaiterMethod(IMethodSymbol getAwaiterMethod)
    {
        if (getAwaiterMethod.IsExtensionMethod)
        {
            if (getAwaiterMethod.Parameters.Length != 1)
            {
                return false;
            }
        }
        else
        {
            if (!getAwaiterMethod.Parameters.IsEmpty)
            {
                return false;
            }
        }

        if (!CommonInterest.ConformsToAwaiterPattern(getAwaiterMethod.ReturnType))
        {
            return false;
        }

        return true;
    }

    public readonly struct TypeMatchSpec
    {
        public TypeMatchSpec(QualifiedType type, QualifiedMember member, bool inverted)
        {
            this.InvertedLogic = inverted;
            this.Type = type;
            this.Member = member;

            if (this.IsWildcard && this.Member.Name is object)
            {
                throw new ArgumentException("Wildcard use is not allowed when member of type is specified.");
            }
        }

        /// <summary>
        /// Gets a value indicating whether this entry appeared in a file with a leading "!" character.
        /// </summary>
        public bool InvertedLogic { get; }

        /// <summary>
        /// Gets the type described by this entry.
        /// </summary>
        public QualifiedType Type { get; }

        /// <summary>
        /// Gets the member described by this entry.
        /// </summary>
        public QualifiedMember Member { get; }

        /// <summary>
        /// Gets a value indicating whether a member match is required.
        /// </summary>
        public bool IsMember => this.Member.Name is object;

        /// <summary>
        /// Gets a value indicating whether the typename is a wildcard.
        /// </summary>
        public bool IsWildcard => this.Type.Name == "*";

        /// <summary>
        /// Gets a value indicating whether this is an uninitialized (default) instance.
        /// </summary>
        public bool IsEmpty => this.Type.Name is null;

        /// <summary>
        /// Tests whether a given symbol matches the description of a type (independent of its <see cref="InvertedLogic"/> property).
        /// </summary>
        public bool IsMatch([NotNullWhen(true)] ITypeSymbol? typeSymbol, ISymbol? memberSymbol)
        {
            if (typeSymbol is null)
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

    public readonly struct QualifiedType
    {
        public QualifiedType(ImmutableArray<string> containingTypeNamespace, string typeName)
        {
            this.Namespace = containingTypeNamespace;
            this.Name = typeName;
        }

        public ImmutableArray<string> Namespace { get; }

        public string Name { get; }

        public bool IsMatch(ISymbol symbol)
        {
            return symbol?.Name == this.Name
                && symbol.BelongsToNamespace(this.Namespace);
        }

        public override string ToString() => string.Join(".", this.Namespace.Concat([this.Name]));
    }

    public readonly struct QualifiedMember
    {
        public QualifiedMember(QualifiedType containingType, string methodName)
        {
            this.ContainingType = containingType;
            this.Name = methodName;
        }

        public QualifiedType ContainingType { get; }

        public string Name { get; }

        public bool IsMatch(ISymbol? symbol)
        {
            return symbol?.Name == this.Name
                && this.ContainingType.IsMatch(symbol.ContainingType);
        }

        public override string ToString() => this.ContainingType.ToString() + "." + this.Name;
    }

    [DebuggerDisplay("{" + nameof(Method) + "} -> {" + nameof(AsyncAlternativeMethodName) + "}")]
    public readonly struct SyncBlockingMethod
    {
        public SyncBlockingMethod(QualifiedMember method, string? asyncAlternativeMethodName = null, ImmutableArray<string>? extensionMethodNamespace = null)
        {
            this.Method = method;
            this.AsyncAlternativeMethodName = asyncAlternativeMethodName;
            this.ExtensionMethodNamespace = extensionMethodNamespace;
        }

        public QualifiedMember Method { get; }

        public string? AsyncAlternativeMethodName { get; }

        public ImmutableArray<string>? ExtensionMethodNamespace { get; }
    }

    public class AwaitableTypeTester
    {
        private readonly HashSet<ITypeSymbol> awaitableTypes;

        public AwaitableTypeTester(HashSet<ITypeSymbol> awaitableTypes)
        {
            this.awaitableTypes = awaitableTypes;
        }

        public bool IsAwaitableType(ITypeSymbol typeSymbol)
        {
            if (this.awaitableTypes.Contains(typeSymbol))
            {
                return true;
            }

            if (typeSymbol is INamedTypeSymbol { IsGenericType: true } genericTypeSymbol)
            {
                if (this.awaitableTypes.Contains(genericTypeSymbol.ConstructUnboundGenericType()))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
