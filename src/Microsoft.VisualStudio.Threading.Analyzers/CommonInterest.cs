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
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.VisualStudio.Threading.Analyzers;

internal static class CommonInterest
{
    internal static readonly Regex FileNamePatternForLegacyThreadSwitchingMembers = new Regex(@"^vs-threading\.LegacyThreadSwitchingMembers(\..*)?.txt$", FileNamePatternRegexOptions);
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
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task.WaitAll)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task.WaitAny)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(TaskAwaiter)), nameof(TaskAwaiter.GetResult)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemRuntimeCompilerServices, nameof(ValueTaskAwaiter)), nameof(ValueTaskAwaiter.GetResult)), null),
    };

    internal static readonly IEnumerable<SyncBlockingMethod> SyncBlockingMethods = JTFSyncBlockers.Concat(ProblematicSyncBlockingMethods).Concat(new[]
    {
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShellInterop, "IVsTask"), "Wait"), extensionMethodNamespace: Namespaces.MicrosoftVisualStudioShell),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShellInterop, "IVsTask"), "GetResult"), extensionMethodNamespace: Namespaces.MicrosoftVisualStudioShell),
    });

    internal static readonly IReadOnlyList<SyncBlockingMethod> SyncBlockingProperties = new[]
    {
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(Task)), nameof(Task<int>.Result)), null),
        new SyncBlockingMethod(new QualifiedMember(new QualifiedType(Namespaces.SystemThreadingTasks, nameof(ValueTask)), nameof(ValueTask<int>.Result)), null),
    };

    internal static readonly IEnumerable<QualifiedMember> ThreadAffinityTestingMethods = new[]
    {
        new QualifiedMember(new QualifiedType(Namespaces.MicrosoftVisualStudioShell, Types.ThreadHelper.TypeName), Types.ThreadHelper.CheckAccess),
    };

    internal static readonly ImmutableArray<QualifiedMember> TaskConfigureAwait = ImmutableArray.Create(
        new QualifiedMember(new QualifiedType(Types.Task.Namespace, Types.Task.TypeName), nameof(Task.ConfigureAwait)),
        new QualifiedMember(new QualifiedType(Types.AwaitExtensions.Namespace, Types.AwaitExtensions.TypeName), Types.AwaitExtensions.ConfigureAwaitRunInline));

    private const RegexOptions FileNamePatternRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(5);  // Prevent expensive CPU hang in Regex.Match if backtracking occurs due to pathological input (see #485).

    private static readonly Regex NegatableTypeOrMemberReferenceRegex = new Regex(@"^(?<negated>!)?\[(?<typeName>[^\[\]\:]+)+\](?:\:\:(?<memberName>\S+))?\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexMatchTimeout);

    private static readonly Regex MemberReferenceRegex = new Regex(@"^\[(?<typeName>[^\[\]\:]+)+\]::(?<memberName>\S+)\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexMatchTimeout);

    /// <summary>
    /// An array with '.' as its only element.
    /// </summary>
    private static readonly char[] QualifiedIdentifierSeparators = new[] { '.' };

    internal static IEnumerable<QualifiedMember> ReadMethods(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        foreach (string line in ReadAdditionalFiles(analyzerOptions, fileNamePattern, cancellationToken))
        {
            yield return ParseAdditionalFileMethodLine(line);
        }
    }

    internal static IEnumerable<TypeMatchSpec> ReadTypesAndMembers(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        foreach (string line in ReadAdditionalFiles(analyzerOptions, fileNamePattern, cancellationToken))
        {
            Match? match = null;
            try
            {
                match = NegatableTypeOrMemberReferenceRegex.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException($"Regex.Match timeout when parsing line: {line}");
            }

            if (!match.Success)
            {
                throw new InvalidOperationException($"Parsing error on line: {line}");
            }

            bool inverted = match.Groups["negated"].Success;
            string[] typeNameElements = match.Groups["typeName"].Value.Split(QualifiedIdentifierSeparators);
            string typeName = typeNameElements[typeNameElements.Length - 1];
            var containingNamespace = typeNameElements.Take(typeNameElements.Length - 1).ToImmutableArray();
            var type = new QualifiedType(containingNamespace, typeName);
            QualifiedMember member = match.Groups["memberName"].Success ? new QualifiedMember(type, match.Groups["memberName"].Value) : default(QualifiedMember);
            yield return new TypeMatchSpec(type, member, inverted);
        }
    }

    internal static IEnumerable<string> ReadAdditionalFiles(AnalyzerOptions analyzerOptions, Regex fileNamePattern, CancellationToken cancellationToken)
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

    internal static bool Contains(this ImmutableArray<QualifiedMember> methods, ISymbol symbol)
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

    internal static bool Contains(this ImmutableArray<TypeMatchSpec> types, [NotNullWhen(true)] ITypeSymbol? typeSymbol, ISymbol? memberSymbol)
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

    internal static IEnumerable<string> ReadLinesFromAdditionalFile(SourceText text)
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

    internal static QualifiedMember ParseAdditionalFileMethodLine(string line)
    {
        Match? match = null;
        try
        {
            match = MemberReferenceRegex.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidOperationException($"Regex.Match timeout when parsing line: {line}");
        }

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

    internal readonly struct TypeMatchSpec
    {
        internal TypeMatchSpec(QualifiedType type, QualifiedMember member, bool inverted)
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
        internal bool IsMember => this.Member.Name is object;

        /// <summary>
        /// Gets a value indicating whether the typename is a wildcard.
        /// </summary>
        internal bool IsWildcard => this.Type.Name == "*";

        /// <summary>
        /// Gets a value indicating whether this is an uninitialized (default) instance.
        /// </summary>
        internal bool IsEmpty => this.Type.Namespace is null;

        /// <summary>
        /// Tests whether a given symbol matches the description of a type (independent of its <see cref="InvertedLogic"/> property).
        /// </summary>
        internal bool IsMatch([NotNullWhen(true)] ITypeSymbol? typeSymbol, ISymbol? memberSymbol)
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

    internal readonly struct QualifiedType
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

    internal readonly struct QualifiedMember
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
    internal readonly struct SyncBlockingMethod
    {
        public SyncBlockingMethod(QualifiedMember method, string? asyncAlternativeMethodName = null, IReadOnlyList<string>? extensionMethodNamespace = null)
        {
            this.Method = method;
            this.AsyncAlternativeMethodName = asyncAlternativeMethodName;
            this.ExtensionMethodNamespace = extensionMethodNamespace;
        }

        public QualifiedMember Method { get; }

        public string? AsyncAlternativeMethodName { get; }

        public IReadOnlyList<string>? ExtensionMethodNamespace { get; }
    }
}
