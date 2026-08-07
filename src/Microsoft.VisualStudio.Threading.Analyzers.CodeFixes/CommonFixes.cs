// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
using static Microsoft.VisualStudio.Threading.Analyzers.CommonInterest;

namespace Microsoft.VisualStudio.Threading.Analyzers;

internal static class CommonFixes
{
    internal static async Task<ImmutableArray<QualifiedMember>> ReadMethodsAsync(CodeFixContext codeFixContext, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        ImmutableArray<QualifiedMember>.Builder? result = ImmutableArray.CreateBuilder<QualifiedMember>();
        foreach (SourceText text in await ReadAdditionalFileTextsAsync(codeFixContext.Document.Project.AdditionalDocuments, fileNamePattern, cancellationToken))
        {
            bool matchAnyArity = !Contains(text, '`');
            foreach (string line in ReadLinesFromAdditionalFile(text))
            {
                result.Add(ParseAdditionalFileMethodLine(line, matchAnyArity));
            }
        }

        return result.ToImmutable();
    }

    internal static async Task<ImmutableArray<SourceText>> ReadAdditionalFileTextsAsync(IEnumerable<TextDocument> additionalFiles, Regex fileNamePattern, CancellationToken cancellationToken)
    {
        if (additionalFiles is null)
        {
            throw new ArgumentNullException(nameof(additionalFiles));
        }

        if (fileNamePattern is null)
        {
            throw new ArgumentNullException(nameof(fileNamePattern));
        }

        IEnumerable<TextDocument>? docs = from doc in additionalFiles.OrderBy(x => x.FilePath, StringComparer.Ordinal)
                                          let fileName = Path.GetFileName(doc.Name)
                                          where fileNamePattern.IsMatch(fileName)
                                          select doc;
        ImmutableArray<SourceText>.Builder? result = ImmutableArray.CreateBuilder<SourceText>();
        foreach (TextDocument? doc in docs)
        {
            SourceText text = await doc.GetTextAsync(cancellationToken);
            result.Add(text);
        }

        return result.ToImmutable();
    }
}
